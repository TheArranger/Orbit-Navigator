using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Sync;

namespace OrbitNavigator.Sync.State;

public sealed record SyncStateScope(
    ProfileId ProfileId,
    DeviceId DeviceId,
    long ClientGeneration)
{
    public bool IsDefined =>
        !ProfileId.IsEmpty &&
        !DeviceId.IsEmpty &&
        ClientGeneration >= 0;
}

public sealed record LocalChangeCursor(string Value)
{
    public bool IsDefined => !string.IsNullOrWhiteSpace(Value) && Value.Length <= 4_096;
}

/// <summary>
/// Durable compare-and-swap checkpoint for independent local-push and remote-pull
/// progress. A store must persist a returned version before reporting success.
/// </summary>
public sealed record SyncCheckpoint(
    SyncStateScope Scope,
    long Version,
    LocalChangeCursor? PushedThrough,
    SyncCursor? PulledThrough)
{
    public bool IsDefined =>
        Scope is { IsDefined: true } &&
        Version >= 0 &&
        (PushedThrough is null || PushedThrough.IsDefined) &&
        (PulledThrough is null || SyncStateValidation.IsDefined(PulledThrough));
}

public interface IDurableSyncCheckpointStore
{
    ValueTask<ControllerResult<SyncCheckpoint>> LoadAsync(
        SyncOperationContext context,
        SyncStateScope scope,
        CancellationToken cancellationToken);

    /// <summary>
    /// Atomically advances local-change progress if <paramref name="expected"/>
    /// is still current. Conflict must be returned instead of overwriting a newer
    /// checkpoint.
    /// </summary>
    ValueTask<ControllerResult<SyncCheckpoint>> CommitPushAsync(
        SyncOperationContext context,
        SyncCheckpoint expected,
        LocalChangeCursor pushedThrough,
        CancellationToken cancellationToken);

    /// <summary>
    /// Atomically advances the pull cursor only after the complete page has been
    /// durably and idempotently applied.
    /// </summary>
    ValueTask<ControllerResult<SyncCheckpoint>> CommitPullAsync(
        SyncOperationContext context,
        SyncCheckpoint expected,
        SyncCursor pulledThrough,
        CancellationToken cancellationToken);
}

public sealed record SyncSequenceReservation(
    SyncStateScope Scope,
    long Sequence);

public interface IDurableSyncSequenceAllocator
{
    /// <summary>
    /// Durably reserves the next sequence before returning it. Returned values
    /// must never repeat for the same profile, device, and generation, including
    /// after process restart.
    /// </summary>
    ValueTask<ControllerResult<SyncSequenceReservation>> ReserveNextAsync(
        SyncOperationContext context,
        SyncStateScope scope,
        CancellationToken cancellationToken);
}

public static class SyncStateValidation
{
    public static bool IsDefined(SyncCursor? cursor) =>
        cursor is not null &&
        !string.IsNullOrWhiteSpace(cursor.Value) &&
        cursor.Value.Length <= 4_096;
}
