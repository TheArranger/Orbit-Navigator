using System.Security.Cryptography;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Sync;
using Xunit;

namespace OrbitNavigator.Contracts.Tests.Sync;

public sealed class SyncDeletionAcknowledgementContractTests
{
    [Fact]
    public void ReceiptBindsOperationCategoriesEnvelopesDigestAndIdempotency()
    {
        var request = Request();
        var aad = AcknowledgementAad(request);
        var receipt = Receipt(aad);
        Assert.True(DeletionAcknowledgementRules.ValidateReceipt(request, receipt).IsValid);

        var substitutions = new[]
        {
            aad with { OperationId = new SyncOperationId(Guid.NewGuid()) },
            aad with { Categories = new HashSet<SyncDataCategory> { SyncDataCategory.Settings } },
            aad with { EnvelopeIds = new[] { new SyncEnvelopeId(Guid.NewGuid()) } },
            aad with { RequestDigest = Digest(9) },
            aad with { IdempotencyKey = new SyncIdempotencyKey(Guid.NewGuid()) },
            aad with { DeviceId = new DeviceId(Guid.NewGuid()) },
            aad with { ClientGeneration = aad.ClientGeneration + 1 },
            aad with { ProtocolVersion = aad.ProtocolVersion + 1 },
            aad with { SchemaVersion = aad.SchemaVersion + 1 },
        };

        Assert.All(substitutions, substitution => Assert.False(
            DeletionAcknowledgementRules.ValidateReceipt(request, Receipt(substitution)).IsValid));

        var wrongFence = Receipt(aad) with
        {
            Fence = new ClientFence(new DeviceId(Guid.NewGuid()), aad.ClientGeneration + 1, 0),
        };
        Assert.False(DeletionAcknowledgementRules.ValidateReceipt(request, wrongFence).IsValid);
        Assert.False(DeletionAcknowledgementRules.ValidateReceipt(
            request,
            Receipt(aad) with { Fence = new ClientFence(default, 0, 0) }).IsValid);
        Assert.False(DeletionAcknowledgementRules.ValidateReceipt(
            request,
            Receipt(aad) with { Fence = new ClientFence(aad.DeviceId, aad.ClientGeneration, 9) }).IsValid);
        Assert.False(DeletionAcknowledgementRules.ValidateReceipt(
            request,
            Receipt(aad) with { Fence = new ClientFence(aad.DeviceId, aad.ClientGeneration, 7) }).IsValid);
    }

    [Fact]
    public void TransportReturnsOnlyServerSignedShapesAndVerifierNeedsNoSyncKey()
    {
        var push = typeof(ISyncedDeletionTransport).GetMethod(nameof(ISyncedDeletionTransport.PushAsync));
        var status = typeof(ISyncedDeletionTransport).GetMethod(
            nameof(ISyncedDeletionTransport.GetPropagationStatusAsync));
        var authenticatePush = typeof(IDeletionAcknowledgementSignatureVerifier).GetMethod(
            nameof(IDeletionAcknowledgementSignatureVerifier.VerifyPushAsync));
        var authenticateStatus = typeof(IDeletionAcknowledgementSignatureVerifier).GetMethod(
            nameof(IDeletionAcknowledgementSignatureVerifier.VerifyStatusAsync));

        Assert.Equal(
            typeof(ValueTask<ControllerResult<ServerSignedDeletionPushAcknowledgement>>),
            push!.ReturnType);
        Assert.Equal(
            typeof(ValueTask<ControllerResult<ServerSignedDeletionPropagationStatus>>),
            status!.ReturnType);
        Assert.Equal(
            typeof(ValueTask<ControllerResult<AuthenticatedDeletionPushReceipt>>),
            authenticatePush!.ReturnType);
        Assert.Equal(
            typeof(ValueTask<ControllerResult<AuthenticatedDeletionPropagationStatus>>),
            authenticateStatus!.ReturnType);
        Assert.DoesNotContain(
            authenticatePush.GetParameters(),
            parameter => parameter.ParameterType == typeof(SyncKeyMaterialHandle));
        Assert.NotNull(typeof(IMyOrbitServerSigningKeyProvider).GetMethod(
            nameof(IMyOrbitServerSigningKeyProvider.GetPinnedKeyAsync)));
    }

