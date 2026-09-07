namespace OrbitNavigator.Presentation.Offline;

public readonly record struct OfflineReadingItemId(Guid Value)
{
    public bool IsEmpty => Value == Guid.Empty;
}

public sealed record OfflineReadingItemPresentation(
    OfflineReadingItemId ItemId,
    string Title,
    Uri SourceAddress,
    DateTimeOffset SavedAtUtc,
    long SizeBytes)
{
    public OfflineReadingItemPresentation Validate()
    {
        if (ItemId.IsEmpty || SourceAddress is not { IsAbsoluteUri: true } ||
            SourceAddress.Scheme is not ("http" or "https") || SavedAtUtc == default || SizeBytes < 0)
        {
            throw new ArgumentException("An offline item requires a valid ID, web source, saved time, and size.");
        }

        var title = string.IsNullOrWhiteSpace(Title) ? SourceAddress.Host : Title.Trim();
        return this with { Title = title.Length <= 300 ? title : title[..300] };
    }
}

public sealed record OfflineReadingCatalogPresentation(
    long Revision,
    bool IsPrivate,
    bool CanSaveCurrentPage,
    string CurrentPageTitle,
    Uri? CurrentPageAddress,
    bool IsBusy,
    string SafeStatusMessage,
    IReadOnlyList<OfflineReadingItemPresentation> Items)
{
    public static OfflineReadingCatalogPresentation Unavailable(bool isPrivate, string message) => new(
        0,
        isPrivate,
        false,
        string.Empty,
        null,
        false,
        message,
        []);

    public OfflineReadingCatalogPresentation Validate()
    {
        if (Revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Revision));
        }
        ArgumentNullException.ThrowIfNull(Items);
        var safeAddress = CurrentPageAddress is { IsAbsoluteUri: true } address &&
            address.Scheme is "http" or "https"
            ? address
            : null;
        var items = Items.Select(item => item.Validate()).ToArray();
        if (items.Select(item => item.ItemId).Distinct().Count() != items.Length)
        {
            throw new ArgumentException("Offline item IDs must be unique.", nameof(Items));
        }

        return this with
        {
            CanSaveCurrentPage = !IsPrivate && CanSaveCurrentPage && safeAddress is not null && !IsBusy,
            CurrentPageTitle = string.IsNullOrWhiteSpace(CurrentPageTitle)
                ? safeAddress?.Host ?? string.Empty
                : CurrentPageTitle.Trim(),
            CurrentPageAddress = safeAddress,
            SafeStatusMessage = string.IsNullOrWhiteSpace(SafeStatusMessage)
                ? IsPrivate
                    ? "Offline reading is unavailable in private browsing."
                    : "Saved pages stay on this device and are not live websites."
                : SafeStatusMessage.Trim(),
            Items = IsPrivate ? [] : items,
        };
    }
}

public abstract record OfflineReadingAction(Guid StableActionId, long ExpectedRevision)
{
    protected static Guid RequireId(Guid value) =>
        value == Guid.Empty ? throw new ArgumentException("A stable action ID is required.") : value;
}

public sealed record SavePageForOfflineAction(Guid Id, long Revision) :
    OfflineReadingAction(RequireId(Id), Revision);

public sealed record OpenOfflineLibraryAction(Guid Id, long Revision) :
    OfflineReadingAction(RequireId(Id), Revision);

public sealed record OpenOfflineReadingItemAction(Guid Id, long Revision, OfflineReadingItemId ItemId) :
    OfflineReadingAction(RequireId(Id), Revision);

public sealed record DeleteOfflineReadingItemAction(Guid Id, long Revision, OfflineReadingItemId ItemId) :
    OfflineReadingAction(RequireId(Id), Revision);

public sealed class OfflineReadingActionRequestedEventArgs(OfflineReadingAction action) : EventArgs
{
    public OfflineReadingAction Action { get; } = action ?? throw new ArgumentNullException(nameof(action));
}
