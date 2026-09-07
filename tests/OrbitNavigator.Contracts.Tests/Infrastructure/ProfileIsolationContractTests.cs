using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using Xunit;

namespace OrbitNavigator.Contracts.Tests.Infrastructure;

public sealed class ProfileIsolationContractTests
{
    [Fact]
    public void PrivateProfileCannotCreatePersistentStorageAddress()
    {
        var context = CreateContext(BrowserProfileMode.Private);
        var storageNamespace = Required(ProfileStorageNamespace.Create("privacy.rules"));
        var key = Required(ProfileStorageKey.Create("site.example"));

        var result = ProfileStorageAddress.Create(
            context,
            storageNamespace,
            key,
            ProfileStorageDurability.Persistent);

        Assert.False(result.IsSuccess);
        Assert.Equal(ControllerErrorCode.PolicyDenied, result.Error?.Code);
        Assert.Equal("error.private.persistent_storage_denied", result.Error?.MessageKey);
    }

    [Fact]
    public void PrivateSessionStorageRetainsExplicitProfileAndSessionIdentity()
    {
        var context = CreateContext(BrowserProfileMode.Private);
        var storageNamespace = Required(ProfileStorageNamespace.Create("clipboard.shelf"));
        var key = Required(ProfileStorageKey.Create("session-index"));

        var result = ProfileStorageAddress.Create(
            context,
            storageNamespace,
            key,
            ProfileStorageDurability.Session);

        Assert.True(result.IsSuccess);
        Assert.Same(context, result.Value?.Context);
        Assert.Equal(context.ProfileId, result.Value?.Context.ProfileId);
        Assert.Equal(context.SessionId, result.Value?.Context.SessionId);
        Assert.Equal(ProfileStorageDurability.Session, result.Value?.Durability);
    }

    [Theory]
    [InlineData("download")]
    [InlineData("downloads")]
    [InlineData("downloaded-file")]
    [InlineData("downloaded-files")]
    public void DownloadedFilesCannotBecomeAStorageClearingNamespace(string name)
    {
        var result = ProfileStorageNamespace.Create(name);

        Assert.False(result.IsSuccess);
        Assert.Equal(ControllerErrorCode.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public void LocalClearRequestNeverIncludesDownloadedFiles()
    {
        var context = CreateContext(BrowserProfileMode.Normal);
        var storageNamespace = Required(ProfileStorageNamespace.Create("history"));

        var result = LocalDataClearRequest.Create(
            context,
            [storageNamespace],
            ProfileStorageDurability.Persistent);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value?.IncludesDownloadedFiles);
        Assert.Equal(context.ProfileId, result.Value?.Context.ProfileId);
        Assert.Equal(context.SessionId, result.Value?.Context.SessionId);
    }

    [Fact]
    public void PrivateWebViewProfileIsAlwaysEphemeralAndSessionBound()
    {
        var context = CreateContext(BrowserProfileMode.Private);

        var result = WebViewProfileDescriptor.Create(
            context,
            "private-profile",
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

        Assert.True(result.IsSuccess);
        Assert.Equal(WebViewProfilePersistence.EphemeralDeleteOnSessionEnd, result.Value?.Persistence);
        Assert.True(result.Value?.MustDeleteAtSessionEnd);
        Assert.Same(context, result.Value?.Context);
    }

    [Fact]
    public void NormalWebViewProfileIsPersistent()
    {
        var context = CreateContext(BrowserProfileMode.Normal);

        var result = WebViewProfileDescriptor.Create(
            context,
            "normal-profile",
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

        Assert.True(result.IsSuccess);
        Assert.Equal(WebViewProfilePersistence.Persistent, result.Value?.Persistence);
        Assert.False(result.Value?.MustDeleteAtSessionEnd);
    }

    private static PrivacyContext CreateContext(BrowserProfileMode mode) =>
        new(
            new ProfileId(Guid.NewGuid()),
            new BrowserSessionId(Guid.NewGuid()),
            mode);

    private static T Required<T>(ControllerResult<T> result)
        where T : class
    {
        Assert.True(result.IsSuccess);
        return Assert.IsType<T>(result.Value);
    }
}
