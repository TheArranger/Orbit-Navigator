using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Privacy;
using OrbitNavigator.Presentation.Permissions;
using Xunit;

namespace OrbitNavigator.Presentation.Tests;

public sealed class PermissionPromptPresenterTests
{
    [Theory]
    [InlineData(WebPermissionCapability.Popups, "ui.permission.capability.popups")]
    [InlineData(WebPermissionCapability.Autoplay, "ui.permission.capability.autoplay")]
    public async Task ContentControlsExposeSafeDefaultAndFullResponseLifecycle(
        WebPermissionCapability capability,
        string expectedLabel)
    {
        var broker = new FakePermissionBroker();
        using var presenter = new PermissionPromptPresenter(broker);
        var prompt = Prompt(capability, DateTimeOffset.UtcNow.AddMinutes(1));

        broker.Raise(prompt);

        Assert.Equal(expectedLabel, presenter.State.CapabilityLabelKey);
        Assert.True(presenter.State.IsOpen);
        var safest = Assert.Single(presenter.State.Choices, choice => choice.IsSafestChoice);
        Assert.Equal(PermissionDecision.Deny, safest.Decision);
        await presenter.RespondAsync(PermissionDecision.Allow, PermissionAllowScope.Session);
        Assert.Equal(prompt.RequestId, Assert.Single(broker.Responses).RequestId);
        Assert.False(presenter.State.IsOpen);
        Assert.Equal("ui.permission.allowed", presenter.State.Announcement?.MessageKey);
    }

    [Fact]
    public async Task ExpiredPromptCannotCallBroker()
    {
        var broker = new FakePermissionBroker();
        using var presenter = new PermissionPromptPresenter(broker);
        broker.Raise(Prompt(WebPermissionCapability.Camera, DateTimeOffset.UtcNow.AddSeconds(-1)));

        await presenter.RespondAsync(PermissionDecision.Allow, PermissionAllowScope.Once);

        Assert.Empty(broker.Responses);
        Assert.True(presenter.State.IsExpired);
        Assert.Equal(ControllerErrorCode.Expired, presenter.State.Failure?.Code);
    }

    [Fact]
    public async Task LaterPromptWaitsUntilActivePromptHasBeenResolved()
    {
        var broker = new FakePermissionBroker();
        using var presenter = new PermissionPromptPresenter(broker);
        var first = Prompt(WebPermissionCapability.Camera, DateTimeOffset.UtcNow.AddMinutes(1));
        var second = Prompt(WebPermissionCapability.Microphone, DateTimeOffset.UtcNow.AddMinutes(1));

        broker.Raise(first);
        broker.Raise(second);

        Assert.Equal(first.RequestId, presenter.State.Prompt?.RequestId);
        Assert.Equal(1, presenter.State.QueuedPromptCount);
        await presenter.RespondAsync(PermissionDecision.Deny, null);

        Assert.Equal(second.RequestId, presenter.State.Prompt?.RequestId);
        Assert.True(presenter.State.IsOpen);
        Assert.Equal(0, presenter.State.QueuedPromptCount);
    }

    [Fact]
    public async Task AlreadyHandledResponseClosesConsumedPromptWithoutExposingBrokerKey()
    {
        var broker = new FakePermissionBroker
        {
            ResponseError = ControllerError.Create(
                ControllerErrorCode.AlreadyHandled,
                "error.permission.response_replayed"),
        };
        using var presenter = new PermissionPromptPresenter(broker);
        broker.Raise(Prompt(WebPermissionCapability.Camera, DateTimeOffset.UtcNow.AddMinutes(1)));

        await presenter.RespondAsync(PermissionDecision.Deny, null);

        Assert.False(presenter.State.IsOpen);
        Assert.Null(presenter.State.Failure);
        Assert.Equal("ui.permission.request_resolved", presenter.State.Announcement?.MessageKey);
        Assert.Single(broker.Responses);
    }

