using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Foundation.Browser;
using OrbitNavigator.Foundation.Profiles;
using Xunit;

namespace OrbitNavigator.Foundation.Tests.Browser;

public sealed class LegacyWorkspaceMetadataRetirementTests
{
    [Fact]
    public async Task DeletesOnlyExactLegacyNamespaceAfterAuthoritativeNormalSessionExists()
    {
        using var temp = new TempDirectory();
        var storage = new FileProfileStorage(temp.Path);
        var context = Context(BrowserProfileMode.Normal);
        var firstLegacy = Address(context, "browser.tab-groups", "window:one");
        var secondLegacy = Address(context, "browser.tab-groups", "window:two");
        var unrelated = Address(context, "browser.workspace-ui", "preferences");
        await storage.WriteAsync(ProfileStorageWriteRequest.Create(firstLegacy, [1]).Value!);
        await storage.WriteAsync(ProfileStorageWriteRequest.Create(secondLegacy, [2]).Value!);
        await storage.WriteAsync(ProfileStorageWriteRequest.Create(unrelated, [3]).Value!);
        var session = Session(context.ProfileId);

        var retirement = new LegacyWorkspaceMetadataRetirement(storage, storage);
        var first = await retirement.RetireAsync(context, session);
        var repeated = await retirement.RetireAsync(context, session);

        Assert.True(first.IsSuccess);
        Assert.False(first.Value!.AlreadyRetired);
        Assert.Equal(2, first.Value.DeletedLegacyFiles);
        Assert.True(repeated.Value!.AlreadyRetired);
        Assert.Equal(ControllerErrorCode.NotFound, (await storage.ReadAsync(firstLegacy)).Error?.Code);
        Assert.Equal(ControllerErrorCode.NotFound, (await storage.ReadAsync(secondLegacy)).Error?.Code);
        Assert.True((await storage.ReadAsync(unrelated)).IsSuccess);
    }

    [Fact]
    public async Task PrivateRetirementIsDeniedBeforeNormalLegacyDataCanChange()
    {
        using var temp = new TempDirectory();
        var storage = new FileProfileStorage(temp.Path);
        var profile = new ProfileId(Guid.NewGuid());
        var normal = Context(BrowserProfileMode.Normal, profile);
        var privateContext = Context(BrowserProfileMode.Private, profile);
        var legacy = Address(normal, "browser.tab-groups", "window:one");
        await storage.WriteAsync(ProfileStorageWriteRequest.Create(legacy, [1]).Value!);

        var result = await new LegacyWorkspaceMetadataRetirement(storage, storage)
            .RetireAsync(privateContext, Session(profile));

        Assert.Equal(ControllerErrorCode.PolicyDenied, result.Error?.Code);
        Assert.True((await storage.ReadAsync(legacy)).IsSuccess);
    }

    private static BrowserWorkspaceSessionSnapshot Session(ProfileId profile)
    {
        var tab = new BrowserTabId(Guid.NewGuid());
        return new(
            profile,
            new BrowserWorkspaceSessionRevision(Guid.NewGuid()),
            new BrowserWindowId(Guid.NewGuid()),
            tab,
            [new(tab, null, "New Tab", null)],
            []);
    }

    private static PrivacyContext Context(BrowserProfileMode mode, ProfileId? profile = null) => new(
        profile ?? new ProfileId(Guid.NewGuid()),
        new BrowserSessionId(Guid.NewGuid()),
        mode);

    private static ProfileStorageAddress Address(
        PrivacyContext context,
        string storageNamespace,
        string key) => ProfileStorageAddress.Create(
            context,
            ProfileStorageNamespace.Create(storageNamespace).Value,
            ProfileStorageKey.Create(key).Value,
            ProfileStorageDurability.Persistent).Value!;
}
