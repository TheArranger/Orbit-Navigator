using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Privacy;
using OrbitNavigator.Privacy.Persistence;

namespace OrbitNavigator.Privacy.Protection;

/// <summary>
/// Applies Orbit's strict protection baseline and owns exact-origin relaxation rules.
/// Session state is process-local, concurrency-safe, and bounded per profile. When
/// profile storage is supplied, normal-profile persistent rules use versioned CAS
/// documents and require explicit profile hydration before host use.
/// </summary>
public sealed class SiteProtectionController : ISiteProtectionController, ISiteProtectionEvaluator
{
    public static readonly TimeSpan DefaultTemporaryDuration = TimeSpan.FromMinutes(15);

    public const int DefaultMaximumExceptionsPerProfile = 1_024;

    private readonly IClock _clock;
    private readonly TimeSpan _temporaryDuration;
    private readonly int _maximumExceptionsPerProfile;
    private readonly PrivacyRulePersistence? _persistence;
    private readonly SemaphoreSlim _persistenceGate = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<ProtectionExceptionId, StoredException> _exceptions = [];
    private readonly Dictionary<ProfileId, DurableProfileState> _durableProfiles = [];
    private readonly HashSet<PrivateHydrationKey> _hydratedPrivateSessions = [];

    public SiteProtectionController(
        IClock clock,
        TimeSpan? temporaryDuration = null,
        int maximumExceptionsPerProfile = DefaultMaximumExceptionsPerProfile,
        IProfileStorage? profileStorage = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _temporaryDuration = temporaryDuration ?? DefaultTemporaryDuration;

        if (_temporaryDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(temporaryDuration),
                "Temporary protection exceptions require a positive duration.");
        }

