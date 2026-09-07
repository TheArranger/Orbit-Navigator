using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;

namespace OrbitNavigator.Contracts.Privacy;

public readonly record struct OrbitPasswordSecretLeaseId(ProfileId ProfileId, Guid Value)
{
    public bool IsEmpty => ProfileId.IsEmpty || Value == Guid.Empty;
}

public readonly record struct SensitiveActionAuthorizationTokenId(Guid Value)
{
    public bool IsEmpty => Value == Guid.Empty;
}

public enum SensitiveActionKind
{
    ViewSavedPasswords = 0,
    AutofillSavedPassword = 1,
    ChangeOrbitPassword = 2,
    RemoveOrbitPassword = 3,
    RestoreSyncKeysWithRecoveryCode = 4,
    RestoreSyncKeysWithTrustedDevice = 5,
    ResetEncryptedSync = 6,
    RevokeSyncDevice = 7,
    ForgottenOrbitPasswordVaultResetWithWindowsUserPresence = 8,
    DeleteSyncedData = 9,
    ResetLocalVault = 10,
    ClearProtectedLocalData = 11,
}

public enum SensitiveAuthorizationRevocationReason
{
    ExplicitRevocation = 0,
    OrbitPasswordChanged = 1,
    OrbitPasswordRemoved = 2,
    VaultReset = 3,
    WindowsSessionLocked = 4,
    BrowserSessionEnded = 5,
    ApplicationStopping = 6,
}

public enum OrbitPasswordVaultStatus
{
    NotConfigured = 0,
    Configured = 1,
}

public enum OrbitPasswordVaultDataDisposition
{
    LocalOnlyNeverSync = 0,
}

public sealed record SensitiveActionAuthorizationRequest(
    BrowsingContext Context,
    SensitiveActionKind Purpose,
    OrbitPasswordSecretLeaseId PasswordLeaseId,
    TimeSpan RequestedLifetime);

public sealed class SensitiveActionAuthorizationToken
{
    private SensitiveActionAuthorizationToken(
        SensitiveActionAuthorizationTokenId id,
        ProfileId profileId,
        BrowserSessionId sessionId,
        SensitiveActionKind purpose,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        Id = id;
        ProfileId = profileId;
        SessionId = sessionId;
        Purpose = purpose;
        IssuedAtUtc = issuedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public SensitiveActionAuthorizationTokenId Id { get; }

    public ProfileId ProfileId { get; }

    public BrowserSessionId SessionId { get; }

    public SensitiveActionKind Purpose { get; }

    public DateTimeOffset IssuedAtUtc { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public static ControllerResult<SensitiveActionAuthorizationToken> Create(
        SensitiveActionAuthorizationTokenId id,
        PrivacyContext context,
        SensitiveActionKind purpose,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (id.IsEmpty ||
            !context.IsStructurallyValid ||
            !Enum.IsDefined(purpose) ||
            purpose == SensitiveActionKind.ForgottenOrbitPasswordVaultResetWithWindowsUserPresence ||
            expiresAtUtc <= issuedAtUtc ||
            expiresAtUtc - issuedAtUtc > TimeSpan.FromMinutes(5))
        {
            return ControllerResult<SensitiveActionAuthorizationToken>.Failure(ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "error.sensitive_authorization.token_invalid"));
        }

        return ControllerResult<SensitiveActionAuthorizationToken>.Success(
            new SensitiveActionAuthorizationToken(
                id,
                context.ProfileId,
                context.SessionId,
                purpose,
                issuedAtUtc,
                expiresAtUtc));
    }

    public bool IsBoundTo(PrivacyContext context, SensitiveActionKind purpose) =>
        context is { IsStructurallyValid: true } &&
        ProfileId == context.ProfileId &&
        SessionId == context.SessionId &&
        Purpose == purpose;
}

public sealed record ValidateSensitiveActionAuthorizationRequest(
    BrowsingContext Context,
    SensitiveActionAuthorizationToken Token,
    SensitiveActionKind ExpectedPurpose);

public sealed record SensitiveActionAuthorizationReceipt(
    SensitiveActionAuthorizationTokenId ConsumedTokenId,
    ProfileId ProfileId,
    BrowserSessionId SessionId,
    SensitiveActionKind Purpose,
    DateTimeOffset ConsumedAtUtc);

public sealed record SensitiveAuthorizationRevocationReceipt(
    ProfileId ProfileId,
    BrowserSessionId? SessionId,
    SensitiveAuthorizationRevocationReason Reason,
    int RevokedTokenCount,
    DateTimeOffset CompletedAtUtc);

public sealed record OrbitPasswordVaultState(
    ProfileId ProfileId,
    OrbitPasswordVaultStatus Status,
    OrbitPasswordVaultDataDisposition DataDisposition,
    DateTimeOffset? LastChangedAtUtc);

public sealed record ConfigureOrbitPasswordRequest(
    BrowsingContext Context,
    OrbitPasswordSecretLeaseId NewPasswordLeaseId);

public sealed record ChangeOrbitPasswordRequest(
    BrowsingContext Context,
    SensitiveActionAuthorizationToken Authorization,
    OrbitPasswordSecretLeaseId NewPasswordLeaseId);

public sealed record RemoveOrbitPasswordRequest(
    BrowsingContext Context,
    SensitiveActionAuthorizationToken Authorization);

public sealed class ForgottenOrbitPasswordVaultResetRequest
{
    private ForgottenOrbitPasswordVaultResetRequest(
        BrowsingContext context,
        WindowsUserPresenceProof userPresenceProof)
    {
        Context = context;
        UserPresenceProof = userPresenceProof;
    }

