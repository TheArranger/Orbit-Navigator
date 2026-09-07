using System.Security.Cryptography;
using System.Text;
using OrbitNavigator.Contracts.Accounts;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Sync;

namespace OrbitNavigator.Sync.Accounts;

/// <summary>
/// Delegated account-link and device-management lifecycle only. This type does
/// not implement sync authorization or enable any browsing-data transport.
/// </summary>
public sealed class MyOrbitAccountConnectionController : IMyOrbitAccountConnectionController, IAsyncDisposable
{
    private static readonly MyOrbitAccountScope[] LinkScopes =
        [MyOrbitAccountScope.LinkAccount, MyOrbitAccountScope.ManageLinkedDevices];
    private readonly IMyOrbitAuthorizationProtocol? _protocol;
    private readonly IMyOrbitCredentialVault? _vault;
    private readonly IMyOrbitSystemBrowserLauncher? _launcher;
    private readonly IMyOrbitLoopbackListenerFactory? _loopback;
    private readonly IClock? _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private StoredMyOrbitCredential? _credential;
    private SensitiveUtf8Buffer? _accessCredential;
    private DateTimeOffset _accessExpiresAtUtc;
    private MyOrbitAccountConnectionState? _status;
    private PendingLink? _pending;
    private Task? _pendingCompletion;
    private long _revision;
    private bool _disposed;
    private int _disposeStarted;

    private MyOrbitAccountConnectionController()
    {
    }

