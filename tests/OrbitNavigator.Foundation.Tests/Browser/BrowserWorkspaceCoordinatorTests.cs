using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Foundation.Browser;
using OrbitNavigator.Foundation.Profiles;
using Xunit;

namespace OrbitNavigator.Foundation.Tests.Browser;

public sealed class BrowserWorkspaceCoordinatorTests
{
    [Fact]
    public async Task CommandsCarryRevisionAndStaleActionDoesNotMutate()
    {
        using var temp = new TempDirectory();
        var context = Context(BrowserProfileMode.Normal);
        var window = new BrowserWindowId(Guid.NewGuid());
        var first = Tab("One");
        var second = Tab("Two");
        var created = await BrowserWorkspaceCoordinator.CreateAsync(
            context,
            window,
            new BrowserState(window, first.TabId, [first, second]),
            new TabGroupMetadataStore(new FileProfileStorage(temp.Path)));
        await using var coordinator = created.Value!;
        var revision = coordinator.Current.Revision;

        var select = await coordinator.ExecuteAsync(new SelectWorkspaceTabAction(
            window, revision, second.TabId));
        var stale = await coordinator.ExecuteAsync(new CloseWorkspaceTabsAction(
            window, revision, [second.TabId]));

        Assert.True(select.IsSuccess);
        Assert.Equal(second.TabId, coordinator.Current.Browser.SelectedTabId);
        Assert.False(stale.IsSuccess);
        Assert.Equal(ControllerErrorCode.Conflict, stale.Error?.Code);
        Assert.Equal(2, coordinator.Current.Browser.Tabs.Count);
    }

    [Fact]
    public async Task OneTabGroupRenameAndUngroupAreSerializedAndDurable()
    {
        using var temp = new TempDirectory();
        var storage = new FileProfileStorage(temp.Path);
        var context = Context(BrowserProfileMode.Normal);
        var window = new BrowserWindowId(Guid.NewGuid());
        var tab = Tab("One");
        await using var coordinator = (await BrowserWorkspaceCoordinator.CreateAsync(
            context,
            window,
            new BrowserState(window, tab.TabId, [tab]),
            new TabGroupMetadataStore(storage))).Value!;
        var groupId = new BrowserTabGroupId(Guid.NewGuid());

        var create = await coordinator.ExecuteAsync(new CreateWorkspaceGroupAction(
            window, coordinator.Current.Revision, groupId, "Group", [tab.TabId]));
        var rename = await coordinator.ExecuteAsync(new RenameWorkspaceGroupAction(
            window, coordinator.Current.Revision, groupId, "Renamed"));
        var persisted = await new TabGroupMetadataStore(storage).LoadAsync(context, window);
        var ungroup = await coordinator.ExecuteAsync(new UngroupWorkspaceTabsAction(
            window, coordinator.Current.Revision, groupId));

        Assert.True(create.IsSuccess);
        Assert.True(rename.IsSuccess);
        Assert.Equal("Renamed", Assert.Single(persisted.Value!.Groups).Name);
        Assert.True(ungroup.IsSuccess);
        Assert.Empty(coordinator.Current.Groups);
        Assert.Null(Assert.Single(coordinator.Current.Browser.Tabs).GroupId);
    }

    [Fact]
    public async Task PrivateGroupCommandsUseOnlyMemoryAndCloseFailsClosed()
    {
        var context = Context(BrowserProfileMode.Private);
        var window = new BrowserWindowId(Guid.NewGuid());
        var tab = Tab("Private", true);
        var store = new TabGroupMetadataStore(new ThrowingStorage());
        var coordinator = (await BrowserWorkspaceCoordinator.CreateAsync(
            context,
            window,
            new BrowserState(window, tab.TabId, [tab]),
            store)).Value!;
        var groupId = new BrowserTabGroupId(Guid.NewGuid());

        var create = await coordinator.ExecuteAsync(new CreateWorkspaceGroupAction(
            window, coordinator.Current.Revision, groupId, "Private", [tab.TabId]));
        await coordinator.DisposeAsync();
        var closed = await coordinator.ExecuteAsync(new RenameWorkspaceGroupAction(
            window, create.Value!.Snapshot.Revision, groupId, "Nope"));

        Assert.True(create.IsSuccess);
        Assert.False(closed.IsSuccess);
        Assert.Equal(ControllerErrorCode.Unavailable, closed.Error?.Code);
    }

