using System.Text.Json;
using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;

namespace OrbitNavigator.Foundation.Browser;

public sealed class HistoryFacade : IHistoryFacade
{
    public const int MaximumEntries = 5000;
    private const string StorageKey = "visits";
    private readonly IProfileStorage _storage;
    private readonly ProfileStorageNamespace _namespace;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public HistoryFacade(IProfileStorage storage)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _namespace = ProfileStorageNamespace.Create("browser.history").Value!;
    }

    public async ValueTask<ControllerResult<IReadOnlyList<HistoryEntry>>> QueryAsync(
        HistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Context is not { IsStructurallyValid: true } ||
            query.MaximumItems is < 1 or > MaximumEntries ||
            (query.FromUtc is { } from && query.ToUtc is { } to && from > to))
        {
            return Invalid<IReadOnlyList<HistoryEntry>>();
        }
        if (query.Context.IsPrivate)
        {
            return ControllerResult<IReadOnlyList<HistoryEntry>>.Success([]);
        }

        var loaded = await LoadAsync(query.Context, cancellationToken).ConfigureAwait(false);
        return loaded.IsSuccess
            ? ControllerResult<IReadOnlyList<HistoryEntry>>.Success(loaded.Value!.Entries
                .Where(entry =>
                    (query.FromUtc is null || entry.LastVisitedAtUtc >= query.FromUtc) &&
                    (query.ToUtc is null || entry.LastVisitedAtUtc <= query.ToUtc))
                .OrderByDescending(entry => entry.LastVisitedAtUtc)
                .Take(query.MaximumItems)
                .ToArray())
            : ControllerResult<IReadOnlyList<HistoryEntry>>.Failure(loaded.Error!);
    }

    public async ValueTask<ControllerResult<HistoryEntry>> RecordVisitAsync(
        RecordHistoryVisitIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (intent.Context is not { IsStructurallyValid: true } ||
            !ValidTarget(intent.Target) ||
            string.IsNullOrWhiteSpace(intent.Title) ||
            intent.VisitedAtUtc == default)
        {
            return Invalid<HistoryEntry>();
        }
        if (intent.Context.Privacy.IsPrivate)
        {
            return PolicyDenied<HistoryEntry>();
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await LoadAsync(intent.Context.Privacy, cancellationToken).ConfigureAwait(false);
            if (!loaded.IsSuccess)
            {
                return ControllerResult<HistoryEntry>.Failure(loaded.Error!);
            }
            var entries = loaded.Value!.Entries.ToList();
            var index = entries.FindIndex(entry => Uri.Compare(
                entry.Target,
                intent.Target,
                UriComponents.AbsoluteUri,
                UriFormat.SafeUnescaped,
                StringComparison.OrdinalIgnoreCase) == 0);
            HistoryEntry updated;
            if (index >= 0)
            {
                var current = entries[index];
                updated = current with
                {
                    Title = intent.Title.Trim(),
                    LastVisitedAtUtc = intent.VisitedAtUtc,
                    VisitCount = checked(current.VisitCount + 1),
                };
                entries[index] = updated;
            }
            else
            {
                updated = new(
                    new HistoryVisitId(intent.Context.Privacy.ProfileId, Guid.NewGuid()),
                    intent.Target,
                    intent.Title.Trim(),
                    intent.VisitedAtUtc,
                    1);
                entries.Add(updated);
                if (entries.Count > MaximumEntries)
                {
                    entries = entries
                        .OrderByDescending(entry => entry.LastVisitedAtUtc)
                        .Take(MaximumEntries)
                        .ToList();
                }
            }

            var write = await WriteAsync(
                intent.Context.Privacy,
                loaded.Value.Revision,
                entries,
                cancellationToken).ConfigureAwait(false);
            return write.IsSuccess
                ? ControllerResult<HistoryEntry>.Success(updated)
                : ControllerResult<HistoryEntry>.Failure(write.Error!);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ControllerResult> ClearAsync(
        ClearHistoryIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (intent.Context is not { IsStructurallyValid: true } ||
            !intent.Confirmed ||
            (intent.FromUtc is { } from && intent.ToUtc is { } to && from > to))
        {
            return ControllerResult.Failure(ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "error.history.clear_invalid"));
        }
        if (intent.Context.IsPrivate)
        {
            return ControllerResult.Failure(ControllerError.Create(
                ControllerErrorCode.PolicyDenied,
                "error.history.private_write_denied"));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await LoadAsync(intent.Context, cancellationToken).ConfigureAwait(false);
            if (!loaded.IsSuccess)
            {
                return ControllerResult.Failure(loaded.Error!);
            }
            var remaining = loaded.Value!.Entries.Where(entry => !(
                (intent.FromUtc is null || entry.LastVisitedAtUtc >= intent.FromUtc) &&
                (intent.ToUtc is null || entry.LastVisitedAtUtc <= intent.ToUtc))).ToArray();
            return await WriteAsync(
                intent.Context,
                loaded.Value.Revision,
                remaining,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<ControllerResult<StoredHistory>> LoadAsync(
        PrivacyContext context,
        CancellationToken cancellationToken)
    {
        var address = Address(context);
        var read = await _storage.ReadAsync(address, cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            return read.Error?.Code == ControllerErrorCode.NotFound
                ? ControllerResult<StoredHistory>.Success(new(null, []))
                : ControllerResult<StoredHistory>.Failure(read.Error!);
        }
        try
        {
            var entries = JsonSerializer.Deserialize<HistoryEntry[]>(read.Value!.Payload.Span);
            return entries is not null && Validate(context.ProfileId, entries)
                ? ControllerResult<StoredHistory>.Success(new(read.Value.Revision, entries))
                : Corrupt<StoredHistory>();
        }
        catch (JsonException)
        {
            return Corrupt<StoredHistory>();
        }
    }

    private async ValueTask<ControllerResult> WriteAsync(
        PrivacyContext context,
        ProfileStorageRevision? revision,
        IReadOnlyList<HistoryEntry> entries,
        CancellationToken cancellationToken)
    {
        var request = ProfileStorageWriteRequest.Create(
            Address(context),
            JsonSerializer.SerializeToUtf8Bytes(entries),
            revision).Value!;
        var write = await _storage.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        return write.IsSuccess ? ControllerResult.Success() : ControllerResult.Failure(write.Error!);
    }

    private ProfileStorageAddress Address(PrivacyContext context) =>
        ProfileStorageAddress.Create(
            context,
            _namespace,
            ProfileStorageKey.Create(StorageKey).Value,
            ProfileStorageDurability.Persistent).Value!;

    private static bool Validate(ProfileId profileId, IReadOnlyList<HistoryEntry> entries)
    {
        if (entries.Count > MaximumEntries)
        {
            return false;
        }
        var ids = new HashSet<HistoryVisitId>();
        return entries.All(entry =>
            entry is not null &&
            !entry.Id.IsEmpty &&
            entry.Id.ProfileId == profileId &&
            ids.Add(entry.Id) &&
            ValidTarget(entry.Target) &&
            !string.IsNullOrWhiteSpace(entry.Title) &&
            entry.VisitCount > 0 &&
            entry.LastVisitedAtUtc != default);
    }

    private static bool ValidTarget(Uri target) =>
        target is { IsAbsoluteUri: true } &&
        target.Scheme is "http" or "https" &&
        string.IsNullOrEmpty(target.UserInfo) &&
        !string.IsNullOrWhiteSpace(target.IdnHost);

    private static ControllerResult<T> Invalid<T>() where T : class =>
        ControllerResult<T>.Failure(ControllerError.Create(
            ControllerErrorCode.InvalidRequest,
            "error.history.invalid"));

    private static ControllerResult<T> PolicyDenied<T>() where T : class =>
        ControllerResult<T>.Failure(ControllerError.Create(
            ControllerErrorCode.PolicyDenied,
            "error.history.private_write_denied"));

    private static ControllerResult<T> Corrupt<T>() where T : class =>
        ControllerResult<T>.Failure(ControllerError.Create(
            ControllerErrorCode.IntegrityFailure,
            "error.history.storage_corrupt"));

    private sealed record StoredHistory(
        ProfileStorageRevision? Revision,
        IReadOnlyList<HistoryEntry> Entries);
}
