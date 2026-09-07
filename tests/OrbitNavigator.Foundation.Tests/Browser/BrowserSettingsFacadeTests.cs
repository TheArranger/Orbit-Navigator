using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Updates;
using OrbitNavigator.Foundation.Browser;
using OrbitNavigator.Foundation.Profiles;
using Xunit;

namespace OrbitNavigator.Foundation.Tests.Browser;

public sealed class BrowserSettingsFacadeTests
{
    [Fact]
    public async Task SettingsAreDurableAndRevisioned()
    {
        using var temp = new TempDirectory();
        var storage = new FileProfileStorage(temp.Path);
        var facade = new BrowserSettingsFacade(storage);
        var context = Context(BrowserProfileMode.Normal);
        var initial = await facade.GetAsync(context);
        var values = new BrowserCoreSettings("duckduckgo", false, false, UpdatePreference.Automatic);

        var saved = await facade.UpdateAsync(new(context, initial.Value!.Revision, values));
        var durable = await new BrowserSettingsFacade(storage).GetAsync(context);
        var stale = await facade.UpdateAsync(new(context, initial.Value.Revision, values));

        Assert.True(saved.IsSuccess);
        Assert.Equal(values, durable.Value!.Values);
        Assert.Equal(saved.Value!.Revision, durable.Value.Revision);
        Assert.Equal(ControllerErrorCode.Conflict, stale.Error?.Code);
    }

    [Fact]
    public async Task PrivateWindowReadsButCannotPersistNormalSettings()
    {
        using var temp = new TempDirectory();
        var facade = new BrowserSettingsFacade(new FileProfileStorage(temp.Path));
        var profile = new ProfileId(Guid.NewGuid());
        var normal = Context(BrowserProfileMode.Normal, profile);
        var privateContext = Context(BrowserProfileMode.Private, profile);
        var values = new BrowserCoreSettings("duckduckgo", false, true, UpdatePreference.NotifyOnly);
        var saved = await facade.UpdateAsync(new(normal, default, values));

        var privateRead = await facade.GetAsync(privateContext);
        var privateWrite = await facade.UpdateAsync(new(
            privateContext,
            privateRead.Value!.Revision,
            values with { RestoreOpenTabs = true }));

        Assert.Equal(values, privateRead.Value.Values);
        Assert.Equal(ControllerErrorCode.PolicyDenied, privateWrite.Error?.Code);
        Assert.Equal(saved.Value!.Revision, (await facade.GetAsync(normal)).Value!.Revision);
    }

    private static PrivacyContext Context(BrowserProfileMode mode, ProfileId? profile = null) =>
        new(
            profile ?? new ProfileId(Guid.NewGuid()),
            new BrowserSessionId(Guid.NewGuid()),
            mode);
}
