using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Foundation.Browser;
using OrbitNavigator.Foundation.Profiles;
using Xunit;

namespace OrbitNavigator.Foundation.Tests.Browser;

public sealed class AffiliatedSitesVisibilityStoreTests
{
    [Fact]
    public async Task VisibilityIsDurableMonotonicRevisionedAndProfileLocal()
    {
        using var temp = new TempDirectory();
        var storage = new FileProfileStorage(temp.Path);
        var store = new AffiliatedSitesVisibilityStore(storage);
        var context = Context(BrowserProfileMode.Normal);

        var initial = await store.LoadAsync(context);
        var hidden = await store.SaveAsync(new(context, initial.Value!.Revision, true));
        var visible = await store.SaveAsync(new(context, hidden.Value!.Revision, false));
        var reloaded = await new AffiliatedSitesVisibilityStore(storage).LoadAsync(context);
        var stale = await store.SaveAsync(new(context, initial.Value.Revision, true));

        Assert.Equal(0, initial.Value.Revision);
        Assert.Equal(1, hidden.Value!.Revision);
        Assert.Equal(2, visible.Value!.Revision);
        Assert.False(reloaded.Value!.IsHidden);
        Assert.Equal(2, reloaded.Value.Revision);
        Assert.False(stale.IsSuccess);
        Assert.Equal(ControllerErrorCode.Conflict, stale.Error?.Code);
    }

    [Fact]
    public async Task PrivateReadsNormalChoiceButCannotPersistAModification()
    {
        using var temp = new TempDirectory();
        var storage = new FileProfileStorage(temp.Path);
        var store = new AffiliatedSitesVisibilityStore(storage);
        var profile = new ProfileId(Guid.NewGuid());
        var normal = Context(BrowserProfileMode.Normal, profile);
        var privateContext = Context(BrowserProfileMode.Private, profile);

        var saved = await store.SaveAsync(new(normal, 0, true));
        var privateRead = await store.LoadAsync(privateContext);
        var privateWrite = await store.SaveAsync(new(privateContext, privateRead.Value!.Revision, false));
        var normalRead = await store.LoadAsync(normal);

        Assert.True(saved.IsSuccess);
        Assert.True(privateRead.Value!.IsHidden);
        Assert.False(privateWrite.IsSuccess);
        Assert.Equal(ControllerErrorCode.PolicyDenied, privateWrite.Error?.Code);
        Assert.True(normalRead.Value!.IsHidden);
        Assert.Equal(saved.Value!.Revision, normalRead.Value.Revision);
    }

    private static PrivacyContext Context(BrowserProfileMode mode, ProfileId? profile = null) =>
        new(
            profile ?? new ProfileId(Guid.NewGuid()),
            new BrowserSessionId(Guid.NewGuid()),
            mode);
}
