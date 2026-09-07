using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.Contracts.Privacy;

public readonly record struct PermissionRuleId(ProfileId ProfileId, Guid Value)
{
    public bool IsEmpty => ProfileId.IsEmpty || Value == Guid.Empty;
}

public enum WebPermissionCapability
{
    Unknown = 0,
    Camera = 1,
    Microphone = 2,
    Geolocation = 3,
    Notifications = 4,
    ClipboardRead = 5,
    ClipboardWrite = 6,
    Midi = 7,
    Serial = 8,
    Usb = 9,
    Popups = 10,
    Autoplay = 11,
}

public enum PermissionDecision
{
    Ask = 0,
    Allow = 1,
    Deny = 2,
}

public enum PermissionAllowScope
{
    Once = 0,
    Session = 1,
    Persistent = 2,
}

public enum PermissionHostDisposition
{
    Deny = 0,
    Allow = 1,
}

public enum PermissionDecisionSource
{
    DefaultPolicy = 0,
    ExistingRule = 1,
    UserResponse = 2,
    RequestExpired = 3,
    HostFailure = 4,
}

public sealed record PermissionBrokerRequest(
    RequestId RequestId,
    ResponseToken ResponseToken,
    BrowsingContext Context,
    SiteIdentity RequestingSite,
    WebPermissionCapability Capability,
    IReadOnlyList<PermissionAllowScope> SupportedAllowScopes,
    PermissionDecision CurrentDecision,
    PermissionDecision DefaultDecision,
    bool HasUserGesture,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record PermissionPromptState(
    RequestId RequestId,
    ResponseToken ResponseToken,
    BrowsingContext Context,
    SiteIdentity RequestingSite,
    string DisplayOrigin,
    WebPermissionCapability Capability,
    IReadOnlyList<PermissionAllowScope> SupportedAllowScopes,
    PermissionDecision CurrentDecision,
    PermissionDecision DefaultDecision,
    DateTimeOffset ExpiresAtUtc);

public sealed class PermissionPromptEventArgs : EventArgs
{
    public PermissionPromptEventArgs(PermissionPromptState prompt)
    {
        Prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
    }

    public PermissionPromptState Prompt { get; }
}

public sealed record PermissionRule(
    PermissionRuleId Id,
    ProfileId ProfileId,
    BrowserSessionId? SessionId,
    SiteIdentity Site,
    WebPermissionCapability Capability,
    PermissionDecision Decision,
    PermissionAllowScope Scope,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ExpiresAtUtc);

public sealed record PermissionResponse(
    RequestId RequestId,
    ResponseToken ResponseToken,
    BrowsingContext Context,
    PermissionDecision Decision,
    PermissionAllowScope? AllowScope,
    DateTimeOffset RespondedAtUtc);

public sealed record ResetPermissionRuleIntent(
    BrowsingContext Context,
    PermissionRuleId RuleId);

public sealed record SitePermissionState(
    BrowsingContext Context,
    SiteIdentity Site,
    IReadOnlyList<PermissionRule> Rules);

public sealed class PermissionHostCompletion
{
    private PermissionHostCompletion(
        RequestId requestId,
        BrowserTabId requestingTabId,
        WebPermissionCapability capability,
        PermissionHostDisposition disposition,
        bool isFailClosed,
        bool saveInProfile,
        PermissionRule? appliedRule,
        PermissionDecisionSource source)
    {
        RequestId = requestId;
        RequestingTabId = requestingTabId;
        Capability = capability;
        Disposition = disposition;
        IsFailClosed = isFailClosed;
        SaveInProfile = saveInProfile;
        AppliedRule = appliedRule;
        Source = source;
    }

    public RequestId RequestId { get; }

    public BrowserTabId RequestingTabId { get; }

    public WebPermissionCapability Capability { get; }

    public PermissionHostDisposition Disposition { get; }

    public bool IsFailClosed { get; }

    public bool SaveInProfile { get; }

    public PermissionRule? AppliedRule { get; }

    public PermissionDecisionSource Source { get; }

    public static PermissionHostCompletion FromAcceptedResponse(
        RequestId requestId,
        BrowserTabId requestingTabId,
        WebPermissionCapability capability,
        PermissionDecision decision,
        PermissionRule? appliedRule = null,
        PermissionDecisionSource source = PermissionDecisionSource.UserResponse) =>
        capability == WebPermissionCapability.Unknown
            ? FailClosed(requestId, requestingTabId, capability, PermissionDecisionSource.HostFailure)
            : new(
            requestId,
            requestingTabId,
            capability,
            decision == PermissionDecision.Allow
                ? PermissionHostDisposition.Allow
                : PermissionHostDisposition.Deny,
            false,
            false,
            appliedRule,
            source);

    public static PermissionHostCompletion FailClosed(
        RequestId requestId,
        BrowserTabId requestingTabId,
        WebPermissionCapability capability,
        PermissionDecisionSource source = PermissionDecisionSource.HostFailure) =>
        new(
            requestId,
            requestingTabId,
            capability,
            PermissionHostDisposition.Deny,
            true,
            false,
            null,
            source);
}

public interface IPermissionBroker
{
    event EventHandler<PermissionPromptEventArgs>? PromptRequested;

    ValueTask<ControllerResult<PermissionPromptState>> IngestAsync(
        PermissionBrokerRequest request,
        CancellationToken cancellationToken);

    ValueTask<ControllerResult<PermissionHostCompletion>> RespondAsync(
        PermissionResponse response,
        CancellationToken cancellationToken);

    ValueTask<ControllerResult<SitePermissionState>> GetCurrentSiteStateAsync(
        BrowsingContext context,
        SiteIdentity site,
        CancellationToken cancellationToken);

    ValueTask<ControllerResult> ResetAsync(
        ResetPermissionRuleIntent intent,
        CancellationToken cancellationToken);
}

public interface IPermissionHostCompletionSink
{
    ValueTask<ControllerResult> CompleteAsync(
        PermissionHostCompletion completion,
        CancellationToken cancellationToken);
}
