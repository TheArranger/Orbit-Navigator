using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Privacy;
using OrbitNavigator.Privacy.Protection;
using Xunit;

namespace OrbitNavigator.Privacy.Tests.Protection;

public sealed class SiteProtectionControllerTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task StrictBaselineIsDefaultAndMatchingUsesExactOrigin()
    {
        var clock = new FakeClock(Start);
        var controller = new SiteProtectionController(clock);
        var context = Browsing();
        var site = Site("https://example.test/path");

        var initial = await controller.GetStateAsync(context, site, default);
        var relaxed = await controller.RelaxAsync(
            new ProtectionRelaxationIntent(
                context,
                site,
                ProtectionRelaxationDuration.Session),
            default);
        var sameOrigin = controller.Evaluate(
            context,
            Site("https://example.test/another"));
        var subdomain = controller.Evaluate(
            context,
            Site("https://sub.example.test"));

        Assert.True(initial.IsSuccess);
        Assert.Equal(SiteProtectionBaseline.Strict, initial.Value?.Baseline);
        Assert.Null(initial.Value?.EffectiveRelaxation);
        Assert.Equal(
            ProtectionEvaluationSource.StrictBaseline,
            initial.Value?.Source);
        Assert.True(relaxed.IsSuccess);
        Assert.Equal(
            ProtectionEvaluationSource.SessionException,
            sameOrigin.Value?.Source);
        Assert.Equal(
            ProtectionEvaluationSource.StrictBaseline,
            subdomain.Value?.Source);
    }

    [Fact]
    public async Task TemporaryExceptionExpiresFailClosedToStrictBaseline()
    {
        var clock = new FakeClock(Start);
        var controller = new SiteProtectionController(
            clock,
            temporaryDuration: TimeSpan.FromMinutes(2));
        var context = Browsing();
        var site = Site("https://temporary.test");

        var relaxed = await controller.RelaxAsync(
            new ProtectionRelaxationIntent(
                context,
                site,
                ProtectionRelaxationDuration.Temporary),
            default);

        Assert.True(relaxed.IsSuccess);
        Assert.Equal(Start.AddMinutes(2), relaxed.Value?.ExpiresAtUtc);
        Assert.Equal(
            ProtectionEvaluationSource.TemporaryException,
            controller.Evaluate(context, site).Value?.Source);

        clock.Advance(TimeSpan.FromMinutes(2));

        var evaluation = controller.Evaluate(context, site);
        var listed = await controller.ListExceptionsAsync(context.Privacy, default);
        Assert.Equal(
            ProtectionEvaluationSource.StrictBaseline,
            evaluation.Value?.Source);
        Assert.Empty(listed.Value!);
    }

    [Fact]
    public async Task SessionExceptionCannotCrossProfileSessionOrMode()
    {
        var clock = new FakeClock(Start);
        var controller = new SiteProtectionController(clock);
        var owner = Browsing();
        var otherProfile = Browsing();
        var otherSession = Browsing(
            BrowserProfileMode.Normal,
            owner.Privacy.ProfileId);
        var sameIdsPrivate = Browsing(
            BrowserProfileMode.Private,
            owner.Privacy.ProfileId,
            owner.Privacy.SessionId);
        var site = Site("https://isolated.test");

        await controller.RelaxAsync(
            new ProtectionRelaxationIntent(
                owner,
                site,
                ProtectionRelaxationDuration.Session),
            default);

        Assert.Equal(
            ProtectionEvaluationSource.SessionException,
            controller.Evaluate(owner, site).Value?.Source);
        Assert.Equal(
            ProtectionEvaluationSource.StrictBaseline,
            controller.Evaluate(otherProfile, site).Value?.Source);
        Assert.Equal(
            ProtectionEvaluationSource.StrictBaseline,
            controller.Evaluate(otherSession, site).Value?.Source);
        Assert.Equal(
            ProtectionEvaluationSource.StrictBaseline,
            controller.Evaluate(sameIdsPrivate, site).Value?.Source);
    }

    [Fact]
    public async Task PersistentExceptionCrossesNormalSessionsButNeverPrivateBoundary()
    {
        var clock = new FakeClock(Start);
        var controller = new SiteProtectionController(clock);
        var normal = Browsing();
        var laterNormal = Browsing(
            BrowserProfileMode.Normal,
            normal.Privacy.ProfileId);
        var privateContext = Browsing(
            BrowserProfileMode.Private,
            normal.Privacy.ProfileId);
        var site = Site("https://persistent.test");

        var created = await controller.RelaxAsync(
            new ProtectionRelaxationIntent(
                normal,
                site,
                ProtectionRelaxationDuration.Persistent),
            default);
        var privateAttempt = await controller.RelaxAsync(
            new ProtectionRelaxationIntent(
                privateContext,
                site,
                ProtectionRelaxationDuration.Persistent),
            default);

        Assert.True(created.IsSuccess);
        Assert.Equal(
            ProtectionEvaluationSource.PersistentException,
            controller.Evaluate(laterNormal, site).Value?.Source);
        Assert.Equal(
            ProtectionEvaluationSource.StrictBaseline,
            controller.Evaluate(privateContext, site).Value?.Source);
        Assert.False(privateAttempt.IsSuccess);
        Assert.Equal(ControllerErrorCode.PolicyDenied, privateAttempt.Error?.Code);
        Assert.Empty((await controller.ListExceptionsAsync(
            privateContext.Privacy,
            default)).Value!);
    }

    [Fact]
    public async Task RemovalRequiresVisibilityAndOwningProfile()
    {
        var controller = new SiteProtectionController(new FakeClock(Start));
        var owner = Browsing();
        var otherSession = Browsing(
            BrowserProfileMode.Normal,
            owner.Privacy.ProfileId);
        var created = await controller.RelaxAsync(
            new ProtectionRelaxationIntent(
                owner,
                Site("https://remove.test"),
                ProtectionRelaxationDuration.Session),
            default);

        var hiddenRemoval = await controller.RemoveExceptionAsync(
            new RemoveProtectionExceptionIntent(
                otherSession.Privacy,
                created.Value!.Id),
            default);
        var ownerRemoval = await controller.RemoveExceptionAsync(
            new RemoveProtectionExceptionIntent(
                owner.Privacy,
                created.Value.Id),
            default);

        Assert.Equal(ControllerErrorCode.NotFound, hiddenRemoval.Error?.Code);
        Assert.True(ownerRemoval.IsSuccess);
        Assert.Empty((await controller.ListExceptionsAsync(
            owner.Privacy,
            default)).Value!);
    }

    [Fact]
    public async Task SameRuleSlotIsReplacedAndBoundRemainsEnforcedConcurrently()
    {
        var controller = new SiteProtectionController(
            new FakeClock(Start),
            maximumExceptionsPerProfile: 4);
        var context = Browsing();
        var site = Site("https://replace.test");

        await controller.RelaxAsync(
            new ProtectionRelaxationIntent(
                context,
                site,
                ProtectionRelaxationDuration.Session),
            default);
        await controller.RelaxAsync(
            new ProtectionRelaxationIntent(
                context,
                site,
                ProtectionRelaxationDuration.Session),
            default);

        Assert.Single((await controller.ListExceptionsAsync(
            context.Privacy,
            default)).Value!);

        var attempts = Enumerable.Range(0, 12)
            .Select(index => controller.RelaxAsync(
                new ProtectionRelaxationIntent(
                    context,
                    Site($"https://site-{index}.test"),
                    ProtectionRelaxationDuration.Session),
                default).AsTask())
            .ToArray();
        var results = await Task.WhenAll(attempts);

        Assert.Equal(3, results.Count(result => result.IsSuccess));
        Assert.All(
            results.Where(result => !result.IsSuccess),
            result => Assert.Equal(ControllerErrorCode.Conflict, result.Error?.Code));
        Assert.Equal(
            4,
            (await controller.ListExceptionsAsync(
                context.Privacy,
                default)).Value!.Count);
    }

    [Fact]
    public void InvalidContextAndTargetProduceBlockingEvaluations()
    {
        var controller = new SiteProtectionController(new FakeClock(Start));
        var invalid = new BrowsingContext(
            new PrivacyContext(default, default, BrowserProfileMode.Normal),
            default,
            default,
            null);

        var invalidContext = controller.Evaluate(
            invalid,
            Site("https://blocked.test"));
        var invalidTarget = controller.Evaluate(Browsing(), null!);

        Assert.Equal(
            ProtectionNavigationDisposition.Block,
            invalidContext.Value?.Disposition);
        Assert.Equal(
            ProtectionEvaluationSource.InvalidContext,
            invalidContext.Value?.Source);
        Assert.Equal(
            ProtectionNavigationDisposition.Block,
            invalidTarget.Value?.Disposition);
        Assert.Equal(
            ProtectionEvaluationSource.InvalidTarget,
            invalidTarget.Value?.Source);
    }

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
}
