using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.Presentation.Tests;

internal static class TestContexts
{
    public static BrowsingContext Browsing(BrowserProfileMode mode = BrowserProfileMode.Normal) =>
        new(
            new PrivacyContext(
                new ProfileId(Guid.NewGuid()),
                new BrowserSessionId(Guid.NewGuid()),
                mode),
            new BrowserWindowId(Guid.NewGuid()),
            new BrowserTabId(Guid.NewGuid()),
            null);

    public static SiteIdentity Site(string uri = "https://example.test") =>
        SiteIdentity.Create(new Uri(uri)).Value!;
}
