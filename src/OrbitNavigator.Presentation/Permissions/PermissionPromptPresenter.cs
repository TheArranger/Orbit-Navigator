using System.Collections.ObjectModel;

using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Privacy;
using OrbitNavigator.Presentation.Common;

namespace OrbitNavigator.Presentation.Permissions;

public sealed record PermissionChoiceViewState(
    PermissionDecision Decision,
    PermissionAllowScope? AllowScope,
    string LabelKey,
    bool IsSafestChoice);

public sealed record PermissionPromptViewState(
    PermissionPromptState? Prompt,
    string? CapabilityLabelKey,
    IReadOnlyList<PermissionChoiceViewState> Choices,
    bool IsOpen,
    bool IsBusy,
    bool IsExpired,
    int QueuedPromptCount,
    PresentationFailure? Failure,
    PresentationAnnouncement? Announcement)
{
    public static PermissionPromptViewState Empty { get; } = new(
        null,
        null,
        Array.Empty<PermissionChoiceViewState>(),
        false,
        false,
        false,
        0,
        null,
        null);
}

public sealed class PermissionPromptPresenter :
    PresentationStateSource<PermissionPromptViewState>,
    IDisposable
{
    private readonly IPermissionBroker broker;
    private readonly TimeProvider timeProvider;
    private readonly Queue<PermissionPromptState> queuedPrompts = new();
    private bool disposed;

    public PermissionPromptPresenter(
        IPermissionBroker broker,
        TimeProvider? timeProvider = null)
        : base(PermissionPromptViewState.Empty)
    {
        this.broker = broker ?? throw new ArgumentNullException(nameof(broker));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        broker.PromptRequested += OnPromptRequested;
    }

    public void RefreshExpiration()
    {
        var current = State;
        if (current.Prompt is null || current.IsExpired)
        {
            return;
        }

        if (current.Prompt.ExpiresAtUtc <= timeProvider.GetUtcNow())
        {
            ActivateNextOrPublishExpired(current);
        }
    }

    public async ValueTask RespondAsync(
        PermissionDecision decision,
        PermissionAllowScope? allowScope,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        RefreshExpiration();
        var current = State;
        if (current.Prompt is null || !current.IsOpen || current.IsExpired || current.IsBusy)
        {
            return;
        }

        if (!IsSupportedResponse(current.Prompt, decision, allowScope))
        {
            Publish(current with
            {
                Failure = new PresentationFailure(
                    "error.permission.response_invalid",
                    false,
                    ControllerErrorCode.InvalidRequest),
                Announcement = new PresentationAnnouncement(
                    "ui.permission.response_invalid",
                    AnnouncementPriority.Assertive),
            });
            return;
        }

        var prompt = current.Prompt;
        Publish(current with { IsBusy = true, Failure = null, Announcement = null });
        var result = await broker.RespondAsync(
            new PermissionResponse(
                prompt.RequestId,
                prompt.ResponseToken,
                prompt.Context,
                decision,
                allowScope,
                timeProvider.GetUtcNow()),
            cancellationToken).ConfigureAwait(false);

        var latest = State;
        if (latest.Prompt?.RequestId != prompt.RequestId ||
            latest.Prompt.ResponseToken != prompt.ResponseToken)
        {
            return;
        }

        if (!result.IsSuccess)
        {
            if (result.Error!.Code is ControllerErrorCode.AlreadyHandled or ControllerErrorCode.NotFound)
            {
                ActivateNextOrEmpty("ui.permission.request_resolved");
                return;
            }
            if (result.Error.Code == ControllerErrorCode.Expired)
            {
                ActivateNextOrPublishExpired(latest);
                return;
            }

            var safeFailure = ToSafeFailure(result.Error);
            // A WebView permission response owns a one-shot host deferral. The broker
            // may already have completed it fail-closed even when persistence or host
            // delivery reports an error. Re-enabling the same choices would invite a
            // replay against a consumed token, so every submitted failure is terminal.
            ActivateNextOrEmpty(safeFailure.MessageKey);
            return;
        }

        var allowed = result.Value!.Disposition == PermissionHostDisposition.Allow;
        ActivateNextOrEmpty(allowed ? "ui.permission.allowed" : "ui.permission.kept_blocked");
    }

    public void Dismiss()
    {
        var current = State;
        if (current.Prompt is not null && !current.IsBusy)
        {
            Publish(current with
            {
                IsOpen = false,
                Announcement = new PresentationAnnouncement("ui.permission.dismissed"),
            });
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        broker.PromptRequested -= OnPromptRequested;
        queuedPrompts.Clear();
        disposed = true;
        GC.SuppressFinalize(this);
    }

    public static string CapabilityLabelKey(WebPermissionCapability capability) => capability switch
    {
        WebPermissionCapability.Camera => "ui.permission.capability.camera",
        WebPermissionCapability.Microphone => "ui.permission.capability.microphone",
        WebPermissionCapability.Geolocation => "ui.permission.capability.location",
        WebPermissionCapability.Notifications => "ui.permission.capability.notifications",
        WebPermissionCapability.ClipboardRead => "ui.permission.capability.clipboard_read",
        WebPermissionCapability.ClipboardWrite => "ui.permission.capability.clipboard_write",
        WebPermissionCapability.Midi => "ui.permission.capability.midi",
        WebPermissionCapability.Serial => "ui.permission.capability.serial",
        WebPermissionCapability.Usb => "ui.permission.capability.usb",
        WebPermissionCapability.Popups => "ui.permission.capability.popups",
        WebPermissionCapability.Autoplay => "ui.permission.capability.autoplay",
        _ => "ui.permission.capability.unknown",
    };

    private void OnPromptRequested(object? sender, PermissionPromptEventArgs args)
    {
        var prompt = args.Prompt;
        var current = State;
        if (current.Prompt is not null && current.IsOpen && !current.IsExpired)
        {
            queuedPrompts.Enqueue(prompt);
            Publish(current with
            {
                QueuedPromptCount = queuedPrompts.Count,
                Announcement = new PresentationAnnouncement("ui.permission.request_queued"),
            });
            return;
        }

        Activate(prompt);
    }

    private void Activate(PermissionPromptState prompt)
    {
        var expired = prompt.ExpiresAtUtc <= timeProvider.GetUtcNow();
        Publish(new PermissionPromptViewState(
            prompt,
            CapabilityLabelKey(prompt.Capability),
            BuildChoices(prompt),
            true,
            false,
            expired,
            queuedPrompts.Count,
            expired
                ? new PresentationFailure(
                    "error.permission.request_expired",
                    false,
                    ControllerErrorCode.Expired)
                : null,
            new PresentationAnnouncement(
                expired ? "ui.permission.request_expired" : "ui.permission.requested",
                expired ? AnnouncementPriority.Assertive : AnnouncementPriority.Polite)));
    }

    private void ActivateNextOrEmpty(string previousAnnouncementKey)
    {
        while (queuedPrompts.Count > 0)
        {
            var next = queuedPrompts.Dequeue();
            if (next.ExpiresAtUtc > timeProvider.GetUtcNow())
            {
                Activate(next);
                return;
            }
        }

        Publish(PermissionPromptViewState.Empty with
        {
            Announcement = new PresentationAnnouncement(previousAnnouncementKey),
        });
    }

    private void ActivateNextOrPublishExpired(PermissionPromptViewState expired)
    {
        while (queuedPrompts.Count > 0)
        {
            var next = queuedPrompts.Dequeue();
            if (next.ExpiresAtUtc > timeProvider.GetUtcNow())
            {
                Activate(next);
                return;
            }
        }

        Publish(expired with
        {
            IsBusy = false,
            IsExpired = true,
            QueuedPromptCount = 0,
            Failure = new PresentationFailure(
                "error.permission.request_expired",
                false,
                ControllerErrorCode.Expired),
            Announcement = new PresentationAnnouncement(
                "ui.permission.request_expired",
                AnnouncementPriority.Assertive),
        });
    }

    private static IReadOnlyList<PermissionChoiceViewState> BuildChoices(
        PermissionPromptState prompt)
    {
        var choices = new List<PermissionChoiceViewState>
        {
            new(PermissionDecision.Deny, null, "ui.permission.keep_blocked", true),
        };
        choices.AddRange(prompt.SupportedAllowScopes
            .Distinct()
            .Where(Enum.IsDefined)
            .Select(scope => new PermissionChoiceViewState(
                PermissionDecision.Allow,
                scope,
                scope switch
                {
                    PermissionAllowScope.Once => "ui.permission.allow_once",
                    PermissionAllowScope.Session => "ui.permission.allow_session",
                    PermissionAllowScope.Persistent => "ui.permission.allow_always",
                    _ => "ui.permission.allow",
                },
                false)));
        return new ReadOnlyCollection<PermissionChoiceViewState>(choices);
    }

    private static bool IsSupportedResponse(
        PermissionPromptState prompt,
        PermissionDecision decision,
        PermissionAllowScope? allowScope) => decision switch
    {
        PermissionDecision.Deny => allowScope is null,
        PermissionDecision.Allow =>
            allowScope is not null && prompt.SupportedAllowScopes.Contains(allowScope.Value),
        _ => false,
    };

    private static PresentationFailure ToSafeFailure(ControllerError error)
    {
        var safeKey = error.Code switch
        {
            ControllerErrorCode.InvalidRequest => "ui.permission.response_invalid",
            ControllerErrorCode.PolicyDenied => "ui.permission.response_not_allowed",
            ControllerErrorCode.Cancelled => "ui.permission.response_cancelled",
            ControllerErrorCode.NotSupported or ControllerErrorCode.Unavailable =>
                "ui.permission.response_unavailable",
            ControllerErrorCode.Conflict or ControllerErrorCode.StaleClient =>
                "ui.permission.response_changed",
            _ => "ui.permission.response_failed",
        };
        return new PresentationFailure(safeKey, error.IsRetryable, error.Code);
    }
}
