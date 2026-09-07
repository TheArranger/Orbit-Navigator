using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Presentation.Accessibility;
using OrbitNavigator.Presentation.Tabs;
using Xunit;

namespace OrbitNavigator.Presentation.Tests;

public sealed class TabFocusRestorationPlannerTests
{
    [Fact]
    public void ClosingFocusedTabPrefersTheHostSelectedVisibleTab()
    {
        var closed = Tab("Closed");
        var selected = Tab("Selected");
        var before = Strip(closed.TabId, closed, selected);
        var after = Strip(selected.TabId, selected);

        var target = TabFocusRestorationPlanner.AfterTabClosed(before, after, closed.TabId);

        Assert.Equal(TabStripFocusTargetKind.Tab, target.Kind);
        Assert.Equal(selected.TabId, target.TabId);
    }

    [Fact]
    public void ClosingLastTabFallsBackToTheTabStrip()
    {
        var closed = Tab("Only tab");
        var before = Strip(closed.TabId, closed);
        var after = new TabStripViewState(before.WindowId, null, Array.Empty<TabStripEntry>());

        var target = TabFocusRestorationPlanner.AfterTabClosed(before, after, closed.TabId);

        Assert.Equal(TabStripFocusTargetKind.TabStrip, target.Kind);
        Assert.Null(target.TabId);
    }

    [Fact]
    public void CollapsingAGroupAlwaysReturnsFocusToItsHeader()
    {
        var groupId = new BrowserTabGroupId(Guid.NewGuid());

        var target = TabFocusRestorationPlanner.AfterGroupCollapsed(groupId);

        Assert.Equal(TabStripFocusTargetKind.GroupHeader, target.Kind);
        Assert.Equal(groupId, target.GroupId);
    }

    private static TabStripViewState Strip(BrowserTabId? selected, params BrowserTabEntry[] tabs) =>
        new(new BrowserWindowId(Guid.NewGuid()), selected, tabs);

    private static BrowserTabEntry Tab(string title) =>
        new(
            new BrowserTabId(Guid.NewGuid()),
            null,
            title,
            new Uri("https://example.test"),
            BrowserLoadState.Idle,
            false,
            false,
            false,
            false);
}
