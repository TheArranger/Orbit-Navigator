using OrbitNavigator.App.Sync;
using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Sync;
using OrbitNavigator.Foundation.Browser;
using Xunit;

namespace OrbitNavigator.App.Tests.Sync;

public sealed class BrowserDataSyncInventoryProviderTests
{
    [Fact]
    public async Task InventoryContainsOnlyNormalHttpHistoryAndTabsWithMonotonicRevisions()
    {
        var browsing = Browsing();
        var historyId = new HistoryVisitId(browsing.Privacy.ProfileId, Guid.NewGuid());
        var visited = new DateTimeOffset(2026, 9, 7, 13, 0, 0, TimeSpan.Zero);
        var webTab = Tab(new Uri("https://my-orbit.snap-it.cc/"));
        var internalTab = Tab(null);
        var workspace = Workspace(browsing, [webTab, internalTab], revision: 11);
        var provider = new BrowserDataSyncInventoryProvider(
            new FakeHistoryFacade([
                new HistoryEntry(
                    historyId,
                    new Uri("https://beacon-spire.dps-games.cc/"),
                    "Beacon Spire",
                    visited,
                    1),
            ]),
            () => workspace);

        var result = await provider.BuildAsync(Context(browsing), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Collection(
            result.Value!.OrderBy(entry => entry.Category),
            history =>
            {
                Assert.Equal(SyncDataCategory.History, history.Category);
                Assert.Equal(historyId.Value, history.EntityId.Value);
                Assert.Equal(visited.UtcTicks, history.Revision);
            },
            tab =>
            {
                Assert.Equal(SyncDataCategory.OpenTabs, tab.Category);
                Assert.Equal(webTab.TabId.Value, tab.EntityId.Value);
                Assert.Equal(11, tab.Revision);
            });
    }

    [Fact]
    public async Task MixedProfileWorkspaceFailsClosed()
    {
        var browsing = Browsing();
        var provider = new BrowserDataSyncInventoryProvider(
            new FakeHistoryFacade([]),
            () => Workspace(Browsing(), [Tab(new Uri("https://example.test/"))]));

        var result = await provider.BuildAsync(Context(browsing), CancellationToken.None);

        Assert.Equal(ControllerErrorCode.IntegrityFailure, result.Error?.Code);
    }

    [Fact]
    public async Task HistoryEntityFromDifferentProfileFailsClosed()
    {
        var browsing = Browsing();
        var provider = new BrowserDataSyncInventoryProvider(
            new FakeHistoryFacade([
                new HistoryEntry(
                    new HistoryVisitId(new ProfileId(Guid.NewGuid()), Guid.NewGuid()),
                    new Uri("https://example.test/"),
                    "Example",
                    DateTimeOffset.UtcNow,
                    1),
            ]),
            () => Workspace(browsing, [Tab(new Uri("https://example.test/"))]));

        var result = await provider.BuildAsync(Context(browsing), CancellationToken.None);

        Assert.Equal(ControllerErrorCode.IntegrityFailure, result.Error?.Code);
    }

    private static BrowsingContext Browsing() => new(
        new PrivacyContext(
            new ProfileId(Guid.NewGuid()),
            new BrowserSessionId(Guid.NewGuid()),
            BrowserProfileMode.Normal),
        new BrowserWindowId(Guid.NewGuid()),
        new BrowserTabId(Guid.NewGuid()),
        null);

    private static SyncOperationContext Context(BrowsingContext browsing) =>
        SyncOperationContext.Authorize(browsing, new SyncOperationId(Guid.NewGuid())).Value!;

    private static BrowserTabState Tab(Uri? address) => new(
        new BrowserTabId(Guid.NewGuid()),
        null,
        address,
        address is null ? "New Tab" : "Example",
        BrowserLoadState.Idle,
        false,
        false,
        false);

    private static BrowserWorkspaceSnapshot Workspace(
        BrowsingContext browsing,
        IReadOnlyList<BrowserTabState> tabs,
        long revision = 1) =>
        new(
            browsing.Privacy,
            browsing.WindowId,
            new BrowserWorkspaceRevision(revision),
            new BrowserState(browsing.WindowId, tabs[0].TabId, tabs),
            []);

    private sealed class FakeHistoryFacade(IReadOnlyList<HistoryEntry> entries) : IHistoryFacade
    {
        public ValueTask<ControllerResult<IReadOnlyList<HistoryEntry>>> QueryAsync(
            HistoryQuery query,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                ControllerResult<IReadOnlyList<HistoryEntry>>.Success(entries));

        public ValueTask<ControllerResult<HistoryEntry>> RecordVisitAsync(
            RecordHistoryVisitIntent intent,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<ControllerResult> ClearAsync(
            ClearHistoryIntent intent,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
