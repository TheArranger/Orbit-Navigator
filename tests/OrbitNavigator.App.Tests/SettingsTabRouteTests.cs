using System.IO;
using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Foundation.Browser;
using OrbitNavigator.Foundation.Profiles;
using Xunit;

namespace OrbitNavigator.App.Tests;

public sealed class SettingsTabRouteTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettingsCreatesAndSelectsARealInternalTabWithoutNavigation(bool isPrivate)
    {
        using var fixture = new WorkspaceFixture(isPrivate);
        await using var workspace = await fixture.CreateAsync();
        var router = new SettingsTabRoute();

        var opened = await router.OpenAsync(workspace);

        Assert.True(opened.IsSuccess);
        Assert.Equal(2, workspace.Current.Browser.Tabs.Count);
        var settings = Assert.Single(workspace.Current.Browser.Tabs, tab => tab.InternalPage == BrowserInternalPageKind.Settings);
        Assert.Equal(settings.TabId, workspace.Current.Browser.SelectedTabId);
        Assert.Equal("Settings", settings.Title);
        Assert.Equal(isPrivate, settings.IsPrivate);
        Assert.Null(settings.Address);
        Assert.Null(settings.GroupId);
        Assert.False(InternalPageTabRoute.RequiresWebView(settings));
        Assert.False(settings.CanGoBack);
        Assert.False(settings.CanGoForward);
    }

    [Fact]
    public async Task ConcurrentMenuRequestsReuseOneSettingsTabAndPreserveTheWebTab()
    {
        using var fixture = new WorkspaceFixture(false);
        await using var workspace = await fixture.CreateAsync();
        var router = new SettingsTabRoute();

        var receipts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => router.OpenAsync(workspace).AsTask()));

        Assert.All(receipts, receipt => Assert.True(receipt.IsSuccess));
        var settings = Assert.Single(workspace.Current.Browser.Tabs, tab => tab.InternalPage == BrowserInternalPageKind.Settings);
        Assert.All(receipts, receipt => Assert.Equal(settings.TabId, receipt.Value!.Snapshot.Browser.SelectedTabId));
        Assert.Contains(fixture.WebTab, workspace.Current.Browser.Tabs);
        Assert.Equal(2, workspace.Current.Browser.Tabs.Count);
    }

    [Fact]
    public async Task RequestAfterClosingSettingsCreatesANewInternalTabInTheSameWindow()
    {
        using var fixture = new WorkspaceFixture(false);
        await using var workspace = await fixture.CreateAsync();
        var router = new SettingsTabRoute();
        await router.OpenAsync(workspace);
        var previous = workspace.Current.Browser.SelectedTabId!.Value;
        await workspace.ExecuteAsync(new CloseWorkspaceTabsAction(workspace.Current.WindowId, workspace.Current.Revision, [previous]));

        await router.OpenAsync(workspace);

        Assert.NotEqual(previous, workspace.Current.Browser.SelectedTabId);
        Assert.Equal(fixture.WindowId, workspace.Current.Browser.WindowId);
        Assert.Single(workspace.Current.Browser.Tabs, tab => tab.InternalPage == BrowserInternalPageKind.Settings);
    }

    [Fact]
    public void UtilityWindowsNoLongerExposeAPopupSettingsEntryPoint()
    {
        Assert.Null(typeof(FoundationUtilityWindow).GetMethod("ShowSettingsAsync"));
        Assert.DoesNotContain(typeof(FoundationUtilityWindow).GetConstructors().SelectMany(constructor => constructor.GetParameters()),
            parameter => parameter.ParameterType == typeof(IBrowserSettingsFacade));
    }

    private sealed class WorkspaceFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "OrbitNavigator.SettingsRouteTests", Guid.NewGuid().ToString("N"));
        private readonly PrivacyContext _privacy;
        public BrowserWindowId WindowId { get; } = new(Guid.NewGuid());
        public BrowserTabState WebTab { get; }

        public WorkspaceFixture(bool isPrivate)
        {
            _privacy = new(new ProfileId(Guid.NewGuid()), new BrowserSessionId(Guid.NewGuid()),
                isPrivate ? BrowserProfileMode.Private : BrowserProfileMode.Normal);
            WebTab = new(new BrowserTabId(Guid.NewGuid()), null, new Uri("https://example.test/"), "Existing web page",
                BrowserLoadState.Idle, true, false, isPrivate);
        }

        public async Task<BrowserWorkspaceCoordinator> CreateAsync()
        {
            IProfileStorage storage = _privacy.IsPrivate ? new NoPrivateStorage() : new FileProfileStorage(_root);
            var created = await BrowserWorkspaceCoordinator.CreateAsync(_privacy, WindowId,
                new BrowserState(WindowId, WebTab.TabId, [WebTab]), new TabGroupMetadataStore(storage));
            Assert.True(created.IsSuccess);
            return created.Value!;
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class NoPrivateStorage : IProfileStorage
    {
        public ValueTask<ControllerResult<ProfileStorageEntry>> ReadAsync(ProfileStorageAddress address, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Private Settings routing must not read persistent storage.");
        public ValueTask<ControllerResult<ProfileStorageWriteReceipt>> WriteAsync(ProfileStorageWriteRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Private Settings routing must not write persistent storage.");
        public ValueTask<ControllerResult> DeleteAsync(ProfileStorageAddress address, ProfileStorageRevision? expectedRevision = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Private Settings routing must not delete persistent storage.");
    }
}
