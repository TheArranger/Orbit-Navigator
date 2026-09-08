using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Sync;
using OrbitNavigator.Sync.Integration;
using OrbitNavigator.Sync.State;
using Xunit;

namespace OrbitNavigator.Sync.Tests.State;

public sealed class ProfileStorageSyncEntityIndexReconcilerTests
{
    [Fact]
    public async Task FirstInventoryAppendsUpsertsThenUnchangedInventoryIsInert()
    {
        var storage = new MemoryStorage();
        var journal = new RecordingJournal();
        var reconciler = new ProfileStorageSyncEntityIndexReconciler(storage, journal);
        var browsing = Browsing(BrowserProfileMode.Normal);
        LocalSyncEntityVersion[] inventory = [
            new(SyncDataCategory.History, Entity(1), 10),
            new(SyncDataCategory.OpenTabs, Entity(2), 4),
        ];

        var first = await reconciler.ReconcileAsync(browsing, Operation(), inventory);
        var second = await reconciler.ReconcileAsync(browsing, Operation(), inventory);

        Assert.True(first.IsSuccess);
        Assert.Equal(2, first.Value!.UpsertCount);
        Assert.Equal(0, first.Value.TombstoneCount);
        Assert.True(second.IsSuccess);
        Assert.Equal(0, second.Value!.UpsertCount);
        Assert.Equal(0, second.Value.TombstoneCount);
        Assert.Equal(2, journal.Changes.Count);
        Assert.All(journal.Changes, change => Assert.Equal(SyncRecordKind.Upsert, change.RecordKind));
    }

