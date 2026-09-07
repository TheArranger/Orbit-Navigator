using OrbitNavigator.App.Accounts;
using OrbitNavigator.Contracts.Accounts;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Sync;
using OrbitNavigator.Presentation.Accounts;
using Xunit;

namespace OrbitNavigator.App.Tests.Accounts;

public sealed class MyOrbitAccountSettingsAdapterTests
{
    [Fact]
    public async Task MismatchedUiPrivacyIsDeniedBeforeController()
    {
        var controller = new FakeController();
        var adapter = new MyOrbitAccountSettingsAdapter(controller, new FixedClock());
        var context = Context(BrowserProfileMode.Normal);
        var differentPrivacy = new PrivacyContext(
            new ProfileId(Guid.NewGuid()),
            new BrowserSessionId(Guid.NewGuid()),
            BrowserProfileMode.Normal);
        var intent = new MyOrbitAccountSettingsIntent(
            Guid.NewGuid(),
            differentPrivacy,
            0,
            MyOrbitAccountSettingsIntentKind.Query);

        var result = await adapter.ExecuteAsync(intent, context);

        Assert.Equal(MyOrbitAccountOperationOutcome.PolicyDenied, result.Outcome);
        Assert.Equal(0, controller.CallCount);
    }

    [Fact]
    public async Task PendingAttemptRemainsHostOnlyAndIsUsedByCancel()
    {
        var context = Context(BrowserProfileMode.Normal);
        var attempt = new MyOrbitLinkAttemptId(Guid.NewGuid());
        var controller = new FakeController
        {
            BeginResult = ControllerResult<MyOrbitExternalLinkReceipt>.Success(new(
                attempt,
                FixedClock.Now.AddMinutes(5),
                State(context.Privacy, 1, MyOrbitAccountConnectionStateKind.LinkPending,
                    MyOrbitAccountCapabilities.CancelPendingLink))),
            CancelResult = ControllerResult<OrbitNavigator.Contracts.Accounts.MyOrbitAccountConnectionState>.Success(
                State(context.Privacy, 2, MyOrbitAccountConnectionStateKind.SignedOut,
                    MyOrbitAccountCapabilities.BeginExternalLink)),
        };
        var adapter = new MyOrbitAccountSettingsAdapter(controller, new FixedClock());

        var begin = await adapter.ExecuteAsync(new(
            Guid.NewGuid(), context.Privacy, 0, MyOrbitAccountSettingsIntentKind.BeginExternalLink), context);
        var cancel = await adapter.ExecuteAsync(new(
            Guid.NewGuid(), context.Privacy, 1, MyOrbitAccountSettingsIntentKind.CancelPendingLink), context);

        Assert.Equal(MyOrbitAccountOperationOutcome.Accepted, begin.Outcome);
        Assert.Equal(MyOrbitAccountOperationOutcome.Accepted, cancel.Outcome);
        Assert.Equal(attempt, controller.CancelAttempt);
        Assert.Equal("Windows PC", controller.DeviceDisplayName);
        Assert.Null(typeof(MyOrbitAccountSettingsIntent).GetProperty("AttemptId"));
    }

    private static BrowsingContext Context(BrowserProfileMode mode)
    {
        var privacy = new PrivacyContext(
            new ProfileId(Guid.NewGuid()),
            new BrowserSessionId(Guid.NewGuid()),
            mode);
        return new BrowsingContext(
            privacy,
            new BrowserWindowId(Guid.NewGuid()),
            new BrowserTabId(Guid.NewGuid()),
            null);
    }

    private static OrbitNavigator.Contracts.Accounts.MyOrbitAccountConnectionState State(
        PrivacyContext privacy,
        long revision,
        MyOrbitAccountConnectionStateKind state,
        MyOrbitAccountCapabilities capabilities) => new(
            privacy,
            new MyOrbitAccountRevision(revision),
            state,
            "My Orbit",
            null,
            state == MyOrbitAccountConnectionStateKind.LinkPending
                ? [MyOrbitAccountScope.LinkAccount, MyOrbitAccountScope.ManageLinkedDevices]
                : [],
            state == MyOrbitAccountConnectionStateKind.LinkPending ? "account.link.pending" : null,
            null,
            null,
            null,
            capabilities);

    private sealed class FixedClock : IClock
    {
        public static DateTimeOffset Now { get; } = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class FakeController : IMyOrbitAccountConnectionController
    {
        public int CallCount { get; private set; }
        public string? DeviceDisplayName { get; private set; }
        public MyOrbitLinkAttemptId? CancelAttempt { get; private set; }
        public ControllerResult<MyOrbitExternalLinkReceipt>? BeginResult { get; init; }
        public ControllerResult<OrbitNavigator.Contracts.Accounts.MyOrbitAccountConnectionState>? CancelResult { get; init; }

        public ValueTask<ControllerResult<OrbitNavigator.Contracts.Accounts.MyOrbitAccountConnectionState>> QueryAsync(
            MyOrbitAccountQuery query,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult(ControllerResult<OrbitNavigator.Contracts.Accounts.MyOrbitAccountConnectionState>.Failure(
                ControllerError.Create(ControllerErrorCode.Unavailable, "account.link.provider-unavailable")));
        }

        public ValueTask<ControllerResult<MyOrbitExternalLinkReceipt>> BeginExternalLinkAsync(
            BeginMyOrbitExternalLinkIntent intent,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            DeviceDisplayName = intent.DeviceDisplayName;
            return ValueTask.FromResult(BeginResult!);
        }

        public ValueTask<ControllerResult<OrbitNavigator.Contracts.Accounts.MyOrbitAccountConnectionState>> CancelPendingLinkAsync(
            CancelMyOrbitExternalLinkIntent intent,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            CancelAttempt = intent.AttemptId;
            return ValueTask.FromResult(CancelResult!);
        }

        public ValueTask<ControllerResult<OrbitNavigator.Contracts.Accounts.MyOrbitAccountConnectionState>> DisconnectCurrentDeviceAsync(
            DisconnectMyOrbitCurrentDeviceIntent intent,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<ControllerResult<MyOrbitDeviceCollection>> QueryDevicesAsync(
            QueryMyOrbitDevicesIntent intent,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<ControllerResult<MyOrbitDeviceCollection>> RevokeDeviceAsync(
            RevokeMyOrbitDeviceIntent intent,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
