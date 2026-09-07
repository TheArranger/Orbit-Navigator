using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.Contracts.Infrastructure;

public readonly record struct WindowsUserPresenceProofId(Guid Value)
{
    public bool IsEmpty => Value == Guid.Empty;
}

public enum WindowsUserPresencePurpose
{
    ForgottenOrbitPasswordVaultReset = 0,
}

public sealed class WindowsUserPresenceProof
{
    private WindowsUserPresenceProof(
        WindowsUserPresenceProofId proofId,
        WindowsUserPresencePurpose purpose,
        ProfileId profileId,
        BrowserSessionId sessionId,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        ProofId = proofId;
        Purpose = purpose;
        ProfileId = profileId;
        SessionId = sessionId;
        IssuedAtUtc = issuedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public WindowsUserPresenceProofId ProofId { get; }

    public WindowsUserPresencePurpose Purpose { get; }

    public ProfileId ProfileId { get; }

    public BrowserSessionId SessionId { get; }

    public DateTimeOffset IssuedAtUtc { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public static ControllerResult<WindowsUserPresenceProof> Create(
        WindowsUserPresenceProofId proofId,
        WindowsUserPresencePurpose purpose,
        ProfileId profileId,
        BrowserSessionId sessionId,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        if (proofId.IsEmpty ||
            !Enum.IsDefined(purpose) ||
            profileId.IsEmpty ||
            sessionId.IsEmpty ||
            expiresAtUtc <= issuedAtUtc)
        {
            return ControllerResult<WindowsUserPresenceProof>.Failure(ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "error.user_presence.proof_invalid"));
        }

        return ControllerResult<WindowsUserPresenceProof>.Success(new WindowsUserPresenceProof(
            proofId,
            purpose,
            profileId,
            sessionId,
            issuedAtUtc,
            expiresAtUtc));
    }
}

public sealed class WindowsUserPresenceIssueRequest
{
    private WindowsUserPresenceIssueRequest(
        PrivacyContext context,
        WindowsUserPresencePurpose purpose,
        TimeSpan maximumLifetime)
    {
        Context = context;
        Purpose = purpose;
        MaximumLifetime = maximumLifetime;
    }

    public PrivacyContext Context { get; }

    public ProfileId ProfileId => Context.ProfileId;

    public BrowserSessionId SessionId => Context.SessionId;

    public WindowsUserPresencePurpose Purpose { get; }

    public TimeSpan MaximumLifetime { get; }

    public bool IsStructurallyValid =>
        Context is { IsStructurallyValid: true, IsPrivate: false } &&
        Enum.IsDefined(Purpose) &&
        MaximumLifetime > TimeSpan.Zero &&
        MaximumLifetime <= TimeSpan.FromMinutes(5);

    public static ControllerResult<WindowsUserPresenceIssueRequest> Create(
        PrivacyContext context,
        WindowsUserPresencePurpose purpose,
        TimeSpan maximumLifetime)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.IsPrivate)
        {
            return ControllerResult<WindowsUserPresenceIssueRequest>.Failure(ControllerError.Create(
                ControllerErrorCode.PolicyDenied,
                "error.user_presence.private_issue_denied"));
        }

        var request = new WindowsUserPresenceIssueRequest(context, purpose, maximumLifetime);
        return request.IsStructurallyValid
            ? ControllerResult<WindowsUserPresenceIssueRequest>.Success(request)
            : ControllerResult<WindowsUserPresenceIssueRequest>.Failure(ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "error.user_presence.issue_invalid"));
    }
}

public sealed class WindowsUserPresenceValidationRequest
{
    private WindowsUserPresenceValidationRequest(
        PrivacyContext context,
        WindowsUserPresenceProof proof,
        WindowsUserPresencePurpose expectedPurpose)
    {
        Context = context;
        Proof = proof;
        ExpectedPurpose = expectedPurpose;
    }

    public PrivacyContext Context { get; }

    public WindowsUserPresenceProof Proof { get; }

    public ProfileId ExpectedProfileId => Context.ProfileId;

    public BrowserSessionId ExpectedSessionId => Context.SessionId;

    public WindowsUserPresencePurpose ExpectedPurpose { get; }

    public bool IsStructurallyValid =>
        Proof is not null &&
        Context is { IsStructurallyValid: true, IsPrivate: false } &&
        Enum.IsDefined(ExpectedPurpose);

    public static ControllerResult<WindowsUserPresenceValidationRequest> Create(
        PrivacyContext context,
        WindowsUserPresenceProof proof,
        WindowsUserPresencePurpose expectedPurpose)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(proof);
        if (context.IsPrivate)
        {
            return ControllerResult<WindowsUserPresenceValidationRequest>.Failure(ControllerError.Create(
                ControllerErrorCode.PolicyDenied,
                "error.user_presence.private_validation_denied"));
        }

        var request = new WindowsUserPresenceValidationRequest(context, proof, expectedPurpose);
        if (!request.IsStructurallyValid ||
            proof.ProfileId != context.ProfileId ||
            proof.SessionId != context.SessionId ||
            proof.Purpose != expectedPurpose)
        {
            return ControllerResult<WindowsUserPresenceValidationRequest>.Failure(ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "error.user_presence.validation_invalid"));
        }

        return ControllerResult<WindowsUserPresenceValidationRequest>.Success(request);
    }
}

public sealed record WindowsUserPresenceValidationReceipt(
    WindowsUserPresenceProofId ConsumedProofId,
    DateTimeOffset ConsumedAtUtc);

public interface IWindowsUserPresenceProofAdapter
{
    ValueTask<ControllerResult<WindowsUserPresenceProof>> IssueAsync(
        WindowsUserPresenceIssueRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<ControllerResult<WindowsUserPresenceValidationReceipt>> ValidateAndConsumeAsync(
        WindowsUserPresenceValidationRequest request,
        CancellationToken cancellationToken = default);
}

public enum WindowsUserPresenceInvalidationReason
{
    WindowsSessionLocked = 0,
    BrowserSessionEnded = 1,
    ProfileRemoved = 2,
    ApplicationStopping = 3,
}

public sealed record WindowsUserPresenceInvalidationReceipt(
    WindowsUserPresenceInvalidationReason Reason,
    int InvalidatedProofCount,
    DateTimeOffset CompletedAtUtc);

public interface IWindowsUserPresenceProofLedger
{
    ValueTask<ControllerResult<WindowsUserPresenceInvalidationReceipt>> InvalidateSessionAsync(
        ProfileId profileId,
        BrowserSessionId sessionId,
        WindowsUserPresenceInvalidationReason reason,
        CancellationToken cancellationToken = default);

    ValueTask<ControllerResult<WindowsUserPresenceInvalidationReceipt>> InvalidateAllAsync(
        WindowsUserPresenceInvalidationReason reason,
        CancellationToken cancellationToken = default);
}
