using System.Collections.ObjectModel;

using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.Presentation.Tabs;

public sealed record TabGroupPresentation(
    BrowserTabGroupId GroupId,
    string Name,
    bool IsCollapsed)
{
    public string ColorToken { get; init; } = "SeaGlass";
    public bool IsTemporary { get; init; } = true;
}

public abstract record TabStripEntry;

public sealed record TabGroupPreviewItemPresentation(
    BrowserTabId TabId,
    string Title,
    Uri? Address,
    ReadOnlyMemory<byte> FaviconPng);

public sealed record TabGroupHeaderEntry(
    BrowserTabGroupId GroupId,
    string Name,
    int TabCount,
    bool IsCollapsed,
    bool ContainsSelectedTab,
    bool IsCompact = false)
    : TabStripEntry
{
    public string ColorToken { get; init; } = "SeaGlass";
    public bool IsTemporary { get; init; } = true;
    public bool UsesPreviewDropdown => TabCount >= 5;
    public IReadOnlyList<BrowserTabId> TabIds { get; init; } = [];
    public IReadOnlyList<TabGroupPreviewItemPresentation> TabPreviews { get; init; } = [];
}

public sealed record BrowserTabEntry(
    BrowserTabId TabId,
    BrowserTabGroupId? GroupId,
    string Title,
    Uri? Address,
    BrowserLoadState LoadState,
    bool IsSelected,
    bool IsPrivate,
    bool CanGoBack,
    bool CanGoForward,
    bool IsCompact = false)
    : TabStripEntry;

public sealed record TabStripViewState(
    BrowserWindowId WindowId,
    BrowserTabId? SelectedTabId,
    IReadOnlyList<TabStripEntry> Entries);

public sealed record TabGroupingPlan(
    TabGroupPresentation Group,
    IReadOnlyList<MoveTabBrowserCommand> Commands);

public sealed class TabGroupPresentationCatalog
{
    public const int MaximumNameLength = 60;

    private readonly Dictionary<BrowserTabGroupId, TabGroupPresentation> groups = [];

    public IReadOnlyDictionary<BrowserTabGroupId, TabGroupPresentation> Groups =>
        new ReadOnlyDictionary<BrowserTabGroupId, TabGroupPresentation>(groups);

    public TabGroupPresentation Create(string name)
    {
        var group = new TabGroupPresentation(
            new BrowserTabGroupId(Guid.NewGuid()),
            NormalizeName(name),
            false);
        groups.Add(group.GroupId, group);
        return group;
    }

    public TabGroupPresentation Upsert(BrowserTabGroupId groupId, string name, bool isCollapsed)
    {
        if (groupId.IsEmpty)
        {
            throw new ArgumentException("A group ID is required.", nameof(groupId));
        }

        var group = new TabGroupPresentation(groupId, NormalizeName(name), isCollapsed);
        groups[groupId] = group;
        return group;
    }

    public TabGroupPresentation SetCollapsed(BrowserTabGroupId groupId, bool isCollapsed)
    {
        if (!groups.TryGetValue(groupId, out var group))
        {
            throw new KeyNotFoundException("The tab group is not registered.");
        }

        var updated = group with { IsCollapsed = isCollapsed };
        groups[groupId] = updated;
        return updated;
    }

    public bool Remove(BrowserTabGroupId groupId) => groups.Remove(groupId);

    private static string NormalizeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalized = name.Trim();
        return normalized.Length <= MaximumNameLength
            ? normalized
            : normalized[..MaximumNameLength];
    }
}

