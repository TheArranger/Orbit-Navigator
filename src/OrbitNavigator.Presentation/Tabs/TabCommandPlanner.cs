using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.Presentation.Tabs;

/// <summary>Produces typed tab commands without executing them.</summary>
public static class TabCommandPlanner
{
    public static CreateTabBrowserCommand CreateNewTab(BrowserWindowId windowId, BrowserTabGroupId? groupId = null) =>
        new(windowId, new BrowserTabId(Guid.NewGuid()), null, groupId);

    public static CloseTabBrowserCommand Close(BrowserState state, BrowserTabId tabId)
    {
        EnsureOwned(state, tabId);
        return new CloseTabBrowserCommand(state.WindowId, tabId);
    }

    public static SelectTabBrowserCommand Select(BrowserState state, BrowserTabId tabId)
    {
        EnsureOwned(state, tabId);
        return new SelectTabBrowserCommand(state.WindowId, tabId);
    }

    public static MoveTabBrowserCommand Move(BrowserState state, BrowserTabId tabId, int index, BrowserTabGroupId? groupId)
    {
        EnsureOwned(state, tabId);
        return new MoveTabBrowserCommand(state.WindowId, tabId, Math.Clamp(index, 0, Math.Max(0, state.Tabs.Count - 1)), groupId);
    }

    private static void EnsureOwned(BrowserState state, BrowserTabId tabId)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (tabId.IsEmpty || !state.Tabs.Any(tab => tab.TabId == tabId))
        {
            throw new ArgumentException("The tab must belong to the active window.", nameof(tabId));
        }
    }
}
