using System.Security.Cryptography;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Sync;
using OrbitNavigator.Sync.Cryptography;
using Xunit;

namespace OrbitNavigator.Sync.Tests.Cryptography;

public sealed class AesGcmSyncEnvelopeCodecTests : IDisposable
{
    private readonly InProcessSyncKeyMaterialRegistry _registry = new();
    private readonly AesGcmSyncEnvelopeCodec _codec;
    private readonly SyncKeyMaterialHandle _keyHandle;
    private readonly ProfileId _profileId = new(Guid.NewGuid());

    public AesGcmSyncEnvelopeCodecTests()
    {
        _codec = new AesGcmSyncEnvelopeCodec(_registry);
        _keyHandle = _registry.Register(RandomNumberGenerator.GetBytes(32));
    }

    [Fact]
    public async Task HistoryRoundTripsWithoutPlaintextFallback()
    {
        var entityId = new SyncEntityId(Guid.NewGuid());
        var record = new HistorySyncRecord(
            entityId,
            7,
            Utc(2026, 8, 8, 10, 11, 12),
            "https://example.test/articles/one?search=orbit",
            "An Orbit Article",
            Utc(2026, 8, 8, 10, 10, 0),
            4);
        var aad = Aad(SyncRecordKind.Upsert, SyncDataCategory.History, entityId);
        var context = Context();

        var encrypted = await _codec.EncryptAsync(context, _keyHandle, aad, record, default);
        var decrypted = await _codec.DecryptAsync(context, _keyHandle, encrypted.Value!, default);

        Assert.True(encrypted.IsSuccess);
        Assert.True(decrypted.IsSuccess);
        var actual = Assert.IsType<HistorySyncRecord>(decrypted.Value);
        Assert.Equal(record, actual);
        Assert.DoesNotContain(record.AbsoluteUrl, Convert.ToBase64String(encrypted.Value!.Ciphertext.Span));
    }

    [Fact]
    public async Task OpenTabRoundTrips()
    {
        var entityId = new SyncEntityId(Guid.NewGuid());
        var record = new OpenTabSyncRecord(
            entityId,
            11,
            Utc(2026, 8, 8, 11, 0, 0),
            "https://video.example.test/watch/42",
            "A social video",
            3,
            "Watch later");
        var aad = Aad(SyncRecordKind.Upsert, SyncDataCategory.OpenTabs, entityId);
        var context = Context();

        var encrypted = await _codec.EncryptAsync(context, _keyHandle, aad, record, default);
        var decrypted = await _codec.DecryptAsync(context, _keyHandle, encrypted.Value!, default);

        Assert.True(decrypted.IsSuccess);
        Assert.Equal(record, Assert.IsType<OpenTabSyncRecord>(decrypted.Value));
    }

    [Fact]
    public async Task AllowlistedSettingsRoundTripWithClosedValueTypes()
    {
        var entityId = new SyncEntityId(Guid.NewGuid());
        var settings = new[]
        {
            new SyncSettingEntry(SyncableSettingField.ThemeMode, new TextSyncSettingValue("dark")),
            new SyncSettingEntry(SyncableSettingField.TextScale, new IntegerSyncSettingValue(125)),
            new SyncSettingEntry(
                SyncableSettingField.DyslexiaFriendlyFontEnabled,
                new BooleanSyncSettingValue(true)),
            new SyncSettingEntry(
                SyncableSettingField.ReadingLineHeight,
                new DecimalSyncSettingValue(1.75m)),
        };
        var record = new SettingsSyncRecord(
            entityId,
            2,
            Utc(2026, 8, 8, 12, 0, 0),
            SyncSettingPersistenceScope.GlobalPersistent,
            settings);
        var aad = Aad(SyncRecordKind.Upsert, SyncDataCategory.Settings, entityId);
        var context = Context();

        var encrypted = await _codec.EncryptAsync(context, _keyHandle, aad, record, default);
        var decrypted = await _codec.DecryptAsync(context, _keyHandle, encrypted.Value!, default);

        Assert.True(decrypted.IsSuccess);
        var actual = Assert.IsType<SettingsSyncRecord>(decrypted.Value);
        Assert.Equal(record.EntityId, actual.EntityId);
        Assert.Equal(record.Revision, actual.Revision);
        Assert.Equal(record.ModifiedAtUtc, actual.ModifiedAtUtc);
        Assert.Equal(record.Scope, actual.Scope);
        Assert.Equal(record.Settings, actual.Settings);
    }

