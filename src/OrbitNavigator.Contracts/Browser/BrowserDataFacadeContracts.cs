using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Sync;
using OrbitNavigator.Contracts.Updates;

namespace OrbitNavigator.Contracts.Browser;

public readonly record struct BookmarkId(ProfileId ProfileId, Guid Value)
{
    public bool IsEmpty => ProfileId.IsEmpty || Value == Guid.Empty;
}

public sealed record BookmarkEntry(
    BookmarkId Id,
    Uri Target,
    string Title,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    public string? Note { get; init; }
}

public sealed record BookmarkQuery(
    PrivacyContext Context,
    string? SearchText,
    int MaximumItems);

public sealed record AddBookmarkIntent(
    BrowsingContext Context,
    Uri Target,
    string Title)
{
    public string? Note { get; init; }
}

public sealed record RemoveBookmarkIntent(
    PrivacyContext Context,
    BookmarkId BookmarkId);

public interface IBookmarksFacade
{
    ValueTask<ControllerResult<IReadOnlyList<BookmarkEntry>>> QueryAsync(
        BookmarkQuery query,
        CancellationToken cancellationToken = default);

    ValueTask<ControllerResult<BookmarkEntry>> AddAsync(
        AddBookmarkIntent intent,
        CancellationToken cancellationToken = default);

    ValueTask<ControllerResult> RemoveAsync(
        RemoveBookmarkIntent intent,
        CancellationToken cancellationToken = default);
}

public readonly record struct HistoryVisitId(ProfileId ProfileId, Guid Value)
{
    public bool IsEmpty => ProfileId.IsEmpty || Value == Guid.Empty;
}

public sealed record HistoryEntry(
    HistoryVisitId Id,
    Uri Target,
    string Title,
    DateTimeOffset LastVisitedAtUtc,
    int VisitCount);

public sealed record HistoryQuery(
    PrivacyContext Context,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    int MaximumItems);

public sealed record RecordHistoryVisitIntent(
    BrowsingContext Context,
    Uri Target,
    string Title,
    DateTimeOffset VisitedAtUtc);

public sealed record ClearHistoryIntent(
    PrivacyContext Context,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    bool Confirmed);

public interface IHistoryFacade
{
    ValueTask<ControllerResult<IReadOnlyList<HistoryEntry>>> QueryAsync(
        HistoryQuery query,
        CancellationToken cancellationToken = default);

    ValueTask<ControllerResult<HistoryEntry>> RecordVisitAsync(
        RecordHistoryVisitIntent intent,
        CancellationToken cancellationToken = default);

    ValueTask<ControllerResult> ClearAsync(
        ClearHistoryIntent intent,
        CancellationToken cancellationToken = default);
}

public readonly record struct DownloadRecordId(ProfileId ProfileId, Guid Value)
{
    public bool IsEmpty => ProfileId.IsEmpty || Value == Guid.Empty;
}

public enum DownloadLifecycleState
{
    InProgress = 0,
    Completed = 1,
    Cancelled = 2,
    Failed = 3,
}

public sealed record DownloadRecord(
    DownloadRecordId Id,
    Uri Source,
    string FileName,
    long? TotalBytes,
    long ReceivedBytes,
    DownloadLifecycleState State,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc);

public sealed record DownloadsQuery(
    PrivacyContext Context,
    int MaximumItems);

public sealed record DownloadRecordIntent(
    PrivacyContext Context,
    DownloadRecordId DownloadId);

public sealed record ClearDownloadRecordsIntent(
    PrivacyContext Context,
    IReadOnlySet<DownloadRecordId> DownloadIds,
    bool Confirmed);

public sealed record ClearDownloadRecordsReceipt(
    int RemovedRecordCount,
    DownloadedFilesDisposition DownloadedFiles);

public interface IDownloadsFacade
{
    event EventHandler? Changed;

    ValueTask<ControllerResult<IReadOnlyList<DownloadRecord>>> QueryAsync(
        DownloadsQuery query,
        CancellationToken cancellationToken = default);

