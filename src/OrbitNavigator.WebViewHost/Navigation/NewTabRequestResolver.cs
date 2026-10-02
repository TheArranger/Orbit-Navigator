using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.WebViewHost.Navigation;

/// <summary>
/// Validates a page's new-window target through the same fail-closed policy as
/// top-level navigation before the host asks the owning browser to create a tab.
/// </summary>
public static class NewTabRequestResolver
{
    public static bool TryResolve(
        HostNavigationGuard navigation,
        BrowsingContext context,
        string? requestedTarget,
        bool isUserInitiated,
        out Uri? target)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(context);
        target = null;
        if (string.IsNullOrWhiteSpace(requestedTarget) ||
            !Uri.TryCreate(requestedTarget, UriKind.Absolute, out var candidate) ||
            candidate.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(candidate.UserInfo) ||
            string.IsNullOrWhiteSpace(candidate.IdnHost) ||
            !navigation.IsAllowed(
                context,
                candidate.AbsoluteUri,
                isMainFrame: true,
                isRedirect: false,
                isUserInitiated))
        {
            return false;
        }

        target = candidate;
        return true;
    }
}
