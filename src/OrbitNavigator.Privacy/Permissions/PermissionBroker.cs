using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Privacy;
using OrbitNavigator.Privacy.Persistence;

namespace OrbitNavigator.Privacy.Permissions;

/// <summary>
/// Owns permission prompts and Orbit permission rules. WebView2 is only ever
/// given one-shot completions with SaveInProfile=false; this bounded first-template
/// store keeps Session rules in memory. When profile storage is supplied,
/// normal-profile Persistent rules use versioned CAS documents and explicit
/// profile hydration.
/// </summary>
public sealed class PermissionBroker : IPermissionBroker
{
    public const int DefaultMaximumPendingRequests = 256;
    public const int DefaultMaximumRulesPerProfile = 1_024;
    public const int DefaultMaximumHandledResponses = 2_048;

    private readonly IClock _clock;
    private readonly IPermissionHostCompletionSink _completionSink;
    private readonly int _maximumPendingRequests;
    private readonly int _maximumRulesPerProfile;
    private readonly int _maximumHandledResponses;
    private readonly PrivacyRulePersistence? _persistence;
    private readonly SemaphoreSlim _persistenceGate = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<RequestId, PendingRequest> _pending = [];
    private readonly Dictionary<PermissionRuleId, StoredRule> _rules = [];
    private readonly HashSet<RequestId> _handledRequestIds = [];
    private readonly HashSet<ResponseToken> _handledResponseTokens = [];
    private readonly Queue<HandledResponse> _handledOrder = [];
    private readonly HashSet<RequestId> _processingPersistent = [];
    private readonly Dictionary<ProfileId, DurableProfileState> _durableProfiles = [];
    private readonly HashSet<PrivateHydrationKey> _hydratedPrivateSessions = [];