    [Fact]
    public async Task ChangedAndRemovedEntitiesProduceUpsertAndTombstoneBeforeIndexAdvance()
    {
        var storage = new MemoryStorage();
        var journal = new RecordingJournal();
        var reconciler = new ProfileStorageSyncEntityIndexReconciler(storage, journal);
        var browsing = Browsing(BrowserProfileMode.Normal);
        var retained = Entity(1);
        var removed = Entity(2);
        Assert.True((await reconciler.ReconcileAsync(browsing, Operation(), [
            new(SyncDataCategory.History, retained, 1),
            new(SyncDataCategory.OpenTabs, removed, 1),
        ])).IsSuccess);
        journal.Changes.Clear();

        var result = await reconciler.ReconcileAsync(browsing, Operation(), [
            new LocalSyncEntityVersion(SyncDataCategory.History, retained, 2),
        ]);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value!.UpsertCount);
        Assert.Equal(1, result.Value.TombstoneCount);
        Assert.Collection(
            journal.Changes,
            change =>
            {
                Assert.Equal(removed, change.EntityId);
                Assert.Equal(SyncRecordKind.Tombstone, change.RecordKind);
            },
            change =>
            {
                Assert.Equal(retained, change.EntityId);
                Assert.Equal(SyncRecordKind.Upsert, change.RecordKind);
            });
    }

    [Fact]
    public async Task FailedIndexCommitCausesSafeReplayOnNextRun()
    {
        var storage = new MemoryStorage { FailNextWrite = true };
        var journal = new RecordingJournal();
        var reconciler = new ProfileStorageSyncEntityIndexReconciler(storage, journal);
        var browsing = Browsing(BrowserProfileMode.Normal);
        var inventory = new[] {
            new LocalSyncEntityVersion(SyncDataCategory.History, Entity(1), 1),
        };

        var failed = await reconciler.ReconcileAsync(browsing, Operation(), inventory);
        var retried = await reconciler.ReconcileAsync(browsing, Operation(), inventory);

        Assert.Equal(ControllerErrorCode.Unavailable, failed.Error?.Code);
        Assert.True(retried.IsSuccess);
        Assert.Equal(2, journal.Changes.Count);
        Assert.All(journal.Changes, change => Assert.Equal(SyncRecordKind.Upsert, change.RecordKind));
    }

    [Fact]
    public async Task PrivateInventoryIsDeniedBeforeStorageOrJournal()
    {
        var storage = new MemoryStorage();
        var journal = new RecordingJournal();
        var reconciler = new ProfileStorageSyncEntityIndexReconciler(storage, journal);

        var result = await reconciler.ReconcileAsync(
            Browsing(BrowserProfileMode.Private),
            Operation(),
            [new(SyncDataCategory.History, Entity(1), 1)]);

        Assert.Equal(ControllerErrorCode.PolicyDenied, result.Error?.Code);
        Assert.Equal(0, storage.CallCount);
        Assert.Empty(journal.Changes);
    }

    [Fact]
    public async Task CorruptOrMixedProfileIndexFailsClosedWithoutJournalMutation()
    {
        var storage = new MemoryStorage { Payload = [1, 2, 3, 4] };
        var journal = new RecordingJournal();
        var reconciler = new ProfileStorageSyncEntityIndexReconciler(storage, journal);

        var result = await reconciler.ReconcileAsync(
            Browsing(BrowserProfileMode.Normal),
            Operation(),
            []);

        Assert.Equal(ControllerErrorCode.IntegrityFailure, result.Error?.Code);
        Assert.Empty(journal.Changes);
    }

    private static SyncEntityId Entity(int value) =>
        new(Guid.Parse($"00000000-0000-0000-0000-{value:D12}"));

    private static SyncOperationId Operation() => new(Guid.NewGuid());

    private static BrowsingContext Browsing(BrowserProfileMode mode) => new(
        new PrivacyContext(
            new ProfileId(Guid.NewGuid()),
            new BrowserSessionId(Guid.NewGuid()),
            mode),
        new BrowserWindowId(Guid.NewGuid()),
        new BrowserTabId(Guid.NewGuid()),
        null);

    private sealed class RecordingJournal : ILocalSyncChangeRecorder
    {
        public List<LocalSyncChange> Changes { get; } = [];

        public ValueTask<ControllerResult<LocalSyncChangeReceipt>> RecordAsync(
            BrowsingContext browsing,
            SyncOperationId operationId,
            SyncRecordKind recordKind,
            SyncDataCategory category,
            SyncEntityId entityId,
            CancellationToken cancellationToken = default)
        {
            var change = new LocalSyncChange(recordKind, category, entityId);
            Changes.Add(change);
            return ValueTask.FromResult(ControllerResult<LocalSyncChangeReceipt>.Success(new(
                new LocalChangeCursor(Changes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                change)));
        }
    }

    private sealed class MemoryStorage : IProfileStorage
    {
        public byte[]? Payload { get; set; }
        public ProfileStorageRevision? Revision { get; private set; }
        public bool FailNextWrite { get; set; }
        public int CallCount { get; private set; }

        public ValueTask<ControllerResult<ProfileStorageEntry>> ReadAsync(
            ProfileStorageAddress address,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (Payload is null)
            {
                return ValueTask.FromResult(ControllerResult<ProfileStorageEntry>.Failure(
                    ControllerError.Create(
                        ControllerErrorCode.NotFound,
                        "error.profile_storage.not-found")));
            }
            Revision ??= new ProfileStorageRevision(Guid.NewGuid());
            return ValueTask.FromResult(ProfileStorageEntry.Create(Revision.Value, Payload));
        }

        public ValueTask<ControllerResult<ProfileStorageWriteReceipt>> WriteAsync(
            ProfileStorageWriteRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (FailNextWrite)
            {
                FailNextWrite = false;
                return ValueTask.FromResult(ControllerResult<ProfileStorageWriteReceipt>.Failure(
                    ControllerError.Create(
                        ControllerErrorCode.Unavailable,
                        "error.profile_storage.unavailable")));
            }
            if (request.ExpectedRevision is { } expected && expected != Revision)
            {
                return ValueTask.FromResult(ControllerResult<ProfileStorageWriteReceipt>.Failure(
                    ControllerError.Create(
                        ControllerErrorCode.Conflict,
                        "error.profile_storage.revision-conflict")));
            }
            Payload = request.Payload.ToArray();
            Revision = new ProfileStorageRevision(Guid.NewGuid());
            return ValueTask.FromResult(
                ControllerResult<ProfileStorageWriteReceipt>.Success(new(Revision.Value)));
        }

        public ValueTask<ControllerResult> DeleteAsync(
            ProfileStorageAddress address,
            ProfileStorageRevision? expectedRevision = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ControllerResult.Success());
    }
}
