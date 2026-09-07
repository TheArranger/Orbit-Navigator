using System.Security.Cryptography;
using System.Text;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Sync;
using OrbitNavigator.Sync.Accounts;
using Xunit;

namespace OrbitNavigator.Sync.Tests.Accounts;

public sealed class DpapiMyOrbitCredentialVaultTests
{
    [Fact]
    public async Task RefreshCredentialIsProtectedWithDedicatedCurrentUserPurposeAndRoundTrips()
    {
        var storage = new MemoryStorage();
        var protection = new XorProtection();
        var vault = new DpapiMyOrbitCredentialVault(storage, protection);
        var context = OperationContext();
        var refreshBytes = Token("mort_");
        using var tokens = Tokens(refreshBytes);

        var saved = await vault.SaveAsync(context, null, tokens, false, CancellationToken.None);

        Assert.True(saved.IsSuccess);
        Assert.Equal(WindowsKeyProtectionPurpose.MyOrbitConnectionCredential, protection.LastProtectPurpose);
        Assert.NotNull(storage.Payload);
        Assert.False(Contains(storage.Payload!, refreshBytes));

        saved.Value!.Dispose();
        var loaded = await vault.LoadAsync(context, CancellationToken.None);
        Assert.True(loaded.IsSuccess);
        using var credential = loaded.Value!;
        Assert.Equal(refreshBytes, credential.RefreshCredential.Bytes.ToArray());
        Assert.Equal(WindowsKeyProtectionPurpose.MyOrbitConnectionCredential, protection.LastUnprotectPurpose);
        Assert.Equal("orbit-user", credential.AccountLabel);
        Assert.False(credential.RevocationPending);
    }

    [Fact]
    public async Task CorruptFrameFailsClosedBeforeUnprotect()
    {
        var storage = new MemoryStorage
        {
            Payload = [1, 2, 3, 4],
            Revision = new ProfileStorageRevision(Guid.NewGuid()),
        };
        var protection = new XorProtection();
        var vault = new DpapiMyOrbitCredentialVault(storage, protection);

        var loaded = await vault.LoadAsync(OperationContext(), CancellationToken.None);

        Assert.Equal(ControllerErrorCode.IntegrityFailure, loaded.Error?.Code);
        Assert.Null(protection.LastUnprotectPurpose);
    }

    [Fact]
    public async Task SaveUsesStorageRevisionCasForRotation()
    {
        var storage = new MemoryStorage();
        var vault = new DpapiMyOrbitCredentialVault(storage, new XorProtection());
        var context = OperationContext();
        using var first = Tokens(Token("mort_", 'A'));
        var saved = await vault.SaveAsync(context, null, first, false, CancellationToken.None);
        Assert.True(saved.IsSuccess);
        using var expected = saved.Value!;
        using var second = Tokens(Token("mort_", 'B'));

        var rotated = await vault.SaveAsync(context, expected, second, false, CancellationToken.None);

        Assert.True(rotated.IsSuccess);
        Assert.Equal(expected.StorageRevision, storage.LastExpectedRevision);
        rotated.Value!.Dispose();
    }

    private static SyncOperationContext OperationContext()
    {
        var browsing = new BrowsingContext(
            new PrivacyContext(new ProfileId(Guid.NewGuid()), new BrowserSessionId(Guid.NewGuid()), BrowserProfileMode.Normal),
            new BrowserWindowId(Guid.NewGuid()),
            new BrowserTabId(Guid.NewGuid()),
            null);
        return SyncOperationContext.Authorize(browsing, new SyncOperationId(Guid.NewGuid())).Value!;
    }

    private static MyOrbitTokenSet Tokens(byte[] refresh) => new(
        new SensitiveUtf8Buffer(Token("moat_")),
        new SensitiveUtf8Buffer(refresh),
        new DeviceId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
        "orbit-user",
        new DateTimeOffset(2026, 8, 15, 12, 10, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero));

