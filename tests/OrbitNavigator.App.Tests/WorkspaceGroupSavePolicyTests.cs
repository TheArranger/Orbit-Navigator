using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Foundation.Browser;
using OrbitNavigator.Presentation.Workspace;
using Xunit;

namespace OrbitNavigator.App.Tests;

public sealed class WorkspaceGroupSavePolicyTests
{
    [Fact]
    public void AcceptsOnlyTemporaryGroupInCanonicalTabOrder()
    {
        var profile = new ProfileId(Guid.NewGuid());
        var groupId = new BrowserTabGroupId(Guid.NewGuid());
        var first = Tab(groupId, "https://my-orbit.snap-it.cc/");
        var second = Tab(groupId, "https://beacon-spire.dps-games.cc/");
        var snapshot = Snapshot(profile, groupId, true, first, second);
        var draft = Draft(first.Address!, second.Address!);

        Assert.True(WorkspaceGroupSavePolicy.TryValidate(snapshot, groupId, draft, out _));
        Assert.False(WorkspaceGroupSavePolicy.TryValidate(
            snapshot, groupId, Draft(second.Address!, first.Address!), out _));
        Assert.False(WorkspaceGroupSavePolicy.TryValidate(
            Snapshot(profile, groupId, false, first, second), groupId, draft, out _));
    }

    [Fact]
    public void RejectsSiteArtworkOutsideAuthoritativeGroup()
    {
        var profile = new ProfileId(Guid.NewGuid());
        var groupId = new BrowserTabGroupId(Guid.NewGuid());
        var tab = Tab(groupId, "https://my-orbit.snap-it.cc/");
        var draft = Draft(tab.Address!) with
        {
            Artwork = new WorkspaceArtworkPresentation(
                WorkspaceArtworkKind.Site,
                new Uri("https://unrelated.example/"),
                null,
                "Unrelated"),
        };

        Assert.False(WorkspaceGroupSavePolicy.TryValidate(
            Snapshot(profile, groupId, true, tab), groupId, draft, out _));
    }

    private static BrowserTabState Tab(BrowserTabGroupId groupId, string address) => new(
        new BrowserTabId(Guid.NewGuid()),
        groupId,
        new Uri(address),
        new Uri(address).Host,
        BrowserLoadState.Idle,
        false,
        false,
        false);

    private static BrowserWorkspaceSnapshot Snapshot(
        ProfileId profile,
        BrowserTabGroupId groupId,
        bool temporary,
        params BrowserTabState[] tabs)
    {
        var window = new BrowserWindowId(Guid.NewGuid());
        return new(
            new PrivacyContext(profile, new BrowserSessionId(Guid.NewGuid()), BrowserProfileMode.Normal),
            window,
            new BrowserWorkspaceRevision(1),
            new BrowserState(window, tabs[0].TabId, tabs),
            [new WorkspaceTabGroupState(groupId, "Group", false) { IsTemporary = temporary }]);
    }

    private static WorkspacePresetDraftPresentation Draft(params Uri[] targets) => new(
        "Workspace",
        "Group",
        targets.Select(target => new WorkspacePresetTabPresentation(target, target.Host)).ToArray());
}
