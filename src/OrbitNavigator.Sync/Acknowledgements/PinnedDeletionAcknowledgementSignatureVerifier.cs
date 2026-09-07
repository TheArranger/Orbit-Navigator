using System.Security.Cryptography;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Sync;

namespace OrbitNavigator.Sync.Acknowledgements;

/// <summary>
/// Converts untrusted deletion transport responses into authenticated receipts by
/// verifying a pinned My Orbit server key and every request/status binding first.
/// Sync encryption key material is deliberately absent from this boundary.
/// </summary>
public sealed class PinnedDeletionAcknowledgementSignatureVerifier
    : IDeletionAcknowledgementSignatureVerifier
{
    private static readonly TimeSpan MaximumFutureClockSkew = TimeSpan.FromMinutes(5);
    private const string P256Oid = "1.2.840.10045.3.1.7";

    private readonly IMyOrbitServerSigningKeyProvider _keyProvider;
    private readonly TimeProvider _timeProvider;

    public PinnedDeletionAcknowledgementSignatureVerifier(
        IMyOrbitServerSigningKeyProvider keyProvider,
        TimeProvider? timeProvider = null)
    {
        _keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<ControllerResult<AuthenticatedDeletionPushReceipt>> VerifyPushAsync(
        SyncOperationContext context,
        BoundDeletionPushRequest acceptedRequest,
        ServerSignedDeletionPushAcknowledgement acknowledgement,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Cancelled<AuthenticatedDeletionPushReceipt>();

        if (acceptedRequest is null ||
            acknowledgement is null ||
            acknowledgement.Payload is null ||
            !ContextMatches(context, acceptedRequest, acknowledgement.Payload.Aad) ||
            !DeletionAcknowledgementRules.ValidateSignedAcknowledgement(acknowledgement).IsValid ||
            !DeletionAcknowledgementRules.ValidateReceipt(acceptedRequest, acknowledgement.Payload).IsValid ||
            !IsPlausibleServerTime(acknowledgement.Payload.AcceptedAtUtc))
        {
            return IntegrityFailure<AuthenticatedDeletionPushReceipt>();
        }

        var verified = await VerifySignatureAsync(
            acknowledgement.SigningKeyId,
            acknowledgement.Signature,
            acknowledgement.Payload.AcceptedAtUtc,
            () => DeletionAcknowledgementRules.EncodeCanonicalSignedReceiptPayload(
                acknowledgement.Payload),
            cancellationToken).ConfigureAwait(false);

        return verified.IsSuccess
            ? ControllerResult<AuthenticatedDeletionPushReceipt>.Success(acknowledgement.Payload)
            : ControllerResult<AuthenticatedDeletionPushReceipt>.Failure(verified.Error!);
    }

    public async ValueTask<ControllerResult<AuthenticatedDeletionPropagationStatus>> VerifyStatusAsync(
        SyncOperationContext context,
        AuthenticatedDeletionPushReceipt acceptedReceipt,
        ServerSignedDeletionPropagationStatus status,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Cancelled<AuthenticatedDeletionPropagationStatus>();

        if (acceptedReceipt is null ||
            status is null ||
            status.Payload is null ||
            !ContextMatches(context, acceptedReceipt.Aad) ||
            !DeletionAcknowledgementRules.ValidateSignedPropagationStatus(status).IsValid ||
            !DeletionAcknowledgementRules.ValidatePropagationStatus(acceptedReceipt, status.Payload).IsValid ||
            status.Payload.ObservedAtUtc < acceptedReceipt.AcceptedAtUtc ||
            !IsPlausibleServerTime(status.Payload.ObservedAtUtc))
        {
            return IntegrityFailure<AuthenticatedDeletionPropagationStatus>();
        }

        var verified = await VerifySignatureAsync(
            status.SigningKeyId,
            status.Signature,
            status.Payload.ObservedAtUtc,
            () => DeletionAcknowledgementRules.EncodeCanonicalSignedStatusPayload(status.Payload),
            cancellationToken).ConfigureAwait(false);

        return verified.IsSuccess
            ? ControllerResult<AuthenticatedDeletionPropagationStatus>.Success(status.Payload)
            : ControllerResult<AuthenticatedDeletionPropagationStatus>.Failure(verified.Error!);
    }

    private async ValueTask<ControllerResult> VerifySignatureAsync(
        MyOrbitServerSigningKeyId signingKeyId,
        ReadOnlyMemory<byte> signature,
        DateTimeOffset signedAtUtc,
        Func<byte[]> encodePayload,
        CancellationToken cancellationToken)
    {
        ControllerResult<MyOrbitServerSigningPublicKey> keyResult;
        try
        {
            keyResult = await _keyProvider.GetPinnedKeyAsync(
                signingKeyId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled();
        }

        if (!keyResult.IsSuccess)
            return ControllerResult.Failure(keyResult.Error!);

        var key = keyResult.Value!;
        if (key.KeyId != signingKeyId ||
            key.Algorithm != MyOrbitServerSigningAlgorithm.EcdsaP256Sha256P1363 ||
            key.State is not (MyOrbitServerSigningKeyState.Active or
                MyOrbitServerSigningKeyState.Retiring) ||
            signedAtUtc < key.ValidFromUtc ||
            signedAtUtc > key.ValidUntilUtc)
        {
            return IntegrityFailure();
        }

        byte[]? canonicalPayload = null;
        try
        {
            canonicalPayload = encodePayload();
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(
                key.SubjectPublicKeyInfo.Span,
                out var bytesRead);
            var parameters = verifier.ExportParameters(false);
            if (bytesRead != key.SubjectPublicKeyInfo.Length ||
                verifier.KeySize != 256 ||
                parameters.Curve.Oid.Value != P256Oid)
            {
                return IntegrityFailure();
            }

            return verifier.VerifyData(
                canonicalPayload,
                signature.Span,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
                ? ControllerResult.Success()
                : IntegrityFailure();
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            return IntegrityFailure();
        }
        finally
        {
            if (canonicalPayload is not null)
                CryptographicOperations.ZeroMemory(canonicalPayload);
        }
    }

    private bool IsPlausibleServerTime(DateTimeOffset value) =>
        value != default && value <= _timeProvider.GetUtcNow() + MaximumFutureClockSkew;

    private static bool ContextMatches(
        SyncOperationContext? context,
        BoundDeletionPushRequest? request,
        DeletionAcknowledgementAad? aad) =>
        context is not null &&
        request is not null &&
        aad is not null &&
        context.Browsing.Privacy.ProfileId == aad.ProfileId &&
        context.OperationId == request.Binding.OperationId &&
        aad.OperationId == context.OperationId;

    private static bool ContextMatches(
        SyncOperationContext? context,
        DeletionAcknowledgementAad? aad) =>
        context is not null &&
        aad is not null &&
        context.Browsing.Privacy.ProfileId == aad.ProfileId &&
        context.OperationId == aad.OperationId;

    private static ControllerResult<T> Cancelled<T>() where T : class =>
        ControllerResult<T>.Failure(ControllerError.Create(
            ControllerErrorCode.Cancelled,
            "error.sync.deletion_ack.cancelled"));

    private static ControllerResult Cancelled() =>
        ControllerResult.Failure(ControllerError.Create(
            ControllerErrorCode.Cancelled,
            "error.sync.deletion_ack.cancelled"));

    private static ControllerResult<T> IntegrityFailure<T>() where T : class =>
        ControllerResult<T>.Failure(ControllerError.Create(
            ControllerErrorCode.IntegrityFailure,
            "error.sync.deletion_ack.integrity_failed"));

    private static ControllerResult IntegrityFailure() =>
        ControllerResult.Failure(ControllerError.Create(
            ControllerErrorCode.IntegrityFailure,
            "error.sync.deletion_ack.integrity_failed"));
}
