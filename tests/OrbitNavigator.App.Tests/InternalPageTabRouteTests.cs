using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using Xunit;

namespace OrbitNavigator.App.Tests;

public sealed class InternalPageTabRouteTests
{
    [Fact]
    public void RepeatedSettingsRequestFindsExistingTabWithoutAllocatingWebView()
    {
        var window = new BrowserWindowId(Guid.NewGuid());
        var web = Tab("Web");
        var settings = Tab("Settings") with
        {
            InternalPage = BrowserInternalPageKind.Settings,
        };
        var state = new BrowserState(window, web.TabId, [web, settings]);

        var existing = InternalPageTabRoute.FindOpenTab(
            state,
            BrowserInternalPageKind.Settings);

        Assert.Equal(settings.TabId, existing);
        Assert.True(InternalPageTabRoute.RequiresWebView(web));
        Assert.False(InternalPageTabRoute.RequiresWebView(settings));
    }

    [Fact]
    public void ClosedSettingsTabIsNoLongerSelectedByRoute()
    {
        var window = new BrowserWindowId(Guid.NewGuid());
        var web = Tab("Web");
        var stateAfterClose = new BrowserState(window, web.TabId, [web]);

        Assert.Null(InternalPageTabRoute.FindOpenTab(
            stateAfterClose,
            BrowserInternalPageKind.Settings));
    }

    private static BrowserTabState Tab(string title) => new(
        new BrowserTabId(Guid.NewGuid()),
        null,
        null,
        title,
        BrowserLoadState.Idle,
        false,
        false,
        false);
}
