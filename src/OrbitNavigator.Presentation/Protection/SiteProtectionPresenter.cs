using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Privacy;
using OrbitNavigator.Presentation.Common;

namespace OrbitNavigator.Presentation.Protection;

public sealed record SiteProtectionViewState(
    BrowsingContext? Context,
    SiteIdentity? Site,
    SiteProtectionBaseline Baseline,
    ProtectionRelaxationDuration? EffectiveRelaxation,
    ProtectionEvaluationSource Source,
    ProtectionException? MatchingException,
    bool IsBusy,
    PresentationFailure? Failure,
    PresentationAnnouncement? Announcement)
{
    public bool IsStrict => EffectiveRelaxation is null;

    public bool CanPersist => Context is { Privacy.IsPrivate: false };

    public static SiteProtectionViewState Empty { get; } = new(
        null,
        null,
        SiteProtectionBaseline.Strict,
        null,
        ProtectionEvaluationSource.StrictBaseline,
        null,
        false,
        null,
        null);
}

public sealed class ProtectionReloadRequestedEventArgs : EventArgs
{
    public ProtectionReloadRequestedEventArgs(BrowsingContext context, SiteIdentity site)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        Site = site ?? throw new ArgumentNullException(nameof(site));
    }

    public BrowsingContext Context { get; }

    public SiteIdentity Site { get; }
}

public sealed class SiteProtectionPresenter : PresentationStateSource<SiteProtectionViewState>
{
    private readonly ISiteProtectionController controller;

    public SiteProtectionPresenter(ISiteProtectionController controller)
        : base(SiteProtectionViewState.Empty)
    {
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
    }

    public event EventHandler<ProtectionReloadRequestedEventArgs>? ReloadRequested;

    public async ValueTask LoadAsync(
        BrowsingContext context,
        SiteIdentity site,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(site);
        Publish(State with
        {
            Context = context,
            Site = site,
            IsBusy = true,
            Failure = null,
            Announcement = null,
        });
        var result = await controller.GetStateAsync(context, site, cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            Publish(State with
            {
                IsBusy = false,
                Failure = PresentationFailure.From(result.Error!),
                Announcement = new PresentationAnnouncement(
                    result.Error!.MessageKey,
                    AnnouncementPriority.Assertive),
            });
            return;
        }

        Publish(FromContract(result.Value!, "ui.protection.state_loaded"));
    }

    public ValueTask RetryWithStandardProtectionAsync(
        CancellationToken cancellationToken = default) =>
        RelaxAsync(ProtectionRelaxationDuration.Session, true, cancellationToken);

    public ValueTask RelaxAsync(
        ProtectionRelaxationDuration duration,
        CancellationToken cancellationToken = default) =>
        RelaxAsync(duration, false, cancellationToken);

    public async ValueTask RestoreStrictAsync(CancellationToken cancellationToken = default)
    {
        var current = State;
        if (current.Context is null || current.MatchingException is null || current.IsBusy)
        {
            return;
        }

        Publish(current with { IsBusy = true, Failure = null, Announcement = null });
        var result = await controller.RemoveExceptionAsync(
            new RemoveProtectionExceptionIntent(
                current.Context.Privacy,
                current.MatchingException.Id),
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            Publish(State with
            {
                IsBusy = false,
                Failure = PresentationFailure.From(result.Error!),
                Announcement = new PresentationAnnouncement(
                    result.Error!.MessageKey,
                    AnnouncementPriority.Assertive),
            });
            return;
        }

        Publish(current with
        {
            EffectiveRelaxation = null,
            Source = ProtectionEvaluationSource.StrictBaseline,
            MatchingException = null,
            IsBusy = false,
            Failure = null,
            Announcement = new PresentationAnnouncement("ui.protection.strict_restored"),
        });
    }

    private async ValueTask RelaxAsync(
        ProtectionRelaxationDuration duration,
        bool reloadAfterSuccess,
        CancellationToken cancellationToken)
    {
        var current = State;
        if (current.Context is null || current.Site is null || current.IsBusy)
        {
            return;
        }

        if (!Enum.IsDefined(duration) ||
            (duration == ProtectionRelaxationDuration.Persistent &&
             current.Context.Privacy.IsPrivate))
        {
            Publish(current with
            {
                Failure = new PresentationFailure(
                    "error.protection.duration_unavailable",
                    false,
                    ControllerErrorCode.PolicyDenied),
                Announcement = new PresentationAnnouncement(
                    "ui.protection.duration_unavailable",
                    AnnouncementPriority.Assertive),
            });
            return;
        }

        Publish(current with { IsBusy = true, Failure = null, Announcement = null });
        var result = await controller.RelaxAsync(
            new ProtectionRelaxationIntent(current.Context, current.Site, duration),
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            Publish(State with
            {
                IsBusy = false,
                Failure = PresentationFailure.From(result.Error!),
                Announcement = new PresentationAnnouncement(
                    result.Error!.MessageKey,
                    AnnouncementPriority.Assertive),
            });
            return;
        }

        var exception = result.Value!;
        Publish(current with
        {
            EffectiveRelaxation = exception.EffectiveDuration,
            Source = exception.Source switch
            {
                ProtectionExceptionSource.TemporaryUserChoice =>
                    ProtectionEvaluationSource.TemporaryException,
                ProtectionExceptionSource.SessionUserChoice =>
                    ProtectionEvaluationSource.SessionException,
                ProtectionExceptionSource.PersistentUserChoice =>
                    ProtectionEvaluationSource.PersistentException,
                _ => ProtectionEvaluationSource.EvaluatorFailure,
            },
            MatchingException = exception,
            IsBusy = false,
            Failure = null,
            Announcement = new PresentationAnnouncement("ui.protection.standard_enabled"),
        });

        if (reloadAfterSuccess)
        {
            ReloadRequested?.Invoke(
                this,
                new ProtectionReloadRequestedEventArgs(current.Context, current.Site));
        }
    }

    private static SiteProtectionViewState FromContract(
        SiteProtectionState value,
        string announcementKey) =>
        new(
            value.Context,
            value.Site,
            value.Baseline,
            value.EffectiveRelaxation,
            value.Source,
            value.MatchingException,
            false,
            null,
            new PresentationAnnouncement(announcementKey));
}
