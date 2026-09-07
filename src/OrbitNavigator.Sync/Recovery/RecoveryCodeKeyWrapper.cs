using System.Security.Cryptography;
using System.Text;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Sync;

namespace OrbitNavigator.Sync.Recovery;

public sealed class RecoveryCodeWrappingOptions
{
    public const int MinimumPbkdf2Iterations = 600_000;
    public const int DefaultPbkdf2Iterations = 600_000;
    public const int DefaultMaximumAcceptedIterations = 2_000_000;

    public RecoveryCodeWrappingOptions(
        int iterations = DefaultPbkdf2Iterations,
        int maximumAcceptedIterations = DefaultMaximumAcceptedIterations)
    {
        if (iterations < MinimumPbkdf2Iterations ||
            maximumAcceptedIterations < MinimumPbkdf2Iterations ||
            iterations > maximumAcceptedIterations)
        {
            throw new ArgumentOutOfRangeException(
                nameof(iterations),
                "PBKDF2 iterations must meet the enforced production minimum and accepted maximum.");
        }

        Iterations = iterations;
        MaximumAcceptedIterations = maximumAcceptedIterations;
    }

    public int Iterations { get; }

    public int MaximumAcceptedIterations { get; }
}

/// <summary>
/// A disposable 32-byte sync root key. Disposal overwrites the complete backing
/// buffer; callers can only obtain a copy into memory they explicitly provide.
/// </summary>
public sealed class UnwrappedSyncRootKey : IDisposable
{
    public const int KeySizeBytes = 32;

    private readonly object _gate = new();
    private readonly byte[] _key;
    private bool _disposed;

    internal UnwrappedSyncRootKey(byte[] key)
    {
        if (key.Length != KeySizeBytes)
            throw new ArgumentException("A sync root key must be exactly 32 bytes.", nameof(key));
        _key = key;
    }

    public bool IsDisposed
    {
        get
        {
            lock (_gate)
                return _disposed;
        }
    }

    public void CopyTo(Span<byte> destination)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (destination.Length < KeySizeBytes)
                throw new ArgumentException("The destination is too small.", nameof(destination));

            _key.CopyTo(destination);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            CryptographicOperations.ZeroMemory(_key);
            _disposed = true;
        }
    }
}

/// <summary>
/// Wraps sync root keys with a one-shot recovery-code lease. Profile identity and
/// all key/KDF metadata are authenticated as canonical AES-GCM associated data.
/// </summary>
public sealed class RecoveryCodeKeyWrapper
{
    private const int SaltSizeBytes = 32;
    private const int WrappingKeySizeBytes = 32;
    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;
    private const int Pbkdf2MemoryKiB = 0;
    private const int Pbkdf2Parallelism = 1;

    private readonly RecoveryCodeLeaseStore _leaseStore;
    private readonly RecoveryCodeWrappingOptions _options;

    public RecoveryCodeKeyWrapper(
        RecoveryCodeLeaseStore leaseStore,
        RecoveryCodeWrappingOptions? options = null)
    {
        _leaseStore = leaseStore ?? throw new ArgumentNullException(nameof(leaseStore));
        _options = options ?? new RecoveryCodeWrappingOptions();
    }

