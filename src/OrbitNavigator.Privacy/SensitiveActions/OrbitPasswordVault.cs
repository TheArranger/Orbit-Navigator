using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Privacy;

namespace OrbitNavigator.Privacy.SensitiveActions;

public interface ILocalSavedPasswordEraser
{
    ValueTask<ControllerResult> DeleteAllAsync(
        PrivacyContext context,
        CancellationToken cancellationToken);
}

public sealed class OrbitPasswordVault : IOrbitPasswordVault
{
    private readonly IOrbitPasswordCredentialStore _credentials;
    private readonly IOrbitPasswordSecretLeaseConsumer _passwordLeases;
    private readonly ISensitiveActionAuthorizer _authorizer;
    private readonly IWindowsUserPresenceProofAdapter _userPresence;
    private readonly ILocalSavedPasswordEraser _savedPasswordEraser;
    private readonly IClock _clock;

    public OrbitPasswordVault(
        IOrbitPasswordCredentialStore credentials,
        IOrbitPasswordSecretLeaseConsumer passwordLeases,
        ISensitiveActionAuthorizer authorizer,
        IWindowsUserPresenceProofAdapter userPresence,
        ILocalSavedPasswordEraser savedPasswordEraser,
        IClock clock)
    {
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _passwordLeases = passwordLeases ?? throw new ArgumentNullException(nameof(passwordLeases));
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        _userPresence = userPresence ?? throw new ArgumentNullException(nameof(userPresence));
        _savedPasswordEraser = savedPasswordEraser ?? throw new ArgumentNullException(nameof(savedPasswordEraser));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public ValueTask<ControllerResult<OrbitPasswordVaultState>> GetStateAsync(
        PrivacyContext context,
        CancellationToken cancellationToken = default) =>
        _credentials.GetStateAsync(context, cancellationToken);

    public async ValueTask<ControllerResult<OrbitPasswordVaultState>> ConfigureAsync(
        ConfigureOrbitPasswordRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Valid(request.Context) ||
            request.NewPasswordLeaseId.IsEmpty ||
            request.NewPasswordLeaseId.ProfileId != request.Context.Privacy.ProfileId)
        {
            return Invalid<OrbitPasswordVaultState>();
        }

        var consumed = _passwordLeases.Consume(request.NewPasswordLeaseId);
        if (!consumed.IsSuccess)
        {
            return ControllerResult<OrbitPasswordVaultState>.Failure(consumed.Error!);
        }

        using var password = consumed.Value!;
        return await _credentials.SetAsync(
            request.Context.Privacy,
            password,
            OrbitPasswordCredentialWriteMode.RequireAbsent,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ControllerResult<OrbitPasswordVaultState>> ChangeAsync(
        ChangeOrbitPasswordRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Valid(request.Context) ||
            request.NewPasswordLeaseId.IsEmpty ||
            request.NewPasswordLeaseId.ProfileId != request.Context.Privacy.ProfileId)
        {
            return Invalid<OrbitPasswordVaultState>();
        }

        var consumed = _passwordLeases.Consume(request.NewPasswordLeaseId);
        if (!consumed.IsSuccess)
        {
            return ControllerResult<OrbitPasswordVaultState>.Failure(consumed.Error!);
        }

        using var password = consumed.Value!;
        var authorized = await _authorizer.ValidateAndConsumeAsync(
            new ValidateSensitiveActionAuthorizationRequest(
                request.Context,
                request.Authorization,
                SensitiveActionKind.ChangeOrbitPassword),
            cancellationToken).ConfigureAwait(false);
        if (!authorized.IsSuccess)
        {
            return ControllerResult<OrbitPasswordVaultState>.Failure(authorized.Error!);
        }

        var changed = await _credentials.SetAsync(
            request.Context.Privacy,
            password,
            OrbitPasswordCredentialWriteMode.RequirePresent,
            cancellationToken).ConfigureAwait(false);
        if (!changed.IsSuccess)
        {
            return changed;
        }

        var revoked = await _authorizer.RevokeProfileAsync(
            request.Context.Privacy.ProfileId,
            SensitiveAuthorizationRevocationReason.OrbitPasswordChanged,
            cancellationToken).ConfigureAwait(false);
        return revoked.IsSuccess
            ? changed
            : ControllerResult<OrbitPasswordVaultState>.Failure(revoked.Error!);
    }

    public async ValueTask<ControllerResult<OrbitPasswordVaultState>> RemoveAsync(
        RemoveOrbitPasswordRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Valid(request.Context))
        {
            return Invalid<OrbitPasswordVaultState>();
        }

        var authorized = await _authorizer.ValidateAndConsumeAsync(
            new ValidateSensitiveActionAuthorizationRequest(
                request.Context,
                request.Authorization,
                SensitiveActionKind.RemoveOrbitPassword),
            cancellationToken).ConfigureAwait(false);
        if (!authorized.IsSuccess)
        {
            return ControllerResult<OrbitPasswordVaultState>.Failure(authorized.Error!);
        }

        var deleted = await _credentials.DeleteAsync(request.Context.Privacy, cancellationToken)
            .ConfigureAwait(false);
        if (!deleted.IsSuccess)
        {
            return deleted;
        }

        var revoked = await _authorizer.RevokeProfileAsync(
            request.Context.Privacy.ProfileId,
            SensitiveAuthorizationRevocationReason.OrbitPasswordRemoved,
            cancellationToken).ConfigureAwait(false);
        return revoked.IsSuccess
            ? deleted
            : ControllerResult<OrbitPasswordVaultState>.Failure(revoked.Error!);
    }