    [Fact]
    public async Task ReencryptingSameRecordUsesFreshNonce()
    {
        var entityId = new SyncEntityId(Guid.NewGuid());
        var record = History(entityId);
        var aad = Aad(SyncRecordKind.Upsert, SyncDataCategory.History, entityId);
        var context = Context();

        var first = await _codec.EncryptAsync(context, _keyHandle, aad, record, default);
        var second = await _codec.EncryptAsync(context, _keyHandle, aad, record, default);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.NotEqual(first.Value!.Nonce.ToArray(), second.Value!.Nonce.ToArray());
        Assert.NotEqual(first.Value.Ciphertext.ToArray(), second.Value.Ciphertext.ToArray());
    }

    [Fact]
    public async Task CiphertextNonceAndTagTamperingFailClosed()
    {
        var entityId = new SyncEntityId(Guid.NewGuid());
        var aad = Aad(SyncRecordKind.Upsert, SyncDataCategory.History, entityId);
        var context = Context();
        var encrypted = (await _codec.EncryptAsync(
            context,
            _keyHandle,
            aad,
            History(entityId),
            default)).Value!;

        var mutations = new[]
        {
            encrypted with { Ciphertext = FlipFirst(encrypted.Ciphertext) },
            encrypted with { Nonce = FlipFirst(encrypted.Nonce) },
            encrypted with { AuthenticationTag = FlipFirst(encrypted.AuthenticationTag) },
        };

        foreach (var mutation in mutations)
        {
            var result = await _codec.DecryptAsync(context, _keyHandle, mutation, default);
            Assert.False(result.IsSuccess);
            Assert.Equal(ControllerErrorCode.IntegrityFailure, result.Error?.Code);
            Assert.Null(result.Value);
        }
    }

    [Fact]
    public async Task CanonicalAadCategoryKeysetEpochAndIdentitySubstitutionFailClosed()
    {
        var entityId = new SyncEntityId(Guid.NewGuid());
        var aad = Aad(SyncRecordKind.Upsert, SyncDataCategory.History, entityId);
        var context = Context();
        var encrypted = (await _codec.EncryptAsync(
            context,
            _keyHandle,
            aad,
            History(entityId),
            default)).Value!;
        var mutations = new[]
        {
            aad with { Category = SyncDataCategory.Settings },
            aad with { KeysetId = new SyncKeysetId(Guid.NewGuid()) },
            aad with { KeyEpoch = aad.KeyEpoch + 1 },
            aad with { EnvelopeId = new SyncEnvelopeId(Guid.NewGuid()) },
            aad with { DeviceId = new DeviceId(Guid.NewGuid()) },
            aad with { EntityId = new SyncEntityId(Guid.NewGuid()) },
            aad with { ClientGeneration = aad.ClientGeneration + 1 },
            aad with { ClientSequence = aad.ClientSequence + 1 },
        };

        foreach (var mutation in mutations)
        {
            var result = await _codec.DecryptAsync(
                context,
                _keyHandle,
                encrypted with { Aad = mutation },
                default);
            Assert.False(result.IsSuccess);
            Assert.Equal(ControllerErrorCode.IntegrityFailure, result.Error?.Code);
        }
    }