    [Theory]
    [InlineData(ControllerErrorCode.InternalFailure, "ui.permission.response_failed")]
    [InlineData(ControllerErrorCode.Unavailable, "ui.permission.response_unavailable")]
    [InlineData(ControllerErrorCode.PolicyDenied, "ui.permission.response_not_allowed")]
    public async Task BrokerFailureClosesTerminallyWithSafePresentationCopy(
        ControllerErrorCode code,
        string expectedSafeKey)
    {
        var broker = new FakePermissionBroker
        {
            ResponseError = ControllerError.Create(code, "error.permission.internal_raw_key"),
        };
        using var presenter = new PermissionPromptPresenter(broker);
        broker.Raise(Prompt(WebPermissionCapability.Camera, DateTimeOffset.UtcNow.AddMinutes(1)));

        await presenter.RespondAsync(PermissionDecision.Allow, PermissionAllowScope.Once);

        Assert.False(presenter.State.IsOpen);
        Assert.False(presenter.State.IsBusy);
        Assert.Null(presenter.State.Prompt);
        Assert.Empty(presenter.State.Choices);
        Assert.Null(presenter.State.Failure);
        Assert.Equal(expectedSafeKey, presenter.State.Announcement?.MessageKey);
        Assert.DoesNotContain("internal_raw_key", presenter.State.Announcement?.MessageKey);
        Assert.Single(broker.Responses);
    }

    [Fact]
    public async Task PendingResponseMakesSubsequentSubmissionInert()
    {
        var pending = new TaskCompletionSource<ControllerResult<PermissionHostCompletion>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var broker = new FakePermissionBroker { PendingResponse = pending };
        using var presenter = new PermissionPromptPresenter(broker);
        broker.Raise(Prompt(WebPermissionCapability.Camera, DateTimeOffset.UtcNow.AddMinutes(1)));

        var first = presenter.RespondAsync(PermissionDecision.Deny, null).AsTask();
        Assert.True(presenter.State.IsBusy);
        await presenter.RespondAsync(PermissionDecision.Allow, PermissionAllowScope.Once);
        Assert.Single(broker.Responses);

        var response = broker.Responses[0];
        pending.SetResult(ControllerResult<PermissionHostCompletion>.Success(
            PermissionHostCompletion.FromAcceptedResponse(
                response.RequestId,
                response.Context.TabId,
                WebPermissionCapability.Camera,
                response.Decision)));
        await first;
        Assert.False(presenter.State.IsOpen);
    }

    private static PermissionPromptState Prompt(
        WebPermissionCapability capability,
        DateTimeOffset expiresAtUtc)
    {
        var context = TestContexts.Browsing();
        var site = TestContexts.Site();
        return new PermissionPromptState(
            new RequestId(Guid.NewGuid()),
            new ResponseToken(Guid.NewGuid()),
            context,
            site,
            site.DisplayOrigin,
            capability,
            [PermissionAllowScope.Once, PermissionAllowScope.Session],
            PermissionDecision.Deny,
            PermissionDecision.Deny,
            expiresAtUtc);
    }

    private sealed class FakePermissionBroker : IPermissionBroker
    {
        public event EventHandler<PermissionPromptEventArgs>? PromptRequested;

        public List<PermissionResponse> Responses { get; } = [];

        public ControllerError? ResponseError { get; init; }

        public TaskCompletionSource<ControllerResult<PermissionHostCompletion>>? PendingResponse { get; init; }

        public void Raise(PermissionPromptState prompt) =>
            PromptRequested?.Invoke(this, new PermissionPromptEventArgs(prompt));

        public ValueTask<ControllerResult<PermissionPromptState>> IngestAsync(
            PermissionBrokerRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ControllerResult<PermissionPromptState>.Failure(Error()));

        public ValueTask<ControllerResult<PermissionHostCompletion>> RespondAsync(
            PermissionResponse response,
            CancellationToken cancellationToken)
        {
            Responses.Add(response);
            if (PendingResponse is not null)
            {
                return new ValueTask<ControllerResult<PermissionHostCompletion>>(PendingResponse.Task);
            }
            if (ResponseError is not null)
            {
                return ValueTask.FromResult(
                    ControllerResult<PermissionHostCompletion>.Failure(ResponseError));
            }
            return ValueTask.FromResult(ControllerResult<PermissionHostCompletion>.Success(
                PermissionHostCompletion.FromAcceptedResponse(
                    response.RequestId,
                    response.Context.TabId,
                    WebPermissionCapability.Popups,
                    response.Decision)));
        }

        public ValueTask<ControllerResult<SitePermissionState>> GetCurrentSiteStateAsync(
            BrowsingContext context,
            SiteIdentity site,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ControllerResult<SitePermissionState>.Failure(Error()));

        public ValueTask<ControllerResult> ResetAsync(
            ResetPermissionRuleIntent intent,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ControllerResult.Failure(Error()));

        private static ControllerError Error() => ControllerError.Create(
            ControllerErrorCode.NotSupported,
            "error.test.not_used");
    }
}