    internal MyOrbitAccountConnectionController(
        IMyOrbitAuthorizationProtocol protocol,
        IMyOrbitCredentialVault vault,
        IMyOrbitSystemBrowserLauncher launcher,
        IMyOrbitLoopbackListenerFactory loopback,
        IClock clock)
    {
        _protocol = protocol ?? throw new ArgumentNullException(nameof(protocol));
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _loopback = loopback ?? throw new ArgumentNullException(nameof(loopback));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public static IMyOrbitAccountConnectionController Create(
        MyOrbitAccountProviderOptions options,
        IProfileStorage profileStorage,
        IWindowsKeyProtection keyProtection,
        IClock clock,
        IMyOrbitSystemBrowserLauncher systemBrowserLauncher)
    {
        ArgumentNullException.ThrowIfNull(options);
        var protocol = new MyOrbitAuthorizationProtocol(options, clock);
        var vault = new DpapiMyOrbitCredentialVault(profileStorage, keyProtection);
        return new MyOrbitAccountConnectionController(
            protocol,
            vault,
            systemBrowserLauncher,
            new TcpMyOrbitLoopbackListenerFactory(),
            clock);
    }

    public static IMyOrbitAccountConnectionController ProviderUnavailable() => new MyOrbitAccountConnectionController();

    public ValueTask<ControllerResult<MyOrbitAccountConnectionState>> QueryAsync(
        MyOrbitAccountQuery query,
        CancellationToken cancellationToken = default) =>
        query?.Context is not { IsStructurallyValid: true }
            ? ValueTask.FromResult(Invalid<MyOrbitAccountConnectionState>())
            : ExecuteOperationAsync(
                query.Context,
                query.OperationId,
                context => QueryCoreAsync(context, query.ExpectedRevision, cancellationToken),
                cancellationToken);

    public ValueTask<ControllerResult<MyOrbitExternalLinkReceipt>> BeginExternalLinkAsync(
        BeginMyOrbitExternalLinkIntent intent,
        CancellationToken cancellationToken = default) =>
        intent?.Context is not { IsStructurallyValid: true }
            ? ValueTask.FromResult(Invalid<MyOrbitExternalLinkReceipt>())
            : ExecuteOperationAsync(
                intent.Context,
                intent.OperationId,
                context => BeginCoreAsync(context, intent, cancellationToken),
                cancellationToken);

    public ValueTask<ControllerResult<MyOrbitAccountConnectionState>> CancelPendingLinkAsync(
        CancelMyOrbitExternalLinkIntent intent,
        CancellationToken cancellationToken = default) =>
        intent?.Context is not { IsStructurallyValid: true }
            ? ValueTask.FromResult(Invalid<MyOrbitAccountConnectionState>())
            : ExecuteOperationAsync(
                intent.Context,
                intent.OperationId,
                context => CancelCoreAsync(context, intent, cancellationToken),
                cancellationToken);

    public ValueTask<ControllerResult<MyOrbitAccountConnectionState>> DisconnectCurrentDeviceAsync(
        DisconnectMyOrbitCurrentDeviceIntent intent,
        CancellationToken cancellationToken = default) =>
        intent?.Context is not { IsStructurallyValid: true }
            ? ValueTask.FromResult(Invalid<MyOrbitAccountConnectionState>())
            : ExecuteOperationAsync(
                intent.Context,
                intent.OperationId,
                context => DisconnectCoreAsync(context, intent.ExpectedRevision, cancellationToken),
                cancellationToken);

    public ValueTask<ControllerResult<MyOrbitDeviceCollection>> QueryDevicesAsync(
        QueryMyOrbitDevicesIntent intent,
        CancellationToken cancellationToken = default) =>
        intent?.Context is not { IsStructurallyValid: true }
            ? ValueTask.FromResult(Invalid<MyOrbitDeviceCollection>())
            : ExecuteOperationAsync(
                intent.Context,
                intent.OperationId,
                context => QueryDevicesCoreAsync(context, intent.ExpectedRevision, cancellationToken),
                cancellationToken);

    public ValueTask<ControllerResult<MyOrbitDeviceCollection>> RevokeDeviceAsync(
        RevokeMyOrbitDeviceIntent intent,
        CancellationToken cancellationToken = default) =>
        intent?.Context is not { IsStructurallyValid: true }
            ? ValueTask.FromResult(Invalid<MyOrbitDeviceCollection>())
            : ExecuteOperationAsync(
                intent.Context,
                intent.OperationId,
                context => RevokeDeviceCoreAsync(context, intent, cancellationToken),
                cancellationToken);

    private static async ValueTask<ControllerResult<T>> ExecuteOperationAsync<T>(
        BrowsingContext browsing,
        SyncOperationId operationId,
        Func<SyncOperationContext, ValueTask<ControllerResult<T>>> operation,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await SyncOperationGate.ExecuteAsync(
                browsing,
                operationId,
                context =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return operation(context);
                })
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ControllerResult<T>.Failure(ControllerError.Create(
                ControllerErrorCode.Cancelled,
                "account.link.cancelled"));
        }
    }

    private async ValueTask<ControllerResult<MyOrbitAccountConnectionState>> QueryCoreAsync(
        SyncOperationContext context,
        MyOrbitAccountRevision expected,
        CancellationToken cancellationToken)
    {
        if (!expected.IsDefined)
            return Invalid<MyOrbitAccountConnectionState>();
        if (_protocol is null)
            return expected == MyOrbitAccountRevision.Initial
                ? ControllerResult<MyOrbitAccountConnectionState>.Success(
                    ProviderUnavailableState(context.Browsing.Privacy, MyOrbitAccountRevision.Initial))
                : Conflict<MyOrbitAccountConnectionState>();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var loaded = await EnsureLoadedAsync(context, cancellationToken).ConfigureAwait(false);
            if (!loaded.IsSuccess)
                return ControllerResult<MyOrbitAccountConnectionState>.Failure(loaded.Error!);
            if (expected.Value > _revision)
                return Conflict<MyOrbitAccountConnectionState>();
            if (_credential is { RevocationPending: true })
                await RetryPendingRevocationAsync(context, cancellationToken).ConfigureAwait(false);
            return ControllerResult<MyOrbitAccountConnectionState>.Success(_status!);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<ControllerResult<MyOrbitExternalLinkReceipt>> BeginCoreAsync(
        SyncOperationContext context,
        BeginMyOrbitExternalLinkIntent intent,
        CancellationToken cancellationToken)
    {
        if (_protocol is null || _vault is null || _launcher is null || _loopback is null || _clock is null)
            return Unavailable<MyOrbitExternalLinkReceipt>();
        if (!ValidDeviceName(intent.DeviceDisplayName))
            return Invalid<MyOrbitExternalLinkReceipt>();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var loaded = await EnsureLoadedAsync(context, cancellationToken).ConfigureAwait(false);
            if (!loaded.IsSuccess)
                return ControllerResult<MyOrbitExternalLinkReceipt>.Failure(loaded.Error!);
            if (!Expected(intent.ExpectedRevision) || _pending is not null ||
                _status!.State is MyOrbitAccountConnectionStateKind.Connected or MyOrbitAccountConnectionStateKind.LinkPending)
                return Conflict<MyOrbitExternalLinkReceipt>();

            var bound = _loopback.Bind();
            if (!bound.IsSuccess)
                return ControllerResult<MyOrbitExternalLinkReceipt>.Failure(bound.Error!);
            var listener = bound.Value!;
            var state = RandomBase64Url(32);
            var verifierBytes = RandomVerifier(64);
            var verifier = new SensitiveUtf8Buffer(verifierBytes);
            var challengeDigest = SHA256.HashData(verifierBytes);
            var challenge = Base64Url(challengeDigest);
            CryptographicOperations.ZeroMemory(verifierBytes);
            CryptographicOperations.ZeroMemory(challengeDigest);
            var pushed = await _protocol.PushAuthorizationAsync(new(
                listener.RedirectUri,
                state,
                challenge,
                intent.DeviceDisplayName.Trim()), cancellationToken).ConfigureAwait(false);
            if (!pushed.IsSuccess)
            {
                verifier.Dispose();
                await listener.DisposeAsync().ConfigureAwait(false);
                SetStatus(context.Browsing.Privacy, MyOrbitAccountConnectionStateKind.Failed,
                    "account.link.provider-unavailable", null);
                return ControllerResult<MyOrbitExternalLinkReceipt>.Failure(pushed.Error!);
            }

            var launched = await _launcher.LaunchAsync(pushed.Value!.AuthorizationUri, cancellationToken)
                .ConfigureAwait(false);
            if (!launched.IsSuccess)
            {
                verifier.Dispose();
                await listener.DisposeAsync().ConfigureAwait(false);
                SetStatus(context.Browsing.Privacy, MyOrbitAccountConnectionStateKind.Failed,
                    "account.link.browser-launch-failed", null);
                return ControllerResult<MyOrbitExternalLinkReceipt>.Failure(launched.Error!);
            }

            var attempt = new MyOrbitLinkAttemptId(Guid.NewGuid());
            var cancellation = new CancellationTokenSource();
            var pending = new PendingLink(
                attempt,
                context,
                listener,
                state,
                verifier,
                pushed.Value.ExpiresAtUtc,
                cancellation,
                _credential);
            _pending = pending;
            SetStatus(context.Browsing.Privacy, MyOrbitAccountConnectionStateKind.LinkPending,
                "account.link.pending", null);
            _pendingCompletion = CompleteLinkAsync(pending);
            return ControllerResult<MyOrbitExternalLinkReceipt>.Success(new(
                attempt,
                pending.ExpiresAtUtc,
                _status!));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<ControllerResult<MyOrbitAccountConnectionState>> CancelCoreAsync(
        SyncOperationContext context,
        CancelMyOrbitExternalLinkIntent intent,
        CancellationToken cancellationToken)
    {
        if (_protocol is null)
            return Unavailable<MyOrbitAccountConnectionState>();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var loaded = await EnsureLoadedAsync(context, cancellationToken).ConfigureAwait(false);
            if (!loaded.IsSuccess)
                return ControllerResult<MyOrbitAccountConnectionState>.Failure(loaded.Error!);
            if (!Expected(intent.ExpectedRevision) || _pending is null ||
                intent.AttemptId.IsEmpty || intent.AttemptId != _pending.AttemptId)
                return Conflict<MyOrbitAccountConnectionState>();
            var pending = _pending;
            _pending = null;
            pending.Cancellation.Cancel();
            await pending.Listener.DisposeAsync().ConfigureAwait(false);
            pending.Verifier.Dispose();
            var restored = pending.PreviousCredential;
            var restoredState = restored is null
                ? MyOrbitAccountConnectionStateKind.SignedOut
                : restored.RefreshExpiresAtUtc <= _clock!.UtcNow
                    ? MyOrbitAccountConnectionStateKind.ReauthorizationRequired
                    : MyOrbitAccountConnectionStateKind.Connected;
            SetStatus(context.Browsing.Privacy, restoredState,
                "account.link.cancelled", restored);
            return ControllerResult<MyOrbitAccountConnectionState>.Success(_status!);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<ControllerResult<MyOrbitAccountConnectionState>> DisconnectCoreAsync(
        SyncOperationContext context,
        MyOrbitAccountRevision expected,
        CancellationToken cancellationToken)
    {
        if (_protocol is null || _vault is null)
            return Unavailable<MyOrbitAccountConnectionState>();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var loaded = await EnsureLoadedAsync(context, cancellationToken).ConfigureAwait(false);
            if (!loaded.IsSuccess)
                return ControllerResult<MyOrbitAccountConnectionState>.Failure(loaded.Error!);
            if (!Expected(expected) || _credential is null)
                return Conflict<MyOrbitAccountConnectionState>();
            var revoked = await _protocol.RevokeAsync(_credential.RefreshCredential, cancellationToken)
                .ConfigureAwait(false);
            if (revoked.IsSuccess)
            {
                var deleted = await _vault.DeleteAsync(context, _credential, cancellationToken).ConfigureAwait(false);
                if (!deleted.IsSuccess)
                    return ControllerResult<MyOrbitAccountConnectionState>.Failure(deleted.Error!);
                ClearCredentials();
                SetStatus(context.Browsing.Privacy, MyOrbitAccountConnectionStateKind.Revoked,
                    "account.link.disconnected", null);
                return ControllerResult<MyOrbitAccountConnectionState>.Success(_status!);
            }

            var marked = await _vault.MarkRevocationPendingAsync(context, _credential, cancellationToken)
                .ConfigureAwait(false);
            if (!marked.IsSuccess)
                return ControllerResult<MyOrbitAccountConnectionState>.Failure(marked.Error!);
            ReplaceCredential(marked.Value!);
            SetStatus(context.Browsing.Privacy, MyOrbitAccountConnectionStateKind.Failed,
                "account.link.revocation-pending", _credential);
            return ControllerResult<MyOrbitAccountConnectionState>.Success(_status!);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<ControllerResult<MyOrbitDeviceCollection>> QueryDevicesCoreAsync(
        SyncOperationContext context,
        MyOrbitAccountRevision expected,
        CancellationToken cancellationToken)
    {
        if (_protocol is null)
            return Unavailable<MyOrbitDeviceCollection>();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var loaded = await EnsureLoadedAsync(context, cancellationToken).ConfigureAwait(false);
            if (!loaded.IsSuccess)
                return ControllerResult<MyOrbitDeviceCollection>.Failure(loaded.Error!);
            if (!Expected(expected) || _credential is null || _credential.RevocationPending)
                return Conflict<MyOrbitDeviceCollection>();
            var access = await GetAccessCredentialAsync(context, cancellationToken).ConfigureAwait(false);
            if (!access.IsSuccess)
                return ControllerResult<MyOrbitDeviceCollection>.Failure(access.Error!);
            using var lease = access.Value!;
            var devices = await _protocol.ListDevicesAsync(lease, cancellationToken).ConfigureAwait(false);
            if (!devices.IsSuccess)
            {
                MapProtocolFailure(context.Browsing.Privacy, devices.Error!);
                return ControllerResult<MyOrbitDeviceCollection>.Failure(devices.Error!);
            }
            var projected = devices.Value!.Devices.Select(device => new MyOrbitLinkedDevice(
                device.DeviceId,
                device.DisplayName,
                device.IsCurrent,
                device.RegisteredAtUtc,
                device.LastSeenAtUtc,
                device.RevokedAtUtc)).ToArray();
            if (devices.Value.AccountLabel is not null && _credential.AccountLabel is null)
                SetStatus(context.Browsing.Privacy, MyOrbitAccountConnectionStateKind.Connected,
                    null, _credential, devices.Value.AccountLabel);
            return ControllerResult<MyOrbitDeviceCollection>.Success(new(_status!, projected));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<ControllerResult<MyOrbitDeviceCollection>> RevokeDeviceCoreAsync(
        SyncOperationContext context,
        RevokeMyOrbitDeviceIntent intent,
        CancellationToken cancellationToken)
    {
        if (_protocol is null || _vault is null || intent.DeviceId.IsEmpty)
            return _protocol is null ? Unavailable<MyOrbitDeviceCollection>() : Invalid<MyOrbitDeviceCollection>();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var loaded = await EnsureLoadedAsync(context, cancellationToken).ConfigureAwait(false);
            if (!loaded.IsSuccess)
                return ControllerResult<MyOrbitDeviceCollection>.Failure(loaded.Error!);
            if (!Expected(intent.ExpectedRevision) || _credential is null || _credential.RevocationPending)
                return Conflict<MyOrbitDeviceCollection>();
            var access = await GetAccessCredentialAsync(context, cancellationToken).ConfigureAwait(false);
            if (!access.IsSuccess)
                return ControllerResult<MyOrbitDeviceCollection>.Failure(access.Error!);
            using var lease = access.Value!;
            var revoked = await _protocol.RevokeDeviceAsync(lease, intent.DeviceId, cancellationToken)
                .ConfigureAwait(false);
            if (!revoked.IsSuccess)
            {
                MapProtocolFailure(context.Browsing.Privacy, revoked.Error!);
                return ControllerResult<MyOrbitDeviceCollection>.Failure(revoked.Error!);
            }
            if (intent.DeviceId == _credential.ConnectionId)
            {
                var deleted = await _vault.DeleteAsync(context, _credential, cancellationToken).ConfigureAwait(false);
                if (!deleted.IsSuccess)
                    return ControllerResult<MyOrbitDeviceCollection>.Failure(deleted.Error!);
                ClearCredentials();
                SetStatus(context.Browsing.Privacy, MyOrbitAccountConnectionStateKind.Revoked,
                    "account.link.current-device-revoked", null);
                return ControllerResult<MyOrbitDeviceCollection>.Success(new(_status!, []));
            }

            var list = await _protocol.ListDevicesAsync(lease, cancellationToken).ConfigureAwait(false);
            if (!list.IsSuccess)
                return ControllerResult<MyOrbitDeviceCollection>.Failure(list.Error!);
            var projected = list.Value!.Devices.Select(device => new MyOrbitLinkedDevice(
                device.DeviceId, device.DisplayName, device.IsCurrent, device.RegisteredAtUtc,
                device.LastSeenAtUtc, device.RevokedAtUtc));
            return ControllerResult<MyOrbitDeviceCollection>.Success(new(_status!, projected));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CompleteLinkAsync(PendingLink pending)
    {
        try
        {
            var callback = await pending.Listener.ReceiveAsync(pending.Cancellation.Token).ConfigureAwait(false);
            if (!callback.IsSuccess)
            {
                await CompleteFailureAsync(pending, callback.Error!).ConfigureAwait(false);
                return;
            }
            using var code = callback.Value!.AuthorizationCode;
            if (code is null || callback.Value.Error is not null ||
                !FixedTimeEquals(callback.Value.State, pending.State))
            {
                await CompleteFailureAsync(pending, ControllerError.Create(
                    ControllerErrorCode.IntegrityFailure,
                    "account.link.callback-invalid")).ConfigureAwait(false);
                return;
            }
            using var verifier = pending.Verifier.Clone();
            var exchanged = await _protocol!.ExchangeCodeAsync(new(
                pending.Listener.RedirectUri,
                code,
                verifier), pending.Cancellation.Token).ConfigureAwait(false);
            if (!exchanged.IsSuccess)
            {
                await CompleteFailureAsync(pending, exchanged.Error!).ConfigureAwait(false);
                return;
            }
            using var tokens = exchanged.Value!;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(_pending, pending))
                {
                    await _protocol.RevokeAsync(tokens.RefreshCredential, CancellationToken.None).ConfigureAwait(false);
                    return;
                }
                var saved = await _vault!.SaveAsync(
                    pending.Context,
                    pending.PreviousCredential,
                    tokens,
                    false,
                    pending.Cancellation.Token).ConfigureAwait(false);
                if (!saved.IsSuccess)
                {
                    await _protocol.RevokeAsync(tokens.RefreshCredential, CancellationToken.None).ConfigureAwait(false);
                    _pending = null;
                    SetStatus(pending.Context.Browsing.Privacy, MyOrbitAccountConnectionStateKind.Failed,
                        "account.link.credential-save-failed", pending.PreviousCredential);
                    return;
                }
                _pending = null;
                ReplaceCredential(saved.Value!);
                ReplaceAccess(tokens.AccessCredential.Clone(), tokens.AccessExpiresAtUtc);
                SetStatus(pending.Context.Browsing.Privacy, MyOrbitAccountConnectionStateKind.Connected,
                    "account.link.connected", _credential);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Explicit cancellation has already projected its own state.
        }
        finally
        {
            pending.Verifier.Dispose();
            pending.Cancellation.Dispose();
            await pending.Listener.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task CompleteFailureAsync(PendingLink pending, ControllerError error)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_pending, pending))
                return;
            _pending = null;
            var message = error.Code == ControllerErrorCode.Cancelled
                ? "account.link.cancelled"
                : error.Code == ControllerErrorCode.Expired
                    ? "account.link.reauthorization-required"
                    : "account.link.failed";
            SetStatus(pending.Context.Browsing.Privacy, MyOrbitAccountConnectionStateKind.Failed,
                message, pending.PreviousCredential);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<ControllerResult> EnsureLoadedAsync(
        SyncOperationContext context,
        CancellationToken cancellationToken)
    {
        if (_status is not null)
        {
            if (_status.ProfileId != context.Browsing.Privacy.ProfileId)
                return ControllerResult.Failure(InvalidError());
            if (_status.Privacy != context.Browsing.Privacy)
                RebindStatus(context.Browsing.Privacy);
            return ControllerResult.Success();
        }
        var loaded = await _vault!.LoadAsync(context, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            if (loaded.Error!.Code != ControllerErrorCode.NotFound)
                return ControllerResult.Failure(loaded.Error!);
            SetStatus(context.Browsing.Privacy, MyOrbitAccountConnectionStateKind.SignedOut,
                null, null, increment: false);
            return ControllerResult.Success();
        }
        var credential = loaded.Value!;
        ReplaceCredential(credential);
        var state = credential.RefreshExpiresAtUtc <= _clock!.UtcNow
            ? MyOrbitAccountConnectionStateKind.ReauthorizationRequired
            : credential.RevocationPending
                ? MyOrbitAccountConnectionStateKind.Failed
                : MyOrbitAccountConnectionStateKind.Connected;
        var message = credential.RevocationPending ? "account.link.revocation-pending" : null;
        SetStatus(context.Browsing.Privacy, state, message, credential, increment: false);
        return ControllerResult.Success();
    }

    private async ValueTask RetryPendingRevocationAsync(
        SyncOperationContext context,
        CancellationToken cancellationToken)
    {
        var revoked = await _protocol!.RevokeAsync(_credential!.RefreshCredential, cancellationToken)
            .ConfigureAwait(false);
        if (!revoked.IsSuccess)
            return;
        var deleted = await _vault!.DeleteAsync(context, _credential, cancellationToken).ConfigureAwait(false);
        if (!deleted.IsSuccess)
            return;
        ClearCredentials();
        SetStatus(context.Browsing.Privacy, MyOrbitAccountConnectionStateKind.Revoked,
            "account.link.disconnected", null);
    }

    private async ValueTask<ControllerResult<SensitiveUtf8Buffer>> GetAccessCredentialAsync(
        SyncOperationContext context,
        CancellationToken cancellationToken)
    {
        if (_accessCredential is not null && _accessExpiresAtUtc > _clock!.UtcNow.AddSeconds(30))
            return ControllerResult<SensitiveUtf8Buffer>.Success(_accessCredential.Clone());
        if (_credential is null || _credential.RefreshExpiresAtUtc <= _clock!.UtcNow)
        {
            SetStatus(context.Browsing.Privacy, MyOrbitAccountConnectionStateKind.ReauthorizationRequired,
                "account.link.reauthorization-required", _credential);
            return ControllerResult<SensitiveUtf8Buffer>.Failure(ControllerError.Create(
                ControllerErrorCode.Expired,
                "account.link.reauthorization-required"));
        }
        var refreshed = await _protocol!.RefreshAsync(_credential.RefreshCredential, cancellationToken)
            .ConfigureAwait(false);
        if (!refreshed.IsSuccess)
        {
            MapProtocolFailure(context.Browsing.Privacy, refreshed.Error!);
            return ControllerResult<SensitiveUtf8Buffer>.Failure(refreshed.Error!);
        }
        using var tokens = refreshed.Value!;
        var saved = await _vault!.SaveAsync(context, _credential, tokens, false, cancellationToken)
            .ConfigureAwait(false);
        if (!saved.IsSuccess)
        {
            await _protocol.RevokeAsync(tokens.RefreshCredential, CancellationToken.None).ConfigureAwait(false);
            await _vault.DeleteAsync(context, _credential, CancellationToken.None).ConfigureAwait(false);
            ClearCredentials();
            SetStatus(context.Browsing.Privacy, MyOrbitAccountConnectionStateKind.ReauthorizationRequired,
                "account.link.credential-save-failed", null);
            return ControllerResult<SensitiveUtf8Buffer>.Failure(saved.Error!);
        }
        ReplaceCredential(saved.Value!);
        ReplaceAccess(tokens.AccessCredential.Clone(), tokens.AccessExpiresAtUtc);
        SetStatus(context.Browsing.Privacy, MyOrbitAccountConnectionStateKind.Connected,
            null, _credential);
        return ControllerResult<SensitiveUtf8Buffer>.Success(_accessCredential!.Clone());
    }

    private void MapProtocolFailure(PrivacyContext privacy, ControllerError error)
    {
        if (error.Code is ControllerErrorCode.Expired or ControllerErrorCode.IntegrityFailure)
            SetStatus(privacy, MyOrbitAccountConnectionStateKind.ReauthorizationRequired,
                "account.link.reauthorization-required", _credential);
        else
            SetStatus(privacy, MyOrbitAccountConnectionStateKind.Failed,
                "account.link.provider-unavailable", _credential);
    }

    private void SetStatus(
        PrivacyContext privacy,
        MyOrbitAccountConnectionStateKind state,
        string? messageKey,
        StoredMyOrbitCredential? credential,
        string? accountLabel = null,
        bool increment = true)
    {
        if (increment)
            _revision = checked(_revision + 1);
        var scopes = state is MyOrbitAccountConnectionStateKind.Connected or
            MyOrbitAccountConnectionStateKind.LinkPending ||
            credential is { RevocationPending: false } &&
            state is MyOrbitAccountConnectionStateKind.ReauthorizationRequired or
                MyOrbitAccountConnectionStateKind.Failed
            ? LinkScopes
            : [];
        var capabilities = state switch
        {
            MyOrbitAccountConnectionStateKind.SignedOut or MyOrbitAccountConnectionStateKind.Revoked =>
                MyOrbitAccountCapabilities.BeginExternalLink,
            MyOrbitAccountConnectionStateKind.LinkPending => MyOrbitAccountCapabilities.CancelPendingLink,
            MyOrbitAccountConnectionStateKind.Connected =>
                MyOrbitAccountCapabilities.DisconnectCurrentDevice |
                MyOrbitAccountCapabilities.QueryDevices |
                MyOrbitAccountCapabilities.RevokeDevice,
            MyOrbitAccountConnectionStateKind.ReauthorizationRequired when credential is not null =>
                MyOrbitAccountCapabilities.BeginExternalLink |
                MyOrbitAccountCapabilities.DisconnectCurrentDevice |
                MyOrbitAccountCapabilities.QueryDevices |
                MyOrbitAccountCapabilities.RevokeDevice,
            MyOrbitAccountConnectionStateKind.ReauthorizationRequired =>
                MyOrbitAccountCapabilities.BeginExternalLink,
            MyOrbitAccountConnectionStateKind.Failed when credential is { RevocationPending: true } =>
                MyOrbitAccountCapabilities.DisconnectCurrentDevice,
            MyOrbitAccountConnectionStateKind.Failed when credential is not null =>
                MyOrbitAccountCapabilities.BeginExternalLink |
                MyOrbitAccountCapabilities.DisconnectCurrentDevice |
                MyOrbitAccountCapabilities.QueryDevices |
                MyOrbitAccountCapabilities.RevokeDevice,
            MyOrbitAccountConnectionStateKind.Failed => MyOrbitAccountCapabilities.BeginExternalLink,
            _ => MyOrbitAccountCapabilities.None,
        };
        _status = new(
            privacy,
            new MyOrbitAccountRevision(_revision),
            state,
            "My Orbit",
            MyOrbitAuthorizationProtocol.SafeAccountLabel(accountLabel ?? credential?.AccountLabel),
            scopes,
            messageKey,
            credential?.AuthorizedAtUtc,
            credential?.LastRefreshedAtUtc,
            credential?.RefreshExpiresAtUtc,
            capabilities);
    }

    private void RebindStatus(PrivacyContext privacy)
    {
        var current = _status!;
        _revision = checked(_revision + 1);
        _status = new(
            privacy,
            new MyOrbitAccountRevision(_revision),
            current.State,
            current.ProviderLabel,
            current.AccountLabel,
            current.GrantedScopes,
            current.MessageKey,
            current.AuthorizedAtUtc,
            current.LastRefreshedAtUtc,
            current.CredentialExpiresAtUtc,
            current.Capabilities,
            current.LocalBrowsingAvailable);
    }

    private static MyOrbitAccountConnectionState ProviderUnavailableState(
        PrivacyContext privacy,
        MyOrbitAccountRevision revision) => new(
            privacy,
            revision,
            MyOrbitAccountConnectionStateKind.ProviderUnavailable,
            "My Orbit",
            null,
            [],
            "account.link.provider-unavailable",
            null,
            null,
            null,
            MyOrbitAccountCapabilities.None);

    private bool Expected(MyOrbitAccountRevision expected) =>
        expected.IsDefined && expected.Value == _revision;

    private void ReplaceCredential(StoredMyOrbitCredential credential)
    {
        _credential?.Dispose();
        _credential = credential;
    }

    private void ReplaceAccess(SensitiveUtf8Buffer access, DateTimeOffset expiresAtUtc)
    {
        _accessCredential?.Dispose();
        _accessCredential = access;
        _accessExpiresAtUtc = expiresAtUtc;
    }

    private void ClearCredentials()
    {
        _credential?.Dispose();
        _credential = null;
        _accessCredential?.Dispose();
        _accessCredential = null;
        _accessExpiresAtUtc = default;
    }

    private static string RandomBase64Url(int bytes)
    {
        var data = RandomNumberGenerator.GetBytes(bytes);
        try { return Base64Url(data); }
        finally { CryptographicOperations.ZeroMemory(data); }
    }

    private static byte[] RandomVerifier(int length)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";
        var result = new byte[length];
        Span<byte> random = stackalloc byte[1];
        var index = 0;
        // Rejection sampling avoids modulo bias across the 66-character PKCE alphabet.
        const int maximumAccepted = 198;
        while (index < result.Length)
        {
            RandomNumberGenerator.Fill(random);
            if (random[0] >= maximumAccepted) continue;
            result[index++] = (byte)alphabet[random[0] % alphabet.Length];
        }
        random.Clear();
        return result;
    }

    private static string Base64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.ASCII.GetBytes(left);
        var rightBytes = Encoding.ASCII.GetBytes(right);
        try
        {
            return leftBytes.Length == rightBytes.Length &&
                CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    private static bool ValidDeviceName(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 80 && !value.Any(char.IsControl);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;
        Task? pendingCompletion;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            pendingCompletion = _pendingCompletion;
            if (_pending is not null)
            {
                _pending.Cancellation.Cancel();
                await _pending.Listener.DisposeAsync().ConfigureAwait(false);
                _pending.Verifier.Dispose();
                _pending = null;
            }
            ClearCredentials();
        }
        finally
        {
            _gate.Release();
        }
        if (pendingCompletion is not null)
        {
            try { await pendingCompletion.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _protocol?.Dispose();
        _gate.Dispose();
    }

    private static ControllerError InvalidError() => ControllerError.Create(
        ControllerErrorCode.InvalidRequest,
        "account.link.request-invalid");

    private static ControllerResult<T> Invalid<T>() where T : class =>
        ControllerResult<T>.Failure(InvalidError());

    private static ControllerResult<T> Conflict<T>() where T : class =>
        ControllerResult<T>.Failure(ControllerError.Create(
            ControllerErrorCode.Conflict,
            "account.link.revision-conflict",
            isRetryable: true));

    private static ControllerResult<T> Unavailable<T>() where T : class =>
        ControllerResult<T>.Failure(ControllerError.Create(
            ControllerErrorCode.Unavailable,
            "account.link.provider-unavailable",
            isRetryable: true));

    private sealed record PendingLink(
        MyOrbitLinkAttemptId AttemptId,
        SyncOperationContext Context,
        IMyOrbitLoopbackListener Listener,
        string State,
        SensitiveUtf8Buffer Verifier,
        DateTimeOffset ExpiresAtUtc,
        CancellationTokenSource Cancellation,
        StoredMyOrbitCredential? PreviousCredential);
}