    public PermissionBroker(
        IClock clock,
        IPermissionHostCompletionSink completionSink,
        int maximumPendingRequests = DefaultMaximumPendingRequests,
        int maximumRulesPerProfile = DefaultMaximumRulesPerProfile,
        int maximumHandledResponses = DefaultMaximumHandledResponses,
        IProfileStorage? profileStorage = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _completionSink = completionSink ?? throw new ArgumentNullException(nameof(completionSink));

        if (maximumPendingRequests <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPendingRequests));
        }

        if (maximumRulesPerProfile <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRulesPerProfile));
        }

        if (maximumHandledResponses <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumHandledResponses));
        }

        _maximumPendingRequests = maximumPendingRequests;
        _maximumRulesPerProfile = maximumRulesPerProfile;
        _maximumHandledResponses = maximumHandledResponses;
        _persistence = profileStorage is null
            ? null
            : new PrivacyRulePersistence(profileStorage);
    }

    public PermissionBroker(
        IClock clock,
        IPermissionHostCompletionSink completionSink,
        IProfileStorage profileStorage,
        int maximumPendingRequests = DefaultMaximumPendingRequests,
        int maximumRulesPerProfile = DefaultMaximumRulesPerProfile,
        int maximumHandledResponses = DefaultMaximumHandledResponses)
        : this(
            clock,
            completionSink,
            maximumPendingRequests,
            maximumRulesPerProfile,
            maximumHandledResponses,
            profileStorage)
    {
    }

    public event EventHandler<PermissionPromptEventArgs>? PromptRequested;

    public async ValueTask<ControllerResult> HydrateProfileAsync(
        PrivacyContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context is not { IsStructurallyValid: true })
        {
            return ControllerResult.Failure(
                InvalidRequest("error.permission.hydration_context_invalid"));
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
                .ReadPermissionsAsync(context, cancellationToken)
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

            if (read.Value!.Rules.Count > _maximumRulesPerProfile)
            {
                var error = ControllerError.Create(
                    ControllerErrorCode.IntegrityFailure,
                    "error.permission.persisted_rule_limit_exceeded");
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
                    _maximumRulesPerProfile ||
                    read.Value.Rules.Any(rule => _rules.ContainsKey(rule.Id)))
                {
                    var error = ControllerError.Create(
                        ControllerErrorCode.IntegrityFailure,
                        "error.permission.persisted_rule_collision");
                    _durableProfiles[context.ProfileId] =
                        DurableProfileState.Failed(error);
                    return ControllerResult.Failure(error);
                }

                foreach (var rule in read.Value.Rules)
                {
                    _rules.Add(
                        rule.Id,
                        new StoredRule(rule, BrowserProfileMode.Normal));
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

    public async ValueTask<ControllerResult<PermissionPromptState>> IngestAsync(
        PermissionBrokerRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var validationError = ValidateRequest(request);
        if (validationError is not null)
        {
            if (request is not null)
            {
                lock (_gate)
                {
                    if (IsKnownRequestOrTokenLocked(
                        request.RequestId,
                        request.ResponseToken))
                    {
                        return ControllerResult<PermissionPromptState>.Failure(
                            ControllerError.Create(
                                ControllerErrorCode.AlreadyHandled,
                                "error.permission.request_replayed"));
                    }

                    if (!request.RequestId.IsEmpty &&
                        !request.ResponseToken.IsEmpty)
                    {
                        MarkHandledLocked(
                            request.RequestId,
                            request.ResponseToken);
                    }
                }

                await CompleteFailureAsync(
                    request,
                    request.Capability == WebPermissionCapability.Unknown
                        ? PermissionDecisionSource.HostFailure
                        : PermissionDecisionSource.DefaultPolicy,
                    cancellationToken).ConfigureAwait(false);
            }

            return ControllerResult<PermissionPromptState>.Failure(validationError);
        }

        var supportedScopes = request.SupportedAllowScopes
            .Distinct()
            .Where(scope =>
                !request.Context.Privacy.IsPrivate ||
                scope != PermissionAllowScope.Persistent)
            .ToArray();
        var normalizedRequest = request with { SupportedAllowScopes = supportedScopes };
        var prompt = new PermissionPromptState(
            request.RequestId,
            request.ResponseToken,
            request.Context,
            request.RequestingSite,
            request.RequestingSite.DisplayOrigin,
            request.Capability,
            supportedScopes,
            request.CurrentDecision,
            request.DefaultDecision,
            request.ExpiresAtUtc);

        ControllerError? intakeFailure = null;
        PermissionDecisionSource failureSource = PermissionDecisionSource.HostFailure;
        var mustCompleteFailure = false;

        lock (_gate)
        {
            var now = _clock.UtcNow;
            PruneExpiredRulesLocked(now);

            if (IsKnownRequestOrTokenLocked(
                request.RequestId,
                request.ResponseToken))
            {
                return ControllerResult<PermissionPromptState>.Failure(
                    ControllerError.Create(
                        ControllerErrorCode.AlreadyHandled,
                        "error.permission.request_replayed"));
            }

            var readinessError = ReadinessErrorLocked(request.Context.Privacy);
            if (readinessError is not null)
            {
                MarkHandledLocked(request.RequestId, request.ResponseToken);
                intakeFailure = readinessError;
                failureSource = PermissionDecisionSource.DefaultPolicy;
                mustCompleteFailure = true;
            }
            else if (now >= request.ExpiresAtUtc)
            {
                MarkHandledLocked(request.RequestId, request.ResponseToken);
                intakeFailure = ControllerError.Create(
                    ControllerErrorCode.Expired,
                    "error.permission.request_expired");
                failureSource = PermissionDecisionSource.RequestExpired;
                mustCompleteFailure = true;
            }
            else if (_pending.Count >= _maximumPendingRequests)
            {
                MarkHandledLocked(request.RequestId, request.ResponseToken);
                intakeFailure = ControllerError.Create(
                    ControllerErrorCode.Unavailable,
                    "error.permission.pending_limit_reached",
                    [ControllerMessageArgument.Count(
                        ControllerMessageArgumentKey.MaximumCount,
                        _maximumPendingRequests)],
                    isRetryable: true);
                mustCompleteFailure = true;
            }
            else
            {
                _pending.Add(request.RequestId, new PendingRequest(normalizedRequest));
            }
        }

        if (mustCompleteFailure)
        {
            await CompleteFailureAsync(
                request,
                failureSource,
                cancellationToken).ConfigureAwait(false);
            return ControllerResult<PermissionPromptState>.Failure(intakeFailure!);
        }

        try
        {
            PromptRequested?.Invoke(this, new PermissionPromptEventArgs(prompt));
            return ControllerResult<PermissionPromptState>.Success(prompt);
        }
        catch (Exception)
        {
            lock (_gate)
            {
                _pending.Remove(request.RequestId);
                MarkHandledLocked(request.RequestId, request.ResponseToken);
            }

            await CompleteFailureAsync(
                request,
                PermissionDecisionSource.HostFailure,
                cancellationToken).ConfigureAwait(false);
            return ControllerResult<PermissionPromptState>.Failure(
                ControllerError.Create(
                    ControllerErrorCode.InternalFailure,
                    "error.permission.prompt_dispatch_failed"));
        }
    }

    public async ValueTask<ControllerResult<PermissionHostCompletion>> RespondAsync(
        PermissionResponse response,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsValid(response))
        {
            return ControllerResult<PermissionHostCompletion>.Failure(
                InvalidRequest("error.permission.response_invalid"));
        }

        if (_persistence is not null &&
            response.Decision == PermissionDecision.Allow &&
            response.AllowScope == PermissionAllowScope.Persistent)
        {
            return await RespondPersistentAsync(response, cancellationToken)
                .ConfigureAwait(false);
        }

        PendingRequest pending;
        PermissionHostCompletion? completion = null;
        ControllerError? responseFailure = null;
        PermissionRule? createdRule = null;
        IReadOnlyList<StoredRule> supersededRules = [];

        lock (_gate)
        {
            if (_processingPersistent.Contains(response.RequestId))
            {
                return ControllerResult<PermissionHostCompletion>.Failure(
                    ControllerError.Create(
                        ControllerErrorCode.AlreadyHandled,
                        "error.permission.response_in_progress"));
            }

            if (_handledRequestIds.Contains(response.RequestId) ||
                _handledResponseTokens.Contains(response.ResponseToken))
            {
                return ControllerResult<PermissionHostCompletion>.Failure(
                    ControllerError.Create(
                        ControllerErrorCode.AlreadyHandled,
                        "error.permission.response_replayed"));
            }

            if (!_pending.TryGetValue(response.RequestId, out pending!))
            {
                return ControllerResult<PermissionHostCompletion>.Failure(
                    ControllerError.Create(
                        ControllerErrorCode.NotFound,
                        "error.permission.request_not_found"));
            }

            if (pending.Request.ResponseToken != response.ResponseToken ||
                !ContextsMatch(pending.Request.Context, response.Context))
            {
                return ControllerResult<PermissionHostCompletion>.Failure(
                    InvalidRequest("error.permission.response_binding_invalid"));
            }

            var now = _clock.UtcNow;
            if (now >= pending.Request.ExpiresAtUtc)
            {
                ConsumePendingLocked(pending.Request);
                completion = PermissionHostCompletion.FailClosed(
                    pending.Request.RequestId,
                    pending.Request.Context.TabId,
                    pending.Request.Capability,
                    PermissionDecisionSource.RequestExpired);
            }
            else
            {
                responseFailure = ValidateResponseChoice(pending.Request, response);
                if (responseFailure is not null)
                {
                    ConsumePendingLocked(pending.Request);
                    completion = PermissionHostCompletion.FailClosed(
                        pending.Request.RequestId,
                        pending.Request.Context.TabId,
                        pending.Request.Capability,
                        PermissionDecisionSource.DefaultPolicy);
                }
                else if (response.Decision == PermissionDecision.Allow)
                {
                    var scope = response.AllowScope!.Value;
                    createdRule = CreateRuleLocked(pending.Request, scope, now);
                    if (scope != PermissionAllowScope.Once &&
                        !CanStoreRuleLocked(
                            createdRule,
                            pending.Request.Context.Privacy.Mode))
                    {
                        createdRule = null;
                        responseFailure = ControllerError.Create(
                            ControllerErrorCode.Conflict,
                            "error.permission.rule_limit_reached",
                            [ControllerMessageArgument.Count(
                                ControllerMessageArgumentKey.MaximumCount,
                                _maximumRulesPerProfile)]);
                        ConsumePendingLocked(pending.Request);
                        completion = PermissionHostCompletion.FailClosed(
                            pending.Request.RequestId,
                            pending.Request.Context.TabId,
                            pending.Request.Capability,
                            PermissionDecisionSource.DefaultPolicy);
                    }
                    else
                    {
                        if (scope != PermissionAllowScope.Once)
                        {
                            supersededRules = StoreRuleLocked(
                                createdRule,
                                pending.Request.Context.Privacy.Mode);
                        }

                        ConsumePendingLocked(pending.Request);
                        completion = PermissionHostCompletion.FromAcceptedResponse(
                            pending.Request.RequestId,
                            pending.Request.Context.TabId,
                            pending.Request.Capability,
                            PermissionDecision.Allow,
                            createdRule);
                    }
                }
                else
                {
                    ConsumePendingLocked(pending.Request);
                    completion = PermissionHostCompletion.FromAcceptedResponse(
                        pending.Request.RequestId,
                        pending.Request.Context.TabId,
                        pending.Request.Capability,
                        PermissionDecision.Deny);
                }
            }
        }

        var sinkResult = await CompleteHostAsync(
            completion!,
            cancellationToken).ConfigureAwait(false);
        if (!sinkResult.IsSuccess)
        {
            if (createdRule is not null &&
                createdRule.Scope != PermissionAllowScope.Once)
            {
                lock (_gate)
                {
                    if (_rules.Remove(createdRule.Id))
                    {
                        RestoreSupersededRulesLocked(supersededRules);
                    }
                }
            }

            return ControllerResult<PermissionHostCompletion>.Failure(sinkResult.Error!);
        }

        return responseFailure is null
            ? ControllerResult<PermissionHostCompletion>.Success(completion!)
            : ControllerResult<PermissionHostCompletion>.Failure(responseFailure);
    }

    public ValueTask<ControllerResult<SitePermissionState>> GetCurrentSiteStateAsync(
        BrowsingContext context,
        SiteIdentity site,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsValid(context) || site is null)
        {
            return ValueTask.FromResult(ControllerResult<SitePermissionState>.Failure(
                InvalidRequest("error.permission.state_invalid")));
        }

        IReadOnlyList<PermissionRule> visible;
        lock (_gate)
        {
            var readinessError = ReadinessErrorLocked(context.Privacy);
            if (readinessError is not null)
            {
                return ValueTask.FromResult(
                    ControllerResult<SitePermissionState>.Failure(readinessError));
            }

            var now = _clock.UtcNow;
            PruneExpiredRulesLocked(now);
            visible = _rules.Values
                .Where(stored =>
                    stored.Rule.Site.Equals(site) &&
                    IsVisible(stored, context.Privacy, now))
                .Select(stored => stored.Rule)
                .OrderBy(value => value.Capability)
                .ThenByDescending(value => ScopePriority(value.Scope))
                .ThenByDescending(value => value.CreatedAtUtc)
                .ToArray();
        }

        return ValueTask.FromResult(ControllerResult<SitePermissionState>.Success(
            new SitePermissionState(context, site, visible)));
    }

    public async ValueTask<ControllerResult> ResetAsync(
        ResetPermissionRuleIntent intent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (intent is null ||
            !IsValid(intent.Context) ||
            intent.RuleId.IsEmpty ||
            intent.RuleId.ProfileId != intent.Context.Privacy.ProfileId)
        {
            return ControllerResult.Failure(
                InvalidRequest("error.permission.reset_invalid"));
        }

        if (_persistence is null)
        {
            lock (_gate)
            {
                var now = _clock.UtcNow;
                PruneExpiredRulesLocked(now);
                if (!_rules.TryGetValue(intent.RuleId, out var stored) ||
                    !IsVisible(stored, intent.Context.Privacy, now))
                {
                    return ControllerResult.Failure(RuleNotFoundError());
                }

                _rules.Remove(intent.RuleId);
                return ControllerResult.Success();
            }
        }

        lock (_gate)
        {
            var readinessError = ReadinessErrorLocked(intent.Context.Privacy);
            if (readinessError is not null)
            {
                return ControllerResult.Failure(readinessError);
            }

            var now = _clock.UtcNow;
            PruneExpiredRulesLocked(now);
            if (!_rules.TryGetValue(intent.RuleId, out var stored) ||
                !IsVisible(stored, intent.Context.Privacy, now))
            {
                return ControllerResult.Failure(RuleNotFoundError());
            }

            if (stored.Rule.Scope != PermissionAllowScope.Persistent)
            {
                _rules.Remove(intent.RuleId);
                return ControllerResult.Success();
            }
        }

        await _persistenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<PermissionRule> nextPersistent;
            ProfileStorageRevision? expectedRevision;
            lock (_gate)
            {
                var readinessError = ReadinessErrorLocked(intent.Context.Privacy);
                if (readinessError is not null)
                {
                    return ControllerResult.Failure(readinessError);
                }

                if (!_rules.TryGetValue(intent.RuleId, out var stored) ||
                    !IsVisible(stored, intent.Context.Privacy, _clock.UtcNow) ||
                    stored.Rule.Scope != PermissionAllowScope.Persistent)
                {
                    return ControllerResult.Failure(RuleNotFoundError());
                }

                nextPersistent = PersistentForProfileLocked(
                    intent.Context.Privacy.ProfileId)
                    .Where(rule => rule.Id != intent.RuleId)
                    .ToArray();
                expectedRevision = _durableProfiles[
                    intent.Context.Privacy.ProfileId].Revision;
            }

            var write = await _persistence.WritePermissionsAsync(
                intent.Context.Privacy,
                nextPersistent,
                expectedRevision,
                cancellationToken).ConfigureAwait(false);
            if (!write.IsSuccess)
            {
                return ControllerResult.Failure(write.Error!);
            }

            lock (_gate)
            {
                _rules.Remove(intent.RuleId);
                _durableProfiles[intent.Context.Privacy.ProfileId] =
                    DurableProfileState.Ready(write.Value!.Revision);
            }

            return ControllerResult.Success();
        }
        finally
        {
            _persistenceGate.Release();
        }
    }

    private static ControllerError? ValidateRequest(PermissionBrokerRequest? request)
    {
        if (request is null ||
            request.RequestId.IsEmpty ||
            request.ResponseToken.IsEmpty ||
            !IsValid(request.Context) ||
            request.RequestingSite is null ||
            !Enum.IsDefined(request.Capability) ||
            request.Capability == WebPermissionCapability.Unknown ||
            !Enum.IsDefined(request.CurrentDecision) ||
            !Enum.IsDefined(request.DefaultDecision) ||
            request.DefaultDecision != PermissionDecision.Deny ||
            request.SupportedAllowScopes is null ||
            request.SupportedAllowScopes.Count == 0 ||
            request.SupportedAllowScopes.Any(scope => !Enum.IsDefined(scope)) ||
            request.SupportedAllowScopes.Distinct().Count() !=
                request.SupportedAllowScopes.Count ||
            request.ExpiresAtUtc <= request.RequestedAtUtc)
        {
            return InvalidRequest("error.permission.request_invalid");
        }

        return null;
    }

    private static bool IsValid(PermissionResponse? response) =>
        response is not null &&
        !response.RequestId.IsEmpty &&
        !response.ResponseToken.IsEmpty &&
        IsValid(response.Context) &&
        Enum.IsDefined(response.Decision) &&
        (response.AllowScope is null || Enum.IsDefined(response.AllowScope.Value));

    private static bool IsValid(BrowsingContext? context) =>
        context is { IsStructurallyValid: true };

    private static ControllerError? ValidateResponseChoice(
        PermissionBrokerRequest request,
        PermissionResponse response)
    {
        if (response.Decision == PermissionDecision.Ask)
        {
            return InvalidRequest("error.permission.response_decision_invalid");
        }

        if (response.Decision == PermissionDecision.Deny)
        {
            return response.AllowScope is null
                ? null
                : InvalidRequest("error.permission.deny_scope_invalid");
        }

        if (response.AllowScope is not { } scope)
        {
            return InvalidRequest("error.permission.allow_scope_required");
        }

        if (request.Context.Privacy.IsPrivate &&
            scope == PermissionAllowScope.Persistent)
        {
            return ControllerError.Create(
                ControllerErrorCode.PolicyDenied,
                "error.permission.private_persistent_denied");
        }

        return request.SupportedAllowScopes.Contains(scope)
            ? null
            : ControllerError.Create(
                ControllerErrorCode.NotSupported,
                "error.permission.allow_scope_unsupported",
                [ControllerMessageArgument.Symbol(
                    ControllerMessageArgumentKey.Scope,
                    scope.ToString())]);
    }

    private async ValueTask<ControllerResult<PermissionHostCompletion>>
        RespondPersistentAsync(
            PermissionResponse response,
            CancellationToken cancellationToken)
    {
        await _persistenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PendingRequest pending;
            PermissionRule? candidate = null;
            IReadOnlyList<PermissionRule> nextPersistent = [];
            ProfileStorageRevision? expectedRevision = null;
            PermissionHostCompletion? earlyCompletion = null;
            ControllerError? earlyError = null;
            var expired = false;

            lock (_gate)
            {
                if (_handledRequestIds.Contains(response.RequestId) ||
                    _handledResponseTokens.Contains(response.ResponseToken) ||
                    _processingPersistent.Contains(response.RequestId))
                {
                    return ControllerResult<PermissionHostCompletion>.Failure(
                        ControllerError.Create(
                            ControllerErrorCode.AlreadyHandled,
                            "error.permission.response_replayed"));
                }

                if (!_pending.TryGetValue(response.RequestId, out pending!))
                {
                    return ControllerResult<PermissionHostCompletion>.Failure(
                        ControllerError.Create(
                            ControllerErrorCode.NotFound,
                            "error.permission.request_not_found"));
                }

                if (pending.Request.ResponseToken != response.ResponseToken ||
                    !ContextsMatch(pending.Request.Context, response.Context))
                {
                    return ControllerResult<PermissionHostCompletion>.Failure(
                        InvalidRequest("error.permission.response_binding_invalid"));
                }

                _processingPersistent.Add(response.RequestId);
                var now = _clock.UtcNow;
                if (now >= pending.Request.ExpiresAtUtc)
                {
                    expired = true;
                    ConsumePendingLocked(pending.Request);
                    _processingPersistent.Remove(response.RequestId);
                    earlyCompletion = PermissionHostCompletion.FailClosed(
                        pending.Request.RequestId,
                        pending.Request.Context.TabId,
                        pending.Request.Capability,
                        PermissionDecisionSource.RequestExpired);
                }
                else if (ReadinessErrorLocked(
                    pending.Request.Context.Privacy) is { } readinessError)
                {
                    ConsumePendingLocked(pending.Request);
                    _processingPersistent.Remove(response.RequestId);
                    earlyError = readinessError;
                    earlyCompletion = PermissionHostCompletion.FailClosed(
                        pending.Request.RequestId,
                        pending.Request.Context.TabId,
                        pending.Request.Capability,
                        PermissionDecisionSource.DefaultPolicy);
                }
                else if (ValidateResponseChoice(
                    pending.Request,
                    response) is { } validationError)
                {
                    ConsumePendingLocked(pending.Request);
                    _processingPersistent.Remove(response.RequestId);
                    earlyError = validationError;
                    earlyCompletion = PermissionHostCompletion.FailClosed(
                        pending.Request.RequestId,
                        pending.Request.Context.TabId,
                        pending.Request.Capability,
                        PermissionDecisionSource.DefaultPolicy);
                }
                else
                {
                    candidate = CreateRuleLocked(
                        pending.Request,
                        PermissionAllowScope.Persistent,
                        now);
                    if (!CanStoreRuleLocked(
                        candidate,
                        BrowserProfileMode.Normal))
                    {
                        ConsumePendingLocked(pending.Request);
                        _processingPersistent.Remove(response.RequestId);
                        earlyError = ControllerError.Create(
                            ControllerErrorCode.Conflict,
                            "error.permission.rule_limit_reached",
                            [ControllerMessageArgument.Count(
                                ControllerMessageArgumentKey.MaximumCount,
                                _maximumRulesPerProfile)]);
                        earlyCompletion = PermissionHostCompletion.FailClosed(
                            pending.Request.RequestId,
                            pending.Request.Context.TabId,
                            pending.Request.Capability,
                            PermissionDecisionSource.DefaultPolicy);
                    }
                    else
                    {
                        nextPersistent = PersistentForProfileLocked(
                            pending.Request.Context.Privacy.ProfileId)
                            .Where(rule =>
                                !rule.Site.Equals(candidate.Site) ||
                                rule.Capability != candidate.Capability)
                            .Append(candidate)
                            .ToArray();
                        expectedRevision = _durableProfiles[
                            pending.Request.Context.Privacy.ProfileId].Revision;
                    }
                }
            }

            if (earlyCompletion is not null)
            {
                var earlySink = await CompleteHostAsync(
                    earlyCompletion,
                    cancellationToken).ConfigureAwait(false);
                if (!earlySink.IsSuccess)
                {
                    return ControllerResult<PermissionHostCompletion>.Failure(
                        earlySink.Error!);
                }

                if (expired)
                {
                    return ControllerResult<PermissionHostCompletion>.Success(
                        earlyCompletion);
                }

                return ControllerResult<PermissionHostCompletion>.Failure(earlyError!);
            }

            var write = await _persistence!.WritePermissionsAsync(
                pending.Request.Context.Privacy,
                nextPersistent,
                expectedRevision,
                cancellationToken).ConfigureAwait(false);
            if (!write.IsSuccess)
            {
                PermissionHostCompletion failure;
                lock (_gate)
                {
                    _processingPersistent.Remove(response.RequestId);
                    if (_pending.ContainsKey(pending.Request.RequestId))
                    {
                        ConsumePendingLocked(pending.Request);
                    }

                    failure = PermissionHostCompletion.FailClosed(
                        pending.Request.RequestId,
                        pending.Request.Context.TabId,
                        pending.Request.Capability,
                        PermissionDecisionSource.HostFailure);
                }

                await CompleteHostAsync(failure, cancellationToken)
                    .ConfigureAwait(false);
                return ControllerResult<PermissionHostCompletion>.Failure(write.Error!);
            }

            PermissionHostCompletion completion;
            lock (_gate)
            {
                StorePersistentSnapshotLocked(
                    pending.Request.Context.Privacy.ProfileId,
                    nextPersistent);
                _durableProfiles[pending.Request.Context.Privacy.ProfileId] =
                    DurableProfileState.Ready(write.Value!.Revision);
                ConsumePendingLocked(pending.Request);
                _processingPersistent.Remove(response.RequestId);
                completion = PermissionHostCompletion.FromAcceptedResponse(
                    pending.Request.RequestId,
                    pending.Request.Context.TabId,
                    pending.Request.Capability,
                    PermissionDecision.Allow,
                    candidate);
            }

            var sink = await CompleteHostAsync(completion, cancellationToken)
                .ConfigureAwait(false);
            return sink.IsSuccess
                ? ControllerResult<PermissionHostCompletion>.Success(completion)
                : ControllerResult<PermissionHostCompletion>.Failure(sink.Error!);
        }
        finally
        {
            lock (_gate)
            {
                _processingPersistent.Remove(response.RequestId);
            }

            _persistenceGate.Release();
        }
    }

    private async ValueTask CompleteFailureAsync(
        PermissionBrokerRequest request,
        PermissionDecisionSource source,
        CancellationToken cancellationToken)
    {
        var completion = PermissionHostCompletion.FailClosed(
            request.RequestId,
            request.Context?.TabId ?? default,
            request.Capability,
            source);
        await CompleteHostAsync(completion, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ControllerResult> CompleteHostAsync(
        PermissionHostCompletion completion,
        CancellationToken cancellationToken)
    {
        if (completion.SaveInProfile)
        {
            return ControllerResult.Failure(ControllerError.Create(
                ControllerErrorCode.IntegrityFailure,
                "error.permission.host_persistence_forbidden"));
        }

        try
        {
            return await _completionSink
                .CompleteAsync(completion, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return ControllerResult.Failure(ControllerError.Create(
                ControllerErrorCode.InternalFailure,
                "error.permission.host_completion_failed",
                isRetryable: true));
        }
    }

    private PermissionRule CreateRuleLocked(
        PermissionBrokerRequest request,
        PermissionAllowScope scope,
        DateTimeOffset now)
    {
        PermissionRuleId id;
        do
        {
            id = new PermissionRuleId(request.Context.Privacy.ProfileId, Guid.NewGuid());
        }
        while (_rules.ContainsKey(id));

        return new PermissionRule(
            id,
            request.Context.Privacy.ProfileId,
            scope == PermissionAllowScope.Persistent
                ? null
                : request.Context.Privacy.SessionId,
            request.RequestingSite,
            request.Capability,
            PermissionDecision.Allow,
            scope,
            now,
            scope == PermissionAllowScope.Once
                ? request.ExpiresAtUtc
                : null);
    }

    private bool CanStoreRuleLocked(
        PermissionRule candidate,
        BrowserProfileMode mode)
    {
        var supersededCount = _rules.Values.Count(stored =>
            IsSameRuleSlot(stored, candidate, mode));
        var currentCount = _rules.Values.Count(stored =>
            stored.Rule.ProfileId == candidate.ProfileId);
        return currentCount - supersededCount < _maximumRulesPerProfile;
    }

    private IReadOnlyList<StoredRule> StoreRuleLocked(
        PermissionRule rule,
        BrowserProfileMode mode)
    {
        var superseded = _rules
            .Where(pair => IsSameRuleSlot(pair.Value, rule, mode))
            .Select(pair => pair.Value)
            .ToArray();
        foreach (var value in superseded)
        {
            _rules.Remove(value.Rule.Id);
        }

        _rules.Add(rule.Id, new StoredRule(rule, mode));
        return superseded;
    }

    private void RestoreSupersededRulesLocked(IReadOnlyList<StoredRule> superseded)
    {
        foreach (var stored in superseded)
        {
            if (!_rules.ContainsKey(stored.Rule.Id) &&
                !_rules.Values.Any(current => IsSameRuleSlot(
                    current,
                    stored.Rule,
                    stored.Mode)))
            {
                _rules.Add(stored.Rule.Id, stored);
            }
        }
    }

    private void ConsumePendingLocked(PermissionBrokerRequest request)
    {
        _pending.Remove(request.RequestId);
        MarkHandledLocked(request.RequestId, request.ResponseToken);
    }

    private void MarkHandledLocked(RequestId requestId, ResponseToken responseToken)
    {
        _handledRequestIds.Add(requestId);
        _handledResponseTokens.Add(responseToken);
        _handledOrder.Enqueue(new HandledResponse(requestId, responseToken));

        while (_handledOrder.Count > _maximumHandledResponses)
        {
            var removed = _handledOrder.Dequeue();
            _handledRequestIds.Remove(removed.RequestId);
            _handledResponseTokens.Remove(removed.ResponseToken);
        }
    }

    private bool IsKnownRequestOrTokenLocked(
        RequestId requestId,
        ResponseToken responseToken) =>
        _handledRequestIds.Contains(requestId) ||
        _handledResponseTokens.Contains(responseToken) ||
        _pending.ContainsKey(requestId) ||
        _pending.Values.Any(value =>
            value.Request.ResponseToken == responseToken);

    private void PruneExpiredRulesLocked(DateTimeOffset now)
    {
        foreach (var id in _rules
            .Where(pair =>
                pair.Value.Rule.ExpiresAtUtc is { } expiresAtUtc &&
                expiresAtUtc <= now)
            .Select(pair => pair.Key)
            .ToArray())
        {
            _rules.Remove(id);
        }
    }

    private static bool IsVisible(
        StoredRule stored,
        PrivacyContext context,
        DateTimeOffset now)
    {
        var rule = stored.Rule;
        if (rule.ProfileId != context.ProfileId ||
            stored.Mode != context.Mode ||
            rule.ExpiresAtUtc is { } expiresAtUtc && expiresAtUtc <= now)
        {
            return false;
        }

        if (rule.Scope == PermissionAllowScope.Persistent)
        {
            return !context.IsPrivate;
        }

        return rule.SessionId == context.SessionId;
    }

    private static bool IsSameRuleSlot(
        StoredRule stored,
        PermissionRule candidate,
        BrowserProfileMode mode)
    {
        var current = stored.Rule;
        if (stored.Mode != mode ||
            current.ProfileId != candidate.ProfileId ||
            !current.Site.Equals(candidate.Site) ||
            current.Capability != candidate.Capability ||
            current.Scope != candidate.Scope)
        {
            return false;
        }

        return candidate.Scope == PermissionAllowScope.Persistent ||
            current.SessionId == candidate.SessionId;
    }

    private static bool ContextsMatch(BrowsingContext expected, BrowsingContext actual) =>
        expected.Privacy.ProfileId == actual.Privacy.ProfileId &&
        expected.Privacy.SessionId == actual.Privacy.SessionId &&
        expected.Privacy.Mode == actual.Privacy.Mode &&
        expected.WindowId == actual.WindowId &&
        expected.TabId == actual.TabId &&
        Equals(expected.CurrentSite, actual.CurrentSite);

    private static int ScopePriority(PermissionAllowScope scope) =>
        scope switch
        {
            PermissionAllowScope.Session => 2,
            PermissionAllowScope.Persistent => 1,
            PermissionAllowScope.Once => 0,
            _ => -1,
        };

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
                    "error.permission.profile_not_hydrated");
        }

        if (!_durableProfiles.TryGetValue(context.ProfileId, out var state))
        {
            return ControllerError.Create(
                ControllerErrorCode.Unavailable,
                "error.permission.profile_not_hydrated");
        }

        return state.Error;
    }

    private int CountForProfileLocked(ProfileId profileId) =>
        _rules.Values.Count(stored => stored.Rule.ProfileId == profileId);

    private IReadOnlyList<PermissionRule> PersistentForProfileLocked(
        ProfileId profileId) =>
        _rules.Values
            .Where(stored =>
                stored.Mode == BrowserProfileMode.Normal &&
                stored.Rule.ProfileId == profileId &&
                stored.Rule.Scope == PermissionAllowScope.Persistent)
            .Select(stored => stored.Rule)
            .ToArray();

    private void RemovePersistentForProfileLocked(ProfileId profileId)
    {
        foreach (var id in _rules
            .Where(pair =>
                pair.Value.Mode == BrowserProfileMode.Normal &&
                pair.Value.Rule.ProfileId == profileId &&
                pair.Value.Rule.Scope == PermissionAllowScope.Persistent)
            .Select(pair => pair.Key)
            .ToArray())
        {
            _rules.Remove(id);
        }
    }

    private void StorePersistentSnapshotLocked(
        ProfileId profileId,
        IReadOnlyList<PermissionRule> rules)
    {
        RemovePersistentForProfileLocked(profileId);
        foreach (var rule in rules)
        {
            _rules.Add(
                rule.Id,
                new StoredRule(rule, BrowserProfileMode.Normal));
        }
    }

    private static ControllerError RuleNotFoundError() =>
        ControllerError.Create(
            ControllerErrorCode.NotFound,
            "error.permission.rule_not_found");

    private static ControllerError InvalidRequest(string messageKey) =>
        ControllerError.Create(ControllerErrorCode.InvalidRequest, messageKey);

    private sealed record PendingRequest(PermissionBrokerRequest Request);

    private sealed record StoredRule(
        PermissionRule Rule,
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

    private readonly record struct HandledResponse(
        RequestId RequestId,
        ResponseToken ResponseToken);
}
