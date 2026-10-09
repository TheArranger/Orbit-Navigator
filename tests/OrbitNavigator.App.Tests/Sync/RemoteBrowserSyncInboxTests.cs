using OrbitNavigator.App.Sync;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Sync;
using OrbitNavigator.Foundation.Profiles;
using OrbitNavigator.Sync.Integration;
using OrbitNavigator.Sync.State;
using Xunit;

namespace OrbitNavigator.App.Tests.Sync;

public sealed class RemoteBrowserSyncInboxTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "OrbitInboxTests", Guid.NewGuid().ToString("N"));
    private readonly BrowsingContext _browsing = new(
        new(new(Guid.NewGuid()), new(Guid.NewGuid()), BrowserProfileMode.Normal),
        new(Guid.NewGuid()), new(Guid.NewGuid()), null);
    private readonly ProfileId _account = new(Guid.NewGuid());
    private readonly SyncKeysetId _keyset = new(Guid.NewGuid());
    private readonly ClientFence _fence = new(new(Guid.NewGuid()), 0, 0);
    private readonly DeviceId _remote = new(Guid.NewGuid());
    private readonly CountingStorage _storage;

    public RemoteBrowserSyncInboxTests() => _storage = new(new FileProfileStorage(_root));

    [Fact]
    public async Task AtomicPageSurvivesRestartAndMapsAccountWithoutChangingLocalIdentity()
    {
        var target = await Bound();
        var history = History(1);
        var tab = Tab(2);
        var applied = await target.ApplyAsync(_browsing, Operation(), Page("page1", [tab, history]));
        Assert.True(applied.IsSuccess);
        Assert.Equal(2, applied.Value!.AppliedUpsertCount);
        var snapshot = await NewTarget().ReadAsync(_browsing, Operation());
        Assert.True(snapshot.IsSuccess);
        Assert.Equal(_browsing.Privacy.ProfileId, snapshot.Value!.LocalProfileId);
        Assert.Equal(_account, snapshot.Value.AccountProfileId);
        Assert.NotEqual(snapshot.Value.LocalProfileId, snapshot.Value.AccountProfileId);
        Assert.Equal("History page", Assert.Single(snapshot.Value.History).Record.Title);
        Assert.Equal("Open page", Assert.Single(snapshot.Value.OpenTabs).Record.Title);
        Assert.Equal(1, _storage.InboxWrites);
    }

    [Fact]
    public async Task ExactReplayIsNoOpAndMutatedReplayFailsAfterRestart()
    {
        var target = await Bound();
        var history = History(1);
        var page = Page("page1", [history]);
        Assert.True((await target.ApplyAsync(_browsing, Operation(), page)).IsSuccess);
        var replay = await NewTarget().ApplyAsync(_browsing, Operation(), page);
        Assert.True(replay.IsSuccess);
        Assert.Equal(1, replay.Value!.AppliedUpsertCount);
        var changed = history with { Payload = ((HistorySyncRecord)history.Payload) with { Title = "Changed" } };
        Assert.False((await NewTarget().ApplyAsync(_browsing, Operation(), Page("page1", [changed]))).IsSuccess);
        Assert.False((await NewTarget().ApplyAsync(_browsing, Operation(), Page("page2", [changed]))).IsSuccess);
        Assert.Equal(1, _storage.InboxWrites);
    }

    [Fact]
    public async Task InvalidSecondRecordCannotPartiallyApplyFirst()
    {
        var target = await Bound();
        var good = History(1);
        var bad = Tab(2);
        bad = bad with { Aad = bad.Aad with { ProfileId = new(Guid.NewGuid()) } };
        Assert.False((await target.ApplyAsync(_browsing, Operation(), Page("page1", [good, bad]))).IsSuccess);
        Assert.Equal(0, _storage.InboxWrites);
        Assert.Empty((await target.ReadAsync(_browsing, Operation())).Value!.History);
    }

    [Fact]
    public async Task FailedAtomicWriteCanRetryWithoutAdvancingReceiptOrInventory()
    {
        var target = await Bound();
        var page = Page("page1", [History(1), Tab(2)]);
        _storage.FailWrite = true;
        Assert.False((await target.ApplyAsync(_browsing, Operation(), page)).IsSuccess);
        Assert.Empty((await target.ReadAsync(_browsing, Operation())).Value!.OpenTabs);
        _storage.FailWrite = false;
        Assert.True((await target.ApplyAsync(_browsing, Operation(), page)).IsSuccess);
        Assert.Single((await target.ReadAsync(_browsing, Operation())).Value!.OpenTabs);
    }

    [Fact]
    public async Task OutOfOrderNewEnvelopeAndSequenceReuseFailClosed()
    {
        var target = await Bound();
        Assert.True((await target.ApplyAsync(_browsing, Operation(), Page("first", [History(10)]))).IsSuccess);
        Assert.False((await target.ApplyAsync(_browsing, Operation(), Page("stale", [History(9)]))).IsSuccess);
        Assert.False((await target.ApplyAsync(_browsing, Operation(), Page("reuse", [History(10)]))).IsSuccess);
        Assert.Equal(1, _storage.InboxWrites);
    }

    [Fact]
    public async Task TombstoneIsOriginScopedAndDoesNotResurrectOnReplay()
    {
        var target = await Bound();
        var tab = Tab(1);
        var other = tab with { Aad = tab.Aad with { DeviceId = new(Guid.NewGuid()), EnvelopeId = new(Guid.NewGuid()) } };
        var initial = Page("first", [tab, other]);
        Assert.True((await target.ApplyAsync(_browsing, Operation(), initial)).IsSuccess);
        var aad = tab.Aad with { RecordKind = SyncRecordKind.Tombstone, ClientSequence = 2, EnvelopeId = new(Guid.NewGuid()) };
        var receipt = new AuthenticatedSyncTombstoneReceipt(aad.ProfileId, aad.DeviceId, aad.KeysetId, aad.KeyEpoch,
            aad.EnvelopeId, aad.Category, aad.EntityId, aad.ClientGeneration, aad.ClientSequence);
        Assert.True((await target.ApplyAsync(_browsing, Operation(), new(new("second"), _fence, [], [new(aad, receipt)], []))).IsSuccess);
        Assert.True((await target.ApplyAsync(_browsing, Operation(), initial)).IsSuccess);
        var snapshot = (await target.ReadAsync(_browsing, Operation())).Value!;
        Assert.Equal(other.Aad.DeviceId, Assert.Single(snapshot.OpenTabs).DeviceId);
    }

    [Fact]
    public async Task UnboundAndMixedLocalProfileCannotReadOrApply()
    {
        Assert.False((await NewTarget().ApplyAsync(_browsing, Operation(), Page("p", [History(1)]))).IsSuccess);
        var target = await Bound();
        var other = _browsing with { Privacy = _browsing.Privacy with { ProfileId = new(Guid.NewGuid()) } };
        var before = _storage.Calls;
        Assert.False((await target.ReadAsync(other, Operation())).IsSuccess);
        Assert.False((await target.ApplyAsync(other, Operation(), Page("p", [History(1)]))).IsSuccess);
        Assert.Equal(before, _storage.Calls);
    }

    [Fact]
    public async Task PrivateReadAndWriteAreDeniedBeforeAnyStorageCall()
    {
        var target = await Bound();
        var privateContext = _browsing with { Privacy = _browsing.Privacy with { Mode = BrowserProfileMode.Private } };
        var before = _storage.Calls;
        Assert.Equal(ControllerErrorCode.PolicyDenied, (await target.ReadAsync(privateContext, Operation())).Error!.Code);
        Assert.Equal(ControllerErrorCode.PolicyDenied, (await target.ApplyAsync(privateContext, Operation(), Page("p", [History(1)]))).Error!.Code);
        Assert.Equal(before, _storage.Calls);
    }

    [Fact]
    public async Task WrongFenceKeysetAndCredentialBearingUrlAreRejected()
    {
        var target = await Bound();
        var history = History(1);
        Assert.False((await target.ApplyAsync(_browsing, Operation(), Page("p", [history]) with { Fence = new(new(Guid.NewGuid()), 0, 0) })).IsSuccess);
        Assert.False((await target.ApplyAsync(_browsing, Operation(), Page("p", [history with { Aad = history.Aad with { KeysetId = new(Guid.NewGuid()) } }]))).IsSuccess);
        Assert.False((await target.ApplyAsync(_browsing, Operation(), Page("p", [history with { Payload = ((HistorySyncRecord)history.Payload) with { AbsoluteUrl = "https://user:secret@example.test/" } }]))).IsSuccess);
        Assert.Equal(0, _storage.InboxWrites);
    }

    [Fact]
    public async Task ConcurrentInstancesDoNotLoseFirstCommit()
    {
        var first = await Bound();
        var second = NewTarget();
        var a = History(1);
        var b = History(1);
        b = b with { Aad = b.Aad with { DeviceId = new(Guid.NewGuid()) } };
        var results = await Task.WhenAll(first.ApplyAsync(_browsing, Operation(), Page("a", [a])).AsTask(),
            second.ApplyAsync(_browsing, Operation(), Page("b", [b])).AsTask());
        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Equal(2, (await first.ReadAsync(_browsing, Operation())).Value!.History.Count);
    }

    private async Task<RemoteBrowserSyncInbox> Bound()
    {
        Assert.True((await new LocalSyncProfileBindingStore(_storage).BindAsync(_browsing, Operation(),
            _account, LocalSyncProfileBindingKind.JoinedAccount)).IsSuccess);
        return NewTarget();
    }
    private RemoteBrowserSyncInbox NewTarget() => new(_storage, _browsing.Privacy.ProfileId, _keyset, 0, _fence);
    private AuthenticatedSyncPage Page(string cursor, IReadOnlyList<AuthenticatedRemoteUpsert> upserts) => new(new(cursor), _fence, upserts, [], []);
    private AuthenticatedRemoteUpsert History(long sequence)
    {
        var aad = Aad(SyncDataCategory.History, sequence);
        return new(aad, new HistorySyncRecord(aad.EntityId, sequence, DateTimeOffset.UnixEpoch,
            "https://example.test/history", "History page", DateTimeOffset.UnixEpoch, 1));
    }
    private AuthenticatedRemoteUpsert Tab(long sequence)
    {
        var aad = Aad(SyncDataCategory.OpenTabs, sequence);
        return new(aad, new OpenTabSyncRecord(aad.EntityId, sequence, DateTimeOffset.UnixEpoch,
            "https://example.test/tab", "Open page", 0, "Research"));
    }
    private CanonicalSyncAad Aad(SyncDataCategory category, long sequence) => new(1, 1, _account, _remote,
        _keyset, 0, SyncRecordKind.Upsert, new(Guid.NewGuid()), category, new(Guid.NewGuid()), null, 0, sequence);
    private static SyncOperationId Operation() => new(Guid.NewGuid());
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private sealed class CountingStorage(IProfileStorage inner) : IProfileStorage
    {
        public int Calls { get; private set; }
        public int InboxWrites { get; private set; }
        public bool FailWrite { get; set; }
        public ValueTask<ControllerResult<ProfileStorageEntry>> ReadAsync(ProfileStorageAddress address, CancellationToken ct = default)
        { Calls++; return inner.ReadAsync(address, ct); }
        public ValueTask<ControllerResult<ProfileStorageWriteReceipt>> WriteAsync(ProfileStorageWriteRequest request, CancellationToken ct = default)
        {
            Calls++;
            if (FailWrite) return ValueTask.FromResult(ControllerResult<ProfileStorageWriteReceipt>.Failure(
                ControllerError.Create(ControllerErrorCode.Unavailable, "test.unavailable")));
            if (request.Address.Namespace.Value == "sync.remote-browser") InboxWrites++;
            return inner.WriteAsync(request, ct);
        }
        public ValueTask<ControllerResult> DeleteAsync(ProfileStorageAddress address, ProfileStorageRevision? expectedRevision = null, CancellationToken ct = default)
        { Calls++; return inner.DeleteAsync(address, expectedRevision, ct); }
    }
}
