using System.Security.Cryptography;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Sync;
using OrbitNavigator.Sync.Acknowledgements;
using Xunit;

namespace OrbitNavigator.Sync.Tests.Acknowledgements;

public sealed class PinnedDeletionAcknowledgementSignatureVerifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ValidPinnedSignatureAuthenticatesFullyBoundPushReceipt()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var fixture = CreateFixture(signer);
        var provider = new StubKeyProvider(fixture.PublicKey);
        var verifier = new PinnedDeletionAcknowledgementSignatureVerifier(
            provider,
            new FixedTimeProvider(Now));

        var result = await verifier.VerifyPushAsync(
            fixture.Context,
            fixture.Request,
            fixture.Acknowledgement,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Same(fixture.Receipt, result.Value);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task ContextMismatchFailsBeforePinnedKeyLookup()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var fixture = CreateFixture(signer);
        var provider = new StubKeyProvider(fixture.PublicKey);
        var verifier = new PinnedDeletionAcknowledgementSignatureVerifier(provider);
        var wrongContext = Context(new ProfileId(Guid.NewGuid()), fixture.Request.Binding.OperationId);

        var result = await verifier.VerifyPushAsync(
            wrongContext,
            fixture.Request,
            fixture.Acknowledgement,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ControllerErrorCode.IntegrityFailure, result.Error!.Code);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task SignedPayloadSubstitutionIsRejected()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var fixture = CreateFixture(signer);
        var provider = new StubKeyProvider(fixture.PublicKey);
        var verifier = new PinnedDeletionAcknowledgementSignatureVerifier(
            provider,
            new FixedTimeProvider(Now));
        var substituted = fixture.Acknowledgement with
        {
            Payload = fixture.Receipt with
            {
                Cursor = new SyncCursor("substituted-cursor"),
            },
        };

        var result = await verifier.VerifyPushAsync(
            fixture.Context,
            fixture.Request,
            substituted,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ControllerErrorCode.IntegrityFailure, result.Error!.Code);
    }

    [Fact]
    public async Task StatusRequiresExactReceiptBindingAndValidPinnedSignature()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var fixture = CreateFixture(signer);
        var provider = new StubKeyProvider(fixture.PublicKey);
        var verifier = new PinnedDeletionAcknowledgementSignatureVerifier(
            provider,
            new FixedTimeProvider(Now));
        var payload = new AuthenticatedDeletionPropagationStatus(
            fixture.Receipt.Aad,
            fixture.Receipt.Fence,
            [new DeletionCategoryPropagation(
                SyncDataCategory.History,
                DeletionCategoryPropagationState.Propagated,
                Now,
                null)],
            true,
            Now);
        var signed = SignStatus(payload, fixture.KeyId, signer);

        var valid = await verifier.VerifyStatusAsync(
            fixture.Context,
            fixture.Receipt,
            signed,
            CancellationToken.None);
        var substituted = signed with
        {
            Payload = payload with
            {
                Categories = [new DeletionCategoryPropagation(
                    SyncDataCategory.History,
                    DeletionCategoryPropagationState.Pending,
                    Now,
                    null)],
                IsComplete = false,
            },
        };
        var rejected = await verifier.VerifyStatusAsync(
            fixture.Context,
            fixture.Receipt,
            substituted,
            CancellationToken.None);

        Assert.True(valid.IsSuccess);
        Assert.False(rejected.IsSuccess);
        Assert.Equal(ControllerErrorCode.IntegrityFailure, rejected.Error!.Code);
    }

    [Fact]
    public async Task ExpiredSigningWindowAndFutureServerTimeAreRejected()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var fixture = CreateFixture(signer, validUntilUtc: Now.AddMinutes(-1));
        var verifier = new PinnedDeletionAcknowledgementSignatureVerifier(
            new StubKeyProvider(fixture.PublicKey),
            new FixedTimeProvider(Now));

        var expired = await verifier.VerifyPushAsync(
            fixture.Context,
            fixture.Request,
            fixture.Acknowledgement,
            CancellationToken.None);
        var futureReceipt = fixture.Receipt with { AcceptedAtUtc = Now.AddMinutes(6) };
        var future = await verifier.VerifyPushAsync(
            fixture.Context,
            fixture.Request,
            SignReceipt(futureReceipt, fixture.KeyId, signer),
            CancellationToken.None);

        Assert.False(expired.IsSuccess);
        Assert.Equal(ControllerErrorCode.IntegrityFailure, expired.Error!.Code);
        Assert.False(future.IsSuccess);
        Assert.Equal(ControllerErrorCode.IntegrityFailure, future.Error!.Code);
    }

    [Fact]
    public async Task CancelledVerificationDoesNotCallPinnedKeyProvider()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var fixture = CreateFixture(signer);
        var provider = new StubKeyProvider(fixture.PublicKey);
        var verifier = new PinnedDeletionAcknowledgementSignatureVerifier(provider);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await verifier.VerifyPushAsync(
            fixture.Context,
            fixture.Request,
            fixture.Acknowledgement,
            cancellation.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal(ControllerErrorCode.Cancelled, result.Error!.Code);
        Assert.Equal(0, provider.CallCount);
    }

    private static Fixture CreateFixture(
        ECDsa signer,
        DateTimeOffset? validUntilUtc = null)
    {
        var profileId = new ProfileId(Guid.NewGuid());
        var deviceId = new DeviceId(Guid.NewGuid());
        var operationId = new SyncOperationId(Guid.NewGuid());
        var keysetId = new SyncKeysetId(Guid.NewGuid());
        var envelopeId = new SyncEnvelopeId(Guid.NewGuid());
        var recordAad = new CanonicalSyncAad(
            SyncProtocol.CurrentProtocolVersion,
            SyncProtocol.CurrentSchemaVersion,
            profileId,
            deviceId,
            keysetId,
            1,
            SyncRecordKind.Tombstone,
            envelopeId,
            SyncDataCategory.History,
            new SyncEntityId(Guid.NewGuid()),
            null,
            8,
            13);
        var tombstone = new EncryptedSyncTombstone(
            recordAad,
            new byte[SyncProtocol.NonceSizeBytes],
            new byte[] { 1 },
            new byte[SyncProtocol.AuthenticationTagSizeBytes]);
        var fence = new ClientFence(deviceId, 8, 8);
        var push = new SyncPushRequest(deviceId, fence, [], [tombstone], []);
        var binding = new DeletionPushBinding(
            operationId,
            new HashSet<SyncDataCategory> { SyncDataCategory.History },
            [envelopeId],
            DeletionAcknowledgementRules.ComputeRequestDigest(push).Value!,
            new SyncIdempotencyKey(Guid.NewGuid()));
        var request = new BoundDeletionPushRequest(push, binding);
        var ackAad = new DeletionAcknowledgementAad(
            SyncProtocol.CurrentProtocolVersion,
            SyncProtocol.CurrentSchemaVersion,
            profileId,
            deviceId,
            keysetId,
            fence.ClientGeneration,
            operationId,
            binding.Categories,
            binding.EnvelopeIds,
            binding.RequestDigest,
            binding.IdempotencyKey);
        var receipt = new AuthenticatedDeletionPushReceipt(
            ackAad,
            new SyncCursor("cursor"),
            fence,
            Now);
        var keyId = new MyOrbitServerSigningKeyId(Guid.NewGuid());
        var publicKey = MyOrbitServerSigningPublicKey.Create(
            keyId,
            MyOrbitServerSigningAlgorithm.EcdsaP256Sha256P1363,
            signer.ExportSubjectPublicKeyInfo(),
            Now.AddDays(-1),
            validUntilUtc ?? Now.AddDays(1),
            MyOrbitServerSigningKeyState.Active).Value!;
        return new Fixture(
            Context(profileId, operationId),
            request,
            receipt,
            SignReceipt(receipt, keyId, signer),
            keyId,
            publicKey);
    }

    private static ServerSignedDeletionPushAcknowledgement SignReceipt(
        AuthenticatedDeletionPushReceipt payload,
        MyOrbitServerSigningKeyId keyId,
        ECDsa signer) =>
        new(
            payload,
            keyId,
            signer.SignData(
                DeletionAcknowledgementRules.EncodeCanonicalSignedReceiptPayload(payload),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    private static ServerSignedDeletionPropagationStatus SignStatus(
        AuthenticatedDeletionPropagationStatus payload,
        MyOrbitServerSigningKeyId keyId,
        ECDsa signer) =>
        new(
            payload,
            keyId,
            signer.SignData(
                DeletionAcknowledgementRules.EncodeCanonicalSignedStatusPayload(payload),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    private static SyncOperationContext Context(ProfileId profileId, SyncOperationId operationId)
    {
        var browsing = new BrowsingContext(
            new PrivacyContext(
                profileId,
                new BrowserSessionId(Guid.NewGuid()),
                BrowserProfileMode.Normal),
            new BrowserWindowId(Guid.NewGuid()),
            new BrowserTabId(Guid.NewGuid()),
            null);
        return SyncOperationContext.Authorize(browsing, operationId).Value!;
    }

    private sealed record Fixture(
        SyncOperationContext Context,
        BoundDeletionPushRequest Request,
        AuthenticatedDeletionPushReceipt Receipt,
        ServerSignedDeletionPushAcknowledgement Acknowledgement,
        MyOrbitServerSigningKeyId KeyId,
        MyOrbitServerSigningPublicKey PublicKey);

    private sealed class StubKeyProvider(MyOrbitServerSigningPublicKey key)
        : IMyOrbitServerSigningKeyProvider
    {
        public int CallCount { get; private set; }

        public ValueTask<ControllerResult<MyOrbitServerSigningPublicKey>> GetPinnedKeyAsync(
            MyOrbitServerSigningKeyId keyId,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return ValueTask.FromResult(keyId == key.KeyId
                ? ControllerResult<MyOrbitServerSigningPublicKey>.Success(key)
                : ControllerResult<MyOrbitServerSigningPublicKey>.Failure(ControllerError.Create(
                    ControllerErrorCode.NotFound,
                    "error.sync.server_signing_key.not_found")));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
