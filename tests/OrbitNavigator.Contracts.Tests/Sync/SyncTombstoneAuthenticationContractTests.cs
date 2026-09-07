using System.Security.Cryptography;
using System.Text;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Sync;
using Xunit;

namespace OrbitNavigator.Contracts.Tests.Sync;

public sealed class SyncTombstoneAuthenticationContractTests
{
    [Fact]
    public void CodecExposesTypedAuthenticatedTombstoneConsumption()
    {
        var method = typeof(ISyncEnvelopeCodec).GetMethod(
            nameof(ISyncEnvelopeCodec.DecryptAndValidateTombstoneAsync));

        Assert.NotNull(method);
        Assert.Equal(
            new[]
            {
                typeof(SyncOperationContext),
                typeof(SyncKeyMaterialHandle),
                typeof(EncryptedSyncTombstone),
                typeof(CancellationToken),
            },
            method.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.Equal(
            typeof(ValueTask<ControllerResult<AuthenticatedSyncTombstoneReceipt>>),
            method.ReturnType);
    }

    [Fact]
    public void AesGcmAuthenticationRejectsCanonicalAadIdentitySubstitution()
    {
        var aad = TombstoneAad();
        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(SyncProtocol.NonceSizeBytes);
        var plaintext = Encoding.UTF8.GetBytes("orbit-tombstone-v1");
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[SyncProtocol.AuthenticationTagSizeBytes];
        using var aes = new AesGcm(key, SyncProtocol.AuthenticationTagSizeBytes);
        aes.Encrypt(
            nonce,
            plaintext,
            ciphertext,
            tag,
            SyncContractRules.EncodeCanonicalAad(aad));

        var decrypted = new byte[plaintext.Length];
        aes.Decrypt(
            nonce,
            ciphertext,
            tag,
            decrypted,
            SyncContractRules.EncodeCanonicalAad(aad));
        Assert.Equal(plaintext, decrypted);

        var substituted = aad with { EntityId = new SyncEntityId(Guid.NewGuid()) };
        Assert.Throws<AuthenticationTagMismatchException>(() => aes.Decrypt(
            nonce,
            ciphertext,
            tag,
            new byte[plaintext.Length],
            SyncContractRules.EncodeCanonicalAad(substituted)));
    }

    [Fact]
    public void AuthenticatedReceiptMustExactlyMatchTombstoneAad()
    {
        var aad = TombstoneAad();
        var tombstone = new EncryptedSyncTombstone(
            aad,
            new byte[SyncProtocol.NonceSizeBytes],
            new byte[] { 1 },
            new byte[SyncProtocol.AuthenticationTagSizeBytes]);
        var valid = Receipt(aad);
        var substituted = valid with { KeysetId = new SyncKeysetId(Guid.NewGuid()) };

        Assert.True(SyncContractRules.ValidateAuthenticatedTombstone(tombstone, valid).IsValid);
        Assert.False(SyncContractRules.ValidateAuthenticatedTombstone(tombstone, substituted).IsValid);
    }

    private static CanonicalSyncAad TombstoneAad() =>
        new(
            SyncProtocol.CurrentProtocolVersion,
            SyncProtocol.CurrentSchemaVersion,
            new ProfileId(Guid.NewGuid()),
            new DeviceId(Guid.NewGuid()),
            new SyncKeysetId(Guid.NewGuid()),
            2,
            SyncRecordKind.Tombstone,
            new SyncEnvelopeId(Guid.NewGuid()),
            SyncDataCategory.History,
            new SyncEntityId(Guid.NewGuid()),
            null,
            3,
            5);

    private static AuthenticatedSyncTombstoneReceipt Receipt(CanonicalSyncAad aad) =>
        new(
            aad.ProfileId,
            aad.DeviceId,
            aad.KeysetId,
            aad.KeyEpoch,
            aad.EnvelopeId,
            aad.Category,
            aad.EntityId,
            aad.ClientGeneration,
            aad.ClientSequence);
}
