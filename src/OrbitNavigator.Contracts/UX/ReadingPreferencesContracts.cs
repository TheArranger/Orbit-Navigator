using System.Collections.ObjectModel;

using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.Contracts.UX;

public readonly record struct ReadingPreviewId(
    ProfileId ProfileId,
    BrowserSessionId SessionId,
    BrowserTabId TabId,
    Guid Value)
{
    public bool IsEmpty =>
        ProfileId.IsEmpty ||
        SessionId.IsEmpty ||
        TabId.IsEmpty ||
        Value == Guid.Empty;
}

public enum ReadingPreferencesScope
{
    GlobalPersistent = 0,
    SitePersistent = 1,
    Session = 2,
}

public enum ReadingContrast
{
    System = 0,
    Standard = 1,
    High = 2,
}

public enum ReadingTint
{
    None = 0,
    Warm = 1,
    Sepia = 2,
    Cool = 3,
    Gray = 4,
}

public enum ReadingLineFocus
{
    Off = 0,
    OneLine = 1,
    ThreeLines = 2,
    FiveLines = 3,
}

public enum ReadingPreferenceField
{
    Enabled = 0,
    FontFamily = 1,
    TextScale = 2,
    LineHeight = 3,
    LetterSpacing = 4,
    WordSpacing = 5,
    Contrast = 6,
    Tint = 7,
    LineFocus = 8,
}

public enum ReadingEffectResultKind
{
    Applied = 0,
    Unsupported = 1,
    Failed = 2,
}

public sealed record DecimalRange(decimal Minimum, decimal Maximum, decimal Step)
{
    public bool Contains(decimal value) => value >= Minimum && value <= Maximum;
}

public sealed record ReadingPreferencesValues(
    string FontFamily,
    decimal TextScale,
    decimal LineHeight,
    decimal LetterSpacing,
    decimal WordSpacing,
    ReadingContrast Contrast,
    ReadingTint Tint,
    ReadingLineFocus LineFocus);

public sealed record ReadingPreferencesState(
    bool Enabled,
    ReadingPreferencesValues Values);

public sealed record ReadingPreferencesCapabilities(
    IReadOnlyList<ReadingPreferencesScope> SupportedTargets,
    IReadOnlyList<string> SupportedFonts,
    DecimalRange TextScale,
    DecimalRange LineHeight,
    DecimalRange LetterSpacing,
    DecimalRange WordSpacing,
    IReadOnlyList<ReadingContrast> SupportedContrastModes,
    IReadOnlyList<ReadingTint> SupportedTints,
    IReadOnlyList<ReadingLineFocus> SupportedLineFocusModes,
    IReadOnlyList<ReadingPreferenceField> GlobalSyncAllowlist);

public sealed class ReadingPreferencesTarget
{
    private ReadingPreferencesTarget(
        BrowsingContext context,
        ReadingPreferencesScope scope,
        SiteIdentity? site)
    {
        Context = context;
        Scope = scope;
        Site = site;
    }

    public BrowsingContext Context { get; }

    public ReadingPreferencesScope Scope { get; }

    public SiteIdentity? Site { get; }

    public static ControllerResult<ReadingPreferencesTarget> GlobalPersistent(BrowsingContext context) =>
        Create(context, ReadingPreferencesScope.GlobalPersistent, null);

    public static ControllerResult<ReadingPreferencesTarget> SitePersistent(
        BrowsingContext context,
        SiteIdentity site) =>
        Create(context, ReadingPreferencesScope.SitePersistent, site);

    public static ControllerResult<ReadingPreferencesTarget> Session(
        BrowsingContext context,
        SiteIdentity? site = null) =>
        Create(context, ReadingPreferencesScope.Session, site);

    private static ControllerResult<ReadingPreferencesTarget> Create(
        BrowsingContext context,
        ReadingPreferencesScope scope,
        SiteIdentity? site)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.IsStructurallyValid ||
            !Enum.IsDefined(scope) ||
            (scope == ReadingPreferencesScope.SitePersistent && site is null))
        {
            return ControllerResult<ReadingPreferencesTarget>.Failure(ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "error.reading.target_invalid"));
        }

        if (context.Privacy.IsPrivate && scope != ReadingPreferencesScope.Session)
        {
            return ControllerResult<ReadingPreferencesTarget>.Failure(ControllerError.Create(
                ControllerErrorCode.PolicyDenied,
                "error.reading.private_persistence_denied"));
        }

        return ControllerResult<ReadingPreferencesTarget>.Success(
            new ReadingPreferencesTarget(context, scope, site));
    }
}

public sealed record BeginReadingPreviewIntent(
    ReadingPreferencesTarget Target,
    ReadingPreferencesState State);

public sealed record UpdateReadingPreviewIntent(
    ReadingPreferencesTarget Target,
    ReadingPreviewId PreviewId,
    ReadingPreferencesState State);

public sealed record ApplyReadingPreferencesIntent(
    ReadingPreferencesTarget Target,
    ReadingPreviewId PreviewId,
    ReadingPreferencesState State);

public sealed record CancelReadingPreviewIntent(
    ReadingPreferencesTarget Target,
    ReadingPreviewId PreviewId);

public sealed record EndReadingPreviewIntent(
    ReadingPreferencesTarget Target,
    ReadingPreviewId PreviewId);

