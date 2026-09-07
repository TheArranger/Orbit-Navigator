using OrbitNavigator.Contracts.Common;

using Xunit;

namespace OrbitNavigator.WebViewHost.Tests;

public sealed class WebViewTabVisualStateTests
{
    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3];

    [Fact]
    public void CreateUsesSafeDocumentTitleSiteAndClonedPng()
    {
        var source = Png.ToArray();
        var state = WebViewTabVisualState.Create(
            new BrowserTabId(Guid.NewGuid()),
            1,
            new Uri("https://www.example.test/path"),
            "  Example\r\n\u202e title  ",
            "New tab",
            true,
            true,
            false,
            source);

        source[8] = 99;

        Assert.Equal("Example title", state.PageTitle);
        Assert.Equal("www.example.test", state.SiteName);
        Assert.Equal("https://www.example.test/path", state.Address!.AbsoluteUri);
        Assert.Equal(1, state.Revision);
        Assert.True(state.IsLoading);
        Assert.True(state.CanGoBack);
        Assert.False(state.CanGoForward);
        Assert.Equal(1, state.FaviconPng.Span[8]);
    }

    [Fact]
    public void CreateFallsBackToSiteAndRejectsNonWebAddressOrInvalidPng()
    {
        var web = WebViewTabVisualState.Create(
            new BrowserTabId(Guid.NewGuid()),
            1,
            new Uri("https://example.test/path"),
            string.Empty,
            "New tab",
            false,
            false,
            false,
            [1, 2, 3]);
        var internalPage = WebViewTabVisualState.Create(
            new BrowserTabId(Guid.NewGuid()),
            1,
            new Uri("about:blank"),
            string.Empty,
            "Private tab",
            false,
            false,
            false,
            Png);

        Assert.Equal("example.test", web.PageTitle);
        Assert.Empty(web.FaviconPng.ToArray());
        Assert.Null(internalPage.Address);
        Assert.Equal("Private tab", internalPage.PageTitle);
        Assert.Equal(Png, internalPage.FaviconPng.ToArray());
    }

    [Fact]
    public void OversizedFaviconFailsClosed()
    {
        var oversized = new byte[WebViewTabVisualState.MaximumFaviconBytes + 1];
        Png.CopyTo(oversized, 0);

        var state = WebViewTabVisualState.Create(
            new BrowserTabId(Guid.NewGuid()),
            1,
            new Uri("https://example.test/"),
            "Example",
            "New tab",
            false,
            false,
            false,
            oversized);

        Assert.Empty(state.FaviconPng.ToArray());
    }

    [Theory]
    [InlineData("New tab")]
    [InlineData("new TAB")]
    [InlineData("")]
    public void AddresslessNormalPlaceholderUsesCanonicalNewTabTitle(string fallbackTitle)
    {
        var state = WebViewTabVisualState.Create(
            new BrowserTabId(Guid.NewGuid()),
            1,
            new Uri("about:blank"),
            string.Empty,
            fallbackTitle,
            false,
            false,
            false,
            []);

        Assert.Equal("New Tab", state.PageTitle);
    }
}
