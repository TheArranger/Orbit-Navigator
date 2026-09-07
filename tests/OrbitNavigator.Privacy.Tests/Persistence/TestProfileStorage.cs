using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;

namespace OrbitNavigator.Privacy.Tests.Persistence;

internal sealed class TestProfileStorage : IProfileStorage
{
    private readonly object _gate = new();
    private readonly Dictionary<StorageKey, ProfileStorageEntry> _entries = [];
    private StorageKey? _lastReadKey;

    public int ReadCount { get; private set; }

    public int WriteCount { get; private set; }

    public ControllerError? NextWriteError { get; set; }

    public ValueTask<ControllerResult<ProfileStorageEntry>> ReadAsync(
        ProfileStorageAddress address,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(address);
        lock (_gate)
        {
            ReadCount++;
            var key = Key(address);
            _lastReadKey = key;
            return ValueTask.FromResult(
                _entries.TryGetValue(key, out var entry)
                    ? ControllerResult<ProfileStorageEntry>.Success(entry)
                    : ControllerResult<ProfileStorageEntry>.Failure(
                        ControllerError.Create(
                            ControllerErrorCode.NotFound,
                            "error.test.not_found")));
        }
    }

    public ValueTask<ControllerResult<ProfileStorageWriteReceipt>> WriteAsync(
        ProfileStorageWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            WriteCount++;
            if (NextWriteError is { } failure)
            {
                NextWriteError = null;
                return ValueTask.FromResult(
                    ControllerResult<ProfileStorageWriteReceipt>.Failure(failure));
            }

            var key = Key(request.Address);
            _entries.TryGetValue(key, out var current);
            if (request.ExpectedRevision != current?.Revision)
            {
                return ValueTask.FromResult(
                    ControllerResult<ProfileStorageWriteReceipt>.Failure(
                        ControllerError.Create(
                            ControllerErrorCode.Conflict,
                            "error.test.revision_conflict")));
            }

            var revision = new ProfileStorageRevision(Guid.NewGuid());
            var entry = ProfileStorageEntry.Create(revision, request.Payload.Span);
            if (!entry.IsSuccess)
            {
                return ValueTask.FromResult(
                    ControllerResult<ProfileStorageWriteReceipt>.Failure(entry.Error!));
            }

            _entries[key] = entry.Value!;
            return ValueTask.FromResult(
                ControllerResult<ProfileStorageWriteReceipt>.Success(
                    new ProfileStorageWriteReceipt(revision)));
        }
    }

    public ValueTask<ControllerResult> DeleteAsync(
        ProfileStorageAddress address,
        ProfileStorageRevision? expectedRevision = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(address);
        lock (_gate)
        {
            var key = Key(address);
            _entries.TryGetValue(key, out var current);
            if (expectedRevision != current?.Revision)
            {
                return ValueTask.FromResult(ControllerResult.Failure(
                    ControllerError.Create(
                        ControllerErrorCode.Conflict,
                        "error.test.revision_conflict")));
            }

            _entries.Remove(key);
            return ValueTask.FromResult(ControllerResult.Success());
        }
    }

    public void ReplaceLastReadPayload(ReadOnlySpan<byte> payload)
    {
        lock (_gate)
        {
            var key = _lastReadKey ?? throw new InvalidOperationException(
                "No storage address has been read.");
            var entry = ProfileStorageEntry.Create(
                new ProfileStorageRevision(Guid.NewGuid()),
                payload);
            _entries[key] = entry.Value!;
        }
    }

    public void BumpLastReadRevision()
    {
        lock (_gate)
        {
            var key = _lastReadKey ?? throw new InvalidOperationException(
                "No storage address has been read.");
            var current = _entries[key];
            _entries[key] = ProfileStorageEntry.Create(
                new ProfileStorageRevision(Guid.NewGuid()),
                current.Payload.Span).Value!;
        }
    }

    public byte[] GetLastReadPayload()
    {
        lock (_gate)
        {
            var key = _lastReadKey ?? throw new InvalidOperationException(
                "No storage address has been read.");
            return _entries[key].Payload.ToArray();
        }
    }

    private static StorageKey Key(ProfileStorageAddress address) =>
        new(
            address.Context.ProfileId,
            address.Namespace.Value,
            address.Key.Value,
            address.Durability,
            address.Durability == ProfileStorageDurability.Session
                ? address.Context.SessionId
                : null);

    private readonly record struct StorageKey(
        ProfileId ProfileId,
        string Namespace,
        string Key,
        ProfileStorageDurability Durability,
        BrowserSessionId? SessionId);
}
