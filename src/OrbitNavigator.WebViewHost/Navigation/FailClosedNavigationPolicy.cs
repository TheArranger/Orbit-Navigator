using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.WebViewHost.Navigation;

public sealed class FailClosedNavigationPolicy : INavigationPolicy
{
    public NavigationDecision Evaluate(in NavigationPolicyRequest request) =>
        NavigationDecision.Block;
}

public sealed class HostNavigationGuard
{
    private readonly INavigationPolicy _policy;

    public HostNavigationGuard(INavigationPolicy? policy = null)
    {
        _policy = policy ?? new FailClosedNavigationPolicy();
    }

    public bool IsAllowed(
        BrowsingContext context,
        string target,
        bool isMainFrame,
        bool isRedirect,
        bool isUserInitiated)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.IsStructurallyValid ||
            !Uri.TryCreate(target, UriKind.Absolute, out var targetUri) ||
            !SiteIdentity.TryCreate(targetUri, out _))
        {
            return false;
        }

        try
        {
            var request = new NavigationPolicyRequest(
                context,
                context.CurrentSite,
                targetUri,
                isMainFrame,
                isRedirect,
                isUserInitiated);
            return _policy.Evaluate(in request) == NavigationDecision.Allow;
        }
        catch
        {
            return false;
        }
    }
}
