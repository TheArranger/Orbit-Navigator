using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Privacy;
using Xunit;

namespace OrbitNavigator.Contracts.Tests.Privacy;

public sealed class PermissionContractTests
{
    [Fact]
    public void UnknownCapabilityAndHostDefaultsAreFailClosed()
    {
        var requestId = new RequestId(Guid.NewGuid());
        var tabId = new BrowserTabId(Guid.NewGuid());

        var completion = PermissionHostCompletion.FromAcceptedResponse(
            requestId,
            tabId,
            WebPermissionCapability.Unknown,
            PermissionDecision.Allow);

        Assert.Equal(0, (int)WebPermissionCapability.Unknown);
        Assert.Equal(PermissionHostDisposition.Deny, completion.Disposition);
        Assert.True(completion.IsFailClosed);
        Assert.False(completion.SaveInProfile);
        Assert.Null(completion.AppliedRule);
    }

    [Fact]
    public void BrokerRequestCarriesReplayExpiryAndRequestingViewIdentity()
    {
        var context = Browsing();
        var now = DateTimeOffset.UtcNow;
        var request = new PermissionBrokerRequest(
            new RequestId(Guid.NewGuid()),
            new ResponseToken(Guid.NewGuid()),
            context,
            SiteIdentity.Create(new Uri("https://example.test/path")).Value!,
            WebPermissionCapability.Camera,
            [PermissionAllowScope.Once, PermissionAllowScope.Session],
            PermissionDecision.Ask,
            PermissionDecision.Deny,
            true,
            now,
            now.AddSeconds(30));

        Assert.False(request.RequestId.IsEmpty);
        Assert.False(request.ResponseToken.IsEmpty);
        Assert.Equal(context.TabId, request.Context.TabId);
        Assert.True(request.ExpiresAtUtc > request.RequestedAtUtc);
        Assert.DoesNotContain(PermissionAllowScope.Persistent, request.SupportedAllowScopes);
    }

    [Theory]
    [InlineData(PermissionAllowScope.Once)]
    [InlineData(PermissionAllowScope.Session)]
    [InlineData(PermissionAllowScope.Persistent)]
    public void WebViewNeverPersistsOrbitPermissionRulesInItsOwnStore(PermissionAllowScope scope)
    {
        var context = Browsing();
        var site = SiteIdentity.Create(new Uri("https://example.test")).Value!;
        var rule = new PermissionRule(
            new PermissionRuleId(context.Privacy.ProfileId, Guid.NewGuid()),
            context.Privacy.ProfileId,
            scope == PermissionAllowScope.Persistent ? null : context.Privacy.SessionId,
            site,
            WebPermissionCapability.Notifications,
            PermissionDecision.Allow,
            scope,
            DateTimeOffset.UtcNow,
            null);

        var completion = PermissionHostCompletion.FromAcceptedResponse(
            new RequestId(Guid.NewGuid()),
            context.TabId,
            WebPermissionCapability.Notifications,
            PermissionDecision.Allow,
            rule);

        Assert.False(completion.SaveInProfile);
        Assert.Same(rule, completion.AppliedRule);
        Assert.Equal(PermissionDecisionSource.UserResponse, completion.Source);
    }

    [Fact]
    public void PromptEventArgsExposeTypedPromptAndExpiry()
    {
        var context = Browsing();
        var expires = DateTimeOffset.UtcNow.AddMinutes(1);
        var prompt = new PermissionPromptState(
            new RequestId(Guid.NewGuid()),
            new ResponseToken(Guid.NewGuid()),
            context,
            SiteIdentity.Create(new Uri("https://example.test")).Value!,
            "https://example.test",
            WebPermissionCapability.Geolocation,
            [PermissionAllowScope.Once],
            PermissionDecision.Ask,
            PermissionDecision.Deny,
            expires);

        var args = new PermissionPromptEventArgs(prompt);

        Assert.Same(prompt, args.Prompt);
        Assert.Equal(context.TabId, args.Prompt.Context.TabId);
        Assert.Equal(expires, args.Prompt.ExpiresAtUtc);
    }

    [Theory]
    [InlineData(WebPermissionCapability.Popups)]
    [InlineData(WebPermissionCapability.Autoplay)]
    public void OnDemandContentControlsUseFullPermissionLifecycle(
        WebPermissionCapability capability)
    {
        var context = Browsing();
        var site = SiteIdentity.Create(new Uri("https://media.example.test")).Value!;
        var now = DateTimeOffset.UtcNow;
        var scopes = new[]
        {
            PermissionAllowScope.Once,
            PermissionAllowScope.Session,
            PermissionAllowScope.Persistent,
        };
        var request = new PermissionBrokerRequest(
            new RequestId(Guid.NewGuid()),
            new ResponseToken(Guid.NewGuid()),
            context,
            site,
            capability,
            scopes,
            PermissionDecision.Deny,
            PermissionDecision.Deny,
            true,
            now,
            now.AddSeconds(30));
        var prompt = new PermissionPromptState(
            request.RequestId,
            request.ResponseToken,
            context,
            site,
            site.DisplayOrigin,
            capability,
            scopes,
            request.CurrentDecision,
            request.DefaultDecision,
            request.ExpiresAtUtc);
        var rule = new PermissionRule(
            new PermissionRuleId(context.Privacy.ProfileId, Guid.NewGuid()),
            context.Privacy.ProfileId,
            context.Privacy.SessionId,
            site,
            capability,
            PermissionDecision.Allow,
            PermissionAllowScope.Session,
            now,
            request.ExpiresAtUtc);
        var state = new SitePermissionState(context, site, [rule]);
        var reset = new ResetPermissionRuleIntent(context, rule.Id);

        Assert.Equal(PermissionDecision.Deny, request.DefaultDecision);
        Assert.Equal(capability, prompt.Capability);
        Assert.True(prompt.ExpiresAtUtc > now);
        Assert.Equal(scopes, prompt.SupportedAllowScopes);
        Assert.Contains(state.Rules, value => value.Capability == capability);
        Assert.Equal(rule.Id, reset.RuleId);
    }

    private static BrowsingContext Browsing() =>
        new(
            new PrivacyContext(
                new ProfileId(Guid.NewGuid()),
                new BrowserSessionId(Guid.NewGuid()),
                BrowserProfileMode.Normal),
            new BrowserWindowId(Guid.NewGuid()),
            new BrowserTabId(Guid.NewGuid()),
            null);
}
