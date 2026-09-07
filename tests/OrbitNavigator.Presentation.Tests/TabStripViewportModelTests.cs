using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Presentation.Tabs;
using OrbitNavigator.Presentation.Workspace;
using Xunit;

namespace OrbitNavigator.Presentation.Tests;

public sealed class TabStripViewportModelTests
{
    [Theory]
    [InlineData(TabStripPlacement.Top, false)]
    [InlineData(TabStripPlacement.Left, false)]
    [InlineData(TabStripPlacement.Right, false)]
    [InlineData(TabStripPlacement.Top, true)]
    public void ManyLongTitlesKeepSelectedTabInOneContiguousViewport(
        TabStripPlacement placement,
        bool detached)
    {
        var tabs = Enumerable.Range(0, 30)
            .Select(index => Entry(index, new string((char)('A' + index % 26), 200)))
            .Cast<TabStripEntry>()
            .ToArray();
        var selected = ((BrowserTabEntry)tabs[22]).TabId;

        var result = TabStripViewportModel.Project(
            tabs,
            selected,
            availableExtent: placement == TabStripPlacement.Top && !detached ? 640 : 420,
            placement,
            detached);

        Assert.Contains(result.VisibleEntries, value => value is BrowserTabEntry tab && tab.TabId == selected);
        Assert.Equal(result.StartIndex, result.HiddenBefore);
        Assert.Equal(tabs.Length - result.StartIndex - result.Count, result.HiddenAfter);
        Assert.True(result.Count > 0);
        Assert.True(result.HiddenCount > 0);
    }

    [Fact]
    public void KeyboardFocusWinsAnchorAndProjectionDoesNotReorderCanonicalEntries()
    {
        var tabs = Enumerable.Range(0, 10).Select(index => Entry(index, $"Tab {index}")).Cast<TabStripEntry>().ToArray();
        var selected = ((BrowserTabEntry)tabs[1]).TabId;
        var focused = ((BrowserTabEntry)tabs[8]).TabId;

        var result = TabStripViewportModel.Project(
            tabs,
            selected,
            focused,
            410,
            TabStripPlacement.Top,
            detached: false);

        Assert.Contains(result.VisibleEntries, value => value is BrowserTabEntry tab && tab.TabId == focused);
        Assert.Equal(tabs.Skip(result.StartIndex).Take(result.Count), result.VisibleEntries);
        Assert.Equal("Tab 0", ((BrowserTabEntry)tabs[0]).Title);
    }

    [Fact]
    public void NarrowFloorStillShowsOneSelectedTab()
    {
        var tabs = Enumerable.Range(0, 8).Select(index => Entry(index, $"Tab {index}")).Cast<TabStripEntry>().ToArray();
        var selected = ((BrowserTabEntry)tabs[5]).TabId;

        var result = TabStripViewportModel.Project(tabs, selected, 24, TabStripPlacement.Top, detached: false);

        var only = Assert.Single(result.VisibleEntries);
        Assert.Equal(selected, Assert.IsType<BrowserTabEntry>(only).TabId);
    }

    [Fact]
    public void TopTabsCompressGraduallyBeforeUsingHonestOverflow()
    {
        var tabs = Enumerable.Range(0, 10).Select(index => Entry(index, $"Long page title {index}"))
            .Cast<TabStripEntry>().ToArray();
        var selected = ((BrowserTabEntry)tabs[4]).TabId;

        var roomy = TabStripViewportModel.Project(
            tabs, selected, null, 1840, TabStripPlacement.Top, detached: false, compactMode: false);
        var compressed = TabStripViewportModel.Project(
            tabs, selected, null, 1400, TabStripPlacement.Top, detached: false, compactMode: false);
        var overflow = TabStripViewportModel.Project(
            tabs, selected, null, 760, TabStripPlacement.Top, detached: false, compactMode: false);
        var compact = TabStripViewportModel.Project(
            tabs, selected, null, 900, TabStripPlacement.Top, detached: false, compactMode: true);

        Assert.Equal(TabStripViewportModel.PreferredTopTabExtent, roomy.EntryExtent);
        Assert.InRange(compressed.EntryExtent, TabStripViewportModel.SafeTopTabExtent, roomy.EntryExtent - 1);
        Assert.True(overflow.HiddenCount > 0);
        Assert.Equal(TabStripViewportModel.SafeTopTabExtent, overflow.EntryExtent);
        Assert.InRange(compact.EntryExtent, TabStripViewportModel.CompactTopTabExtent, 90);
        Assert.True(compact.Count > overflow.Count);
        Assert.Contains(overflow.VisibleEntries, entry => entry is BrowserTabEntry tab && tab.TabId == selected);
    }

    private static BrowserTabEntry Entry(int index, string title) => new(
        new BrowserTabId(Guid.Parse($"00000000-0000-0000-0000-{index + 1:D12}")),
        null,
        title,
        new Uri($"https://example{index}.test/"),
        BrowserLoadState.Idle,
        false,
        false,
        false,
        false);
}
