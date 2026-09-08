using OrbitNavigator.App.Sync;
using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Sync;
using OrbitNavigator.Foundation.Browser;
using Xunit;

namespace OrbitNavigator.App.Tests.Sync;

public sealed class BrowserDataSyncRecordProjectorTests
{
    [Fact]
    public async Task HistoryProjectionMapsOnlyRequestedProfileBoundEntity()
    {
        var browsing = Browsing();
        var entity = new SyncEntityId(Guid.NewGuid());
        var visited = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
        var facade = new FakeHistoryFacade([
            new HistoryEntry(
                new HistoryVisitId(browsing.Privacy.ProfileId, entity.Value),
                new Uri("https://my-orbit.snap-it.cc/settings"),
                "My Orbit",
                visited,
                3),
        ]);
        var projector = new BrowserDataSyncRecordProjector(facade, () => Workspace(browsing));

        var result = await projector.ProjectAsync(
            Context(browsing),
            SyncDataCategory.History,
            entity,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var record = Assert.IsType<HistorySyncRecord>(result.Value);
        Assert.Equal(entity, record.EntityId);
        Assert.Equal("https://my-orbit.snap-it.cc/settings", record.AbsoluteUrl);
        Assert.Equal("My Orbit", record.Title);
        Assert.Equal(3, record.VisitCount);
        Assert.Equal(visited.UtcTicks, record.Revision);
    }

    [Fact]
    public async Task OpenTabProjectionPreservesCanonicalPositionAndGroupLabel()
    {
        var browsing = Browsing();
        var groupId = new BrowserTabGroupId(Guid.NewGuid());
        var requested = new BrowserTabState(
            new BrowserTabId(Guid.NewGuid()),
            groupId,
            new Uri("https://beacon-spire.dps-games.cc/"),
            "Beacon Spire",
            BrowserLoadState.Idle,
            false,
            false,
            false);
        var first = Tab();
        var snapshot = Workspace(browsing, [first, requested], [
            new WorkspaceTabGroupState(groupId, "Research", false),
        ]);
        var projector = new BrowserDataSyncRecordProjector(
            new FakeHistoryFacade([]),
            () => snapshot);

        var result = await projector.ProjectAsync(
            Context(browsing),
            SyncDataCategory.OpenTabs,
            new SyncEntityId(requested.TabId.Value),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var record = Assert.IsType<OpenTabSyncRecord>(result.Value);
        Assert.Equal(1, record.Position);
        Assert.Equal("Research", record.GroupLabel);
        Assert.Equal("Beacon Spire", record.Title);
        Assert.Equal(snapshot.Revision.Value, record.Revision);
    }

    [Fact]
    public async Task InternalOrUnknownTabIsNotProjected()
    {
        var browsing = Browsing();
        var internalTab = Tab() with { Address = null };
        var snapshot = Workspace(browsing, [internalTab]);
        var projector = new BrowserDataSyncRecordProjector(
            new FakeHistoryFacade([]),
            () => snapshot);

        var result = await projector.ProjectAsync(
            Context(browsing),
            SyncDataCategory.OpenTabs,
            new SyncEntityId(internalTab.TabId.Value),
            CancellationToken.None);

        Assert.Equal(ControllerErrorCode.NotFound, result.Error?.Code);
    }

    [Fact]
    public async Task SnapshotFromDifferentProfileFailsClosed()
    {
        var browsing = Browsing();
        var different = Browsing();
        var tab = Tab();
        var projector = new BrowserDataSyncRecordProjector(
            new FakeHistoryFacade([]),
            () => Workspace(different, [tab]));

        var result = await projector.ProjectAsync(
            Context(browsing),
            SyncDataCategory.OpenTabs,
            new SyncEntityId(tab.TabId.Value),
            CancellationToken.None);

        Assert.Equal(ControllerErrorCode.IntegrityFailure, result.Error?.Code);
    }

    [Fact]
    public async Task SettingsProjectionIsNotEnabled()
    {
        var browsing = Browsing();
        var projector = new BrowserDataSyncRecordProjector(
            new FakeHistoryFacade([]),
            () => Workspace(browsing));

        var result = await projector.ProjectAsync(
            Context(browsing),
            SyncDataCategory.Settings,
            new SyncEntityId(Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(ControllerErrorCode.InvalidRequest, result.Error?.Code);
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
        SyncOperationContext.Authorize(
            browsing,
            new SyncOperationId(Guid.NewGuid())).Value!;

    private static BrowserTabState Tab() => new(
        new BrowserTabId(Guid.NewGuid()),
        null,
        new Uri("https://example.test/"),
        "Example",
        BrowserLoadState.Idle,
        false,
        false,
        false);

    private static BrowserWorkspaceSnapshot Workspace(
        BrowsingContext browsing,
        IReadOnlyList<BrowserTabState>? tabs = null,
        IReadOnlyList<WorkspaceTabGroupState>? groups = null)
    {
        tabs ??= [Tab()];
        return new(
            browsing.Privacy,
            browsing.WindowId,
            new BrowserWorkspaceRevision(7),
            new BrowserState(browsing.WindowId, tabs[0].TabId, tabs),
            groups ?? []);
    }

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
