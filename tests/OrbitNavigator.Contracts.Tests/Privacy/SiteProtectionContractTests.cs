using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Privacy;
using Xunit;

namespace OrbitNavigator.Contracts.Tests.Privacy;

public sealed class SiteProtectionContractTests
{
    [Fact]
    public void StrictBaselineWithoutExceptionAllowsNavigation()
    {
        var site = Site("https://example.test/path");

        var evaluation = ProtectionEvaluation.AllowUnderStrictBaseline(site);

        Assert.Equal(ProtectionNavigationDisposition.Allow, evaluation.Disposition);
        Assert.Equal(SiteProtectionBaseline.Strict, evaluation.Baseline);
        Assert.Equal(ProtectionEvaluationSource.StrictBaseline, evaluation.Source);
        Assert.Null(evaluation.ExceptionId);
    }

    [Theory]
    [InlineData(ProtectionEvaluationSource.InvalidContext)]
    [InlineData(ProtectionEvaluationSource.InvalidTarget)]
    [InlineData(ProtectionEvaluationSource.EvaluatorFailure)]
    public void OnlyFailureSourcesCreateBlockingEvaluation(ProtectionEvaluationSource source)
    {
        var evaluation = ProtectionEvaluation.BlockFailure(source);

        Assert.Equal(ProtectionNavigationDisposition.Block, evaluation.Disposition);
        Assert.Equal(0, (int)evaluation.Disposition);
    }

    [Fact]
    public void TemporaryExceptionIsSessionBoundAndRequiresFutureExpiry()
    {
        var context = Browsing(BrowserProfileMode.Normal);
        var now = DateTimeOffset.UtcNow;
        var id = new ProtectionExceptionId(context.Privacy.ProfileId, Guid.NewGuid());

        var invalid = ProtectionException.Create(
            context,
            id,
            Site("https://example.test"),
            ProtectionRelaxationDuration.Temporary,
            now,
            now);
        var valid = ProtectionException.Create(
            context,
            id,
            Site("https://example.test"),
            ProtectionRelaxationDuration.Temporary,
            now,
            now.AddMinutes(10));

        Assert.False(invalid.IsSuccess);
        Assert.True(valid.IsSuccess);
        Assert.Equal(context.Privacy.SessionId, valid.Value?.SessionId);
        Assert.Equal(ProtectionRelaxationDuration.Temporary, valid.Value?.EffectiveDuration);
        Assert.Equal(ProtectionExceptionSource.TemporaryUserChoice, valid.Value?.Source);
    }

    [Fact]
    public void PrivateContextRejectsPersistentExceptionAndCannotSeeNormalPersistentState()
    {
        var normal = Browsing(BrowserProfileMode.Normal);
        var privateContext = Browsing(
            BrowserProfileMode.Private,
            normal.Privacy.ProfileId,
            new BrowserSessionId(Guid.NewGuid()));
        var id = new ProtectionExceptionId(normal.Privacy.ProfileId, Guid.NewGuid());
        var normalPersistent = ProtectionException.Create(
            normal,
            id,
            Site("https://example.test"),
            ProtectionRelaxationDuration.Persistent,
            DateTimeOffset.UtcNow);
        var privatePersistent = ProtectionException.Create(
            privateContext,
            id,
            Site("https://example.test"),
            ProtectionRelaxationDuration.Persistent,
            DateTimeOffset.UtcNow);

        Assert.True(normalPersistent.IsSuccess);
        Assert.False(normalPersistent.Value?.IsVisibleTo(privateContext.Privacy));
        Assert.False(privatePersistent.IsSuccess);
        Assert.Equal(ControllerErrorCode.PolicyDenied, privatePersistent.Error?.Code);
    }

    [Fact]
    public void SessionExceptionCannotCrossSessionBoundary()
    {
        var first = Browsing(BrowserProfileMode.Normal);
        var second = Browsing(
            BrowserProfileMode.Normal,
            first.Privacy.ProfileId,
            new BrowserSessionId(Guid.NewGuid()));
        var value = ProtectionException.Create(
            first,
            new ProtectionExceptionId(first.Privacy.ProfileId, Guid.NewGuid()),
            Site("https://example.test"),
            ProtectionRelaxationDuration.Session,
            DateTimeOffset.UtcNow).Value!;

        Assert.True(value.IsVisibleTo(first.Privacy));
        Assert.False(value.IsVisibleTo(second.Privacy));
    }

    private static SiteIdentity Site(string uri) => SiteIdentity.Create(new Uri(uri)).Value!;

    private static BrowsingContext Browsing(
        BrowserProfileMode mode,
        ProfileId? profile = null,
        BrowserSessionId? session = null) =>
        new(
            new PrivacyContext(
                profile ?? new ProfileId(Guid.NewGuid()),
                session ?? new BrowserSessionId(Guid.NewGuid()),
                mode),
            new BrowserWindowId(Guid.NewGuid()),
            new BrowserTabId(Guid.NewGuid()),
            null);
}
