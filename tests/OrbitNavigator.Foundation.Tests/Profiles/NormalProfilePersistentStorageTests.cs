using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Foundation.Profiles;
using Xunit;

namespace OrbitNavigator.Foundation.Tests.Profiles;

public sealed class NormalProfilePersistentStorageTests
{
    [Fact]
    public async Task AllowsOnlyMatchingNormalProfilePersistentAddresses()
    {
        var profile = new ProfileId(Guid.NewGuid());
        var inner = new RecordingStorage();
        var adapter = new NormalProfilePersistentStorage(inner, profile);
        var allowed = Address(profile, BrowserProfileMode.Normal, ProfileStorageDurability.Persistent);

        await adapter.ReadAsync(allowed);
        await adapter.WriteAsync(ProfileStorageWriteRequest.Create(allowed, [1]).Value!);
        await adapter.DeleteAsync(allowed);

        Assert.Equal(3, inner.CallCount);
    }

    [Theory]
    [InlineData(BrowserProfileMode.Normal, ProfileStorageDurability.Session)]
    [InlineData(BrowserProfileMode.Private, ProfileStorageDurability.Session)]
    public async Task DeniesSessionAndPrivateWithoutCallingInner(
        BrowserProfileMode mode,
        ProfileStorageDurability durability)
    {
        var profile = new ProfileId(Guid.NewGuid());
        var inner = new RecordingStorage();
        var adapter = new NormalProfilePersistentStorage(inner, profile);
        var address = Address(profile, mode, durability);

        var read = await adapter.ReadAsync(address);
        var write = await adapter.WriteAsync(ProfileStorageWriteRequest.Create(address, [1]).Value!);
        var delete = await adapter.DeleteAsync(address);

        Assert.Equal(ControllerErrorCode.PolicyDenied, read.Error?.Code);
        Assert.Equal(ControllerErrorCode.PolicyDenied, write.Error?.Code);
        Assert.Equal(ControllerErrorCode.PolicyDenied, delete.Error?.Code);
        Assert.Equal(0, inner.CallCount);
    }

    [Fact]
    public async Task DeniesAnotherProfileWithoutCallingInner()
    {
        var profile = new ProfileId(Guid.NewGuid());
        var inner = new RecordingStorage();
        var adapter = new NormalProfilePersistentStorage(inner, profile);

        var result = await adapter.ReadAsync(Address(
            new ProfileId(Guid.NewGuid()),
            BrowserProfileMode.Normal,
            ProfileStorageDurability.Persistent));

        Assert.Equal(ControllerErrorCode.PolicyDenied, result.Error?.Code);
        Assert.Equal(0, inner.CallCount);
    }

    private static ProfileStorageAddress Address(
        ProfileId profile,
        BrowserProfileMode mode,
        ProfileStorageDurability durability) =>
        ProfileStorageAddress.Create(
            new PrivacyContext(profile, new BrowserSessionId(Guid.NewGuid()), mode),
            ProfileStorageNamespace.Create("privacy-rules").Value,
            ProfileStorageKey.Create("rules-v1").Value,
            durability).Value!;

    private sealed class RecordingStorage : IProfileStorage
    {
        public int CallCount { get; private set; }

        public ValueTask<ControllerResult<ProfileStorageEntry>> ReadAsync(
            ProfileStorageAddress address,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult(ControllerResult<ProfileStorageEntry>.Failure(
                ControllerError.Create(ControllerErrorCode.NotFound, "error.test.not_found")));
        }

        public ValueTask<ControllerResult<ProfileStorageWriteReceipt>> WriteAsync(
            ProfileStorageWriteRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult(ControllerResult<ProfileStorageWriteReceipt>.Success(
                new ProfileStorageWriteReceipt(new ProfileStorageRevision(Guid.NewGuid()))));
        }

        public ValueTask<ControllerResult> DeleteAsync(
            ProfileStorageAddress address,
            ProfileStorageRevision? expectedRevision = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult(ControllerResult.Success());
        }
    }
}
