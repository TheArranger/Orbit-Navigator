using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Privacy;
using OrbitNavigator.Privacy.Clipboard;
using OrbitNavigator.Privacy.SensitiveActions;
using OrbitNavigator.Foundation.Security;

namespace OrbitNavigator.App.Composition;

/// <summary>Application-lifetime local privacy services; none require an account.</summary>
public sealed class LocalPrivacyComposition : IAsyncDisposable
{
    private readonly WindowsUserPresenceProofAdapter _userPresence;

    public LocalPrivacyComposition(IProfileStorage storage, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(clock);
        ClipboardCaptureLeases = new ClipboardCaptureLeaseStore(clock);
        ClipboardShelf = new ClipboardShelfController(
            ClipboardCaptureLeases,
            new WindowsClipboardShelfUseTarget(),
            clock);
        PasswordSecretLeases = new OrbitPasswordSecretLeaseStore(clock);
        var credentials = new ProtectedOrbitPasswordCredentialStore(
            storage,
            new WindowsDataProtection(),
            clock);
        SensitiveActions = new SensitiveActionAuthorizer(
            PasswordSecretLeases,
            credentials,
            clock);
        _userPresence = new WindowsUserPresenceProofAdapter(
            new FailClosedWindowsUserPresenceVerifier(),
            clock);
        PasswordVault = new OrbitPasswordVault(
            credentials,
            PasswordSecretLeases,
            SensitiveActions,
            _userPresence,
            new UnavailableSavedPasswordEraser(),
            clock);
    }

    public ClipboardCaptureLeaseStore ClipboardCaptureLeases { get; }

    public ClipboardShelfController ClipboardShelf { get; }

    public OrbitPasswordSecretLeaseStore PasswordSecretLeases { get; }

    public SensitiveActionAuthorizer SensitiveActions { get; }

    public OrbitPasswordVault PasswordVault { get; }

    public IWindowsUserPresenceProofAdapter UserPresence => _userPresence;

    public async ValueTask RevokeSessionAsync(PrivacyContext context)
    {
        await SensitiveActions.RevokeSessionAsync(
            context,
            SensitiveAuthorizationRevocationReason.BrowserSessionEnded);
        await _userPresence.InvalidateSessionAsync(
            context.ProfileId,
            context.SessionId,
            WindowsUserPresenceInvalidationReason.BrowserSessionEnded);
    }

    public async ValueTask DisposeAsync()
    {
        await _userPresence.InvalidateAllAsync(
            WindowsUserPresenceInvalidationReason.ApplicationStopping);
        ClipboardCaptureLeases.Dispose();
        await ClipboardShelf.DisposeAsync();
        PasswordSecretLeases.Dispose();
    }

    private sealed class UnavailableSavedPasswordEraser : ILocalSavedPasswordEraser
    {
        public ValueTask<ControllerResult> DeleteAllAsync(
            PrivacyContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ControllerResult.Failure(ControllerError.Create(
                ControllerErrorCode.Unavailable,
                "error.saved-passwords.store-unavailable")));
    }
}
