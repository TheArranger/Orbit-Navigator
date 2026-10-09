using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Sync;
using OrbitNavigator.Sync.State;
using Xunit;

namespace OrbitNavigator.Sync.Tests.State;

public sealed class LocalSyncProfileBindingStoreTests
{
    [Fact]
    public async Task OriginBindingUsesLocalProfileAndIsDurableAndIdempotent()
    {
        var storage = new MemoryStorage();
        var store = new LocalSyncProfileBindingStore(storage);
        var browsing = Browsing(BrowserProfileMode.Normal, LocalProfile);

        var first = await store.BindAsync(
            browsing,
            new SyncOperationId(Guid.NewGuid()),
            LocalProfile,
            LocalSyncProfileBindingKind.AccountOrigin);
        var repeated = await store.BindAsync(
            browsing,
            new SyncOperationId(Guid.NewGuid()),
            LocalProfile,
            LocalSyncProfileBindingKind.AccountOrigin);
        var loaded = await store.LoadAsync(browsing, new SyncOperationId(Guid.NewGuid()));

        Assert.True(first.IsSuccess);
        Assert.True(repeated.IsSuccess);
        Assert.True(loaded.IsSuccess);
        Assert.Equal(LocalProfile, loaded.Value!.LocalProfileId);
        Assert.Equal(LocalProfile, loaded.Value.SyncProfileId);
        Assert.Equal(LocalSyncProfileBindingKind.AccountOrigin, loaded.Value.Kind);
        Assert.Equal(1, storage.WriteCount);
    }

    [Fact]
    public async Task JoinedBindingMapsRemoteIdentityWithoutRewritingLocalIdentity()
    {
        var storage = new MemoryStorage();
        var store = new LocalSyncProfileBindingStore(storage);
        var browsing = Browsing(BrowserProfileMode.Normal, LocalProfile);

        var result = await store.BindAsync(
            browsing,
            new SyncOperationId(Guid.NewGuid()),
            AccountSyncProfile,
            LocalSyncProfileBindingKind.JoinedAccount);

        Assert.True(result.IsSuccess);
        Assert.Equal(LocalProfile, result.Value!.LocalProfileId);
        Assert.Equal(AccountSyncProfile, result.Value.SyncProfileId);
        Assert.NotEqual(result.Value.LocalProfileId, result.Value.SyncProfileId);
        Assert.Equal(LocalProfile, browsing.Privacy.ProfileId);
    }

    [Fact]
    public async Task ExistingBindingCannotBeSilentlyReplacedOrReclassified()
    {
        var storage = new MemoryStorage();
        var store = new LocalSyncProfileBindingStore(storage);
        var browsing = Browsing(BrowserProfileMode.Normal, LocalProfile);
        Assert.True((await store.BindAsync(
            browsing,
            new SyncOperationId(Guid.NewGuid()),
            AccountSyncProfile,
            LocalSyncProfileBindingKind.JoinedAccount)).IsSuccess);

        var differentIdentity = await store.BindAsync(
            browsing,
            new SyncOperationId(Guid.NewGuid()),
            new ProfileId(Guid.NewGuid()),
            LocalSyncProfileBindingKind.JoinedAccount);
        var reclassified = await store.BindAsync(
            browsing,
            new SyncOperationId(Guid.NewGuid()),
            LocalProfile,
            LocalSyncProfileBindingKind.AccountOrigin);

        Assert.Equal(ControllerErrorCode.Conflict, differentIdentity.Error?.Code);
        Assert.Equal(ControllerErrorCode.Conflict, reclassified.Error?.Code);
        Assert.Equal(1, storage.WriteCount);
    }

    [Fact]
    public async Task SimultaneousFirstBindingsAcrossStoreInstancesCannotReplaceAccountIdentity()
    {
        var storage = new MemoryStorage { PauseFirstWrite = true };
        var firstStore = new LocalSyncProfileBindingStore(storage);
        var competingStore = new LocalSyncProfileBindingStore(storage);
        var browsing = Browsing(BrowserProfileMode.Normal, LocalProfile);
        var first = firstStore.BindAsync(browsing, new(Guid.NewGuid()), AccountSyncProfile,
            LocalSyncProfileBindingKind.JoinedAccount).AsTask();
        await storage.FirstWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var competing = competingStore.BindAsync(browsing, new(Guid.NewGuid()), new(Guid.NewGuid()),
            LocalSyncProfileBindingKind.JoinedAccount).AsTask();
        try
        {
            // A second instance must wait before its read, not race an initial
            // missing entry and perform another unconditional first write.
            Assert.False(competing.IsCompleted);
            Assert.Equal(1, storage.WriteCount);
        }
        finally
        {
            storage.ReleaseFirstWrite.TrySetResult();
        }
        Assert.True((await first).IsSuccess);
        Assert.Equal(ControllerErrorCode.Conflict, (await competing).Error?.Code);
        var loaded = await competingStore.LoadAsync(browsing, new(Guid.NewGuid()));
        Assert.Equal(AccountSyncProfile, loaded.Value!.SyncProfileId);
        Assert.Equal(1, storage.WriteCount);
    }

