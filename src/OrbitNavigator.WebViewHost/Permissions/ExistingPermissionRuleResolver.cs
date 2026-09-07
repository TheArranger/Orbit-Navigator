using OrbitNavigator.Contracts.Privacy;

namespace OrbitNavigator.WebViewHost.Permissions;

public static class ExistingPermissionRuleResolver
{
    public static PermissionHostCompletion? Resolve(
        PermissionBrokerRequest request,
        SitePermissionState state)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(state);
        if (state.Context != request.Context || !state.Site.Equals(request.RequestingSite))
        {
            return null;
        }

        var rule = state.Rules.FirstOrDefault(candidate =>
            candidate.ProfileId == request.Context.Privacy.ProfileId &&
            candidate.Site.Equals(request.RequestingSite) &&
            candidate.Capability == request.Capability &&
            candidate.Decision is PermissionDecision.Allow or PermissionDecision.Deny &&
            candidate.Scope is PermissionAllowScope.Session or PermissionAllowScope.Persistent &&
            (!request.Context.Privacy.IsPrivate ||
             candidate.Scope != PermissionAllowScope.Persistent));
        return rule is null
            ? null
            : PermissionHostCompletion.FromAcceptedResponse(
                request.RequestId,
                request.Context.TabId,
                request.Capability,
                rule.Decision,
                rule,
                PermissionDecisionSource.ExistingRule);
    }
}
