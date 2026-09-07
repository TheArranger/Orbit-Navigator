using OrbitNavigator.Contracts.Accounts;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Sync;
using OrbitNavigator.Presentation.Accounts;
using OrbitNavigator.Presentation.Wpf;
using ContractState = OrbitNavigator.Contracts.Accounts.MyOrbitAccountConnectionState;
using PresentationState = OrbitNavigator.Presentation.Accounts.MyOrbitAccountConnectionState;

namespace OrbitNavigator.App.Accounts;

/// <summary>
/// Maps the credential-free account contract to the credential-free Settings
/// presentation. Protocol, loopback, token, DPAPI, and storage work stays in Sync.
/// </summary>
public sealed class MyOrbitAccountSettingsAdapter
{
    private readonly IMyOrbitAccountConnectionController _controller;
    private readonly IClock _clock;
    private readonly CancellationToken _applicationLifetime;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<MyOrbitAccountSettingsControl, Func<BrowsingContext?>> _contextProviders = [];
    private IReadOnlyList<MyOrbitLinkedDevice> _devices = [];
    private MyOrbitLinkAttemptId? _pendingAttempt;
    private DateTimeOffset? _linkStartedAtUtc;
    private DateTimeOffset? _linkExpiresAtUtc;

    public MyOrbitAccountSettingsAdapter(
        IMyOrbitAccountConnectionController controller,
        IClock clock,
        CancellationToken applicationLifetime = default)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _applicationLifetime = applicationLifetime;
    }

    public async Task AttachAsync(
        MyOrbitAccountSettingsControl control,
        Func<BrowsingContext?> currentContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(currentContext);
        control.IntentRequested -= OnIntentRequested;
        control.IntentRequested += OnIntentRequested;
        _contextProviders[control] = currentContext;
        var context = currentContext();
        if (context is not { IsStructurallyValid: true })
        {
            control.Render(UnavailableState(
                control.CurrentState?.Privacy,
                "My Orbit account status is unavailable because this browser tab is not ready."));
            return;
        }

        control.Render(LoadingState(context.Privacy, control.CurrentState?.Revision ?? 0));
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _applicationLifetime);
        var result = await QueryAsync(context, control.CurrentState!.Revision, linkedCancellation.Token);
        control.ApplyOperationResult(new(
            Guid.NewGuid(),
            result.Outcome,
            result.SafeMessage,
            result.RefreshedState));
    }

    public void Detach(MyOrbitAccountSettingsControl control)
    {
        ArgumentNullException.ThrowIfNull(control);
        control.IntentRequested -= OnIntentRequested;
        _contextProviders.Remove(control);
    }

    private async void OnIntentRequested(
        object? sender,
        MyOrbitAccountSettingsIntentRequestedEventArgs args)
    {
        if (sender is not MyOrbitAccountSettingsControl control ||
            !_contextProviders.TryGetValue(control, out var currentContext))
        {
            return;
        }

        MyOrbitAccountOperationResult result;
        try
        {
            result = await ExecuteAsync(args.Intent, currentContext());
        }
        catch (OperationCanceledException)
        {
            result = new(
                args.Intent.StableIntentId,
                MyOrbitAccountOperationOutcome.Failed,
                "The My Orbit account operation was cancelled.");
        }
        catch
        {
            result = new(
                args.Intent.StableIntentId,
                MyOrbitAccountOperationOutcome.Failed,
                "The My Orbit account operation could not be completed.");
        }

        control.ApplyOperationResult(result);
    }

    internal async Task<MyOrbitAccountOperationResult> ExecuteAsync(
        MyOrbitAccountSettingsIntent intent,
        BrowsingContext? context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        intent.Validate();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _applicationLifetime);
        cancellationToken = linkedCancellation.Token;
        if (context is not { IsStructurallyValid: true } || context.Privacy != intent.Privacy)
        {
            return new(
                intent.StableIntentId,
                MyOrbitAccountOperationOutcome.PolicyDenied,
                "This account action does not match the active browser profile.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return intent.Kind switch
            {
                MyOrbitAccountSettingsIntentKind.Query =>
                    await QueryAsync(context, intent.ExpectedRevision, cancellationToken, intent.StableIntentId),
                MyOrbitAccountSettingsIntentKind.BeginExternalLink =>
                    await BeginAsync(context, intent, cancellationToken),
                MyOrbitAccountSettingsIntentKind.CancelPendingLink =>
                    await CancelAsync(context, intent, cancellationToken),
                MyOrbitAccountSettingsIntentKind.DisconnectCurrentDevice =>
                    await DisconnectAsync(context, intent, cancellationToken),
                MyOrbitAccountSettingsIntentKind.QueryDevices =>
                    await QueryDevicesAsync(context, intent, cancellationToken),
                MyOrbitAccountSettingsIntentKind.RevokeDevice =>
                    await RevokeDeviceAsync(context, intent, cancellationToken),
                _ => new(
                    intent.StableIntentId,
                    MyOrbitAccountOperationOutcome.Failed,
                    "That My Orbit account action is not supported."),
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<MyOrbitAccountOperationResult> QueryAsync(
        BrowsingContext context,
        long expectedRevision,
        CancellationToken cancellationToken,
        Guid? stableIntentId = null)
    {
        var result = await _controller.QueryAsync(new(
            context,
            NewOperationId(),
            new MyOrbitAccountRevision(expectedRevision)), cancellationToken);
        if (!result.IsSuccess)
        {
            return Failure(
                stableIntentId ?? Guid.NewGuid(),
                result.Error!,
                ErrorState(context.Privacy, expectedRevision, result.Error!));
        }

        var state = Project(result.Value!);
        return new(
            stableIntentId ?? Guid.NewGuid(),
            MyOrbitAccountOperationOutcome.Accepted,
            SafeStatusCopy(result.Value!),
            state);
    }

    private async Task<MyOrbitAccountOperationResult> BeginAsync(
        BrowsingContext context,
        MyOrbitAccountSettingsIntent intent,
        CancellationToken cancellationToken)
    {
        var startedAt = _clock.UtcNow;
        var result = await _controller.BeginExternalLinkAsync(new(
            context,
            NewOperationId(),
            new MyOrbitAccountRevision(intent.ExpectedRevision),
            SafeDeviceDisplayName()), cancellationToken);
        if (!result.IsSuccess)
        {
            return await FailureWithRefreshAsync(context, intent, result.Error!, cancellationToken);
        }

        _pendingAttempt = result.Value!.AttemptId;
        _linkStartedAtUtc = startedAt;
        _linkExpiresAtUtc = result.Value.ExpiresAtUtc;
        return Accepted(intent, result.Value.Status,
            "Your default browser opened. Finish linking there, then refresh this status.");
    }

    private async Task<MyOrbitAccountOperationResult> CancelAsync(
        BrowsingContext context,
        MyOrbitAccountSettingsIntent intent,
        CancellationToken cancellationToken)
    {
        if (_pendingAttempt is not { IsEmpty: false } attempt)
        {
            return await StaleWithRefreshAsync(context, intent, cancellationToken);
        }

        var result = await _controller.CancelPendingLinkAsync(new(
            context,
            NewOperationId(),
            new MyOrbitAccountRevision(intent.ExpectedRevision),
            attempt), cancellationToken);
        if (!result.IsSuccess)
        {
            return await FailureWithRefreshAsync(context, intent, result.Error!, cancellationToken);
        }

        ClearPendingAttempt();
        return Accepted(intent, result.Value!, "My Orbit account linking was cancelled.");
    }

    private async Task<MyOrbitAccountOperationResult> DisconnectAsync(
        BrowsingContext context,
        MyOrbitAccountSettingsIntent intent,
        CancellationToken cancellationToken)
    {
        var result = await _controller.DisconnectCurrentDeviceAsync(new(
            context,
            NewOperationId(),
            new MyOrbitAccountRevision(intent.ExpectedRevision)), cancellationToken);
        if (!result.IsSuccess)
        {
            return await FailureWithRefreshAsync(context, intent, result.Error!, cancellationToken);
        }

        _devices = [];
        ClearPendingAttempt();
        return Accepted(intent, result.Value!, "This device is disconnected from My Orbit. Local browsing remains available.");
    }

    private async Task<MyOrbitAccountOperationResult> QueryDevicesAsync(
        BrowsingContext context,
        MyOrbitAccountSettingsIntent intent,
        CancellationToken cancellationToken)
    {
        var result = await _controller.QueryDevicesAsync(new(
            context,
            NewOperationId(),
            new MyOrbitAccountRevision(intent.ExpectedRevision)), cancellationToken);
        if (!result.IsSuccess)
        {
            return await FailureWithRefreshAsync(context, intent, result.Error!, cancellationToken);
        }

        _devices = result.Value!.Devices;
        return Accepted(intent, result.Value.Status, "Linked devices refreshed.");
    }

    private async Task<MyOrbitAccountOperationResult> RevokeDeviceAsync(
        BrowsingContext context,
        MyOrbitAccountSettingsIntent intent,
        CancellationToken cancellationToken)
    {
        if (intent.DeviceId is not { IsEmpty: false } deviceId)
        {
            return new(intent.StableIntentId, MyOrbitAccountOperationOutcome.Failed,
                "Choose a linked device to revoke.");
        }

        var result = await _controller.RevokeDeviceAsync(new(
            context,
            NewOperationId(),
            new MyOrbitAccountRevision(intent.ExpectedRevision),
            deviceId), cancellationToken);
        if (!result.IsSuccess)
        {
            return await FailureWithRefreshAsync(context, intent, result.Error!, cancellationToken);
        }

        _devices = result.Value!.Devices;
        return Accepted(intent, result.Value.Status, "The selected My Orbit device link was revoked.");
    }

    private MyOrbitAccountOperationResult Accepted(
        MyOrbitAccountSettingsIntent intent,
        ContractState status,
        string safeMessage) => new(
            intent.StableIntentId,
            MyOrbitAccountOperationOutcome.Accepted,
            safeMessage,
            Project(status));

    private async Task<MyOrbitAccountOperationResult> FailureWithRefreshAsync(
        BrowsingContext context,
        MyOrbitAccountSettingsIntent intent,
        ControllerError error,
        CancellationToken cancellationToken)
    {
        if (error.Code is ControllerErrorCode.Conflict or ControllerErrorCode.StaleClient)
        {
            return await StaleWithRefreshAsync(context, intent, cancellationToken);
        }

        var refreshed = await _controller.QueryAsync(new(
            context,
            NewOperationId(),
            new MyOrbitAccountRevision(intent.ExpectedRevision)), cancellationToken);
        return Failure(
            intent.StableIntentId,
            error,
            refreshed.IsSuccess ? Project(refreshed.Value!) : null);
    }

    private async Task<MyOrbitAccountOperationResult> StaleWithRefreshAsync(
        BrowsingContext context,
        MyOrbitAccountSettingsIntent intent,
        CancellationToken cancellationToken)
    {
        var refreshed = await _controller.QueryAsync(new(
            context,
            NewOperationId(),
            new MyOrbitAccountRevision(intent.ExpectedRevision)), cancellationToken);
        return new(
            intent.StableIntentId,
            MyOrbitAccountOperationOutcome.Stale,
            "Account status changed. The latest status is shown; try the action again.",
            refreshed.IsSuccess ? Project(refreshed.Value!) : null);
    }

    private MyOrbitAccountSettingsPresentationState Project(ContractState state)
    {
        if (state.State != MyOrbitAccountConnectionStateKind.LinkPending)
        {
            ClearPendingAttempt();
        }

        var canRevoke = state.Capabilities.HasFlag(MyOrbitAccountCapabilities.RevokeDevice);
        var devices = _devices.Select(device => new MyOrbitDevicePresentation(
            device.DeviceId,
            device.DisplayName,
            device.IsCurrent,
            device.RegisteredAtUtc,
            device.LastSeenAtUtc,
            device.RevokedAtUtc is not null,
            device.RevokedAtUtc,
            canRevoke && !device.IsCurrent && device.RevokedAtUtc is null,
            canRevoke || device.IsCurrent || device.RevokedAtUtc is not null
                ? null
                : "Device revocation is unavailable for the current account state.")).ToArray();
        var capabilities = state.Privacy.IsPrivate
            ? new MyOrbitAccountSettingsCapabilities(false, false, false, false, false)
            : new(
                state.Capabilities.HasFlag(MyOrbitAccountCapabilities.BeginExternalLink),
                state.Capabilities.HasFlag(MyOrbitAccountCapabilities.CancelPendingLink),
                state.Capabilities.HasFlag(MyOrbitAccountCapabilities.DisconnectCurrentDevice),
                state.Capabilities.HasFlag(MyOrbitAccountCapabilities.QueryDevices),
                state.Capabilities.HasFlag(MyOrbitAccountCapabilities.RevokeDevice));
        return new MyOrbitAccountSettingsPresentationState(
            state.Privacy,
            state.Revision.Value,
            MapState(state.State),
            state.ProviderLabel,
            state.AccountLabel,
            SafeStatusCopy(state),
            state.State == MyOrbitAccountConnectionStateKind.LinkPending ? _linkStartedAtUtc ?? _clock.UtcNow : null,
            state.State == MyOrbitAccountConnectionStateKind.LinkPending ? _linkExpiresAtUtc : null,
            state.Privacy.IsPrivate ? [] : devices,
            capabilities).Validate();
    }

    private MyOrbitAccountSettingsPresentationState LoadingState(PrivacyContext privacy, long revision) => new(
        privacy,
        Math.Max(0, revision),
        PresentationState.Loading,
        "My Orbit",
        null,
        "Checking My Orbit account status...",
        null,
        null,
        [],
        new(false, false, false, false, false));

    private static MyOrbitAccountSettingsPresentationState UnavailableState(
        PrivacyContext? privacy,
        string message)
    {
        var safePrivacy = privacy is { IsStructurallyValid: true }
            ? privacy
            : new PrivacyContext(
                new ProfileId(Guid.NewGuid()),
                new BrowserSessionId(Guid.NewGuid()),
                BrowserProfileMode.Private);
        return new(
            safePrivacy,
            0,
            PresentationState.ProviderUnavailable,
            "My Orbit",
            null,
            message,
            null,
            null,
            [],
            new(false, false, false, false, false));
    }

    private MyOrbitAccountSettingsPresentationState ErrorState(
        PrivacyContext privacy,
        long revision,
        ControllerError error) => new(
            privacy,
            Math.Max(0, revision),
            error.Code == ControllerErrorCode.Unavailable
                ? PresentationState.ProviderUnavailable
                : PresentationState.Failed,
            "My Orbit",
            null,
            SafeErrorCopy(error),
            null,
            null,
            [],
            new(false, false, false, false, false));

    private static MyOrbitAccountOperationResult Failure(
        Guid stableIntentId,
        ControllerError error,
        MyOrbitAccountSettingsPresentationState? refreshedState)
    {
        var outcome = error.Code switch
        {
            ControllerErrorCode.Conflict or ControllerErrorCode.StaleClient => MyOrbitAccountOperationOutcome.Stale,
            ControllerErrorCode.PolicyDenied => MyOrbitAccountOperationOutcome.PolicyDenied,
            ControllerErrorCode.Unavailable => MyOrbitAccountOperationOutcome.ProviderUnavailable,
            _ => MyOrbitAccountOperationOutcome.Failed,
        };
        return new(stableIntentId, outcome, SafeErrorCopy(error), refreshedState);
    }

    private static PresentationState MapState(MyOrbitAccountConnectionStateKind state) => state switch
    {
        MyOrbitAccountConnectionStateKind.ProviderUnavailable => PresentationState.ProviderUnavailable,
        MyOrbitAccountConnectionStateKind.SignedOut => PresentationState.SignedOut,
        MyOrbitAccountConnectionStateKind.LinkPending => PresentationState.LinkPending,
        MyOrbitAccountConnectionStateKind.Connected => PresentationState.Connected,
        MyOrbitAccountConnectionStateKind.ReauthorizationRequired => PresentationState.ReauthorizationRequired,
        MyOrbitAccountConnectionStateKind.Revoked => PresentationState.Revoked,
        MyOrbitAccountConnectionStateKind.Failed => PresentationState.Failed,
        MyOrbitAccountConnectionStateKind.Loading => PresentationState.Loading,
        _ => PresentationState.Failed,
    };

    private static string SafeStatusCopy(ContractState state) => state.MessageKey switch
    {
        "account.link.pending" => "Waiting for My Orbit authorization in your default browser.",
        "account.link.connected" => "This browser is securely linked to My Orbit for account and device management only.",
        "account.link.cancelled" => "My Orbit account linking was cancelled.",
        "account.link.disconnected" => "This device is disconnected from My Orbit.",
        "account.link.current-device-revoked" => "This device's My Orbit account link was revoked.",
        "account.link.reauthorization-required" => "My Orbit requires authorization again before account management can continue.",
        "account.link.revocation-pending" => "Disconnect is pending and will be retried securely.",
        "account.link.browser-launch-failed" => "The default browser could not be opened for My Orbit linking.",
        "account.link.provider-unavailable" => "My Orbit account linking is currently unavailable. Local browsing still works.",
        "account.link.failed" or "account.link.callback-invalid" or "account.link.credential-save-failed" =>
            "My Orbit account linking did not complete. No browsing data was synced.",
        _ => state.State switch
        {
            MyOrbitAccountConnectionStateKind.ProviderUnavailable =>
                "My Orbit account linking is currently unavailable. Local browsing still works.",
            MyOrbitAccountConnectionStateKind.SignedOut =>
                "Orbit Navigator is not linked to My Orbit. Local browsing remains available.",
            MyOrbitAccountConnectionStateKind.LinkPending =>
                "Waiting for My Orbit authorization in your default browser.",
            MyOrbitAccountConnectionStateKind.Connected =>
                "This browser is linked to My Orbit for account and device management only.",
            MyOrbitAccountConnectionStateKind.ReauthorizationRequired =>
                "My Orbit requires authorization again.",
            MyOrbitAccountConnectionStateKind.Revoked =>
                "The My Orbit account link is revoked. Local browsing remains available.",
            _ => "My Orbit account status is unavailable. Local browsing remains available.",
        },
    };

    private static string SafeErrorCopy(ControllerError error) => error.MessageKey switch
    {
        "error.private.remote_operation_denied" => "My Orbit account operations are unavailable in private windows.",
        "account.link.provider-unavailable" => "My Orbit account linking is currently unavailable. Local browsing still works.",
        "account.link.browser-launch-failed" => "The default browser could not be opened for My Orbit linking.",
        "account.link.rate-limited" => "My Orbit is temporarily limiting requests. Try again later.",
        "account.link.reauthorization-required" => "My Orbit requires authorization again.",
        "account.link.cancelled" => "The My Orbit account operation was cancelled.",
        "account.link.revision-conflict" => "Account status changed. Refresh and try again.",
        "account.link.loopback-unavailable" or "account.link.callback-unavailable" =>
            "Orbit Navigator could not open its private local authorization callback. Try again.",
        _ => error.Code switch
        {
            ControllerErrorCode.PolicyDenied => "This My Orbit account action is not allowed in the current window.",
            ControllerErrorCode.Conflict or ControllerErrorCode.StaleClient => "Account status changed. Refresh and try again.",
            ControllerErrorCode.Unavailable => "My Orbit account linking is currently unavailable. Local browsing still works.",
            ControllerErrorCode.Cancelled => "The My Orbit account operation was cancelled.",
            _ => "The My Orbit account operation could not be completed.",
        },
    };

    private static SyncOperationId NewOperationId() => new(Guid.NewGuid());

    private static string SafeDeviceDisplayName() => "Windows PC";

    private void ClearPendingAttempt()
    {
        _pendingAttempt = null;
        _linkStartedAtUtc = null;
        _linkExpiresAtUtc = null;
    }
}
