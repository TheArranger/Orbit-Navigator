using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.App;

internal static class InternalPageTabRoute
{
    public static BrowserTabId? FindOpenTab(BrowserState state, BrowserInternalPageKind page)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!Enum.IsDefined(page) || page == BrowserInternalPageKind.None)
            throw new ArgumentOutOfRangeException(nameof(page));
        return state.Tabs.FirstOrDefault(tab => tab.InternalPage == page)?.TabId;
    }

    public static bool RequiresWebView(BrowserTabState tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        return tab.InternalPage == BrowserInternalPageKind.None;
    }
}