public sealed record ResetReadingPreferencesIntent(ReadingPreferencesTarget Target);

public sealed record ReadingPreviewSession(
    ReadingPreferencesTarget Target,
    ReadingPreviewId PreviewId,
    ReadingPreferencesState OriginalState,
    ReadingPreferencesState PreviewState);

public sealed record GlobalReadingPreferencesSyncProjection(
    bool Enabled,
    string FontFamily,
    decimal TextScale,
    decimal LineHeight,
    decimal LetterSpacing,
    decimal WordSpacing,
    ReadingContrast Contrast,
    ReadingTint Tint,
    ReadingLineFocus LineFocus);

public sealed record ReadingEffectCommand(
    ReadingPreferencesTarget Target,
    ReadingPreferencesState State,
    ReadingPreviewId? PreviewId);

public static class ReadingPreviewErrors
{
    public static ControllerError Stale() => ControllerError.Create(
        ControllerErrorCode.Conflict,
        "error.reading.preview_stale");

    public static ControllerError NotFound() => ControllerError.Create(
        ControllerErrorCode.NotFound,
        "error.reading.preview_not_found");
}

public sealed class ReadingEffectResult
{
    private ReadingEffectResult(
        ReadingEffectResultKind kind,
        IReadOnlyList<ReadingPreferenceField> unsupportedFields,
        ControllerError? error)
    {
        Kind = kind;
        UnsupportedFields = unsupportedFields;
        Error = error;
    }

    public ReadingEffectResultKind Kind { get; }

    public IReadOnlyList<ReadingPreferenceField> UnsupportedFields { get; }

    public ControllerError? Error { get; }

    public static ReadingEffectResult Applied() =>
        new(
            ReadingEffectResultKind.Applied,
            Array.Empty<ReadingPreferenceField>(),
            null);

    public static ReadingEffectResult Unsupported(IEnumerable<ReadingPreferenceField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var copy = fields.Distinct().ToArray();
        if (copy.Length == 0 || copy.Any(field => !Enum.IsDefined(field)))
        {
            throw new ArgumentException(
                "At least one valid unsupported field is required.",
                nameof(fields));
        }

        return new ReadingEffectResult(
            ReadingEffectResultKind.Unsupported,
            new ReadOnlyCollection<ReadingPreferenceField>(copy),
            null);
    }

    public static ReadingEffectResult Failed(ControllerError error) =>
        new(
            ReadingEffectResultKind.Failed,
            Array.Empty<ReadingPreferenceField>(),
            error ?? throw new ArgumentNullException(nameof(error)));
}

public interface IReadingPreferencesController
{
    ValueTask<ControllerResult<ReadingPreferencesCapabilities>> GetCapabilitiesAsync(
        ReadingPreferencesTarget target,
        CancellationToken cancellationToken);

    ValueTask<ControllerResult<ReadingPreferencesState>> GetCurrentAsync(
        ReadingPreferencesTarget target,
        CancellationToken cancellationToken);

    ValueTask<ControllerResult<ReadingPreviewSession>> BeginPreviewAsync(
        BeginReadingPreviewIntent intent,
        CancellationToken cancellationToken);

    ValueTask<ControllerResult<ReadingPreviewSession>> UpdatePreviewAsync(
        UpdateReadingPreviewIntent intent,
        CancellationToken cancellationToken);

    /// <summary>
    /// On success, the implementation atomically persists the selected target,
    /// applies the host effect, and closes the matching preview. A stale preview
    /// returns Conflict and an absent preview returns NotFound.
    /// </summary>
    ValueTask<ControllerResult<ReadingPreferencesState>> ApplyAsync(
        ApplyReadingPreferencesIntent intent,
        CancellationToken cancellationToken);

    ValueTask<ControllerResult> CancelPreviewAsync(
        CancelReadingPreviewIntent intent,
        CancellationToken cancellationToken);

    ValueTask<ControllerResult> EndPreviewAsync(
        EndReadingPreviewIntent intent,
        CancellationToken cancellationToken);

    ValueTask<ControllerResult<ReadingPreferencesState>> ResetAsync(
        ResetReadingPreferencesIntent intent,
        CancellationToken cancellationToken);
}

public interface IReadingPreferencesStore
{
    ValueTask<ControllerResult<ReadingPreferencesState>> LoadAsync(
        ReadingPreferencesTarget target,
        CancellationToken cancellationToken);

    ValueTask<ControllerResult> SaveAsync(
        ReadingPreferencesTarget target,
        ReadingPreferencesState state,
        CancellationToken cancellationToken);

    ValueTask<ControllerResult> ResetAsync(
        ReadingPreferencesTarget target,
        CancellationToken cancellationToken);
}

public interface IReadingPreferencesSyncProjector
{
    ValueTask<ControllerResult<GlobalReadingPreferencesSyncProjection>> ProjectAsync(
        PrivacyContext context,
        ReadingPreferencesTarget target,
        ReadingPreferencesState state,
        CancellationToken cancellationToken);
}

public interface IReadingEffectAdapter
{
    ValueTask<ReadingEffectResult> ApplyAsync(
        ReadingEffectCommand command,
        CancellationToken cancellationToken);

    ValueTask<ReadingEffectResult> ClearAsync(
        ReadingPreferencesTarget target,
        ReadingPreviewId? previewId,
        CancellationToken cancellationToken);
}