    public async ValueTask<ControllerResult<ForgottenOrbitPasswordVaultResetReceipt>> ResetForgottenPasswordAsync(
        ForgottenOrbitPasswordVaultResetRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validation = WindowsUserPresenceValidationRequest.Create(
            request.Context.Privacy,
            request.UserPresenceProof,
            WindowsUserPresencePurpose.ForgottenOrbitPasswordVaultReset);
        if (!validation.IsSuccess)
        {
            return ControllerResult<ForgottenOrbitPasswordVaultResetReceipt>.Failure(validation.Error!);
        }

        var presence = await _userPresence.ValidateAndConsumeAsync(validation.Value!, cancellationToken)
            .ConfigureAwait(false);
        if (!presence.IsSuccess)
        {
            return ControllerResult<ForgottenOrbitPasswordVaultResetReceipt>.Failure(presence.Error!);
        }

        var erased = await _savedPasswordEraser.DeleteAllAsync(request.Context.Privacy, cancellationToken)
            .ConfigureAwait(false);
        if (!erased.IsSuccess)
        {
            return ControllerResult<ForgottenOrbitPasswordVaultResetReceipt>.Failure(erased.Error!);
        }

        var deleted = await _credentials.DeleteAsync(request.Context.Privacy, cancellationToken)
            .ConfigureAwait(false);
        if (!deleted.IsSuccess)
        {
            return ControllerResult<ForgottenOrbitPasswordVaultResetReceipt>.Failure(deleted.Error!);
        }

        var revoked = await _authorizer.RevokeProfileAsync(
            request.Context.Privacy.ProfileId,
            SensitiveAuthorizationRevocationReason.VaultReset,
            cancellationToken).ConfigureAwait(false);
        if (!revoked.IsSuccess)
        {
            return ControllerResult<ForgottenOrbitPasswordVaultResetReceipt>.Failure(revoked.Error!);
        }

        return ControllerResult<ForgottenOrbitPasswordVaultResetReceipt>.Success(
            new ForgottenOrbitPasswordVaultResetReceipt(
                request.Context.Privacy.ProfileId,
                request.Context.Privacy.SessionId,
                presence.Value!.ConsumedProofId,
                _clock.UtcNow,
                true,
                false,
                false));
    }

    private static bool Valid(BrowsingContext context) => context is { IsStructurallyValid: true };

    private static ControllerResult<T> Invalid<T>() where T : class =>
        ControllerResult<T>.Failure(ControllerError.Create(
            ControllerErrorCode.InvalidRequest,
            "error.orbit-password.request-invalid"));
}