    ValueTask<ControllerResult> CancelAsync(
        DownloadRecordIntent intent,
        CancellationToken cancellationToken = default);

    ValueTask<ControllerResult> OpenFileAsync(
        DownloadRecordIntent intent,
        CancellationToken cancellationToken = default);

    ValueTask<ControllerResult<ClearDownloadRecordsReceipt>> ClearRecordsAsync(
        ClearDownloadRecordsIntent intent,
        CancellationToken cancellationToken = default);
}

public readonly record struct BrowserSettingsRevision(Guid Value)
{
    public bool IsEmpty => Value == Guid.Empty;
}

public sealed record BrowserCoreSettings(
    string SearchProviderId,
    bool RestoreOpenTabs,
    bool AskWhereToSaveDownloads,
    UpdatePreference UpdatePreference);

public sealed record BrowserSettingsSnapshot(
    PrivacyContext Context,
    BrowserSettingsRevision Revision,
    BrowserCoreSettings Values);

public sealed record UpdateBrowserSettingsIntent(
    PrivacyContext Context,
    BrowserSettingsRevision ExpectedRevision,
    BrowserCoreSettings Values);

public interface IBrowserSettingsFacade
{
    ValueTask<ControllerResult<BrowserSettingsSnapshot>> GetAsync(
        PrivacyContext context,
        CancellationToken cancellationToken = default);

    ValueTask<ControllerResult<BrowserSettingsSnapshot>> UpdateAsync(
        UpdateBrowserSettingsIntent intent,
        CancellationToken cancellationToken = default);
}

public sealed record CreatePrivateWindowIntent(
    PrivacyContext InitiatingContext,
    BrowserWindowId InitiatingWindowId);

public sealed record PrivateWindowSession(
    PrivacyContext Context,
    BrowserWindowId WindowId,
    DateTimeOffset OpenedAtUtc);

public sealed record ClosePrivateWindowIntent(
    PrivacyContext Context,
    BrowserWindowId WindowId);

public sealed record PrivateWindowCloseReceipt(
    ProfileId ProfileId,
    BrowserSessionId SessionId,
    BrowserWindowId WindowId,
    bool EphemeralProfileDeleted,
    DateTimeOffset ClosedAtUtc);

public interface IPrivateWindowLifecycle
{
    ValueTask<ControllerResult<PrivateWindowSession>> OpenAsync(
        CreatePrivateWindowIntent intent,
        CancellationToken cancellationToken = default);

    ValueTask<ControllerResult<PrivateWindowCloseReceipt>> CloseAsync(
        ClosePrivateWindowIntent intent,
        CancellationToken cancellationToken = default);
}

public readonly record struct TabGroupCatalogRevision(Guid Value)
{
    public bool IsEmpty => Value == Guid.Empty;
}

public sealed record TabGroupMetadata(
    BrowserTabGroupId GroupId,
    string Name,
    bool IsCollapsed,
    IReadOnlyList<BrowserTabId> TabOrder,
    DateTimeOffset UpdatedAtUtc);

public sealed record TabGroupMetadataSnapshot(
    PrivacyContext Context,
    BrowserWindowId WindowId,
    TabGroupCatalogRevision Revision,
    IReadOnlyList<TabGroupMetadata> Groups);

public sealed record ReplaceTabGroupMetadataIntent(
    PrivacyContext Context,
    BrowserWindowId WindowId,
    TabGroupCatalogRevision ExpectedRevision,
    IReadOnlyList<TabGroupMetadata> Groups);

public interface ITabGroupMetadataStore
{
    ValueTask<ControllerResult<TabGroupMetadataSnapshot>> LoadAsync(
        PrivacyContext context,
        BrowserWindowId windowId,
        CancellationToken cancellationToken = default);

    ValueTask<ControllerResult<TabGroupMetadataSnapshot>> ReplaceAsync(
        ReplaceTabGroupMetadataIntent intent,
        CancellationToken cancellationToken = default);
}
