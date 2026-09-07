using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Sync;

namespace OrbitNavigator.Sync.Accounts;

internal sealed class DpapiMyOrbitCredentialVault : IMyOrbitCredentialVault
{
    private const uint OuterMagic = 0x56434F4D; // MOCV
    private const uint InnerMagic = 0x52434F4D; // MOCR
    private const int FormatVersion = 1;
    private const int MaximumPayloadBytes = 16 * 1024;
    private const int MaximumLabelBytes = 320;
    private const int MaximumCredentialBytes = 256;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly ProfileStorageNamespace StorageNamespace =
        ProfileStorageNamespace.Create("account.connection").Value!;
    private static readonly ProfileStorageKey StorageKey =
        ProfileStorageKey.Create("my-orbit-link").Value!;
    private readonly IProfileStorage _storage;
    private readonly IWindowsKeyProtection _protection;

    public DpapiMyOrbitCredentialVault(IProfileStorage storage, IWindowsKeyProtection protection)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _protection = protection ?? throw new ArgumentNullException(nameof(protection));
    }

    public async ValueTask<ControllerResult<StoredMyOrbitCredential>> LoadAsync(
        SyncOperationContext context,
        CancellationToken cancellationToken)
    {
        var address = Address(context);
        if (!address.IsSuccess)
            return ControllerResult<StoredMyOrbitCredential>.Failure(address.Error!);
        var read = await _storage.ReadAsync(address.Value!, cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
            return ControllerResult<StoredMyOrbitCredential>.Failure(read.Error!);
        var blob = DecodeOuter(read.Value!.Payload.Span);
        if (!blob.IsSuccess)
            return ControllerResult<StoredMyOrbitCredential>.Failure(blob.Error!);
        var clear = await _protection.UnprotectAsync(new(
            context.Browsing.Privacy,
            WindowsKeyProtectionPurpose.MyOrbitConnectionCredential,
            blob.Value!), cancellationToken).ConfigureAwait(false);
        if (!clear.IsSuccess)
            return ControllerResult<StoredMyOrbitCredential>.Failure(clear.Error!);
        using var material = clear.Value!;
        return DecodeInner(material.Bytes.Span, read.Value.Revision);
    }

    public async ValueTask<ControllerResult<StoredMyOrbitCredential>> SaveAsync(
        SyncOperationContext context,
        StoredMyOrbitCredential? expected,
        MyOrbitTokenSet tokens,
        bool revocationPending,
        CancellationToken cancellationToken)
    {
        if (tokens is null || tokens.ConnectionId.IsEmpty ||
            tokens.RefreshCredential.Bytes.IsEmpty ||
            (!revocationPending && tokens.RefreshExpiresAtUtc <= tokens.IssuedAtUtc))
            return Invalid<StoredMyOrbitCredential>();
        var address = Address(context);
        if (!address.IsSuccess)
            return ControllerResult<StoredMyOrbitCredential>.Failure(address.Error!);
        var authorizedAtUtc = expected?.AuthorizedAtUtc ?? tokens.IssuedAtUtc;
        var inner = EncodeInner(tokens, authorizedAtUtc, revocationPending);
        if (!inner.IsSuccess)
            return ControllerResult<StoredMyOrbitCredential>.Failure(inner.Error!);
        byte[]? outer = null;
        try
        {
            var protectedResult = await _protection.ProtectAsync(new(
                context.Browsing.Privacy,
                WindowsKeyProtectionPurpose.MyOrbitConnectionCredential,
                inner.Value!), cancellationToken).ConfigureAwait(false);
            if (!protectedResult.IsSuccess)
                return ControllerResult<StoredMyOrbitCredential>.Failure(protectedResult.Error!);
            outer = EncodeOuter(protectedResult.Value!);
            var request = ProfileStorageWriteRequest.Create(
                address.Value,
                outer,
                expected?.StorageRevision);
            if (!request.IsSuccess)
                return ControllerResult<StoredMyOrbitCredential>.Failure(request.Error!);
            var write = await _storage.WriteAsync(request.Value!, cancellationToken).ConfigureAwait(false);
            if (!write.IsSuccess)
                return ControllerResult<StoredMyOrbitCredential>.Failure(write.Error!);
            return ControllerResult<StoredMyOrbitCredential>.Success(new(
                tokens.RefreshCredential.Clone(),
                tokens.ConnectionId,
                tokens.AccountLabel,
                expected?.AuthorizedAtUtc ?? tokens.IssuedAtUtc,
                tokens.IssuedAtUtc,
                tokens.RefreshExpiresAtUtc,
                revocationPending,
                write.Value!.Revision));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(inner.Value!);
            if (outer is not null) CryptographicOperations.ZeroMemory(outer);
        }
    }

    public async ValueTask<ControllerResult<StoredMyOrbitCredential>> MarkRevocationPendingAsync(
        SyncOperationContext context,
        StoredMyOrbitCredential expected,
        CancellationToken cancellationToken)
    {
        if (expected is null)
            return Invalid<StoredMyOrbitCredential>();
        using var accessPlaceholder = new SensitiveUtf8Buffer(
            "moat_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"u8);
        using var refresh = expected.RefreshCredential.Clone();
        using var tokenSet = new MyOrbitTokenSet(
            accessPlaceholder.Clone(),
            refresh.Clone(),
            expected.ConnectionId,
            expected.AccountLabel,
            expected.LastRefreshedAtUtc.AddMinutes(1),
            expected.RefreshExpiresAtUtc,
            expected.LastRefreshedAtUtc);
        return await SaveAsync(context, expected, tokenSet, true, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ControllerResult> DeleteAsync(
        SyncOperationContext context,
        StoredMyOrbitCredential expected,
        CancellationToken cancellationToken)
    {
        if (expected is null)
            return ControllerResult.Failure(InvalidError());
        var address = Address(context);
        return address.IsSuccess
            ? await _storage.DeleteAsync(address.Value!, expected.StorageRevision, cancellationToken)
                .ConfigureAwait(false)
            : ControllerResult.Failure(address.Error!);
    }

    private static ControllerResult<byte[]> EncodeInner(
        MyOrbitTokenSet tokens,
        DateTimeOffset authorizedAtUtc,
        bool revocationPending)
    {
        var label = MyOrbitAuthorizationProtocol.SafeAccountLabel(tokens.AccountLabel);
        var labelBytes = label is null ? [] : StrictUtf8.GetBytes(label);
        var credential = tokens.RefreshCredential.Bytes;
        if (labelBytes.Length > MaximumLabelBytes || credential.Length > MaximumCredentialBytes)
            return Invalid<byte[]>();
        var payload = new byte[4 + 4 + 16 + 1 + 8 + 8 + 8 + 4 + 4 + labelBytes.Length + credential.Length];
        var offset = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset, 4), InnerMagic); offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset, 4), FormatVersion); offset += 4;
        tokens.ConnectionId.Value.TryWriteBytes(payload.AsSpan(offset, 16)); offset += 16;
        payload[offset++] = revocationPending ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(offset, 8), authorizedAtUtc.UtcTicks); offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(offset, 8), tokens.IssuedAtUtc.UtcTicks); offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(offset, 8), tokens.RefreshExpiresAtUtc.UtcTicks); offset += 8;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset, 4), labelBytes.Length); offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset, 4), credential.Length); offset += 4;
        labelBytes.CopyTo(payload, offset); offset += labelBytes.Length;
        credential.Span.CopyTo(payload.AsSpan(offset));
        CryptographicOperations.ZeroMemory(labelBytes);
        return ControllerResult<byte[]>.Success(payload);
    }

    private static ControllerResult<StoredMyOrbitCredential> DecodeInner(
        ReadOnlySpan<byte> payload,
        ProfileStorageRevision storageRevision)
    {
        const int fixedSize = 4 + 4 + 16 + 1 + 8 + 8 + 8 + 4 + 4;
        if (payload.Length < fixedSize || payload.Length > MaximumPayloadBytes)
            return Integrity<StoredMyOrbitCredential>();
        try
        {
            var offset = 0;
            var magic = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset, 4)); offset += 4;
            var version = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset, 4)); offset += 4;
            var connection = new DeviceId(new Guid(payload.Slice(offset, 16))); offset += 16;
            var pendingByte = payload[offset++];
            var authorized = new DateTimeOffset(BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(offset, 8)), TimeSpan.Zero); offset += 8;
            var refreshed = new DateTimeOffset(BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(offset, 8)), TimeSpan.Zero); offset += 8;
            var expires = new DateTimeOffset(BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(offset, 8)), TimeSpan.Zero); offset += 8;
            var labelLength = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset, 4)); offset += 4;
            var credentialLength = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset, 4)); offset += 4;
            if (magic != InnerMagic || version != FormatVersion || connection.IsEmpty || pendingByte > 1 ||
                labelLength is < 0 or > MaximumLabelBytes ||
                credentialLength is < 1 or > MaximumCredentialBytes ||
                offset + labelLength + credentialLength != payload.Length ||
                refreshed < authorized || expires <= refreshed)
                return Integrity<StoredMyOrbitCredential>();
            var label = labelLength == 0 ? null : StrictUtf8.GetString(payload.Slice(offset, labelLength));
            offset += labelLength;
            if (label != MyOrbitAuthorizationProtocol.SafeAccountLabel(label))
                return Integrity<StoredMyOrbitCredential>();
            var credential = new SensitiveUtf8Buffer(payload.Slice(offset, credentialLength));
            return ControllerResult<StoredMyOrbitCredential>.Success(new(
                credential,
                connection,
                label,
                authorized,
                refreshed,
                expires,
                pendingByte == 1,
                storageRevision));
        }
        catch (Exception exception) when (exception is ArgumentException or DecoderFallbackException)
        {
            return Integrity<StoredMyOrbitCredential>();
        }
    }

    private static byte[] EncodeOuter(ProtectedKeyBlob blob)
    {
        var format = Encoding.ASCII.GetBytes(blob.Format);
        var payload = new byte[4 + 4 + 4 + 4 + format.Length + blob.Bytes.Length];
        var offset = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset, 4), OuterMagic); offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset, 4), FormatVersion); offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset, 4), format.Length); offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset, 4), blob.Bytes.Length); offset += 4;
        format.CopyTo(payload, offset); offset += format.Length;
        blob.Bytes.Span.CopyTo(payload.AsSpan(offset));
        return payload;
    }

    private static ControllerResult<ProtectedKeyBlob> DecodeOuter(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 17 || payload.Length > MaximumPayloadBytes)
            return Integrity<ProtectedKeyBlob>();
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(payload[..4]);
        var version = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4, 4));
        var formatLength = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(8, 4));
        var blobLength = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(12, 4));
        if (magic != OuterMagic || version != FormatVersion ||
            formatLength is < 1 or > 64 || blobLength < 1 ||
            16 + formatLength + blobLength != payload.Length)
            return Integrity<ProtectedKeyBlob>();
        var format = Encoding.ASCII.GetString(payload.Slice(16, formatLength));
        var created = ProtectedKeyBlob.Create(format, payload.Slice(16 + formatLength, blobLength));
        return created.IsSuccess ? created : Integrity<ProtectedKeyBlob>();
    }

    private static ControllerResult<ProfileStorageAddress> Address(SyncOperationContext context)
    {
        if (context is null)
            return Invalid<ProfileStorageAddress>();
        var normal = NormalProfileOperationGuard.RequireNormal(context.Browsing);
        return normal.IsSuccess
            ? ProfileStorageAddress.Create(
                context.Browsing.Privacy,
                StorageNamespace,
                StorageKey,
                ProfileStorageDurability.Persistent)
            : ControllerResult<ProfileStorageAddress>.Failure(normal.Error!);
    }

    private static ControllerError InvalidError() => ControllerError.Create(
        ControllerErrorCode.InvalidRequest,
        "account.link.credential-invalid");

    private static ControllerResult<T> Invalid<T>() where T : class =>
        ControllerResult<T>.Failure(InvalidError());

    private static ControllerResult<T> Integrity<T>() where T : class =>
        ControllerResult<T>.Failure(ControllerError.Create(
            ControllerErrorCode.IntegrityFailure,
            "account.link.credential-corrupt"));
}