    public BrowsingContext Context { get; }

    public WindowsUserPresenceProof UserPresenceProof { get; }

    public bool IsDestructive => true;

    public bool DeletesLocalSavedPasswords => true;

    public bool AffectsSyncedData => false;

    public bool DeletesDownloadedFiles => false;

    public static ControllerResult<ForgottenOrbitPasswordVaultResetRequest> Create(
        BrowsingContext context,
        WindowsUserPresenceProof userPresenceProof,
        bool destructiveResetConfirmed)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(userPresenceProof);

        if (!context.IsStructurallyValid ||
            context.Privacy.IsPrivate ||
            !destructiveResetConfirmed ||
            userPresenceProof.Purpose != WindowsUserPresencePurpose.ForgottenOrbitPasswordVaultReset ||
            userPresenceProof.ProfileId != context.Privacy.ProfileId ||
            userPresenceProof.SessionId != context.Privacy.SessionId)
        {
            var code = context.Privacy.IsPrivate
                ? ControllerErrorCode.PolicyDenied
                : ControllerErrorCode.InvalidRequest;
            return ControllerResult<ForgottenOrbitPasswordVaultResetRequest>.Failure(
                ControllerError.Create(code, "error.orbit_password.forgotten_reset_invalid"));
        }

        return ControllerResult<ForgottenOrbitPasswordVaultResetRequest>.Success(
            new ForgottenOrbitPasswordVaultResetRequest(context, userPresenceProof));
    }
}

public sealed record ForgottenOrbitPasswordVaultResetReceipt(
    ProfileId ProfileId,
    BrowserSessionId SessionId,
    WindowsUserPresenceProofId ConsumedProofId,
    DateTimeOffset CompletedAtUtc,
    bool LocalSavedPasswordsDeleted,
    bool SyncedDataAffected,
    bool DownloadedFilesDeleted);

public interface ISensitiveActionAuthorizer
{
    ValueTask<ControllerResult<SensitiveActionAuthorizationToken>> AuthorizeAsync(
        SensitiveActionAuthorizationRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<ControllerResult<SensitiveActionAuthorizationReceipt>> ValidateAndConsumeAsync(
        ValidateSensitiveActionAuthorizationRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<ControllerResult<SensitiveAuthorizationRevocationReceipt>> RevokeSessionAsync(
        PrivacyContext context,
        SensitiveAuthorizationRevocationReason reason,
        CancellationToken cancellationToken = default);

    ValueTask<ControllerResult<SensitiveAuthorizationRevocationReceipt>> RevokeProfileAsync(
        ProfileId profileId,
        SensitiveAuthorizationRevocationReason reason,
        CancellationToken cancellationToken = default);
}

public interface IOrbitPasswordVault
{
    ValueTask<ControllerResult<OrbitPasswordVaultState>> GetStateAsync(
        PrivacyContext context,
        CancellationToken cancellationToken = default);

    ValueTask<ControllerResult<OrbitPasswordVaultState>> ConfigureAsync(
        ConfigureOrbitPasswordRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<ControllerResult<OrbitPasswordVaultState>> ChangeAsync(
        ChangeOrbitPasswordRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<ControllerResult<OrbitPasswordVaultState>> RemoveAsync(
        RemoveOrbitPasswordRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<ControllerResult<ForgottenOrbitPasswordVaultResetReceipt>> ResetForgottenPasswordAsync(
        ForgottenOrbitPasswordVaultResetRequest request,
        CancellationToken cancellationToken = default);
}
