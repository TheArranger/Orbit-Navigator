using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Privacy;
using OrbitNavigator.Presentation.Protection;
using Xunit;

namespace OrbitNavigator.Presentation.Tests;

public sealed class SiteProtectionPresenterTests
{
    [Fact]
    public async Task RetryUsesSessionRelaxationAndRequestsReloadAfterSuccess()
    {
        var controller = new FakeProtectionController();
        var presenter = new SiteProtectionPresenter(controller);
        var context = TestContexts.Browsing();
        var site = TestContexts.Site();
        ProtectionReloadRequestedEventArgs? reload = null;
        presenter.ReloadRequested += (_, args) => reload = args;
        await presenter.LoadAsync(context, site);

        await presenter.RetryWithStandardProtectionAsync();

        Assert.Equal(ProtectionRelaxationDuration.Session, controller.LastRelaxation?.Duration);
        Assert.Equal(ProtectionRelaxationDuration.Session, presenter.State.EffectiveRelaxation);
        Assert.Same(context, reload?.Context);
        Assert.Same(site, reload?.Site);
    }

    [Fact]
    public async Task PrivateContextNeverOffersPersistentRelaxationToController()
    {
        var controller = new FakeProtectionController();
        var presenter = new SiteProtectionPresenter(controller);
        await presenter.LoadAsync(
            TestContexts.Browsing(BrowserProfileMode.Private),
            TestContexts.Site());

        await presenter.RelaxAsync(ProtectionRelaxationDuration.Persistent);

        Assert.Null(controller.LastRelaxation);
        Assert.False(presenter.State.CanPersist);
        Assert.Equal(ControllerErrorCode.PolicyDenied, presenter.State.Failure?.Code);
    }

    private sealed class FakeProtectionController : ISiteProtectionController
    {
        public ProtectionRelaxationIntent? LastRelaxation { get; private set; }

        public ValueTask<ControllerResult<SiteProtectionState>> GetStateAsync(
            BrowsingContext context,
            SiteIdentity site,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ControllerResult<SiteProtectionState>.Success(
                new SiteProtectionState(
                    context,
                    site,
                    SiteProtectionBaseline.Strict,
                    null,
                    ProtectionEvaluationSource.StrictBaseline,
                    null)));

        public ValueTask<ControllerResult<ProtectionException>> RelaxAsync(
            ProtectionRelaxationIntent intent,
            CancellationToken cancellationToken)
        {
            LastRelaxation = intent;
            return ValueTask.FromResult(ProtectionException.Create(
                intent.Context,
                new ProtectionExceptionId(intent.Context.Privacy.ProfileId, Guid.NewGuid()),
                intent.Site,
                intent.Duration,
                DateTimeOffset.UtcNow,
                intent.Duration == ProtectionRelaxationDuration.Temporary
                    ? DateTimeOffset.UtcNow.AddMinutes(10)
                    : null));
        }

        public ValueTask<ControllerResult<IReadOnlyList<ProtectionException>>> ListExceptionsAsync(
            PrivacyContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ControllerResult<IReadOnlyList<ProtectionException>>.Success(
                Array.Empty<ProtectionException>()));

        public ValueTask<ControllerResult> RemoveExceptionAsync(
            RemoveProtectionExceptionIntent intent,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ControllerResult.Success());
    }
}