public static class TabGroupingPlanner
{
    public static TabGroupingPlan Create(
        BrowserState browserState,
        IReadOnlyList<BrowserTabId> tabIds,
        TabGroupPresentationCatalog catalog,
        string name)
    {
        ArgumentNullException.ThrowIfNull(browserState);
        ArgumentNullException.ThrowIfNull(tabIds);
        ArgumentNullException.ThrowIfNull(catalog);

        var distinctIds = tabIds.Distinct().ToArray();
        if (distinctIds.Length < 2)
        {
            throw new ArgumentException("At least two distinct tabs are required.", nameof(tabIds));
        }

        var indices = distinctIds.Select(id =>
        {
            var index = browserState.Tabs.ToList().FindIndex(tab => tab.TabId == id);
            if (index < 0)
            {
                throw new ArgumentException("Every grouped tab must belong to the window.", nameof(tabIds));
            }

            return (Id: id, Index: index);
        }).OrderBy(value => value.Index).ToArray();

        var group = catalog.Create(name);
        var commands = indices
            .Select(value => new MoveTabBrowserCommand(
                browserState.WindowId,
                value.Id,
                value.Index,
                group.GroupId))
            .ToArray();
        return new TabGroupingPlan(group, commands);
    }
}

public static class TabStripProjector
{
    public static TabStripViewState Project(
        BrowserState browserState,
        IReadOnlyDictionary<BrowserTabGroupId, TabGroupPresentation> groups,
        bool collapseToActive = false)
    {
        ArgumentNullException.ThrowIfNull(browserState);
        ArgumentNullException.ThrowIfNull(groups);

        var tabsByGroup = browserState.Tabs
            .Where(tab => tab.GroupId is not null)
            .GroupBy(tab => tab.GroupId!.Value)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var renderedGroups = new HashSet<BrowserTabGroupId>();
        var entries = new List<TabStripEntry>();

        foreach (var tab in browserState.Tabs)
        {
            if (tab.GroupId is not { } groupId)
            {
                entries.Add(ToEntry(
                    tab,
                    browserState.SelectedTabId,
                    collapseToActive && tab.TabId != browserState.SelectedTabId));
                continue;
            }

            var groupTabs = tabsByGroup[groupId];
            if (renderedGroups.Add(groupId))
            {
                groups.TryGetValue(groupId, out var presentation);
                var name = presentation?.Name ?? "Tab group";
                var isCollapsed = presentation?.IsCollapsed ?? false;
                var containsSelected = groupTabs.Any(value => value.TabId == browserState.SelectedTabId);
                entries.Add(new TabGroupHeaderEntry(
                    groupId,
                    name,
                    groupTabs.Length,
                    isCollapsed,
                    containsSelected,
                    collapseToActive && !containsSelected)
                {
                    ColorToken = presentation?.ColorToken ?? "SeaGlass",
                    IsTemporary = presentation?.IsTemporary ?? true,
                    TabIds = groupTabs.Select(value => value.TabId).ToArray(),
                    TabPreviews = groupTabs.Select(value => new TabGroupPreviewItemPresentation(
                        value.TabId,
                        string.IsNullOrWhiteSpace(value.Title) ? "New Tab" : value.Title,
                        value.Address,
                        ReadOnlyMemory<byte>.Empty)).ToArray(),
                });

                if (collapseToActive)
                {
                    entries.AddRange(groupTabs.Select(value => ToEntry(
                        value,
                        browserState.SelectedTabId,
                        value.TabId != browserState.SelectedTabId)));
                }
                else if (isCollapsed || groupTabs.Length >= 5)
                {
                    var selected = groupTabs.FirstOrDefault(value => value.TabId == browserState.SelectedTabId);
                    if (selected is not null)
                    {
                        entries.Add(ToEntry(selected, browserState.SelectedTabId));
                    }
                }
                else
                {
                    entries.AddRange(groupTabs.Select(value => ToEntry(value, browserState.SelectedTabId)));
                }
            }
        }

        return new TabStripViewState(
            browserState.WindowId,
            browserState.SelectedTabId,
            new ReadOnlyCollection<TabStripEntry>(entries));
    }

    private static BrowserTabEntry ToEntry(
        BrowserTabState tab,
        BrowserTabId? selectedTabId,
        bool isCompact = false) =>
        new(
            tab.TabId,
            tab.GroupId,
            string.IsNullOrWhiteSpace(tab.Title) ? "New tab" : tab.Title,
            tab.Address,
            tab.LoadState,
            tab.TabId == selectedTabId,
            tab.IsPrivate,
            tab.CanGoBack,
            tab.CanGoForward,
            isCompact);
}
