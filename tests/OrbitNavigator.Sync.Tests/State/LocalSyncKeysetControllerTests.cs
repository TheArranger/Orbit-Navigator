using System.Security.Cryptography;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Sync;
using OrbitNavigator.Sync.Cryptography;
using OrbitNavigator.Sync.State;
using Xunit;

namespace OrbitNavigator.Sync.Tests.State;

public sealed class LocalSyncKeysetControllerTests
{
    [Fact]
    public async Task PrivateInitializationIsDeniedBeforeStorageOrProtection()
    {
        var storage = new MemoryStorage();
        var protection = new XorProtection();
        using var registry = new InProcessSyncKeyMaterialRegistry();
        await using var controller = new LocalSyncKeysetController(storage, protection, registry);

        var result = await controller.InitializeAsync(
            Browsing(BrowserProfileMode.Private),
            new SyncOperationId(Guid.NewGuid()));

        Assert.Equal(ControllerErrorCode.PolicyDenied, result.Error?.Code);
        Assert.Equal(0, storage.CallCount);
        Assert.Equal(0, protection.CallCount);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task FirstInitializationProtectsRootAndPersistsNoPlaintextKey()
    {
        var storage = new MemoryStorage();
        var protection = new XorProtection();
        using var registry = new InProcessSyncKeyMaterialRegistry();
        await using var controller = new LocalSyncKeysetController(storage, protection, registry);
        var browsing = Browsing(BrowserProfileMode.Normal);

        var result = await controller.InitializeAsync(browsing, new SyncOperationId(Guid.NewGuid()));

        Assert.True(result.IsSuccess);
        Assert.Equal(browsing.Privacy.ProfileId, result.Value!.ProfileId);
        Assert.True(result.Value.KeysetId.IsDefined);
        Assert.Equal(0, result.Value.KeyEpoch);
        Assert.NotEqual(Guid.Empty, result.Value.KeyMaterial.Value);
        Assert.Equal(WindowsKeyProtectionPurpose.SyncKeysetWrappingKey, protection.LastProtectPurpose);
        Assert.NotNull(protection.LastProtectedPlaintext);
        Assert.NotNull(storage.Payload);
        Assert.False(storage.Payload!.AsSpan().IndexOf(
            protection.LastProtectedPlaintext!.AsSpan()[^InProcessSyncKeyMaterialRegistry.RootKeySizeBytes..]) >= 0);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public async Task ConcurrentControllersConvergeOnOnePersistedKeyset()
    {
        var storage = new MemoryStorage();
        var protection = new XorProtection();
        using var firstRegistry = new InProcessSyncKeyMaterialRegistry();
        using var secondRegistry = new InProcessSyncKeyMaterialRegistry();
        await using var firstController = new LocalSyncKeysetController(
            storage,
            protection,
            firstRegistry);
        await using var secondController = new LocalSyncKeysetController(
            storage,
            protection,
            secondRegistry);
        var firstBrowsing = Browsing(BrowserProfileMode.Normal, KnownProfile);
        var secondBrowsing = Browsing(BrowserProfileMode.Normal, KnownProfile);

        var results = await Task.WhenAll(
            firstController.InitializeAsync(
                firstBrowsing,
                new SyncOperationId(Guid.NewGuid())).AsTask(),
            secondController.InitializeAsync(
                secondBrowsing,
                new SyncOperationId(Guid.NewGuid())).AsTask());

        Assert.All(results, result => Assert.True(result.IsSuccess));
        var first = Assert.IsType<LocalSyncKeysetSession>(results[0].Value);
        var second = Assert.IsType<LocalSyncKeysetSession>(results[1].Value);
        Assert.Equal(first.KeysetId, second.KeysetId);
        Assert.NotEqual(first.KeyMaterial, second.KeyMaterial);
        Assert.Equal(1, storage.WriteCount);
    }

    [Fact]
    public async Task RestartRestoresSameKeysetBehindANewOpaqueHandle()
    {
        var storage = new MemoryStorage();
        var protection = new XorProtection();
        LocalSyncKeysetSession first;
        using (var firstRegistry = new InProcessSyncKeyMaterialRegistry())
        {
            await using var firstController = new LocalSyncKeysetController(storage, protection, firstRegistry);
            first = (await firstController.InitializeAsync(
                Browsing(BrowserProfileMode.Normal, KnownProfile),
                new SyncOperationId(Guid.NewGuid()))).Value!;
        }

        using var secondRegistry = new InProcessSyncKeyMaterialRegistry();
        await using var secondController = new LocalSyncKeysetController(storage, protection, secondRegistry);
        var restored = await secondController.InitializeAsync(
            Browsing(BrowserProfileMode.Normal, KnownProfile),
            new SyncOperationId(Guid.NewGuid()));

        Assert.True(restored.IsSuccess);
        Assert.Equal(first.KeysetId, restored.Value!.KeysetId);
        Assert.Equal(first.KeyEpoch, restored.Value.KeyEpoch);
        Assert.NotEqual(first.KeyMaterial, restored.Value.KeyMaterial);
        Assert.Equal(WindowsKeyProtectionPurpose.SyncKeysetWrappingKey, protection.LastUnprotectPurpose);
    }

    [Fact]
    public async Task MixedProfileProtectedEnvelopeFailsClosed()
    {
        var storage = new MemoryStorage();
        var protection = new XorProtection();
        using (var registry = new InProcessSyncKeyMaterialRegistry())
        {
            await using var controller = new LocalSyncKeysetController(storage, protection, registry);
            Assert.True((await controller.InitializeAsync(
                Browsing(BrowserProfileMode.Normal, KnownProfile),
                new SyncOperationId(Guid.NewGuid()))).IsSuccess);
        }

        using var otherRegistry = new InProcessSyncKeyMaterialRegistry();
        await using var otherController = new LocalSyncKeysetController(storage, protection, otherRegistry);
        var result = await otherController.InitializeAsync(
            Browsing(BrowserProfileMode.Normal, new ProfileId(Guid.NewGuid())),
            new SyncOperationId(Guid.NewGuid()));

        Assert.Equal(ControllerErrorCode.IntegrityFailure, result.Error?.Code);
        Assert.Equal(0, otherRegistry.Count);
    }

    [Fact]
    public async Task CorruptOuterEnvelopeFailsBeforeUnprotect()
    {
        var storage = new MemoryStorage { Payload = [1, 2, 3, 4] };
        var protection = new XorProtection();
        using var registry = new InProcessSyncKeyMaterialRegistry();
        await using var controller = new LocalSyncKeysetController(storage, protection, registry);

        var result = await controller.InitializeAsync(
            Browsing(BrowserProfileMode.Normal),
            new SyncOperationId(Guid.NewGuid()));

        Assert.Equal(ControllerErrorCode.IntegrityFailure, result.Error?.Code);
        Assert.Null(protection.LastUnprotectPurpose);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task DisposalRemovesRegisteredKeyMaterialAndFutureCallsFailClosed()
    {
        var storage = new MemoryStorage();
        using var registry = new InProcessSyncKeyMaterialRegistry();
        var controller = new LocalSyncKeysetController(storage, new XorProtection(), registry);
        var browsing = Browsing(BrowserProfileMode.Normal);
        var initialized = await controller.InitializeAsync(
            browsing,
            new SyncOperationId(Guid.NewGuid()));
        Assert.True(initialized.IsSuccess);
        Assert.Equal(1, registry.Count);

        await controller.DisposeAsync();

        Assert.Equal(0, registry.Count);
        var afterDispose = await controller.InitializeAsync(
            browsing,
            new SyncOperationId(Guid.NewGuid()));
        Assert.Equal(ControllerErrorCode.Unavailable, afterDispose.Error?.Code);
    }

    private static readonly ProfileId KnownProfile =
        new(Guid.Parse("11111111-2222-3333-4444-555555555555"));

    private static BrowsingContext Browsing(
        BrowserProfileMode mode,
        ProfileId? profileId = null) =>
        new(
            new PrivacyContext(
                profileId ?? new ProfileId(Guid.NewGuid()),
                new BrowserSessionId(Guid.NewGuid()),
                mode),
            new BrowserWindowId(Guid.NewGuid()),
            new BrowserTabId(Guid.NewGuid()),
            null);

    private sealed class MemoryStorage : IProfileStorage
    {
        public byte[]? Payload { get; set; }
        public ProfileStorageRevision? Revision { get; private set; }
        public int CallCount { get; private set; }
        public int WriteCount { get; private set; }

        public ValueTask<ControllerResult<ProfileStorageEntry>> ReadAsync(
            ProfileStorageAddress address,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (Payload is null)
            {
                return ValueTask.FromResult(ControllerResult<ProfileStorageEntry>.Failure(
                    ControllerError.Create(
                        ControllerErrorCode.NotFound,
                        "error.profile_storage.not-found")));
            }

            Revision ??= new ProfileStorageRevision(Guid.NewGuid());
            return ValueTask.FromResult(ProfileStorageEntry.Create(Revision.Value, Payload));
        }

        public ValueTask<ControllerResult<ProfileStorageWriteReceipt>> WriteAsync(
            ProfileStorageWriteRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            WriteCount++;
            Payload = request.Payload.ToArray();
            Revision = new ProfileStorageRevision(Guid.NewGuid());
            return ValueTask.FromResult(
                ControllerResult<ProfileStorageWriteReceipt>.Success(new(Revision.Value)));
        }

        public ValueTask<ControllerResult> DeleteAsync(
            ProfileStorageAddress address,
            ProfileStorageRevision? expectedRevision = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Payload = null;
            Revision = null;
            return ValueTask.FromResult(ControllerResult.Success());
        }
    }

    private sealed class XorProtection : IWindowsKeyProtection
    {
        public int CallCount { get; private set; }
        public WindowsKeyProtectionPurpose? LastProtectPurpose { get; private set; }
        public WindowsKeyProtectionPurpose? LastUnprotectPurpose { get; private set; }
        public byte[]? LastProtectedPlaintext { get; private set; }

        public ValueTask<ControllerResult<ProtectedKeyBlob>> ProtectAsync(
            ProtectKeyRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastProtectPurpose = request.Purpose;
            LastProtectedPlaintext = request.Plaintext.ToArray();
            return ValueTask.FromResult(
                ProtectedKeyBlob.Create("test-dpapi-v1", Transform(request.Plaintext.Span)));
        }

        public ValueTask<ControllerResult<IUnprotectedKeyMaterial>> UnprotectAsync(
            UnprotectKeyRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastUnprotectPurpose = request.Purpose;
            IUnprotectedKeyMaterial material = new Material(Transform(request.ProtectedBlob.Bytes.Span));
            return ValueTask.FromResult(
                ControllerResult<IUnprotectedKeyMaterial>.Success(material));
        }

        private static byte[] Transform(ReadOnlySpan<byte> bytes)
        {
            var transformed = bytes.ToArray();
            for (var index = 0; index < transformed.Length; index++)
                transformed[index] ^= 0xa5;
            return transformed;
        }
    }

    private sealed class Material(byte[] bytes) : IUnprotectedKeyMaterial
    {
        private byte[]? _bytes = bytes;

        public ReadOnlyMemory<byte> Bytes => _bytes ?? ReadOnlyMemory<byte>.Empty;

        public void Dispose()
        {
            if (_bytes is null)
                return;
            CryptographicOperations.ZeroMemory(_bytes);
            _bytes = null;
        }
    }
}
