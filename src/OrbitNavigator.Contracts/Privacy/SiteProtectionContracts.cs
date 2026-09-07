using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.Contracts.Privacy;

public readonly record struct ProtectionExceptionId(ProfileId ProfileId, Guid Value)
{
    public bool IsEmpty => ProfileId.IsEmpty || Value == Guid.Empty;
}

public enum SiteProtectionBaseline
{
    Strict = 0,
}

public enum ProtectionRelaxationDuration
{
    Temporary = 0,
    Session = 1,
    Persistent = 2,
}

public enum ProtectionExceptionSource
{
    TemporaryUserChoice = 0,
    SessionUserChoice = 1,
    PersistentUserChoice = 2,
}

public enum ProtectionNavigationDisposition
{
    Block = 0,
    Allow = 1,
}

public enum ProtectionEvaluationSource
{
    StrictBaseline = 0,
    TemporaryException = 1,
    SessionException = 2,
    PersistentException = 3,
    InvalidContext = 4,
    InvalidTarget = 5,
    EvaluatorFailure = 6,
}

public sealed record ProtectionRelaxationIntent(
    BrowsingContext Context,
    SiteIdentity Site,
    ProtectionRelaxationDuration Duration);

public sealed record RemoveProtectionExceptionIntent(
    PrivacyContext Context,
    ProtectionExceptionId ExceptionId);

public sealed record ProtectionException
{
    private ProtectionException(
        ProtectionExceptionId id,
        SiteIdentity site,
        BrowserSessionId? sessionId,
        ProtectionRelaxationDuration requestedDuration,
        ProtectionRelaxationDuration effectiveDuration,
        ProtectionExceptionSource source,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? expiresAtUtc)
    {
        Id = id;
        Site = site;
        SessionId = sessionId;
        RequestedDuration = requestedDuration;
        EffectiveDuration = effectiveDuration;
        Source = source;
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public ProtectionExceptionId Id { get; }

    public SiteIdentity Site { get; }

    public BrowserSessionId? SessionId { get; }

    public SiteProtectionBaseline Baseline => SiteProtectionBaseline.Strict;

    public ProtectionRelaxationDuration RequestedDuration { get; }

    public ProtectionRelaxationDuration EffectiveDuration { get; }

    public ProtectionExceptionSource Source { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset? ExpiresAtUtc { get; }

    public static ControllerResult<ProtectionException> Create(
        BrowsingContext context,
        ProtectionExceptionId id,
        SiteIdentity site,
        ProtectionRelaxationDuration requestedDuration,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? expiresAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(site);

        if (!context.IsStructurallyValid ||
            id.IsEmpty ||
            id.ProfileId != context.Privacy.ProfileId ||
            !Enum.IsDefined(requestedDuration))
        {
            return ControllerResult<ProtectionException>.Failure(ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "error.protection.exception_invalid"));
        }

        if (context.Privacy.IsPrivate && requestedDuration == ProtectionRelaxationDuration.Persistent)
        {
            return ControllerResult<ProtectionException>.Failure(ControllerError.Create(
                ControllerErrorCode.PolicyDenied,
                "error.protection.private_persistent_denied"));
        }

        if (requestedDuration == ProtectionRelaxationDuration.Temporary &&
            (expiresAtUtc is null || expiresAtUtc <= createdAtUtc))
        {
            return ControllerResult<ProtectionException>.Failure(ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "error.protection.temporary_expiry_invalid"));
        }

        if (requestedDuration != ProtectionRelaxationDuration.Temporary && expiresAtUtc is not null)
        {
            return ControllerResult<ProtectionException>.Failure(ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "error.protection.unexpected_expiry"));
        }

        var effectiveDuration = requestedDuration;
        BrowserSessionId? sessionId = effectiveDuration != ProtectionRelaxationDuration.Persistent
            ? context.Privacy.SessionId
            : null;
        var source = requestedDuration switch
        {
            ProtectionRelaxationDuration.Temporary => ProtectionExceptionSource.TemporaryUserChoice,
            ProtectionRelaxationDuration.Session => ProtectionExceptionSource.SessionUserChoice,
            ProtectionRelaxationDuration.Persistent => ProtectionExceptionSource.PersistentUserChoice,
            _ => throw new InvalidOperationException("Unsupported relaxation duration."),
        };

