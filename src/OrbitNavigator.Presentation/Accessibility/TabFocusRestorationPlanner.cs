using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Presentation.Tabs;

namespace OrbitNavigator.Presentation.Accessibility;

public enum TabStripFocusTargetKind
{
    Tab = 0,
    GroupHeader = 1,
    TabStrip = 2,
}

/// <summary>
/// A framework-neutral focus target. The WPF shell resolves this to its own
/// automation element after a tab strip change has been committed by the host.
/// </summary>
public sealed record TabStripFocusTarget(
    TabStripFocusTargetKind Kind,
    BrowserTabId? TabId,
    BrowserTabGroupId? GroupId)
{
    public static TabStripFocusTarget Tab(BrowserTabId tabId) =>
        new(TabStripFocusTargetKind.Tab, tabId, null);

    public static TabStripFocusTarget GroupHeader(BrowserTabGroupId groupId) =>
        new(TabStripFocusTargetKind.GroupHeader, null, groupId);

    public static TabStripFocusTarget Strip() =>
        new(TabStripFocusTargetKind.TabStrip, null, null);
}

public static class TabFocusRestorationPlanner
{
    /// <summary>
    /// Chooses a visible, keyboard-reachable item after the focused tab was
    /// closed. The host-selected tab wins when it remains visible; otherwise
    /// the nearest surviving entry in the old visual order is selected.
    /// </summary>
    public static TabStripFocusTarget AfterTabClosed(
        TabStripViewState before,
        TabStripViewState after,
        BrowserTabId closedTabId)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var visibleAfter = after.Entries.OfType<BrowserTabEntry>().ToArray();
        var selected = visibleAfter.FirstOrDefault(tab => tab.TabId == after.SelectedTabId);
        if (selected is not null)
        {
            return TabStripFocusTarget.Tab(selected.TabId);
        }

        var beforeIndex = IndexOfTab(before.Entries, closedTabId);
        if (beforeIndex >= 0)
        {
            for (var distance = 0; distance < after.Entries.Count; distance++)
            {
                var forward = beforeIndex + distance;
                if (forward < after.Entries.Count && after.Entries[forward] is BrowserTabEntry next)
                {
                    return TabStripFocusTarget.Tab(next.TabId);
                }

                var backward = beforeIndex - distance - 1;
                if (backward >= 0 && after.Entries[backward] is BrowserTabEntry previous)
                {
                    return TabStripFocusTarget.Tab(previous.TabId);
                }
            }
        }

        return visibleAfter.FirstOrDefault() is { } first
            ? TabStripFocusTarget.Tab(first.TabId)
            : TabStripFocusTarget.Strip();
    }

    /// <summary>
    /// Collapsing a group returns focus to its named header. This avoids
    /// leaving keyboard focus on a member that has just left the tab order.
    /// </summary>
    public static TabStripFocusTarget AfterGroupCollapsed(BrowserTabGroupId groupId)
    {
        if (groupId.IsEmpty)
        {
            throw new ArgumentException("A group ID is required.", nameof(groupId));
        }

        return TabStripFocusTarget.GroupHeader(groupId);
    }

    private static int IndexOfTab(IReadOnlyList<TabStripEntry> entries, BrowserTabId tabId)
    {
        for (var index = 0; index < entries.Count; index++)
        {
            if (entries[index] is BrowserTabEntry tab && tab.TabId == tabId)
            {
                return index;
            }
        }

        return -1;
    }
}
