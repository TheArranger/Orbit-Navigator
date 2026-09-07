using System.Text.Json;
using System.Text.Json.Serialization;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Sync;

namespace OrbitNavigator.Sync.Deletion;

internal sealed record DeletionOperationCheckpointModel(
    ProfileId ProfileId,
    SyncOperationId OperationId,
    IReadOnlyList<LocalDataCategory> LocalCategories,
    IReadOnlyList<SyncDataCategory> SyncedCategories,
    ClientFence? RequestedFence,
    IReadOnlyList<DataDeletionStageStatus> Stages,
    IReadOnlyDictionary<SyncDataCategory, IReadOnlyList<SyncEntityId>> Entities,
    IReadOnlyList<EncryptedSyncTombstone>? Tombstones,
    IReadOnlyList<EncryptedPurgeCommand>? Purges,
    AuthenticatedDeletionPushReceipt? TombstoneReceipt,
    AuthenticatedDeletionPushReceipt? PurgeReceipt,
    AuthenticatedDeletionPropagationStatus? VerifiedPropagationStatus,
    bool PurgesPublished,
    bool PropagationComplete);

internal static class DeletionOperationCheckpointCodec
{
    public const int CurrentFormatVersion = 1;
    public const int MaximumPayloadBytes = 32 * 1024 * 1024;

