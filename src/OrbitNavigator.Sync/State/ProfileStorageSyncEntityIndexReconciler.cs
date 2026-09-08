using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Sync;

namespace OrbitNavigator.Sync.State;

public sealed record LocalSyncEntityVersion(
    SyncDataCategory Category,
    SyncEntityId EntityId,
    long Revision);

public sealed record SyncEntityReconciliationReceipt(
    int UpsertCount,
    int TombstoneCount,
    LocalChangeCursor? LastJournalCursor);

/// <summary>
/// Reconciles an authoritative local entity inventory into the durable change
/// journal. Journal entries are appended before the comparison index advances,
/// so interruption can cause a safe duplicate but cannot silently lose a change.
/// </summary>
public sealed class ProfileStorageSyncEntityIndexReconciler
{
    public const int MaximumTrackedEntities = 10_000;

    private const uint Magic = 0x58494E4F; // ONIX
    private const int FormatVersion = 1;
    private const int HeaderSizeBytes = 4 + 4 + 16 + 4;
    private const int EntrySizeBytes = 1 + 16 + 8;
    private const int DigestSizeBytes = 32;
    private const int MaximumPayloadBytes =
        HeaderSizeBytes + (MaximumTrackedEntities * EntrySizeBytes) + DigestSizeBytes;

    private static readonly ProfileStorageNamespace StorageNamespace =
        ProfileStorageNamespace.Create("sync.entity-index").Value!;
    private static readonly ProfileStorageKey StorageKey =
        ProfileStorageKey.Create("current").Value!;
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> ProfileGates = new();

    private readonly IProfileStorage _storage;
    private readonly ILocalSyncChangeRecorder _journal;

