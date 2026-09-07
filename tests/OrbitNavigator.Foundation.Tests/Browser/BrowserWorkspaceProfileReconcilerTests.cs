using System.Text.Json;
using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Foundation.Browser;
using OrbitNavigator.Foundation.Profiles;
using Xunit;

namespace OrbitNavigator.Foundation.Tests.Browser;

public sealed class BrowserWorkspaceProfileReconcilerTests
{
    [Fact]
    public void RemovesEmptyAndOrphanedGroupsWhilePreservingLiveCanonicalOrder()
    {
        var profile = new ProfileId(Guid.NewGuid());
        var window = new BrowserWindowId(Guid.NewGuid());
        var first = new BrowserTabId(Guid.NewGuid());
        var second = new BrowserTabId(Guid.NewGuid());
        var missing = new BrowserTabId(Guid.NewGuid());
        var liveGroup = new BrowserTabGroupId(Guid.NewGuid());
        var emptyGroup = new BrowserTabGroupId(Guid.NewGuid());
        var orphanedReference = new BrowserTabGroupId(Guid.NewGuid());
        var snapshot = new BrowserWorkspaceSessionSnapshot(
            profile,
            new BrowserWorkspaceSessionRevision(Guid.NewGuid()),
            window,
            missing,
            [
                new(first, new Uri("https://example.com/"), " First ", liveGroup),
                new(second, null, "", orphanedReference),
            ],
            [
                new(liveGroup, "Live", true, "Violet", false, [missing, first]),
                new(emptyGroup, "Empty", false, "Gold", true, [missing]),
            ]);

        var result = BrowserWorkspaceProfileReconciler.Reconcile(snapshot);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.WasRepaired);
        Assert.Equal(first, result.Value.Snapshot.SelectedTabId);
        Assert.Equal("First", result.Value.Snapshot.Tabs[0].Title);
        Assert.Equal("New Tab", result.Value.Snapshot.Tabs[1].Title);
        Assert.Null(result.Value.Snapshot.Tabs[1].GroupId);
        var group = Assert.Single(result.Value.Snapshot.Groups);
        Assert.Equal(liveGroup, group.GroupId);
        Assert.Equal([first], group.TabOrder);
        Assert.Contains("workspace.group.empty_removed", result.Value.RepairReasons);
        Assert.Contains("workspace.tab.group_reference_reconciled", result.Value.RepairReasons);
    }

    [Fact]
    public async Task SessionLoadRepairsUpgradedShapeAndWritesExactNewRevision()
    {
        using var temp = new TempDirectory();
        var storage = new FileProfileStorage(temp.Path);
        var context = Context();
        var window = new BrowserWindowId(Guid.NewGuid());
        var live = new BrowserTabId(Guid.NewGuid());
        var missing = new BrowserTabId(Guid.NewGuid());
        var group = new BrowserTabGroupId(Guid.NewGuid());
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            WindowId = window,
            SelectedTabId = missing,
            Tabs = new[] { new BrowserWorkspaceSessionTab(live, null, "", group) },
            Groups = new[]
            {
                new BrowserWorkspaceSessionGroup(group, "New group", true, "Scarlet", true, [missing, live]),
                new BrowserWorkspaceSessionGroup(new BrowserTabGroupId(Guid.NewGuid()), "Test", true, "SeaGlass", true, [missing]),
            },
        });
        var address = SessionAddress(context);
        var seeded = await storage.WriteAsync(ProfileStorageWriteRequest.Create(address, payload).Value!);

        var loaded = await new BrowserWorkspaceSessionStore(storage).LoadAsync(context);
        var after = await storage.ReadAsync(address);

        Assert.True(loaded.IsSuccess);
        Assert.Equal(live, loaded.Value!.SelectedTabId);
        Assert.Equal("New Tab", Assert.Single(loaded.Value.Tabs).Title);
        Assert.Equal([live], Assert.Single(loaded.Value.Groups).TabOrder);
        Assert.NotEqual(seeded.Value!.Revision, after.Value!.Revision);
        Assert.Equal(after.Value.Revision.Value, loaded.Value.Revision.Value);
    }

    [Fact]
    public async Task SixteenTabsRemainAuthoritativeAndRestoreFromOneSessionRevisionChain()
    {
        using var temp = new TempDirectory();
        var storage = new FileProfileStorage(temp.Path);
        var context = Context();
        var window = new BrowserWindowId(Guid.NewGuid());
        var first = Tab("Tab 1");
        var sessions = new BrowserWorkspaceSessionStore(storage);
        await using var coordinator = (await BrowserWorkspaceCoordinator.CreateAsync(
            context,
            window,
            new BrowserState(window, first.TabId, [first]),
            new TabGroupMetadataStore(storage),
            sessions)).Value!;

        for (var index = 2; index <= 16; index++)
        {
            var tab = Tab($"Tab {index}");
            var result = await coordinator.ExecuteAsync(new AddWorkspaceTabAction(
                window,
                coordinator.Current.Revision,
                tab,
                true));
            Assert.True(result.IsSuccess);
            Assert.Equal(index, coordinator.Current.Browser.Tabs.Count);
            Assert.Equal(index, coordinator.Current.Revision.Value);
        }

        var restored = await sessions.LoadAsync(context);
        Assert.True(restored.IsSuccess);
        Assert.Equal(16, restored.Value!.Tabs.Count);
        Assert.Equal(coordinator.Current.Browser.SelectedTabId, restored.Value.SelectedTabId);
        Assert.Equal(coordinator.Current.Browser.Tabs.Select(tab => tab.TabId),
            restored.Value.Tabs.Select(tab => tab.TabId));
    }

    [Fact]
    public async Task WorkspaceOpenCommitsTabsCollapsedGroupAndSelectionInOneRevision()
    {
        using var temp = new TempDirectory();
        var storage = new FileProfileStorage(temp.Path);
        var context = Context();
        var window = new BrowserWindowId(Guid.NewGuid());
        var existing = Tab("Existing");
        var sessions = new BrowserWorkspaceSessionStore(storage);
        await using var coordinator = (await BrowserWorkspaceCoordinator.CreateAsync(
            context,
            window,
            new BrowserState(window, existing.TabId, [existing]),
            new TabGroupMetadataStore(storage),
            sessions)).Value!;
        var groupId = new BrowserTabGroupId(Guid.NewGuid());
        var first = Tab("Workspace one") with { Address = new Uri("https://example.com/one") };
        var second = Tab("Workspace two") with { Address = new Uri("https://example.com/two") };
        var beforeRevision = coordinator.Current.Revision;

        var opened = await coordinator.ExecuteAsync(new OpenWorkspaceGroupAction(
            window,
            beforeRevision,
            [first, second],
            groupId,
            "Research",
            "Violet",
            second.TabId,
            InitiallyCollapsed: true,
            IsTemporary: false));

        Assert.True(opened.IsSuccess);
        Assert.Equal(beforeRevision.Value + 1, opened.Value!.Snapshot.Revision.Value);
        Assert.Equal([existing.TabId, first.TabId, second.TabId],
            opened.Value.Snapshot.Browser.Tabs.Select(tab => tab.TabId));
        Assert.Equal(second.TabId, opened.Value.Snapshot.Browser.SelectedTabId);
        Assert.Equal([groupId, groupId], opened.Value.Snapshot.Browser.Tabs.Skip(1).Select(tab => tab.GroupId));
        var group = Assert.Single(opened.Value.Snapshot.Groups);
        Assert.True(group.IsCollapsed);
        Assert.False(group.IsTemporary);
        Assert.Equal("Violet", group.ColorToken);

        var persisted = await sessions.LoadAsync(context);
        Assert.True(persisted.IsSuccess);
        Assert.Equal(3, persisted.Value!.Tabs.Count);
        Assert.Equal([first.TabId, second.TabId], Assert.Single(persisted.Value.Groups).TabOrder);
    }

    [Fact]
    public async Task InvalidAtomicWorkspaceOpenLeavesExistingSessionUnchanged()
    {
        using var temp = new TempDirectory();
        var storage = new FileProfileStorage(temp.Path);
        var context = Context();
        var window = new BrowserWindowId(Guid.NewGuid());
        var existing = Tab("Existing");
        var sessions = new BrowserWorkspaceSessionStore(storage);
        await using var coordinator = (await BrowserWorkspaceCoordinator.CreateAsync(
            context,
            window,
            new BrowserState(window, existing.TabId, [existing]),
            new TabGroupMetadataStore(storage),
            sessions)).Value!;
        var before = coordinator.Current;

        var rejected = await coordinator.ExecuteAsync(new OpenWorkspaceGroupAction(
            window,
            before.Revision,
            [existing],
            new BrowserTabGroupId(Guid.NewGuid()),
            "Collision",
            "Gold",
            existing.TabId,
            true,
            false));
        var persisted = await sessions.LoadAsync(context);

        Assert.False(rejected.IsSuccess);
        Assert.Equal(before.Revision, coordinator.Current.Revision);
        Assert.Equal([existing.TabId], coordinator.Current.Browser.Tabs.Select(tab => tab.TabId));
        Assert.Equal([existing.TabId], persisted.Value!.Tabs.Select(tab => tab.TabId));
        Assert.Empty(persisted.Value.Groups);
    }

    private static BrowserTabState Tab(string title) => new(
        new BrowserTabId(Guid.NewGuid()),
        null,
        null,
        title,
        BrowserLoadState.Idle,
        false,
        false,
        false);

    private static PrivacyContext Context() => new(
        new ProfileId(Guid.NewGuid()),
        new BrowserSessionId(Guid.NewGuid()),
        BrowserProfileMode.Normal);

    private static ProfileStorageAddress SessionAddress(PrivacyContext context) =>
        ProfileStorageAddress.Create(
            context,
            ProfileStorageNamespace.Create("browser.workspace-session").Value,
            ProfileStorageKey.Create("primary").Value,
            ProfileStorageDurability.Persistent).Value!;
}
