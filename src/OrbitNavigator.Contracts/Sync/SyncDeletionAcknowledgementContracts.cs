using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.Contracts.Sync;

public readonly record struct SyncIdempotencyKey(Guid Value)
{
    public bool IsDefined => Value != Guid.Empty;
}

public sealed class DeletionRequestDigest : IEquatable<DeletionRequestDigest>
{
    public const int SizeBytes = 32;
    private readonly byte[] _bytes;

    private DeletionRequestDigest(byte[] bytes) => _bytes = bytes;

    public ReadOnlyMemory<byte> Bytes => _bytes;

    public static ControllerResult<DeletionRequestDigest> Create(ReadOnlySpan<byte> bytes) =>
        bytes.Length == SizeBytes
            ? ControllerResult<DeletionRequestDigest>.Success(
                new DeletionRequestDigest(bytes.ToArray()))
            : ControllerResult<DeletionRequestDigest>.Failure(ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "error.sync.deletion_digest_invalid"));

    public bool Equals(DeletionRequestDigest? other) =>
        other is not null && CryptographicOperations.FixedTimeEquals(_bytes, other._bytes);

    public override bool Equals(object? obj) => Equals(obj as DeletionRequestDigest);

    public override int GetHashCode() => BitConverter.ToInt32(_bytes, 0);
}

public sealed record DeletionPushBinding(
    SyncOperationId OperationId,
    IReadOnlySet<SyncDataCategory> Categories,
    IReadOnlyList<SyncEnvelopeId> EnvelopeIds,
    DeletionRequestDigest RequestDigest,
    SyncIdempotencyKey IdempotencyKey);

public sealed record BoundDeletionPushRequest(
    SyncPushRequest Push,
    DeletionPushBinding Binding);

public sealed record DeletionAcknowledgementAad(
    int ProtocolVersion,
    int SchemaVersion,
    ProfileId ProfileId,
    DeviceId DeviceId,
    SyncKeysetId KeysetId,
    long ClientGeneration,
    SyncOperationId OperationId,
    IReadOnlySet<SyncDataCategory> Categories,
    IReadOnlyList<SyncEnvelopeId> EnvelopeIds,
    DeletionRequestDigest RequestDigest,
    SyncIdempotencyKey IdempotencyKey);

public sealed record AuthenticatedDeletionPushReceipt(
    DeletionAcknowledgementAad Aad,
    SyncCursor Cursor,
    ClientFence Fence,
    DateTimeOffset AcceptedAtUtc);

public readonly record struct MyOrbitServerSigningKeyId(Guid Value)
{
    public bool IsDefined => Value != Guid.Empty;
}

public enum MyOrbitServerSigningAlgorithm
{
    EcdsaP256Sha256P1363 = 0,
}

public enum MyOrbitServerSigningKeyState
{
    Active = 0,
    Retiring = 1,
}

/// <summary>
/// A key returned by the trusted application keyring. The keyring accepts only
/// pinned primary keys and rotations authenticated by an already-pinned key.
/// </summary>
public sealed class MyOrbitServerSigningPublicKey
{
    public const int MaximumSubjectPublicKeyInfoBytes = 1_024;
    private readonly byte[] _subjectPublicKeyInfo;

    private MyOrbitServerSigningPublicKey(
        MyOrbitServerSigningKeyId keyId,
        MyOrbitServerSigningAlgorithm algorithm,
        byte[] subjectPublicKeyInfo,
        DateTimeOffset validFromUtc,
        DateTimeOffset validUntilUtc,
        MyOrbitServerSigningKeyState state)
    {
        KeyId = keyId;
        Algorithm = algorithm;
        _subjectPublicKeyInfo = subjectPublicKeyInfo;
        ValidFromUtc = validFromUtc;
        ValidUntilUtc = validUntilUtc;
        State = state;
    }

    public MyOrbitServerSigningKeyId KeyId { get; }

    public MyOrbitServerSigningAlgorithm Algorithm { get; }

    public ReadOnlyMemory<byte> SubjectPublicKeyInfo => _subjectPublicKeyInfo;

    public DateTimeOffset ValidFromUtc { get; }

    public DateTimeOffset ValidUntilUtc { get; }

    public MyOrbitServerSigningKeyState State { get; }

