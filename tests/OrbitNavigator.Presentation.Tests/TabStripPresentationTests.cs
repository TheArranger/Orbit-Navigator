using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Presentation.Tabs;
using Xunit;

namespace OrbitNavigator.Presentation.Tests;

public sealed class TabStripPresentationTests
{
    [Fact]
    public void CollapsedGroupKeepsHeaderAndSelectedMemberInKeyboardOrder()
    {
        var window = new BrowserWindowId(Guid.NewGuid());
        var groupId = new BrowserTabGroupId(Guid.NewGuid());
        var first = Tab(groupId, "First");
        var selected = Tab(groupId, "Selected");
        var outside = Tab(null, "Outside");
        var state = new BrowserState(window, selected.TabId, [first, selected, outside]);
        var groups = new Dictionary<BrowserTabGroupId, TabGroupPresentation>
        {
            [groupId] = new(groupId, "Research", true),
        };

        var result = TabStripProjector.Project(state, groups);

        var header = Assert.IsType<TabGroupHeaderEntry>(result.Entries[0]);
        Assert.True(header.IsCollapsed);
        Assert.True(header.ContainsSelectedTab);
        Assert.Equal(2, header.TabCount);
        Assert.Equal(selected.TabId, Assert.IsType<BrowserTabEntry>(result.Entries[1]).TabId);
        Assert.Equal(outside.TabId, Assert.IsType<BrowserTabEntry>(result.Entries[2]).TabId);
        Assert.DoesNotContain(result.Entries.OfType<BrowserTabEntry>(), value => value.TabId == first.TabId);
    }

    [Fact]
    public void ContextMenuAndDragCanShareOneDeterministicGroupingPlan()
    {
        var window = new BrowserWindowId(Guid.NewGuid());
        var first = Tab(null, "First");
        var second = Tab(null, "Second");
        var state = new BrowserState(window, first.TabId, [first, second]);
        var catalog = new TabGroupPresentationCatalog();

        var plan = TabGroupingPlanner.Create(
            state,
            [first.TabId, second.TabId],
            catalog,
            "Planning");

        Assert.Equal(2, plan.Commands.Count);
        Assert.All(plan.Commands, command => Assert.Equal(plan.Group.GroupId, command.GroupId));
        Assert.Equal("Planning", plan.Group.Name);
    }

    [Fact]
    public void CollapseToActiveKeepsEveryTabKeyboardReachableButMarksInactiveEntriesCompact()
    {
        var window = new BrowserWindowId(Guid.NewGuid());
        var groupId = new BrowserTabGroupId(Guid.NewGuid());
        var grouped = Tab(groupId, "Grouped");
        var selected = Tab(null, "Selected");
        var inactive = Tab(null, "Inactive");
        var state = new BrowserState(window, selected.TabId, [grouped, selected, inactive]);
        var groups = new Dictionary<BrowserTabGroupId, TabGroupPresentation>
        {
            [groupId] = new(groupId, "Research", false),
        };

        var result = TabStripProjector.Project(state, groups, collapseToActive: true);

        Assert.Equal(3, result.Entries.OfType<BrowserTabEntry>().Count());
        Assert.True(Assert.IsType<TabGroupHeaderEntry>(result.Entries[0]).IsCompact);
        Assert.True(Assert.IsType<BrowserTabEntry>(result.Entries[1]).IsCompact);
        Assert.False(result.Entries.OfType<BrowserTabEntry>().Single(tab => tab.IsSelected).IsCompact);
        Assert.True(result.Entries.OfType<BrowserTabEntry>().Single(tab => tab.TabId == inactive.TabId).IsCompact);
    }

    [Fact]
    public void SmallLiveGroupExpandsInlineButFiveTabGroupUsesCanonicalPreviewDropdown()
    {
        var window = new BrowserWindowId(Guid.NewGuid());
        var groupId = new BrowserTabGroupId(Guid.NewGuid());
        var tabs = Enumerable.Range(1, 5).Select(index => Tab(groupId, $"Site {index}")).ToArray();
        var state = new BrowserState(window, tabs[0].TabId, tabs);
        var groups = new Dictionary<BrowserTabGroupId, TabGroupPresentation>
        {
            [groupId] = new(groupId, "Route", false)
            {
                ColorToken = "Gold",
                IsTemporary = true,
            },
        };

        var large = TabStripProjector.Project(state, groups);
        var header = Assert.IsType<TabGroupHeaderEntry>(large.Entries[0]);
        Assert.True(header.UsesPreviewDropdown);
        Assert.Equal(5, header.TabPreviews.Count);
        Assert.Equal("Gold", header.ColorToken);
        Assert.True(header.IsTemporary);
        Assert.Single(large.Entries.OfType<BrowserTabEntry>());

        var smallState = state with { Tabs = tabs.Take(4).ToArray() };
        var small = TabStripProjector.Project(smallState, groups);
        Assert.False(Assert.IsType<TabGroupHeaderEntry>(small.Entries[0]).UsesPreviewDropdown);
        Assert.Equal(4, small.Entries.OfType<BrowserTabEntry>().Count());
    }

    private static BrowserTabState Tab(BrowserTabGroupId? groupId, string title) =>
        new(
            new BrowserTabId(Guid.NewGuid()),
            groupId,
            new Uri("https://example.test"),
            title,
            BrowserLoadState.Idle,
            false,
            false,
            false);
}
