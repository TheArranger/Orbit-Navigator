using System.Collections.ObjectModel;

using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Privacy;
using OrbitNavigator.Presentation.Common;

namespace OrbitNavigator.ClipboardShelf.Presentation;

public sealed record ClipboardShelfViewState(
    BrowsingContext? Context,
    ClipboardShelfAvailability Availability,
    ClipboardShelfUnavailableReason UnavailableReason,
    int Capacity,
    IReadOnlyList<ClipboardShelfItemSummary> Items,
    ClipboardShelfUseReceipt? LastUseReceipt,
    bool IsBusy,
    bool IsClearConfirmationOpen,
    PresentationFailure? Failure,
    PresentationAnnouncement? Announcement)
{
    public bool IsAvailable => Availability == ClipboardShelfAvailability.Available;

    public static ClipboardShelfViewState Empty { get; } = new(
        null,
        ClipboardShelfAvailability.Unavailable,
        ClipboardShelfUnavailableReason.PlatformUnavailable,
        ClipboardShelfReadModel.MaximumCapacity,
        Array.Empty<ClipboardShelfItemSummary>(),
        null,
        false,
        false,
        null,
        null);
}

public sealed class ClipboardShelfPresenter :
    PresentationStateSource<ClipboardShelfViewState>
{
    private readonly IClipboardShelfController controller;

    public ClipboardShelfPresenter(IClipboardShelfController controller)
        : base(ClipboardShelfViewState.Empty)
    {
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
    }

    public async ValueTask LoadAsync(
        BrowsingContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        Publish(ClipboardShelfViewState.Empty with
        {
            Context = context,
            IsBusy = true,
            UnavailableReason = context.Privacy.IsPrivate
                ? ClipboardShelfUnavailableReason.PrivateMode
                : ClipboardShelfUnavailableReason.PlatformUnavailable,
        });
        var result = await controller.GetAsync(context, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            PublishFailure(result.Error!);
            return;
        }

        var model = result.Value!;
        var safeCapacity = Math.Clamp(
            model.Capacity,
            0,
            ClipboardShelfReadModel.MaximumCapacity);
        var items = model.Availability == ClipboardShelfAvailability.Available
            ? model.Items.Take(safeCapacity).ToArray()
            : Array.Empty<ClipboardShelfItemSummary>();
        Publish(new ClipboardShelfViewState(
            context,
            model.Availability,
            model.UnavailableReason,
            safeCapacity,
            new ReadOnlyCollection<ClipboardShelfItemSummary>(items),
            null,
            false,
            false,
            null,
            new PresentationAnnouncement(
                model.UnavailableReason == ClipboardShelfUnavailableReason.PrivateMode
                    ? "ui.clipboard_shelf.private_unavailable"
                    : "ui.clipboard_shelf.loaded")));
    }

    public async ValueTask UseAsync(
        ClipboardShelfItemId itemId,
        CancellationToken cancellationToken = default)
    {
        var current = State;
        if (current.Context is null || current.IsBusy || !current.IsAvailable)
        {
            return;
        }

        if (!current.Items.Any(item => item.Id == itemId))
        {
            Publish(current with
            {
                Failure = new PresentationFailure(
                    "error.clipboard_shelf.item_not_visible",
                    false,
                    ControllerErrorCode.NotFound),
                Announcement = new PresentationAnnouncement(
                    "ui.clipboard_shelf.item_not_visible",
                    AnnouncementPriority.Assertive),
            });
            return;
        }

        Publish(current with { IsBusy = true, Failure = null, Announcement = null });
        var result = await controller.UseAsync(
            new UseClipboardShelfItemIntent(current.Context, itemId),
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            PublishFailure(result.Error!);
            return;
        }

        Publish(current with
        {
            LastUseReceipt = result.Value,
            IsBusy = false,
            Failure = null,
            Announcement = new PresentationAnnouncement("ui.clipboard_shelf.item_used"),
        });
    }

    public void RequestClear()
    {
        var current = State;
        if (current.IsAvailable && current.Items.Count > 0 && !current.IsBusy)
        {
            Publish(current with
            {
                IsClearConfirmationOpen = true,
                Failure = null,
                Announcement = new PresentationAnnouncement("ui.clipboard_shelf.clear_confirmation"),
            });
        }
    }

    public void CancelClear()
    {
        var current = State;
        if (current.IsClearConfirmationOpen && !current.IsBusy)
        {
            Publish(current with
            {
                IsClearConfirmationOpen = false,
                Announcement = new PresentationAnnouncement("ui.clipboard_shelf.clear_cancelled"),
            });
        }
    }

    public async ValueTask ConfirmClearAsync(CancellationToken cancellationToken = default)
    {
        var current = State;
        if (current.Context is null ||
            current.IsBusy ||
            !current.IsAvailable ||
            !current.IsClearConfirmationOpen)
        {
            return;
        }

        Publish(current with { IsBusy = true, Failure = null, Announcement = null });
        var result = await controller.ClearAsync(
            new ClearClipboardShelfIntent(current.Context, true),
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            PublishFailure(result.Error!);
            return;
        }

        Publish(current with
        {
            Items = Array.Empty<ClipboardShelfItemSummary>(),
            LastUseReceipt = null,
            IsBusy = false,
            IsClearConfirmationOpen = false,
            Failure = null,
            Announcement = new PresentationAnnouncement("ui.clipboard_shelf.cleared"),
        });
    }

    private void PublishFailure(ControllerError error)
    {
        Publish(State with
        {
            IsBusy = false,
            Failure = PresentationFailure.From(error),
            Announcement = new PresentationAnnouncement(
                error.MessageKey,
                AnnouncementPriority.Assertive),
        });
    }
}