    public static ControllerResult<MyOrbitServerSigningPublicKey> Create(
        MyOrbitServerSigningKeyId keyId,
        MyOrbitServerSigningAlgorithm algorithm,
        ReadOnlySpan<byte> subjectPublicKeyInfo,
        DateTimeOffset validFromUtc,
        DateTimeOffset validUntilUtc,
        MyOrbitServerSigningKeyState state)
    {
        if (!keyId.IsDefined ||
            !Enum.IsDefined(algorithm) ||
            subjectPublicKeyInfo.IsEmpty ||
            subjectPublicKeyInfo.Length > MaximumSubjectPublicKeyInfoBytes ||
            validFromUtc >= validUntilUtc ||
            !Enum.IsDefined(state))
        {
            return ControllerResult<MyOrbitServerSigningPublicKey>.Failure(ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "error.sync.server_signing_key_invalid"));
        }

        return ControllerResult<MyOrbitServerSigningPublicKey>.Success(
            new MyOrbitServerSigningPublicKey(
                keyId,
                algorithm,
                subjectPublicKeyInfo.ToArray(),
                validFromUtc,
                validUntilUtc,
                state));
    }
}

public enum DeletionCategoryPropagationState
{
    Pending = 0,
    Propagated = 1,
    Failed = 2,
}

public sealed record DeletionCategoryPropagation(
    SyncDataCategory Category,
    DeletionCategoryPropagationState State,
    DateTimeOffset? UpdatedAtUtc,
    ControllerError? Error);

public sealed record AuthenticatedDeletionPropagationStatus(
    DeletionAcknowledgementAad Aad,
    ClientFence Fence,
    IReadOnlyList<DeletionCategoryPropagation> Categories,
    bool IsComplete,
    DateTimeOffset ObservedAtUtc);

public sealed record ServerSignedDeletionPushAcknowledgement(
    AuthenticatedDeletionPushReceipt Payload,
    MyOrbitServerSigningKeyId SigningKeyId,
    ReadOnlyMemory<byte> Signature);

public sealed record ServerSignedDeletionPropagationStatus(
    AuthenticatedDeletionPropagationStatus Payload,
    MyOrbitServerSigningKeyId SigningKeyId,
    ReadOnlyMemory<byte> Signature);

/// <summary>
/// Untrusted remote adapter. Its outputs cannot advance local deletion state
/// until the pinned-key signature verifier authenticates and binds them.
/// </summary>
public interface ISyncedDeletionTransport
{
    ValueTask<ControllerResult<ServerSignedDeletionPushAcknowledgement>> PushAsync(
        SyncOperationContext context,
        OpaqueAuthHandle authorization,
        BoundDeletionPushRequest request,
        CancellationToken cancellationToken);

    ValueTask<ControllerResult<ServerSignedDeletionPropagationStatus>> GetPropagationStatusAsync(
        SyncOperationContext context,
        OpaqueAuthHandle authorization,
        AuthenticatedDeletionPushReceipt acceptedReceipt,
        CancellationToken cancellationToken);
}

/// <summary>
/// Trusted application keyring. Implementations pin the initial My Orbit key
/// and accept rotations only through a chain authenticated by a pinned key.
/// </summary>
public interface IMyOrbitServerSigningKeyProvider
{
    ValueTask<ControllerResult<MyOrbitServerSigningPublicKey>> GetPinnedKeyAsync(
        MyOrbitServerSigningKeyId keyId,
        CancellationToken cancellationToken);
}

/// <summary>Signature-verification boundary for untrusted remote results.</summary>
public interface IDeletionAcknowledgementSignatureVerifier
{
    ValueTask<ControllerResult<AuthenticatedDeletionPushReceipt>> VerifyPushAsync(
        SyncOperationContext context,
        BoundDeletionPushRequest acceptedRequest,
        ServerSignedDeletionPushAcknowledgement acknowledgement,
        CancellationToken cancellationToken);

    ValueTask<ControllerResult<AuthenticatedDeletionPropagationStatus>> VerifyStatusAsync(
        SyncOperationContext context,
        AuthenticatedDeletionPushReceipt acceptedReceipt,
        ServerSignedDeletionPropagationStatus status,
        CancellationToken cancellationToken);
}

