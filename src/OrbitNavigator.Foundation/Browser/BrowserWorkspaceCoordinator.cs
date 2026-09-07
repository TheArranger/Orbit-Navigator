using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.Foundation.Browser;

public readonly record struct BrowserWorkspaceRevision(long Value)
{
    public bool IsEmpty => Value <= 0;
}

public sealed record WorkspaceTabGroupState(
    BrowserTabGroupId GroupId,
    string Name,
    bool IsCollapsed)
{
    public string ColorToken { get; init; } = "SeaGlass";
    public bool IsTemporary { get; init; } = true;
}

public sealed record BrowserWorkspaceSnapshot(
    PrivacyContext Context,
    BrowserWindowId WindowId,
    BrowserWorkspaceRevision Revision,
    BrowserState Browser,
    IReadOnlyList<WorkspaceTabGroupState> Groups);

public abstract record BrowserWorkspaceAction(
    BrowserWindowId WindowId,
    BrowserWorkspaceRevision ExpectedRevision);

public sealed record AddWorkspaceTabAction(
    BrowserWindowId WindowId,
    BrowserWorkspaceRevision ExpectedRevision,
    BrowserTabState Tab,
    bool Select)
    : BrowserWorkspaceAction(WindowId, ExpectedRevision);

public sealed record SelectWorkspaceTabAction(
    BrowserWindowId WindowId,
    BrowserWorkspaceRevision ExpectedRevision,
    BrowserTabId TabId)
    : BrowserWorkspaceAction(WindowId, ExpectedRevision);

public sealed record UpdateWorkspaceTabAction(
    BrowserWindowId WindowId,
    BrowserWorkspaceRevision ExpectedRevision,
    BrowserTabState Tab)
    : BrowserWorkspaceAction(WindowId, ExpectedRevision);

public sealed record CloseWorkspaceTabsAction(
    BrowserWindowId WindowId,
    BrowserWorkspaceRevision ExpectedRevision,
    IReadOnlyList<BrowserTabId> TabIds)
    : BrowserWorkspaceAction(WindowId, ExpectedRevision);

public sealed record MoveWorkspaceTabAction(
    BrowserWindowId WindowId,
    BrowserWorkspaceRevision ExpectedRevision,
    BrowserTabId TabId,
    int NewIndex,
    BrowserTabGroupId? GroupId)
    : BrowserWorkspaceAction(WindowId, ExpectedRevision);

public sealed record CreateWorkspaceGroupAction(
    BrowserWindowId WindowId,
    BrowserWorkspaceRevision ExpectedRevision,
    BrowserTabGroupId GroupId,
    string Name,
    IReadOnlyList<BrowserTabId> TabIds)
    : BrowserWorkspaceAction(WindowId, ExpectedRevision);

public sealed record OpenWorkspaceGroupAction(
    BrowserWindowId WindowId,
    BrowserWorkspaceRevision ExpectedRevision,
    IReadOnlyList<BrowserTabState> Tabs,
    BrowserTabGroupId GroupId,
    string Name,
    string ColorToken,
    BrowserTabId SelectedTabId,
    bool InitiallyCollapsed,
    bool IsTemporary)
    : BrowserWorkspaceAction(WindowId, ExpectedRevision);

public sealed record RenameWorkspaceGroupAction(
    BrowserWindowId WindowId,
    BrowserWorkspaceRevision ExpectedRevision,
    BrowserTabGroupId GroupId,
    string RequestedName)
    : BrowserWorkspaceAction(WindowId, ExpectedRevision);

public sealed record ToggleWorkspaceGroupAction(
    BrowserWindowId WindowId,
    BrowserWorkspaceRevision ExpectedRevision,
    BrowserTabGroupId GroupId,
    bool IsCollapsed)
    : BrowserWorkspaceAction(WindowId, ExpectedRevision);

public sealed record SetWorkspaceGroupColorAction(
    BrowserWindowId WindowId,
    BrowserWorkspaceRevision ExpectedRevision,
    BrowserTabGroupId GroupId,
    string ColorToken)
    : BrowserWorkspaceAction(WindowId, ExpectedRevision);