    [Fact]
    public async Task DifferentRootKeyCannotDecryptEnvelope()
    {
        var entityId = new SyncEntityId(Guid.NewGuid());
        var aad = Aad(SyncRecordKind.Upsert, SyncDataCategory.History, entityId);
        var context = Context();
        var encrypted = (await _codec.EncryptAsync(
            context,
            _keyHandle,
            aad,
            History(entityId),
            default)).Value!;
        var wrongHandle = _registry.Register(RandomNumberGenerator.GetBytes(32));

        var result = await _codec.DecryptAsync(context, wrongHandle, encrypted, default);

        Assert.False(result.IsSuccess);
        Assert.Equal(ControllerErrorCode.IntegrityFailure, result.Error?.Code);
    }

    [Fact]
    public async Task UnknownAndRemovedHandlesAreRejectedBeforeEncryption()
    {
        var entityId = new SyncEntityId(Guid.NewGuid());
        var aad = Aad(SyncRecordKind.Upsert, SyncDataCategory.History, entityId);
        var context = Context();
        var unknown = new SyncKeyMaterialHandle(Guid.NewGuid());

        var unknownResult = await _codec.EncryptAsync(
            context,
            unknown,
            aad,
            History(entityId),
            default);
        Assert.True(_registry.Remove(_keyHandle));
        var removedResult = await _codec.EncryptAsync(
            context,
            _keyHandle,
            aad,
            History(entityId),
            default);

        Assert.Equal(ControllerErrorCode.NotFound, unknownResult.Error?.Code);
        Assert.Equal(ControllerErrorCode.NotFound, removedResult.Error?.Code);
    }

    [Fact]
    public async Task RegistryDisposalInvalidatesPreviouslyIssuedHandle()
    {
        var entityId = new SyncEntityId(Guid.NewGuid());
        _registry.Dispose();

        var result = await _codec.EncryptAsync(
            Context(),
            _keyHandle,
            Aad(SyncRecordKind.Upsert, SyncDataCategory.History, entityId),
            History(entityId),
            default);

        Assert.False(result.IsSuccess);
        Assert.Equal(ControllerErrorCode.NotFound, result.Error?.Code);
    }

    [Fact]
    public async Task TombstoneRoundTripsToExactAuthenticatedReceipt()
    {
        var entityId = new SyncEntityId(Guid.NewGuid());
        var aad = Aad(SyncRecordKind.Tombstone, SyncDataCategory.OpenTabs, entityId);
        var context = Context();

        var encrypted = await _codec.EncryptTombstoneAsync(context, _keyHandle, aad, default);
        var authenticated = await _codec.DecryptAndValidateTombstoneAsync(
            context,
            _keyHandle,
            encrypted.Value!,
            default);

        Assert.True(encrypted.IsSuccess);
        Assert.True(authenticated.IsSuccess);
        Assert.True(SyncContractRules.ValidateAuthenticatedTombstone(
            encrypted.Value,
            authenticated.Value).IsValid);
        Assert.Equal(aad.EntityId, authenticated.Value!.EntityId);
        Assert.Equal(aad.ClientSequence, authenticated.Value.ClientSequence);
    }

    [Fact]
    public async Task TombstoneTamperingAndAadSubstitutionFailClosed()
    {
        var entityId = new SyncEntityId(Guid.NewGuid());
        var aad = Aad(SyncRecordKind.Tombstone, SyncDataCategory.History, entityId);
        var context = Context();
        var encrypted = (await _codec.EncryptTombstoneAsync(context, _keyHandle, aad, default)).Value!;
        var mutations = new[]
        {
            encrypted with { Ciphertext = FlipFirst(encrypted.Ciphertext) },
            encrypted with { AuthenticationTag = FlipFirst(encrypted.AuthenticationTag) },
            encrypted with { Aad = aad with { ClientSequence = aad.ClientSequence + 1 } },
        };

        foreach (var mutation in mutations)
        {
            var result = await _codec.DecryptAndValidateTombstoneAsync(
                context,
                _keyHandle,
                mutation,
                default);
            Assert.False(result.IsSuccess);
            Assert.Equal(ControllerErrorCode.IntegrityFailure, result.Error?.Code);
        }
    }