public static class DeletionAcknowledgementRules
{
    public static SyncValidationResult ValidateRequest(BoundDeletionPushRequest? request)
    {
        var issues = new List<SyncValidationIssue>();
        if (request is null || request.Push is null || request.Binding is null)
        {
            issues.Add(new("deletion_push.required", "sync.deletion_push.required"));
            return new(issues);
        }

        issues.AddRange(SyncTransferRules.ValidatePush(request.Push).Issues);
        var binding = request.Binding;
        if (!binding.OperationId.IsDefined ||
            binding.RequestDigest is null ||
            !binding.IdempotencyKey.IsDefined ||
            binding.Categories is null ||
            binding.Categories.Count == 0 ||
            binding.Categories.Any(category => !SyncAllowlist.IsAllowed(category)) ||
            binding.EnvelopeIds is null ||
            binding.EnvelopeIds.Count == 0 ||
            binding.EnvelopeIds.Any(id => !id.IsDefined) ||
            binding.EnvelopeIds.Distinct().Count() != binding.EnvelopeIds.Count)
        {
            issues.Add(new("deletion_push.binding", "sync.deletion_push.binding_invalid"));
            return new(issues);
        }

        var payloadIds = request.Push.Envelopes.Select(item => item.Aad.EnvelopeId)
            .Concat(request.Push.Tombstones.Select(item => item.Aad.EnvelopeId))
            .Concat(request.Push.Purges.Select(item => item.Aad.EnvelopeId))
            .ToHashSet();
        if (!payloadIds.SetEquals(binding.EnvelopeIds))
        {
            issues.Add(new("deletion_push.envelopes", "sync.deletion_push.envelopes_mismatch"));
        }

        var payloadCategories = request.Push.Envelopes.Select(item => item.Aad.Category)
            .Concat(request.Push.Tombstones.Select(item => item.Aad.Category))
            .Concat(request.Push.Purges.Select(item => item.Aad.Category))
            .ToHashSet();
        if (!payloadCategories.SetEquals(binding.Categories))
        {
            issues.Add(new("deletion_push.categories", "sync.deletion_push.categories_mismatch"));
        }

        var payloadAads = request.Push.Envelopes.Select(item => item.Aad)
            .Concat(request.Push.Tombstones.Select(item => item.Aad))
            .Concat(request.Push.Purges.Select(item => item.Aad))
            .ToArray();
        var firstAad = payloadAads.FirstOrDefault();
        if (firstAad is null ||
            request.Push.DeviceId != request.Push.Fence.DeviceId ||
            payloadAads.Any(aad =>
                aad.ProfileId != firstAad.ProfileId ||
                aad.DeviceId != request.Push.DeviceId ||
                aad.KeysetId != firstAad.KeysetId ||
                aad.ClientGeneration != request.Push.Fence.ClientGeneration))
        {
            issues.Add(new("deletion_push.identity", "sync.deletion_push.payload_identity_mismatch"));
        }

        var computedDigest = ComputeRequestDigest(request.Push);
        if (!computedDigest.IsSuccess || !binding.RequestDigest.Equals(computedDigest.Value))
        {
            issues.Add(new("deletion_push.digest", "sync.deletion_push.digest_mismatch"));
        }

        return new(issues);
    }

    public static SyncValidationResult ValidateReceipt(
        BoundDeletionPushRequest acceptedRequest,
        AuthenticatedDeletionPushReceipt? receipt)
    {
        var issues = new List<SyncValidationIssue>(ValidateRequest(acceptedRequest).Issues);
        if (receipt is null ||
            !ValidateAad(receipt.Aad).IsValid ||
            !AadMatches(acceptedRequest, receipt.Aad) ||
            receipt.Fence is not { IsDefined: true } ||
            receipt.Fence.DeviceId != receipt.Aad.DeviceId ||
            receipt.Fence.ClientGeneration != receipt.Aad.ClientGeneration ||
            receipt.Fence.MinimumAcceptedGeneration <
                acceptedRequest.Push.Fence.MinimumAcceptedGeneration)
        {
            issues.Add(new("deletion_receipt.binding", "sync.deletion_receipt.binding_mismatch"));
        }

        return new(issues);
    }

