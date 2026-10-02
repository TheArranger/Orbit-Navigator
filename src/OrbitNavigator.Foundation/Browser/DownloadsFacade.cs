using System.Diagnostics;
using System.Text.Json;
using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Sync;

namespace OrbitNavigator.Foundation.Browser;

public sealed record DownloadTrackingStart(
    PrivacyContext Context,
    Uri Source,
    string ResultFilePath,
    long? TotalBytes);

public sealed record DownloadTrackingRegistration(DownloadRecordId DownloadId);

public interface IDownloadRuntimeControl
{
    ValueTask<ControllerResult> CancelAsync(CancellationToken cancellationToken = default);
}

public interface IDownloadTrackingSink
{
    ValueTask<ControllerResult<DownloadTrackingRegistration>> BeginAsync(
        DownloadTrackingStart start,
        IDownloadRuntimeControl runtimeControl,
        CancellationToken cancellationToken = default);

    ValueTask UpdateAsync(
        PrivacyContext context,
        DownloadRecordId downloadId,
        long receivedBytes,
        long? totalBytes,
        DownloadLifecycleState state,
        CancellationToken cancellationToken = default);
}

public interface IDownloadFileLauncher
{
    ControllerResult Open(string fullPath);
}

/// <summary>Host-only private-session teardown; never writes profile storage.</summary>
public interface IPrivateDownloadSessionLifecycle
{
    ValueTask<ControllerResult> EndPrivateSessionAsync(
        PrivacyContext context,
        CancellationToken cancellationToken = default);
}

public sealed class WindowsDownloadFileLauncher : IDownloadFileLauncher
{
    public ControllerResult Open(string fullPath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = fullPath,
                UseShellExecute = true,
            });
            return ControllerResult.Success();
        }
        catch
        {
            return ControllerResult.Failure(ControllerError.Create(
                ControllerErrorCode.Unavailable,
                "error.downloads.open_failed"));
        }
    }
}

/// <summary>
/// Local-only download metadata and active-operation coordinator. Normal profile
/// records are durable; private records are isolated by private session and are
/// never sent to profile storage. Clearing records never removes downloaded files.
/// </summary>
public sealed class DownloadsFacade : IDownloadsFacade, IDownloadTrackingSink, IPrivateDownloadSessionLifecycle
{
    public const int MaximumRecords = 2000;
    private const string StorageKey = "catalog";
    private static readonly TimeSpan ProgressNotificationInterval = TimeSpan.FromMilliseconds(250);
    private readonly IProfileStorage _storage;
    private readonly IClock _clock;
    private readonly IDownloadFileLauncher _fileLauncher;
    private readonly ProfileStorageNamespace _namespace;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<DownloadRecordId, ActiveDownload> _active = [];
    private readonly Dictionary<PrivateSessionKey, List<StoredDownload>> _privateCatalogs = [];
    private readonly HashSet<PrivateSessionKey> _closedPrivateSessions = [];

    public DownloadsFacade(
        IProfileStorage storage,
        IClock clock,
        IDownloadFileLauncher? fileLauncher = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _fileLauncher = fileLauncher ?? new WindowsDownloadFileLauncher();
        _namespace = ProfileStorageNamespace.Create("browser.downloads").Value!;
    }

    public event EventHandler? Changed;

    public async ValueTask<ControllerResult<IReadOnlyList<DownloadRecord>>> QueryAsync(
        DownloadsQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Context is not { IsStructurallyValid: true } ||
            query.MaximumItems is < 1 or > MaximumRecords)
        {
            return Invalid<IReadOnlyList<DownloadRecord>>();
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsClosedPrivateSession(query.Context))
            {
                return ClosedSession<IReadOnlyList<DownloadRecord>>();
            }
            IReadOnlyList<StoredDownload> entries;
            if (query.Context.IsPrivate)
            {
                entries = PrivateCatalog(query.Context).ToArray();
            }
            else
            {
                var loaded = await LoadAsync(query.Context, cancellationToken).ConfigureAwait(false);
                if (!loaded.IsSuccess)
                {
                    return ControllerResult<IReadOnlyList<DownloadRecord>>.Failure(loaded.Error!);
                }
                entries = loaded.Value!.Entries;
            }

