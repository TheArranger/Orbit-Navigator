using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.Contracts.Sync;

public sealed record SyncKeyMaterialHandle(Guid Value);

public interface ISyncRecordProjector
{
    ValueTask<ControllerResult<SyncRecordPayload>> ProjectAsync(
        SyncOperationContext context,
        SyncDataCategory category,
        SyncEntityId entityId,
        CancellationToken cancellationToken);
}

public interface ISyncEnvelopeCodec
{
    ValueTask<ControllerResult<EncryptedSyncEnvelope>> EncryptAsync(
        SyncOperationContext context,
        SyncKeyMaterialHandle keyMaterial,
        CanonicalSyncAad aad,
        SyncRecordPayload record,
        CancellationToken cancellationToken);

    ValueTask<ControllerResult<SyncRecordPayload>> DecryptAsync(
        SyncOperationContext context,
        SyncKeyMaterialHandle keyMaterial,
        EncryptedSyncEnvelope envelope,
        CancellationToken cancellationToken);

    ValueTask<ControllerResult<EncryptedSyncTombstone>> EncryptTombstoneAsync(
        SyncOperationContext context,
        SyncKeyMaterialHandle keyMaterial,
        CanonicalSyncAad aad,
        CancellationToken cancellationToken);

    ValueTask<ControllerResult<AuthenticatedSyncTombstoneReceipt>> DecryptAndValidateTombstoneAsync(
        SyncOperationContext context,
        SyncKeyMaterialHandle keyMaterial,
        EncryptedSyncTombstone tombstone,
        CancellationToken cancellationToken);

    ValueTask<ControllerResult<EncryptedPurgeCommand>> EncryptPurgeAsync(
        SyncOperationContext context,
        SyncKeyMaterialHandle keyMaterial,
        CanonicalSyncAad aad,
        DecryptedPurgeMarker marker,
        CancellationToken cancellationToken);

    ValueTask<ControllerResult<DecryptedPurgeMarker>> DecryptAndValidatePurgeAsync(
        SyncOperationContext context,
        SyncKeyMaterialHandle keyMaterial,
        EncryptedPurgeCommand command,
        CancellationToken cancellationToken);
}

public sealed record SyncCursor(string Value);

public sealed record SyncPushRequest(
    DeviceId DeviceId,
    ClientFence Fence,
    IReadOnlyList<EncryptedSyncEnvelope> Envelopes,
    IReadOnlyList<EncryptedSyncTombstone> Tombstones,
    IReadOnlyList<EncryptedPurgeCommand> Purges);

public sealed record SyncPushReceipt(
    SyncCursor Cursor,
    ClientFence Fence,
    int AcceptedEnvelopeCount,
    int AcceptedTombstoneCount,
    int AcceptedPurgeCount);

public sealed record SyncPullRequest(
    DeviceId DeviceId,
    ClientFence Fence,
    SyncCursor? After,
    int MaximumItems);

public sealed record SyncPullPage(
    SyncCursor Cursor,
    ClientFence Fence,
    IReadOnlyList<EncryptedSyncEnvelope> Envelopes,
    IReadOnlyList<EncryptedSyncTombstone> Tombstones,
    IReadOnlyList<EncryptedPurgeCommand> Purges,
    bool HasMore);

public sealed record SyncAuthorizationReceipt(OpaqueAuthHandle Handle);

public interface ISyncAccountAuthorization
{
    ValueTask<ControllerResult<SyncAuthorizationReceipt>> AuthorizeAsync(
        SyncOperationContext context,
        CancellationToken cancellationToken);
}

public interface ISyncTransport
{
    ValueTask<ControllerResult<SyncPushReceipt>> PushAsync(
        SyncOperationContext context,
        OpaqueAuthHandle authorization,
        SyncPushRequest request,
        CancellationToken cancellationToken);

    ValueTask<ControllerResult<SyncPullPage>> PullAsync(
        SyncOperationContext context,
        OpaqueAuthHandle authorization,
        SyncPullRequest request,
        CancellationToken cancellationToken);
}

public sealed record SyncDeviceRegistrationRequest(
    string DisplayName,
    ReadOnlyMemory<byte> PublicIdentityKey);

public sealed record SyncDeviceRegistration(
    DeviceId DeviceId,
    string DisplayName,
    DateTimeOffset RegisteredAtUtc,
    ClientFence Fence);

public sealed record SyncDeviceRevocationReceipt(
    DeviceId DeviceId,
    DateTimeOffset RevokedAtUtc,
    ClientFence Fence);

public interface ISyncDeviceRegistry
{
    ValueTask<ControllerResult<SyncDeviceRegistration>> RegisterAsync(
        SyncOperationContext context,
        OpaqueAuthHandle authorization,
        SyncDeviceRegistrationRequest request,
        CancellationToken cancellationToken);

    ValueTask<ControllerResult<SyncDeviceRevocationReceipt>> RevokeAsync(
        SyncOperationContext context,
        OpaqueAuthHandle authorization,
        OrbitNavigator.Contracts.Privacy.SensitiveActionAuthorizationToken sensitiveAuthorization,
        DeviceId deviceId,
        CancellationToken cancellationToken);
}

public static class SyncTransferRules
{
    public static SyncValidationResult ValidatePush(SyncPushRequest? request)
    {
        var issues = new List<SyncValidationIssue>();
        if (request is null)
        {
            issues.Add(new("push.required", "sync.validation.push.required"));
            return new(issues);
        }

        if (request.DeviceId.IsEmpty)
            issues.Add(new("push.device", "sync.validation.device.required"));
        if (!request.Fence.IsDefined)
            issues.Add(new("push.fence", "sync.validation.fence.required"));

        foreach (var envelope in request.Envelopes)
            issues.AddRange(SyncContractRules.ValidateEnvelope(envelope).Issues);
        foreach (var tombstone in request.Tombstones)
            issues.AddRange(SyncContractRules.ValidateTombstone(tombstone).Issues);
        foreach (var purge in request.Purges)
            issues.AddRange(SyncContractRules.ValidatePurgeCommand(purge).Issues);

        var envelopeIds = request.Envelopes.Select(value => value.Aad.EnvelopeId)
            .Concat(request.Tombstones.Select(value => value.Aad.EnvelopeId))
            .Concat(request.Purges.Select(value => value.Aad.EnvelopeId))
            .ToArray();
        if (envelopeIds.Distinct().Count() != envelopeIds.Length)
            issues.Add(new("push.envelopes", "sync.validation.envelope.duplicate"));

        return new(issues);
    }

    public static SyncValidationResult ValidatePull(SyncPullRequest? request)
    {
        var issues = new List<SyncValidationIssue>();
        if (request is null)
        {
            issues.Add(new("pull.required", "sync.validation.pull.required"));
            return new(issues);
        }

        if (request.DeviceId.IsEmpty)
            issues.Add(new("pull.device", "sync.validation.device.required"));
        if (!request.Fence.IsDefined)
            issues.Add(new("pull.fence", "sync.validation.fence.required"));
        if (request.MaximumItems is < 1 or > 1_000)
            issues.Add(new("pull.maximum", "sync.validation.pull.maximum"));
        return new(issues);
    }
}