    public static SyncValidationResult ValidatePropagationStatus(
        AuthenticatedDeletionPushReceipt acceptedReceipt,
        AuthenticatedDeletionPropagationStatus? status)
    {
        ArgumentNullException.ThrowIfNull(acceptedReceipt);
        var acceptedRequestAad = acceptedReceipt.Aad;
        var issues = new List<SyncValidationIssue>(ValidateAad(acceptedRequestAad).Issues);
        if (status is null ||
            !ValidateAad(status.Aad).IsValid ||
            !AadEquals(acceptedRequestAad, status.Aad) ||
            acceptedReceipt.Fence is not { IsDefined: true } ||
            status.Fence is not { IsDefined: true } ||
            status.Fence != acceptedReceipt.Fence ||
            status.Fence.DeviceId != status.Aad.DeviceId ||
            status.Fence.ClientGeneration != status.Aad.ClientGeneration)
        {
            issues.Add(new("deletion_status.binding", "sync.deletion_status.binding_mismatch"));
            return new(issues);
        }

        var categories = status.Categories;
        if (categories is null)
        {
            issues.Add(new("deletion_status.categories", "sync.deletion_status.categories_mismatch"));
            return new(issues);
        }

        if (categories.Select(item => item.Category).Distinct().Count() != categories.Count ||
            !categories.Select(item => item.Category).ToHashSet()
                .SetEquals(acceptedRequestAad.Categories))
        {
            issues.Add(new("deletion_status.categories", "sync.deletion_status.categories_mismatch"));
        }

        if (status.IsComplete && categories.Any(item =>
            item.State != DeletionCategoryPropagationState.Propagated ||
            item.Error is not null))
        {
            issues.Add(new("deletion_status.complete", "sync.deletion_status.not_complete"));
        }

        return new(issues);
    }

    public static SyncValidationResult ValidateSignedAcknowledgement(
        ServerSignedDeletionPushAcknowledgement? acknowledgement) =>
        ValidateSigned(
            acknowledgement?.Payload?.Aad,
            acknowledgement?.SigningKeyId,
            acknowledgement?.Signature);

    public static SyncValidationResult ValidateSignedPropagationStatus(
        ServerSignedDeletionPropagationStatus? status) =>
        ValidateSigned(status?.Payload?.Aad, status?.SigningKeyId, status?.Signature);

    public static ControllerResult<DeletionRequestDigest> ComputeRequestDigest(SyncPushRequest? push)
    {
        var validation = SyncTransferRules.ValidatePush(push);
        if (!validation.IsValid || push is null)
        {
            return ControllerResult<DeletionRequestDigest>.Failure(ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "error.sync.deletion_push_invalid"));
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendText(hash, "orbit-navigator|deletion-request-v1");
        AppendGuid(hash, push.DeviceId.Value);
        AppendInt64(hash, push.Fence.ClientGeneration);
        AppendInt64(hash, push.Fence.MinimumAcceptedGeneration);

        var records = push.Envelopes
            .Select(item => (Kind: SyncRecordKind.Upsert, item.Aad, item.Aad.EnvelopeId, item.Nonce, item.Ciphertext, item.AuthenticationTag))
            .Concat(push.Tombstones.Select(item =>
                (Kind: SyncRecordKind.Tombstone, item.Aad, item.Aad.EnvelopeId, item.Nonce, item.Ciphertext, item.AuthenticationTag)))
            .Concat(push.Purges.Select(item =>
                (Kind: SyncRecordKind.Purge, item.Aad, item.Aad.EnvelopeId, item.Nonce, item.Ciphertext, item.AuthenticationTag)))
            .OrderBy(item => item.EnvelopeId.Value)
            .ToArray();
        AppendInt64(hash, records.Length);
        foreach (var record in records)
        {
            AppendInt64(hash, (int)record.Kind);
            AppendGuid(hash, record.EnvelopeId.Value);
            AppendBytes(hash, SyncContractRules.EncodeCanonicalAad(record.Aad));
            AppendBytes(hash, record.Nonce.Span);
            AppendBytes(hash, record.Ciphertext.Span);
            AppendBytes(hash, record.AuthenticationTag.Span);
        }

        return DeletionRequestDigest.Create(hash.GetHashAndReset());
    }