    private const int MaximumEntityCount = 1_000_000;
    private const int MaximumErrorArguments = 16;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 24,
    };

    public static ControllerResult<byte[]> Serialize(DeletionOperationCheckpointModel model)
    {
        var validation = Validate(model);
        if (validation is not null)
            return ControllerResult<byte[]>.Failure(validation);

        try
        {
            var dto = ToDto(model);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(dto, JsonOptions);
            return bytes.Length is > 0 and <= MaximumPayloadBytes
                ? ControllerResult<byte[]>.Success(bytes)
                : ControllerResult<byte[]>.Failure(InvalidCheckpoint());
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return ControllerResult<byte[]>.Failure(InvalidCheckpoint());
        }
    }

    public static ControllerResult<DeletionOperationCheckpointModel> Deserialize(
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length is <= 0 or > MaximumPayloadBytes)
            return ControllerResult<DeletionOperationCheckpointModel>.Failure(InvalidCheckpoint());

        try
        {
            var dto = JsonSerializer.Deserialize<CheckpointDto>(payload, JsonOptions);
            var model = FromDto(dto);
            var validation = model is null ? InvalidCheckpoint() : Validate(model);
            return validation is null
                ? ControllerResult<DeletionOperationCheckpointModel>.Success(model!)
                : ControllerResult<DeletionOperationCheckpointModel>.Failure(validation);
        }
        catch (Exception exception) when (
            exception is JsonException or
            NotSupportedException or
            ArgumentException or
            InvalidOperationException or
            OverflowException or
            FormatException or
            NullReferenceException)
        {
            return ControllerResult<DeletionOperationCheckpointModel>.Failure(InvalidCheckpoint());
        }
    }

    private static ControllerError? Validate(DeletionOperationCheckpointModel model)
    {
        if (model.ProfileId.IsEmpty || !model.OperationId.IsDefined)
            return InvalidCheckpoint();

        if (model.LocalCategories.Count == 0 && model.SyncedCategories.Count == 0)
            return InvalidCheckpoint();
        if (model.LocalCategories.Count > Enum.GetValues<LocalDataCategory>().Length ||
            model.SyncedCategories.Count > Enum.GetValues<SyncDataCategory>().Length ||
            model.LocalCategories.Distinct().Count() != model.LocalCategories.Count ||
            model.SyncedCategories.Distinct().Count() != model.SyncedCategories.Count ||
            model.LocalCategories.Any(value => !Enum.IsDefined(value)) ||
            model.SyncedCategories.Any(value => !Enum.IsDefined(value)))
        {
            return InvalidCheckpoint();
        }

        var selection = new DataDeletionSelection(
            model.LocalCategories.ToHashSet(),
            model.SyncedCategories.ToHashSet());
        if (!DataDeletionRules.ValidateSelection(selection).IsValid)
            return InvalidCheckpoint();
        if (model.SyncedCategories.Count > 0 && model.RequestedFence is not { IsDefined: true })
            return InvalidCheckpoint();
        if (model.SyncedCategories.Count == 0 && model.RequestedFence is not null)
            return InvalidCheckpoint();

        if (model.Stages.Count != Enum.GetValues<DataDeletionStageKind>().Length ||
            model.Stages.Select(value => value.Stage).Distinct().Count() != model.Stages.Count)
        {
            return InvalidCheckpoint();
        }

        foreach (var stage in model.Stages)
        {
            if (!Enum.IsDefined(stage.Stage) || !Enum.IsDefined(stage.State))
                return InvalidCheckpoint();
            if (stage.State == DataDeletionStageState.Failed && stage.Error is null)
                return InvalidCheckpoint();
            if (stage.State != DataDeletionStageState.Failed && stage.Error is not null)
                return InvalidCheckpoint();
            if ((stage.Error?.FormattingArguments.Count ?? 0) > MaximumErrorArguments)
                return InvalidCheckpoint();
        }

        if (model.Entities.Count > model.SyncedCategories.Count ||
            model.Entities.Keys.Any(category => !model.SyncedCategories.Contains(category)))
        {
            return InvalidCheckpoint();
        }

        var entityCount = 0;
        foreach (var values in model.Entities.Values)
        {
            entityCount = checked(entityCount + values.Count);
            if (values.Count != values.Distinct().Count() || values.Any(value => !value.IsDefined))
                return InvalidCheckpoint();
        }
        if (entityCount > MaximumEntityCount)
            return InvalidCheckpoint();

        if (model.Tombstones is not null)
        {
            if (model.Tombstones.Count > MaximumEntityCount)
                return InvalidCheckpoint();
            foreach (var tombstone in model.Tombstones)
            {
                if (!SyncContractRules.ValidateTombstone(tombstone).IsValid ||
                    !ValidateAadIdentity(model, tombstone.Aad, SyncRecordKind.Tombstone))
                {
                    return InvalidCheckpoint();
                }
            }
        }

        if (model.Purges is not null)
        {
            if (model.Purges.Count != model.SyncedCategories.Count)
                return InvalidCheckpoint();
            foreach (var purge in model.Purges)
            {
                if (!SyncContractRules.ValidatePurgeCommand(purge).IsValid ||
                    !ValidateAadIdentity(model, purge.Aad, SyncRecordKind.Purge) ||
                    purge.Aad.OperationId != model.OperationId)
                {
                    return InvalidCheckpoint();
                }
            }
        }

        if (model.PurgesPublished && model.Purges is null)
            return InvalidCheckpoint();
        if (model.TombstoneReceipt is not null)
        {
            if (model.Tombstones is not { Count: > 0 } ||
                !ValidateReceipt(model, model.Tombstones, Array.Empty<EncryptedPurgeCommand>(), "tombstones", model.TombstoneReceipt))
            {
                return InvalidCheckpoint();
            }
        }
        if (model.PurgeReceipt is not null)
        {
            if (model.Purges is not { Count: > 0 } ||
                !ValidateReceipt(model, Array.Empty<EncryptedSyncTombstone>(), model.Purges, "purges", model.PurgeReceipt))
            {
                return InvalidCheckpoint();
            }
        }
        if (model.PurgesPublished && model.PurgeReceipt is null)
            return InvalidCheckpoint();
        var tombstoneStageSucceeded = model.Stages.Single(value =>
            value.Stage == DataDeletionStageKind.UploadTombstones).State == DataDeletionStageState.Succeeded;
        var purgeStageSucceeded = model.Stages.Single(value =>
            value.Stage == DataDeletionStageKind.UploadPurgeCommands).State == DataDeletionStageState.Succeeded;
        if (tombstoneStageSucceeded &&
            model.Tombstones is { Count: > 0 } &&
            model.TombstoneReceipt is null)
        {
            return InvalidCheckpoint();
        }
        if (purgeStageSucceeded && model.SyncedCategories.Count > 0 && model.PurgeReceipt is null)
            return InvalidCheckpoint();
        if (model.VerifiedPropagationStatus is not null &&
            (model.PurgeReceipt is null || !DeletionAcknowledgementRules
                .ValidatePropagationStatus(model.PurgeReceipt, model.VerifiedPropagationStatus)
                .IsValid))
        {
            return InvalidCheckpoint();
        }
        if (model.VerifiedPropagationStatus is { } verifiedStatus &&
            verifiedStatus.Categories.Any(value =>
                !Enum.IsDefined(value.State) ||
                (value.State == DeletionCategoryPropagationState.Failed) != (value.Error is not null)))
        {
            return InvalidCheckpoint();
        }
        if (model.PropagationComplete && !model.PurgesPublished && model.SyncedCategories.Count > 0)
            return InvalidCheckpoint();
        if (model.PropagationComplete &&
            model.SyncedCategories.Count > 0 &&
            model.VerifiedPropagationStatus is not { IsComplete: true })
        {
            return InvalidCheckpoint();
        }

        return null;
    }

    private static bool ValidateReceipt(
        DeletionOperationCheckpointModel model,
        IReadOnlyList<EncryptedSyncTombstone> tombstones,
        IReadOnlyList<EncryptedPurgeCommand> purges,
        string phase,
        AuthenticatedDeletionPushReceipt receipt)
    {
        if (model.RequestedFence is null)
            return false;

        var push = new SyncPushRequest(
            model.RequestedFence.DeviceId,
            model.RequestedFence,
            Array.Empty<EncryptedSyncEnvelope>(),
            tombstones,
            purges);
        var digest = DeletionAcknowledgementRules.ComputeRequestDigest(push);
        if (!digest.IsSuccess)
            return false;
        var binding = new DeletionPushBinding(
            model.OperationId,
            tombstones.Select(value => value.Aad.Category)
                .Concat(purges.Select(value => value.Aad.Category))
                .ToHashSet(),
            tombstones.Select(value => value.Aad.EnvelopeId)
                .Concat(purges.Select(value => value.Aad.EnvelopeId))
                .ToArray(),
            digest.Value!,
            CreateIdempotencyKey(model.OperationId, phase));
        return DeletionAcknowledgementRules
            .ValidateReceipt(new BoundDeletionPushRequest(push, binding), receipt)
            .IsValid;
    }

    private static SyncIdempotencyKey CreateIdempotencyKey(
        SyncOperationId operationId,
        string phase)
    {
        var source = System.Text.Encoding.UTF8.GetBytes(
            $"orbit-navigator|deletion|{operationId.Value:N}|{phase}");
        Span<byte> digest = stackalloc byte[32];
        System.Security.Cryptography.SHA256.HashData(source, digest);
        return new SyncIdempotencyKey(new Guid(digest[..16]));
    }

    private static bool ValidateAadIdentity(
        DeletionOperationCheckpointModel model,
        CanonicalSyncAad aad,
        SyncRecordKind kind) =>
        aad.ProfileId == model.ProfileId &&
        aad.RecordKind == kind &&
        model.SyncedCategories.Contains(aad.Category) &&
        model.RequestedFence is { } fence &&
        aad.DeviceId == fence.DeviceId &&
        aad.ClientGeneration == fence.ClientGeneration;

    private static CheckpointDto ToDto(DeletionOperationCheckpointModel model) =>
        new()
        {
            FormatVersion = CurrentFormatVersion,
            ProfileId = model.ProfileId.Value,
            OperationId = model.OperationId.Value,
            LocalCategories = model.LocalCategories.Select(value => (int)value).ToArray(),
            SyncedCategories = model.SyncedCategories.Select(value => (int)value).ToArray(),
            Fence = model.RequestedFence is null
                ? null
                : new FenceDto
                {
                    DeviceId = model.RequestedFence.DeviceId.Value,
                    ClientGeneration = model.RequestedFence.ClientGeneration,
                    MinimumAcceptedGeneration = model.RequestedFence.MinimumAcceptedGeneration,
                },
            Stages = model.Stages.Select(ToDto).ToArray(),
            Entities = model.Entities.ToDictionary(
                pair => ((int)pair.Key).ToString(System.Globalization.CultureInfo.InvariantCulture),
                pair => (Guid[]?)pair.Value.Select(value => value.Value).ToArray()),
            Tombstones = model.Tombstones?.Select(value => ToDto(value.Aad, value.Nonce, value.Ciphertext, value.AuthenticationTag)).ToArray(),
            Purges = model.Purges?.Select(value => ToDto(value.Aad, value.Nonce, value.Ciphertext, value.AuthenticationTag)).ToArray(),
            TombstoneReceipt = model.TombstoneReceipt is null ? null : ToDto(model.TombstoneReceipt),
            PurgeReceipt = model.PurgeReceipt is null ? null : ToDto(model.PurgeReceipt),
            VerifiedPropagationStatus = model.VerifiedPropagationStatus is null
                ? null
                : ToDto(model.VerifiedPropagationStatus),
            PurgesPublished = model.PurgesPublished,
            PropagationComplete = model.PropagationComplete,
        };

    private static DeletionOperationCheckpointModel? FromDto(CheckpointDto? dto)
    {
        if (dto is null || dto.FormatVersion != CurrentFormatVersion)
            return null;

        var local = dto.LocalCategories?.Select(value => (LocalDataCategory)value).ToArray();
        var synced = dto.SyncedCategories?.Select(value => (SyncDataCategory)value).ToArray();
        var stages = dto.Stages?.Select(FromDto).ToArray();
        if (local is null || synced is null || stages is null || dto.Entities is null)
            return null;

        var entities = new Dictionary<SyncDataCategory, IReadOnlyList<SyncEntityId>>();
        foreach (var pair in dto.Entities)
        {
            if (!int.TryParse(pair.Key, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var category) ||
                pair.Value is null)
            {
                return null;
            }
            entities[(SyncDataCategory)category] = pair.Value.Select(value => new SyncEntityId(value)).ToArray();
        }

        var fence = dto.Fence is null
            ? null
            : new ClientFence(
                new DeviceId(dto.Fence.DeviceId),
                dto.Fence.ClientGeneration,
                dto.Fence.MinimumAcceptedGeneration);
        var tombstones = dto.Tombstones?.Select(value =>
            new EncryptedSyncTombstone(FromDto(value.Aad), value.Nonce!, value.Ciphertext!, value.Tag!)).ToArray();
        var purges = dto.Purges?.Select(value =>
            new EncryptedPurgeCommand(FromDto(value.Aad), value.Nonce!, value.Ciphertext!, value.Tag!)).ToArray();
        var tombstoneReceipt = dto.TombstoneReceipt is null ? null : FromDto(dto.TombstoneReceipt);
        var purgeReceipt = dto.PurgeReceipt is null ? null : FromDto(dto.PurgeReceipt);
        var propagationStatus = dto.VerifiedPropagationStatus is null
            ? null
            : FromDto(dto.VerifiedPropagationStatus);

        return new DeletionOperationCheckpointModel(
            new ProfileId(dto.ProfileId),
            new SyncOperationId(dto.OperationId),
            local,
            synced,
            fence,
            stages,
            entities,
            tombstones,
            purges,
            tombstoneReceipt,
            purgeReceipt,
            propagationStatus,
            dto.PurgesPublished,
            dto.PropagationComplete);
    }

    private static StageDto ToDto(DataDeletionStageStatus stage) =>
        new()
        {
            Stage = (int)stage.Stage,
            State = (int)stage.State,
            UpdatedAtUtc = stage.UpdatedAtUtc,
            Error = ToDto(stage.Error),
        };

    private static DataDeletionStageStatus FromDto(StageDto dto)
    {
        return new DataDeletionStageStatus(
            (DataDeletionStageKind)dto.Stage,
            (DataDeletionStageState)dto.State,
            dto.UpdatedAtUtc,
            FromDto(dto.Error));
    }

    private static DeletionReceiptDto ToDto(AuthenticatedDeletionPushReceipt receipt) =>
        new()
        {
            Aad = ToDto(receipt.Aad),
            Cursor = receipt.Cursor.Value,
            Fence = ToDto(receipt.Fence),
            AcceptedAtUtc = receipt.AcceptedAtUtc,
        };

    private static AuthenticatedDeletionPushReceipt FromDto(DeletionReceiptDto dto) =>
        new(
            FromDto(dto.Aad),
            new SyncCursor(dto.Cursor!),
            FromDto(dto.Fence),
            dto.AcceptedAtUtc);

    private static DeletionPropagationStatusDto ToDto(
        AuthenticatedDeletionPropagationStatus status) =>
        new()
        {
            Aad = ToDto(status.Aad),
            Fence = ToDto(status.Fence),
            Categories = status.Categories.Select(value => new PropagationCategoryDto
            {
                Category = (int)value.Category,
                State = (int)value.State,
                UpdatedAtUtc = value.UpdatedAtUtc,
                Error = ToDto(value.Error),
            }).ToArray(),
            IsComplete = status.IsComplete,
            ObservedAtUtc = status.ObservedAtUtc,
        };

    private static AuthenticatedDeletionPropagationStatus FromDto(
        DeletionPropagationStatusDto dto) =>
        new(
            FromDto(dto.Aad),
            FromDto(dto.Fence),
            dto.Categories!.Select(value => new DeletionCategoryPropagation(
                (SyncDataCategory)value.Category,
                (DeletionCategoryPropagationState)value.State,
                value.UpdatedAtUtc,
                FromDto(value.Error))).ToArray(),
            dto.IsComplete,
            dto.ObservedAtUtc);

    private static DeletionAcknowledgementAadDto ToDto(DeletionAcknowledgementAad aad) =>
        new()
        {
            ProtocolVersion = aad.ProtocolVersion,
            SchemaVersion = aad.SchemaVersion,
            ProfileId = aad.ProfileId.Value,
            DeviceId = aad.DeviceId.Value,
            KeysetId = aad.KeysetId.Value,
            ClientGeneration = aad.ClientGeneration,
            OperationId = aad.OperationId.Value,
            Categories = aad.Categories.Select(value => (int)value).ToArray(),
            EnvelopeIds = aad.EnvelopeIds.Select(value => value.Value).ToArray(),
            RequestDigest = aad.RequestDigest.Bytes.ToArray(),
            IdempotencyKey = aad.IdempotencyKey.Value,
        };

    private static DeletionAcknowledgementAad FromDto(DeletionAcknowledgementAadDto dto) =>
        new(
            dto.ProtocolVersion,
            dto.SchemaVersion,
            new ProfileId(dto.ProfileId),
            new DeviceId(dto.DeviceId),
            new SyncKeysetId(dto.KeysetId),
            dto.ClientGeneration,
            new SyncOperationId(dto.OperationId),
            dto.Categories!.Select(value => (SyncDataCategory)value).ToHashSet(),
            dto.EnvelopeIds!.Select(value => new SyncEnvelopeId(value)).ToArray(),
            DeletionRequestDigest.Create(dto.RequestDigest!).Value!,
            new SyncIdempotencyKey(dto.IdempotencyKey));

    private static FenceDto ToDto(ClientFence fence) =>
        new()
        {
            DeviceId = fence.DeviceId.Value,
            ClientGeneration = fence.ClientGeneration,
            MinimumAcceptedGeneration = fence.MinimumAcceptedGeneration,
        };

    private static ClientFence FromDto(FenceDto dto) =>
        new(
            new DeviceId(dto.DeviceId),
            dto.ClientGeneration,
            dto.MinimumAcceptedGeneration);

    private static ErrorDto? ToDto(ControllerError? error) =>
        error is null
            ? null
            : new ErrorDto
            {
                Code = (int)error.Code,
                MessageKey = error.MessageKey,
                IsRetryable = error.IsRetryable,
                Arguments = error.FormattingArguments.Select(argument => new ErrorArgumentDto
                {
                    Key = (int)argument.Key,
                    Value = argument.Value,
                }).ToArray(),
            };

    private static ControllerError? FromDto(ErrorDto? dto)
    {
        if (dto is null)
            return null;

        var arguments = dto.Arguments?.Select(argument =>
        {
            var key = (ControllerMessageArgumentKey)argument.Key;
            return key is ControllerMessageArgumentKey.ItemCount or
                ControllerMessageArgumentKey.MaximumCount or
                ControllerMessageArgumentKey.RetryAfterSeconds
                ? ControllerMessageArgument.Count(key, int.Parse(
                    argument.Value!,
                    System.Globalization.CultureInfo.InvariantCulture))
                : ControllerMessageArgument.Symbol(key, argument.Value!);
        }).ToArray();
        return ControllerError.Create(
            (ControllerErrorCode)dto.Code,
            dto.MessageKey!,
            arguments,
            dto.IsRetryable);
    }

    private static EncryptedRecordDto ToDto(
        CanonicalSyncAad aad,
        ReadOnlyMemory<byte> nonce,
        ReadOnlyMemory<byte> ciphertext,
        ReadOnlyMemory<byte> tag) =>
        new()
        {
            Aad = new AadDto
            {
                ProtocolVersion = aad.ProtocolVersion,
                SchemaVersion = aad.SchemaVersion,
                ProfileId = aad.ProfileId.Value,
                DeviceId = aad.DeviceId.Value,
                KeysetId = aad.KeysetId.Value,
                KeyEpoch = aad.KeyEpoch,
                RecordKind = (int)aad.RecordKind,
                EnvelopeId = aad.EnvelopeId.Value,
                Category = (int)aad.Category,
                EntityId = aad.EntityId.Value,
                OperationId = aad.OperationId?.Value,
                ClientGeneration = aad.ClientGeneration,
                ClientSequence = aad.ClientSequence,
            },
            Nonce = nonce.ToArray(),
            Ciphertext = ciphertext.ToArray(),
            Tag = tag.ToArray(),
        };

    private static CanonicalSyncAad FromDto(AadDto dto) =>
        new(
            dto.ProtocolVersion,
            dto.SchemaVersion,
            new ProfileId(dto.ProfileId),
            new DeviceId(dto.DeviceId),
            new SyncKeysetId(dto.KeysetId),
            dto.KeyEpoch,
            (SyncRecordKind)dto.RecordKind,
            new SyncEnvelopeId(dto.EnvelopeId),
            (SyncDataCategory)dto.Category,
            new SyncEntityId(dto.EntityId),
            dto.OperationId is { } operation ? new SyncOperationId(operation) : null,
            dto.ClientGeneration,
            dto.ClientSequence);

    private static ControllerError InvalidCheckpoint() =>
        ControllerError.Create(
            ControllerErrorCode.IntegrityFailure,
            "sync.deletion.checkpoint-invalid");

    private sealed class CheckpointDto
    {
        public int FormatVersion { get; set; }
        public Guid ProfileId { get; set; }
        public Guid OperationId { get; set; }
        public int[]? LocalCategories { get; set; }
        public int[]? SyncedCategories { get; set; }
        public FenceDto? Fence { get; set; }
        public StageDto[]? Stages { get; set; }
        public Dictionary<string, Guid[]?>? Entities { get; set; }
        public EncryptedRecordDto[]? Tombstones { get; set; }
        public EncryptedRecordDto[]? Purges { get; set; }
        public DeletionReceiptDto? TombstoneReceipt { get; set; }
        public DeletionReceiptDto? PurgeReceipt { get; set; }
        public DeletionPropagationStatusDto? VerifiedPropagationStatus { get; set; }
        public bool PurgesPublished { get; set; }
        public bool PropagationComplete { get; set; }
    }

    private sealed class FenceDto
    {
        public Guid DeviceId { get; set; }
        public long ClientGeneration { get; set; }
        public long MinimumAcceptedGeneration { get; set; }
    }

    private sealed class StageDto
    {
        public int Stage { get; set; }
        public int State { get; set; }
        public DateTimeOffset? UpdatedAtUtc { get; set; }
        public ErrorDto? Error { get; set; }
    }

    private sealed class ErrorDto
    {
        public int Code { get; set; }
        public string? MessageKey { get; set; }
        public bool IsRetryable { get; set; }
        public ErrorArgumentDto[]? Arguments { get; set; }
    }

    private sealed class ErrorArgumentDto
    {
        public int Key { get; set; }
        public string? Value { get; set; }
    }

    private sealed class EncryptedRecordDto
    {
        public AadDto Aad { get; set; } = new();
        public byte[]? Nonce { get; set; }
        public byte[]? Ciphertext { get; set; }
        public byte[]? Tag { get; set; }
    }

    private sealed class AadDto
    {
        public int ProtocolVersion { get; set; }
        public int SchemaVersion { get; set; }
        public Guid ProfileId { get; set; }
        public Guid DeviceId { get; set; }
        public Guid KeysetId { get; set; }
        public long KeyEpoch { get; set; }
        public int RecordKind { get; set; }
        public Guid EnvelopeId { get; set; }
        public int Category { get; set; }
        public Guid EntityId { get; set; }
        public Guid? OperationId { get; set; }
        public long ClientGeneration { get; set; }
        public long ClientSequence { get; set; }
    }

    private sealed class DeletionReceiptDto
    {
        public DeletionAcknowledgementAadDto Aad { get; set; } = new();
        public string? Cursor { get; set; }
        public FenceDto Fence { get; set; } = new();
        public DateTimeOffset AcceptedAtUtc { get; set; }
    }

    private sealed class DeletionPropagationStatusDto
    {
        public DeletionAcknowledgementAadDto Aad { get; set; } = new();
        public FenceDto Fence { get; set; } = new();
        public PropagationCategoryDto[]? Categories { get; set; }
        public bool IsComplete { get; set; }
        public DateTimeOffset ObservedAtUtc { get; set; }
    }

    private sealed class PropagationCategoryDto
    {
        public int Category { get; set; }
        public int State { get; set; }
        public DateTimeOffset? UpdatedAtUtc { get; set; }
        public ErrorDto? Error { get; set; }
    }

    private sealed class DeletionAcknowledgementAadDto
    {
        public int ProtocolVersion { get; set; }
        public int SchemaVersion { get; set; }
        public Guid ProfileId { get; set; }
        public Guid DeviceId { get; set; }
        public Guid KeysetId { get; set; }
        public long ClientGeneration { get; set; }
        public Guid OperationId { get; set; }
        public int[]? Categories { get; set; }
        public Guid[]? EnvelopeIds { get; set; }
        public byte[]? RequestDigest { get; set; }
        public Guid IdempotencyKey { get; set; }
    }
}
