using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Sync;
using OrbitNavigator.Sync.Integration;

namespace OrbitNavigator.Sync.State;

public sealed record LocalSyncChangeReceipt(
    LocalChangeCursor Cursor,
    LocalSyncChange Change);

public interface ILocalSyncChangeRecorder
{
    ValueTask<ControllerResult<LocalSyncChangeReceipt>> RecordAsync(
        BrowsingContext browsing,
        SyncOperationId operationId,
        SyncRecordKind recordKind,
        SyncDataCategory category,
        SyncEntityId entityId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Durable normal-profile journal containing only record kind, allowlisted category,
/// and opaque entity identity. Decrypted page addresses and titles are never stored
/// in this queue; projection happens only immediately before encryption.
/// </summary>
public sealed class ProfileStorageLocalSyncChangeCatalog :
    ILocalSyncChangeCatalog,
    ILocalSyncChangeRecorder
{
    public const int MaximumJournalEntries = 20_000;

    private const uint Magic = 0x4A434E4F; // ONCJ
    private const int FormatVersion = 1;
    private const int HeaderSizeBytes = 4 + 4 + 16 + 8 + 4;
    private const int EntrySizeBytes = 8 + 1 + 1 + 16;
    private const int DigestSizeBytes = 32;
    private const int MaximumPayloadBytes =
        HeaderSizeBytes + (MaximumJournalEntries * EntrySizeBytes) + DigestSizeBytes;

    private static readonly ProfileStorageNamespace StorageNamespace =
        ProfileStorageNamespace.Create("sync.local-changes").Value!;
    private static readonly ProfileStorageKey StorageKey =
        ProfileStorageKey.Create("journal").Value!;
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> ProfileGates = new();

    private readonly IProfileStorage _storage;

    public ProfileStorageLocalSyncChangeCatalog(IProfileStorage storage)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    }

    public ValueTask<ControllerResult<LocalSyncChangeReceipt>> RecordAsync(
        BrowsingContext browsing,
        SyncOperationId operationId,
        SyncRecordKind recordKind,
        SyncDataCategory category,
        SyncEntityId entityId,
        CancellationToken cancellationToken = default) =>
        SyncOperationGate.ExecuteAsync(
            browsing,
            operationId,
            context => RecordAuthorizedAsync(
                context,
                recordKind,
                category,
                entityId,
                cancellationToken));

    public async ValueTask<ControllerResult<LocalSyncChangePage>> ListPendingAsync(
        SyncOperationContext context,
        LocalChangeCursor? after,
        int maximumItems,
        CancellationToken cancellationToken)
    {
        if (context is null || context.Browsing.Privacy.IsPrivate)
            return PolicyDenied<LocalSyncChangePage>();
        if (maximumItems is < 1 or > OptionalSyncCoordinator.MaximumItemsPerPage ||
            !TryParseCursor(after, out var afterSequence))
        {
            return Invalid<LocalSyncChangePage>();
        }

        var gate = ProfileGates.GetOrAdd(
            context.Browsing.Privacy.ProfileId.Value,
            static _ => new SemaphoreSlim(1, 1));
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled<LocalSyncChangePage>();
        }

        try
        {
            var loaded = await LoadAsync(context, cancellationToken).ConfigureAwait(false);
            if (!loaded.IsSuccess)
                return ControllerResult<LocalSyncChangePage>.Failure(loaded.Error!);
            var journal = loaded.Value!;
            if (afterSequence >= journal.NextSequence)
                return Invalid<LocalSyncChangePage>();

            var selected = journal.Entries
                .Where(entry => entry.Sequence > afterSequence)
                .Take(maximumItems)
                .ToArray();
            var last = selected.Length == 0 ? afterSequence : selected[^1].Sequence;
            var hasMore = journal.Entries.Any(entry => entry.Sequence > last);
            var nextCursor = selected.Length == 0 ? after : Cursor(last);
            return ControllerResult<LocalSyncChangePage>.Success(new(
                selected.Select(entry => entry.Change).ToArray(),
                nextCursor,
                hasMore));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled<LocalSyncChangePage>();
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask<ControllerResult<LocalSyncChangeReceipt>> RecordAuthorizedAsync(
        SyncOperationContext context,
        SyncRecordKind recordKind,
        SyncDataCategory category,
        SyncEntityId entityId,
        CancellationToken cancellationToken)
    {
        if (recordKind is not (SyncRecordKind.Upsert or SyncRecordKind.Tombstone) ||
            category is not (SyncDataCategory.History or SyncDataCategory.OpenTabs) ||
            !entityId.IsDefined)
        {
            return Invalid<LocalSyncChangeReceipt>();
        }

        var gate = ProfileGates.GetOrAdd(
            context.Browsing.Privacy.ProfileId.Value,
            static _ => new SemaphoreSlim(1, 1));
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled<LocalSyncChangeReceipt>();
        }

        try
        {
            var loaded = await LoadAsync(context, cancellationToken).ConfigureAwait(false);
            if (!loaded.IsSuccess)
                return ControllerResult<LocalSyncChangeReceipt>.Failure(loaded.Error!);
            var journal = loaded.Value!;
            if (journal.Entries.Count >= MaximumJournalEntries || journal.NextSequence == long.MaxValue)
                return Capacity<LocalSyncChangeReceipt>();

            var change = new LocalSyncChange(recordKind, category, entityId);
            var entry = new JournalEntry(journal.NextSequence, change);
            var next = new Journal(
                journal.StorageRevision,
                checked(journal.NextSequence + 1),
                [.. journal.Entries, entry]);
            var payload = Encode(context.Browsing.Privacy.ProfileId, next);
            if (!payload.IsSuccess)
                return ControllerResult<LocalSyncChangeReceipt>.Failure(payload.Error!);

            var address = Address(context);
            if (!address.IsSuccess)
                return ControllerResult<LocalSyncChangeReceipt>.Failure(address.Error!);
            var request = ProfileStorageWriteRequest.Create(
                address.Value,
                payload.Value!,
                journal.StorageRevision);
            if (!request.IsSuccess)
                return ControllerResult<LocalSyncChangeReceipt>.Failure(request.Error!);
            var written = await _storage.WriteAsync(request.Value!, cancellationToken).ConfigureAwait(false);
            if (!written.IsSuccess)
                return ControllerResult<LocalSyncChangeReceipt>.Failure(written.Error!);
            if (written.Value!.Revision.IsEmpty)
                return Integrity<LocalSyncChangeReceipt>();

            return ControllerResult<LocalSyncChangeReceipt>.Success(new(Cursor(entry.Sequence), change));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled<LocalSyncChangeReceipt>();
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask<ControllerResult<Journal>> LoadAsync(
        SyncOperationContext context,
        CancellationToken cancellationToken)
    {
        var address = Address(context);
        if (!address.IsSuccess)
            return ControllerResult<Journal>.Failure(address.Error!);
        var read = await _storage.ReadAsync(address.Value!, cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            return read.Error!.Code == ControllerErrorCode.NotFound
                ? ControllerResult<Journal>.Success(new(null, 1, []))
                : ControllerResult<Journal>.Failure(read.Error);
        }

        return Decode(
            read.Value!.Payload.Span,
            context.Browsing.Privacy.ProfileId,
            read.Value.Revision);
    }

    private static ControllerResult<byte[]> Encode(ProfileId profileId, Journal journal)
    {
        if (profileId.IsEmpty || journal.NextSequence < 1 ||
            journal.Entries.Count > MaximumJournalEntries)
        {
            return Invalid<byte[]>();
        }

        var payload = new byte[
            HeaderSizeBytes + (journal.Entries.Count * EntrySizeBytes) + DigestSizeBytes];
        var offset = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset, 4), Magic);
        offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset, 4), FormatVersion);
        offset += 4;
        profileId.Value.TryWriteBytes(payload.AsSpan(offset, 16));
        offset += 16;
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(offset, 8), journal.NextSequence);
        offset += 8;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset, 4), journal.Entries.Count);
        offset += 4;
        long previous = 0;
        foreach (var entry in journal.Entries)
        {
            if (!ValidEntry(entry) || entry.Sequence <= previous || entry.Sequence >= journal.NextSequence)
                return Invalid<byte[]>();
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(offset, 8), entry.Sequence);
            offset += 8;
            payload[offset++] = (byte)entry.Change.RecordKind;
            payload[offset++] = (byte)entry.Change.Category;
            entry.Change.EntityId.Value.TryWriteBytes(payload.AsSpan(offset, 16));
            offset += 16;
            previous = entry.Sequence;
        }

        SHA256.HashData(payload.AsSpan(0, offset), payload.AsSpan(offset, DigestSizeBytes));
        return ControllerResult<byte[]>.Success(payload);
    }

    private static ControllerResult<Journal> Decode(
        ReadOnlySpan<byte> payload,
        ProfileId expectedProfileId,
        ProfileStorageRevision storageRevision)
    {
        if (payload.Length is < HeaderSizeBytes + DigestSizeBytes or > MaximumPayloadBytes)
            return Integrity<Journal>();
        var contentLength = payload.Length - DigestSizeBytes;
        Span<byte> digest = stackalloc byte[DigestSizeBytes];
        SHA256.HashData(payload[..contentLength], digest);
        if (!CryptographicOperations.FixedTimeEquals(digest, payload[contentLength..]))
            return Integrity<Journal>();

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(payload[..4]);
        var version = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4, 4));
        var profileId = new ProfileId(new Guid(payload.Slice(8, 16)));
        var nextSequence = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(24, 8));
        var count = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(32, 4));
        if (magic != Magic || version != FormatVersion || profileId != expectedProfileId ||
            nextSequence < 1 || count is < 0 or > MaximumJournalEntries ||
            HeaderSizeBytes + (count * EntrySizeBytes) != contentLength)
        {
            return Integrity<Journal>();
        }

        var entries = new JournalEntry[count];
        var offset = HeaderSizeBytes;
        long previous = 0;
        for (var index = 0; index < count; index++)
        {
            var sequence = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(offset, 8));
            offset += 8;
            var kind = (SyncRecordKind)payload[offset++];
            var category = (SyncDataCategory)payload[offset++];
            var entity = new SyncEntityId(new Guid(payload.Slice(offset, 16)));
            offset += 16;
            var entry = new JournalEntry(sequence, new LocalSyncChange(kind, category, entity));
            if (!ValidEntry(entry) || sequence <= previous || sequence >= nextSequence)
                return Integrity<Journal>();
            entries[index] = entry;
            previous = sequence;
        }

        return ControllerResult<Journal>.Success(new(storageRevision, nextSequence, entries));
    }

    private static bool ValidEntry(JournalEntry entry) =>
        entry.Sequence >= 1 &&
        entry.Change is not null &&
        entry.Change.RecordKind is SyncRecordKind.Upsert or SyncRecordKind.Tombstone &&
        entry.Change.Category is SyncDataCategory.History or SyncDataCategory.OpenTabs &&
        entry.Change.EntityId.IsDefined;

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

    private static bool TryParseCursor(LocalChangeCursor? cursor, out long sequence)
    {
        if (cursor is null)
        {
            sequence = 0;
            return true;
        }

        return long.TryParse(
            cursor.Value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out sequence) && sequence >= 0;
    }

    private static LocalChangeCursor Cursor(long sequence) =>
        new(sequence.ToString(CultureInfo.InvariantCulture));

    private static ControllerResult<T> Invalid<T>() where T : class =>
        ControllerResult<T>.Failure(ControllerError.Create(
            ControllerErrorCode.InvalidRequest,
            "sync.local-changes.invalid"));

    private static ControllerResult<T> PolicyDenied<T>() where T : class =>
        ControllerResult<T>.Failure(ControllerError.Create(
            ControllerErrorCode.PolicyDenied,
            "sync.private-mode.policy-denied"));

    private static ControllerResult<T> Integrity<T>() where T : class =>
        ControllerResult<T>.Failure(ControllerError.Create(
            ControllerErrorCode.IntegrityFailure,
            "sync.local-changes.corrupt"));

    private static ControllerResult<T> Capacity<T>() where T : class =>
        ControllerResult<T>.Failure(ControllerError.Create(
            ControllerErrorCode.Unavailable,
            "sync.local-changes.capacity-reached"));

    private static ControllerResult<T> Cancelled<T>() where T : class =>
        ControllerResult<T>.Failure(ControllerError.Create(
            ControllerErrorCode.Cancelled,
            "sync.local-changes.cancelled",
            isRetryable: true));

    private sealed record Journal(
        ProfileStorageRevision? StorageRevision,
        long NextSequence,
        IReadOnlyList<JournalEntry> Entries);

    private sealed record JournalEntry(long Sequence, LocalSyncChange Change);
}
