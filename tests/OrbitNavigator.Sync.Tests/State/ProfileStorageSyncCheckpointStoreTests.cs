using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Sync;
using OrbitNavigator.Sync.State;
using Xunit;

namespace OrbitNavigator.Sync.Tests.State;

public sealed class ProfileStorageSyncCheckpointStoreTests
{
    [Fact]
    public async Task MissingStateLoadsAsInitialCheckpoint()
    {
        var fixture = new Fixture();

        var loaded = await fixture.Store.LoadAsync(
            fixture.Context,
            fixture.Scope,
            CancellationToken.None);

        Assert.True(loaded.IsSuccess);
        Assert.Equal(new SyncCheckpoint(fixture.Scope, 0, null, null), loaded.Value);
        Assert.Equal(1, fixture.Storage.ReadCalls);
        Assert.Equal(0, fixture.Storage.WriteCalls);
    }

    [Fact]
    public async Task PushAndPullCursorsSurviveFreshStoreInstance()
    {
        var fixture = new Fixture();
        var initial = (await fixture.Store.LoadAsync(
            fixture.Context,
            fixture.Scope,
            CancellationToken.None)).Value!;
        var pushed = await fixture.Store.CommitPushAsync(
            fixture.Context,
            initial,
            new LocalChangeCursor("local:17"),
            CancellationToken.None);
        var pulled = await fixture.Store.CommitPullAsync(
            fixture.Context,
            pushed.Value!,
            new SyncCursor("remote:41"),
            CancellationToken.None);

        var restarted = new ProfileStorageSyncCheckpointStore(fixture.Storage);
        var loaded = await restarted.LoadAsync(
            fixture.Context,
            fixture.Scope,
            CancellationToken.None);

        Assert.True(pushed.IsSuccess);
        Assert.True(pulled.IsSuccess);
        Assert.Equal(2, pulled.Value!.Version);
        Assert.Equal("local:17", loaded.Value!.PushedThrough!.Value);
        Assert.Equal("remote:41", loaded.Value.PulledThrough!.Value);
        Assert.Equal(pulled.Value, loaded.Value);
    }