    public ControllerResult<WrappedSyncKeyset> Wrap(
        ProfileId profileId,
        SyncKeysetId keysetId,
        long generation,
        ReadOnlySpan<byte> syncRootKey,
        RecoveryCodeLeaseId recoveryCodeLease)
    {
        if (profileId.IsEmpty ||
            !keysetId.IsDefined ||
            generation < 0 ||
            syncRootKey.Length != UnwrappedSyncRootKey.KeySizeBytes ||
            !recoveryCodeLease.IsDefined)
        {
            return ControllerResult<WrappedSyncKeyset>.Failure(InvalidRequest());
        }

        var leaseResult = _leaseStore.Consume(profileId, recoveryCodeLease);
        if (!leaseResult.IsSuccess)
            return ControllerResult<WrappedSyncKeyset>.Failure(leaseResult.Error!);

        using var lease = leaseResult.Value!;
        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var ciphertext = new byte[UnwrappedSyncRootKey.KeySizeBytes];
        var tag = new byte[TagSizeBytes];
        var wrappingKey = new byte[WrappingKeySizeBytes];

        try
        {
            lease.UseClaimedCode(code =>
            {
                DeriveWrappingKey(code, salt, _options.Iterations, wrappingKey);
                return 0;
            });

            var kdf = new SyncKdfParameters(
                SyncKdfAlgorithm.Pbkdf2Sha256,
                salt,
                _options.Iterations,
                Pbkdf2MemoryKiB,
                Pbkdf2Parallelism,
                WrappingKeySizeBytes);
            var aad = EncodeCanonicalMetadata(profileId, keysetId, generation, SyncKeyWrapMethod.RecoveryCode, kdf);
            using (var aes = new AesGcm(wrappingKey, TagSizeBytes))
                aes.Encrypt(nonce, syncRootKey, ciphertext, tag, aad);

            return ControllerResult<WrappedSyncKeyset>.Success(new WrappedSyncKeyset(
                keysetId,
                generation,
                SyncKeyWrapMethod.RecoveryCode,
                kdf,
                nonce,
                ciphertext,
                tag));
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
            return ControllerResult<WrappedSyncKeyset>.Failure(InternalCryptoFailure());
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException)
        {
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
            return ControllerResult<WrappedSyncKeyset>.Failure(LeaseUnavailable());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrappingKey);
        }
    }

    public ControllerResult<UnwrappedSyncRootKey> Unwrap(
        ProfileId profileId,
        WrappedSyncKeyset wrappedKeyset,
        RecoveryCodeLeaseId recoveryCodeLease)
    {
        if (profileId.IsEmpty || wrappedKeyset is null || !recoveryCodeLease.IsDefined)
            return ControllerResult<UnwrappedSyncRootKey>.Failure(InvalidRequest());

        var snapshotResult = SnapshotAndValidate(wrappedKeyset);
        if (!snapshotResult.IsSuccess)
            return ControllerResult<UnwrappedSyncRootKey>.Failure(snapshotResult.Error!);
        var snapshot = snapshotResult.Value!;

        var leaseResult = _leaseStore.Consume(profileId, recoveryCodeLease);
        if (!leaseResult.IsSuccess)
            return ControllerResult<UnwrappedSyncRootKey>.Failure(leaseResult.Error!);

        using var lease = leaseResult.Value!;
        var wrappingKey = new byte[WrappingKeySizeBytes];
        var plaintext = new byte[UnwrappedSyncRootKey.KeySizeBytes];
        try
        {
            lease.UseClaimedCode(code =>
            {
                DeriveWrappingKey(code, snapshot.Salt, snapshot.Iterations, wrappingKey);
                return 0;
            });

            var kdf = new SyncKdfParameters(
                SyncKdfAlgorithm.Pbkdf2Sha256,
                snapshot.Salt,
                snapshot.Iterations,
                Pbkdf2MemoryKiB,
                Pbkdf2Parallelism,
                WrappingKeySizeBytes);
            var aad = EncodeCanonicalMetadata(
                profileId,
                snapshot.KeysetId,
                snapshot.Generation,
                SyncKeyWrapMethod.RecoveryCode,
                kdf);
            using (var aes = new AesGcm(wrappingKey, TagSizeBytes))
            {
                aes.Decrypt(
                    snapshot.Nonce,
                    snapshot.Ciphertext,
                    snapshot.Tag,
                    plaintext,
                    aad);
            }

            return ControllerResult<UnwrappedSyncRootKey>.Success(new UnwrappedSyncRootKey(plaintext));
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            return ControllerResult<UnwrappedSyncRootKey>.Failure(IntegrityFailure());
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            return ControllerResult<UnwrappedSyncRootKey>.Failure(LeaseUnavailable());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrappingKey);
            snapshot.Dispose();
        }
    }

    private ControllerResult<WrappedSnapshot> SnapshotAndValidate(WrappedSyncKeyset wrapped)
    {
        if (!wrapped.KeysetId.IsDefined ||
            wrapped.Generation < 0 ||
            wrapped.WrapMethod != SyncKeyWrapMethod.RecoveryCode ||
            wrapped.Kdf is null ||
            wrapped.Kdf.Algorithm != SyncKdfAlgorithm.Pbkdf2Sha256 ||
            wrapped.Kdf.Salt.Length != SaltSizeBytes ||
            wrapped.Kdf.Iterations < RecoveryCodeWrappingOptions.MinimumPbkdf2Iterations ||
            wrapped.Kdf.Iterations > _options.MaximumAcceptedIterations ||
            wrapped.Kdf.MemoryKiB != Pbkdf2MemoryKiB ||
            wrapped.Kdf.Parallelism != Pbkdf2Parallelism ||
            wrapped.Kdf.DerivedKeySizeBytes != WrappingKeySizeBytes ||
            wrapped.Nonce.Length != NonceSizeBytes ||
            wrapped.WrappedKeyCiphertext.Length != UnwrappedSyncRootKey.KeySizeBytes ||
            wrapped.AuthenticationTag.Length != TagSizeBytes)
        {
            return ControllerResult<WrappedSnapshot>.Failure(InvalidWrappedKeyset());
        }

        return ControllerResult<WrappedSnapshot>.Success(new WrappedSnapshot(
            wrapped.KeysetId,
            wrapped.Generation,
            wrapped.Kdf.Iterations,
            wrapped.Kdf.Salt.ToArray(),
            wrapped.Nonce.ToArray(),
            wrapped.WrappedKeyCiphertext.ToArray(),
            wrapped.AuthenticationTag.ToArray()));
    }

    private static void DeriveWrappingKey(
        ReadOnlySpan<char> code,
        ReadOnlySpan<byte> salt,
        int iterations,
        Span<byte> destination)
    {
        Span<byte> encodedCode = stackalloc byte[Encoding.UTF8.GetMaxByteCount(RecoveryCodeLeaseStore.FormattedCodeLength)];
        try
        {
            var byteCount = Encoding.UTF8.GetBytes(code, encodedCode);
            Rfc2898DeriveBytes.Pbkdf2(
                encodedCode[..byteCount],
                salt,
                destination,
                iterations,
                HashAlgorithmName.SHA256);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encodedCode);
        }
    }

    private static byte[] EncodeCanonicalMetadata(
        ProfileId profileId,
        SyncKeysetId keysetId,
        long generation,
        SyncKeyWrapMethod wrapMethod,
        SyncKdfParameters kdf)
    {
        var canonical = string.Join(
            '|',
            "orbit-navigator",
            "recovery-key-wrap",
            "version=1",
            $"profile={profileId.Value:N}",
            $"keyset={keysetId.Value:N}",
            $"generation={generation.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            $"wrap={wrapMethod.ToString().ToLowerInvariant()}",
            $"kdf={kdf.Algorithm.ToString().ToLowerInvariant()}",
            $"iterations={kdf.Iterations.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            $"memorykib={kdf.MemoryKiB.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            $"parallelism={kdf.Parallelism.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            $"derivedbytes={kdf.DerivedKeySizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            $"salt={Convert.ToHexString(kdf.Salt.Span)}");
        return Encoding.UTF8.GetBytes(canonical);
    }

    private static ControllerError InvalidRequest() =>
        ControllerError.Create(
            ControllerErrorCode.InvalidRequest,
            "sync.recovery.wrap-request-invalid");

    private static ControllerError InvalidWrappedKeyset() =>
        ControllerError.Create(
            ControllerErrorCode.InvalidRequest,
            "sync.recovery.wrapped-keyset-invalid");

    private static ControllerError IntegrityFailure() =>
        ControllerError.Create(
            ControllerErrorCode.IntegrityFailure,
            "sync.recovery.integrity-failure");

    private static ControllerError LeaseUnavailable() =>
        ControllerError.Create(
            ControllerErrorCode.AlreadyHandled,
            "sync.recovery.lease-unavailable");

    private static ControllerError InternalCryptoFailure() =>
        ControllerError.Create(
            ControllerErrorCode.InternalFailure,
            "sync.recovery.crypto-failure");

    private sealed class WrappedSnapshot : IDisposable
    {
        public WrappedSnapshot(
            SyncKeysetId keysetId,
            long generation,
            int iterations,
            byte[] salt,
            byte[] nonce,
            byte[] ciphertext,
            byte[] tag)
        {
            KeysetId = keysetId;
            Generation = generation;
            Iterations = iterations;
            Salt = salt;
            Nonce = nonce;
            Ciphertext = ciphertext;
            Tag = tag;
        }

        public SyncKeysetId KeysetId { get; }

        public long Generation { get; }

        public int Iterations { get; }

        public byte[] Salt { get; }

        public byte[] Nonce { get; }

        public byte[] Ciphertext { get; }

        public byte[] Tag { get; }

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(Salt);
            CryptographicOperations.ZeroMemory(Nonce);
            CryptographicOperations.ZeroMemory(Ciphertext);
            CryptographicOperations.ZeroMemory(Tag);
        }
    }
}
