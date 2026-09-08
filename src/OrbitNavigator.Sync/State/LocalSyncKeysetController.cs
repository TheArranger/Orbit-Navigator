using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Sync;
using OrbitNavigator.Sync.Cryptography;
using OrbitNavigator.Sync.Recovery;

namespace OrbitNavigator.Sync.State;

/// <summary>
/// UI-safe description of the active local sync keyset. The root key never leaves
/// the in-process key registry; callers receive only its opaque handle.
/// </summary>
public sealed record LocalSyncKeysetSession(
    ProfileId ProfileId,
    ProfileId SyncProfileId,
    SyncKeysetId KeysetId,
    long KeyEpoch,
    SyncKeyMaterialHandle KeyMaterial);

/// <summary>
/// Creates or restores the profile's sync root key. Clear key material is protected
/// with the Windows CurrentUser adapter before profile storage is called and is
/// zeroed immediately after registration in the opaque in-process registry.
/// </summary>
public sealed class LocalSyncKeysetController : IAsyncDisposable
{
    private const uint OuterMagic = 0x4B534E4F; // ONSK
    private const uint InnerMagic = 0x524B4E4F; // ONKR
    private const int OuterFormatVersion = 1;
    private const int LegacyFormatVersion = 1;
    private const int FormatVersion = 2;
    private const int RootKeySizeBytes = InProcessSyncKeyMaterialRegistry.RootKeySizeBytes;
    private const int LegacyInnerSizeBytes = 4 + 4 + 16 + 16 + 8 + RootKeySizeBytes;
    private const int InnerSizeBytes = 4 + 4 + 16 + 16 + 16 + 8 + RootKeySizeBytes;
    private const int MaximumOuterSizeBytes = 16 * 1024;

    private static readonly ProfileStorageNamespace StorageNamespace =
        ProfileStorageNamespace.Create("sync.keysets").Value!;
    private static readonly ProfileStorageKey StorageKey =
        ProfileStorageKey.Create("active-root-key").Value!;
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> ProfileGates = new();

    private readonly IProfileStorage _storage;
    private readonly IWindowsKeyProtection _protection;
    private readonly InProcessSyncKeyMaterialRegistry _registry;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<ProfileId, LocalSyncKeysetSession> _sessions = [];
    private int _disposed;

    public LocalSyncKeysetController(
        IProfileStorage storage,
        IWindowsKeyProtection protection,
        InProcessSyncKeyMaterialRegistry registry)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _protection = protection ?? throw new ArgumentNullException(nameof(protection));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public ValueTask<ControllerResult<LocalSyncKeysetSession>> InitializeAsync(
        BrowsingContext browsing,
        SyncOperationId operationId,
        CancellationToken cancellationToken = default) =>
        SyncOperationGate.ExecuteAsync(
            browsing,
            operationId,
            context => InitializeAuthorizedAsync(context, cancellationToken));

    public ValueTask<ControllerResult<LocalSyncKeysetSession>> ImportRecoveredAsync(
        BrowsingContext browsing,
        SyncOperationId operationId,
        ProfileId syncProfileId,
        SyncKeysetId keysetId,
        long keyEpoch,
        UnwrappedSyncRootKey rootKey,
        CancellationToken cancellationToken = default) =>
        SyncOperationGate.ExecuteAsync(
            browsing,
            operationId,
            context => ImportRecoveredAuthorizedAsync(
                context, syncProfileId, keysetId, keyEpoch, rootKey, cancellationToken));

    public ValueTask<ControllerResult<WrappedSyncKeyset>> WrapForRecoveryAsync(
        BrowsingContext browsing,
        SyncOperationId operationId,
        LocalSyncKeysetSession session,
        RecoveryCodeLeaseId recoveryCodeLease,
        RecoveryCodeKeyWrapper wrapper,
        CancellationToken cancellationToken = default) =>
        SyncOperationGate.ExecuteAsync(
            browsing,
            operationId,
            context => WrapForRecoveryAuthorizedAsync(
                context, session, recoveryCodeLease, wrapper, cancellationToken));

    private async ValueTask<ControllerResult<LocalSyncKeysetSession>> InitializeAuthorizedAsync(
        SyncOperationContext context,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return Unavailable<LocalSyncKeysetSession>();

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled<LocalSyncKeysetSession>();
        }

