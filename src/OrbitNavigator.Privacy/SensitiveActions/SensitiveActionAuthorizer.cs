using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Privacy;

namespace OrbitNavigator.Privacy.SensitiveActions;

/// <summary>
/// Verifies the local Orbit password and issues one-shot, purpose/profile/session-bound
/// authorizations with a maximum five-minute lifetime.
/// </summary>
public sealed class SensitiveActionAuthorizer : ISensitiveActionAuthorizer
{
    private readonly IOrbitPasswordSecretLeaseConsumer _passwordLeases;
    private readonly IOrbitPasswordCredentialStore _credentials;
    private readonly IClock _clock;
    private readonly object _gate = new();
    private readonly Dictionary<SensitiveActionAuthorizationTokenId, SensitiveActionAuthorizationToken> _active = [];
    private readonly Dictionary<SensitiveActionAuthorizationTokenId, DateTimeOffset> _handled = [];

    public SensitiveActionAuthorizer(
        IOrbitPasswordSecretLeaseConsumer passwordLeases,
        IOrbitPasswordCredentialStore credentials,
        IClock clock)
    {
        _passwordLeases = passwordLeases ?? throw new ArgumentNullException(nameof(passwordLeases));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask<ControllerResult<SensitiveActionAuthorizationToken>> AuthorizeAsync(
        SensitiveActionAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Context.IsStructurallyValid ||
            request.PasswordLeaseId.IsEmpty ||
            request.PasswordLeaseId.ProfileId != request.Context.Privacy.ProfileId ||
            !Enum.IsDefined(request.Purpose) ||
            request.Purpose == SensitiveActionKind.ForgottenOrbitPasswordVaultResetWithWindowsUserPresence ||
            request.RequestedLifetime <= TimeSpan.Zero ||
            request.RequestedLifetime > TimeSpan.FromMinutes(5))
        {
            return Failure<SensitiveActionAuthorizationToken>(
                ControllerErrorCode.InvalidRequest,
                "error.sensitive-authorization.request-invalid");
        }

        var consumed = _passwordLeases.Consume(request.PasswordLeaseId);
        if (!consumed.IsSuccess)
        {
            return ControllerResult<SensitiveActionAuthorizationToken>.Failure(consumed.Error!);
        }

        using var password = consumed.Value!;
        var verified = await _credentials.VerifyAsync(
            request.Context.Privacy,
            password,
            cancellationToken).ConfigureAwait(false);
        if (!verified.IsSuccess)
        {
            return ControllerResult<SensitiveActionAuthorizationToken>.Failure(verified.Error!);
        }

        if (!verified.Value!.IsConfigured)
        {
            return Failure<SensitiveActionAuthorizationToken>(
                ControllerErrorCode.Unavailable,
                "error.sensitive-authorization.password-not-configured");
        }

        if (!verified.Value.Matches)
        {
            return Failure<SensitiveActionAuthorizationToken>(
                ControllerErrorCode.PolicyDenied,
                "error.sensitive-authorization.password-rejected");
        }

        var now = _clock.UtcNow;
        var token = SensitiveActionAuthorizationToken.Create(
            new SensitiveActionAuthorizationTokenId(Guid.NewGuid()),
            request.Context.Privacy,
            request.Purpose,
            now,
            now.Add(request.RequestedLifetime));
        if (!token.IsSuccess)
        {
            return token;
        }

        lock (_gate)
        {
            Prune(now);
            _active.Add(token.Value!.Id, token.Value);
        }

        return token;
    }

    public ValueTask<ControllerResult<SensitiveActionAuthorizationReceipt>> ValidateAndConsumeAsync(
        ValidateSensitiveActionAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!request.Context.IsStructurallyValid ||
            request.Token is null ||
            !Enum.IsDefined(request.ExpectedPurpose) ||
            !request.Token.IsBoundTo(request.Context.Privacy, request.ExpectedPurpose))
        {
            return ValueTask.FromResult(Failure<SensitiveActionAuthorizationReceipt>(
                ControllerErrorCode.InvalidRequest,
                "error.sensitive-authorization.binding-invalid"));
        }