    [Fact]
    public async Task PurgeRoundTripsAndValidatesMarkerAgainstAad()
    {
        var operationId = new SyncOperationId(Guid.NewGuid());
        var entityId = new SyncEntityId(Guid.NewGuid());
        var aad = Aad(
            SyncRecordKind.Purge,
            SyncDataCategory.Settings,
            entityId,
            operationId: operationId);
        var context = Context(operationId);
        var marker = new DecryptedPurgeMarker(
            operationId,
            aad.ProfileId,
            aad.Category,
            aad.ClientGeneration,
            aad.KeysetId);

        var encrypted = await _codec.EncryptPurgeAsync(context, _keyHandle, aad, marker, default);
        var decrypted = await _codec.DecryptAndValidatePurgeAsync(
            context,
            _keyHandle,
            encrypted.Value!,
            default);

        Assert.True(encrypted.IsSuccess);
        Assert.True(decrypted.IsSuccess);
        Assert.Equal(marker, decrypted.Value);
    }

    [Fact]
    public async Task RemotePurgeDecryptsWhenOriginOperationDiffersFromLocalPullRun()
    {
        var remoteOperation = new SyncOperationId(Guid.NewGuid());
        var localPullOperation = new SyncOperationId(Guid.NewGuid());
        var aad = Aad(
            SyncRecordKind.Purge,
            SyncDataCategory.OpenTabs,
            new SyncEntityId(Guid.NewGuid()),
            operationId: remoteOperation);
        var marker = new DecryptedPurgeMarker(
            remoteOperation,
            aad.ProfileId,
            aad.Category,
            aad.ClientGeneration,
            aad.KeysetId);
        var encrypted = await _codec.EncryptPurgeAsync(
            Context(remoteOperation),
            _keyHandle,
            aad,
            marker,
            default);

        var decrypted = await _codec.DecryptAndValidatePurgeAsync(
            Context(localPullOperation),
            _keyHandle,
            encrypted.Value!,
            default);

        Assert.True(decrypted.IsSuccess);
        Assert.Equal(marker, decrypted.Value);
    }