        return ControllerResult<ProtectionException>.Success(new ProtectionException(
            id,
            site,
            sessionId,
            requestedDuration,
            effectiveDuration,
            source,
            createdAtUtc,
            expiresAtUtc));
    }

    public bool IsVisibleTo(PrivacyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.IsStructurallyValid || context.ProfileId != Id.ProfileId)
        {
            return false;
        }

        if (context.IsPrivate)
        {
            return EffectiveDuration is ProtectionRelaxationDuration.Temporary or ProtectionRelaxationDuration.Session &&
                SessionId == context.SessionId;
        }

        return EffectiveDuration == ProtectionRelaxationDuration.Persistent ||
            SessionId == context.SessionId;
    }
}

public sealed record SiteProtectionState(
    BrowsingContext Context,
    SiteIdentity Site,
    SiteProtectionBaseline Baseline,
    ProtectionRelaxationDuration? EffectiveRelaxation,
    ProtectionEvaluationSource Source,
    ProtectionException? MatchingException);

public sealed class ProtectionEvaluation
{
    private ProtectionEvaluation(
        SiteIdentity? site,
        ProtectionNavigationDisposition disposition,
        SiteProtectionBaseline baseline,
        ProtectionEvaluationSource source,
        ProtectionExceptionId? exceptionId)
    {
        Site = site;
        Disposition = disposition;
        Baseline = baseline;
        Source = source;
        ExceptionId = exceptionId;
    }

    public SiteIdentity? Site { get; }

    public ProtectionNavigationDisposition Disposition { get; }

    public SiteProtectionBaseline Baseline { get; }

    public ProtectionEvaluationSource Source { get; }

    public ProtectionExceptionId? ExceptionId { get; }

    public static ProtectionEvaluation AllowUnderStrictBaseline(SiteIdentity site) =>
        new(
            site ?? throw new ArgumentNullException(nameof(site)),
            ProtectionNavigationDisposition.Allow,
            SiteProtectionBaseline.Strict,
            ProtectionEvaluationSource.StrictBaseline,
            null);

    public static ProtectionEvaluation AllowWithException(ProtectionException exception) =>
        new(
            (exception ?? throw new ArgumentNullException(nameof(exception))).Site,
            ProtectionNavigationDisposition.Allow,
            SiteProtectionBaseline.Strict,
            exception.EffectiveDuration switch
            {
                ProtectionRelaxationDuration.Temporary => ProtectionEvaluationSource.TemporaryException,
                ProtectionRelaxationDuration.Session => ProtectionEvaluationSource.SessionException,
                ProtectionRelaxationDuration.Persistent => ProtectionEvaluationSource.PersistentException,
                _ => throw new InvalidOperationException("Unsupported relaxation duration."),
            },
            exception.Id);

    public static ProtectionEvaluation BlockFailure(
        ProtectionEvaluationSource source,
        SiteIdentity? site = null)
    {
        if (source is not (
            ProtectionEvaluationSource.InvalidContext or
            ProtectionEvaluationSource.InvalidTarget or
            ProtectionEvaluationSource.EvaluatorFailure))
        {
            throw new ArgumentOutOfRangeException(nameof(source));
        }

        return new ProtectionEvaluation(
            site,
            ProtectionNavigationDisposition.Block,
            SiteProtectionBaseline.Strict,
            source,
            null);
    }
}

public interface ISiteProtectionController
{
    ValueTask<ControllerResult<SiteProtectionState>> GetStateAsync(
        BrowsingContext context,
        SiteIdentity site,
        CancellationToken cancellationToken);

    ValueTask<ControllerResult<ProtectionException>> RelaxAsync(
        ProtectionRelaxationIntent intent,
        CancellationToken cancellationToken);

    ValueTask<ControllerResult<IReadOnlyList<ProtectionException>>> ListExceptionsAsync(
        PrivacyContext context,
        CancellationToken cancellationToken);

    ValueTask<ControllerResult> RemoveExceptionAsync(
        RemoveProtectionExceptionIntent intent,
        CancellationToken cancellationToken);
}

public interface ISiteProtectionEvaluator
{
    ControllerResult<ProtectionEvaluation> Evaluate(
        BrowsingContext context,
        SiteIdentity target);
}
