using System.Collections.Concurrent;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Privacy;
using OrbitNavigator.Privacy.Permissions;
using Xunit;

namespace OrbitNavigator.Privacy.Tests.Permissions;

public sealed class PermissionBrokerTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task OnceResponseIsOneShotNeverStoredAndNeverSavedByHost()
    {
        var clock = new FakeClock(Start);
        var sink = new RecordingSink();
        var broker = new PermissionBroker(clock, sink);
        var context = Browsing();
        var request = Request(
            context,
            Site("https://once.test"),
            [PermissionAllowScope.Once]);
        PermissionPromptState? raised = null;
        broker.PromptRequested += (_, args) => raised = args.Prompt;

        var ingested = await broker.IngestAsync(request, default);
        var response = Response(
            request,
            PermissionDecision.Allow,
            PermissionAllowScope.Once);
        var accepted = await broker.RespondAsync(response, default);
        var replay = await broker.RespondAsync(response, default);
        var state = await broker.GetCurrentSiteStateAsync(
            context,
            request.RequestingSite,
            default);

        Assert.True(ingested.IsSuccess);
        Assert.Same(ingested.Value, raised);
        Assert.True(accepted.IsSuccess);
        Assert.Equal(PermissionHostDisposition.Allow, accepted.Value?.Disposition);
        Assert.Equal(PermissionAllowScope.Once, accepted.Value?.AppliedRule?.Scope);
        Assert.False(accepted.Value?.SaveInProfile);
        Assert.Equal(ControllerErrorCode.AlreadyHandled, replay.Error?.Code);
        Assert.Empty(state.Value!.Rules);
        Assert.Single(sink.Completions);
        Assert.All(sink.Completions, completion => Assert.False(completion.SaveInProfile));
    }

    [Fact]
    public async Task SessionRuleUsesExactOriginProfileSessionAndMode()
    {
        var clock = new FakeClock(Start);
        var sink = new RecordingSink();
        var broker = new PermissionBroker(clock, sink);
        var owner = Browsing();
        var site = Site("https://media.test/path");
        var request = Request(
            owner,
            site,
            [PermissionAllowScope.Session]);
        await broker.IngestAsync(request, default);
        await broker.RespondAsync(
            Response(
                request,
                PermissionDecision.Allow,
                PermissionAllowScope.Session),
            default);

        var sameOrigin = await broker.GetCurrentSiteStateAsync(
            owner,
            Site("https://media.test/other"),
            default);
        var subdomain = await broker.GetCurrentSiteStateAsync(
            owner,
            Site("https://sub.media.test"),
            default);
        var otherProfile = await broker.GetCurrentSiteStateAsync(
            Browsing(),
            site,
            default);
        var otherSession = await broker.GetCurrentSiteStateAsync(
            Browsing(BrowserProfileMode.Normal, owner.Privacy.ProfileId),
            site,
            default);
        var sameIdsPrivate = await broker.GetCurrentSiteStateAsync(
            Browsing(
                BrowserProfileMode.Private,
                owner.Privacy.ProfileId,
                owner.Privacy.SessionId),
            site,
            default);

        Assert.Single(sameOrigin.Value!.Rules);
        Assert.Empty(subdomain.Value!.Rules);
        Assert.Empty(otherProfile.Value!.Rules);
        Assert.Empty(otherSession.Value!.Rules);
        Assert.Empty(sameIdsPrivate.Value!.Rules);
    }

    [Fact]
    public async Task PersistentRuleCrossesNormalSessionButIsHiddenFromPrivateMode()
    {
        var clock = new FakeClock(Start);
        var broker = new PermissionBroker(clock, new RecordingSink());
        var normal = Browsing();
        var site = Site("https://persistent.test");
        var request = Request(
            normal,
            site,
            [PermissionAllowScope.Persistent]);
        await broker.IngestAsync(request, default);
        var accepted = await broker.RespondAsync(
            Response(
                request,
                PermissionDecision.Allow,
                PermissionAllowScope.Persistent),
            default);

        var laterNormal = await broker.GetCurrentSiteStateAsync(
            Browsing(BrowserProfileMode.Normal, normal.Privacy.ProfileId),
            site,
            default);
        var privateContext = await broker.GetCurrentSiteStateAsync(
            Browsing(BrowserProfileMode.Private, normal.Privacy.ProfileId),
            site,
            default);

        Assert.True(accepted.IsSuccess);
        Assert.Null(accepted.Value?.AppliedRule?.SessionId);
        Assert.Single(laterNormal.Value!.Rules);
        Assert.Empty(privateContext.Value!.Rules);
    }

    [Fact]
    public async Task PrivatePromptHidesPersistentAndPersistentResponseFailsClosed()
    {
        var sink = new RecordingSink();
        var broker = new PermissionBroker(new FakeClock(Start), sink);
        var request = Request(
            Browsing(BrowserProfileMode.Private),
            Site("https://private.test"),
            [
                PermissionAllowScope.Once,
                PermissionAllowScope.Session,
                PermissionAllowScope.Persistent,
            ]);

        var prompt = await broker.IngestAsync(request, default);
        var result = await broker.RespondAsync(
            Response(
                request,
                PermissionDecision.Allow,
                PermissionAllowScope.Persistent),
            default);

        Assert.DoesNotContain(
            PermissionAllowScope.Persistent,
            prompt.Value!.SupportedAllowScopes);
        Assert.Equal(ControllerErrorCode.PolicyDenied, result.Error?.Code);
        var completion = Assert.Single(sink.Completions);
        Assert.True(completion.IsFailClosed);
        Assert.Equal(PermissionHostDisposition.Deny, completion.Disposition);
        Assert.False(completion.SaveInProfile);
    }

    [Fact]
    public async Task UnsupportedScopeFailsClosedAndConsumesResponseToken()
    {
        var sink = new RecordingSink();
        var broker = new PermissionBroker(new FakeClock(Start), sink);
        var request = Request(
            Browsing(),
            Site("https://scope.test"),
            [PermissionAllowScope.Once]);
        await broker.IngestAsync(request, default);
        var response = Response(
            request,
            PermissionDecision.Allow,
            PermissionAllowScope.Session);

        var rejected = await broker.RespondAsync(response, default);
        var replay = await broker.RespondAsync(response, default);

        Assert.Equal(ControllerErrorCode.NotSupported, rejected.Error?.Code);
        Assert.Equal(ControllerErrorCode.AlreadyHandled, replay.Error?.Code);
        Assert.True(Assert.Single(sink.Completions).IsFailClosed);
    }

    [Fact]
    public async Task ExpiredResponseCompletesHostFailClosed()
    {
        var clock = new FakeClock(Start);
        var sink = new RecordingSink();
        var broker = new PermissionBroker(clock, sink);
        var request = Request(
            Browsing(),
            Site("https://expired.test"),
            [PermissionAllowScope.Once],
            expiresAtUtc: Start.AddSeconds(30));
        await broker.IngestAsync(request, default);

        clock.Advance(TimeSpan.FromSeconds(30));
        var result = await broker.RespondAsync(
            Response(
                request,
                PermissionDecision.Allow,
                PermissionAllowScope.Once),
            default);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value?.IsFailClosed);
        Assert.Equal(PermissionDecisionSource.RequestExpired, result.Value?.Source);
        Assert.Equal(PermissionHostDisposition.Deny, result.Value?.Disposition);
        Assert.False(result.Value?.SaveInProfile);
        Assert.Single(sink.Completions);
    }

    [Fact]
    public async Task UnknownCapabilityAndNonDenyDefaultFailClosedWithoutPrompt()
    {
        var sink = new RecordingSink();
        var broker = new PermissionBroker(new FakeClock(Start), sink);
        var prompts = 0;
        broker.PromptRequested += (_, _) => prompts++;
        var unknown = Request(
            Browsing(),
            Site("https://unknown.test"),
            [PermissionAllowScope.Once],
            capability: WebPermissionCapability.Unknown);
        var unsafeDefault = Request(
            Browsing(),
            Site("https://default.test"),
            [PermissionAllowScope.Once],
            defaultDecision: PermissionDecision.Allow);

        var unknownResult = await broker.IngestAsync(unknown, default);
        var defaultResult = await broker.IngestAsync(unsafeDefault, default);
        var unknownReplay = await broker.IngestAsync(unknown, default);

        Assert.False(unknownResult.IsSuccess);
        Assert.False(defaultResult.IsSuccess);
        Assert.Equal(
            ControllerErrorCode.AlreadyHandled,
            unknownReplay.Error?.Code);
        Assert.Equal(0, prompts);
        Assert.Equal(2, sink.Completions.Count);
        Assert.All(sink.Completions, completion =>
        {
            Assert.True(completion.IsFailClosed);
            Assert.False(completion.SaveInProfile);
        });
    }

    [Fact]
    public async Task ConcurrentResponsesProduceExactlyOneHostCompletion()
    {
        var sink = new RecordingSink();
        var broker = new PermissionBroker(new FakeClock(Start), sink);
        var request = Request(
            Browsing(),
            Site("https://concurrent.test"),
            [PermissionAllowScope.Once]);
        await broker.IngestAsync(request, default);
        var response = Response(
            request,
            PermissionDecision.Allow,
            PermissionAllowScope.Once);

        var attempts = Enumerable.Range(0, 20)
            .Select(_ => broker.RespondAsync(response, default).AsTask())
            .ToArray();
        var results = await Task.WhenAll(attempts);

        Assert.Single(results, result => result.IsSuccess);
        Assert.All(
            results.Where(result => !result.IsSuccess),
            result => Assert.Equal(
                ControllerErrorCode.AlreadyHandled,
                result.Error?.Code));
        Assert.Single(sink.Completions);
    }

    [Fact]
    public async Task ResetRequiresRuleVisibility()
    {
        var broker = new PermissionBroker(
            new FakeClock(Start),
            new RecordingSink());
        var owner = Browsing();
        var site = Site("https://reset.test");
        var request = Request(
            owner,
            site,
            [PermissionAllowScope.Session]);
        await broker.IngestAsync(request, default);
        var completion = await broker.RespondAsync(
            Response(
                request,
                PermissionDecision.Allow,
                PermissionAllowScope.Session),
            default);
        var ruleId = completion.Value!.AppliedRule!.Id;

        var hidden = await broker.ResetAsync(
            new ResetPermissionRuleIntent(
                Browsing(
                    BrowserProfileMode.Normal,
                    owner.Privacy.ProfileId).WithSite(site),
                ruleId),
            default);
        var removed = await broker.ResetAsync(
            new ResetPermissionRuleIntent(owner.WithSite(site), ruleId),
            default);

        Assert.Equal(ControllerErrorCode.NotFound, hidden.Error?.Code);
        Assert.True(removed.IsSuccess);
        Assert.Empty((await broker.GetCurrentSiteStateAsync(
            owner,
            site,
            default)).Value!.Rules);
    }

    [Fact]
    public async Task RuleStoreIsBoundedAndRetainsExistingRuleOnOverflow()
    {
        var sink = new RecordingSink();
        var broker = new PermissionBroker(
            new FakeClock(Start),
            sink,
            maximumRulesPerProfile: 1);
        var context = Browsing();
        var first = Request(
            context,
            Site("https://first.test"),
            [PermissionAllowScope.Persistent]);
        var second = Request(
            context,
            Site("https://second.test"),
            [PermissionAllowScope.Persistent]);
        await broker.IngestAsync(first, default);
        await broker.RespondAsync(
            Response(
                first,
                PermissionDecision.Allow,
                PermissionAllowScope.Persistent),
            default);
        await broker.IngestAsync(second, default);

        var overflow = await broker.RespondAsync(
            Response(
                second,
                PermissionDecision.Allow,
                PermissionAllowScope.Persistent),
            default);
        var firstState = await broker.GetCurrentSiteStateAsync(
            context,
            first.RequestingSite,
            default);
        var secondState = await broker.GetCurrentSiteStateAsync(
            context,
            second.RequestingSite,
            default);

        Assert.Equal(ControllerErrorCode.Conflict, overflow.Error?.Code);
        Assert.Single(firstState.Value!.Rules);
        Assert.Empty(secondState.Value!.Rules);
        Assert.True(sink.Completions.Last().IsFailClosed);
    }

    [Fact]
    public async Task HostCompletionFailureRollsBackRuleReplacement()
    {
        var sink = new ToggleSink();
        var broker = new PermissionBroker(new FakeClock(Start), sink);
        var context = Browsing();
        var site = Site("https://rollback.test");
        var first = Request(
            context,
            site,
            [PermissionAllowScope.Persistent]);
        await broker.IngestAsync(first, default);
        var firstCompletion = await broker.RespondAsync(
            Response(
                first,
                PermissionDecision.Allow,
                PermissionAllowScope.Persistent),
            default);
        var originalRuleId = firstCompletion.Value!.AppliedRule!.Id;
        var replacement = Request(
            context,
            site,
            [PermissionAllowScope.Persistent]);
        await broker.IngestAsync(replacement, default);
        sink.FailCompletions = true;

        var failed = await broker.RespondAsync(
            Response(
                replacement,
                PermissionDecision.Allow,
                PermissionAllowScope.Persistent),
            default);
        var state = await broker.GetCurrentSiteStateAsync(context, site, default);

        Assert.Equal(ControllerErrorCode.Unavailable, failed.Error?.Code);
        var retained = Assert.Single(state.Value!.Rules);
        Assert.Equal(originalRuleId, retained.Id);
    }

    [Fact]
    public async Task PromptDispatchFailureCompletesHostAndConsumesToken()
    {
        var sink = new RecordingSink();
        var broker = new PermissionBroker(new FakeClock(Start), sink);
        broker.PromptRequested += (_, _) => throw new InvalidOperationException("UI failed");
        var request = Request(
            Browsing(),
            Site("https://prompt.test"),
            [PermissionAllowScope.Once]);

        var ingest = await broker.IngestAsync(request, default);
        var response = await broker.RespondAsync(
            Response(
                request,
                PermissionDecision.Deny,
                null),
            default);

        Assert.Equal(ControllerErrorCode.InternalFailure, ingest.Error?.Code);
        Assert.Equal(ControllerErrorCode.AlreadyHandled, response.Error?.Code);
        Assert.True(Assert.Single(sink.Completions).IsFailClosed);
    }

    private static PermissionBrokerRequest Request(
        BrowsingContext context,
        SiteIdentity site,
        IReadOnlyList<PermissionAllowScope> scopes,
        WebPermissionCapability capability = WebPermissionCapability.Camera,
        PermissionDecision defaultDecision = PermissionDecision.Deny,
        DateTimeOffset? expiresAtUtc = null) =>
        new(
            new RequestId(Guid.NewGuid()),
            new ResponseToken(Guid.NewGuid()),
            context,
            site,
            capability,
            scopes,
            PermissionDecision.Ask,
            defaultDecision,
            true,
            Start,
            expiresAtUtc ?? Start.AddMinutes(1));

    private static PermissionResponse Response(
        PermissionBrokerRequest request,
        PermissionDecision decision,
        PermissionAllowScope? scope) =>
        new(
            request.RequestId,
            request.ResponseToken,
            request.Context,
            decision,
            scope,
            Start.AddSeconds(1));

    private static SiteIdentity Site(string uri) =>
        SiteIdentity.Create(new Uri(uri)).Value!;

    private static BrowsingContext Browsing(
        BrowserProfileMode mode = BrowserProfileMode.Normal,
        ProfileId? profileId = null,
        BrowserSessionId? sessionId = null) =>
        new(
            new PrivacyContext(
                profileId ?? new ProfileId(Guid.NewGuid()),
                sessionId ?? new BrowserSessionId(Guid.NewGuid()),
                mode),
            new BrowserWindowId(Guid.NewGuid()),
            new BrowserTabId(Guid.NewGuid()),
            null);

    private sealed class FakeClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = utcNow;

        public void Advance(TimeSpan duration) => UtcNow = UtcNow.Add(duration);
    }

    private sealed class RecordingSink : IPermissionHostCompletionSink
    {
        private readonly ConcurrentQueue<PermissionHostCompletion> _completions = [];

        public IReadOnlyList<PermissionHostCompletion> Completions =>
            _completions.ToArray();

        public ValueTask<ControllerResult> CompleteAsync(
            PermissionHostCompletion completion,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _completions.Enqueue(completion);
            return ValueTask.FromResult(ControllerResult.Success());
        }
    }

    private sealed class ToggleSink : IPermissionHostCompletionSink
    {
        public bool FailCompletions { get; set; }

        public ValueTask<ControllerResult> CompleteAsync(
            PermissionHostCompletion completion,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.False(completion.SaveInProfile);
            return ValueTask.FromResult(
                FailCompletions
                    ? ControllerResult.Failure(ControllerError.Create(
                        ControllerErrorCode.Unavailable,
                        "error.test.sink_unavailable",
                        isRetryable: true))
                    : ControllerResult.Success());
        }
    }
}

internal static class BrowsingContextTestExtensions
{
    public static BrowsingContext WithSite(
        this BrowsingContext context,
        SiteIdentity site) =>
        context with { CurrentSite = site };
}
