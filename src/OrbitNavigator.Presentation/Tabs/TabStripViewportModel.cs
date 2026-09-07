using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Presentation.Workspace;

namespace OrbitNavigator.Presentation.Tabs;

public sealed record TabStripViewportProjection(
    int StartIndex,
    int Count,
    int HiddenBefore,
    int HiddenAfter,
    bool CanPagePrevious,
    bool CanPageNext,
    IReadOnlyList<TabStripEntry> VisibleEntries)
{
    public int HiddenCount => HiddenBefore + HiddenAfter;

    /// <summary>The allocated extent for each visible entry, including its close action.</summary>
    public double EntryExtent { get; init; } = TabStripViewportModel.PreferredTopTabExtent;
}

public static class TabStripViewportModel
{
    public const double PreferredTopTabExtent = 156;
    public const double SafeTopTabExtent = 88;
    public const double CompactTopTabExtent = 88;
    public const double AudibleCompactTopTabExtent = 132;
    public const double SafeVerticalEntryExtent = 48;
    public const double MinimumVerticalColumnWidth = 136;
    public const double PreferredVerticalColumnWidth = 184;
    public const double VerticalColumnSpacing = 4;
    public const double ReservedVerticalScrollbarGutter = 18;
    public const double ReadableTitleThreshold = 132;

    public static TabStripViewportProjection Project(
        IReadOnlyList<TabStripEntry> canonicalEntries,
        BrowserTabId? selectedTabId,
        double availableExtent,
        TabStripPlacement placement,
        bool detached) =>
        Project(canonicalEntries, selectedTabId, null, availableExtent, placement, detached, false);

    public static TabStripViewportProjection Project(
        IReadOnlyList<TabStripEntry> canonicalEntries,
        BrowserTabId? selectedTabId,
        BrowserTabId? keyboardFocusedTabId,
        double availableExtent,
        TabStripPlacement placement,
        bool detached) =>
        Project(canonicalEntries, selectedTabId, keyboardFocusedTabId, availableExtent, placement, detached, false);

    public static TabStripViewportProjection Project(
        IReadOnlyList<TabStripEntry> canonicalEntries,
        BrowserTabId? selectedTabId,
        BrowserTabId? keyboardFocusedTabId,
        double availableExtent,
        TabStripPlacement placement,
        bool detached,
        bool compactMode,
        double minimumEntryExtentOverride = 0)
    {
        ArgumentNullException.ThrowIfNull(canonicalEntries);
        if (!Enum.IsDefined(placement))
        {
            throw new ArgumentOutOfRangeException(nameof(placement));
        }

        if (canonicalEntries.Count == 0 || availableExtent <= 0)
        {
            return new(0, 0, 0, canonicalEntries.Count, false, canonicalEntries.Count > 0, [])
            {
                EntryExtent = Math.Max(
                    compactMode ? CompactTopTabExtent : SafeTopTabExtent,
                    minimumEntryExtentOverride),
            };
        }

        var vertical = detached || placement is TabStripPlacement.Left or TabStripPlacement.Right;
        var baseMinimumExtent = vertical
            ? SafeVerticalEntryExtent
            : SafeTopTabExtent;
        var minimumExtent = Math.Max(baseMinimumExtent, minimumEntryExtentOverride);
        var extent = vertical
            ? SafeVerticalEntryExtent
            : Math.Clamp(
                availableExtent / Math.Max(1, canonicalEntries.Count),
                minimumExtent,
                PreferredTopTabExtent);
        var capacity = Math.Max(1, Math.Min(canonicalEntries.Count, (int)Math.Floor(availableExtent / extent)));
        var anchor = FindTabIndex(canonicalEntries, keyboardFocusedTabId) ??
                     FindTabIndex(canonicalEntries, selectedTabId) ?? 0;
        var start = Math.Clamp(anchor - (capacity / 2), 0, canonicalEntries.Count - capacity);
        var end = start + capacity;

        // Keep a group header with its first visible member when space allows, without
        // changing canonical order or the group's collapsed state.
        if (start > 0 && canonicalEntries[start] is BrowserTabEntry { GroupId: { } groupId } &&
            canonicalEntries[start - 1] is TabGroupHeaderEntry header && header.GroupId == groupId &&
            capacity > 1)
        {
            start--;
            end--;
        }

        var visible = canonicalEntries.Skip(start).Take(end - start).ToArray();
        return new(
            start,
            visible.Length,
            start,
            canonicalEntries.Count - end,
            start > 0,
            end < canonicalEntries.Count,
            visible)
        {
            EntryExtent = extent,
        };
    }

    private static int? FindTabIndex(IReadOnlyList<TabStripEntry> entries, BrowserTabId? tabId)
    {
        if (tabId is null)
        {
            return null;
        }

        for (var index = 0; index < entries.Count; index++)
        {
            if (entries[index] is BrowserTabEntry tab && tab.TabId == tabId)
            {
                return index;
            }
        }

        return null;
    }
}