    public ProfileStorageSyncEntityIndexReconciler(
        IProfileStorage storage,
        ILocalSyncChangeRecorder journal)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    }

    public ValueTask<ControllerResult<SyncEntityReconciliationReceipt>> ReconcileAsync(
        BrowsingContext browsing,
        SyncOperationId operationId,
        IReadOnlyList<LocalSyncEntityVersion> current,
        CancellationToken cancellationToken = default) =>
        SyncOperationGate.ExecuteAsync(
            browsing,
            operationId,
            context => ReconcileAuthorizedAsync(
                context,
                operationId,
                current,
                cancellationToken));

    private async ValueTask<ControllerResult<SyncEntityReconciliationReceipt>> ReconcileAuthorizedAsync(
        SyncOperationContext context,
        SyncOperationId operationId,
        IReadOnlyList<LocalSyncEntityVersion> current,
        CancellationToken cancellationToken)
    {
        if (!TryCreateMap(current, out var currentMap))
            return Invalid<SyncEntityReconciliationReceipt>();

        var profileId = context.Browsing.Privacy.ProfileId;
        var gate = ProfileGates.GetOrAdd(profileId.Value, static _ => new SemaphoreSlim(1, 1));
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled<SyncEntityReconciliationReceipt>();
        }

        try
        {
            var loaded = await LoadAsync(context, cancellationToken).ConfigureAwait(false);
            if (!loaded.IsSuccess)
                return ControllerResult<SyncEntityReconciliationReceipt>.Failure(loaded.Error!);
            var previous = loaded.Value!;

            var tombstones = previous.Entities.Keys
                .Where(key => !currentMap.ContainsKey(key))
                .OrderBy(key => key.Category)
                .ThenBy(key => key.EntityId.Value)
                .ToArray();
            var upserts = currentMap
                .Where(pair => !previous.Entities.TryGetValue(pair.Key, out var prior) ||
                    prior != pair.Value)
                .OrderBy(pair => pair.Key.Category)
                .ThenBy(pair => pair.Key.EntityId.Value)
                .Select(pair => pair.Key)
                .ToArray();

            LocalChangeCursor? lastCursor = null;
            foreach (var key in tombstones)
            {
                var recorded = await _journal.RecordAsync(
                    context.Browsing,
                    operationId,
                    SyncRecordKind.Tombstone,
                    key.Category,
                    key.EntityId,
                    cancellationToken).ConfigureAwait(false);
                if (!recorded.IsSuccess)
                    return ControllerResult<SyncEntityReconciliationReceipt>.Failure(recorded.Error!);
                lastCursor = recorded.Value!.Cursor;
            }
            foreach (var key in upserts)
            {
                var recorded = await _journal.RecordAsync(
                    context.Browsing,
                    operationId,
                    SyncRecordKind.Upsert,
                    key.Category,
                    key.EntityId,
                    cancellationToken).ConfigureAwait(false);
                if (!recorded.IsSuccess)
                    return ControllerResult<SyncEntityReconciliationReceipt>.Failure(recorded.Error!);
                lastCursor = recorded.Value!.Cursor;
            }

            if (tombstones.Length != 0 || upserts.Length != 0 || previous.StorageRevision is null)
            {
                var saved = await SaveAsync(
                    context,
                    previous.StorageRevision,
                    currentMap,
                    cancellationToken).ConfigureAwait(false);
                if (!saved.IsSuccess)
                    return ControllerResult<SyncEntityReconciliationReceipt>.Failure(saved.Error!);
            }

            return ControllerResult<SyncEntityReconciliationReceipt>.Success(new(
                upserts.Length,
                tombstones.Length,
                lastCursor));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled<SyncEntityReconciliationReceipt>();
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask<ControllerResult<StoredIndex>> LoadAsync(
        SyncOperationContext context,
        CancellationToken cancellationToken)
    {
        var address = Address(context);
        if (!address.IsSuccess)
            return ControllerResult<StoredIndex>.Failure(address.Error!);
        var read = await _storage.ReadAsync(address.Value!, cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            return read.Error!.Code == ControllerErrorCode.NotFound
                ? ControllerResult<StoredIndex>.Success(new(
                    null,
                    new Dictionary<EntityKey, long>()))
                : ControllerResult<StoredIndex>.Failure(read.Error);
        }
        return Decode(
            read.Value!.Payload.Span,
            context.Browsing.Privacy.ProfileId,
            read.Value.Revision);
    }

    private async ValueTask<ControllerResult<StoredIndex>> SaveAsync(
        SyncOperationContext context,
        ProfileStorageRevision? expectedRevision,
        IReadOnlyDictionary<EntityKey, long> entities,
        CancellationToken cancellationToken)
    {
        var address = Address(context);
        if (!address.IsSuccess)
            return ControllerResult<StoredIndex>.Failure(address.Error!);
        var payload = Encode(context.Browsing.Privacy.ProfileId, entities);
        if (!payload.IsSuccess)
            return ControllerResult<StoredIndex>.Failure(payload.Error!);
        var request = ProfileStorageWriteRequest.Create(
            address.Value,
            payload.Value!,
            expectedRevision);
        if (!request.IsSuccess)
            return ControllerResult<StoredIndex>.Failure(request.Error!);
        var written = await _storage.WriteAsync(request.Value!, cancellationToken).ConfigureAwait(false);
        return written.IsSuccess && !written.Value!.Revision.IsEmpty
            ? ControllerResult<StoredIndex>.Success(new(written.Value.Revision, entities))
            : written.IsSuccess
                ? Integrity<StoredIndex>()
                : ControllerResult<StoredIndex>.Failure(written.Error!);
    }

    private static bool TryCreateMap(
        IReadOnlyList<LocalSyncEntityVersion>? current,
        out IReadOnlyDictionary<EntityKey, long> result)
    {
        var map = new Dictionary<EntityKey, long>();
        if (current is null || current.Count > MaximumTrackedEntities)
        {
            result = map;
            return false;
        }
        foreach (var entity in current)
        {
            if (entity is null ||
                entity.Category is not (SyncDataCategory.History or SyncDataCategory.OpenTabs) ||
                !entity.EntityId.IsDefined || entity.Revision < 0 ||
                !map.TryAdd(new(entity.Category, entity.EntityId), entity.Revision))
            {
                result = map;
                return false;
            }
        }
        result = map;
        return true;
    }

    private static ControllerResult<byte[]> Encode(
        ProfileId profileId,
        IReadOnlyDictionary<EntityKey, long> entities)
    {
        if (profileId.IsEmpty || entities.Count > MaximumTrackedEntities)
            return Invalid<byte[]>();
        var ordered = entities.OrderBy(pair => pair.Key.Category)
            .ThenBy(pair => pair.Key.EntityId.Value)
            .ToArray();
        var payload = new byte[HeaderSizeBytes + (ordered.Length * EntrySizeBytes) + DigestSizeBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), Magic);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4, 4), FormatVersion);
        profileId.Value.TryWriteBytes(payload.AsSpan(8, 16));
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(24, 4), ordered.Length);
        var offset = HeaderSizeBytes;
        foreach (var pair in ordered)
        {
            payload[offset++] = (byte)pair.Key.Category;
            pair.Key.EntityId.Value.TryWriteBytes(payload.AsSpan(offset, 16));
            offset += 16;
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(offset, 8), pair.Value);
            offset += 8;
        }
        SHA256.HashData(payload.AsSpan(0, offset), payload.AsSpan(offset, DigestSizeBytes));
        return ControllerResult<byte[]>.Success(payload);
    }

    private static ControllerResult<StoredIndex> Decode(
        ReadOnlySpan<byte> payload,
        ProfileId expectedProfileId,
        ProfileStorageRevision revision)
    {
        if (payload.Length is < HeaderSizeBytes + DigestSizeBytes or > MaximumPayloadBytes)
            return Integrity<StoredIndex>();
        var contentLength = payload.Length - DigestSizeBytes;
        Span<byte> digest = stackalloc byte[DigestSizeBytes];
        SHA256.HashData(payload[..contentLength], digest);
        if (!CryptographicOperations.FixedTimeEquals(digest, payload[contentLength..]))
            return Integrity<StoredIndex>();
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(payload[..4]);
        var version = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4, 4));
        var profileId = new ProfileId(new Guid(payload.Slice(8, 16)));
        var count = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(24, 4));
        if (magic != Magic || version != FormatVersion || profileId != expectedProfileId ||
            count is < 0 or > MaximumTrackedEntities ||
            HeaderSizeBytes + (count * EntrySizeBytes) != contentLength)
        {
            return Integrity<StoredIndex>();
        }

        var result = new Dictionary<EntityKey, long>();
        var offset = HeaderSizeBytes;
        for (var index = 0; index < count; index++)
        {
            var category = (SyncDataCategory)payload[offset++];
            var entityId = new SyncEntityId(new Guid(payload.Slice(offset, 16)));
            offset += 16;
            var entityRevision = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(offset, 8));
            offset += 8;
            var key = new EntityKey(category, entityId);
            if (category is not (SyncDataCategory.History or SyncDataCategory.OpenTabs) ||
                !entityId.IsDefined || entityRevision < 0 || !result.TryAdd(key, entityRevision))
            {
                return Integrity<StoredIndex>();
            }
        }
        return ControllerResult<StoredIndex>.Success(new(revision, result));
    }

    private static ControllerResult<ProfileStorageAddress> Address(SyncOperationContext context)
    {
        var normal = NormalProfileOperationGuard.RequireNormal(context.Browsing);
        return normal.IsSuccess
            ? ProfileStorageAddress.Create(
                context.Browsing.Privacy,
                StorageNamespace,
                StorageKey,
                ProfileStorageDurability.Persistent)
            : ControllerResult<ProfileStorageAddress>.Failure(normal.Error!);
    }

    private static ControllerResult<T> Invalid<T>() where T : class =>
        ControllerResult<T>.Failure(ControllerError.Create(
            ControllerErrorCode.InvalidRequest,
            "sync.entity-index.invalid"));

    private static ControllerResult<T> Integrity<T>() where T : class =>
        ControllerResult<T>.Failure(ControllerError.Create(
            ControllerErrorCode.IntegrityFailure,
            "sync.entity-index.corrupt"));

    private static ControllerResult<T> Cancelled<T>() where T : class =>
        ControllerResult<T>.Failure(ControllerError.Create(
            ControllerErrorCode.Cancelled,
            "sync.entity-index.cancelled",
            isRetryable: true));

    private readonly record struct EntityKey(
        SyncDataCategory Category,
        SyncEntityId EntityId);

    private sealed record StoredIndex(
        ProfileStorageRevision? StorageRevision,
        IReadOnlyDictionary<EntityKey, long> Entities);
}
