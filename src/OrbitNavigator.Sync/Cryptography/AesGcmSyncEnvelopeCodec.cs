using System.Security.Cryptography;
using System.Text;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Sync;

namespace OrbitNavigator.Sync.Cryptography;

/// <summary>
/// Encrypts the sync allowlist with category-separated AES-256-GCM keys.
/// No plaintext fallback is provided: validation, key, or integrity failures are
/// returned as errors and no decrypted value is released.
/// </summary>
public sealed class AesGcmSyncEnvelopeCodec : ISyncEnvelopeCodec
{
    private readonly InProcessSyncKeyMaterialRegistry _keyRegistry;

    public AesGcmSyncEnvelopeCodec(InProcessSyncKeyMaterialRegistry keyRegistry)
    {
        _keyRegistry = keyRegistry ?? throw new ArgumentNullException(nameof(keyRegistry));
    }

    public ValueTask<ControllerResult<EncryptedSyncEnvelope>> EncryptAsync(
        SyncOperationContext context,
        SyncKeyMaterialHandle keyMaterial,
        CanonicalSyncAad aad,
        SyncRecordPayload record,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Cancelled<EncryptedSyncEnvelope>();

        var inputError = ValidateOperation(context, aad, SyncRecordKind.Upsert);
        if (inputError is not null)
            return Failure<EncryptedSyncEnvelope>(inputError);

        var recordValidation = SyncContractRules.ValidateRecord(record);
        if (!recordValidation.IsValid || record.Category != aad.Category || record.EntityId != aad.EntityId)
            return Invalid<EncryptedSyncEnvelope>("sync.crypto.record.invalid");

        byte[]? plaintext = null;
        try
        {
            plaintext = SyncPayloadBinaryCodec.SerializeRecord(record);
            if (plaintext.Length is 0 or > SyncProtocol.MaximumCiphertextSizeBytes)
                return Invalid<EncryptedSyncEnvelope>("sync.crypto.record.size-invalid");

            var encrypted = Encrypt(keyMaterial, aad, plaintext);
            return encrypted.IsSuccess
                ? Success(new EncryptedSyncEnvelope(
                    aad,
                    encrypted.Value!.Nonce,
                    encrypted.Value.Ciphertext,
                    encrypted.Value.AuthenticationTag))
                : Failure<EncryptedSyncEnvelope>(encrypted.Error!);
        }
        catch (Exception exception) when (exception is InvalidDataException or EncoderFallbackException)
        {
            return Invalid<EncryptedSyncEnvelope>("sync.crypto.record.serialization-failed");
        }
        finally
        {
            if (plaintext is not null)
                CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public ValueTask<ControllerResult<SyncRecordPayload>> DecryptAsync(
        SyncOperationContext context,
        SyncKeyMaterialHandle keyMaterial,
        EncryptedSyncEnvelope envelope,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Cancelled<SyncRecordPayload>();
        if (envelope is null)
            return Invalid<SyncRecordPayload>("sync.crypto.envelope.required");

        var envelopeValidation = SyncContractRules.ValidateEnvelope(envelope);
        if (!envelopeValidation.IsValid)
            return Invalid<SyncRecordPayload>("sync.crypto.envelope.invalid");

        var inputError = ValidateOperation(context, envelope.Aad, SyncRecordKind.Upsert);
        if (inputError is not null)
            return Failure<SyncRecordPayload>(inputError);

        var decrypted = Decrypt(
            keyMaterial,
            envelope.Aad,
            envelope.Nonce.Span,
            envelope.Ciphertext.Span,
            envelope.AuthenticationTag.Span);
        if (!decrypted.IsSuccess)
            return Failure<SyncRecordPayload>(decrypted.Error!);

        var plaintext = decrypted.Value!;
        try
        {
            var record = SyncPayloadBinaryCodec.DeserializeRecord(plaintext.Bytes, envelope.Aad.Category);
            var validation = SyncContractRules.ValidateRecord(record);
            if (!validation.IsValid ||
                record.Category != envelope.Aad.Category ||
                record.EntityId != envelope.Aad.EntityId)
            {
                return Integrity<SyncRecordPayload>();
            }

            return Success(record);
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or DecoderFallbackException or ArgumentException)
        {
            return Integrity<SyncRecordPayload>();
        }
        finally
        {
            plaintext.Dispose();
        }
    }

    public ValueTask<ControllerResult<EncryptedSyncTombstone>> EncryptTombstoneAsync(
        SyncOperationContext context,
        SyncKeyMaterialHandle keyMaterial,
        CanonicalSyncAad aad,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Cancelled<EncryptedSyncTombstone>();

        var inputError = ValidateOperation(context, aad, SyncRecordKind.Tombstone);
        if (inputError is not null)
            return Failure<EncryptedSyncTombstone>(inputError);

        var plaintext = SyncPayloadBinaryCodec.SerializeTombstone(aad);
        try
        {
            var encrypted = Encrypt(keyMaterial, aad, plaintext);
            return encrypted.IsSuccess
                ? Success(new EncryptedSyncTombstone(
                    aad,
                    encrypted.Value!.Nonce,
                    encrypted.Value.Ciphertext,
                    encrypted.Value.AuthenticationTag))
                : Failure<EncryptedSyncTombstone>(encrypted.Error!);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public ValueTask<ControllerResult<AuthenticatedSyncTombstoneReceipt>> DecryptAndValidateTombstoneAsync(
        SyncOperationContext context,
        SyncKeyMaterialHandle keyMaterial,
        EncryptedSyncTombstone tombstone,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Cancelled<AuthenticatedSyncTombstoneReceipt>();
        if (tombstone is null)
            return Invalid<AuthenticatedSyncTombstoneReceipt>("sync.crypto.tombstone.required");

        var tombstoneValidation = SyncContractRules.ValidateTombstone(tombstone);
        if (!tombstoneValidation.IsValid)
            return Invalid<AuthenticatedSyncTombstoneReceipt>("sync.crypto.tombstone.invalid");

        var inputError = ValidateOperation(context, tombstone.Aad, SyncRecordKind.Tombstone);
        if (inputError is not null)
            return Failure<AuthenticatedSyncTombstoneReceipt>(inputError);

        var decrypted = Decrypt(
            keyMaterial,
            tombstone.Aad,
            tombstone.Nonce.Span,
            tombstone.Ciphertext.Span,
            tombstone.AuthenticationTag.Span);
        if (!decrypted.IsSuccess)
            return Failure<AuthenticatedSyncTombstoneReceipt>(decrypted.Error!);

        var plaintext = decrypted.Value!;
        try
        {
            var receipt = SyncPayloadBinaryCodec.DeserializeTombstone(plaintext.Bytes, tombstone.Aad);
            return SyncContractRules.ValidateAuthenticatedTombstone(tombstone, receipt).IsValid
                ? Success(receipt)
                : Integrity<AuthenticatedSyncTombstoneReceipt>();
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or ArgumentException)
        {
            return Integrity<AuthenticatedSyncTombstoneReceipt>();
        }
        finally
        {
            plaintext.Dispose();
        }
    }

    public ValueTask<ControllerResult<EncryptedPurgeCommand>> EncryptPurgeAsync(
        SyncOperationContext context,
        SyncKeyMaterialHandle keyMaterial,
        CanonicalSyncAad aad,
        DecryptedPurgeMarker marker,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Cancelled<EncryptedPurgeCommand>();

        var inputError = ValidateOperation(
            context,
            aad,
            SyncRecordKind.Purge,
            requirePurgeOperationBinding: true);
        if (inputError is not null)
            return Failure<EncryptedPurgeCommand>(inputError);
        if (!SyncContractRules.ValidatePurgeMarker(aad, marker).IsValid)
            return Invalid<EncryptedPurgeCommand>("sync.crypto.purge.marker-invalid");

        var plaintext = SyncPayloadBinaryCodec.SerializePurge(marker);
        try
        {
            var encrypted = Encrypt(keyMaterial, aad, plaintext);
            return encrypted.IsSuccess
                ? Success(new EncryptedPurgeCommand(
                    aad,
                    encrypted.Value!.Nonce,
                    encrypted.Value.Ciphertext,
                    encrypted.Value.AuthenticationTag))
                : Failure<EncryptedPurgeCommand>(encrypted.Error!);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public ValueTask<ControllerResult<DecryptedPurgeMarker>> DecryptAndValidatePurgeAsync(
        SyncOperationContext context,
        SyncKeyMaterialHandle keyMaterial,
        EncryptedPurgeCommand command,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Cancelled<DecryptedPurgeMarker>();
        if (command is null)
            return Invalid<DecryptedPurgeMarker>("sync.crypto.purge.required");

        var commandValidation = SyncContractRules.ValidatePurgeCommand(command);
        if (!commandValidation.IsValid)
            return Invalid<DecryptedPurgeMarker>("sync.crypto.purge.invalid");

        var inputError = ValidateOperation(context, command.Aad, SyncRecordKind.Purge);
        if (inputError is not null)
            return Failure<DecryptedPurgeMarker>(inputError);

        var decrypted = Decrypt(
            keyMaterial,
            command.Aad,
            command.Nonce.Span,
            command.Ciphertext.Span,
            command.AuthenticationTag.Span);
        if (!decrypted.IsSuccess)
            return Failure<DecryptedPurgeMarker>(decrypted.Error!);

        var plaintext = decrypted.Value!;
        try
        {
            var marker = SyncPayloadBinaryCodec.DeserializePurge(plaintext.Bytes);
            return SyncContractRules.ValidatePurgeMarker(command.Aad, marker).IsValid
                ? Success(marker)
                : Integrity<DecryptedPurgeMarker>();
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or ArgumentException)
        {
            return Integrity<DecryptedPurgeMarker>();
        }
        finally
        {
            plaintext.Dispose();
        }
    }

    private ControllerResult<EncryptedParts> Encrypt(
        SyncKeyMaterialHandle keyMaterial,
        CanonicalSyncAad aad,
        ReadOnlySpan<byte> plaintext)
    {
        var categoryKey = new byte[InProcessSyncKeyMaterialRegistry.CategoryKeySizeBytes];
        try
        {
            if (!_keyRegistry.TryDeriveCategoryKey(
                    keyMaterial,
                    aad.KeysetId,
                    aad.KeyEpoch,
                    aad.Category,
                    categoryKey))
            {
                return ControllerResult<EncryptedParts>.Failure(KeyUnavailable());
            }

            var nonce = RandomNumberGenerator.GetBytes(SyncProtocol.NonceSizeBytes);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[SyncProtocol.AuthenticationTagSizeBytes];
            var canonicalAad = SyncContractRules.EncodeCanonicalAad(aad);
            try
            {
                using var aes = new AesGcm(categoryKey, SyncProtocol.AuthenticationTagSizeBytes);
                aes.Encrypt(nonce, plaintext, ciphertext, tag, canonicalAad);
                return ControllerResult<EncryptedParts>.Success(new EncryptedParts(nonce, ciphertext, tag));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(canonicalAad);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(categoryKey);
        }
    }

    private ControllerResult<OwnedPlaintext> Decrypt(
        SyncKeyMaterialHandle keyMaterial,
        CanonicalSyncAad aad,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag)
    {
        var categoryKey = new byte[InProcessSyncKeyMaterialRegistry.CategoryKeySizeBytes];
        try
        {
            if (!_keyRegistry.TryDeriveCategoryKey(
                    keyMaterial,
                    aad.KeysetId,
                    aad.KeyEpoch,
                    aad.Category,
                    categoryKey))
            {
                return ControllerResult<OwnedPlaintext>.Failure(KeyUnavailable());
            }

            var plaintext = new OwnedPlaintext(ciphertext.Length);
            var canonicalAad = SyncContractRules.EncodeCanonicalAad(aad);
            try
            {
                using var aes = new AesGcm(categoryKey, SyncProtocol.AuthenticationTagSizeBytes);
                aes.Decrypt(nonce, ciphertext, tag, plaintext.Bytes, canonicalAad);
                return ControllerResult<OwnedPlaintext>.Success(plaintext);
            }
            catch (CryptographicException)
            {
                plaintext.Dispose();
                return ControllerResult<OwnedPlaintext>.Failure(IntegrityError());
            }
            finally
            {
                CryptographicOperations.ZeroMemory(canonicalAad);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(categoryKey);
        }
    }

    private static ControllerError? ValidateOperation(
        SyncOperationContext? context,
        CanonicalSyncAad? aad,
        SyncRecordKind expectedKind,
        bool requirePurgeOperationBinding = false)
    {
        if (context is null || !context.Browsing.IsStructurallyValid)
            return Error(ControllerErrorCode.InvalidRequest, "sync.crypto.context.invalid");
        if (context.Browsing.Privacy.Mode != BrowserProfileMode.Normal)
            return Error(ControllerErrorCode.PolicyDenied, "sync.private-mode.policy-denied");
        if (aad is null || !SyncContractRules.ValidateAad(aad).IsValid || aad.RecordKind != expectedKind)
            return Error(ControllerErrorCode.InvalidRequest, "sync.crypto.aad.invalid");
        if (context.Browsing.Privacy.ProfileId != aad.ProfileId)
            return Error(ControllerErrorCode.InvalidRequest, "sync.crypto.context.profile-mismatch");
        if (expectedKind == SyncRecordKind.Purge &&
            requirePurgeOperationBinding &&
            aad.OperationId != context.OperationId)
            return Error(ControllerErrorCode.InvalidRequest, "sync.crypto.context.operation-mismatch");
        return null;
    }

    private static ValueTask<ControllerResult<T>> Success<T>(T value)
        where T : class =>
        ValueTask.FromResult(ControllerResult<T>.Success(value));

    private static ValueTask<ControllerResult<T>> Failure<T>(ControllerError error)
        where T : class =>
        ValueTask.FromResult(ControllerResult<T>.Failure(error));

    private static ValueTask<ControllerResult<T>> Invalid<T>(string messageKey)
        where T : class =>
        Failure<T>(Error(ControllerErrorCode.InvalidRequest, messageKey));

    private static ValueTask<ControllerResult<T>> Integrity<T>()
        where T : class =>
        Failure<T>(IntegrityError());

    private static ValueTask<ControllerResult<T>> Cancelled<T>()
        where T : class =>
        Failure<T>(Error(ControllerErrorCode.Cancelled, "sync.crypto.cancelled"));

    private static ControllerError KeyUnavailable() =>
        Error(ControllerErrorCode.NotFound, "sync.crypto.key-unavailable");

    private static ControllerError IntegrityError() =>
        Error(ControllerErrorCode.IntegrityFailure, "sync.crypto.integrity-failure");

    private static ControllerError Error(ControllerErrorCode code, string messageKey) =>
        ControllerError.Create(code, messageKey);

    private sealed record EncryptedParts(byte[] Nonce, byte[] Ciphertext, byte[] AuthenticationTag);

    private sealed class OwnedPlaintext : IDisposable
    {
        private byte[]? _bytes;

        public OwnedPlaintext(int length)
        {
            _bytes = new byte[length];
        }

        public byte[] Bytes => _bytes ?? throw new ObjectDisposedException(nameof(OwnedPlaintext));

        public void Dispose()
        {
            var bytes = Interlocked.Exchange(ref _bytes, null);
            if (bytes is not null)
                CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