    [Fact]
    public async Task NormalSessionPersistsStableTabsGroupsColorAndTemporaryState()
    {
        using var temp = new TempDirectory();
        var storage = new FileProfileStorage(temp.Path);
        var context = Context(BrowserProfileMode.Normal);
        var window = new BrowserWindowId(Guid.NewGuid());
        var first = Tab("One") with { Address = new Uri("https://my-orbit.snap-it.cc/") };
        var second = Tab("Two") with { Address = new Uri("https://beacon-spire.dps-games.cc/") };
        var sessions = new BrowserWorkspaceSessionStore(storage);
        await using var coordinator = (await BrowserWorkspaceCoordinator.CreateAsync(
            context,
            window,
            new BrowserState(window, first.TabId, [first, second]),
            new TabGroupMetadataStore(storage),
            sessions)).Value!;
        var groupId = new BrowserTabGroupId(Guid.NewGuid());

        Assert.True((await coordinator.ExecuteAsync(new CreateWorkspaceGroupAction(
            window, coordinator.Current.Revision, groupId, "Trip", [first.TabId, second.TabId]))).IsSuccess);
        Assert.True((await coordinator.ExecuteAsync(new SetWorkspaceGroupColorAction(
            window, coordinator.Current.Revision, groupId, "Violet"))).IsSuccess);
        Assert.True((await coordinator.ExecuteAsync(new MarkWorkspaceGroupSavedAction(
            window, coordinator.Current.Revision, groupId))).IsSuccess);
        Assert.True((await coordinator.ExecuteAsync(new SelectWorkspaceTabAction(
            window, coordinator.Current.Revision, second.TabId))).IsSuccess);

        var persisted = await sessions.LoadAsync(context);
        Assert.True(persisted.IsSuccess);
        Assert.Equal(window, persisted.Value!.WindowId);
        Assert.Equal(second.TabId, persisted.Value.SelectedTabId);
        Assert.Equal([first.TabId, second.TabId], persisted.Value.Tabs.Select(tab => tab.TabId));
        var group = Assert.Single(persisted.Value.Groups);
        Assert.Equal("Violet", group.ColorToken);
        Assert.False(group.IsTemporary);
        Assert.Equal([first.TabId, second.TabId], group.TabOrder);
    }

    [Fact]
    public async Task RestoredSessionIsAuthoritativeOverLegacyGroupMetadata()
    {
        using var temp = new TempDirectory();
        var storage = new FileProfileStorage(temp.Path);
        var context = Context(BrowserProfileMode.Normal);
        var window = new BrowserWindowId(Guid.NewGuid());
        var first = Tab("One");
        var second = Tab("Two");
        var restoredGroupId = new BrowserTabGroupId(Guid.NewGuid());
        first = first with { GroupId = restoredGroupId };
        second = second with { GroupId = restoredGroupId };
        var sessions = new BrowserWorkspaceSessionStore(storage);
        var saved = await sessions.SaveAsync(new(
            context,
            default,
            window,
            second.TabId,
            [
                new(first.TabId, first.Address, first.Title, first.GroupId),
                new(second.TabId, second.Address, second.Title, second.GroupId),
            ],
            [new(restoredGroupId, "Restored", true, "Scarlet", true, [first.TabId, second.TabId])]));
        Assert.True(saved.IsSuccess);

        await using var coordinator = (await BrowserWorkspaceCoordinator.CreateAsync(
            context,
            window,
            new BrowserState(window, second.TabId, [first, second]),
            new TabGroupMetadataStore(storage),
            sessions,
            saved.Value)).Value!;

        Assert.Equal(second.TabId, coordinator.Current.Browser.SelectedTabId);
        var restored = Assert.Single(coordinator.Current.Groups);
        Assert.Equal("Restored", restored.Name);
        Assert.True(restored.IsCollapsed);
        Assert.Equal("Scarlet", restored.ColorToken);
        Assert.True(restored.IsTemporary);
    }

