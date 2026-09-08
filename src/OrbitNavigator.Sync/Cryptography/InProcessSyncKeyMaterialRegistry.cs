using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Sync;

namespace OrbitNavigator.Sync.Cryptography;

internal delegate T SyncRootKeyReader<T>(ReadOnlySpan<byte> rootKey);

/// <summary>
/// Keeps sync root keys inside the process and exposes only opaque handles to callers.
/// The registry owns a private copy of every registered key and zeroes that copy when
/// the handle is removed or the registry is disposed.
/// </summary>
public sealed class InProcessSyncKeyMaterialRegistry : IDisposable
{
    public const int RootKeySizeBytes = 32;
    internal const int CategoryKeySizeBytes = 32;

    private static readonly byte[] DerivationSaltLabel =
        Encoding.UTF8.GetBytes("orbit-navigator|sync-key-derivation|v1");

    private readonly ConcurrentDictionary<Guid, KeyEntry> _entries = new();
    private int _disposed;

    public int Count => _entries.Count;

    public SyncKeyMaterialHandle Register(ProfileId syncProfileId, ReadOnlySpan<byte> rootKey)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (syncProfileId.IsEmpty || rootKey.Length != RootKeySizeBytes)
        {
            throw new ArgumentException(
                $"A sync profile and exactly {RootKeySizeBytes} root-key bytes are required.",
                nameof(rootKey));
        }

        while (true)
        {
            var handle = new SyncKeyMaterialHandle(Guid.NewGuid());
            var entry = new KeyEntry(syncProfileId, rootKey);
            if (_entries.TryAdd(handle.Value, entry))
            {
                if (Volatile.Read(ref _disposed) == 0)
                    return handle;

                if (_entries.TryRemove(handle.Value, out var addedAfterDisposal))
                    addedAfterDisposal.Dispose();
                throw new ObjectDisposedException(nameof(InProcessSyncKeyMaterialRegistry));
            }

            entry.Dispose();
        }
    }

    public bool Remove(SyncKeyMaterialHandle? handle)
    {
        if (handle is null || handle.Value == Guid.Empty)
            return false;

        if (!_entries.TryRemove(handle.Value, out var entry))
            return false;

        entry.Dispose();
        return true;
    }

    internal bool TryDeriveCategoryKey(
        SyncKeyMaterialHandle? handle,
        ProfileId syncProfileId,
        SyncKeysetId keysetId,
        long keyEpoch,
        SyncDataCategory category,
        Span<byte> destination)
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            handle is null ||
            handle.Value == Guid.Empty ||
            syncProfileId.IsEmpty ||
            !keysetId.IsDefined ||
            keyEpoch < 0 ||
            !SyncAllowlist.IsAllowed(category) ||
            destination.Length != CategoryKeySizeBytes ||
            !_entries.TryGetValue(handle.Value, out var entry))
        {
            return false;
        }

        Span<byte> salt = stackalloc byte[DerivationSaltLabel.Length + 16 + sizeof(long)];
        DerivationSaltLabel.CopyTo(salt);
        if (!keysetId.Value.TryWriteBytes(
                salt[DerivationSaltLabel.Length..],
                bigEndian: true,
                out var bytesWritten) ||
            bytesWritten != 16)
        {
            CryptographicOperations.ZeroMemory(salt);
            return false;
        }

        BinaryPrimitives.WriteInt64BigEndian(salt[^sizeof(long)..], keyEpoch);
        var information = category switch
        {
            SyncDataCategory.History => "orbit-navigator|sync-category|history|v1"u8,
            SyncDataCategory.Settings => "orbit-navigator|sync-category|settings|v1"u8,
            SyncDataCategory.OpenTabs => "orbit-navigator|sync-category|open-tabs|v1"u8,
            _ => ReadOnlySpan<byte>.Empty,
        };

        try
        {
            return entry.TryDerive(syncProfileId, salt, information, destination);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    internal bool TryUseRootKey<T>(
        SyncKeyMaterialHandle? handle,
        ProfileId syncProfileId,
        SyncRootKeyReader<T> reader,
        out T? result)
    {
        ArgumentNullException.ThrowIfNull(reader);
        result = default;
        if (Volatile.Read(ref _disposed) != 0 ||
            handle is null || handle.Value == Guid.Empty || syncProfileId.IsEmpty ||
            !_entries.TryGetValue(handle.Value, out var entry))
            return false;
        return entry.TryUse(syncProfileId, reader, out result);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        foreach (var pair in _entries)
        {
            if (_entries.TryRemove(pair.Key, out var entry))
                entry.Dispose();
        }
    }

    private sealed class KeyEntry : IDisposable
    {
        private readonly object _gate = new();
        private readonly ProfileId _syncProfileId;
        private readonly byte[] _rootKey;
        private bool _disposed;

        public KeyEntry(ProfileId syncProfileId, ReadOnlySpan<byte> rootKey)
        {
            _syncProfileId = syncProfileId;
            _rootKey = rootKey.ToArray();
        }

        public bool TryDerive(
            ProfileId syncProfileId,
            ReadOnlySpan<byte> salt,
            ReadOnlySpan<byte> information,
            Span<byte> destination)
        {
            lock (_gate)
            {
                if (_disposed || syncProfileId != _syncProfileId)
                    return false;

                HKDF.DeriveKey(
                    HashAlgorithmName.SHA256,
                    _rootKey,
                    destination,
                    salt,
                    information);
                return true;
            }
        }

        public bool TryUse<T>(
            ProfileId syncProfileId,
            SyncRootKeyReader<T> reader,
            out T? result)
        {
            lock (_gate)
            {
                if (_disposed || syncProfileId != _syncProfileId)
                {
                    result = default;
                    return false;
                }

                result = reader(_rootKey);
                return true;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;

                CryptographicOperations.ZeroMemory(_rootKey);
                _disposed = true;
            }
        }
    }
}
