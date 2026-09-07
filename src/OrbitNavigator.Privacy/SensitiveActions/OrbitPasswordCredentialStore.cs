using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Privacy;

namespace OrbitNavigator.Privacy.SensitiveActions;

public sealed record OrbitPasswordKdfOptions(int Iterations)
{
    public const int DefaultIterations = 600_000;
    public const int MinimumIterations = 100_000;

    public static OrbitPasswordKdfOptions Default { get; } = new(DefaultIterations);

    public bool IsValid => Iterations >= MinimumIterations;
}

public sealed record OrbitPasswordVerification(bool IsConfigured, bool Matches);

public enum OrbitPasswordCredentialWriteMode
{
    RequireAbsent = 0,
    RequirePresent = 1,
}

public interface IOrbitPasswordCredentialStore
{
    ValueTask<ControllerResult<OrbitPasswordVaultState>> GetStateAsync(
        PrivacyContext context,
        CancellationToken cancellationToken);

    ValueTask<ControllerResult<OrbitPasswordVerification>> VerifyAsync(
        PrivacyContext context,
        SensitiveOrbitPassword password,
        CancellationToken cancellationToken);

    ValueTask<ControllerResult<OrbitPasswordVaultState>> SetAsync(
        PrivacyContext context,
        SensitiveOrbitPassword password,
        OrbitPasswordCredentialWriteMode mode,
        CancellationToken cancellationToken);