    [Fact]
    public async Task MovingLastMemberOutPrunesGroupAndPersistsCanonicalState()
    {
        using var temp = new TempDirectory();
        var storage = new FileProfileStorage(temp.Path);
        var context = Context(BrowserProfileMode.Normal);
        var window = new BrowserWindowId(Guid.NewGuid());
        var grouped = Tab("Grouped");
        var survivor = Tab("Survivor");
        var sessions = new BrowserWorkspaceSessionStore(storage);
        await using var coordinator = (await BrowserWorkspaceCoordinator.CreateAsync(
            context,
            window,
            new BrowserState(window, grouped.TabId, [grouped, survivor]),
            new TabGroupMetadataStore(storage),
            sessions)).Value!;
        var groupId = new BrowserTabGroupId(Guid.NewGuid());
        Assert.True((await coordinator.ExecuteAsync(new CreateWorkspaceGroupAction(
            window, coordinator.Current.Revision, groupId, "Temporary", [grouped.TabId]))).IsSuccess);

        var moved = await coordinator.ExecuteAsync(new MoveWorkspaceTabAction(
            window, coordinator.Current.Revision, grouped.TabId, 1, null));
        var persisted = await sessions.LoadAsync(context);

        Assert.True(moved.IsSuccess);
        Assert.Empty(moved.Value!.Snapshot.Groups);
        Assert.Null(moved.Value.Snapshot.Browser.Tabs.Single(tab => tab.TabId == grouped.TabId).GroupId);
        Assert.True(persisted.IsSuccess);
        Assert.Empty(persisted.Value!.Groups);
    }

    [Fact]
    public async Task ClosingAllMembersPrunesGroupWithoutClosingSoleSurvivor()
    {
        using var temp = new TempDirectory();
        var storage = new FileProfileStorage(temp.Path);
        var context = Context(BrowserProfileMode.Normal);
        var window = new BrowserWindowId(Guid.NewGuid());
        var first = Tab("One");
        var second = Tab("Two");
        var survivor = Tab("Survivor");
        var sessions = new BrowserWorkspaceSessionStore(storage);
        await using var coordinator = (await BrowserWorkspaceCoordinator.CreateAsync(
            context,
            window,
            new BrowserState(window, first.TabId, [first, second, survivor]),
            new TabGroupMetadataStore(storage),
            sessions)).Value!;
        var groupId = new BrowserTabGroupId(Guid.NewGuid());
        Assert.True((await coordinator.ExecuteAsync(new CreateWorkspaceGroupAction(
            window, coordinator.Current.Revision, groupId, "Close me", [first.TabId, second.TabId]))).IsSuccess);

        var closed = await coordinator.ExecuteAsync(new CloseWorkspaceTabsAction(
            window, coordinator.Current.Revision, [first.TabId, second.TabId]));

        Assert.True(closed.IsSuccess);
        Assert.Empty(closed.Value!.Snapshot.Groups);
        Assert.Equal(survivor.TabId, Assert.Single(closed.Value.Snapshot.Browser.Tabs).TabId);
        Assert.Equal([first.TabId, second.TabId], closed.Value.ClosedTabIds);
    }

