using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.UX;
using OrbitNavigator.Presentation.Common;

namespace OrbitNavigator.Presentation.Reading;

public sealed record ReadingPreferencesViewState(
    ReadingPreferencesTarget? Target,
    ReadingPreferencesCapabilities? Capabilities,
    ReadingPreferencesState? CommittedState,
    ReadingPreferencesState? DraftState,
    ReadingPreviewId? PreviewId,
    IReadOnlyList<ReadingPreferenceField> UnsupportedFields,
    bool IsBusy,
    PresentationFailure? Failure,
    PresentationAnnouncement? Announcement)
{
    public bool HasPreview => PreviewId is not null;

    public static ReadingPreferencesViewState Empty { get; } = new(
        null,
        null,
        null,
        null,
        null,
        Array.Empty<ReadingPreferenceField>(),
        false,
        null,
        null);
}

public sealed class ReadingPreferencesPresenter :
    PresentationStateSource<ReadingPreferencesViewState>
{
    private readonly IReadingPreferencesController controller;

    public ReadingPreferencesPresenter(IReadingPreferencesController controller)
        : base(ReadingPreferencesViewState.Empty)
    {
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
    }

    public async ValueTask LoadAsync(
        ReadingPreferencesTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        Publish(ReadingPreferencesViewState.Empty with
        {
            Target = target,
            IsBusy = true,
        });

        var capabilities = await controller.GetCapabilitiesAsync(target, cancellationToken)
            .ConfigureAwait(false);
        if (!capabilities.IsSuccess)
        {
            PublishFailure(capabilities.Error!);
            return;
        }

        var current = await controller.GetCurrentAsync(target, cancellationToken)
            .ConfigureAwait(false);
        if (!current.IsSuccess)
        {
            PublishFailure(current.Error!);
            return;
        }

        Publish(new ReadingPreferencesViewState(
            target,
            capabilities.Value,
            current.Value,
            current.Value,
            null,
            Array.Empty<ReadingPreferenceField>(),
            false,
            null,
            new PresentationAnnouncement("ui.reading.loaded")));
    }

    public async ValueTask PreviewAsync(
        ReadingPreferencesState draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var current = State;
        if (current.Target is null || current.Capabilities is null || current.IsBusy)
        {
            return;
        }

        var invalidFields = FindInvalidFields(draft, current.Capabilities);
        if (invalidFields.Count > 0)
        {
            Publish(current with
            {
                DraftState = draft,
                UnsupportedFields = invalidFields,
                Failure = new PresentationFailure(
                    "error.reading.values_unsupported",
                    false,
                    ControllerErrorCode.NotSupported),
                Announcement = new PresentationAnnouncement(
                    "ui.reading.values_unsupported",
                    AnnouncementPriority.Assertive),
            });
            return;
        }

        Publish(current with
        {
            DraftState = draft,
            IsBusy = true,
            Failure = null,
            UnsupportedFields = Array.Empty<ReadingPreferenceField>(),
            Announcement = null,
        });

        ControllerResult<ReadingPreviewSession> result;
        if (current.PreviewId is { } previewId)
        {
            result = await controller.UpdatePreviewAsync(
                new UpdateReadingPreviewIntent(current.Target, previewId, draft),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            result = await controller.BeginPreviewAsync(
                new BeginReadingPreviewIntent(current.Target, draft),
                cancellationToken).ConfigureAwait(false);
        }

        if (!result.IsSuccess)
        {
            PublishFailure(result.Error!);
            return;
        }

        Publish(State with
        {
            Target = result.Value!.Target,
            DraftState = result.Value.PreviewState,
            PreviewId = result.Value.PreviewId,
            IsBusy = false,
            Failure = null,
            UnsupportedFields = Array.Empty<ReadingPreferenceField>(),
            Announcement = new PresentationAnnouncement("ui.reading.preview_updated"),
        });
    }

    public async ValueTask ApplyAsync(CancellationToken cancellationToken = default)
    {
        var current = State;
        if (current.Target is null || current.DraftState is null || current.IsBusy)
        {
            return;
        }

        if (current.PreviewId is null)
        {
            await PreviewAsync(current.DraftState, cancellationToken).ConfigureAwait(false);
            current = State;
            if (current.Target is null ||
                current.DraftState is null ||
                current.PreviewId is null ||
                current.Failure is not null)
            {
                return;
            }
        }

        var target = current.Target;
        var draft = current.DraftState;
        var previewId = current.PreviewId;
        if (target is null || draft is null || previewId is null)
        {
            return;
        }

        Publish(current with { IsBusy = true, Failure = null, Announcement = null });
        var result = await controller.ApplyAsync(
            new ApplyReadingPreferencesIntent(
                target,
                previewId.Value,
                draft),
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            PublishFailure(result.Error!);
            return;
        }

        Publish(current with
        {
            CommittedState = result.Value,
            DraftState = result.Value,
            PreviewId = null,
            IsBusy = false,
            Failure = null,
            UnsupportedFields = Array.Empty<ReadingPreferenceField>(),
            Announcement = new PresentationAnnouncement("ui.reading.applied"),
        });
    }

    public async ValueTask CancelPreviewAsync(CancellationToken cancellationToken = default)
    {
        var current = State;
        if (current.Target is null || current.PreviewId is null || current.IsBusy)
        {
            return;
        }

        Publish(current with { IsBusy = true, Failure = null, Announcement = null });
        var result = await controller.CancelPreviewAsync(
            new CancelReadingPreviewIntent(current.Target, current.PreviewId.Value),
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            PublishFailure(result.Error!);
            return;
        }

        Publish(current with
        {
            DraftState = current.CommittedState,
            PreviewId = null,
            IsBusy = false,
            Failure = null,
            UnsupportedFields = Array.Empty<ReadingPreferenceField>(),
            Announcement = new PresentationAnnouncement("ui.reading.preview_cancelled"),
        });
    }

    public async ValueTask EndPreviewAsync(CancellationToken cancellationToken = default)
    {
        var current = State;
        if (current.Target is null || current.PreviewId is null || current.IsBusy)
        {
            return;
        }

        Publish(current with { IsBusy = true, Failure = null, Announcement = null });
        var result = await controller.EndPreviewAsync(
            new EndReadingPreviewIntent(current.Target, current.PreviewId.Value),
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            PublishFailure(result.Error!);
            return;
        }

        Publish(current with
        {
            PreviewId = null,
            DraftState = current.CommittedState,
            IsBusy = false,
            Failure = null,
            Announcement = null,
        });
    }

    public async ValueTask ResetAsync(CancellationToken cancellationToken = default)
    {
        var current = State;
        if (current.Target is null || current.IsBusy)
        {
            return;
        }

        var target = current.Target;

        if (current.PreviewId is not null)
        {
            await CancelPreviewAsync(cancellationToken).ConfigureAwait(false);
            current = State;
            if (current.Failure is not null)
            {
                return;
            }
        }

        Publish(current with { IsBusy = true, Failure = null, Announcement = null });
        var result = await controller.ResetAsync(
            new ResetReadingPreferencesIntent(target),
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            PublishFailure(result.Error!);
            return;
        }

        Publish(current with
        {
            CommittedState = result.Value,
            DraftState = result.Value,
            PreviewId = null,
            IsBusy = false,
            Failure = null,
            UnsupportedFields = Array.Empty<ReadingPreferenceField>(),
            Announcement = new PresentationAnnouncement("ui.reading.reset"),
        });
    }

    public static IReadOnlyList<ReadingPreferenceField> FindInvalidFields(
        ReadingPreferencesState state,
        ReadingPreferencesCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(capabilities);
        var invalid = new List<ReadingPreferenceField>();
        if (!capabilities.SupportedFonts.Contains(state.Values.FontFamily, StringComparer.Ordinal))
        {
            invalid.Add(ReadingPreferenceField.FontFamily);
        }

        AddIfOutside(capabilities.TextScale, state.Values.TextScale, ReadingPreferenceField.TextScale, invalid);
        AddIfOutside(capabilities.LineHeight, state.Values.LineHeight, ReadingPreferenceField.LineHeight, invalid);
        AddIfOutside(capabilities.LetterSpacing, state.Values.LetterSpacing, ReadingPreferenceField.LetterSpacing, invalid);
        AddIfOutside(capabilities.WordSpacing, state.Values.WordSpacing, ReadingPreferenceField.WordSpacing, invalid);
        if (!capabilities.SupportedContrastModes.Contains(state.Values.Contrast))
        {
            invalid.Add(ReadingPreferenceField.Contrast);
        }

        if (!capabilities.SupportedTints.Contains(state.Values.Tint))
        {
            invalid.Add(ReadingPreferenceField.Tint);
        }

        if (!capabilities.SupportedLineFocusModes.Contains(state.Values.LineFocus))
        {
            invalid.Add(ReadingPreferenceField.LineFocus);
        }

        return invalid;
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

    private static void AddIfOutside(
        DecimalRange range,
        decimal value,
        ReadingPreferenceField field,
        ICollection<ReadingPreferenceField> invalid)
    {
        if (!range.Contains(value))
        {
            invalid.Add(field);
        }
    }
}
