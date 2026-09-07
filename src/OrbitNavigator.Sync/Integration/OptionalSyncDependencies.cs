using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Sync;
using OrbitNavigator.Sync.State;

namespace OrbitNavigator.Sync.Integration;

public sealed record OptionalSyncSession(
    OpaqueAuthHandle Authorization,
    DeviceId DeviceId,
    SyncKeyMaterialHandle KeyMaterial,
    SyncKeysetId KeysetId,
    long KeyEpoch,
    ClientFence Fence)
{
    public bool IsDefined =>
        !Authorization.IsEmpty &&
        !DeviceId.IsEmpty &&
        KeyMaterial.Value != Guid.Empty &&
        KeysetId.IsDefined &&
        KeyEpoch >= 0 &&
        Fence is { IsDefined: true } &&
        Fence.DeviceId == DeviceId;
}

public interface IOptionalSyncSessionProvider
{
    /// <summary>
    /// Returns a fully authorized session, or a typed Unavailable result when the
    /// user is signed out or optional sync is unavailable.
    /// </summary>
    ValueTask<ControllerResult<OptionalSyncSession>> GetAuthorizedSessionAsync(
        SyncOperationContext context,
        CancellationToken cancellationToken);
}

public sealed record LocalSyncChange(
    SyncRecordKind RecordKind,
    SyncDataCategory Category,
    SyncEntityId EntityId);

public sealed record LocalSyncChangePage(
    IReadOnlyList<LocalSyncChange> Changes,
    LocalChangeCursor? NextCursor,
    bool HasMore);

public interface ILocalSyncChangeCatalog
{
    /// <summary>
    /// Lists only local allowlisted changes after a durable cursor. Returned pages
    /// must be stable until their NextCursor is durably committed.
    /// </summary>
    ValueTask<ControllerResult<LocalSyncChangePage>> ListPendingAsync(
        SyncOperationContext context,
        LocalChangeCursor? after,
        int maximumItems,
        CancellationToken cancellationToken);
}

public sealed record AuthenticatedRemoteUpsert(
    CanonicalSyncAad Aad,
    SyncRecordPayload Payload);

public sealed record AuthenticatedRemoteTombstone(
    CanonicalSyncAad Aad,
    AuthenticatedSyncTombstoneReceipt Receipt);

public sealed record AuthenticatedRemotePurge(
    CanonicalSyncAad Aad,
    DecryptedPurgeMarker Marker);

public sealed record AuthenticatedSyncPage(
    SyncCursor Cursor,
    ClientFence Fence,
    IReadOnlyList<AuthenticatedRemoteUpsert> Upserts,
    IReadOnlyList<AuthenticatedRemoteTombstone> Tombstones,
    IReadOnlyList<AuthenticatedRemotePurge> Purges);

public sealed record AuthenticatedPageApplyReceipt(
    SyncCursor Cursor,
    int AppliedUpsertCount,
    int AppliedTombstoneCount,
    int AppliedPurgeCount);

public interface IAuthenticatedSyncApplyTarget
{
    /// <summary>
    /// Atomically and idempotently applies one fully authenticated page. The target
    /// must order by authenticated device/generation/sequence, remember envelope
    /// identities, and enforce each purge generation fence before accepting stale
    /// upserts or tombstones, regardless of their array order in the page.
    /// </summary>
    ValueTask<ControllerResult<AuthenticatedPageApplyReceipt>> ApplyPageAsync(
        SyncOperationContext context,
        AuthenticatedSyncPage page,
        CancellationToken cancellationToken);
}

public sealed record OptionalSyncRunReceipt(
    SyncAvailability Availability,
    int PushedUpsertCount,
    int PushedTombstoneCount,
    int AppliedUpsertCount,
    int AppliedTombstoneCount,
    int AppliedPurgeCount,
    int PulledPageCount,
    SyncCursor? FinalPullCursor);