    [Fact]
    public async Task AtomicWorkspaceOpenPersistenceFailureLeavesExistingTabsAndSelectionUntouched()
    {
        using var temp = new TempDirectory();
        var context = Context(BrowserProfileMode.Normal);
        var window = new BrowserWindowId(Guid.NewGuid());
        var existing = Tab("Existing");
        var sessions = new FailAfterInitialSaveSessionStore();
        await using var coordinator = (await BrowserWorkspaceCoordinator.CreateAsync(
            context,
            window,
            new BrowserState(window, existing.TabId, [existing]),
            new TabGroupMetadataStore(new FileProfileStorage(temp.Path)),
            sessions)).Value!;
        var before = coordinator.Current;
        var workspace = Tab("Workspace") with { Address = new Uri("https://example.com/workspace") };

        var result = await coordinator.ExecuteAsync(new OpenWorkspaceGroupAction(
            window,
            before.Revision,
            [workspace],
            new BrowserTabGroupId(Guid.NewGuid()),
            "Research",
            "Violet",
            workspace.TabId,
            InitiallyCollapsed: true,
            IsTemporary: false));

        Assert.False(result.IsSuccess);
        Assert.Equal(ControllerErrorCode.Unavailable, result.Error?.Code);
        Assert.Equal(before.Revision, coordinator.Current.Revision);
        Assert.Equal(existing.TabId, coordinator.Current.Browser.SelectedTabId);
        Assert.Equal([existing.TabId], coordinator.Current.Browser.Tabs.Select(tab => tab.TabId));
        Assert.Empty(coordinator.Current.Groups);
        Assert.Equal([existing.TabId], sessions.LastSaved!.Tabs.Select(tab => tab.TabId));
    }

    private static PrivacyContext Context(BrowserProfileMode mode) =>
        new(new ProfileId(Guid.NewGuid()), new BrowserSessionId(Guid.NewGuid()), mode);

    private static BrowserTabState Tab(string title, bool isPrivate = false) =>
        new(
            new BrowserTabId(Guid.NewGuid()),
            null,
            null,
            title,
            BrowserLoadState.Idle,
            false,
            false,
            isPrivate);

    private sealed class ThrowingStorage : OrbitNavigator.Contracts.Infrastructure.IProfileStorage
    {
        public ValueTask<ControllerResult<OrbitNavigator.Contracts.Infrastructure.ProfileStorageEntry>> ReadAsync(
            OrbitNavigator.Contracts.Infrastructure.ProfileStorageAddress address,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Private workspace must not read storage.");

        public ValueTask<ControllerResult<OrbitNavigator.Contracts.Infrastructure.ProfileStorageWriteReceipt>> WriteAsync(
            OrbitNavigator.Contracts.Infrastructure.ProfileStorageWriteRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Private workspace must not write storage.");

        public ValueTask<ControllerResult> DeleteAsync(
            OrbitNavigator.Contracts.Infrastructure.ProfileStorageAddress address,
            OrbitNavigator.Contracts.Infrastructure.ProfileStorageRevision? expectedRevision = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Private workspace must not delete storage.");
    }

    private sealed class FailAfterInitialSaveSessionStore : IBrowserWorkspaceSessionStore
    {
        private int _saveCount;

        public BrowserWorkspaceSessionSnapshot? LastSaved { get; private set; }

        public ValueTask<ControllerResult<BrowserWorkspaceSessionSnapshot>> LoadAsync(
            PrivacyContext context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ControllerResult<BrowserWorkspaceSessionSnapshot>.Failure(
                ControllerError.Create(ControllerErrorCode.NotFound, "error.workspace.not_found")));

        public ValueTask<ControllerResult<BrowserWorkspaceSessionSnapshot>> SaveAsync(
            SaveBrowserWorkspaceSessionIntent intent,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _saveCount) > 1)
            {
                return ValueTask.FromResult(ControllerResult<BrowserWorkspaceSessionSnapshot>.Failure(
                    ControllerError.Create(ControllerErrorCode.Unavailable, "error.workspace.write_failed")));
            }

            LastSaved = new(
                intent.Context.ProfileId,
                new BrowserWorkspaceSessionRevision(Guid.NewGuid()),
                intent.WindowId,
                intent.SelectedTabId,
                intent.Tabs,
                intent.Groups);
            return ValueTask.FromResult(
                ControllerResult<BrowserWorkspaceSessionSnapshot>.Success(LastSaved));
        }
    }
}
