using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Foundation.Browser;
using OrbitNavigator.Foundation.Profiles;
using Xunit;

namespace OrbitNavigator.Foundation.Tests.Browser;

public sealed class BrowserWorkspaceSessionStoreTests
{
    [Fact]
    public async Task NormalSessionRoundTripsStableTabsGroupsSelectionAndRevision()
    {
        using var temp = new TempDirectory();
        var store = new BrowserWorkspaceSessionStore(new FileProfileStorage(temp.Path));
        var context = Context(BrowserProfileMode.Normal);
        var window = new BrowserWindowId(Guid.NewGuid());
        var first = new BrowserTabId(Guid.NewGuid());
        var second = new BrowserTabId(Guid.NewGuid());
        var group = new BrowserTabGroupId(Guid.NewGuid());
        var tabs = new[]
        {
            new BrowserWorkspaceSessionTab(first, new Uri("https://my-orbit.snap-it.cc/"), "My Orbit", group),
            new BrowserWorkspaceSessionTab(second, null, "New Tab", group),
        };
        var groups = new[]
        {
            new BrowserWorkspaceSessionGroup(group, "Orbit", true, "Violet", false, [first, second]),
        };

        var saved = await store.SaveAsync(new(context, default, window, second, tabs, groups));
        var loaded = await new BrowserWorkspaceSessionStore(
            new FileProfileStorage(temp.Path)).LoadAsync(context);
        var stale = await store.SaveAsync(new(context, default, window, second, tabs, groups));

        Assert.True(saved.IsSuccess);
        Assert.True(loaded.IsSuccess);
        Assert.Equal(window, loaded.Value!.WindowId);
        Assert.Equal(second, loaded.Value.SelectedTabId);
        Assert.Equal(new[] { first, second }, loaded.Value.Tabs.Select(tab => tab.TabId));
        Assert.Equal("Violet", loaded.Value.Groups.Single().ColorToken);
        Assert.False(loaded.Value.Groups.Single().IsTemporary);
        Assert.False(stale.IsSuccess);
        Assert.Equal(ControllerErrorCode.Conflict, stale.Error?.Code);
    }

    [Fact]
    public async Task PrivateLoadAndSaveAreDeniedBeforeStorage()
    {
        var storage = new CountingStorage();
        var store = new BrowserWorkspaceSessionStore(storage);
        var context = Context(BrowserProfileMode.Private);
        var window = new BrowserWindowId(Guid.NewGuid());
        var tab = new BrowserTabId(Guid.NewGuid());

        var loaded = await store.LoadAsync(context);
        var saved = await store.SaveAsync(new(
            context,
            default,
            window,
            tab,
            [new BrowserWorkspaceSessionTab(tab, null, "Private tab", null)],
            []));

        Assert.Equal(ControllerErrorCode.PolicyDenied, loaded.Error?.Code);
        Assert.Equal(ControllerErrorCode.PolicyDenied, saved.Error?.Code);
        Assert.Equal(0, storage.TotalCalls);
    }

    [Fact]
    public async Task RejectsUnsafeOrStructurallyInconsistentRestoreData()
    {
        var store = new BrowserWorkspaceSessionStore(new CountingStorage());
        var context = Context(BrowserProfileMode.Normal);
        var window = new BrowserWindowId(Guid.NewGuid());
        var tab = new BrowserTabId(Guid.NewGuid());
        var group = new BrowserTabGroupId(Guid.NewGuid());

        var unsafeAddress = await store.SaveAsync(new(
            context,
            default,
            window,
            tab,
            [new BrowserWorkspaceSessionTab(tab, new Uri("file:///C:/secret.txt"), "Secret", null)],
            []));
        var mismatchedGroup = await store.SaveAsync(new(
            context,
            default,
            window,
            tab,
            [new BrowserWorkspaceSessionTab(tab, null, "New Tab", group)],
            [new BrowserWorkspaceSessionGroup(group, "Group", false, "SeaGlass", true, [])]));

        Assert.Equal(ControllerErrorCode.InvalidRequest, unsafeAddress.Error?.Code);
        Assert.Equal(ControllerErrorCode.InvalidRequest, mismatchedGroup.Error?.Code);
    }

    [Fact]
    public async Task CorruptSessionCanBeRepairedWithExactStorageCas()
    {
        using var temp = new TempDirectory();
        var storage = new FileProfileStorage(temp.Path);
        var context = Context(BrowserProfileMode.Normal);
        var profileNamespace = ProfileStorageNamespace.Create("browser.workspace-session").Value!;
        var address = ProfileStorageAddress.Create(
            context,
            profileNamespace,
            ProfileStorageKey.Create("primary").Value,
            ProfileStorageDurability.Persistent).Value!;
        var corrupt = ProfileStorageWriteRequest.Create(address, "{not-json"u8.ToArray(), null).Value!;
        Assert.True((await storage.WriteAsync(corrupt)).IsSuccess);
        var store = new BrowserWorkspaceSessionStore(storage);
        Assert.Equal(ControllerErrorCode.IntegrityFailure, (await store.LoadAsync(context)).Error?.Code);
        var window = new BrowserWindowId(Guid.NewGuid());
        var tab = new BrowserTabId(Guid.NewGuid());

        var repaired = await store.SaveAsync(new(
            context,
            default,
            window,
            tab,
            [new BrowserWorkspaceSessionTab(tab, null, "New Tab", null)],
            []));
        var loaded = await store.LoadAsync(context);

        Assert.True(repaired.IsSuccess);
        Assert.True(loaded.IsSuccess);
        Assert.Equal(window, loaded.Value!.WindowId);
        Assert.Equal(tab, loaded.Value.SelectedTabId);
    }

    private static PrivacyContext Context(BrowserProfileMode mode) =>
        new(new ProfileId(Guid.NewGuid()), new BrowserSessionId(Guid.NewGuid()), mode);

    private sealed class CountingStorage : IProfileStorage
    {
        public int TotalCalls { get; private set; }

        public ValueTask<ControllerResult<ProfileStorageEntry>> ReadAsync(
            ProfileStorageAddress address,
            CancellationToken cancellationToken = default)
        {
            TotalCalls++;
            return ValueTask.FromResult(ControllerResult<ProfileStorageEntry>.Failure(
                ControllerError.Create(ControllerErrorCode.NotFound, "error.test.not_found")));
        }

        public ValueTask<ControllerResult<ProfileStorageWriteReceipt>> WriteAsync(
            ProfileStorageWriteRequest request,
            CancellationToken cancellationToken = default)
        {
            TotalCalls++;
            throw new InvalidOperationException("Unexpected storage write.");
        }

        public ValueTask<ControllerResult> DeleteAsync(
            ProfileStorageAddress address,
            ProfileStorageRevision? expectedRevision = null,
            CancellationToken cancellationToken = default)
        {
            TotalCalls++;
            throw new InvalidOperationException("Unexpected storage delete.");
        }
    }
}
