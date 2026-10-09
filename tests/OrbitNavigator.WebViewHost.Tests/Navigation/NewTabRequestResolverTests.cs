using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.WebViewHost.Navigation;
using Xunit;

namespace OrbitNavigator.WebViewHost.Tests.Navigation;

public sealed class NewTabRequestResolverTests
{
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/private.txt")]
    [InlineData("data:text/html,hello")]
    [InlineData("blob:https://example.test/123")]
    [InlineData("ftp://example.test/path")]
    [InlineData("/relative/path")]
    [InlineData("https://user:password@example.test/")]
    [InlineData("")]
    public void UnsafePopupTargetsFailClosedBeforeReachingBrowser(string requested)
    {
        var policy = new RecordingPolicy(NavigationDecision.Allow);

        var resolved = NewTabRequestResolver.TryResolve(
            new HostNavigationGuard(policy),
            Context(BrowserProfileMode.Normal),
            requested,
            true,
            out var target);

        Assert.False(resolved);
        Assert.Null(target);
        Assert.Equal(0, policy.Calls);
    }

    [Theory]
    [InlineData(BrowserProfileMode.Normal)]
    [InlineData(BrowserProfileMode.Private)]
    public void AllowedPopupPreservesWindowProfileAndUserInitiation(BrowserProfileMode mode)
    {
        var policy = new RecordingPolicy(NavigationDecision.Allow);
        var context = Context(mode);

        var resolved = NewTabRequestResolver.TryResolve(
            new HostNavigationGuard(policy),
            context,
            "https://example.test/path",
            true,
            out var target);

        Assert.True(resolved);
        Assert.Equal("https://example.test/path", target!.AbsoluteUri);
        Assert.Equal(1, policy.Calls);
        Assert.Equal(context, policy.LastRequest?.Context);
        Assert.True(policy.LastRequest!.IsUserInitiated);
    }

    [Fact]
    public void PolicyDenialCannotCreateTab()
    {
        var policy = new RecordingPolicy(NavigationDecision.Block);

        var resolved = NewTabRequestResolver.TryResolve(
            new HostNavigationGuard(policy),
            Context(BrowserProfileMode.Normal),
            "https://blocked.example.test/",
            false,
            out var target);

        Assert.False(resolved);
        Assert.Null(target);
        Assert.Equal(1, policy.Calls);
        Assert.False(policy.LastRequest!.IsUserInitiated);
    }

    private static BrowsingContext Context(BrowserProfileMode mode) => new(
        new PrivacyContext(
            new ProfileId(Guid.NewGuid()),
            new BrowserSessionId(Guid.NewGuid()),
            mode),
        new BrowserWindowId(Guid.NewGuid()),
        new BrowserTabId(Guid.NewGuid()),
        null);

    private sealed class RecordingPolicy(NavigationDecision decision) : INavigationPolicy
    {
        public int Calls { get; private set; }
        public NavigationPolicyRequest? LastRequest { get; private set; }

        public NavigationDecision Evaluate(in NavigationPolicyRequest request)
        {
            Calls++;
            LastRequest = request;
            return decision;
        }
    }
}