    ValueTask<ControllerResult<OrbitPasswordVaultState>> DeleteAsync(
        PrivacyContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// Stores only a salted verifier. The serialized verifier is additionally protected
/// by Windows CurrentUser protection before it reaches profile storage.
/// </summary>
public sealed class ProtectedOrbitPasswordCredentialStore : IOrbitPasswordCredentialStore
{
    private const int FormatVersion = 1;
    private const int SaltSize = 16;
    private const int VerifierSize = 32;

    private static readonly ProfileStorageNamespace StorageNamespace =
        ProfileStorageNamespace.Create("privacy.orbit-password").Value!;
    private static readonly ProfileStorageKey StorageKey =
        ProfileStorageKey.Create("verifier-v1").Value!;

    private readonly IProfileStorage _storage;
    private readonly IWindowsKeyProtection _keyProtection;
    private readonly IClock _clock;
    private readonly OrbitPasswordKdfOptions _kdf;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public ProtectedOrbitPasswordCredentialStore(
        IProfileStorage storage,
        IWindowsKeyProtection keyProtection,
        IClock clock,
        OrbitPasswordKdfOptions? kdf = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _keyProtection = keyProtection ?? throw new ArgumentNullException(nameof(keyProtection));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _kdf = kdf ?? OrbitPasswordKdfOptions.Default;
        if (!_kdf.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(kdf));
        }
    }

    public async ValueTask<ControllerResult<OrbitPasswordVaultState>> GetStateAsync(
        PrivacyContext context,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(context, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return ControllerResult<OrbitPasswordVaultState>.Failure(loaded.Error!);
        }

        using var state = loaded.Value!;
        return ControllerResult<OrbitPasswordVaultState>.Success(ToContractState(context.ProfileId, state.Record));
    }

    public async ValueTask<ControllerResult<OrbitPasswordVerification>> VerifyAsync(
        PrivacyContext context,
        SensitiveOrbitPassword password,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(password);
        var loaded = await LoadAsync(context, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return ControllerResult<OrbitPasswordVerification>.Failure(loaded.Error!);
        }

        using var state = loaded.Value!;
        if (state.Record is null)
        {
            return ControllerResult<OrbitPasswordVerification>.Success(new(false, false));
        }

        var candidate = new byte[VerifierSize];
        try
        {
            Rfc2898DeriveBytes.Pbkdf2(
                password.Characters,
                state.Record.Salt,
                candidate,
                state.Record.Iterations,
                HashAlgorithmName.SHA256);
            return ControllerResult<OrbitPasswordVerification>.Success(new(
                true,
                CryptographicOperations.FixedTimeEquals(candidate, state.Record.Verifier)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(candidate);
        }
    }

    public async ValueTask<ControllerResult<OrbitPasswordVaultState>> SetAsync(
        PrivacyContext context,
        SensitiveOrbitPassword password,
        OrbitPasswordCredentialWriteMode mode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (!Valid(context) || !Enum.IsDefined(mode) ||
            password.Characters.Length is < OrbitPasswordSecretLeaseStore.MinimumPasswordCharacters or
                > OrbitPasswordSecretLeaseStore.MaximumPasswordCharacters)
        {
            return Failure<OrbitPasswordVaultState>(ControllerErrorCode.InvalidRequest, "error.orbit-password.set-invalid");
        }
        if (context.IsPrivate)
        {
            return Failure<OrbitPasswordVaultState>(
                ControllerErrorCode.PolicyDenied,
                "error.orbit-password.private-persistent-denied");
        }

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await LoadCoreAsync(context, cancellationToken).ConfigureAwait(false);
            if (!loaded.IsSuccess)
            {
                return ControllerResult<OrbitPasswordVaultState>.Failure(loaded.Error!);
            }

            using var state = loaded.Value!;
            if ((mode == OrbitPasswordCredentialWriteMode.RequireAbsent && state.Record is not null) ||
                (mode == OrbitPasswordCredentialWriteMode.RequirePresent && state.Record is null))
            {
                return Failure<OrbitPasswordVaultState>(ControllerErrorCode.Conflict, "error.orbit-password.state-conflict");
            }

            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            var verifier = new byte[VerifierSize];
            try
            {
                Rfc2898DeriveBytes.Pbkdf2(
                    password.Characters,
                    salt,
                    verifier,
                    _kdf.Iterations,
                    HashAlgorithmName.SHA256);
                using var record = new CredentialRecord(salt, verifier, _kdf.Iterations, _clock.UtcNow);
                salt = [];
                verifier = [];
                var saved = await SaveAsync(context, state.Revision, record, cancellationToken).ConfigureAwait(false);
                if (!saved.IsSuccess)
                {
                    return ControllerResult<OrbitPasswordVaultState>.Failure(saved.Error!);
                }

                return ControllerResult<OrbitPasswordVaultState>.Success(
                    ToContractState(context.ProfileId, record));
            }
            finally
            {
                if (salt.Length > 0) CryptographicOperations.ZeroMemory(salt);
                if (verifier.Length > 0) CryptographicOperations.ZeroMemory(verifier);
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<ControllerResult<OrbitPasswordVaultState>> DeleteAsync(
        PrivacyContext context,
        CancellationToken cancellationToken)
    {
        if (!Valid(context))
        {
            return Failure<OrbitPasswordVaultState>(ControllerErrorCode.InvalidRequest, "error.orbit-password.context-invalid");
        }
        if (context.IsPrivate)
        {
            return Failure<OrbitPasswordVaultState>(
                ControllerErrorCode.PolicyDenied,
                "error.orbit-password.private-persistent-denied");
        }

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await LoadCoreAsync(context, cancellationToken).ConfigureAwait(false);
            if (!loaded.IsSuccess)
            {
                return ControllerResult<OrbitPasswordVaultState>.Failure(loaded.Error!);
            }

            using var state = loaded.Value!;
            if (state.Record is not null)
            {
                var deleted = await _storage.DeleteAsync(Address(context), state.Revision, cancellationToken)
                    .ConfigureAwait(false);
                if (!deleted.IsSuccess && deleted.Error?.Code != ControllerErrorCode.NotFound)
                {
                    return ControllerResult<OrbitPasswordVaultState>.Failure(deleted.Error!);
                }
            }

            return ControllerResult<OrbitPasswordVaultState>.Success(ToContractState(context.ProfileId, null));
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async ValueTask<ControllerResult<LoadedState>> LoadAsync(
        PrivacyContext context,
        CancellationToken cancellationToken)
    {
        if (!Valid(context))
        {
            return Failure<LoadedState>(ControllerErrorCode.InvalidRequest, "error.orbit-password.context-invalid");
        }

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadCoreAsync(context, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async ValueTask<ControllerResult<LoadedState>> LoadCoreAsync(
        PrivacyContext context,
        CancellationToken cancellationToken)
    {
        var read = await _storage.ReadAsync(Address(context), cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            return read.Error?.Code == ControllerErrorCode.NotFound
                ? ControllerResult<LoadedState>.Success(new LoadedState(null, null))
                : ControllerResult<LoadedState>.Failure(read.Error!);
        }

        var blobResult = DeserializeProtectedBlob(read.Value!.Payload.Span);
        if (!blobResult.IsSuccess)
        {
            return ControllerResult<LoadedState>.Failure(blobResult.Error!);
        }

        var unprotected = await _keyProtection.UnprotectAsync(
            new UnprotectKeyRequest(StorageContext(context), WindowsKeyProtectionPurpose.LocalPasswordVaultKey, blobResult.Value!),
            cancellationToken).ConfigureAwait(false);
        if (!unprotected.IsSuccess)
        {
            return ControllerResult<LoadedState>.Failure(unprotected.Error!);
        }

        using var material = unprotected.Value!;
        var record = DeserializeRecord(material.Bytes.Span);
        return record.IsSuccess
            ? ControllerResult<LoadedState>.Success(new LoadedState(record.Value, read.Value.Revision))
            : ControllerResult<LoadedState>.Failure(record.Error!);
    }

    private async ValueTask<ControllerResult> SaveAsync(
        PrivacyContext context,
        ProfileStorageRevision? expectedRevision,
        CredentialRecord record,
        CancellationToken cancellationToken)
    {
        var clear = SerializeRecord(record);
        try
        {
            var protectedResult = await _keyProtection.ProtectAsync(
                new ProtectKeyRequest(StorageContext(context), WindowsKeyProtectionPurpose.LocalPasswordVaultKey, clear),
                cancellationToken).ConfigureAwait(false);
            if (!protectedResult.IsSuccess)
            {
                return ControllerResult.Failure(protectedResult.Error!);
            }

            var payload = SerializeProtectedBlob(protectedResult.Value!);
            try
            {
                var write = ProfileStorageWriteRequest.Create(Address(context), payload, expectedRevision);
                if (!write.IsSuccess)
                {
                    return ControllerResult.Failure(write.Error!);
                }

                var saved = await _storage.WriteAsync(write.Value!, cancellationToken).ConfigureAwait(false);
                return saved.IsSuccess ? ControllerResult.Success() : ControllerResult.Failure(saved.Error!);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    private static byte[] SerializeRecord(CredentialRecord record)
    {
        var bytes = new byte[4 + 4 + 8 + 4 + record.Salt.Length + 4 + record.Verifier.Length];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(span, FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], record.Iterations);
        BinaryPrimitives.WriteInt64LittleEndian(span[8..], record.ChangedAtUtc.ToUnixTimeMilliseconds());
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], record.Salt.Length);
        record.Salt.CopyTo(span[20..]);
        var verifierOffset = 20 + record.Salt.Length;
        BinaryPrimitives.WriteInt32LittleEndian(span[verifierOffset..], record.Verifier.Length);
        record.Verifier.CopyTo(span[(verifierOffset + 4)..]);
        return bytes;
    }

    private static ControllerResult<CredentialRecord> DeserializeRecord(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 24 ||
            BinaryPrimitives.ReadInt32LittleEndian(bytes) != FormatVersion)
        {
            return Integrity<CredentialRecord>();
        }

        var iterations = BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]);
        var changed = BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]);
        var saltLength = BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]);
        if (iterations < OrbitPasswordKdfOptions.MinimumIterations || saltLength != SaltSize || bytes.Length < 24 + saltLength)
        {
            return Integrity<CredentialRecord>();
        }

        var verifierOffset = 20 + saltLength;
        var verifierLength = BinaryPrimitives.ReadInt32LittleEndian(bytes[verifierOffset..]);
        if (verifierLength != VerifierSize || bytes.Length != verifierOffset + 4 + verifierLength)
        {
            return Integrity<CredentialRecord>();
        }

        try
        {
            return ControllerResult<CredentialRecord>.Success(new CredentialRecord(
                bytes.Slice(20, saltLength).ToArray(),
                bytes.Slice(verifierOffset + 4, verifierLength).ToArray(),
                iterations,
                DateTimeOffset.FromUnixTimeMilliseconds(changed)));
        }
        catch (ArgumentOutOfRangeException)
        {
            return Integrity<CredentialRecord>();
        }
    }

    private static byte[] SerializeProtectedBlob(ProtectedKeyBlob blob)
    {
        var format = Encoding.ASCII.GetBytes(blob.Format);
        var bytes = new byte[4 + format.Length + 4 + blob.Bytes.Length];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, format.Length);
        format.CopyTo(bytes.AsSpan(4));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4 + format.Length), blob.Bytes.Length);
        blob.Bytes.Span.CopyTo(bytes.AsSpan(8 + format.Length));
        return bytes;
    }

    private static ControllerResult<ProtectedKeyBlob> DeserializeProtectedBlob(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 9)
        {
            return Integrity<ProtectedKeyBlob>();
        }

        var formatLength = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        if (formatLength is < 1 or > 64 || bytes.Length < 8 + formatLength)
        {
            return Integrity<ProtectedKeyBlob>();
        }

        var blobLength = BinaryPrimitives.ReadInt32LittleEndian(bytes[(4 + formatLength)..]);
        if (blobLength < 1 || bytes.Length != 8 + formatLength + blobLength)
        {
            return Integrity<ProtectedKeyBlob>();
        }

        var format = Encoding.ASCII.GetString(bytes.Slice(4, formatLength));
        var created = ProtectedKeyBlob.Create(format, bytes.Slice(8 + formatLength, blobLength));
        return created.IsSuccess ? created : Integrity<ProtectedKeyBlob>();
    }

    private static ProfileStorageAddress Address(PrivacyContext context) =>
        ProfileStorageAddress.Create(
            StorageContext(context),
            StorageNamespace,
            StorageKey,
            ProfileStorageDurability.Persistent).Value!;

    private static PrivacyContext StorageContext(PrivacyContext context) =>
        new(context.ProfileId, context.SessionId, BrowserProfileMode.Normal);

    private static bool Valid(PrivacyContext? context) => context is { IsStructurallyValid: true };

    private static OrbitPasswordVaultState ToContractState(ProfileId profileId, CredentialRecord? record) =>
        new(
            profileId,
            record is null ? OrbitPasswordVaultStatus.NotConfigured : OrbitPasswordVaultStatus.Configured,
            OrbitPasswordVaultDataDisposition.LocalOnlyNeverSync,
            record?.ChangedAtUtc);

    private static ControllerResult<T> Failure<T>(ControllerErrorCode code, string key) where T : class =>
        ControllerResult<T>.Failure(ControllerError.Create(code, key));

    private static ControllerResult<T> Integrity<T>() where T : class =>
        Failure<T>(ControllerErrorCode.IntegrityFailure, "error.orbit-password.credential-integrity");

    private sealed class LoadedState : IDisposable
    {
        public LoadedState(CredentialRecord? record, ProfileStorageRevision? revision)
        {
            Record = record;
            Revision = revision;
        }

        public CredentialRecord? Record { get; }
        public ProfileStorageRevision? Revision { get; }
        public void Dispose() => Record?.Dispose();
    }

    private sealed class CredentialRecord : IDisposable
    {
        private byte[]? _salt;
        private byte[]? _verifier;

        public CredentialRecord(byte[] salt, byte[] verifier, int iterations, DateTimeOffset changedAtUtc)
        {
            _salt = salt;
            _verifier = verifier;
            Iterations = iterations;
            ChangedAtUtc = changedAtUtc;
        }

        public ReadOnlySpan<byte> Salt => _salt ?? throw new ObjectDisposedException(nameof(CredentialRecord));
        public ReadOnlySpan<byte> Verifier => _verifier ?? throw new ObjectDisposedException(nameof(CredentialRecord));
        public int Iterations { get; }
        public DateTimeOffset ChangedAtUtc { get; }

        public void Dispose()
        {
            var salt = Interlocked.Exchange(ref _salt, null);
            var verifier = Interlocked.Exchange(ref _verifier, null);
            if (salt is not null) CryptographicOperations.ZeroMemory(salt);
            if (verifier is not null) CryptographicOperations.ZeroMemory(verifier);
        }
    }
}
