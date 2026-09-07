using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.Contracts.Privacy;

public readonly record struct ClipboardShelfItemId(ProfileId ProfileId, Guid Value)
{
    public bool IsEmpty => ProfileId.IsEmpty || Value == Guid.Empty;
}

public readonly record struct ClipboardCaptureLeaseId(ProfileId ProfileId, Guid Value)
{
    public bool IsEmpty => ProfileId.IsEmpty || Value == Guid.Empty;
}

public enum ClipboardShelfAvailability
{
    Available = 0,
    Unavailable = 1,
}

public enum ClipboardShelfUnavailableReason
{
    None = 0,
    PrivateMode = 1,
    PlatformUnavailable = 2,
    PolicyDisabled = 3,
}

public enum ClipboardShelfContentKind
{
    Text = 0,
    Link = 1,
}

public enum ClipboardCaptureClassification
{
    ExplicitUserInitiatedNonSensitive = 0,
}

public sealed record ClipboardShelfItemSummary(
    ClipboardShelfItemId Id,
    ClipboardShelfContentKind Kind,
    string DisplayPreview,
    SiteIdentity? SourceSite,
    DateTimeOffset CapturedAtUtc);

public sealed record ClipboardShelfReadModel(
    ClipboardShelfAvailability Availability,
    ClipboardShelfUnavailableReason UnavailableReason,
    int Capacity,
    IReadOnlyList<ClipboardShelfItemSummary> Items)
{
    public const int MaximumCapacity = 10;

    public static ClipboardShelfReadModel PrivateUnavailable() =>
        new(
            ClipboardShelfAvailability.Unavailable,
            ClipboardShelfUnavailableReason.PrivateMode,
            MaximumCapacity,
            Array.Empty<ClipboardShelfItemSummary>());
}

public sealed record UseClipboardShelfItemIntent(
    BrowsingContext Context,
    ClipboardShelfItemId ItemId);

public sealed record ClearClipboardShelfIntent(
    BrowsingContext Context,
    bool Confirmed);

public sealed record ClipboardShelfUseReceipt(
    ClipboardShelfItemId ItemId,
    BrowserTabId DestinationTabId,
    DateTimeOffset UsedAtUtc);

public sealed record ClipboardShelfCaptureRequest(
    BrowsingContext Context,
    ClipboardCaptureLeaseId ContentLeaseId,
    ClipboardShelfContentKind Kind,
    ClipboardCaptureClassification Classification,
    SiteIdentity? SourceSite,
    DateTimeOffset CapturedAtUtc);

public interface IClipboardShelfController
{
    ValueTask<ControllerResult<ClipboardShelfReadModel>> GetAsync(
        BrowsingContext context,
        CancellationToken cancellationToken);

    ValueTask<ControllerResult<ClipboardShelfUseReceipt>> UseAsync(
        UseClipboardShelfItemIntent intent,
        CancellationToken cancellationToken);

    ValueTask<ControllerResult> ClearAsync(
        ClearClipboardShelfIntent intent,
        CancellationToken cancellationToken);
}

public interface IClipboardShelfCaptureSink
{
    ValueTask<ControllerResult> CaptureAsync(
        ClipboardShelfCaptureRequest request,
        CancellationToken cancellationToken);
}

public static class ClipboardShelfPolicy
{
    public static ControllerResult<ClipboardShelfReadModel> Read(BrowsingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.IsStructurallyValid)
        {
            return ControllerResult<ClipboardShelfReadModel>.Failure(ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "error.clipboard_shelf.context_invalid"));
        }

        return context.Privacy.IsPrivate
            ? ControllerResult<ClipboardShelfReadModel>.Success(ClipboardShelfReadModel.PrivateUnavailable())
            : ControllerResult<ClipboardShelfReadModel>.Failure(ControllerError.Create(
                ControllerErrorCode.Unavailable,
                "error.clipboard_shelf.provider_required"));
    }

    public static ControllerResult AuthorizeMutation(BrowsingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.IsStructurallyValid)
        {
            return ControllerResult.Failure(ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "error.clipboard_shelf.context_invalid"));
        }

        return context.Privacy.IsPrivate
            ? ControllerResult.Failure(ControllerError.Create(
                ControllerErrorCode.PolicyDenied,
                "error.clipboard_shelf.private_mutation_denied"))
            : ControllerResult.Success();
    }
}