    private static byte[] Token(string prefix, char fill = 'A') =>
        Encoding.ASCII.GetBytes(prefix + new string(fill, 43));

    private static bool Contains(byte[] source, byte[] value) => source.AsSpan().IndexOf(value) >= 0;

    private sealed class MemoryStorage : IProfileStorage
    {
        public byte[]? Payload { get; set; }
        public ProfileStorageRevision? Revision { get; set; }
        public ProfileStorageRevision? LastExpectedRevision { get; private set; }

        public ValueTask<ControllerResult<ProfileStorageEntry>> ReadAsync(
            ProfileStorageAddress address,
            CancellationToken cancellationToken = default)
        {
            if (Payload is null || Revision is null)
                return ValueTask.FromResult(ControllerResult<ProfileStorageEntry>.Failure(
                    ControllerError.Create(ControllerErrorCode.NotFound, "error.profile_storage.not_found")));
            return ValueTask.FromResult(ProfileStorageEntry.Create(Revision.Value, Payload));
        }

        public ValueTask<ControllerResult<ProfileStorageWriteReceipt>> WriteAsync(
            ProfileStorageWriteRequest request,
            CancellationToken cancellationToken = default)
        {
            LastExpectedRevision = request.ExpectedRevision;
            if (request.ExpectedRevision is { } expected && expected != Revision)
                return ValueTask.FromResult(ControllerResult<ProfileStorageWriteReceipt>.Failure(
                    ControllerError.Create(ControllerErrorCode.Conflict, "error.profile_storage.revision_conflict")));
            Payload = request.Payload.ToArray();
            Revision = new ProfileStorageRevision(Guid.NewGuid());
            return ValueTask.FromResult(ControllerResult<ProfileStorageWriteReceipt>.Success(new(Revision.Value)));
        }

        public ValueTask<ControllerResult> DeleteAsync(
            ProfileStorageAddress address,
            ProfileStorageRevision? expectedRevision = null,
            CancellationToken cancellationToken = default)
        {
            if (expectedRevision is { } expected && expected != Revision)
                return ValueTask.FromResult(ControllerResult.Failure(
                    ControllerError.Create(ControllerErrorCode.Conflict, "error.profile_storage.revision_conflict")));
            Payload = null;
            Revision = null;
            return ValueTask.FromResult(ControllerResult.Success());
        }
    }

    private sealed class XorProtection : IWindowsKeyProtection
    {
        public WindowsKeyProtectionPurpose? LastProtectPurpose { get; private set; }
        public WindowsKeyProtectionPurpose? LastUnprotectPurpose { get; private set; }

        public ValueTask<ControllerResult<ProtectedKeyBlob>> ProtectAsync(
            ProtectKeyRequest request,
            CancellationToken cancellationToken = default)
        {
            LastProtectPurpose = request.Purpose;
            var transformed = Transform(request.Plaintext.Span);
            return ValueTask.FromResult(ProtectedKeyBlob.Create("test-protected-v1", transformed));
        }

        public ValueTask<ControllerResult<IUnprotectedKeyMaterial>> UnprotectAsync(
            UnprotectKeyRequest request,
            CancellationToken cancellationToken = default)
        {
            LastUnprotectPurpose = request.Purpose;
            IUnprotectedKeyMaterial material = new Material(Transform(request.ProtectedBlob.Bytes.Span));
            return ValueTask.FromResult(ControllerResult<IUnprotectedKeyMaterial>.Success(material));
        }

        private static byte[] Transform(ReadOnlySpan<byte> value)
        {
            var result = value.ToArray();
            for (var index = 0; index < result.Length; index++) result[index] ^= 0xa5;
            return result;
        }
    }

    private sealed class Material(byte[] bytes) : IUnprotectedKeyMaterial
    {
        private byte[]? value = bytes;
        public ReadOnlyMemory<byte> Bytes => value ?? ReadOnlyMemory<byte>.Empty;
        public void Dispose()
        {
            if (value is null) return;
            CryptographicOperations.ZeroMemory(value);
            value = null;
        }
    }
}
