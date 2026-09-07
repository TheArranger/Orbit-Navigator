using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Presentation.Shell;
using OrbitNavigator.Presentation.Tabs;
using Xunit;

namespace OrbitNavigator.Presentation.Tests;

public sealed class TabCommandPlannerTests
{
    [Fact]
    public void CloseSelectAndMoveUseOnlyTabsFromTheActiveWindow()
    {
        var tab = new BrowserTabId(Guid.NewGuid());
        var state = new BrowserState(
            new BrowserWindowId(Guid.NewGuid()),
            tab,
            [new BrowserTabState(tab, null, null, "One", BrowserLoadState.Idle, false, false, false)]);

        Assert.Equal(tab, TabCommandPlanner.Close(state, tab).TabId);
        Assert.Equal(tab, TabCommandPlanner.Select(state, tab).TabId);
        Assert.Equal(0, TabCommandPlanner.Move(state, tab, 9, null).NewIndex);
        Assert.Throws<ArgumentException>(() => TabCommandPlanner.Close(state, new BrowserTabId(Guid.NewGuid())));
    }

    [Fact]
    public void NewPrivateWindowRequiresANormalBrowsingContext()
    {
        var normal = TestContexts.Browsing();
        var request = PrivateWindowRequestPlanner.Create(normal);

        Assert.Equal(normal.Privacy, request.InitiatingContext);
        Assert.Throws<ArgumentException>(() => PrivateWindowRequestPlanner.Create(TestContexts.Browsing(BrowserProfileMode.Private)));
    }
}