        var now = _clock.UtcNow;
        lock (_gate)
        {
            PruneHandled(now);
            if (!_active.Remove(request.Token.Id, out var active))
            {
                var code = _handled.ContainsKey(request.Token.Id)
                    ? ControllerErrorCode.AlreadyHandled
                    : ControllerErrorCode.PolicyDenied;
                return ValueTask.FromResult(Failure<SensitiveActionAuthorizationReceipt>(
                    code,
                    "error.sensitive-authorization.not-active"));
            }

            _handled[active.Id] = now.AddMinutes(10);
            if (active.ExpiresAtUtc <= now)
            {
                return ValueTask.FromResult(Failure<SensitiveActionAuthorizationReceipt>(
                    ControllerErrorCode.Expired,
                    "error.sensitive-authorization.expired"));
            }

            if (!active.IsBoundTo(request.Context.Privacy, request.ExpectedPurpose))
            {
                return ValueTask.FromResult(Failure<SensitiveActionAuthorizationReceipt>(
                    ControllerErrorCode.InvalidRequest,
                    "error.sensitive-authorization.binding-invalid"));
            }

            return ValueTask.FromResult(ControllerResult<SensitiveActionAuthorizationReceipt>.Success(
                new SensitiveActionAuthorizationReceipt(
                    active.Id,
                    active.ProfileId,
                    active.SessionId,
                    active.Purpose,
                    now)));
        }
    }

    public ValueTask<ControllerResult<SensitiveAuthorizationRevocationReceipt>> RevokeSessionAsync(
        PrivacyContext context,
        SensitiveAuthorizationRevocationReason reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (!context.IsStructurallyValid || !Enum.IsDefined(reason))
        {
            return ValueTask.FromResult(Failure<SensitiveAuthorizationRevocationReceipt>(
                ControllerErrorCode.InvalidRequest,
                "error.sensitive-authorization.revocation-invalid"));
        }

        return ValueTask.FromResult(Revoke(
            context.ProfileId,
            context.SessionId,
            reason,
            token => token.ProfileId == context.ProfileId && token.SessionId == context.SessionId));
    }

    public ValueTask<ControllerResult<SensitiveAuthorizationRevocationReceipt>> RevokeProfileAsync(
        ProfileId profileId,
        SensitiveAuthorizationRevocationReason reason,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (profileId.IsEmpty || !Enum.IsDefined(reason))
        {
            return ValueTask.FromResult(Failure<SensitiveAuthorizationRevocationReceipt>(
                ControllerErrorCode.InvalidRequest,
                "error.sensitive-authorization.revocation-invalid"));
        }

        return ValueTask.FromResult(Revoke(
            profileId,
            null,
            reason,
            token => token.ProfileId == profileId));
    }

    private ControllerResult<SensitiveAuthorizationRevocationReceipt> Revoke(
        ProfileId profileId,
        BrowserSessionId? sessionId,
        SensitiveAuthorizationRevocationReason reason,
        Func<SensitiveActionAuthorizationToken, bool> predicate)
    {
        lock (_gate)
        {
            var now = _clock.UtcNow;
            Prune(now);
            var ids = _active.Values.Where(predicate).Select(token => token.Id).ToArray();
            foreach (var id in ids)
            {
                _active.Remove(id);
                _handled[id] = now.AddMinutes(10);
            }

            return ControllerResult<SensitiveAuthorizationRevocationReceipt>.Success(
                new SensitiveAuthorizationRevocationReceipt(
                    profileId,
                    sessionId,
                    reason,
                    ids.Length,
                    now));
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var id in _active.Where(pair => pair.Value.ExpiresAtUtc <= now).Select(pair => pair.Key).ToArray())
        {
            _active.Remove(id);
            _handled[id] = now.AddMinutes(10);
        }

        PruneHandled(now);
    }

    private void PruneHandled(DateTimeOffset now)
    {
        foreach (var id in _handled.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray())
        {
            _handled.Remove(id);
        }
    }

    private static ControllerResult<T> Failure<T>(ControllerErrorCode code, string key) where T : class =>
        ControllerResult<T>.Failure(ControllerError.Create(code, key));
}