    public static byte[] EncodeCanonicalAad(DeletionAcknowledgementAad aad)
    {
        ArgumentNullException.ThrowIfNull(aad);
        if (aad.ProtocolVersion != SyncProtocol.CurrentProtocolVersion ||
            aad.SchemaVersion != SyncProtocol.CurrentSchemaVersion ||
            aad.ProfileId.IsEmpty ||
            aad.DeviceId.IsEmpty ||
            !aad.KeysetId.IsDefined ||
            aad.ClientGeneration < 0 ||
            !aad.OperationId.IsDefined ||
            aad.Categories is null ||
            aad.Categories.Count == 0 ||
            aad.EnvelopeIds is null ||
            aad.EnvelopeIds.Count == 0 ||
            aad.RequestDigest is null ||
            !aad.IdempotencyKey.IsDefined)
        {
            throw new ArgumentException("Deletion acknowledgement AAD is invalid.", nameof(aad));
        }

        static string Id(Guid value) => value.ToString("N");
        var categories = string.Join(',', aad.Categories.OrderBy(item => item).Select(item => item.ToString()));
        var envelopes = string.Join(',', aad.EnvelopeIds.OrderBy(item => item.Value).Select(item => Id(item.Value)));
        var canonical = string.Join(
            '|',
            "orbit-navigator",
            "deletion-ack-aad",
            $"protocol={aad.ProtocolVersion}",
            $"schema={aad.SchemaVersion}",
            $"profile={Id(aad.ProfileId.Value)}",
            $"device={Id(aad.DeviceId.Value)}",
            $"keyset={Id(aad.KeysetId.Value)}",
            $"generation={aad.ClientGeneration}",
            $"operation={Id(aad.OperationId.Value)}",
            $"categories={categories}",
            $"envelopes={envelopes}",
            $"digest={Convert.ToHexString(aad.RequestDigest.Bytes.Span)}",
            $"idempotency={Id(aad.IdempotencyKey.Value)}");
        return Encoding.UTF8.GetBytes(canonical);
    }

    public static byte[] EncodeCanonicalSignedReceiptPayload(
        AuthenticatedDeletionPushReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!ValidateAad(receipt.Aad).IsValid ||
            receipt.Fence is not { IsDefined: true } ||
            receipt.Fence.DeviceId != receipt.Aad.DeviceId ||
            receipt.Fence.ClientGeneration != receipt.Aad.ClientGeneration ||
            receipt.Cursor is null ||
            string.IsNullOrWhiteSpace(receipt.Cursor.Value))
        {
            throw new ArgumentException("Deletion receipt payload is invalid.", nameof(receipt));
        }

