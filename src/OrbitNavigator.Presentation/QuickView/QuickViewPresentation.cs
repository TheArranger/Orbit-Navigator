namespace OrbitNavigator.Presentation.QuickView;

public enum QuickViewHostState
{
    Unavailable = 0,
    Ready = 1,
    Opening = 2,
    Open = 3,
    Closing = 4,
    Failed = 5,
}

public enum QuickViewStateTransferCapability
{
    AddressReloadOnly = 0,
    PreserveCurrentPageState = 1,
}

public sealed record QuickViewBookmarkPresentation(string StableId, string Title, Uri Target)
{
    public QuickViewBookmarkPresentation Validate()
    {
        if (string.IsNullOrWhiteSpace(StableId) || Target is not { IsAbsoluteUri: true } ||
            Target.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("A Quick View bookmark requires a stable ID and a web target.");
        }

        return this with
        {
            StableId = StableId.Trim(),
            Title = string.IsNullOrWhiteSpace(Title) ? Target.Host : Title.Trim(),
        };
    }
}

public sealed record QuickViewPresentation(
    long Revision,
    bool IsPrivate,
    bool IsNormalSite,
    QuickViewHostState HostState,
    string Title,
    Uri? Address,
    QuickViewStateTransferCapability TransferCapability,
    string SafeStatusMessage,
    IReadOnlyList<QuickViewBookmarkPresentation> Bookmarks)
{
    public static QuickViewPresentation Unavailable(bool isPrivate, string message) => new(
        0,
        isPrivate,
        false,
        QuickViewHostState.Unavailable,
        string.Empty,
        null,
        QuickViewStateTransferCapability.AddressReloadOnly,
        message,
        []);

    public bool CanShowAnchor => !IsPrivate && IsNormalSite && HostState != QuickViewHostState.Unavailable;

    public QuickViewPresentation Validate()
    {
        if (Revision < 0 || !Enum.IsDefined(HostState) || !Enum.IsDefined(TransferCapability))
        {
            throw new ArgumentOutOfRangeException(nameof(Revision));
        }
        ArgumentNullException.ThrowIfNull(Bookmarks);
        var address = Address is { IsAbsoluteUri: true } value && value.Scheme is "http" or "https"
            ? value
            : null;
        var bookmarks = Bookmarks.Select(bookmark => bookmark.Validate()).ToArray();
        if (bookmarks.Select(bookmark => bookmark.StableId).Distinct(StringComparer.Ordinal).Count() != bookmarks.Length)
        {
            throw new ArgumentException("Quick View bookmark IDs must be unique.", nameof(Bookmarks));
        }

        return this with
        {
            IsNormalSite = !IsPrivate && IsNormalSite,
            HostState = IsPrivate ? QuickViewHostState.Unavailable : HostState,
            Title = string.IsNullOrWhiteSpace(Title) ? address?.Host ?? "Quick View" : Title.Trim(),
            Address = address,
            SafeStatusMessage = string.IsNullOrWhiteSpace(SafeStatusMessage)
                ? HostState switch
                {
                    QuickViewHostState.Opening => "Opening Quick View.",
                    QuickViewHostState.Closing => "Closing Quick View.",
                    QuickViewHostState.Failed => "Quick View could not be opened.",
                    _ => "Quick View is ready.",
                }
                : SafeStatusMessage.Trim(),
            Bookmarks = IsPrivate ? [] : bookmarks,
        };
    }
}

public abstract record QuickViewAction(Guid StableActionId, long ExpectedRevision)
{
    protected static Guid RequireId(Guid value) =>
        value == Guid.Empty ? throw new ArgumentException("A stable action ID is required.") : value;
}

public sealed record OpenQuickViewAction(Guid Id, long Revision, string Query) :
    QuickViewAction(RequireId(Id), Revision);

public sealed record NavigateQuickViewAction(Guid Id, long Revision, string Query) :
    QuickViewAction(RequireId(Id), Revision);

public sealed record OpenQuickViewBookmarkAction(Guid Id, long Revision, string BookmarkId) :
    QuickViewAction(RequireId(Id), Revision);

public sealed record ExpandQuickViewToTabAction(Guid Id, long Revision, bool PreferStateTransfer) :
    QuickViewAction(RequireId(Id), Revision);

public sealed record CloseQuickViewAction(Guid Id, long Revision) :
    QuickViewAction(RequireId(Id), Revision);

public sealed record ResizeQuickViewAction(Guid Id, long Revision, double WidthRatio, double HeightRatio) :
    QuickViewAction(RequireId(Id), Revision);

public sealed class QuickViewActionRequestedEventArgs(QuickViewAction action) : EventArgs
{
    public QuickViewAction Action { get; } = action ?? throw new ArgumentNullException(nameof(action));
}