public sealed record MarkWorkspaceGroupSavedAction(
    BrowserWindowId WindowId,
    BrowserWorkspaceRevision ExpectedRevision,
    BrowserTabGroupId GroupId)
    : BrowserWorkspaceAction(WindowId, ExpectedRevision);

public sealed record UngroupWorkspaceTabsAction(
    BrowserWindowId WindowId,
    BrowserWorkspaceRevision ExpectedRevision,
    BrowserTabGroupId GroupId)
    : BrowserWorkspaceAction(WindowId, ExpectedRevision);

public sealed record BrowserWorkspaceCommandReceipt(
    BrowserWorkspaceSnapshot Snapshot,
    IReadOnlyList<BrowserTabId> ClosedTabIds);

public sealed class BrowserWorkspaceCoordinator : IAsyncDisposable
{
    private readonly PrivacyContext _context;
    private readonly BrowserWindowId _windowId;
    private readonly ITabGroupMetadataStore _groupsStore;
    private readonly IBrowserWorkspaceSessionStore? _sessionStore;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private BrowserState _browser;
    private Dictionary<BrowserTabGroupId, WorkspaceTabGroupState> _groups;
    private TabGroupCatalogRevision _groupRevision;
    private BrowserWorkspaceSessionRevision _sessionRevision;
    private long _revision = 1;
    private int _disposed;

    private BrowserWorkspaceCoordinator(
        PrivacyContext context,
        BrowserWindowId windowId,
        BrowserState browser,
        Dictionary<BrowserTabGroupId, WorkspaceTabGroupState> groups,
        TabGroupCatalogRevision groupRevision,
        ITabGroupMetadataStore groupsStore,
        IBrowserWorkspaceSessionStore? sessionStore,
        BrowserWorkspaceSessionRevision sessionRevision)
    {
        _context = context;
        _windowId = windowId;
        _browser = browser;
        _groups = groups;
        _groupRevision = groupRevision;
        _groupsStore = groupsStore;
        _sessionStore = sessionStore;
        _sessionRevision = sessionRevision;
    }

    public BrowserWorkspaceSnapshot Current => Snapshot();

    public static async ValueTask<ControllerResult<BrowserWorkspaceCoordinator>> CreateAsync(
        PrivacyContext context,
        BrowserWindowId windowId,
        BrowserState initialState,
        ITabGroupMetadataStore groupsStore,
        IBrowserWorkspaceSessionStore? sessionStore = null,
        BrowserWorkspaceSessionSnapshot? restoredSession = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(initialState);
        ArgumentNullException.ThrowIfNull(groupsStore);
        if (!context.IsStructurallyValid ||
            windowId.IsEmpty ||
            initialState.WindowId != windowId ||
            !ValidateBrowser(initialState))
        {
            return ControllerResult<BrowserWorkspaceCoordinator>.Failure(Invalid());
        }

        var loaded = await groupsStore.LoadAsync(context, windowId, cancellationToken)
            .ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return ControllerResult<BrowserWorkspaceCoordinator>.Failure(loaded.Error!);
        }