    [Fact]
    public async Task StaleExpectedCheckpointCannotOverwriteNewerProgress()
    {
        var fixture = new Fixture();
        var initial = (await fixture.Store.LoadAsync(
            fixture.Context,
            fixture.Scope,
            CancellationToken.None)).Value!;
        var first = await fixture.Store.CommitPushAsync(
            fixture.Context,
            initial,
            new LocalChangeCursor("first"),
            CancellationToken.None);
        var stale = await fixture.Store.CommitPullAsync(
            fixture.Context,
            initial,
            new SyncCursor("stale"),
            CancellationToken.None);
        var loaded = await fixture.Store.LoadAsync(
            fixture.Context,
            fixture.Scope,
            CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.False(stale.IsSuccess);
        Assert.Equal(ControllerErrorCode.Conflict, stale.Error!.Code);
        Assert.Equal("first", loaded.Value!.PushedThrough!.Value);
        Assert.Null(loaded.Value.PulledThrough);
    }

    [Fact]
    public async Task ConcurrentFirstAdvanceHasSingleWinnerWithinProcess()
    {
        var fixture = new Fixture();
        var initial = (await fixture.Store.LoadAsync(
            fixture.Context,
            fixture.Scope,
            CancellationToken.None)).Value!;
        var secondStore = new ProfileStorageSyncCheckpointStore(fixture.Storage);

        var results = await Task.WhenAll(
            fixture.Store.CommitPushAsync(
                fixture.Context,
                initial,
                new LocalChangeCursor("one"),
                CancellationToken.None).AsTask(),
            secondStore.CommitPushAsync(
                fixture.Context,
                initial,
                new LocalChangeCursor("two"),
                CancellationToken.None).AsTask());

        Assert.Equal(1, results.Count(value => value.IsSuccess));
        Assert.Equal(1, results.Count(value => value.Error?.Code == ControllerErrorCode.Conflict));
    }

    [Fact]
    public async Task CorruptCheckpointFailsClosed()
    {
        var fixture = new Fixture();
        var initial = (await fixture.Store.LoadAsync(
            fixture.Context,
            fixture.Scope,
            CancellationToken.None)).Value!;
        var committed = await fixture.Store.CommitPushAsync(
            fixture.Context,
            initial,
            new LocalChangeCursor("cursor"),
            CancellationToken.None);
        Assert.True(committed.IsSuccess);
        fixture.Storage.CorruptOnlyEntry();

        var loaded = await fixture.Store.LoadAsync(
            fixture.Context,
            fixture.Scope,
            CancellationToken.None);

        Assert.False(loaded.IsSuccess);
        Assert.Equal(ControllerErrorCode.IntegrityFailure, loaded.Error!.Code);
    }

    [Fact]
    public async Task ProfileMismatchFailsBeforeStorageCall()
    {
        var fixture = new Fixture();
        var wrong = fixture.Scope with { ProfileId = new ProfileId(Guid.NewGuid()) };

        var loaded = await fixture.Store.LoadAsync(
            fixture.Context,
            wrong,
            CancellationToken.None);

        Assert.False(loaded.IsSuccess);
        Assert.Equal(ControllerErrorCode.InvalidRequest, loaded.Error!.Code);
        Assert.Equal(0, fixture.Storage.ReadCalls);
        Assert.Equal(0, fixture.Storage.WriteCalls);
    }

    private sealed class Fixture
    {
        public Fixture()
        {
            var profile = new ProfileId(Guid.NewGuid());
            var operation = new SyncOperationId(Guid.NewGuid());
            Context = Authorize(profile, operation);
            Scope = new SyncStateScope(profile, new DeviceId(Guid.NewGuid()), 7);
            Store = new ProfileStorageSyncCheckpointStore(Storage);
        }

        public InMemoryProfileStorage Storage { get; } = new();

        public SyncOperationContext Context { get; }

        public SyncStateScope Scope { get; }

        public ProfileStorageSyncCheckpointStore Store { get; }
    }

    private static SyncOperationContext Authorize(ProfileId profile, SyncOperationId operation)
    {
        var browsing = new BrowsingContext(
            new PrivacyContext(profile, new BrowserSessionId(Guid.NewGuid()), BrowserProfileMode.Normal),
            new BrowserWindowId(Guid.NewGuid()),
            new BrowserTabId(Guid.NewGuid()),
            null);
        return SyncOperationContext.Authorize(browsing, operation).Value!;
    }

    private sealed class InMemoryProfileStorage : IProfileStorage
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, ProfileStorageEntry> _entries = [];

        public int ReadCalls { get; private set; }

        public int WriteCalls { get; private set; }

        public ValueTask<ControllerResult<ProfileStorageEntry>> ReadAsync(
            ProfileStorageAddress address,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                ReadCalls++;
                return ValueTask.FromResult(_entries.TryGetValue(Key(address), out var value)
                    ? ProfileStorageEntry.Create(value.Revision, value.Payload.Span)
                    : ControllerResult<ProfileStorageEntry>.Failure(ControllerError.Create(
                        ControllerErrorCode.NotFound,
                        "error.profile_storage.not_found")));
            }
        }

        public ValueTask<ControllerResult<ProfileStorageWriteReceipt>> WriteAsync(
            ProfileStorageWriteRequest request,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                WriteCalls++;
                var key = Key(request.Address);
                _entries.TryGetValue(key, out var current);
                if (request.ExpectedRevision is { } expected && current?.Revision != expected)
                {
                    return ValueTask.FromResult(
                        ControllerResult<ProfileStorageWriteReceipt>.Failure(ControllerError.Create(
                            ControllerErrorCode.Conflict,
                            "error.profile_storage.revision_conflict")));
                }

                var revision = new ProfileStorageRevision(Guid.NewGuid());
                _entries[key] = ProfileStorageEntry.Create(revision, request.Payload.Span).Value!;
                return ValueTask.FromResult(
                    ControllerResult<ProfileStorageWriteReceipt>.Success(
                        new ProfileStorageWriteReceipt(revision)));
            }
        }

        public ValueTask<ControllerResult> DeleteAsync(
            ProfileStorageAddress address,
            ProfileStorageRevision? expectedRevision = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _entries.Remove(Key(address));
                return ValueTask.FromResult(ControllerResult.Success());
            }
        }

        public void CorruptOnlyEntry()
        {
            lock (_gate)
            {
                var pair = Assert.Single(_entries);
                var bytes = pair.Value.Payload.ToArray();
                bytes[^1] ^= 0xff;
                _entries[pair.Key] = ProfileStorageEntry.Create(pair.Value.Revision, bytes).Value!;
            }
        }

        private static string Key(ProfileStorageAddress address) =>
            $"{address.Context.ProfileId.Value:N}:{address.Namespace.Value}:{address.Key.Value}";
    }
}
