using System.Collections.Concurrent;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;

namespace OrbitNavigator.Foundation.Security;

public interface IWindowsUserPresenceVerifier
{
    ValueTask<bool> VerifyAsync(
        WindowsUserPresencePurpose purpose,
        CancellationToken cancellationToken = default);
}

public sealed class FailClosedWindowsUserPresenceVerifier : IWindowsUserPresenceVerifier
{
    public ValueTask<bool> VerifyAsync(
        WindowsUserPresencePurpose purpose,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(false);
}

public sealed class WindowsUserPresenceProofAdapter :
    IWindowsUserPresenceProofAdapter,
    IWindowsUserPresenceProofLedger
{
    private readonly IWindowsUserPresenceVerifier _verifier;
    private readonly IClock _clock;
    private readonly ConcurrentDictionary<WindowsUserPresenceProofId, WindowsUserPresenceProof> _proofs = new();

    public WindowsUserPresenceProofAdapter(
        IWindowsUserPresenceVerifier verifier,
        IClock clock)
    {
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask<ControllerResult<WindowsUserPresenceProof>> IssueAsync(
        WindowsUserPresenceIssueRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.IsStructurallyValid)
        {
            return ControllerResult<WindowsUserPresenceProof>.Failure(ControllerError.Create(
                request.Context.IsPrivate ? ControllerErrorCode.PolicyDenied : ControllerErrorCode.InvalidRequest,
                "error.user_presence.issue_invalid"));
        }

        if (!await _verifier.VerifyAsync(request.Purpose, cancellationToken).ConfigureAwait(false))
        {
            return ControllerResult<WindowsUserPresenceProof>.Failure(ControllerError.Create(
                ControllerErrorCode.PolicyDenied,
                "error.user_presence.not_verified"));
        }

        var now = _clock.UtcNow;
        var proofResult = WindowsUserPresenceProof.Create(
            new WindowsUserPresenceProofId(Guid.NewGuid()),
            request.Purpose,
            request.ProfileId,
            request.SessionId,
            now,
            now.Add(request.MaximumLifetime));
        if (proofResult.IsSuccess)
        {
            _proofs[proofResult.Value!.ProofId] = proofResult.Value;
        }

        return proofResult;
    }

    public ValueTask<ControllerResult<WindowsUserPresenceValidationReceipt>> ValidateAndConsumeAsync(
        WindowsUserPresenceValidationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        if (!request.IsStructurallyValid || request.Context.IsPrivate)
        {
            return ValueTask.FromResult(ControllerResult<WindowsUserPresenceValidationReceipt>.Failure(
                ControllerError.Create(
                    request.Context.IsPrivate ? ControllerErrorCode.PolicyDenied : ControllerErrorCode.InvalidRequest,
                    "error.user_presence.validation_invalid")));
        }

        if (!_proofs.TryRemove(request.Proof.ProofId, out var proof))
        {
            return ValueTask.FromResult(ControllerResult<WindowsUserPresenceValidationReceipt>.Failure(
                ControllerError.Create(
                    ControllerErrorCode.AlreadyHandled,
                    "error.user_presence.already_consumed")));
        }

        var now = _clock.UtcNow;
        if (proof.ExpiresAtUtc <= now ||
            proof.ProfileId != request.ExpectedProfileId ||
            proof.SessionId != request.ExpectedSessionId ||
            proof.Purpose != request.ExpectedPurpose)
        {
            return ValueTask.FromResult(ControllerResult<WindowsUserPresenceValidationReceipt>.Failure(
                ControllerError.Create(
                    proof.ExpiresAtUtc <= now ? ControllerErrorCode.Expired : ControllerErrorCode.PolicyDenied,
                    "error.user_presence.proof_rejected")));
        }

        return ValueTask.FromResult(ControllerResult<WindowsUserPresenceValidationReceipt>.Success(
            new WindowsUserPresenceValidationReceipt(proof.ProofId, now)));
    }

    public ValueTask<ControllerResult<WindowsUserPresenceInvalidationReceipt>> InvalidateSessionAsync(
        ProfileId profileId,
        BrowserSessionId sessionId,
        WindowsUserPresenceInvalidationReason reason,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var removed = 0;
        foreach (var item in _proofs)
        {
            if (item.Value.ProfileId == profileId &&
                item.Value.SessionId == sessionId &&
                _proofs.TryRemove(item.Key, out _))
            {
                removed++;
            }
        }

        return ValueTask.FromResult(Receipt(reason, removed));
    }

    public ValueTask<ControllerResult<WindowsUserPresenceInvalidationReceipt>> InvalidateAllAsync(
        WindowsUserPresenceInvalidationReason reason,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var removed = 0;
        foreach (var item in _proofs)
        {
            if (_proofs.TryRemove(item.Key, out _))
            {
                removed++;
            }
        }

        return ValueTask.FromResult(Receipt(reason, removed));
    }

    private ControllerResult<WindowsUserPresenceInvalidationReceipt> Receipt(
        WindowsUserPresenceInvalidationReason reason,
        int count) =>
        ControllerResult<WindowsUserPresenceInvalidationReceipt>.Success(
            new WindowsUserPresenceInvalidationReceipt(reason, count, _clock.UtcNow));
}