    [Fact]
    public async Task OriginCannotClaimForeignSyncProfile()
    {
        var storage = new MemoryStorage();
        var store = new LocalSyncProfileBindingStore(storage);

        var result = await store.BindAsync(
            Browsing(BrowserProfileMode.Normal, LocalProfile),
            new SyncOperationId(Guid.NewGuid()),
            AccountSyncProfile,
            LocalSyncProfileBindingKind.AccountOrigin);

        Assert.Equal(ControllerErrorCode.InvalidRequest, result.Error?.Code);
        Assert.Equal(0, storage.CallCount);
    }

    [Fact]
    public async Task PrivateBindingAndLoadAreDeniedBeforeStorage()
    {
        var storage = new MemoryStorage();
        var store = new LocalSyncProfileBindingStore(storage);
        var browsing = Browsing(BrowserProfileMode.Private, LocalProfile);

        var bind = await store.BindAsync(
            browsing,
            new SyncOperationId(Guid.NewGuid()),
            AccountSyncProfile,
            LocalSyncProfileBindingKind.JoinedAccount);
        var load = await store.LoadAsync(browsing, new SyncOperationId(Guid.NewGuid()));

        Assert.Equal(ControllerErrorCode.PolicyDenied, bind.Error?.Code);
        Assert.Equal(ControllerErrorCode.PolicyDenied, load.Error?.Code);
        Assert.Equal(0, storage.CallCount);
    }

    [Fact]
    public async Task CorruptOrMixedLocalProfileBindingFailsClosed()
    {
        var storage = new MemoryStorage();
        var store = new LocalSyncProfileBindingStore(storage);
        Assert.True((await store.BindAsync(
            Browsing(BrowserProfileMode.Normal, LocalProfile),
            new SyncOperationId(Guid.NewGuid()),
            AccountSyncProfile,
            LocalSyncProfileBindingKind.JoinedAccount)).IsSuccess);

        var mixed = await store.LoadAsync(
            Browsing(BrowserProfileMode.Normal, new ProfileId(Guid.NewGuid())),
            new SyncOperationId(Guid.NewGuid()));
        Assert.Equal(ControllerErrorCode.IntegrityFailure, mixed.Error?.Code);

        storage.Payload = [1, 2, 3];
        var corrupt = await store.LoadAsync(
            Browsing(BrowserProfileMode.Normal, LocalProfile),
            new SyncOperationId(Guid.NewGuid()));
        Assert.Equal(ControllerErrorCode.IntegrityFailure, corrupt.Error?.Code);
    }

    private static BrowsingContext Browsing(BrowserProfileMode mode, ProfileId profileId) => new(
        new PrivacyContext(profileId, new BrowserSessionId(Guid.NewGuid()), mode),
        new BrowserWindowId(Guid.NewGuid()),
        new BrowserTabId(Guid.NewGuid()),
        null);

    private static readonly ProfileId LocalProfile = new(
        Guid.ParseExact("11111111111111111111111111111111", "N"));
    private static readonly ProfileId AccountSyncProfile = new(
        Guid.ParseExact("22222222222222222222222222222222", "N"));

    private sealed class MemoryStorage : IProfileStorage
    {
        public byte[]? Payload { get; set; }
        public ProfileStorageRevision? Revision { get; private set; }
        public int CallCount { get; private set; }
        public int WriteCount { get; private set; }
        public bool PauseFirstWrite { get; init; }
        public TaskCompletionSource FirstWriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<ControllerResult<ProfileStorageEntry>> ReadAsync(
            ProfileStorageAddress address,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult(Payload is null
                ? ControllerResult<ProfileStorageEntry>.Failure(ControllerError.Create(
                    ControllerErrorCode.NotFound, "error.profile_storage.not-found"))
                : ProfileStorageEntry.Create(Revision!.Value, Payload));
        }

        public async ValueTask<ControllerResult<ProfileStorageWriteReceipt>> WriteAsync(
            ProfileStorageWriteRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            WriteCount++;
            if (PauseFirstWrite && WriteCount == 1)
            {
                FirstWriteStarted.TrySetResult();
                await ReleaseFirstWrite.Task.WaitAsync(cancellationToken);
            }
            Payload = request.Payload.ToArray();
            Revision = new ProfileStorageRevision(Guid.NewGuid());
            return ControllerResult<ProfileStorageWriteReceipt>.Success(new(Revision.Value));
        }

        public ValueTask<ControllerResult> DeleteAsync(
            ProfileStorageAddress address,
            ProfileStorageRevision? expectedRevision = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Payload = null;
            Revision = null;
            return ValueTask.FromResult(ControllerResult.Success());
        }
    }
}