            var records = entries
                .Select(entry => ProjectCurrentRecord(query.Context, entry.Record))
                .OrderByDescending(record => record.StartedAtUtc)
                .Take(query.MaximumItems)
                .ToArray();
            return ControllerResult<IReadOnlyList<DownloadRecord>>.Success(records);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ControllerResult> CancelAsync(
        DownloadRecordIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (!ValidIntent(intent.Context, intent.DownloadId))
        {
            return Invalid();
        }

        IDownloadRuntimeControl? runtime = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsClosedPrivateSession(intent.Context)) return ClosedSession();
            if (_active.TryGetValue(intent.DownloadId, out var active) &&
                ContextCanAccess(intent.Context, active.Context) &&
                active.Record.State == DownloadLifecycleState.InProgress)
            {
                runtime = active.RuntimeControl;
            }
        }
        finally
        {
            _gate.Release();
        }

        return runtime is null
            ? ControllerResult.Failure(ControllerError.Create(
                ControllerErrorCode.NotFound,
                "error.downloads.active_not_found"))
            : await runtime.CancelAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ControllerResult> OpenFileAsync(
        DownloadRecordIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (!ValidIntent(intent.Context, intent.DownloadId))
        {
            return Invalid();
        }

        string? path = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsClosedPrivateSession(intent.Context)) return ClosedSession();
            var entry = await FindAsync(intent.Context, intent.DownloadId, cancellationToken)
                .ConfigureAwait(false);
            if (entry?.Record.State == DownloadLifecycleState.Completed)
            {
                path = entry.ResultFilePath;
            }
        }
        finally
        {
            _gate.Release();
        }

        if (path is null || !File.Exists(path))
        {
            return ControllerResult.Failure(ControllerError.Create(
                ControllerErrorCode.NotFound,
                "error.downloads.file_not_found"));
        }
        return _fileLauncher.Open(path);
    }

    public async ValueTask<ControllerResult<ClearDownloadRecordsReceipt>> ClearRecordsAsync(
        ClearDownloadRecordsIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (intent.Context is not { IsStructurallyValid: true } ||
            !intent.Confirmed ||
            intent.DownloadIds is null ||
            intent.DownloadIds.Count == 0 ||
            intent.DownloadIds.Any(id => id.IsEmpty || id.ProfileId != intent.Context.ProfileId))
        {
            return Invalid<ClearDownloadRecordsReceipt>();
        }

        var changed = false;
        var removed = 0;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsClosedPrivateSession(intent.Context))
            {
                return ClosedSession<ClearDownloadRecordsReceipt>();
            }
            if (intent.DownloadIds.Any(id =>
                    _active.TryGetValue(id, out var active) &&
                    ContextCanAccess(intent.Context, active.Context) &&
                    active.Record.State == DownloadLifecycleState.InProgress))
            {
                return ControllerResult<ClearDownloadRecordsReceipt>.Failure(ControllerError.Create(
                    ControllerErrorCode.Conflict,
                    "error.downloads.clear_in_progress"));
            }

            if (intent.Context.IsPrivate)
            {
                var entries = PrivateCatalog(intent.Context);
                removed = entries.RemoveAll(entry => intent.DownloadIds.Contains(entry.Record.Id));
                changed = removed > 0;
            }
            else
            {
                var loaded = await LoadAsync(intent.Context, cancellationToken).ConfigureAwait(false);
                if (!loaded.IsSuccess)
                {
                    return ControllerResult<ClearDownloadRecordsReceipt>.Failure(loaded.Error!);
                }
                var entries = loaded.Value!.Entries.ToList();
                removed = entries.RemoveAll(entry => intent.DownloadIds.Contains(entry.Record.Id));
                if (removed > 0)
                {
                    var write = await WriteAsync(
                        intent.Context,
                        loaded.Value.Revision,
                        entries,
                        cancellationToken).ConfigureAwait(false);
                    if (!write.IsSuccess)
                    {
                        return ControllerResult<ClearDownloadRecordsReceipt>.Failure(write.Error!);
                    }
                    changed = true;
                }
            }
            if (changed)
            {
                foreach (var id in intent.DownloadIds)
                {
                    if (_active.TryGetValue(id, out var active) &&
                        ContextCanAccess(intent.Context, active.Context) &&
                        active.Record.State != DownloadLifecycleState.InProgress)
                    {
                        _active.Remove(id);
                    }
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        if (changed) RaiseChanged();
        return ControllerResult<ClearDownloadRecordsReceipt>.Success(new(
            removed,
            DownloadedFilesDisposition.Preserved));
    }

    public async ValueTask<ControllerResult<DownloadTrackingRegistration>> BeginAsync(
        DownloadTrackingStart start,
        IDownloadRuntimeControl runtimeControl,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(runtimeControl);
        if (start.Context is not { IsStructurallyValid: true } ||
            !ValidSource(start.Source) ||
            !TryNormalizePath(start.ResultFilePath, out var fullPath) ||
            start.TotalBytes is < 0)
        {
            return Invalid<DownloadTrackingRegistration>();
        }

        var fileName = Path.GetFileName(fullPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return Invalid<DownloadTrackingRegistration>();
        }

        var id = new DownloadRecordId(start.Context.ProfileId, Guid.NewGuid());
        var record = new DownloadRecord(
            id,
            new Uri(start.Source.AbsoluteUri),
            fileName,
            start.TotalBytes,
            0,
            DownloadLifecycleState.InProgress,
            _clock.UtcNow,
            null);
        var stored = new StoredDownload(record, fullPath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsClosedPrivateSession(start.Context)) return ClosedSession<DownloadTrackingRegistration>();
            if (start.Context.IsPrivate)
            {
                var entries = PrivateCatalog(start.Context);
                TrimForInsert(entries);
                if (entries.Count >= MaximumRecords)
                {
                    return CapacityReached<DownloadTrackingRegistration>();
                }
                entries.Add(stored);
            }
            else
            {
                var loaded = await LoadAsync(start.Context, cancellationToken).ConfigureAwait(false);
                if (!loaded.IsSuccess)
                {
                    return ControllerResult<DownloadTrackingRegistration>.Failure(loaded.Error!);
                }
                var entries = loaded.Value!.Entries.ToList();
                TrimForInsert(entries);
                if (entries.Count >= MaximumRecords)
                {
                    return CapacityReached<DownloadTrackingRegistration>();
                }
                entries.Add(stored);
                var write = await WriteAsync(
                    start.Context,
                    loaded.Value.Revision,
                    entries,
                    cancellationToken).ConfigureAwait(false);
                if (!write.IsSuccess)
                {
                    return ControllerResult<DownloadTrackingRegistration>.Failure(write.Error!);
                }
            }

            _active.Add(id, new ActiveDownload(
                start.Context,
                record,
                runtimeControl,
                fullPath,
                DateTimeOffset.MinValue));
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged();
        return ControllerResult<DownloadTrackingRegistration>.Success(new(id));
    }

    public async ValueTask UpdateAsync(
        PrivacyContext context,
        DownloadRecordId downloadId,
        long receivedBytes,
        long? totalBytes,
        DownloadLifecycleState state,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsStructurallyValid ||
            downloadId.IsEmpty ||
            downloadId.ProfileId != context.ProfileId ||
            receivedBytes < 0 ||
            totalBytes is < 0 ||
            (totalBytes is { } total && receivedBytes > total) ||
            !Enum.IsDefined(state))
        {
            return;
        }

        var notify = false;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_active.TryGetValue(downloadId, out var active) ||
                !ContextCanAccess(context, active.Context) ||
                active.Record.State != DownloadLifecycleState.InProgress)
            {
                return;
            }

            var now = _clock.UtcNow;
            var terminal = state != DownloadLifecycleState.InProgress;
            var updated = active.Record with
            {
                TotalBytes = totalBytes ?? active.Record.TotalBytes,
                ReceivedBytes = receivedBytes,
                State = state,
                CompletedAtUtc = terminal ? now : null,
            };
            var stored = new StoredDownload(updated, active.ResultFilePath);
            var terminalStored = context.IsPrivate;
            if (context.IsPrivate)
            {
                ReplacePrivate(context, stored);
            }
            else if (terminal)
            {
                var loaded = await LoadAsync(context, cancellationToken).ConfigureAwait(false);
                if (loaded.IsSuccess)
                {
                    var entries = loaded.Value!.Entries.ToList();
                    var index = entries.FindIndex(entry => entry.Record.Id == downloadId);
                    if (index >= 0)
                    {
                        entries[index] = stored;
                        var write = await WriteAsync(
                            context,
                            loaded.Value.Revision,
                            entries,
                            cancellationToken).ConfigureAwait(false);
                        terminalStored = write.IsSuccess;
                    }
                }
            }

            if (terminal)
            {
                if (terminalStored)
                {
                    _active.Remove(downloadId);
                }
                else
                {
                    _active[downloadId] = active with { Record = updated };
                }
                notify = true;
            }
            else
            {
                var lastNotification = active.LastNotificationUtc;
                _active[downloadId] = active with
                {
                    Record = updated,
                    LastNotificationUtc = now - lastNotification >= ProgressNotificationInterval
                        ? now
                        : lastNotification,
                };
                notify = now - lastNotification >= ProgressNotificationInterval;
            }
        }
        finally
        {
            _gate.Release();
        }

        if (notify) RaiseChanged();
    }

    public async ValueTask<ControllerResult> EndPrivateSessionAsync(
        PrivacyContext context,
        CancellationToken cancellationToken = default)
    {
        if (context is not { IsStructurallyValid: true, IsPrivate: true }) return Invalid();

        List<IDownloadRuntimeControl> pending = [];
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var key = PrivateSessionKey.From(context);
            if (!_closedPrivateSessions.Add(key)) return ControllerResult.Success();

            // Revoke access and erase URL/name/path metadata before invoking runtime
            // callbacks. Late progress notifications can no longer recreate a row.
            _privateCatalogs.Remove(key);
            foreach (var entry in _active.Where(entry => ContextCanAccess(context, entry.Value.Context)).ToArray())
            {
                if (entry.Value.Record.State == DownloadLifecycleState.InProgress)
                    pending.Add(entry.Value.RuntimeControl);
                _active.Remove(entry.Key);
            }
        }
        finally
        {
            _gate.Release();
        }

        var cancelled = true;
        foreach (var runtime in pending)
        {
            try
            {
                // Teardown is committed; caller cancellation must not skip another
                // in-flight private transfer. Never call runtime code under _gate.
                var result = await runtime.CancelAsync(CancellationToken.None).ConfigureAwait(false);
                cancelled &= result.IsSuccess || result.Error?.Code == ControllerErrorCode.NotFound;
            }
            catch
            {
                cancelled = false;
            }
        }
        RaiseChanged();
        return cancelled
            ? ControllerResult.Success()
            : ControllerResult.Failure(ControllerError.Create(
                ControllerErrorCode.Unavailable, "error.downloads.private_cancel_failed"));
    }

    private DownloadRecord ProjectCurrentRecord(PrivacyContext context, DownloadRecord persisted)
    {
        if (_active.TryGetValue(persisted.Id, out var active) && ContextCanAccess(context, active.Context))
        {
            return active.Record;
        }
        return persisted.State == DownloadLifecycleState.InProgress
            ? persisted with { State = DownloadLifecycleState.Failed }
            : persisted;
    }

    private async ValueTask<StoredDownload?> FindAsync(
        PrivacyContext context,
        DownloadRecordId id,
        CancellationToken cancellationToken)
    {
        if (_active.TryGetValue(id, out var active) && ContextCanAccess(context, active.Context))
        {
            return new StoredDownload(active.Record, active.ResultFilePath);
        }
        if (context.IsPrivate)
        {
            return PrivateCatalog(context).SingleOrDefault(entry => entry.Record.Id == id);
        }
        var loaded = await LoadAsync(context, cancellationToken).ConfigureAwait(false);
        return loaded.IsSuccess
            ? loaded.Value!.Entries.SingleOrDefault(entry => entry.Record.Id == id)
            : null;
    }

    private List<StoredDownload> PrivateCatalog(PrivacyContext context)
    {
        var key = PrivateSessionKey.From(context);
        if (!_privateCatalogs.TryGetValue(key, out var entries))
        {
            entries = [];
            _privateCatalogs.Add(key, entries);
        }
        return entries;
    }

    private void ReplacePrivate(PrivacyContext context, StoredDownload updated)
    {
        var entries = PrivateCatalog(context);
        var index = entries.FindIndex(entry => entry.Record.Id == updated.Record.Id);
        if (index >= 0) entries[index] = updated;
    }

    private async ValueTask<ControllerResult<StoredCatalog>> LoadAsync(
        PrivacyContext context,
        CancellationToken cancellationToken)
    {
        var read = await _storage.ReadAsync(Address(context), cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            return read.Error?.Code == ControllerErrorCode.NotFound
                ? ControllerResult<StoredCatalog>.Success(new(null, []))
                : ControllerResult<StoredCatalog>.Failure(read.Error!);
        }
        try
        {
            var entries = JsonSerializer.Deserialize<StoredDownload[]>(read.Value!.Payload.Span);
            return entries is not null && ValidateCatalog(context.ProfileId, entries)
                ? ControllerResult<StoredCatalog>.Success(new(read.Value.Revision, entries))
                : Corrupt<StoredCatalog>();
        }
        catch (JsonException)
        {
            return Corrupt<StoredCatalog>();
        }
    }

    private async ValueTask<ControllerResult> WriteAsync(
        PrivacyContext context,
        ProfileStorageRevision? expectedRevision,
        IReadOnlyList<StoredDownload> entries,
        CancellationToken cancellationToken)
    {
        var request = ProfileStorageWriteRequest.Create(
            Address(context),
            JsonSerializer.SerializeToUtf8Bytes(entries),
            expectedRevision).Value!;
        var write = await _storage.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        return write.IsSuccess ? ControllerResult.Success() : ControllerResult.Failure(write.Error!);
    }

    private ProfileStorageAddress Address(PrivacyContext context) =>
        ProfileStorageAddress.Create(
            context,
            _namespace,
            ProfileStorageKey.Create(StorageKey).Value,
            ProfileStorageDurability.Persistent).Value!;

    private static bool ValidateCatalog(ProfileId profileId, IReadOnlyList<StoredDownload> entries)
    {
        if (entries.Count > MaximumRecords) return false;
        var ids = new HashSet<DownloadRecordId>();
        return entries.All(entry =>
            entry is not null &&
            !entry.Record.Id.IsEmpty &&
            entry.Record.Id.ProfileId == profileId &&
            ids.Add(entry.Record.Id) &&
            ValidSource(entry.Record.Source) &&
            !string.IsNullOrWhiteSpace(entry.Record.FileName) &&
            entry.Record.TotalBytes is not < 0 &&
            entry.Record.ReceivedBytes >= 0 &&
            (entry.Record.TotalBytes is not { } total || entry.Record.ReceivedBytes <= total) &&
            Enum.IsDefined(entry.Record.State) &&
            entry.Record.StartedAtUtc != default &&
            TryNormalizePath(entry.ResultFilePath, out _));
    }

    private static void TrimForInsert(List<StoredDownload> entries)
    {
        while (entries.Count >= MaximumRecords)
        {
            var removable = entries
                .Where(entry => entry.Record.State != DownloadLifecycleState.InProgress)
                .OrderBy(entry => entry.Record.StartedAtUtc)
                .FirstOrDefault();
            if (removable is null) break;
            entries.Remove(removable);
        }
    }

    private static bool ValidIntent(PrivacyContext context, DownloadRecordId id) =>
        context.IsStructurallyValid && !id.IsEmpty && id.ProfileId == context.ProfileId;

    private static bool ContextCanAccess(PrivacyContext requested, PrivacyContext owner) =>
        requested.ProfileId == owner.ProfileId &&
        requested.Mode == owner.Mode &&
        (!requested.IsPrivate || requested.SessionId == owner.SessionId);

    private static bool ValidSource(Uri source) =>
        source is { IsAbsoluteUri: true } &&
        source.Scheme is "http" or "https" &&
        string.IsNullOrEmpty(source.UserInfo) &&
        !string.IsNullOrWhiteSpace(source.IdnHost);

    private static bool TryNormalizePath(string? path, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32767) return false;
        try
        {
            fullPath = Path.GetFullPath(path);
            return Path.IsPathFullyQualified(fullPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static ControllerResult Invalid() => ControllerResult.Failure(ControllerError.Create(
        ControllerErrorCode.InvalidRequest,
        "error.downloads.invalid"));

    private static ControllerResult<T> Invalid<T>() where T : class =>
        ControllerResult<T>.Failure(ControllerError.Create(
            ControllerErrorCode.InvalidRequest,
            "error.downloads.invalid"));

    private static ControllerResult<T> Corrupt<T>() where T : class =>
        ControllerResult<T>.Failure(ControllerError.Create(
            ControllerErrorCode.IntegrityFailure,
            "error.downloads.storage_corrupt"));

    private static ControllerResult<T> CapacityReached<T>() where T : class =>
        ControllerResult<T>.Failure(ControllerError.Create(
            ControllerErrorCode.Conflict,
            "error.downloads.capacity_reached"));

    private bool IsClosedPrivateSession(PrivacyContext context) =>
        context.IsPrivate && _closedPrivateSessions.Contains(PrivateSessionKey.From(context));

    private static ControllerResult ClosedSession() => ControllerResult.Failure(ControllerError.Create(
        ControllerErrorCode.PolicyDenied, "error.downloads.private_session_closed"));

    private static ControllerResult<T> ClosedSession<T>() where T : class =>
        ControllerResult<T>.Failure(ControllerError.Create(
            ControllerErrorCode.PolicyDenied, "error.downloads.private_session_closed"));

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private sealed record StoredDownload(DownloadRecord Record, string ResultFilePath);

    private readonly record struct PrivateSessionKey(ProfileId ProfileId, BrowserSessionId SessionId)
    {
        public static PrivateSessionKey From(PrivacyContext context) => new(context.ProfileId, context.SessionId);
    }

    private sealed record StoredCatalog(
        ProfileStorageRevision? Revision,
        IReadOnlyList<StoredDownload> Entries);

    private sealed record ActiveDownload(
        PrivacyContext Context,
        DownloadRecord Record,
        IDownloadRuntimeControl RuntimeControl,
        string ResultFilePath,
        DateTimeOffset LastNotificationUtc);
}
