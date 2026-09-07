using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Sync;

namespace OrbitNavigator.Sync.State;

/// <summary>
/// Profile-isolated durable sync cursors. Logical checkpoint comparison is
/// combined with the profile store's revision CAS so a stale coordinator cannot
/// overwrite progress committed by another coordinator.
/// </summary>
public sealed class ProfileStorageSyncCheckpointStore : IDurableSyncCheckpointStore
{
    private const uint Magic = 0x50434E4F; // ONCP in little-endian storage.
    private const int FormatVersion = 1;
    private const int DigestSizeBytes = 32;
    private const int MaximumCursorBytes = 16_384;
    private const int FixedContentBytes = 4 + 4 + 16 + 16 + 8 + 8 + 4 + 4;
    private const int MaximumPayloadBytes = FixedContentBytes + (2 * MaximumCursorBytes) + DigestSizeBytes;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly ProfileStorageNamespace StorageNamespace = CreateNamespace();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(StringComparer.Ordinal);

    private readonly IProfileStorage _storage;

    public ProfileStorageSyncCheckpointStore(IProfileStorage storage)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    }

    public async ValueTask<ControllerResult<SyncCheckpoint>> LoadAsync(
        SyncOperationContext context,
        SyncStateScope scope,
        CancellationToken cancellationToken)
    {
        var address = Address(context, scope);
        if (!address.IsSuccess)
            return ControllerResult<SyncCheckpoint>.Failure(address.Error!);

        try
        {
            var read = await _storage.ReadAsync(address.Value!, cancellationToken).ConfigureAwait(false);
            if (!read.IsSuccess)
            {
                return read.Error!.Code == ControllerErrorCode.NotFound
                    ? ControllerResult<SyncCheckpoint>.Success(Initial(scope))
                    : ControllerResult<SyncCheckpoint>.Failure(read.Error);
            }

            return Decode(read.Value!.Payload.Span, scope);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled<SyncCheckpoint>();
        }
    }

    public ValueTask<ControllerResult<SyncCheckpoint>> CommitPushAsync(
        SyncOperationContext context,
        SyncCheckpoint expected,
        LocalChangeCursor pushedThrough,
        CancellationToken cancellationToken) =>
        CommitAsync(
            context,
            expected,
            expected is not null &&
            expected.Version < long.MaxValue &&
            pushedThrough is { IsDefined: true }
                ? expected with
                {
                    Version = NextVersion(expected.Version),
                    PushedThrough = pushedThrough,
                }
                : null,
            cancellationToken);

    public ValueTask<ControllerResult<SyncCheckpoint>> CommitPullAsync(
        SyncOperationContext context,
        SyncCheckpoint expected,
        SyncCursor pulledThrough,
        CancellationToken cancellationToken) =>
        CommitAsync(
            context,
            expected,
            expected is not null &&
            expected.Version < long.MaxValue &&
            SyncStateValidation.IsDefined(pulledThrough)
                ? expected with
                {
                    Version = NextVersion(expected.Version),
                    PulledThrough = pulledThrough,
                }
                : null,
            cancellationToken);

    private async ValueTask<ControllerResult<SyncCheckpoint>> CommitAsync(
        SyncOperationContext context,
        SyncCheckpoint expected,
        SyncCheckpoint? next,
        CancellationToken cancellationToken)
    {
        if (expected is not { IsDefined: true } ||
            next is not { IsDefined: true } ||
            next.Version <= expected.Version)
        {
            return Invalid<SyncCheckpoint>();
        }

        var address = Address(context, expected.Scope);
        if (!address.IsSuccess)
            return ControllerResult<SyncCheckpoint>.Failure(address.Error!);

        var gateKey = GateKey(expected.Scope);
        var gate = Gates.GetOrAdd(gateKey, static _ => new SemaphoreSlim(1, 1));
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled<SyncCheckpoint>();
        }

        try
        {
            var read = await _storage.ReadAsync(address.Value!, cancellationToken).ConfigureAwait(false);
            ProfileStorageRevision? storageRevision = null;
            SyncCheckpoint current;
            if (read.IsSuccess)
            {
                var decoded = Decode(read.Value!.Payload.Span, expected.Scope);
                if (!decoded.IsSuccess)
                    return decoded;
                current = decoded.Value!;
                storageRevision = read.Value.Revision;
            }
            else if (read.Error!.Code == ControllerErrorCode.NotFound)
            {
                current = Initial(expected.Scope);
            }
            else
            {
                return ControllerResult<SyncCheckpoint>.Failure(read.Error);
            }

            if (current != expected)
                return Conflict<SyncCheckpoint>();

            var encoded = Encode(next);
            if (!encoded.IsSuccess)
                return ControllerResult<SyncCheckpoint>.Failure(encoded.Error!);
            var writeRequest = ProfileStorageWriteRequest.Create(
                address.Value,
                encoded.Value!,
                storageRevision);
            if (!writeRequest.IsSuccess)
                return ControllerResult<SyncCheckpoint>.Failure(writeRequest.Error!);

            var written = await _storage.WriteAsync(writeRequest.Value!, cancellationToken)
                .ConfigureAwait(false);
            if (!written.IsSuccess)
                return ControllerResult<SyncCheckpoint>.Failure(written.Error!);
            if (written.Value!.Revision.IsEmpty)
                return Integrity<SyncCheckpoint>();

            return ControllerResult<SyncCheckpoint>.Success(next);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled<SyncCheckpoint>();
        }
        catch (OverflowException)
        {
            return Conflict<SyncCheckpoint>();
        }
        finally
        {
            gate.Release();
        }
    }

    private static ControllerResult<byte[]> Encode(SyncCheckpoint checkpoint)
    {
        if (!checkpoint.IsDefined)
            return Invalid<byte[]>();

        byte[]? pushed = null;
        byte[]? pulled = null;
        try
        {
            pushed = EncodeCursor(checkpoint.PushedThrough?.Value);
            pulled = EncodeCursor(checkpoint.PulledThrough?.Value);
            if (pushed.Length > MaximumCursorBytes || pulled.Length > MaximumCursorBytes)
                return Invalid<byte[]>();

            var contentLength = FixedContentBytes + pushed.Length + pulled.Length;
            var payload = new byte[contentLength + DigestSizeBytes];
            var offset = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset, 4), Magic);
            offset += 4;
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset, 4), FormatVersion);
            offset += 4;
            checkpoint.Scope.ProfileId.Value.TryWriteBytes(payload.AsSpan(offset, 16));
            offset += 16;
            checkpoint.Scope.DeviceId.Value.TryWriteBytes(payload.AsSpan(offset, 16));
            offset += 16;
            BinaryPrimitives.WriteInt64LittleEndian(
                payload.AsSpan(offset, sizeof(long)),
                checkpoint.Scope.ClientGeneration);
            offset += sizeof(long);
            BinaryPrimitives.WriteInt64LittleEndian(
                payload.AsSpan(offset, sizeof(long)),
                checkpoint.Version);
            offset += sizeof(long);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset, 4), CursorLength(pushed, checkpoint.PushedThrough));
            offset += 4;
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset, 4), CursorLength(pulled, checkpoint.PulledThrough));
            offset += 4;
            pushed.CopyTo(payload, offset);
            offset += pushed.Length;
            pulled.CopyTo(payload, offset);
            offset += pulled.Length;
            SHA256.HashData(payload.AsSpan(0, contentLength), payload.AsSpan(offset, DigestSizeBytes));
            return ControllerResult<byte[]>.Success(payload);
        }
        catch (EncoderFallbackException)
        {
            return Invalid<byte[]>();
        }
        finally
        {
            if (pushed is not null)
                CryptographicOperations.ZeroMemory(pushed);
            if (pulled is not null)
                CryptographicOperations.ZeroMemory(pulled);
        }
    }

    private static ControllerResult<SyncCheckpoint> Decode(
        ReadOnlySpan<byte> payload,
        SyncStateScope expectedScope)
    {
        if (payload.Length < FixedContentBytes + DigestSizeBytes || payload.Length > MaximumPayloadBytes)
            return Integrity<SyncCheckpoint>();

        var contentLength = payload.Length - DigestSizeBytes;
        Span<byte> digest = stackalloc byte[DigestSizeBytes];
        SHA256.HashData(payload[..contentLength], digest);
        if (!CryptographicOperations.FixedTimeEquals(digest, payload[contentLength..]))
            return Integrity<SyncCheckpoint>();

        try
        {
            var offset = 0;
            var magic = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset, 4));
            offset += 4;
            var format = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset, 4));
            offset += 4;
            var profile = new ProfileId(new Guid(payload.Slice(offset, 16)));
            offset += 16;
            var device = new DeviceId(new Guid(payload.Slice(offset, 16)));
            offset += 16;
            var generation = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(offset, sizeof(long)));
            offset += sizeof(long);
            var version = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(offset, sizeof(long)));
            offset += sizeof(long);
            var pushedLength = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset, 4));
            offset += 4;
            var pulledLength = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset, 4));
            offset += 4;

            if (magic != Magic ||
                format != FormatVersion ||
                profile != expectedScope.ProfileId ||
                device != expectedScope.DeviceId ||
                generation != expectedScope.ClientGeneration ||
                version < 0 ||
                !ValidEncodedLength(pushedLength) ||
                !ValidEncodedLength(pulledLength))
            {
                return Integrity<SyncCheckpoint>();
            }

            var pushedBytes = pushedLength < 0 ? 0 : pushedLength;
            var pulledBytes = pulledLength < 0 ? 0 : pulledLength;
            if (offset + pushedBytes + pulledBytes != contentLength)
                return Integrity<SyncCheckpoint>();

            LocalChangeCursor? pushed = null;
            if (pushedLength >= 0)
            {
                pushed = new LocalChangeCursor(StrictUtf8.GetString(payload.Slice(offset, pushedLength)));
                offset += pushedLength;
            }

            SyncCursor? pulled = null;
            if (pulledLength >= 0)
                pulled = new SyncCursor(StrictUtf8.GetString(payload.Slice(offset, pulledLength)));

            var checkpoint = new SyncCheckpoint(expectedScope, version, pushed, pulled);
            return checkpoint.IsDefined
                ? ControllerResult<SyncCheckpoint>.Success(checkpoint)
                : Integrity<SyncCheckpoint>();
        }
        catch (Exception exception) when (exception is ArgumentException or DecoderFallbackException)
        {
            return Integrity<SyncCheckpoint>();
        }
    }

    private static ControllerResult<ProfileStorageAddress> Address(
        SyncOperationContext? context,
        SyncStateScope? scope)
    {
        if (context is null ||
            scope is not { IsDefined: true } ||
            context.Browsing.Privacy.ProfileId != scope.ProfileId)
        {
            return ControllerResult<ProfileStorageAddress>.Failure(InvalidError());
        }

        var normal = NormalProfileOperationGuard.RequireNormal(context.Browsing);
        if (!normal.IsSuccess)
            return ControllerResult<ProfileStorageAddress>.Failure(normal.Error!);
        var key = ProfileStorageKey.Create(
            $"checkpoint:{scope.DeviceId.Value:N}:{scope.ClientGeneration}");
        return key.IsSuccess
            ? ProfileStorageAddress.Create(
                context.Browsing.Privacy,
                StorageNamespace,
                key.Value,
                ProfileStorageDurability.Persistent)
            : ControllerResult<ProfileStorageAddress>.Failure(key.Error!);
    }

    private static byte[] EncodeCursor(string? value) =>
        value is null ? [] : StrictUtf8.GetBytes(value);

    private static int CursorLength(byte[] encoded, object? cursor) =>
        cursor is null ? -1 : encoded.Length;

    private static bool ValidEncodedLength(int length) =>
        length == -1 || length is >= 1 and <= MaximumCursorBytes;

    private static long NextVersion(long version) => version + 1;

    private static SyncCheckpoint Initial(SyncStateScope scope) => new(scope, 0, null, null);

    private static string GateKey(SyncStateScope scope) =>
        $"{scope.ProfileId.Value:N}:{scope.DeviceId.Value:N}:{scope.ClientGeneration}";

    private static ProfileStorageNamespace CreateNamespace() =>
        ProfileStorageNamespace.Create("sync.checkpoints").Value!;

    private static ControllerError InvalidError() =>
        ControllerError.Create(
            ControllerErrorCode.InvalidRequest,
            "sync.checkpoint.request-invalid");

    private static ControllerResult<T> Invalid<T>() where T : class =>
        ControllerResult<T>.Failure(InvalidError());

    private static ControllerResult<T> Integrity<T>() where T : class =>
        ControllerResult<T>.Failure(ControllerError.Create(
            ControllerErrorCode.IntegrityFailure,
            "sync.checkpoint.state-invalid"));

    private static ControllerResult<T> Conflict<T>() where T : class =>
        ControllerResult<T>.Failure(ControllerError.Create(
            ControllerErrorCode.Conflict,
            "sync.checkpoint.revision-conflict",
            isRetryable: true));

    private static ControllerResult<T> Cancelled<T>() where T : class =>
        ControllerResult<T>.Failure(ControllerError.Create(
            ControllerErrorCode.Cancelled,
            "sync.checkpoint.cancelled",
            isRetryable: true));
}