    [Fact]
    public void ServerSignatureRejectsBoundPayloadSubstitution()
    {
        var receipt = Receipt(AcknowledgementAad(Request()));
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signature = signingKey.SignData(
            DeletionAcknowledgementRules.EncodeCanonicalSignedReceiptPayload(receipt),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        Assert.True(signingKey.VerifyData(
            DeletionAcknowledgementRules.EncodeCanonicalSignedReceiptPayload(receipt),
            signature,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

        var substituted = receipt with
        {
            Aad = receipt.Aad with { IdempotencyKey = new SyncIdempotencyKey(Guid.NewGuid()) },
        };
        Assert.False(signingKey.VerifyData(
            DeletionAcknowledgementRules.EncodeCanonicalSignedReceiptPayload(substituted),
            signature,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public void PropagationStatusMustCoverExactAcceptedCategorySet()
    {
        var aad = AcknowledgementAad(Request());
        var receipt = Receipt(aad);
        var valid = new AuthenticatedDeletionPropagationStatus(
            aad,
            receipt.Fence,
            [new DeletionCategoryPropagation(
                SyncDataCategory.History,
                DeletionCategoryPropagationState.Propagated,
                DateTimeOffset.UtcNow,
                null)],
            true,
            DateTimeOffset.UtcNow);
        var substituted = valid with
        {
            Categories = [new DeletionCategoryPropagation(
                SyncDataCategory.Settings,
                DeletionCategoryPropagationState.Propagated,
                DateTimeOffset.UtcNow,
                null)],
        };

        Assert.True(DeletionAcknowledgementRules.ValidatePropagationStatus(receipt, valid).IsValid);
        Assert.False(DeletionAcknowledgementRules.ValidatePropagationStatus(receipt, substituted).IsValid);

        var failedAsComplete = valid with
        {
            Categories = [new DeletionCategoryPropagation(
                SyncDataCategory.History,
                DeletionCategoryPropagationState.Failed,
                DateTimeOffset.UtcNow,
                ControllerError.Create(ControllerErrorCode.InternalFailure, "error.remote"))],
        };
        Assert.False(DeletionAcknowledgementRules.ValidatePropagationStatus(receipt, failedAsComplete).IsValid);

        var wrongFence = valid with
        {
            Fence = valid.Fence with { MinimumAcceptedGeneration = valid.Fence.MinimumAcceptedGeneration - 1 },
        };
        Assert.False(DeletionAcknowledgementRules.ValidatePropagationStatus(receipt, wrongFence).IsValid);
    }

    [Fact]
    public void MixedPayloadProfileDeviceKeysetOrGenerationIsRejected()
    {
        var original = Request();
        var first = Assert.Single(original.Push.Tombstones);
        var secondAad = first.Aad with
        {
            EnvelopeId = new SyncEnvelopeId(Guid.NewGuid()),
            EntityId = new SyncEntityId(Guid.NewGuid()),
            ProfileId = new ProfileId(Guid.NewGuid()),
            ClientGeneration = first.Aad.ClientGeneration + 1,
        };
        var second = first with { Aad = secondAad };
        var push = original.Push with { Tombstones = [first, second] };
        var binding = original.Binding with
        {
            EnvelopeIds = [first.Aad.EnvelopeId, second.Aad.EnvelopeId],
            RequestDigest = DeletionAcknowledgementRules.ComputeRequestDigest(push).Value!,
        };

        var validation = DeletionAcknowledgementRules.ValidateRequest(
            new BoundDeletionPushRequest(push, binding));

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Issues, issue => issue.Code == "deletion_push.identity");
    }

    [Fact]
    public void SignedAcknowledgementAndStatusRequirePinnedKeyIdAndP256SignatureShape()
    {
        var aad = AcknowledgementAad(Request());
        var receipt = Receipt(aad);
        var keyId = new MyOrbitServerSigningKeyId(Guid.NewGuid());
        var validAck = new ServerSignedDeletionPushAcknowledgement(receipt, keyId, new byte[64]);
        var validStatus = new ServerSignedDeletionPropagationStatus(
            new AuthenticatedDeletionPropagationStatus(
                aad,
                receipt.Fence,
                [new DeletionCategoryPropagation(
                    SyncDataCategory.History,
                    DeletionCategoryPropagationState.Pending,
                    null,
                    null)],
                false,
                DateTimeOffset.UtcNow),
            keyId,
            new byte[64]);

        Assert.True(DeletionAcknowledgementRules.ValidateSignedAcknowledgement(validAck).IsValid);
        Assert.True(DeletionAcknowledgementRules.ValidateSignedPropagationStatus(validStatus).IsValid);
        Assert.False(DeletionAcknowledgementRules.ValidateSignedAcknowledgement(
            validAck with { SigningKeyId = default }).IsValid);
        Assert.False(DeletionAcknowledgementRules.ValidateSignedPropagationStatus(
            validStatus with { Signature = new byte[63] }).IsValid);
    }

    [Fact]
    public void PinnedAndRetiringServerKeysUseBoundKeyIdsAndDefensiveKeyBytes()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var encoded = signingKey.ExportSubjectPublicKeyInfo();
        var keyId = new MyOrbitServerSigningKeyId(Guid.NewGuid());
        var result = MyOrbitServerSigningPublicKey.Create(
            keyId,
            MyOrbitServerSigningAlgorithm.EcdsaP256Sha256P1363,
            encoded,
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30),
            MyOrbitServerSigningKeyState.Retiring);
        encoded[0] ^= 0xff;

        Assert.True(result.IsSuccess);
        Assert.Equal(keyId, result.Value!.KeyId);
        Assert.NotEqual(encoded[0], result.Value.SubjectPublicKeyInfo.Span[0]);
    }

    [Fact]
    public void RequestDigestIsComputedFromFenceIdsAndEncryptedBytes()
    {
        var request = Request();
        var original = DeletionAcknowledgementRules.ComputeRequestDigest(request.Push).Value!;
        var tombstone = Assert.Single(request.Push.Tombstones);
        var changedCiphertext = request.Push with
        {
            Tombstones = [tombstone with { Ciphertext = new byte[] { 2 } }],
        };
        var changedAadOnly = request.Push with
        {
            Tombstones = [tombstone with
            {
                Aad = tombstone.Aad with { EntityId = new SyncEntityId(Guid.NewGuid()) },
            }],
        };
        var changedFence = request.Push with
        {
            Fence = request.Push.Fence with
            {
                ClientGeneration = request.Push.Fence.ClientGeneration + 1,
            },
        };

        Assert.NotEqual(
            Convert.ToHexString(original.Bytes.Span),
            Convert.ToHexString(DeletionAcknowledgementRules.ComputeRequestDigest(changedCiphertext).Value!.Bytes.Span));
        Assert.NotEqual(
            Convert.ToHexString(original.Bytes.Span),
            Convert.ToHexString(DeletionAcknowledgementRules.ComputeRequestDigest(changedAadOnly).Value!.Bytes.Span));
        Assert.NotEqual(
            Convert.ToHexString(original.Bytes.Span),
            Convert.ToHexString(DeletionAcknowledgementRules.ComputeRequestDigest(changedFence).Value!.Bytes.Span));
    }

    private static BoundDeletionPushRequest Request()
    {
        var device = new DeviceId(Guid.NewGuid());
        var envelope = new SyncEnvelopeId(Guid.NewGuid());
        var aad = new CanonicalSyncAad(
            SyncProtocol.CurrentProtocolVersion,
            SyncProtocol.CurrentSchemaVersion,
            new ProfileId(Guid.NewGuid()),
            device,
            new SyncKeysetId(Guid.NewGuid()),
            1,
            SyncRecordKind.Tombstone,
            envelope,
            SyncDataCategory.History,
            new SyncEntityId(Guid.NewGuid()),
            null,
            8,
            13);
        var tombstone = new EncryptedSyncTombstone(
            aad,
            new byte[SyncProtocol.NonceSizeBytes],
            new byte[] { 1 },
            new byte[SyncProtocol.AuthenticationTagSizeBytes]);
        var push = new SyncPushRequest(
            device,
            new ClientFence(device, 8, 8),
            [],
            [tombstone],
            []);
        var binding = new DeletionPushBinding(
            new SyncOperationId(Guid.NewGuid()),
            new HashSet<SyncDataCategory> { SyncDataCategory.History },
            [envelope],
            DeletionAcknowledgementRules.ComputeRequestDigest(push).Value!,
            new SyncIdempotencyKey(Guid.NewGuid()));
        return new BoundDeletionPushRequest(push, binding);
    }

    private static DeletionAcknowledgementAad AcknowledgementAad(
        BoundDeletionPushRequest request)
    {
        var payloadAad = Assert.Single(request.Push.Tombstones).Aad;
        return new DeletionAcknowledgementAad(
            SyncProtocol.CurrentProtocolVersion,
            SyncProtocol.CurrentSchemaVersion,
            payloadAad.ProfileId,
            request.Push.DeviceId,
            payloadAad.KeysetId,
            request.Push.Fence.ClientGeneration,
            request.Binding.OperationId,
            request.Binding.Categories,
            request.Binding.EnvelopeIds,
            request.Binding.RequestDigest,
            request.Binding.IdempotencyKey);
    }

    private static AuthenticatedDeletionPushReceipt Receipt(DeletionAcknowledgementAad aad) =>
        new(
            aad,
            new SyncCursor("cursor"),
            new ClientFence(aad.DeviceId, aad.ClientGeneration, aad.ClientGeneration),
            DateTimeOffset.UtcNow);

    private static DeletionRequestDigest Digest(byte value) =>
        DeletionRequestDigest.Create(Enumerable.Repeat(value, 32).Select(item => (byte)item).ToArray()).Value!;
}
