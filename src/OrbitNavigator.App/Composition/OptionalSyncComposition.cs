using OrbitNavigator.Contracts.Sync;
using OrbitNavigator.Sync.Integration;

namespace OrbitNavigator.App.Composition;

public sealed record OptionalSyncCompositionStatus(
    SyncAvailability Availability,
    bool RecoveryAvailable,
    bool SyncedDeletionAvailable,
    string? RecoveryUnavailableMessageKey,
    string? SyncedDeletionUnavailableMessageKey);

/// <summary>
/// Foundation-owned optional integration seam. Signed-out construction keeps
/// local browsing available and exposes no callable remote coordinator.
/// </summary>
public sealed class OptionalSyncComposition
{
    private OptionalSyncComposition(
        OptionalSyncCompositionStatus status,
        OptionalSyncCoordinator? coordinator,
        ISyncKeyRestoration? keyRestoration,
        ISyncedDataDeletion? syncedDeletion)
    {
        Status = status;
        Coordinator = coordinator;
        KeyRestoration = keyRestoration;
        SyncedDeletion = syncedDeletion;
    }

    public OptionalSyncCompositionStatus Status { get; }

    public OptionalSyncCoordinator? Coordinator { get; }

    public ISyncKeyRestoration? KeyRestoration { get; }

    public ISyncedDataDeletion? SyncedDeletion { get; }

    public static OptionalSyncComposition SignedOut() => new(
        new OptionalSyncCompositionStatus(
            SyncAvailabilityPolicy.Evaluate(hasAccountAuthorization: false),
            false,
            false,
            "sync.recovery.account-unavailable",
            "sync.deletion.account-unavailable"),
        null,
        null,
        null);

    public static OptionalSyncComposition Connected(
        OptionalSyncCoordinator coordinator,
        ISyncKeyRestoration keyRestoration,
        ISyncedDataDeletion syncedDeletion) => new(
        new OptionalSyncCompositionStatus(
            SyncAvailabilityPolicy.Evaluate(hasAccountAuthorization: true),
            true,
            true,
            null,
            null),
        coordinator ?? throw new ArgumentNullException(nameof(coordinator)),
        keyRestoration ?? throw new ArgumentNullException(nameof(keyRestoration)),
        syncedDeletion ?? throw new ArgumentNullException(nameof(syncedDeletion)));
}
