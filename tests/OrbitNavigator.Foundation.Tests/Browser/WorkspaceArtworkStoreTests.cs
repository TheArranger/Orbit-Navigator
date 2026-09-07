using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Foundation.Browser;
using OrbitNavigator.Foundation.Profiles;
using Xunit;

namespace OrbitNavigator.Foundation.Tests.Browser;

public sealed class WorkspaceArtworkStoreTests
{
    private static readonly byte[] SafePng = [137, 80, 78, 71, 13, 10, 26, 10, 0];

    [Fact]
    public async Task NormalImportUsesOpaqueIdAndPrivateCanOnlyRead()
    {
        using var temp = new TempDirectory();
        var store = new WorkspaceArtworkStore(new FileProfileStorage(temp.Path));
        var profile = new ProfileId(Guid.NewGuid());
        var normal = Context(profile, BrowserProfileMode.Normal);
        var privateContext = Context(profile, BrowserProfileMode.Private);

        var imported = await store.ImportAsync(normal, SafePng);
        var privateRead = await store.LoadAsync(privateContext, imported.Value!.AssetId);
        var privateWrite = await store.ImportAsync(privateContext, SafePng);

        Assert.True(imported.IsSuccess);
        Assert.StartsWith("wa_", imported.Value.AssetId, StringComparison.Ordinal);
        Assert.Equal(SafePng, privateRead.Value!.PngBytes.ToArray());
        Assert.Equal(ControllerErrorCode.PolicyDenied, privateWrite.Error?.Code);
    }

    [Fact]
    public async Task InvalidOrOversizedPayloadIsRejected()
    {
        using var temp = new TempDirectory();
        var store = new WorkspaceArtworkStore(new FileProfileStorage(temp.Path));
        var context = Context(new ProfileId(Guid.NewGuid()), BrowserProfileMode.Normal);

        var invalid = await store.ImportAsync(context, new byte[] { 1, 2, 3 });
        var oversized = new byte[WorkspaceArtworkStore.MaximumPngBytes + 1];
        SafePng.CopyTo(oversized, 0);
        var tooLarge = await store.ImportAsync(context, oversized);

        Assert.Equal(ControllerErrorCode.InvalidRequest, invalid.Error?.Code);
        Assert.Equal(ControllerErrorCode.InvalidRequest, tooLarge.Error?.Code);
    }

    private static PrivacyContext Context(ProfileId profile, BrowserProfileMode mode) =>
        new(profile, new BrowserSessionId(Guid.NewGuid()), mode);
}
