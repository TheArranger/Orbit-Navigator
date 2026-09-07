using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Foundation.Browser;
using OrbitNavigator.Foundation.Profiles;
using Xunit;

namespace OrbitNavigator.Foundation.Tests.Browser;

public sealed class WorkspaceUiPreferencesStoreTests
{
    [Fact]
    public async Task PreferencesAreDurableRevisionedAndProfileLocal()
    {
        using var temp = new TempDirectory();
        var storage = new FileProfileStorage(temp.Path);
        var store = new WorkspaceUiPreferencesStore(storage);
        var context = Context(BrowserProfileMode.Normal);

        var initial = await store.LoadAsync(context);
        Assert.False(initial.Value!.CompactTabs);
        var saved = await store.SaveAsync(new(
            context,
            initial.Value.Revision,
            TabStripPlacement.Left,
            true,
            false,
            true,
            WorkspaceNewTabMode.Basic,
            WorkspacePreviewMode.Click,
            WorkspaceAffiliatedRailPlacement.Right,
            false,
            312));
        var reloaded = await new WorkspaceUiPreferencesStore(storage).LoadAsync(context);
        var stale = await store.SaveAsync(new(
            context,
            initial.Value.Revision,
            TabStripPlacement.Right,
            false,
            true,
            false));

        Assert.True(saved.IsSuccess);
        Assert.False(saved.Value!.Revision.IsEmpty);
        Assert.Equal(TabStripPlacement.Left, reloaded.Value!.TabStripPlacement);
        Assert.True(reloaded.Value.CollapseToActive);
        Assert.False(reloaded.Value.ShowOrbitalGroupPreview);
        Assert.True(reloaded.Value.CompactTabs);
        Assert.Equal(WorkspaceNewTabMode.Basic, reloaded.Value.NewTabMode);
        Assert.Equal(WorkspacePreviewMode.Click, reloaded.Value.PreviewMode);
        Assert.Equal(WorkspaceAffiliatedRailPlacement.Right, reloaded.Value.AffiliatedRailPlacement);
        Assert.False(reloaded.Value.ShowAffiliatedRail);
        Assert.Equal(312, reloaded.Value.SideTabPanelWidth);
        Assert.False(stale.IsSuccess);
        Assert.Equal(ControllerErrorCode.Conflict, stale.Error?.Code);
    }

    [Fact]
    public async Task PrivateWindowReadsNormalPreferenceButCannotPersistChanges()
    {
        using var temp = new TempDirectory();
        var storage = new FileProfileStorage(temp.Path);
        var store = new WorkspaceUiPreferencesStore(storage);
        var profile = new ProfileId(Guid.NewGuid());
        var normal = Context(BrowserProfileMode.Normal, profile);
        var privateContext = Context(BrowserProfileMode.Private, profile);
        var saved = await store.SaveAsync(new(
            normal,
            default,
            TabStripPlacement.Right,
            true,
            true,
            true,
            SideTabPanelWidth: 288));

        var privateRead = await store.LoadAsync(privateContext);
        var privateWrite = await store.SaveAsync(new(
            privateContext,
            privateRead.Value!.Revision,
            TabStripPlacement.Left,
            false,
            false,
            false,
            SideTabPanelWidth: 240));
        var normalRead = await store.LoadAsync(normal);

        Assert.True(saved.IsSuccess);
        Assert.Equal(TabStripPlacement.Right, privateRead.Value.TabStripPlacement);
        Assert.True(privateRead.Value.CompactTabs);
        Assert.False(privateWrite.IsSuccess);
        Assert.Equal(ControllerErrorCode.PolicyDenied, privateWrite.Error?.Code);
        Assert.Equal(TabStripPlacement.Right, normalRead.Value!.TabStripPlacement);
        Assert.True(normalRead.Value.CompactTabs);
        Assert.Equal(288, privateRead.Value.SideTabPanelWidth);
        Assert.Equal(288, normalRead.Value.SideTabPanelWidth);
        Assert.Equal(saved.Value!.Revision, normalRead.Value.Revision);
    }

    private static PrivacyContext Context(BrowserProfileMode mode, ProfileId? profile = null) =>
        new(
            profile ?? new ProfileId(Guid.NewGuid()),
            new BrowserSessionId(Guid.NewGuid()),
            mode);
}