        return Encoding.UTF8.GetBytes(string.Join(
            '|',
            "orbit-navigator",
            "server-signed-deletion-receipt-v1",
            $"aad={Convert.ToBase64String(EncodeCanonicalAad(receipt.Aad))}",
            $"cursor={Convert.ToBase64String(Encoding.UTF8.GetBytes(receipt.Cursor.Value))}",
            $"fence-device={receipt.Fence.DeviceId.Value:N}",
            $"fence-generation={receipt.Fence.ClientGeneration}",
            $"fence-minimum={receipt.Fence.MinimumAcceptedGeneration}",
            $"accepted-ms={receipt.AcceptedAtUtc.ToUnixTimeMilliseconds()}"));
    }

    public static byte[] EncodeCanonicalSignedStatusPayload(
        AuthenticatedDeletionPropagationStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (!ValidateAad(status.Aad).IsValid ||
            status.Fence is not { IsDefined: true } ||
            status.Fence.DeviceId != status.Aad.DeviceId ||
            status.Fence.ClientGeneration != status.Aad.ClientGeneration ||
            status.Categories is null)
        {
            throw new ArgumentException("Deletion propagation payload is invalid.", nameof(status));
        }

        var categories = string.Join(',', status.Categories
            .OrderBy(item => item.Category)
            .Select(EncodeCategoryPropagation));
        return Encoding.UTF8.GetBytes(string.Join(
            '|',
            "orbit-navigator",
            "server-signed-deletion-status-v1",
            $"aad={Convert.ToBase64String(EncodeCanonicalAad(status.Aad))}",
            $"fence-device={status.Fence.DeviceId.Value:N}",
            $"fence-generation={status.Fence.ClientGeneration}",
            $"fence-minimum={status.Fence.MinimumAcceptedGeneration}",
            $"complete={status.IsComplete}",
            $"observed-ms={status.ObservedAtUtc.ToUnixTimeMilliseconds()}",
            $"categories={categories}"));
    }

    private static SyncValidationResult ValidateAad(DeletionAcknowledgementAad? aad)
    {
        var issues = new List<SyncValidationIssue>();
        if (aad is null ||
            aad.ProtocolVersion != SyncProtocol.CurrentProtocolVersion ||
            aad.SchemaVersion != SyncProtocol.CurrentSchemaVersion ||
            aad.ProfileId.IsEmpty ||
            aad.DeviceId.IsEmpty ||
            !aad.KeysetId.IsDefined ||
            aad.ClientGeneration < 0 ||
            !aad.OperationId.IsDefined ||
            aad.Categories is null ||
            aad.Categories.Count == 0 ||
            aad.Categories.Any(category => !SyncAllowlist.IsAllowed(category)) ||
            aad.EnvelopeIds is null ||
            aad.EnvelopeIds.Count == 0 ||
            aad.EnvelopeIds.Any(id => !id.IsDefined) ||
            aad.EnvelopeIds.Distinct().Count() != aad.EnvelopeIds.Count ||
            aad.RequestDigest is null ||
            !aad.IdempotencyKey.IsDefined)
        {
            issues.Add(new("deletion_ack.aad", "sync.deletion_ack.aad_invalid"));
        }

        return new(issues);
    }

    private static SyncValidationResult ValidateSigned(
        DeletionAcknowledgementAad? aad,
        MyOrbitServerSigningKeyId? signingKeyId,
        ReadOnlyMemory<byte>? signature)
    {
        var issues = new List<SyncValidationIssue>(ValidateAad(aad).Issues);
        if (signingKeyId is not { IsDefined: true })
            issues.Add(new("deletion_ack.key_id", "sync.deletion_ack.key_id_invalid"));
        if (signature is null || signature.Value.Length != 64)
            issues.Add(new("deletion_ack.signature", "sync.deletion_ack.signature_size"));
        return new(issues);
    }

    private static string EncodeCategoryPropagation(DeletionCategoryPropagation item)
    {
        var error = item.Error is null
            ? "none"
            : string.Join(
                ':',
                item.Error.Code,
                Convert.ToBase64String(Encoding.UTF8.GetBytes(item.Error.MessageKey)),
                string.Join(';', item.Error.FormattingArguments
                    .OrderBy(argument => argument.Key)
                    .Select(argument => $"{argument.Key}={Convert.ToBase64String(Encoding.UTF8.GetBytes(argument.Value))}")),
                item.Error.IsRetryable);
        var updated = item.UpdatedAtUtc?.ToUnixTimeMilliseconds().ToString() ?? "none";
        return $"{item.Category}:{item.State}:{updated}:{error}";
    }

    private static void AppendText(IncrementalHash hash, string value) =>
        AppendBytes(hash, Encoding.UTF8.GetBytes(value));

    private static void AppendGuid(IncrementalHash hash, Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes);
        hash.AppendData(bytes);
    }

    private static void AppendInt64(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendBytes(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }

    private static bool AadMatches(
        BoundDeletionPushRequest request,
        DeletionAcknowledgementAad aad)
    {
        var payloadAad = request.Push.Envelopes.Select(item => item.Aad)
            .Concat(request.Push.Tombstones.Select(item => item.Aad))
            .Concat(request.Push.Purges.Select(item => item.Aad))
            .FirstOrDefault();
        return payloadAad is not null &&
            aad.ProfileId == payloadAad.ProfileId &&
            aad.DeviceId == request.Push.DeviceId &&
            aad.KeysetId == payloadAad.KeysetId &&
            aad.ClientGeneration == request.Push.Fence.ClientGeneration &&
            aad.OperationId == request.Binding.OperationId &&
            aad.Categories.ToHashSet().SetEquals(request.Binding.Categories) &&
            aad.EnvelopeIds.ToHashSet().SetEquals(request.Binding.EnvelopeIds) &&
            aad.RequestDigest.Equals(request.Binding.RequestDigest) &&
            aad.IdempotencyKey == request.Binding.IdempotencyKey;
    }

    private static bool AadEquals(
        DeletionAcknowledgementAad left,
        DeletionAcknowledgementAad right) =>
        left.ProtocolVersion == right.ProtocolVersion &&
        left.SchemaVersion == right.SchemaVersion &&
        left.ProfileId == right.ProfileId &&
        left.DeviceId == right.DeviceId &&
        left.KeysetId == right.KeysetId &&
        left.ClientGeneration == right.ClientGeneration &&
        left.OperationId == right.OperationId &&
        left.Categories.ToHashSet().SetEquals(right.Categories) &&
        left.EnvelopeIds.ToHashSet().SetEquals(right.EnvelopeIds) &&
        left.RequestDigest.Equals(right.RequestDigest) &&
        left.IdempotencyKey == right.IdempotencyKey;
}