        try
        {
            if (Volatile.Read(ref _disposed) != 0)
                return Unavailable<LocalSyncKeysetSession>();

            var profileId = context.Browsing.Privacy.ProfileId;
            if (_sessions.TryGetValue(profileId, out var active))
                return ControllerResult<LocalSyncKeysetSession>.Success(active);

            var profileGate = ProfileGates.GetOrAdd(profileId.Value, static _ => new SemaphoreSlim(1, 1));
            await profileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var loaded = await LoadOrCreateAsync(context, cancellationToken).ConfigureAwait(false);
                if (!loaded.IsSuccess)
                    return loaded;

                if (Volatile.Read(ref _disposed) != 0)
                {
                    _registry.Remove(loaded.Value!.KeyMaterial);
                    return Unavailable<LocalSyncKeysetSession>();
                }

                _sessions.Add(profileId, loaded.Value!);
                return loaded;
            }
            finally
            {
                profileGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled<LocalSyncKeysetSession>();
        }
        catch (Exception exception) when (exception is CryptographicException or ObjectDisposedException)
        {
            return Unavailable<LocalSyncKeysetSession>();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<ControllerResult<LocalSyncKeysetSession>> ImportRecoveredAuthorizedAsync(
        SyncOperationContext context,
        ProfileId syncProfileId,
        SyncKeysetId keysetId,
        long keyEpoch,
        UnwrappedSyncRootKey rootKey,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return Unavailable<LocalSyncKeysetSession>();
        if (syncProfileId.IsEmpty || !keysetId.IsDefined || keyEpoch < 0 ||
            rootKey is null || rootKey.IsDisposed)
            return Invalid<LocalSyncKeysetSession>();

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled<LocalSyncKeysetSession>();
        }

        try
        {
            var localProfileId = context.Browsing.Privacy.ProfileId;
            if (Volatile.Read(ref _disposed) != 0)
                return Unavailable<LocalSyncKeysetSession>();
            if (_sessions.ContainsKey(localProfileId))
                return Conflict<LocalSyncKeysetSession>();

            var profileGate = ProfileGates.GetOrAdd(
                localProfileId.Value,
                static _ => new SemaphoreSlim(1, 1));
            await profileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var address = Address(context);
                if (!address.IsSuccess)
                    return ControllerResult<LocalSyncKeysetSession>.Failure(address.Error!);
                var existing = await _storage.ReadAsync(address.Value!, cancellationToken).ConfigureAwait(false);
                if (existing.IsSuccess)
                    return Conflict<LocalSyncKeysetSession>();
                if (existing.Error!.Code != ControllerErrorCode.NotFound)
                    return ControllerResult<LocalSyncKeysetSession>.Failure(existing.Error);

                var inner = new byte[InnerSizeBytes];
                byte[]? outer = null;
                try
                {
                    EncodeInner(inner, localProfileId, syncProfileId, keysetId, keyEpoch);
                    rootKey.CopyTo(inner.AsSpan(InnerSizeBytes - RootKeySizeBytes));
                    var protectedResult = await _protection.ProtectAsync(
                        new ProtectKeyRequest(
                            context.Browsing.Privacy,
                            WindowsKeyProtectionPurpose.SyncKeysetWrappingKey,
                            inner),
                        cancellationToken).ConfigureAwait(false);
                    if (!protectedResult.IsSuccess)
                        return ControllerResult<LocalSyncKeysetSession>.Failure(protectedResult.Error!);

                    outer = EncodeOuter(protectedResult.Value!);
                    var writeRequest = ProfileStorageWriteRequest.Create(address.Value!, outer);
                    if (!writeRequest.IsSuccess)
                        return ControllerResult<LocalSyncKeysetSession>.Failure(writeRequest.Error!);
                    var written = await _storage.WriteAsync(writeRequest.Value!, cancellationToken)
                        .ConfigureAwait(false);
                    if (!written.IsSuccess)
                        return ControllerResult<LocalSyncKeysetSession>.Failure(written.Error!);
                    if (written.Value!.Revision.IsEmpty)
                        return Integrity<LocalSyncKeysetSession>();

                    var handle = _registry.Register(
                        syncProfileId,
                        inner.AsSpan(InnerSizeBytes - RootKeySizeBytes));
                    var session = new LocalSyncKeysetSession(
                        localProfileId,
                        syncProfileId,
                        keysetId,
                        keyEpoch,
                        handle);
                    _sessions.Add(localProfileId, session);
                    return ControllerResult<LocalSyncKeysetSession>.Success(session);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(inner);
                    if (outer is not null)
                        CryptographicOperations.ZeroMemory(outer);
                }
            }
            finally
            {
                profileGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled<LocalSyncKeysetSession>();
        }
        catch (Exception exception) when (exception is CryptographicException or ObjectDisposedException)
        {
            return Unavailable<LocalSyncKeysetSession>();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<ControllerResult<WrappedSyncKeyset>> WrapForRecoveryAuthorizedAsync(
        SyncOperationContext context,
        LocalSyncKeysetSession session,
        RecoveryCodeLeaseId recoveryCodeLease,
        RecoveryCodeKeyWrapper wrapper,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return Unavailable<WrappedSyncKeyset>();
        if (session is null || wrapper is null || !recoveryCodeLease.IsDefined ||
            session.ProfileId != context.Browsing.Privacy.ProfileId ||
            session.SyncProfileId.IsEmpty || !session.KeysetId.IsDefined ||
            session.KeyEpoch < 0 || session.KeyMaterial.Value == Guid.Empty)
            return Invalid<WrappedSyncKeyset>();

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled<WrappedSyncKeyset>();
        }

        try
        {
            if (!_sessions.TryGetValue(session.ProfileId, out var active) || active != session)
                return Conflict<WrappedSyncKeyset>();
            return _registry.TryUseRootKey(
                session.KeyMaterial,
                session.SyncProfileId,
                rootKey => wrapper.Wrap(
                    session.SyncProfileId,
                    session.KeysetId,
                    session.KeyEpoch,
                    rootKey,
                    recoveryCodeLease),
                out ControllerResult<WrappedSyncKeyset>? wrapped) && wrapped is not null
                ? wrapped
                : Unavailable<WrappedSyncKeyset>();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<ControllerResult<LocalSyncKeysetSession>> LoadOrCreateAsync(
        SyncOperationContext context,
        CancellationToken cancellationToken)
    {
        var address = Address(context);
        if (!address.IsSuccess)
            return ControllerResult<LocalSyncKeysetSession>.Failure(address.Error!);

        var read = await _storage.ReadAsync(address.Value!, cancellationToken).ConfigureAwait(false);
        if (read.IsSuccess)
            return await RestoreAsync(context, read.Value!, cancellationToken).ConfigureAwait(false);
        if (read.Error!.Code != ControllerErrorCode.NotFound)
            return ControllerResult<LocalSyncKeysetSession>.Failure(read.Error);

        return await CreateAsync(context, address.Value!, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ControllerResult<LocalSyncKeysetSession>> CreateAsync(
        SyncOperationContext context,
        ProfileStorageAddress address,
        CancellationToken cancellationToken)
    {
        var profileId = context.Browsing.Privacy.ProfileId;
        var keysetId = new SyncKeysetId(Guid.NewGuid());
        const long keyEpoch = 0;
        var inner = new byte[InnerSizeBytes];
        byte[]? outer = null;
        try
        {
            EncodeInner(inner, profileId, profileId, keysetId, keyEpoch);
            RandomNumberGenerator.Fill(inner.AsSpan(InnerSizeBytes - RootKeySizeBytes));

            var protectedResult = await _protection.ProtectAsync(
                new ProtectKeyRequest(
                    context.Browsing.Privacy,
                    WindowsKeyProtectionPurpose.SyncKeysetWrappingKey,
                    inner),
                cancellationToken).ConfigureAwait(false);
            if (!protectedResult.IsSuccess)
                return ControllerResult<LocalSyncKeysetSession>.Failure(protectedResult.Error!);

            outer = EncodeOuter(protectedResult.Value!);
            var request = ProfileStorageWriteRequest.Create(address, outer);
            if (!request.IsSuccess)
                return ControllerResult<LocalSyncKeysetSession>.Failure(request.Error!);
            var written = await _storage.WriteAsync(request.Value!, cancellationToken).ConfigureAwait(false);
            if (!written.IsSuccess)
                return ControllerResult<LocalSyncKeysetSession>.Failure(written.Error!);
            if (written.Value!.Revision.IsEmpty)
                return Integrity<LocalSyncKeysetSession>();

            var handle = _registry.Register(profileId, inner.AsSpan(InnerSizeBytes - RootKeySizeBytes));
            return ControllerResult<LocalSyncKeysetSession>.Success(
                new LocalSyncKeysetSession(profileId, profileId, keysetId, keyEpoch, handle));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(inner);
            if (outer is not null)
                CryptographicOperations.ZeroMemory(outer);
        }
    }

    private async ValueTask<ControllerResult<LocalSyncKeysetSession>> RestoreAsync(
        SyncOperationContext context,
        ProfileStorageEntry entry,
        CancellationToken cancellationToken)
    {
        var blob = DecodeOuter(entry.Payload.Span);
        if (!blob.IsSuccess)
            return ControllerResult<LocalSyncKeysetSession>.Failure(blob.Error!);

        var unprotected = await _protection.UnprotectAsync(
            new UnprotectKeyRequest(
                context.Browsing.Privacy,
                WindowsKeyProtectionPurpose.SyncKeysetWrappingKey,
                blob.Value!),
            cancellationToken).ConfigureAwait(false);
        if (!unprotected.IsSuccess)
            return ControllerResult<LocalSyncKeysetSession>.Failure(unprotected.Error!);

        using var material = unprotected.Value!;
        return DecodeAndRegisterInner(
            material.Bytes.Span,
            context.Browsing.Privacy.ProfileId);
    }

    private ControllerResult<LocalSyncKeysetSession> DecodeAndRegisterInner(
        ReadOnlySpan<byte> payload,
        ProfileId expectedProfileId)
    {
        if (payload.Length is not (InnerSizeBytes or LegacyInnerSizeBytes))
            return Integrity<LocalSyncKeysetSession>();

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(payload[..4]);
        var version = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4, 4));
        var profileId = new ProfileId(new Guid(payload.Slice(8, 16)));
        var legacy = version == LegacyFormatVersion && payload.Length == LegacyInnerSizeBytes;
        var syncProfileId = legacy
            ? profileId
            : new ProfileId(new Guid(payload.Slice(24, 16)));
        var keysetOffset = legacy ? 24 : 40;
        var keysetId = new SyncKeysetId(new Guid(payload.Slice(keysetOffset, 16)));
        var keyEpoch = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(keysetOffset + 16, 8));
        if (magic != InnerMagic || !(legacy || version == FormatVersion && payload.Length == InnerSizeBytes) ||
            profileId != expectedProfileId || syncProfileId.IsEmpty ||
            !keysetId.IsDefined || keyEpoch < 0)
        {
            return Integrity<LocalSyncKeysetSession>();
        }

        var handle = _registry.Register(syncProfileId, payload[^RootKeySizeBytes..]);
        return ControllerResult<LocalSyncKeysetSession>.Success(
            new LocalSyncKeysetSession(profileId, syncProfileId, keysetId, keyEpoch, handle));
    }

    private static void EncodeInner(
        Span<byte> destination,
        ProfileId profileId,
        ProfileId syncProfileId,
        SyncKeysetId keysetId,
        long keyEpoch)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination[..4], InnerMagic);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(4, 4), FormatVersion);
        profileId.Value.TryWriteBytes(destination.Slice(8, 16));
        syncProfileId.Value.TryWriteBytes(destination.Slice(24, 16));
        keysetId.Value.TryWriteBytes(destination.Slice(40, 16));
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(56, 8), keyEpoch);
    }

    private static byte[] EncodeOuter(ProtectedKeyBlob blob)
    {
        var format = Encoding.ASCII.GetBytes(blob.Format);
        var result = new byte[16 + format.Length + blob.Bytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0, 4), OuterMagic);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4, 4), OuterFormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8, 4), format.Length);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(12, 4), blob.Bytes.Length);
        format.CopyTo(result, 16);
        blob.Bytes.Span.CopyTo(result.AsSpan(16 + format.Length));
        return result;
    }

    private static ControllerResult<ProtectedKeyBlob> DecodeOuter(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is < 17 or > MaximumOuterSizeBytes)
            return Integrity<ProtectedKeyBlob>();

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(payload[..4]);
        var version = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4, 4));
        var formatLength = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(8, 4));
        var blobLength = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(12, 4));
        if (magic != OuterMagic || version != OuterFormatVersion ||
            formatLength is < 1 or > 64 || blobLength < 1 ||
            16 + formatLength + blobLength != payload.Length)
        {
            return Integrity<ProtectedKeyBlob>();
        }

        try
        {
            var format = Encoding.ASCII.GetString(payload.Slice(16, formatLength));
            var created = ProtectedKeyBlob.Create(
                format,
                payload.Slice(16 + formatLength, blobLength));
            return created.IsSuccess ? created : Integrity<ProtectedKeyBlob>();
        }
        catch (ArgumentException)
        {
            return Integrity<ProtectedKeyBlob>();
        }
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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var session in _sessions.Values)
                _registry.Remove(session.KeyMaterial);
            _sessions.Clear();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private static ControllerResult<T> Integrity<T>() where T : class =>
        ControllerResult<T>.Failure(ControllerError.Create(
            ControllerErrorCode.IntegrityFailure,
            "sync.keyset.state-invalid"));

    private static ControllerResult<T> Unavailable<T>() where T : class =>
        ControllerResult<T>.Failure(ControllerError.Create(
            ControllerErrorCode.Unavailable,
            "sync.keyset.unavailable"));

    private static ControllerResult<T> Cancelled<T>() where T : class =>
        ControllerResult<T>.Failure(ControllerError.Create(
            ControllerErrorCode.Cancelled,
            "sync.keyset.cancelled",
            isRetryable: true));

    private static ControllerResult<T> Invalid<T>() where T : class =>
        ControllerResult<T>.Failure(ControllerError.Create(
            ControllerErrorCode.InvalidRequest,
            "sync.keyset.import-invalid"));

    private static ControllerResult<T> Conflict<T>() where T : class =>
        ControllerResult<T>.Failure(ControllerError.Create(
            ControllerErrorCode.Conflict,
            "sync.keyset.already-initialized"));
}
