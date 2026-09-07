using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Privacy;

namespace OrbitNavigator.WebViewHost.Navigation;

/// <summary>
/// Adapts the privacy-owned evaluator to the synchronous WebView navigation
/// seam. It deliberately makes no privacy decision of its own.
/// </summary>
public sealed class SiteProtectionNavigationPolicy : INavigationPolicy
{
    private readonly ISiteProtectionEvaluator _evaluator;

    public SiteProtectionNavigationPolicy(ISiteProtectionEvaluator evaluator)
    {
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
    }

    public NavigationDecision Evaluate(in NavigationPolicyRequest request)
    {
        if (request.Context is not { IsStructurallyValid: true } ||
            !SiteIdentity.TryCreate(request.Target, out var target))
        {
            return NavigationDecision.Block;
        }

        try
        {
            var evaluation = _evaluator.Evaluate(request.Context, target!);
            return evaluation.IsSuccess &&
                   evaluation.Value!.Disposition == ProtectionNavigationDisposition.Allow
                ? NavigationDecision.Allow
                : NavigationDecision.Block;
        }
        catch
        {
            return NavigationDecision.Block;
        }
    }
}
