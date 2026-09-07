using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.Contracts.Browser;

public enum NavigationDecision
{
    Block = 0,
    Allow = 1,
}

public sealed record NavigationPolicyRequest(
    BrowsingContext Context,
    SiteIdentity? SourceSite,
    Uri Target,
    bool IsMainFrame,
    bool IsRedirect,
    bool IsUserInitiated);

public interface INavigationPolicy
{
    NavigationDecision Evaluate(in NavigationPolicyRequest request);
}

