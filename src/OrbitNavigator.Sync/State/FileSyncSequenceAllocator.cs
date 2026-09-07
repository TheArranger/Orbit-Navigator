using System.Buffers.Binary;
using System.Collections.Concurrent;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Sync;

namespace OrbitNavigator.Sync.State;

/// <summary>
/// File-backed monotonic sequence allocation. Each allocation is flushed and
/// atomically replaced before the value is released to the caller. Files are
/// isolated by profile, device, and client generation.
/// </summary>
public sealed class FileSyncSequenceAllocator : IDurableSyncSequenceAllocator
{
    private const int StateSizeBytes = 4 + 1 + 16 + 16 + sizeof(long) + sizeof(long);
    private static readonly byte[] Magic = "ONSQ"u8.ToArray();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _rootDirectory;

    public FileSyncSequenceAllocator(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootDirectory = Path.GetFullPath(rootDirectory);
    }

    public async ValueTask<ControllerResult<SyncSequenceReservation>> ReserveNextAsync(
        SyncOperationContext context,
        SyncStateScope scope,
        CancellationToken cancellationToken)
    {
        if (context is null ||
            scope is not { IsDefined: true } ||
            context.Browsing.Privacy.ProfileId != scope.ProfileId)
        {
            return Invalid();
        }

        var statePath = GetStatePath(scope);
        var gate = Gates.GetOrAdd(statePath, static _ => new SemaphoreSlim(1, 1));
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled();
        }

        try
        {
            Directory.CreateDirectory(_rootDirectory);
            await using var crossProcessLock = await AcquireProcessLockAsync(
                    $"{statePath}.lock",
                    cancellationToken)
                .ConfigureAwait(false);
            var lastSequence = await ReadLastSequenceAsync(statePath, scope, cancellationToken)
                .ConfigureAwait(false);
            if (!lastSequence.IsSuccess)
                return ControllerResult<SyncSequenceReservation>.Failure(lastSequence.Error!);
            if (lastSequence.Value!.Value == long.MaxValue)
            {
                return ControllerResult<SyncSequenceReservation>.Failure(
                    ControllerError.Create(
                        ControllerErrorCode.Conflict,
                        "sync.sequence.exhausted"));
            }

            var next = checked(lastSequence.Value.Value + 1);
            var persisted = await PersistAsync(statePath, scope, next, cancellationToken)
                .ConfigureAwait(false);
            if (!persisted.IsSuccess)
                return ControllerResult<SyncSequenceReservation>.Failure(persisted.Error!);

            return ControllerResult<SyncSequenceReservation>.Success(
                new SyncSequenceReservation(scope, next));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled();
        }
        catch (IOException)
        {
            return Unavailable();
        }
        catch (UnauthorizedAccessException)
        {
            return Unavailable();
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask<ControllerResult<SequenceValue>> ReadLastSequenceAsync(
        string statePath,
        SyncStateScope scope,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(statePath))
            return ControllerResult<SequenceValue>.Success(new SequenceValue(-1));

        var bytes = await File.ReadAllBytesAsync(statePath, cancellationToken).ConfigureAwait(false);
        if (bytes.Length != StateSizeBytes || !bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            return Integrity();

        var offset = Magic.Length;
        if (bytes[offset++] != 1)
            return Integrity();

        var profileId = new Guid(bytes.AsSpan(offset, 16), bigEndian: true);
        offset += 16;
        var deviceId = new Guid(bytes.AsSpan(offset, 16), bigEndian: true);
        offset += 16;
        var generation = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(offset, sizeof(long)));
        offset += sizeof(long);
        var lastSequence = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(offset, sizeof(long)));

        if (profileId != scope.ProfileId.Value ||
            deviceId != scope.DeviceId.Value ||
            generation != scope.ClientGeneration ||
            lastSequence < 0)
        {
            return Integrity();
        }

        return ControllerResult<SequenceValue>.Success(new SequenceValue(lastSequence));
    }

    private static async ValueTask<ControllerResult> PersistAsync(
        string statePath,
        SyncStateScope scope,
        long sequence,
        CancellationToken cancellationToken)
    {
        var bytes = new byte[StateSizeBytes];
        Magic.CopyTo(bytes, 0);
        var offset = Magic.Length;
        bytes[offset++] = 1;
        if (!scope.ProfileId.Value.TryWriteBytes(bytes.AsSpan(offset, 16), bigEndian: true, out var written) ||
            written != 16)
        {
            return ControllerResult.Failure(IntegrityError());
        }

        offset += 16;
        if (!scope.DeviceId.Value.TryWriteBytes(bytes.AsSpan(offset, 16), bigEndian: true, out written) ||
            written != 16)
        {
            return ControllerResult.Failure(IntegrityError());
        }

        offset += 16;
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(offset, sizeof(long)), scope.ClientGeneration);
        offset += sizeof(long);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(offset, sizeof(long)), sequence);

        var temporaryPath = $"{statePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4_096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, statePath, overwrite: true);
            return ControllerResult.Success();
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static async ValueTask<FileStream> AcquireProcessLockAsync(
        string lockPath,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private string GetStatePath(SyncStateScope scope) =>
        Path.Combine(
            _rootDirectory,
            $"{scope.ProfileId.Value:N}-{scope.DeviceId.Value:N}-{scope.ClientGeneration}.sequence");

    private static ControllerResult<SequenceValue> Integrity() =>
        ControllerResult<SequenceValue>.Failure(IntegrityError());

    private static ControllerError IntegrityError() =>
        ControllerError.Create(
            ControllerErrorCode.IntegrityFailure,
            "sync.sequence.state-invalid");

    private static ControllerResult<SyncSequenceReservation> Invalid() =>
        ControllerResult<SyncSequenceReservation>.Failure(
            ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "sync.sequence.request-invalid"));

    private static ControllerResult<SyncSequenceReservation> Cancelled() =>
        ControllerResult<SyncSequenceReservation>.Failure(
            ControllerError.Create(
                ControllerErrorCode.Cancelled,
                "sync.sequence.cancelled",
                isRetryable: true));

    private static ControllerResult<SyncSequenceReservation> Unavailable() =>
        ControllerResult<SyncSequenceReservation>.Failure(
            ControllerError.Create(
                ControllerErrorCode.Unavailable,
                "sync.sequence.storage-unavailable",
                isRetryable: true));

    private sealed record SequenceValue(long Value);
}
