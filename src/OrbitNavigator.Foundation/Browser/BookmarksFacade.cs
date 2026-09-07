using System.Text.Json;
using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;

namespace OrbitNavigator.Foundation.Browser;

public sealed class BookmarksFacade : IBookmarksFacade
{
    public const int MaximumBookmarks = 500;
    public const int MaximumTitleLength = 200;
    public const int MaximumNoteLength = 500;

    private const string StorageKey = "catalog";
    private readonly IProfileStorage _storage;
    private readonly IClock _clock;
    private readonly ProfileStorageNamespace _namespace;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public BookmarksFacade(IProfileStorage storage, IClock clock)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _namespace = ProfileStorageNamespace.Create("browser.bookmarks").Value!;
    }

    public async ValueTask<ControllerResult<IReadOnlyList<BookmarkEntry>>> QueryAsync(
        BookmarkQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Context is not { IsStructurallyValid: true } ||
            query.MaximumItems is < 1 or > MaximumBookmarks)
        {
            return Invalid<IReadOnlyList<BookmarkEntry>>();
        }

        var loaded = await LoadAsync(query.Context, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return ControllerResult<IReadOnlyList<BookmarkEntry>>.Failure(loaded.Error!);
        }

        var search = query.SearchText?.Trim();
        var entries = loaded.Value!.Entries
            .Where(entry => string.IsNullOrWhiteSpace(search) ||
                entry.Title.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                entry.Target.AbsoluteUri.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.UpdatedAtUtc)
            .Take(query.MaximumItems)
            .ToArray();
        return ControllerResult<IReadOnlyList<BookmarkEntry>>.Success(entries);
    }

    public async ValueTask<ControllerResult<BookmarkEntry>> AddAsync(
        AddBookmarkIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (intent.Context is not { IsStructurallyValid: true } ||
            !ValidTarget(intent.Target) ||
            !ValidTitle(intent.Title) ||
            !ValidNote(intent.Note))
        {
            return Invalid<BookmarkEntry>();
        }
        if (intent.Context.Privacy.IsPrivate)
        {
            return PolicyDenied<BookmarkEntry>();
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await LoadAsync(intent.Context.Privacy, cancellationToken).ConfigureAwait(false);
            if (!loaded.IsSuccess)
            {
                return ControllerResult<BookmarkEntry>.Failure(loaded.Error!);
            }

            var entries = loaded.Value!.Entries.ToList();
            var index = entries.FindIndex(entry =>
                Uri.Compare(entry.Target, intent.Target, UriComponents.AbsoluteUri,
                    UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0);
            var now = _clock.UtcNow;
            var note = NormalizeNote(intent.Note);
            BookmarkEntry bookmark;
            if (index >= 0)
            {
                bookmark = entries[index] with
                {
                    Title = intent.Title.Trim(),
                    Note = note,
                    UpdatedAtUtc = now,
                };
                entries[index] = bookmark;
            }
            else
            {
                if (entries.Count >= MaximumBookmarks)
                {
                    return ControllerResult<BookmarkEntry>.Failure(ControllerError.Create(
                        ControllerErrorCode.InvalidRequest,
                        "error.bookmarks.capacity_reached"));
                }
                bookmark = new BookmarkEntry(
                    new BookmarkId(intent.Context.Privacy.ProfileId, Guid.NewGuid()),
                    intent.Target,
                    intent.Title.Trim(),
                    now,
                    now)
                {
                    Note = note,
                };
                entries.Add(bookmark);
            }

            var write = await WriteAsync(
                intent.Context.Privacy,
                loaded.Value.Revision,
                entries,
                cancellationToken).ConfigureAwait(false);
            return write.IsSuccess
                ? ControllerResult<BookmarkEntry>.Success(bookmark)
                : ControllerResult<BookmarkEntry>.Failure(write.Error!);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ControllerResult> RemoveAsync(
        RemoveBookmarkIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (intent.Context is not { IsStructurallyValid: true } ||
            intent.BookmarkId.IsEmpty ||
            intent.BookmarkId.ProfileId != intent.Context.ProfileId)
        {
            return ControllerResult.Failure(ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "error.bookmarks.invalid"));
        }
        if (intent.Context.IsPrivate)
        {
            return ControllerResult.Failure(ControllerError.Create(
                ControllerErrorCode.PolicyDenied,
                "error.bookmarks.private_write_denied"));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await LoadAsync(intent.Context, cancellationToken).ConfigureAwait(false);
            if (!loaded.IsSuccess)
            {
                return ControllerResult.Failure(loaded.Error!);
            }

            var entries = loaded.Value!.Entries.ToList();
            if (entries.RemoveAll(entry => entry.Id == intent.BookmarkId) != 1)
            {
                return ControllerResult.Failure(ControllerError.Create(
                    ControllerErrorCode.NotFound,
                    "error.bookmarks.not_found"));
            }

            return await WriteAsync(
                intent.Context,
                loaded.Value.Revision,
                entries,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<ControllerResult<StoredBookmarks>> LoadAsync(
        PrivacyContext context,
        CancellationToken cancellationToken)
    {
        var address = Address(context);
        if (!address.IsSuccess)
        {
            return ControllerResult<StoredBookmarks>.Failure(address.Error!);
        }

        var read = await _storage.ReadAsync(address.Value!, cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            return read.Error?.Code == ControllerErrorCode.NotFound
                ? ControllerResult<StoredBookmarks>.Success(new(null, []))
                : ControllerResult<StoredBookmarks>.Failure(read.Error!);
        }

        try
        {
            var entries = JsonSerializer.Deserialize<BookmarkEntry[]>(read.Value!.Payload.Span);
            if (entries is null || !ValidateCatalog(context.ProfileId, entries))
            {
                return Corrupt<StoredBookmarks>();
            }
            return ControllerResult<StoredBookmarks>.Success(new(read.Value.Revision, entries));
        }
        catch (JsonException)
        {
            return Corrupt<StoredBookmarks>();
        }
    }

    private async ValueTask<ControllerResult> WriteAsync(
        PrivacyContext context,
        ProfileStorageRevision? expectedRevision,
        IReadOnlyList<BookmarkEntry> entries,
        CancellationToken cancellationToken)
    {
        var address = Address(context).Value!;
        var request = ProfileStorageWriteRequest.Create(
            address,
            JsonSerializer.SerializeToUtf8Bytes(entries),
            expectedRevision).Value!;
        var write = await _storage.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        return write.IsSuccess ? ControllerResult.Success() : ControllerResult.Failure(write.Error!);
    }

    private ControllerResult<ProfileStorageAddress> Address(PrivacyContext context)
    {
        if (context is not { IsStructurallyValid: true })
        {
            return ControllerResult<ProfileStorageAddress>.Failure(ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "error.bookmarks.context_invalid"));
        }

        var normalContext = context.IsPrivate
            ? new PrivacyContext(context.ProfileId, context.SessionId, BrowserProfileMode.Normal)
            : context;
        return ProfileStorageAddress.Create(
            normalContext,
            _namespace,
            ProfileStorageKey.Create(StorageKey).Value,
            ProfileStorageDurability.Persistent);
    }

    private static bool ValidateCatalog(ProfileId profileId, IReadOnlyList<BookmarkEntry> entries)
    {
        if (entries.Count > MaximumBookmarks)
        {
            return false;
        }

        var ids = new HashSet<BookmarkId>();
        return entries.All(entry =>
            entry is not null &&
            !entry.Id.IsEmpty &&
            entry.Id.ProfileId == profileId &&
            ids.Add(entry.Id) &&
            ValidTarget(entry.Target) &&
            ValidTitle(entry.Title) &&
            ValidNote(entry.Note) &&
            entry.CreatedAtUtc <= entry.UpdatedAtUtc);
    }

    private static bool ValidTarget(Uri target) =>
        target is { IsAbsoluteUri: true } &&
        target.Scheme is "http" or "https" &&
        string.IsNullOrEmpty(target.UserInfo) &&
        !string.IsNullOrWhiteSpace(target.IdnHost);

    private static bool ValidTitle(string title) =>
        !string.IsNullOrWhiteSpace(title) && title.Trim().Length <= MaximumTitleLength;

    private static bool ValidNote(string? note) =>
        note is null || note.Trim().Length <= MaximumNoteLength;

    private static string? NormalizeNote(string? note)
    {
        var value = note?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static ControllerResult<T> Invalid<T>() where T : class =>
        ControllerResult<T>.Failure(ControllerError.Create(
            ControllerErrorCode.InvalidRequest,
            "error.bookmarks.invalid"));

    private static ControllerResult<T> PolicyDenied<T>() where T : class =>
        ControllerResult<T>.Failure(ControllerError.Create(
            ControllerErrorCode.PolicyDenied,
            "error.bookmarks.private_write_denied"));

    private static ControllerResult<T> Corrupt<T>() where T : class =>
        ControllerResult<T>.Failure(ControllerError.Create(
            ControllerErrorCode.IntegrityFailure,
            "error.bookmarks.storage_corrupt"));

    private sealed record StoredBookmarks(
        ProfileStorageRevision? Revision,
        IReadOnlyList<BookmarkEntry> Entries);
}
