using System.Buffers.Binary;
using System.Security.Cryptography;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Sync;

namespace OrbitNavigator.Sync.Deletion;

public sealed record DeletionJournalCheckpoint(
    ProfileId ProfileId,
    SyncOperationId OperationId,
    int FormatVersion,
    ReadOnlyMemory<byte> SerializedState,
    ProfileStorageRevision? Revision);

public sealed record DeletionJournalLookup(
    bool Found,
    DeletionJournalCheckpoint? Checkpoint);

public interface IDeletionOperationJournal
{
    ValueTask<ControllerResult<DeletionJournalLookup>> LoadAsync(
        SyncOperationContext context,
        SyncOperationId operationId,
        CancellationToken cancellationToken);

    ValueTask<ControllerResult<DeletionJournalCheckpoint>> WriteAsync(
        SyncOperationContext context,
        DeletionJournalCheckpoint checkpoint,
        CancellationToken cancellationToken);
}

/// <summary>
/// Persistent deletion journal over profile-isolated storage. The stored frame is
/// versioned, bounded, checksummed, profile/operation bound, and updated using the
/// storage revision as a compare-and-swap token.
/// </summary>
public sealed class ProfileStorageDeletionOperationJournal : IDeletionOperationJournal
{
    private const uint FrameMagic = 0x4A444E4F; // ONDJ in little-endian storage.
    private const int FrameVersion = 1;
    private const int DigestSizeBytes = 32;
    private const int FixedFrameBytes = 4 + 4 + 16 + 16 + 4 + 4 + DigestSizeBytes;

    private static readonly ProfileStorageNamespace JournalNamespace = CreateNamespace();

    private readonly IProfileStorage _storage;

