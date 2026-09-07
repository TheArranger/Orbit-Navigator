using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.WebViewHost.Navigation;
using Xunit;

namespace OrbitNavigator.WebViewHost.Tests.Navigation;

public sealed class HostNavigationGuardTests
{
    [Fact]
    public void MissingPolicyBlocksByDefault()
    {
        var guard = new HostNavigationGuard();

        Assert.False(guard.IsAllowed(Context(), "https://example.test", true, false, true));
    }

    [Theory]
    [InlineData("file:///C:/secret.txt")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not a URI")]
    public void NonHttpTargetsBlockBeforePolicy(string target)
    {
        var policy = new RecordingAllowPolicy();
        var guard = new HostNavigationGuard(policy);

        Assert.False(guard.IsAllowed(Context(), target, true, false, true));
        Assert.Equal(0, policy.Calls);
    }

    [Fact]
    public void PolicyExceptionBlocksNavigation()
    {
        var guard = new HostNavigationGuard(new ThrowingPolicy());

        Assert.False(guard.IsAllowed(Context(), "https://example.test", true, false, true));
    }

    [Fact]
    public void ExplicitPolicyAllowPermitsCanonicalHttpTarget()
    {
        var policy = new RecordingAllowPolicy();
        var guard = new HostNavigationGuard(policy);

        Assert.True(guard.IsAllowed(Context(), "https://example.test/path", true, false, true));
        Assert.Equal(1, policy.Calls);
    }

    private static BrowsingContext Context() =>
        new(
            new PrivacyContext(
                new ProfileId(Guid.NewGuid()),
                new BrowserSessionId(Guid.NewGuid()),
                BrowserProfileMode.Normal),
            new BrowserWindowId(Guid.NewGuid()),
            new BrowserTabId(Guid.NewGuid()),
            null);

    private sealed class RecordingAllowPolicy : INavigationPolicy
    {
        public int Calls { get; private set; }

        public NavigationDecision Evaluate(in NavigationPolicyRequest request)
        {
            Calls++;
            return NavigationDecision.Allow;
        }
    }

    private sealed class ThrowingPolicy : INavigationPolicy
    {
        public NavigationDecision Evaluate(in NavigationPolicyRequest request) =>
            throw new InvalidOperationException("test");
    }
}