        if (maximumExceptionsPerProfile <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumExceptionsPerProfile));
        }

        _maximumExceptionsPerProfile = maximumExceptionsPerProfile;
        _persistence = profileStorage is null
            ? null
            : new PrivacyRulePersistence(profileStorage);
    }

    public SiteProtectionController(
        IClock clock,
        IProfileStorage profileStorage,
        TimeSpan? temporaryDuration = null,
        int maximumExceptionsPerProfile = DefaultMaximumExceptionsPerProfile)
        : this(
            clock,
            temporaryDuration,
            maximumExceptionsPerProfile,
            profileStorage)
    {
    }

    public async ValueTask<ControllerResult> HydrateProfileAsync(
        PrivacyContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsValid(context))
        {
            return ControllerResult.Failure(
                InvalidRequest("error.protection.hydration_context_invalid"));
        }

        if (_persistence is null)
        {
            return ControllerResult.Success();
        }

        if (context.IsPrivate)
        {
            lock (_gate)
            {
                _hydratedPrivateSessions.Add(new PrivateHydrationKey(
                    context.ProfileId,
                    context.SessionId));
            }

            return ControllerResult.Success();
        }

        await _persistenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var read = await _persistence
                .ReadProtectionAsync(context, cancellationToken)
                .ConfigureAwait(false);
            if (!read.IsSuccess)
            {
                lock (_gate)
                {
                    RemovePersistentForProfileLocked(context.ProfileId);
                    _durableProfiles[context.ProfileId] =
                        DurableProfileState.Failed(read.Error!);
                }

                return ControllerResult.Failure(read.Error!);
            }

            if (read.Value!.Rules.Count > _maximumExceptionsPerProfile)
            {
                var error = ControllerError.Create(
                    ControllerErrorCode.IntegrityFailure,
                    "error.protection.persisted_rule_limit_exceeded");
                lock (_gate)
                {
                    RemovePersistentForProfileLocked(context.ProfileId);
                    _durableProfiles[context.ProfileId] =
                        DurableProfileState.Failed(error);
                }

                return ControllerResult.Failure(error);
            }

            lock (_gate)
            {
                RemovePersistentForProfileLocked(context.ProfileId);
                var nonPersistentCount = CountForProfileLocked(context.ProfileId);
                if (nonPersistentCount + read.Value.Rules.Count >
                    _maximumExceptionsPerProfile ||
                    read.Value.Rules.Any(rule => _exceptions.ContainsKey(rule.Id)))
                {
                    var error = ControllerError.Create(
                        ControllerErrorCode.IntegrityFailure,
                        "error.protection.persisted_rule_collision");
                    _durableProfiles[context.ProfileId] =
                        DurableProfileState.Failed(error);
                    return ControllerResult.Failure(error);
                }

                foreach (var rule in read.Value.Rules)
                {
                    _exceptions.Add(
                        rule.Id,
                        new StoredException(rule, BrowserProfileMode.Normal));
                }

                _durableProfiles[context.ProfileId] =
                    DurableProfileState.Ready(read.Value.Revision);
            }

            return ControllerResult.Success();
        }
        finally
        {
            _persistenceGate.Release();
        }
    }

    public ValueTask<ControllerResult<SiteProtectionState>> GetStateAsync(
        BrowsingContext context,
        SiteIdentity site,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsValid(context) || site is null)
        {
            return ValueTask.FromResult(ControllerResult<SiteProtectionState>.Failure(
                InvalidRequest("error.protection.state_invalid")));
        }

        ProtectionException? matching;
        lock (_gate)
        {
            var readinessError = ReadinessErrorLocked(context.Privacy);
            if (readinessError is not null)
            {
                return ValueTask.FromResult(
                    ControllerResult<SiteProtectionState>.Failure(readinessError));
            }

            var now = _clock.UtcNow;
            PruneExpiredLocked(now);
            matching = FindMatchingLocked(context.Privacy, site, now);
        }

        var state = new SiteProtectionState(
            context,
            site,
            SiteProtectionBaseline.Strict,
            matching?.EffectiveDuration,
            matching is null
                ? ProtectionEvaluationSource.StrictBaseline
                : SourceFor(matching.EffectiveDuration),
            matching);

        return ValueTask.FromResult(ControllerResult<SiteProtectionState>.Success(state));
    }

    public async ValueTask<ControllerResult<ProtectionException>> RelaxAsync(
        ProtectionRelaxationIntent intent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (intent is null ||
            !IsValid(intent.Context) ||
            intent.Site is null ||
            !Enum.IsDefined(intent.Duration))
        {
            return ControllerResult<ProtectionException>.Failure(
                InvalidRequest("error.protection.relaxation_invalid"));
        }

        if (intent.Context.Privacy.IsPrivate &&
            intent.Duration == ProtectionRelaxationDuration.Persistent)
        {
            return ControllerResult<ProtectionException>.Failure(
                ControllerError.Create(
                    ControllerErrorCode.PolicyDenied,
                    "error.protection.private_persistent_denied"));
        }

        if (_persistence is null ||
            intent.Duration != ProtectionRelaxationDuration.Persistent)
        {
            lock (_gate)
            {
                var readinessError = ReadinessErrorLocked(intent.Context.Privacy);
                if (readinessError is not null)
                {
                    return ControllerResult<ProtectionException>.Failure(readinessError);
                }

                var now = _clock.UtcNow;
                PruneExpiredLocked(now);
                RemoveSupersededLocked(intent);

                if (CountForProfileLocked(intent.Context.Privacy.ProfileId) >=
                    _maximumExceptionsPerProfile)
                {
                    return ControllerResult<ProtectionException>.Failure(
                        ExceptionLimitError());
                }

                var result = CreateExceptionLocked(intent, now);
                if (!result.IsSuccess)
                {
                    return result;
                }

                _exceptions.Add(
                    result.Value!.Id,
                    new StoredException(
                        result.Value,
                        intent.Context.Privacy.Mode));
                return result;
            }
        }

        await _persistenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProtectionException candidate;
            IReadOnlyList<ProtectionException> nextPersistent;
            ProfileStorageRevision? expectedRevision;
            lock (_gate)
            {
                var readinessError = ReadinessErrorLocked(intent.Context.Privacy);
                if (readinessError is not null)
                {
                    return ControllerResult<ProtectionException>.Failure(readinessError);
                }

                var now = _clock.UtcNow;
                PruneExpiredLocked(now);
                var superseded = _exceptions.Values.Count(stored =>
                    IsSameRuleSlot(stored, intent));
                if (CountForProfileLocked(intent.Context.Privacy.ProfileId) -
                    superseded >= _maximumExceptionsPerProfile)
                {
                    return ControllerResult<ProtectionException>.Failure(
                        ExceptionLimitError());
                }

                var created = CreateExceptionLocked(intent, now);
                if (!created.IsSuccess)
                {
                    return created;
                }

                candidate = created.Value!;
                nextPersistent = PersistentForProfileLocked(
                    intent.Context.Privacy.ProfileId)
                    .Where(value => !value.Site.Equals(intent.Site))
                    .Append(candidate)
                    .ToArray();
                expectedRevision = _durableProfiles[
                    intent.Context.Privacy.ProfileId].Revision;
            }

            var write = await _persistence.WriteProtectionAsync(
                intent.Context.Privacy,
                nextPersistent,
                expectedRevision,
                cancellationToken).ConfigureAwait(false);
            if (!write.IsSuccess)
            {
                return ControllerResult<ProtectionException>.Failure(write.Error!);
            }

            lock (_gate)
            {
                RemovePersistentForProfileLocked(intent.Context.Privacy.ProfileId);
                foreach (var rule in nextPersistent)
                {
                    _exceptions.Add(
                        rule.Id,
                        new StoredException(rule, BrowserProfileMode.Normal));
                }

                _durableProfiles[intent.Context.Privacy.ProfileId] =
                    DurableProfileState.Ready(write.Value!.Revision);
            }

            return ControllerResult<ProtectionException>.Success(candidate);
        }
        finally
        {
            _persistenceGate.Release();
        }
    }

    public ValueTask<ControllerResult<IReadOnlyList<ProtectionException>>> ListExceptionsAsync(
        PrivacyContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsValid(context))
        {
            return ValueTask.FromResult(
                ControllerResult<IReadOnlyList<ProtectionException>>.Failure(
                    InvalidRequest("error.protection.context_invalid")));
        }

        IReadOnlyList<ProtectionException> visible;
        lock (_gate)
        {
            var readinessError = ReadinessErrorLocked(context);
            if (readinessError is not null)
            {
                return ValueTask.FromResult(
                    ControllerResult<IReadOnlyList<ProtectionException>>.Failure(
                        readinessError));
            }

            var now = _clock.UtcNow;
            PruneExpiredLocked(now);
            visible = _exceptions.Values
                .Where(stored => IsVisible(stored, context, now))
                .Select(stored => stored.Exception)
                .OrderBy(value => value.Site.CanonicalOrigin, StringComparer.Ordinal)
                .ThenBy(value => value.EffectiveDuration)
                .ThenByDescending(value => value.CreatedAtUtc)
                .ToArray();
        }

        return ValueTask.FromResult(
            ControllerResult<IReadOnlyList<ProtectionException>>.Success(visible));
    }

    public async ValueTask<ControllerResult> RemoveExceptionAsync(
        RemoveProtectionExceptionIntent intent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (intent is null ||
            !IsValid(intent.Context) ||
            intent.ExceptionId.IsEmpty ||
            intent.ExceptionId.ProfileId != intent.Context.ProfileId)
        {
            return ControllerResult.Failure(
                InvalidRequest("error.protection.remove_invalid"));
        }

        if (_persistence is null)
        {
            lock (_gate)
            {
                var now = _clock.UtcNow;
                PruneExpiredLocked(now);

                if (!_exceptions.TryGetValue(intent.ExceptionId, out var stored) ||
                    !IsVisible(stored, intent.Context, now))
                {
                    return ControllerResult.Failure(ExceptionNotFoundError());
                }

                _exceptions.Remove(intent.ExceptionId);
                return ControllerResult.Success();
            }
        }

        lock (_gate)
        {
            var readinessError = ReadinessErrorLocked(intent.Context);
            if (readinessError is not null)
            {
                return ControllerResult.Failure(readinessError);
            }

            var now = _clock.UtcNow;
            PruneExpiredLocked(now);
            if (!_exceptions.TryGetValue(intent.ExceptionId, out var stored) ||
                !IsVisible(stored, intent.Context, now))
            {
                return ControllerResult.Failure(ExceptionNotFoundError());
            }

            if (stored.Exception.EffectiveDuration !=
                ProtectionRelaxationDuration.Persistent)
            {
                _exceptions.Remove(intent.ExceptionId);
                return ControllerResult.Success();
            }
        }

        await _persistenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<ProtectionException> nextPersistent;
            ProfileStorageRevision? expectedRevision;
            lock (_gate)
            {
                var readinessError = ReadinessErrorLocked(intent.Context);
                if (readinessError is not null)
                {
                    return ControllerResult.Failure(readinessError);
                }

                if (!_exceptions.TryGetValue(intent.ExceptionId, out var stored) ||
                    !IsVisible(stored, intent.Context, _clock.UtcNow) ||
                    stored.Exception.EffectiveDuration !=
                        ProtectionRelaxationDuration.Persistent)
                {
                    return ControllerResult.Failure(ExceptionNotFoundError());
                }

                nextPersistent = PersistentForProfileLocked(intent.Context.ProfileId)
                    .Where(value => value.Id != intent.ExceptionId)
                    .ToArray();
                expectedRevision = _durableProfiles[intent.Context.ProfileId].Revision;
            }

            var write = await _persistence.WriteProtectionAsync(
                intent.Context,
                nextPersistent,
                expectedRevision,
                cancellationToken).ConfigureAwait(false);
            if (!write.IsSuccess)
            {
                return ControllerResult.Failure(write.Error!);
            }

            lock (_gate)
            {
                _exceptions.Remove(intent.ExceptionId);
                _durableProfiles[intent.Context.ProfileId] =
                    DurableProfileState.Ready(write.Value!.Revision);
            }

            return ControllerResult.Success();
        }
        finally
        {
            _persistenceGate.Release();
        }
    }

    public ControllerResult<ProtectionEvaluation> Evaluate(
        BrowsingContext context,
        SiteIdentity target)
    {
        if (!IsValid(context))
        {
            return ControllerResult<ProtectionEvaluation>.Success(
                ProtectionEvaluation.BlockFailure(
                    ProtectionEvaluationSource.InvalidContext,
                    target));
        }

        if (target is null)
        {
            return ControllerResult<ProtectionEvaluation>.Success(
                ProtectionEvaluation.BlockFailure(
                    ProtectionEvaluationSource.InvalidTarget));
        }

        try
        {
            lock (_gate)
            {
                if (ReadinessErrorLocked(context.Privacy) is not null)
                {
                    return ControllerResult<ProtectionEvaluation>.Success(
                        ProtectionEvaluation.AllowUnderStrictBaseline(target));
                }

                var now = _clock.UtcNow;
                PruneExpiredLocked(now);
                var matching = FindMatchingLocked(context.Privacy, target, now);
                return ControllerResult<ProtectionEvaluation>.Success(
                    matching is null
                        ? ProtectionEvaluation.AllowUnderStrictBaseline(target)
                        : ProtectionEvaluation.AllowWithException(matching));
            }
        }
        catch (Exception)
        {
            return ControllerResult<ProtectionEvaluation>.Success(
                ProtectionEvaluation.BlockFailure(
                    ProtectionEvaluationSource.EvaluatorFailure,
                    target));
        }
    }

    private static bool IsValid(BrowsingContext? context) =>
        context is { IsStructurallyValid: true };

    private static bool IsValid(PrivacyContext? context) =>
        context is { IsStructurallyValid: true };

    private static ControllerError InvalidRequest(string messageKey) =>
        ControllerError.Create(ControllerErrorCode.InvalidRequest, messageKey);

    private ControllerError? ReadinessErrorLocked(PrivacyContext context)
    {
        if (_persistence is null)
        {
            return null;
        }

        if (context.IsPrivate)
        {
            return _hydratedPrivateSessions.Contains(new PrivateHydrationKey(
                context.ProfileId,
                context.SessionId))
                ? null
                : ControllerError.Create(
                    ControllerErrorCode.Unavailable,
                    "error.protection.profile_not_hydrated");
        }

        if (!_durableProfiles.TryGetValue(context.ProfileId, out var state))
        {
            return ControllerError.Create(
                ControllerErrorCode.Unavailable,
                "error.protection.profile_not_hydrated");
        }

        return state.Error;
    }

    private ControllerResult<ProtectionException> CreateExceptionLocked(
        ProtectionRelaxationIntent intent,
        DateTimeOffset now) =>
        ProtectionException.Create(
            intent.Context,
            CreateUniqueIdLocked(intent.Context.Privacy.ProfileId),
            intent.Site,
            intent.Duration,
            now,
            intent.Duration == ProtectionRelaxationDuration.Temporary
                ? now.Add(_temporaryDuration)
                : null);

    private ControllerError ExceptionLimitError() =>
        ControllerError.Create(
            ControllerErrorCode.Conflict,
            "error.protection.exception_limit_reached",
            [ControllerMessageArgument.Count(
                ControllerMessageArgumentKey.MaximumCount,
                _maximumExceptionsPerProfile)]);

    private static ControllerError ExceptionNotFoundError() =>
        ControllerError.Create(
            ControllerErrorCode.NotFound,
            "error.protection.exception_not_found");

    private IReadOnlyList<ProtectionException> PersistentForProfileLocked(
        ProfileId profileId) =>
        _exceptions.Values
            .Where(stored =>
                stored.Mode == BrowserProfileMode.Normal &&
                stored.Exception.Id.ProfileId == profileId &&
                stored.Exception.EffectiveDuration ==
                    ProtectionRelaxationDuration.Persistent)
            .Select(stored => stored.Exception)
            .ToArray();

    private void RemovePersistentForProfileLocked(ProfileId profileId)
    {
        foreach (var id in _exceptions
            .Where(pair =>
                pair.Value.Mode == BrowserProfileMode.Normal &&
                pair.Value.Exception.Id.ProfileId == profileId &&
                pair.Value.Exception.EffectiveDuration ==
                    ProtectionRelaxationDuration.Persistent)
            .Select(pair => pair.Key)
            .ToArray())
        {
            _exceptions.Remove(id);
        }
    }

    private static ProtectionEvaluationSource SourceFor(
        ProtectionRelaxationDuration duration) =>
        duration switch
        {
            ProtectionRelaxationDuration.Temporary =>
                ProtectionEvaluationSource.TemporaryException,
            ProtectionRelaxationDuration.Session =>
                ProtectionEvaluationSource.SessionException,
            ProtectionRelaxationDuration.Persistent =>
                ProtectionEvaluationSource.PersistentException,
            _ => ProtectionEvaluationSource.EvaluatorFailure,
        };

    private int CountForProfileLocked(ProfileId profileId) =>
        _exceptions.Values.Count(value => value.Exception.Id.ProfileId == profileId);

    private ProtectionExceptionId CreateUniqueIdLocked(ProfileId profileId)
    {
        ProtectionExceptionId id;
        do
        {
            id = new ProtectionExceptionId(profileId, Guid.NewGuid());
        }
        while (_exceptions.ContainsKey(id));

        return id;
    }

    private void PruneExpiredLocked(DateTimeOffset now)
    {
        foreach (var id in _exceptions
            .Where(pair => IsExpired(pair.Value.Exception, now))
            .Select(pair => pair.Key)
            .ToArray())
        {
            _exceptions.Remove(id);
        }
    }

    private void RemoveSupersededLocked(ProtectionRelaxationIntent intent)
    {
        foreach (var id in _exceptions
            .Where(pair => IsSameRuleSlot(pair.Value, intent))
            .Select(pair => pair.Key)
            .ToArray())
        {
            _exceptions.Remove(id);
        }
    }

    private static bool IsSameRuleSlot(
        StoredException stored,
        ProtectionRelaxationIntent intent)
    {
        var value = stored.Exception;
        if (stored.Mode != intent.Context.Privacy.Mode ||
            value.Id.ProfileId != intent.Context.Privacy.ProfileId ||
            !value.Site.Equals(intent.Site) ||
            value.EffectiveDuration != intent.Duration)
        {
            return false;
        }

        return intent.Duration == ProtectionRelaxationDuration.Persistent ||
            value.SessionId == intent.Context.Privacy.SessionId;
    }

    private ProtectionException? FindMatchingLocked(
        PrivacyContext context,
        SiteIdentity site,
        DateTimeOffset now) =>
        _exceptions.Values
            .Where(stored =>
                stored.Exception.Site.Equals(site) &&
                IsVisible(stored, context, now))
            .OrderByDescending(stored => Priority(stored.Exception.EffectiveDuration))
            .ThenByDescending(stored => stored.Exception.CreatedAtUtc)
            .Select(stored => stored.Exception)
            .FirstOrDefault();

    private static bool IsVisible(
        StoredException stored,
        PrivacyContext context,
        DateTimeOffset now)
    {
        var value = stored.Exception;
        if (stored.Mode != context.Mode ||
            value.Id.ProfileId != context.ProfileId ||
            IsExpired(value, now))
        {
            return false;
        }

        if (value.EffectiveDuration == ProtectionRelaxationDuration.Persistent)
        {
            return !context.IsPrivate;
        }

        return value.SessionId == context.SessionId;
    }

    private static bool IsExpired(ProtectionException value, DateTimeOffset now) =>
        value.ExpiresAtUtc is { } expiresAtUtc && expiresAtUtc <= now;

    private static int Priority(ProtectionRelaxationDuration duration) =>
        duration switch
        {
            ProtectionRelaxationDuration.Temporary => 3,
            ProtectionRelaxationDuration.Session => 2,
            ProtectionRelaxationDuration.Persistent => 1,
            _ => 0,
        };

    private sealed record StoredException(
        ProtectionException Exception,
        BrowserProfileMode Mode);

    private sealed record DurableProfileState(
        ProfileStorageRevision? Revision,
        ControllerError? Error)
    {
        public static DurableProfileState Ready(ProfileStorageRevision? revision) =>
            new(revision, null);

        public static DurableProfileState Failed(ControllerError error) =>
            new(null, error ?? throw new ArgumentNullException(nameof(error)));
    }

    private readonly record struct PrivateHydrationKey(
        ProfileId ProfileId,
        BrowserSessionId SessionId);
}