    public ProfileStorageDeletionOperationJournal(IProfileStorage storage)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    }

    public async ValueTask<ControllerResult<DeletionJournalLookup>> LoadAsync(
        SyncOperationContext context,
        SyncOperationId operationId,
        CancellationToken cancellationToken)
    {
        var address = CreateAddress(context, operationId);
        if (!address.IsSuccess)
            return ControllerResult<DeletionJournalLookup>.Failure(address.Error!);

        var read = await _storage.ReadAsync(address.Value!, cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            return read.Error!.Code == ControllerErrorCode.NotFound
                ? ControllerResult<DeletionJournalLookup>.Success(new DeletionJournalLookup(false, null))
                : ControllerResult<DeletionJournalLookup>.Failure(read.Error);
        }

        var parsed = ParseFrame(
            context.Browsing.Privacy.ProfileId,
            operationId,
            read.Value!.Payload.Span,
            read.Value.Revision);
        return parsed.IsSuccess
            ? ControllerResult<DeletionJournalLookup>.Success(
                new DeletionJournalLookup(true, parsed.Value))
            : ControllerResult<DeletionJournalLookup>.Failure(parsed.Error!);
    }

    public async ValueTask<ControllerResult<DeletionJournalCheckpoint>> WriteAsync(
        SyncOperationContext context,
        DeletionJournalCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        if (checkpoint is null ||
            checkpoint.ProfileId != context?.Browsing.Privacy.ProfileId ||
            checkpoint.OperationId != context.OperationId ||
            checkpoint.FormatVersion != DeletionOperationCheckpointCodec.CurrentFormatVersion)
        {
            return ControllerResult<DeletionJournalCheckpoint>.Failure(InvalidJournalRequest());
        }

        var normal = NormalProfileOperationGuard.RequireNormal(context.Browsing);
        if (!normal.IsSuccess)
            return ControllerResult<DeletionJournalCheckpoint>.Failure(normal.Error!);

        var parsed = DeletionOperationCheckpointCodec.Deserialize(checkpoint.SerializedState.Span);
        if (!parsed.IsSuccess ||
            parsed.Value!.ProfileId != checkpoint.ProfileId ||
            parsed.Value.OperationId != checkpoint.OperationId)
        {
            return ControllerResult<DeletionJournalCheckpoint>.Failure(InvalidJournalPayload());
        }

        var address = CreateAddress(context, checkpoint.OperationId);
        if (!address.IsSuccess)
            return ControllerResult<DeletionJournalCheckpoint>.Failure(address.Error!);

        var frame = CreateFrame(checkpoint);
        if (!frame.IsSuccess)
            return ControllerResult<DeletionJournalCheckpoint>.Failure(frame.Error!);
        var request = ProfileStorageWriteRequest.Create(
            address.Value,
            frame.Value!,
            checkpoint.Revision);
        if (!request.IsSuccess)
            return ControllerResult<DeletionJournalCheckpoint>.Failure(request.Error!);

        var written = await _storage.WriteAsync(request.Value!, cancellationToken).ConfigureAwait(false);
        return written.IsSuccess
            ? ControllerResult<DeletionJournalCheckpoint>.Success(checkpoint with
            {
                SerializedState = checkpoint.SerializedState.ToArray(),
                Revision = written.Value!.Revision,
            })
            : ControllerResult<DeletionJournalCheckpoint>.Failure(written.Error!);
    }

    private static ControllerResult<byte[]> CreateFrame(DeletionJournalCheckpoint checkpoint)
    {
        if (checkpoint.SerializedState.Length is <= 0 or > DeletionOperationCheckpointCodec.MaximumPayloadBytes)
            return ControllerResult<byte[]>.Failure(InvalidJournalPayload());

        var frame = new byte[FixedFrameBytes + checkpoint.SerializedState.Length];
        var offset = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(offset, 4), FrameMagic);
        offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(offset, 4), FrameVersion);
        offset += 4;
        checkpoint.ProfileId.Value.TryWriteBytes(frame.AsSpan(offset, 16));
        offset += 16;
        checkpoint.OperationId.Value.TryWriteBytes(frame.AsSpan(offset, 16));
        offset += 16;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(offset, 4), checkpoint.FormatVersion);
        offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(offset, 4), checkpoint.SerializedState.Length);
        offset += 4;
        checkpoint.SerializedState.Span.CopyTo(frame.AsSpan(offset));
        offset += checkpoint.SerializedState.Length;
        SHA256.HashData(frame.AsSpan(0, offset), frame.AsSpan(offset, DigestSizeBytes));
        return ControllerResult<byte[]>.Success(frame);
    }

    private static ControllerResult<DeletionJournalCheckpoint> ParseFrame(
        ProfileId expectedProfile,
        SyncOperationId expectedOperation,
        ReadOnlySpan<byte> frame,
        ProfileStorageRevision revision)
    {
        if (frame.Length < FixedFrameBytes ||
            frame.Length > FixedFrameBytes + DeletionOperationCheckpointCodec.MaximumPayloadBytes)
        {
            return ControllerResult<DeletionJournalCheckpoint>.Failure(InvalidJournalPayload());
        }

        var contentLength = frame.Length - DigestSizeBytes;
        Span<byte> digest = stackalloc byte[DigestSizeBytes];
        SHA256.HashData(frame[..contentLength], digest);
        if (!CryptographicOperations.FixedTimeEquals(digest, frame[contentLength..]))
            return ControllerResult<DeletionJournalCheckpoint>.Failure(InvalidJournalPayload());

        var offset = 0;
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(offset, 4));
        offset += 4;
        var frameVersion = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(offset, 4));
        offset += 4;
        var profile = new ProfileId(new Guid(frame.Slice(offset, 16)));
        offset += 16;
        var operation = new SyncOperationId(new Guid(frame.Slice(offset, 16)));
        offset += 16;
        var formatVersion = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(offset, 4));
        offset += 4;
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(offset, 4));
        offset += 4;

        if (magic != FrameMagic ||
            frameVersion != FrameVersion ||
            profile != expectedProfile ||
            operation != expectedOperation ||
            formatVersion != DeletionOperationCheckpointCodec.CurrentFormatVersion ||
            payloadLength <= 0 ||
            payloadLength > DeletionOperationCheckpointCodec.MaximumPayloadBytes ||
            offset + payloadLength != contentLength)
        {
            return ControllerResult<DeletionJournalCheckpoint>.Failure(InvalidJournalPayload());
        }

        var payload = frame.Slice(offset, payloadLength).ToArray();
        var parsed = DeletionOperationCheckpointCodec.Deserialize(payload);
        if (!parsed.IsSuccess ||
            parsed.Value!.ProfileId != expectedProfile ||
            parsed.Value.OperationId != expectedOperation)
        {
            return ControllerResult<DeletionJournalCheckpoint>.Failure(InvalidJournalPayload());
        }

        return ControllerResult<DeletionJournalCheckpoint>.Success(new DeletionJournalCheckpoint(
            expectedProfile,
            expectedOperation,
            formatVersion,
            payload,
            revision));
    }

    private static ControllerResult<ProfileStorageAddress> CreateAddress(
        SyncOperationContext? context,
        SyncOperationId operationId)
    {
        if (context is null || !operationId.IsDefined || context.OperationId != operationId)
            return ControllerResult<ProfileStorageAddress>.Failure(InvalidJournalRequest());

        var normal = NormalProfileOperationGuard.RequireNormal(context.Browsing);
        if (!normal.IsSuccess)
            return ControllerResult<ProfileStorageAddress>.Failure(normal.Error!);

        var key = ProfileStorageKey.Create($"operation:{operationId.Value:N}");
        return key.IsSuccess
            ? ProfileStorageAddress.Create(
                context.Browsing.Privacy,
                JournalNamespace,
                key.Value,
                ProfileStorageDurability.Persistent)
            : ControllerResult<ProfileStorageAddress>.Failure(key.Error!);
    }

    private static ProfileStorageNamespace CreateNamespace()
    {
        var created = ProfileStorageNamespace.Create("sync.deletion.journal");
        return created.IsSuccess
            ? created.Value!
            : throw new InvalidOperationException("The built-in deletion journal namespace is invalid.");
    }

    private static ControllerError InvalidJournalRequest() =>
        ControllerError.Create(
            ControllerErrorCode.InvalidRequest,
            "sync.deletion.journal-request-invalid");

    private static ControllerError InvalidJournalPayload() =>
        ControllerError.Create(
            ControllerErrorCode.IntegrityFailure,
            "sync.deletion.journal-payload-invalid");
}
