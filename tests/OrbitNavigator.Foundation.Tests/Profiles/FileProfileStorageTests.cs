using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Foundation.Profiles;
using Xunit;

namespace OrbitNavigator.Foundation.Tests.Profiles;

public sealed class FileProfileStorageTests
{
    [Fact]
    public async Task LocalProfileIdentityPersistsWithoutAccount()
    {
        using var temp = new TempDirectory();
        var store = new LocalProfileIdentityStore(Path.Combine(temp.Path, "profile.id"));

        var first = await store.LoadOrCreateAsync();
        var second = await store.LoadOrCreateAsync();

        Assert.False(first.IsEmpty);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task WritesReadsAndUsesOptimisticRevisionChecks()
    {
        using var temp = new TempDirectory();
        var storage = new FileProfileStorage(temp.Path);
        var address = Address(Context(BrowserProfileMode.Normal), ProfileStorageDurability.Persistent);
        var writeRequest = ProfileStorageWriteRequest.Create(address, new byte[] { 1, 2, 3 }).Value!;

        var first = await storage.WriteAsync(writeRequest);
        var read = await storage.ReadAsync(address);
        var stale = await storage.WriteAsync(ProfileStorageWriteRequest.Create(
            address,
            new byte[] { 4 },
            new ProfileStorageRevision(Guid.NewGuid())).Value!);

        Assert.True(first.IsSuccess);
        Assert.True(read.IsSuccess);
        Assert.Equal(new byte[] { 1, 2, 3 }, read.Value?.Payload.ToArray());
        Assert.Equal(first.Value?.Revision, read.Value?.Revision);
        Assert.False(stale.IsSuccess);
        Assert.Equal(ControllerErrorCode.Conflict, stale.Error?.Code);
    }

    [Fact]
    public async Task PrivateSessionsWithSameProfileCannotReadEachOthersEntries()
    {
        using var temp = new TempDirectory();
        var storage = new FileProfileStorage(temp.Path);
        var profile = new ProfileId(Guid.NewGuid());
        var first = Address(
            Context(BrowserProfileMode.Private, profile),
            ProfileStorageDurability.Session);
        var second = Address(
            Context(BrowserProfileMode.Private, profile),
            ProfileStorageDurability.Session);

        await storage.WriteAsync(ProfileStorageWriteRequest.Create(first, new byte[] { 1 }).Value!);
        var crossRead = await storage.ReadAsync(second);

        Assert.False(crossRead.IsSuccess);
        Assert.Equal(ControllerErrorCode.NotFound, crossRead.Error?.Code);
    }

    private static ProfileStorageAddress Address(
        PrivacyContext context,
        ProfileStorageDurability durability) =>
        ProfileStorageAddress.Create(
            context,
            ProfileStorageNamespace.Create("settings").Value,
            ProfileStorageKey.Create("main").Value,
            durability).Value!;

    private static PrivacyContext Context(
        BrowserProfileMode mode,
        ProfileId? profile = null) =>
        new(
            profile ?? new ProfileId(Guid.NewGuid()),
            new BrowserSessionId(Guid.NewGuid()),
            mode);
}