        var loadedGroups = loaded.Value!;
        Dictionary<BrowserTabGroupId, WorkspaceTabGroupState> groups;
        var reconciledBrowser = initialState;
        var sessionRevision = default(BrowserWorkspaceSessionRevision);
        if (restoredSession is not null)
        {
            if (sessionStore is null ||
                !ValidateRestoredSession(context, windowId, initialState, restoredSession))
            {
                return ControllerResult<BrowserWorkspaceCoordinator>.Failure(Invalid());
            }
            groups = restoredSession.Groups.ToDictionary(
                group => group.GroupId,
                group => new WorkspaceTabGroupState(group.GroupId, group.Name, group.IsCollapsed)
                {
                    ColorToken = group.ColorToken,
                    IsTemporary = group.IsTemporary,
                });
            sessionRevision = restoredSession.Revision;
        }
        else
        {
            var reconciliation = BrowserWorkspaceProfileReconciler.ReconcileLegacy(
                context,
                windowId,
                initialState,
                loadedGroups);
            if (!reconciliation.IsSuccess)
            {
                return ControllerResult<BrowserWorkspaceCoordinator>.Failure(reconciliation.Error!);
            }
            var repaired = reconciliation.Value!.Snapshot;
            reconciledBrowser = initialState with
            {
                SelectedTabId = repaired.SelectedTabId,
                Tabs = repaired.Tabs.Select(tab => initialState.Tabs
                    .Single(original => original.TabId == tab.TabId) with { GroupId = tab.GroupId }).ToArray(),
            };
            groups = repaired.Groups.ToDictionary(
                group => group.GroupId,
                group => new WorkspaceTabGroupState(group.GroupId, group.Name, group.IsCollapsed)
                {
                    ColorToken = group.ColorToken,
                    IsTemporary = group.IsTemporary,
                });
        }
        var coordinator = new BrowserWorkspaceCoordinator(
            context,
            windowId,
            reconciledBrowser,
            groups,
            loadedGroups.Revision,
            groupsStore,
            sessionStore,
            sessionRevision);
        if (sessionStore is not null && restoredSession is null)
        {
            var initialized = await coordinator.PersistSessionAsync(
                reconciledBrowser,
                groups,
                cancellationToken).ConfigureAwait(false);
            if (!initialized.IsSuccess)
            {
                await coordinator.DisposeAsync().ConfigureAwait(false);
                return ControllerResult<BrowserWorkspaceCoordinator>.Failure(initialized.Error!);
            }
            coordinator._sessionRevision = initialized.Value!.Revision;
        }
        return ControllerResult<BrowserWorkspaceCoordinator>.Success(coordinator);
    }

    public async ValueTask<ControllerResult<BrowserWorkspaceCommandReceipt>> ExecuteAsync(
        BrowserWorkspaceAction action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Volatile.Read(ref _disposed) != 0)
        {
            return Failure(ControllerErrorCode.Unavailable, "error.workspace.closed");
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return Failure(ControllerErrorCode.Unavailable, "error.workspace.closed");
            }
            if (action.WindowId != _windowId)
            {
                return Failure(ControllerErrorCode.PolicyDenied, "error.workspace.window_mismatch");
            }
            if (action.ExpectedRevision.Value != _revision)
            {
                return Failure(ControllerErrorCode.Conflict, "error.workspace.revision_conflict");
            }

            var nextBrowser = _browser;
            var nextGroups = new Dictionary<BrowserTabGroupId, WorkspaceTabGroupState>(_groups);
            IReadOnlyList<BrowserTabId> closed = [];
            var persistGroups = false;

            switch (action)
            {
                case AddWorkspaceTabAction add when ValidateTab(add.Tab) &&
                    !_browser.Tabs.Any(tab => tab.TabId == add.Tab.TabId):
                    nextBrowser = _browser with
                    {
                        Tabs = _browser.Tabs.Append(add.Tab).ToArray(),
                        SelectedTabId = add.Select ? add.Tab.TabId : _browser.SelectedTabId,
                    };
                    break;

                case SelectWorkspaceTabAction select when Owns(select.TabId):
                    nextBrowser = _browser with { SelectedTabId = select.TabId };
                    break;

                case UpdateWorkspaceTabAction update when
                    ValidateTab(update.Tab) && Owns(update.Tab.TabId):
                    nextBrowser = _browser with
                    {
                        Tabs = _browser.Tabs.Select(tab => tab.TabId == update.Tab.TabId
                            ? update.Tab
                            : tab).ToArray(),
                    };
                    persistGroups = _browser.Tabs.First(tab => tab.TabId == update.Tab.TabId).GroupId !=
                        update.Tab.GroupId;
                    break;

                case CloseWorkspaceTabsAction close:
                    var closeIds = close.TabIds?.Distinct().ToArray() ?? [];
                    if (closeIds.Length == 0 || closeIds.Any(id => !Owns(id)) ||
                        _browser.Tabs.Count - closeIds.Length < 1)
                    {
                        return InvalidResult();
                    }
                    var remaining = _browser.Tabs.Where(tab => !closeIds.Contains(tab.TabId)).ToArray();
                    nextBrowser = _browser with
                    {
                        Tabs = remaining,
                        SelectedTabId = closeIds.Contains(_browser.SelectedTabId ?? default)
                            ? SelectAfterClose(_browser.Tabs, closeIds)
                            : _browser.SelectedTabId,
                    };
                    closed = closeIds;
                    persistGroups = closeIds.Any(id => _browser.Tabs.First(tab => tab.TabId == id).GroupId is not null);
                    break;

                case MoveWorkspaceTabAction move when Owns(move.TabId) &&
                    (move.GroupId is null || nextGroups.ContainsKey(move.GroupId.Value)):
                    var tabs = _browser.Tabs.ToList();
                    var oldIndex = tabs.FindIndex(tab => tab.TabId == move.TabId);
                    var moved = tabs[oldIndex] with { GroupId = move.GroupId };
                    tabs.RemoveAt(oldIndex);
                    tabs.Insert(Math.Clamp(move.NewIndex, 0, tabs.Count), moved);
                    nextBrowser = _browser with { Tabs = tabs };
                    persistGroups = true;
                    break;

                case CreateWorkspaceGroupAction create when
                    !create.GroupId.IsEmpty &&
                    !nextGroups.ContainsKey(create.GroupId) &&
                    ValidName(create.Name) &&
                    create.TabIds is { Count: > 0 } &&
                    create.TabIds.Distinct().Count() == create.TabIds.Count &&
                    create.TabIds.All(Owns):
                    nextGroups.Add(create.GroupId, new(create.GroupId, create.Name, false));
                    var members = create.TabIds.ToHashSet();
                    nextBrowser = _browser with
                    {
                        Tabs = _browser.Tabs.Select(tab => members.Contains(tab.TabId)
                            ? tab with { GroupId = create.GroupId }
                            : tab).ToArray(),
                    };
                    persistGroups = true;
                    break;

                case OpenWorkspaceGroupAction open when
                    open.Tabs is { Count: > 0 } &&
                    !open.GroupId.IsEmpty &&
                    !nextGroups.ContainsKey(open.GroupId) &&
                    ValidName(open.Name) &&
                    ValidColor(open.ColorToken) &&
                    open.Tabs.All(ValidateTab) &&
                    open.Tabs.Select(tab => tab.TabId).Distinct().Count() == open.Tabs.Count &&
                    open.Tabs.All(tab => !Owns(tab.TabId)) &&
                    open.Tabs.Any(tab => tab.TabId == open.SelectedTabId):
                    var groupedTabs = open.Tabs.Select(tab => tab with
                    {
                        GroupId = open.GroupId,
                    }).ToArray();
                    nextGroups.Add(open.GroupId, new(
                        open.GroupId,
                        open.Name.Trim(),
                        open.InitiallyCollapsed)
                    {
                        ColorToken = open.ColorToken,
                        IsTemporary = open.IsTemporary,
                    });
                    nextBrowser = _browser with
                    {
                        Tabs = _browser.Tabs.Concat(groupedTabs).ToArray(),
                        SelectedTabId = open.SelectedTabId,
                    };
                    persistGroups = true;
                    break;

                case RenameWorkspaceGroupAction rename when
                    nextGroups.TryGetValue(rename.GroupId, out var group) &&
                    ValidName(rename.RequestedName):
                    nextGroups[rename.GroupId] = group with { Name = rename.RequestedName };
                    persistGroups = true;
                    break;

                case ToggleWorkspaceGroupAction toggle when
                    nextGroups.TryGetValue(toggle.GroupId, out var toggleGroup):
                    nextGroups[toggle.GroupId] = toggleGroup with { IsCollapsed = toggle.IsCollapsed };
                    persistGroups = true;
                    break;

                case SetWorkspaceGroupColorAction color when
                    nextGroups.TryGetValue(color.GroupId, out var colorGroup) &&
                    ValidColor(color.ColorToken):
                    nextGroups[color.GroupId] = colorGroup with { ColorToken = color.ColorToken };
                    break;

                case MarkWorkspaceGroupSavedAction saved when
                    nextGroups.TryGetValue(saved.GroupId, out var savedGroup):
                    nextGroups[saved.GroupId] = savedGroup with { IsTemporary = false };
                    break;

                case UngroupWorkspaceTabsAction ungroup when nextGroups.ContainsKey(ungroup.GroupId):
                    nextGroups.Remove(ungroup.GroupId);
                    nextBrowser = _browser with
                    {
                        Tabs = _browser.Tabs.Select(tab => tab.GroupId == ungroup.GroupId
                            ? tab with { GroupId = null }
                            : tab).ToArray(),
                    };
                    persistGroups = true;
                    break;

                default:
                    return InvalidResult();
            }

            PruneEmptyGroups(nextBrowser, nextGroups);

            if (_sessionStore is null && persistGroups)
            {
                var save = await PersistGroupsAsync(nextBrowser, nextGroups, cancellationToken)
                    .ConfigureAwait(false);
                if (!save.IsSuccess)
                {
                    return ControllerResult<BrowserWorkspaceCommandReceipt>.Failure(save.Error!);
                }
                _groupRevision = save.Value!.Revision;
            }

            if (_sessionStore is not null)
            {
                var session = await PersistSessionAsync(nextBrowser, nextGroups, cancellationToken)
                    .ConfigureAwait(false);
                if (!session.IsSuccess)
                {
                    return ControllerResult<BrowserWorkspaceCommandReceipt>.Failure(session.Error!);
                }
                _sessionRevision = session.Value!.Revision;
            }

            _browser = nextBrowser;
            _groups = nextGroups;
            _revision = checked(_revision + 1);
            return ControllerResult<BrowserWorkspaceCommandReceipt>.Success(new(Snapshot(), closed));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        if (_groupsStore is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask<ControllerResult<TabGroupMetadataSnapshot>> PersistGroupsAsync(
        BrowserState browser,
        IReadOnlyDictionary<BrowserTabGroupId, WorkspaceTabGroupState> groups,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var metadata = groups.Values.Select(group => new TabGroupMetadata(
            group.GroupId,
            group.Name,
            group.IsCollapsed,
            browser.Tabs.Where(tab => tab.GroupId == group.GroupId).Select(tab => tab.TabId).ToArray(),
            now)).ToArray();
        return await _groupsStore.ReplaceAsync(new(
            _context,
            _windowId,
            _groupRevision,
            metadata), cancellationToken).ConfigureAwait(false);
    }

    private ValueTask<ControllerResult<BrowserWorkspaceSessionSnapshot>> PersistSessionAsync(
        BrowserState browser,
        IReadOnlyDictionary<BrowserTabGroupId, WorkspaceTabGroupState> groups,
        CancellationToken cancellationToken)
    {
        var tabs = browser.Tabs.Select(tab => new BrowserWorkspaceSessionTab(
            tab.TabId,
            tab.Address is null ? null : CanonicalWebAddress.Normalize(tab.Address),
            string.IsNullOrWhiteSpace(tab.Title) ? "New Tab" : tab.Title,
            tab.GroupId)).ToArray();
        var storedGroups = groups.Values.Select(group => new BrowserWorkspaceSessionGroup(
            group.GroupId,
            group.Name,
            group.IsCollapsed,
            group.ColorToken,
            group.IsTemporary,
            tabs.Where(tab => tab.GroupId == group.GroupId).Select(tab => tab.TabId).ToArray()))
            .ToArray();
        return _sessionStore!.SaveAsync(new(
            _context,
            _sessionRevision,
            _windowId,
            browser.SelectedTabId ?? browser.Tabs[0].TabId,
            tabs,
            storedGroups), cancellationToken);
    }

    private BrowserWorkspaceSnapshot Snapshot() =>
        new(
            _context,
            _windowId,
            new BrowserWorkspaceRevision(_revision),
            _browser with { Tabs = _browser.Tabs.ToArray() },
            _groups.Values.ToArray());

    private bool Owns(BrowserTabId tabId) =>
        !tabId.IsEmpty && _browser.Tabs.Any(tab => tab.TabId == tabId);

    private static void PruneEmptyGroups(
        BrowserState browser,
        IDictionary<BrowserTabGroupId, WorkspaceTabGroupState> groups)
    {
        var referenced = browser.Tabs
            .Where(tab => tab.GroupId is not null)
            .Select(tab => tab.GroupId!.Value)
            .ToHashSet();
        foreach (var empty in groups.Keys.Where(groupId => !referenced.Contains(groupId)).ToArray())
        {
            groups.Remove(empty);
        }
    }

    private static BrowserTabId SelectAfterClose(
        IReadOnlyList<BrowserTabState> tabs,
        IReadOnlyCollection<BrowserTabId> closing)
    {
        var selectedIndex = tabs.ToList().FindIndex(tab => closing.Contains(tab.TabId));
        for (var offset = 1; offset <= tabs.Count; offset++)
        {
            var right = selectedIndex + offset;
            if (right < tabs.Count && !closing.Contains(tabs[right].TabId)) return tabs[right].TabId;
            var left = selectedIndex - offset;
            if (left >= 0 && !closing.Contains(tabs[left].TabId)) return tabs[left].TabId;
        }
        return tabs.First(tab => !closing.Contains(tab.TabId)).TabId;
    }

    private static bool ValidateBrowser(BrowserState state) =>
        state.Tabs is { Count: > 0 } &&
        state.Tabs.All(ValidateTab) &&
        state.Tabs.Select(tab => tab.TabId).Distinct().Count() == state.Tabs.Count &&
        state.SelectedTabId is { } selected &&
        state.Tabs.Any(tab => tab.TabId == selected);

    private static bool ValidateTab(BrowserTabState tab) =>
        tab is not null && !tab.TabId.IsEmpty;

    private static bool ValidName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= TabGroupMetadataStore.MaximumGroupNameLength;

    private static bool ValidColor(string value) => value is
        "SeaGlass" or "Gold" or "Violet" or "Scarlet" or "Azure" or "Slate";

    private static bool ValidateRestoredSession(
        PrivacyContext context,
        BrowserWindowId windowId,
        BrowserState initialState,
        BrowserWorkspaceSessionSnapshot restored)
    {
        if (context.IsPrivate || restored.ProfileId != context.ProfileId ||
            restored.WindowId != windowId || restored.SelectedTabId != initialState.SelectedTabId ||
            !restored.Tabs.Select(tab => tab.TabId).SequenceEqual(initialState.Tabs.Select(tab => tab.TabId)))
        {
            return false;
        }

        var initialById = initialState.Tabs.ToDictionary(tab => tab.TabId);
        if (restored.Tabs.Any(tab =>
                !initialById.TryGetValue(tab.TabId, out var initial) ||
                initial.Address != tab.Address || initial.GroupId != tab.GroupId))
        {
            return false;
        }

        var groupIds = restored.Groups.Select(group => group.GroupId).ToHashSet();
        return restored.Groups.Count <= BrowserWorkspaceSessionStore.MaximumGroups &&
            groupIds.Count == restored.Groups.Count &&
            restored.Groups.All(group =>
                !group.GroupId.IsEmpty && ValidName(group.Name) && ValidColor(group.ColorToken) &&
                group.TabOrder.SequenceEqual(initialState.Tabs
                    .Where(tab => tab.GroupId == group.GroupId)
                    .Select(tab => tab.TabId))) &&
            initialState.Tabs.All(tab => tab.GroupId is null || groupIds.Contains(tab.GroupId.Value));
    }

    private static ControllerError Invalid() =>
        ControllerError.Create(ControllerErrorCode.InvalidRequest, "error.workspace.invalid");

    private static ControllerResult<BrowserWorkspaceCommandReceipt> InvalidResult() =>
        ControllerResult<BrowserWorkspaceCommandReceipt>.Failure(Invalid());

    private static ControllerResult<BrowserWorkspaceCommandReceipt> Failure(
        ControllerErrorCode code,
        string key) =>
        ControllerResult<BrowserWorkspaceCommandReceipt>.Failure(ControllerError.Create(code, key));
}