    [Fact]
    public async Task LocalPurgeEncryptionRejectsMismatchedOperation()
    {
        var aadOperation = new SyncOperationId(Guid.NewGuid());
        var aad = Aad(
            SyncRecordKind.Purge,
            SyncDataCategory.History,
            new SyncEntityId(Guid.NewGuid()),
            operationId: aadOperation);
        var marker = new DecryptedPurgeMarker(
            aadOperation,
            aad.ProfileId,
            aad.Category,
            aad.ClientGeneration,
            aad.KeysetId);

        var result = await _codec.EncryptPurgeAsync(
            Context(new SyncOperationId(Guid.NewGuid())),
            _keyHandle,
            aad,
            marker,
            default);

        Assert.False(result.IsSuccess);
        Assert.Equal(ControllerErrorCode.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task PurgeMarkerMismatchAndCiphertextTamperingAreRejected()
    {
        var operationId = new SyncOperationId(Guid.NewGuid());
        var aad = Aad(
            SyncRecordKind.Purge,
            SyncDataCategory.History,
            new SyncEntityId(Guid.NewGuid()),
            operationId: operationId);
        var context = Context(operationId);
        var marker = new DecryptedPurgeMarker(
            operationId,
            aad.ProfileId,
            aad.Category,
            aad.ClientGeneration,
            aad.KeysetId);

        var mismatch = await _codec.EncryptPurgeAsync(
            context,
            _keyHandle,
            aad,
            marker with { Category = SyncDataCategory.Settings },
            default);
        var encrypted = (await _codec.EncryptPurgeAsync(
            context,
            _keyHandle,
            aad,
            marker,
            default)).Value!;
        var tampered = await _codec.DecryptAndValidatePurgeAsync(
            context,
            _keyHandle,
            encrypted with { Ciphertext = FlipFirst(encrypted.Ciphertext) },
            default);

        Assert.Equal(ControllerErrorCode.InvalidRequest, mismatch.Error?.Code);
        Assert.Equal(ControllerErrorCode.IntegrityFailure, tampered.Error?.Code);
    }

    [Fact]
    public async Task UnsupportedPayloadSubtypeIsRejectedBeforeSerialization()
    {
        var entityId = new SyncEntityId(Guid.NewGuid());
        var aad = Aad(SyncRecordKind.Upsert, SyncDataCategory.History, entityId);

        var result = await _codec.EncryptAsync(
            Context(),
            _keyHandle,
            aad,
            new ForbiddenPayload(entityId),
            default);

        Assert.False(result.IsSuccess);
        Assert.Equal(ControllerErrorCode.InvalidRequest, result.Error?.Code);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task PrivateOuterGateRejectsWithZeroCodecCalls()
    {
        var calls = 0;
        var privateBrowsing = Browsing(BrowserProfileMode.Private);

        var result = await SyncOperationGate.ExecuteAsync(
            privateBrowsing,
            new SyncOperationId(Guid.NewGuid()),
            _ =>
            {
                calls++;
                return ValueTask.FromResult(ControllerResult<Probe>.Success(new Probe()));
            });

        Assert.False(result.IsSuccess);
        Assert.Equal(ControllerErrorCode.PolicyDenied, result.Error?.Code);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task CancelledOperationReturnsTypedFailure()
    {
        var entityId = new SyncEntityId(Guid.NewGuid());
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        var result = await _codec.EncryptAsync(
            Context(),
            _keyHandle,
            Aad(SyncRecordKind.Upsert, SyncDataCategory.History, entityId),
            History(entityId),
            source.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal(ControllerErrorCode.Cancelled, result.Error?.Code);
    }

    public void Dispose()
    {
        _registry.Dispose();
    }

    private SyncOperationContext Context(SyncOperationId? operationId = null)
    {
        var result = SyncOperationContext.Authorize(
            Browsing(BrowserProfileMode.Normal),
            operationId ?? new SyncOperationId(Guid.NewGuid()));
        return result.Value!;
    }

    private BrowsingContext Browsing(BrowserProfileMode mode) =>
        new(
            new PrivacyContext(
                _profileId,
                new BrowserSessionId(Guid.NewGuid()),
                mode),
            new BrowserWindowId(Guid.NewGuid()),
            new BrowserTabId(Guid.NewGuid()),
            null);

    private CanonicalSyncAad Aad(
        SyncRecordKind kind,
        SyncDataCategory category,
        SyncEntityId entityId,
        SyncOperationId? operationId = null) =>
        new(
            SyncProtocol.CurrentProtocolVersion,
            SyncProtocol.CurrentSchemaVersion,
            _profileId,
            new DeviceId(Guid.NewGuid()),
            new SyncKeysetId(Guid.NewGuid()),
            3,
            kind,
            new SyncEnvelopeId(Guid.NewGuid()),
            category,
            entityId,
            operationId,
            8,
            13);

    private static HistorySyncRecord History(SyncEntityId entityId) =>
        new(
            entityId,
            1,
            Utc(2026, 8, 8, 9, 0, 0),
            "https://example.test/",
            "Example",
            Utc(2026, 8, 8, 8, 59, 0),
            1);

    private static DateTimeOffset Utc(
        int year,
        int month,
        int day,
        int hour,
        int minute,
        int second) =>
        new(year, month, day, hour, minute, second, TimeSpan.Zero);

    private static byte[] FlipFirst(ReadOnlyMemory<byte> value)
    {
        var mutated = value.ToArray();
        mutated[0] ^= 0x80;
        return mutated;
    }

    private sealed record ForbiddenPayload(SyncEntityId Id)
        : SyncRecordPayload(Id, 0, DateTimeOffset.UnixEpoch)
    {
        public override SyncDataCategory Category => SyncDataCategory.History;

        public string Cookie => "session=must-not-sync";
    }

    private sealed record Probe;
}
