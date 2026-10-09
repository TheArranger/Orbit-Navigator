using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Sync;
using OrbitNavigator.Sync.Cryptography;
using OrbitNavigator.Sync.Recovery;
using Xunit;

namespace OrbitNavigator.Sync.Tests.Interop;

/// <summary>Known-answer fixtures generated independently by Python cryptography/OpenSSL.</summary>
public sealed class V1InteroperabilityTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task NativeCodecAuthenticatesIndependentHistoryTabsAndTombstoneVectors(int index)
    {
        using var fixture = Load();
        var root = fixture.RootElement;
        var vector = root.GetProperty("records")[index];
        var record = vector.GetProperty("record");
        var aad = Aad(record.GetProperty("aad"));
        var expectedPlaintext = Convert.FromHexString(vector.GetProperty("plaintext_hex").GetString()!);
        Assert.Equal(vector.GetProperty("canonical_aad").GetString(),
            Encoding.UTF8.GetString(SyncContractRules.EncodeCanonicalAad(aad)));
        using var registry = new InProcessSyncKeyMaterialRegistry();
        var handle = registry.Register(aad.ProfileId, Convert.FromHexString(root.GetProperty("root_key_hex").GetString()!));
        var key = new byte[32];
        Assert.True(registry.TryDeriveCategoryKey(handle, aad.ProfileId, aad.KeysetId, aad.KeyEpoch, aad.Category, key));
        Assert.Equal(Convert.FromHexString(vector.GetProperty("category_key_hex").GetString()!), key);
        using (var cipher = new AesGcm(key, 16))
        {
            var output = new byte[expectedPlaintext.Length];
            var tag = new byte[16];
            cipher.Encrypt(Bytes(record, "nonce"), expectedPlaintext, output, tag, SyncContractRules.EncodeCanonicalAad(aad));
            Assert.Equal(Bytes(record, "ciphertext"), output);
            Assert.Equal(Bytes(record, "authentication_tag"), tag);
        }
        CryptographicOperations.ZeroMemory(key);
        var codec = new AesGcmSyncEnvelopeCodec(registry);
        // A joining device has a DIFFERENT local profile; crypto binds to account
        // sync identity without renaming the local browser profile.
        var localProfile = new ProfileId(Guid.ParseExact("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "N"));
        var context = Context(localProfile);
        if (aad.RecordKind == SyncRecordKind.Tombstone)
        {
            var result = await codec.DecryptAndValidateTombstoneAsync(context, handle,
                new(aad, Bytes(record, "nonce"), Bytes(record, "ciphertext"), Bytes(record, "authentication_tag")), default);
            Assert.True(result.IsSuccess);
            Assert.Equal(aad.ProfileId, result.Value!.ProfileId);
            Assert.Equal(aad.ClientSequence, result.Value.ClientSequence);
            Assert.Equal(expectedPlaintext, SyncPayloadBinaryCodec.SerializeTombstone(aad));
        }
        else
        {
            var result = await codec.DecryptAsync(context, handle,
                new(aad, Bytes(record, "nonce"), Bytes(record, "ciphertext"), Bytes(record, "authentication_tag")), default);
            Assert.True(result.IsSuccess);
            Assert.Equal(expectedPlaintext, SyncPayloadBinaryCodec.SerializeRecord(result.Value!));
            Assert.Equal(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero), result.Value!.ModifiedAtUtc);
            if (result.Value is HistorySyncRecord history)
                Assert.Equal("Fixture title \u03a9 \U0001f680", history.Title);
            else
            {
                var tab = Assert.IsType<OpenTabSyncRecord>(result.Value);
                Assert.Equal(3, tab.Position);
                Assert.Equal("Fixture group", tab.GroupLabel);
            }
        }
        Assert.Equal(localProfile, context.Browsing.Privacy.ProfileId);
    }

    [Fact]
    public void NativeRecoveryUnwrapAcceptsIndependentPbkdf2AesGcmVector()
    {
        using var fixture = Load();
        var root = fixture.RootElement;
        var wrapped = root.GetProperty("wrapped_keyset");
        var profile = new ProfileId(Guid.ParseExact(root.GetProperty("profile_id").GetString()!, "N"));
        using var leases = new RecoveryCodeLeaseStore();
        var lease = leases.Import(profile, root.GetProperty("recovery_code").GetString()!);
        Assert.True(lease.IsSuccess);
        var kdf = wrapped.GetProperty("kdf");
        var envelope = new WrappedSyncKeyset(
            new(Guid.ParseExact(wrapped.GetProperty("keyset_id").GetString()!, "N")), 0,
            SyncKeyWrapMethod.RecoveryCode,
            new(SyncKdfAlgorithm.Pbkdf2Sha256, Bytes(kdf, "salt"), 600000, 0, 1, 32),
            Bytes(wrapped, "nonce"), Bytes(wrapped, "wrapped_key_ciphertext"), Bytes(wrapped, "authentication_tag"));
        var recovered = new RecoveryCodeKeyWrapper(leases).Unwrap(profile, envelope, lease.Value!.LeaseId);
        Assert.True(recovered.IsSuccess);
        using var owned = recovered.Value!;
        var material = new byte[32];
        owned.CopyTo(material);
        Assert.Equal(Convert.FromHexString(root.GetProperty("root_key_hex").GetString()!), material);
        CryptographicOperations.ZeroMemory(material);
        Assert.True(lease.Value.IsDisposed);
    }

    private static JsonDocument Load()
    {
        using var stream = typeof(V1InteroperabilityTests).Assembly.GetManifestResourceStream(
            "OrbitNavigator.Sync.Tests.Interop.v1-vectors.json")!;
        return JsonDocument.Parse(stream);
    }

    private static byte[] Bytes(JsonElement value, string property)
    {
        var encoded = value.GetProperty(property).GetString()!;
        return Convert.FromBase64String(encoded.Replace('-', '+').Replace('_', '/') + new string('=', (4 - encoded.Length % 4) % 4));
    }

    private static CanonicalSyncAad Aad(JsonElement value) => new(
        1, 1, new(Id(value, "profile_id")), new(Id(value, "device_id")), new(Id(value, "keyset_id")), 0,
        value.GetProperty("record_kind").GetString() == "upsert" ? SyncRecordKind.Upsert : SyncRecordKind.Tombstone,
        new(Id(value, "envelope_id")), value.GetProperty("category").GetString() == "history" ? SyncDataCategory.History : SyncDataCategory.OpenTabs,
        new(Id(value, "entity_id")), null, 0, value.GetProperty("client_sequence").GetInt64());

    private static Guid Id(JsonElement value, string property) => Guid.ParseExact(value.GetProperty(property).GetString()!, "N");

    private static SyncOperationContext Context(ProfileId profile) => SyncOperationContext.Authorize(new(
        new PrivacyContext(profile, new BrowserSessionId(Guid.NewGuid()), BrowserProfileMode.Normal),
        new BrowserWindowId(Guid.NewGuid()), new BrowserTabId(Guid.NewGuid()), null),
        new SyncOperationId(Guid.NewGuid())).Value!;
}
