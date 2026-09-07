using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using OrbitNavigator.ClipboardShelf.Presentation;
using OrbitNavigator.App.Accounts;
using OrbitNavigator.App.Composition;
using OrbitNavigator.App.Diagnostics;
using OrbitNavigator.App.Offline;
using OrbitNavigator.App.QuickView;
using OrbitNavigator.App.Resources;
using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Foundation.Browser;
using OrbitNavigator.Foundation.Offline;
using OrbitNavigator.Foundation.Resources;
using OrbitNavigator.Presentation.Permissions;
using OrbitNavigator.Presentation.Accessibility;
using OrbitNavigator.Presentation.Navigation;
using OrbitNavigator.Presentation.Offline;
using OrbitNavigator.Presentation.Protection;
using OrbitNavigator.Presentation.Shell;
using OrbitNavigator.Presentation.Workspace;
using OrbitNavigator.Presentation.Resources;
using OrbitNavigator.Presentation.QuickView;
using OrbitNavigator.Presentation.Tabs;
using OrbitNavigator.Presentation.Wpf;
using OrbitNavigator.WebViewHost;
using OrbitNavigator.Updates;
using FoundationTabStripPlacement = OrbitNavigator.Foundation.Browser.TabStripPlacement;
using PresentationTabStripPlacement = OrbitNavigator.Presentation.Workspace.TabStripPlacement;

namespace OrbitNavigator.App;

/// <summary>Foundation composition host for the UX-owned chrome and live tab surfaces.</summary>
public sealed class FoundationWindow : Window
{
    private readonly BrowserChromeControl _chrome = new();
    private readonly StartupLoadingOverlay _startupLoading = new();
    private readonly Grid _webSurface = new();
    private readonly NewTabPageControl _newTabPage = new();
    private readonly IBrowserWorkspaceAuditSink? _workspaceAudit;
    private readonly Dictionary<BrowserTabId, WebView2HostControl> _hosts = [];
    private readonly BrowserWorkspaceCoordinator _workspaceCoordinator;
    private readonly BrowserWorkspaceSessionSnapshot? _restoredSession;
    private readonly ITabControllerLayoutStore _tabControllerLayouts;
    private readonly PrivacyContext _privacy;
    private readonly BrowserWindowId _windowId;
    private readonly PreparedWebViewHost _initialHost;
    private readonly Func<BrowsingContext, Task<PreparedWebViewHost?>> _prepareHost;
    private readonly IPrivateWindowLifecycle _privateWindows;
    private readonly Func<PrivateWindowSession, Task<FoundationWindow?>> _createPrivateWindow;
    private readonly PermissionPromptPresenter _permissionPrompts;
    private readonly SiteProtectionPresenter _siteProtection;
    private readonly ClipboardShelfPresenter _clipboardShelf;
    private readonly IBookmarksFacade _bookmarks;
    private readonly IHistoryFacade _history;
    private readonly IBrowserSettingsFacade _settings;
    private readonly IWorkspaceUiPreferencesStore _workspacePreferences;
    private readonly IAffiliatedSitesVisibilityStore _affiliatedSitesVisibility;
    private readonly IWorkspacePresetFacade _workspacePresets;
    private readonly IWorkspaceArtworkStore _workspaceArtwork;
    private readonly IOfflineReadingStore _offlineReading;
    private readonly MyOrbitAccountSettingsAdapter _myOrbitAccountSettings;
    private readonly BetaUpdateClient _betaUpdates;
    private readonly Action _retryStartup;
    private readonly TabControllerPresentationSession _tabControllerSession;
    private readonly BrowserResourceSampler _resourceSampler;
    private readonly QuickViewSessionState _quickViewState;
    private readonly SemaphoreSlim _quickViewGate = new(1, 1);
    private readonly Dictionary<string, BookmarkEntry> _quickViewBookmarks = new(StringComparer.Ordinal);
    private OfflineReadingCatalogSnapshot _offlineCatalog;
    private string _offlineStatus = "Saved pages stay on this device and are not live websites.";
    private bool _offlineBusy;
    private OfflineLibraryControl? _offlineLibraryControl;
    private Window? _offlineLibraryWindow;
    private PreparedWebViewHost? _quickViewPrepared;
    private WebView2HostControl? _quickViewHost;
    private BrowserState _browserState;
    private TabControllerLayoutRevision _tabControllerLayoutRevision;
    private TabControllerHostState _tabControllerHostState = TabControllerHostState.Docked;
    private FoundationTabControllerWindow? _tabControllerWindow;
    private ResourceTaskWindow? _resourceMonitorWindow;
    private readonly ResourceSamplingLifecycle _resourceSampling;
    private readonly SemaphoreSlim _tabVisualUpdateGate = new(1, 1);
    private readonly Dictionary<BrowserTabId, WebViewTabVisualState> _tabVisualStates = [];
    private readonly Dictionary<BrowserTabGroupId, TabGroupPresentation> _groupPresentations = [];
    private int _resourceSampleMisses;
    private IInputElement? _tabControllerOriginFocus;
    private WorkspaceUiPreferencesRevision _workspacePreferencesRevision;
    private WorkspacePresetCatalogRevision _workspacePresetRevision;
    private bool _startupFailureShown;
    private readonly TaskCompletionSource<bool> _initialHostReady = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _startupOverlayCleared = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _windowLifetime = new();

    public FoundationWindow(
        PreparedWebViewHost initialHost,
        UxComposition ux,
        PrivacyPersistenceComposition privacyPersistence,
        BrowsingContext context,
        BrowserWorkspaceCoordinator workspaceCoordinator,
        BrowserWorkspaceSessionSnapshot? restoredSession,
        ITabControllerLayoutStore tabControllerLayouts,
        Func<BrowsingContext, Task<PreparedWebViewHost?>> prepareHost,
        IPrivateWindowLifecycle privateWindows,
        Func<PrivateWindowSession, Task<FoundationWindow?>> createPrivateWindow,
        PermissionPromptPresenter permissionPrompts,
        SiteProtectionPresenter siteProtection,
        ClipboardShelfPresenter clipboardShelf,
        IBookmarksFacade bookmarks,
        IHistoryFacade history,
        IBrowserSettingsFacade settings,
        IWorkspaceUiPreferencesStore workspacePreferences,
        IAffiliatedSitesVisibilityStore affiliatedSitesVisibility,
        IWorkspacePresetFacade workspacePresets,
        IWorkspaceArtworkStore workspaceArtwork,
        IOfflineReadingStore offlineReading,
        LocalPrivacyComposition localPrivacy,
        MyOrbitAccountSettingsAdapter myOrbitAccountSettings,
        BetaUpdateClient betaUpdates,
        OptionalSyncComposition optionalSync,
        string payloadRoot,
        Action retryStartup,
        IBrowserWorkspaceAuditSink? workspaceAudit = null)
    {
        _initialHost = initialHost ?? throw new ArgumentNullException(nameof(initialHost));
        Ux = ux ?? throw new ArgumentNullException(nameof(ux));
        PrivacyPersistence = privacyPersistence ?? throw new ArgumentNullException(nameof(privacyPersistence));
        ArgumentNullException.ThrowIfNull(context);
        _workspaceCoordinator = workspaceCoordinator ?? throw new ArgumentNullException(nameof(workspaceCoordinator));
        _restoredSession = restoredSession;
        _tabControllerLayouts = tabControllerLayouts ?? throw new ArgumentNullException(nameof(tabControllerLayouts));
        _prepareHost = prepareHost ?? throw new ArgumentNullException(nameof(prepareHost));
        _privateWindows = privateWindows ?? throw new ArgumentNullException(nameof(privateWindows));
        _createPrivateWindow = createPrivateWindow ?? throw new ArgumentNullException(nameof(createPrivateWindow));
        _permissionPrompts = permissionPrompts ?? throw new ArgumentNullException(nameof(permissionPrompts));
        _siteProtection = siteProtection ?? throw new ArgumentNullException(nameof(siteProtection));
        _clipboardShelf = clipboardShelf ?? throw new ArgumentNullException(nameof(clipboardShelf));
        _bookmarks = bookmarks ?? throw new ArgumentNullException(nameof(bookmarks));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _workspacePreferences = workspacePreferences ?? throw new ArgumentNullException(nameof(workspacePreferences));
        _affiliatedSitesVisibility = affiliatedSitesVisibility ?? throw new ArgumentNullException(nameof(affiliatedSitesVisibility));
        _workspacePresets = workspacePresets ?? throw new ArgumentNullException(nameof(workspacePresets));
        _workspaceArtwork = workspaceArtwork ?? throw new ArgumentNullException(nameof(workspaceArtwork));
        _offlineReading = offlineReading ?? throw new ArgumentNullException(nameof(offlineReading));
        LocalPrivacy = localPrivacy ?? throw new ArgumentNullException(nameof(localPrivacy));
        _myOrbitAccountSettings = myOrbitAccountSettings ?? throw new ArgumentNullException(nameof(myOrbitAccountSettings));
        _betaUpdates = betaUpdates ?? throw new ArgumentNullException(nameof(betaUpdates));
        OptionalSync = optionalSync ?? throw new ArgumentNullException(nameof(optionalSync));
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadRoot);
        _retryStartup = retryStartup ?? throw new ArgumentNullException(nameof(retryStartup));
        _workspaceAudit = workspaceAudit;
        _privacy = context.Privacy;
        _windowId = context.WindowId;
        _offlineCatalog = new(_privacy.ProfileId, 0, []);
        _quickViewState = new QuickViewSessionState(_privacy);
        _hosts.Add(context.TabId, initialHost.Host);
        AttachHost(initialHost.Host);
        _webSurface.Children.Add(initialHost.Host);
        _newTabPage.ShowDonationLink = false;
        _newTabPage.CanModifyWorkspace = !_privacy.IsPrivate;
        _newTabPage.ApplyPrivateMode(_privacy.IsPrivate);
        _newTabPage.NavigationRequested += OnNewTabNavigationRequested;
        _newTabPage.BookmarkLaunchRequested += OnBookmarkLaunchRequested;
        _newTabPage.ManageBookmarksRequested += OnManageBookmarksRequested;
        _newTabPage.OpenWorkspaceRequested += OnOpenWorkspaceRequested;
        _newTabPage.ConfigureWorkspaceRequested += OnConfigureWorkspaceRequested;
        _newTabPage.RemoveWorkspaceRequested += OnRemoveWorkspaceRequested;
        _newTabPage.AffiliatedSiteLaunchRequested += OnAffiliatedSiteLaunchRequested;
        _newTabPage.AffiliatedSitesVisibilityChangeRequested += OnAffiliatedSitesVisibilityChangeRequested;
        _webSurface.Children.Add(_newTabPage);
        Panel.SetZIndex(_newTabPage, 1);
        _browserState = _workspaceCoordinator.Current.Browser;
        SynchronizeGroupCatalog(_workspaceCoordinator.Current.Groups);
        _chrome.WorkspaceLocalArtworkImporter = ImportWorkspaceArtwork;
        _tabControllerSession = new TabControllerPresentationSession(
            _windowId,
            _privacy.IsPrivate,
            new WindowTabControllerCommandSink(this));
        _chrome.BindTabControllerSession(_tabControllerSession);
        var processSource = new WebViewResourceProcessSource(
            _privacy,
            () => _hosts.Values.ToArray());
        _resourceSampler = new BrowserResourceSampler(
            _privacy,
            _windowId,
            processSource,
            new WindowsResourceMeasurementSource(),
            new StopwatchTimestampSource());
        _resourceSampling = new ResourceSamplingLifecycle(
            ResourceTaskPanelControl.RecommendedSamplingInterval,
            _windowLifetime.Token,
            SampleResourcesOnceAsync);

        Title = "Orbit Navigator";
        Icon = OrbitProgramIdentity.CreateWindowIcon();
        Width = 1200;
        Height = 800;
        MinWidth = 640;
        MinHeight = 480;
        var reducedMotion = !SystemParameters.ClientAreaAnimation;
        _chrome.ReducedMotion = reducedMotion;
        _newTabPage.ReducedMotion = reducedMotion;
        _startupLoading.ReducedMotion = reducedMotion;
        var resolvedPayloadRoot = Path.GetFullPath(payloadRoot);
        var newTabAssetRoot = Path.Combine(resolvedPayloadRoot, "assets", "new-tab");
        _newTabPage.SetCoordinatorDecoration(
            Path.Combine(newTabAssetRoot, "orbit-navigation-field-v2.png"),
            SystemParameters.HighContrast);
        _newTabPage.SetStellarHubAssets(
            Path.Combine(newTabAssetRoot, "bookmark-star-v1.png"),
            Path.Combine(newTabAssetRoot, "workspace-star-v1.png"));
        var sequenceDirectory = Path.Combine(
            resolvedPayloadRoot,
            StartupLoadingOverlay.CoordinatorSequenceRelativeDirectory);
        if (!_startupLoading.SetSequenceAssetDirectory(sequenceDirectory))
        {
            _startupLoading.SetBackgroundAsset(Path.Combine(
                resolvedPayloadRoot,
                StartupLoadingOverlay.CoordinatorAssetRelativePath));
        }
        _startupLoading.SetState(StartupLoadingState.Initial());
        _startupLoading.RetryRequested += OnStartupRetryRequested;
        _startupLoading.Cleared += OnStartupLoadingCleared;
        _chrome.WebContent = _webSurface;
        _chrome.SetBrowsingContext(context);
        _chrome.BindPermissionPrompt(_permissionPrompts);
        _chrome.BrowserCommandRequested += OnBrowserCommandRequested;
        _chrome.TabGroupToggleRequested += OnTabGroupToggleRequested;
        _chrome.TabGroupCreateRequested += OnTabGroupCreateRequested;
        _chrome.TabGroupRenameRequested += OnTabGroupRenameRequested;
        _chrome.TabGroupUngroupRequested += OnTabGroupUngroupRequested;
        _chrome.PrivateWindowRequested += OnPrivateWindowRequested;
        _chrome.UtilitySurfaceRequested += OnUtilitySurfaceRequested;
        _chrome.WorkspacePreferencesChanged += OnWorkspacePreferencesChanged;
        _chrome.CompactTabModeChangeRequested += OnCompactTabModeChangeRequested;
        _chrome.ShowTabsRequested += OnShowTabsRequested;
        _chrome.ResourceMonitorRequested += OnResourceMonitorRequested;
        _chrome.OfflineReadingActionRequested += OnOfflineReadingActionRequested;
        _chrome.QuickViewActionRequested += OnQuickViewActionRequested;
        ConfigureUtilitySurfaceAvailability();
        _siteProtection.ReloadRequested += OnProtectionReloadRequested;
        Ux.Shell.SetPrivateMode(_privacy.IsPrivate);
        RenderBrowserState();
        var compositionRoot = new Grid();
        compositionRoot.Children.Add(_chrome);
        compositionRoot.Children.Add(_startupLoading);
        Panel.SetZIndex(_startupLoading, 1);
        Content = compositionRoot;
        Closing += OnClosing;
        Closed += OnClosed;
        Loaded += OnLoaded;
    }

    public UxComposition Ux { get; }
    public PrivacyPersistenceComposition PrivacyPersistence { get; }
    public LocalPrivacyComposition LocalPrivacy { get; }
    public OptionalSyncComposition OptionalSync { get; }
    public SiteProtectionPresenter SiteProtection => _siteProtection;
    public ClipboardShelfPresenter ClipboardShelf => _clipboardShelf;
    public Task<bool> InitialHostReady => _initialHostReady.Task;
    public Task<bool> StartupOverlayCleared => _startupOverlayCleared.Task;
    public int StartupSequenceFrameCount => _startupLoading.LoadedSequenceFrameCount;
    public StartupLoadingPhase StartupLoadingPhase => _startupLoading.State.Phase;
    public bool ReducedMotion => _startupLoading.ReducedMotion;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            // Let the initial Starting state render before beginning any
            // attached WebView/profile work.
            await Dispatcher.Yield(DispatcherPriority.Render);
            if (_windowLifetime.IsCancellationRequested)
            {
                _initialHostReady.TrySetResult(false);
                return;
            }

            _startupLoading.SetState(StartupLoadingState.Create(
                StartupLoadingPhase.PreparingProfile,
                "Preparing your local privacy profile\u2026",
                0.28));
            await Dispatcher.Yield(DispatcherPriority.Background);

            _startupLoading.SetState(StartupLoadingState.Create(
                StartupLoadingPhase.StartingBrowserEngine,
                "Starting the protected web engine\u2026",
                0.62));
            var initialized = await _initialHost.InitializeAttachedAsync(
                TimeSpan.FromSeconds(20),
                _windowLifetime.Token);
            if (!initialized.IsSuccess)
            {
                if (_windowLifetime.IsCancellationRequested)
                {
                    _initialHostReady.TrySetResult(false);
                    return;
                }
                ShowStartupFailure();
                return;
            }

            if (_windowLifetime.IsCancellationRequested)
            {
                _initialHostReady.TrySetResult(false);
                return;
            }
            _startupLoading.SetState(StartupLoadingState.Create(
                StartupLoadingPhase.RestoringSession,
                "Restoring local tabs and groups\u2026",
                0.88));
            await RestoreWorkspaceHostsAsync();
            await LoadWorkspacePreferencesAsync();
            await LoadAffiliatedSitesVisibilityAsync();
            await RefreshWorkspaceDataAsync();
            await LoadTabControllerLayoutAsync();
            await LoadOfflineReadingAsync();
            await LoadQuickViewBookmarksAsync();
            RenderBrowserState();
            ShowSelectedHost();
            CompleteInitialHostReady();
        }
        catch
        {
            if (_windowLifetime.IsCancellationRequested)
            {
                _initialHostReady.TrySetResult(false);
                return;
            }
            ShowStartupFailure();
        }
    }

    private async void OnBrowserCommandRequested(object? sender, BrowserCommand command)
    {
        if (command.WindowId != _windowId) return;
        switch (command)
        {
            case CreateTabBrowserCommand create:
                await CreateTabAsync(create);
                break;
            case CloseTabBrowserCommand close:
                await CloseTabAsync(close.TabId);
                break;
            case SelectTabBrowserCommand select:
                await SelectTabAsync(select.TabId);
                break;
            case MoveTabBrowserCommand move:
                await MoveTabAsync(move.TabId, move.NewIndex, move.GroupId);
                break;
            case NavigateBrowserCommand navigate when _hosts.TryGetValue(navigate.TabId, out var navigateHost):
                var canonicalTarget = CanonicalWebAddress.Normalize(navigate.Target);
                if ((await navigateHost.NavigateAsync(canonicalTarget)).IsSuccess)
                {
                    await UpdateTabAsync(navigate.TabId, tab => tab with { Address = canonicalTarget, Title = canonicalTarget.Host, LoadState = BrowserLoadState.Loading });
                    ShowSelectedHost();
                    await LoadProtectionAsync(navigateHost);
                    await RecordHistoryAsync(navigateHost.Context, canonicalTarget, canonicalTarget.Host);
                }
                break;
            case GoBackBrowserCommand back when _hosts.TryGetValue(back.TabId, out var backHost):
                await backHost.GoBackAsync();
                break;
            case GoForwardBrowserCommand forward when _hosts.TryGetValue(forward.TabId, out var forwardHost):
                await forwardHost.GoForwardAsync();
                break;
            case ReloadBrowserCommand reload when _hosts.TryGetValue(reload.TabId, out var reloadHost):
                await reloadHost.ReloadAsync();
                break;
        }
    }

    private async void OnNewTabNavigationRequested(
        object? sender,
        NewTabNavigationRequestedEventArgs args)
    {
        if (_browserState.SelectedTabId is not { } selectedTabId ||
            !_hosts.TryGetValue(selectedTabId, out var host))
        {
            return;
        }

        var target = CanonicalWebAddress.Normalize(args.Target.Uri);
        if ((await host.NavigateAsync(target)).IsSuccess)
        {
            await UpdateTabAsync(selectedTabId, tab => tab with
            {
                Address = target,
                Title = target.Host,
                LoadState = BrowserLoadState.Loading,
            });
            ShowSelectedHost();
            await LoadProtectionAsync(host);
            await RecordHistoryAsync(host.Context, target, target.Host);
        }
    }

    private async void OnBookmarkLaunchRequested(object? sender, BookmarkLaunchRequestedEventArgs args) =>
        await NavigateSelectedTabAsync(args.Bookmark.Target, args.Bookmark.Title);

    private async void OnAffiliatedSiteLaunchRequested(
        object? sender,
        AffiliatedSiteLaunchRequestedEventArgs args)
    {
        if (!AffiliatedSitesHostPolicy.TryResolveApprovedTarget(args, out var target) || target is null)
        {
            return;
        }

        await CreateTabAsync(new CreateTabBrowserCommand(
            _windowId,
            new BrowserTabId(Guid.NewGuid()),
            target,
            null));
    }

    private async void OnAffiliatedSitesVisibilityChangeRequested(
        object? sender,
        AffiliatedSitesVisibilityChangeRequestedEventArgs args)
    {
        if (_privacy.IsPrivate)
        {
            await LoadAffiliatedSitesVisibilityAsync();
            return;
        }

        var saved = await _affiliatedSitesVisibility.SaveAsync(new(
            _privacy,
            args.ExpectedRevision,
            args.IsHidden),
            _windowLifetime.Token);
        if (saved.IsSuccess)
        {
            ApplyAffiliatedSitesVisibility(saved.Value!);
            return;
        }

        await LoadAffiliatedSitesVisibilityAsync();
    }

    private async void OnManageBookmarksRequested(object? sender, EventArgs args) =>
        await ShowBookmarksAsync();

    private async void OnWorkspacePreferencesChanged(
        object? sender,
        WorkspacePreferencesChangedEventArgs args)
    {
        if (_privacy.IsPrivate)
        {
            // Private windows may use a session-only presentation choice. The
            // persistent store remains authoritative and is never called here.
            _chrome.ApplyWorkspacePreferences(args.Preferences);
            _newTabPage.ApplyWorkspacePreferences(args.Preferences);
            RenderBrowserState();
            return;
        }

        var save = await _workspacePreferences.SaveAsync(new(
            _privacy,
            _workspacePreferencesRevision,
            ToFoundationPlacement(args.Preferences.TabStripPlacement),
            args.Preferences.CollapseToActive,
            args.Preferences.ShowOrbitalGroupPreview,
            _chrome.IsCompactTabMode,
            ToFoundationNewTabMode(args.Preferences.NewTabMode),
            ToFoundationPreviewMode(args.Preferences.WorkspacePreviewReveal),
            ToFoundationRailPlacement(args.Preferences.AffiliatedRailPlacement),
            args.Preferences.ShowAffiliatedRail,
            args.Preferences.SideTabPanelWidth));
        if (save.IsSuccess)
        {
            ApplyWorkspacePreferences(save.Value!);
            return;
        }

        await LoadWorkspacePreferencesAsync();
        _newTabPage.ShowWorkspaceOperationMessage(
            save.Error?.Code == ControllerErrorCode.Conflict
                ? "Workspace preferences changed elsewhere and were refreshed."
                : "Workspace preferences could not be saved.",
            true);
    }

    private async void OnCompactTabModeChangeRequested(
        object? sender,
        CompactTabModeChangeRequestedEventArgs args)
    {
        if (_privacy.IsPrivate)
        {
            _chrome.ApplyCompactTabMode(args.IsCompactModeRequested);
            return;
        }

        var current = _chrome.WorkspacePreferences;
        var save = await _workspacePreferences.SaveAsync(new(
            _privacy,
            _workspacePreferencesRevision,
            ToFoundationPlacement(current.TabStripPlacement),
            current.CollapseToActive,
            current.ShowOrbitalGroupPreview,
            args.IsCompactModeRequested,
            ToFoundationNewTabMode(current.NewTabMode),
            ToFoundationPreviewMode(current.WorkspacePreviewReveal),
            ToFoundationRailPlacement(current.AffiliatedRailPlacement),
            current.ShowAffiliatedRail,
            current.SideTabPanelWidth));
        if (save.IsSuccess)
        {
            ApplyWorkspacePreferences(save.Value!);
            return;
        }

        await LoadWorkspacePreferencesAsync();
        _newTabPage.ShowWorkspaceOperationMessage(
            save.Error?.Code == ControllerErrorCode.Conflict
                ? "Compact tab mode changed elsewhere and was refreshed."
                : "Compact tab mode could not be saved.",
            true);
    }

    private async void OnOpenWorkspaceRequested(object? sender, OpenWorkspaceRequestedEventArgs args)
    {
        if (!args.OpenAsNewLiveGroup || !args.InitiallyCollapsed ||
            !args.SelectFirstSiteImmediately || args.Workspace.Id.ProfileId != _privacy.ProfileId)
        {
            _newTabPage.ShowWorkspaceOperationMessage("That workspace request is no longer valid.", true);
            return;
        }

        var catalog = await _workspacePresets.QueryAsync(_privacy, _windowLifetime.Token);
        var preset = catalog.IsSuccess
            ? catalog.Value!.Presets.SingleOrDefault(candidate =>
                candidate.Id.ProfileId == args.Workspace.Id.ProfileId &&
                candidate.Id.Value == args.Workspace.Id.Value)
            : null;
        if (preset is null)
        {
            await RefreshWorkspaceDataAsync();
            _newTabPage.ShowWorkspaceOperationMessage(
                "That workspace changed or is no longer available.",
                true);
            return;
        }

        var groupId = new BrowserTabGroupId(Guid.NewGuid());
        var preparedHosts = new List<(BrowserTabId TabId, WebView2HostControl Host, Uri Target)>();
        try
        {
            foreach (var tab in preset.Tabs)
            {
                var tabId = new BrowserTabId(Guid.NewGuid());
                var context = new BrowsingContext(_privacy, _windowId, tabId, null);
                var prepared = await _prepareHost(context);
                if (prepared is null)
                {
                    await DisposePreparedWorkspaceHostsAsync(preparedHosts);
                    _newTabPage.ShowWorkspaceOperationMessage(
                        $"{preset.Name} could not prepare its browser tabs.",
                        true);
                    return;
                }
                var host = prepared.Host;
                var target = CanonicalWebAddress.Normalize(tab.Target);
                preparedHosts.Add((tabId, host, target));
                // Collapsed WebView2 controls do not create a controller reliably.
                // Hidden keeps the attached control loaded without exposing an
                // uncommitted workspace tab in the browser surface.
                host.Visibility = Visibility.Hidden;
                host.IsHitTestVisible = false;
                _hosts.Add(tabId, host);
                AttachHost(host);
                _webSurface.Children.Add(host);
                var initialized = await prepared.InitializeAttachedAsync(
                    TimeSpan.FromSeconds(20),
                    _windowLifetime.Token);
                host.Visibility = Visibility.Collapsed;
                host.IsHitTestVisible = true;
                if (!initialized.IsSuccess)
                {
                    await DisposePreparedWorkspaceHostsAsync(preparedHosts);
                    _newTabPage.ShowWorkspaceOperationMessage(
                        $"{preset.Name} could not prepare its browser tabs.",
                        true);
                    return;
                }
            }

            // Resolve every host target before the serialized coordinator commit.
            // A rejected host navigation therefore leaves both the authoritative
            // session and all pre-existing tabs untouched.
            foreach (var prepared in preparedHosts)
            {
                if (!(await prepared.Host.NavigateAsync(
                        prepared.Target,
                        _windowLifetime.Token)).IsSuccess)
                {
                    await DisposePreparedWorkspaceHostsAsync(preparedHosts);
                    _newTabPage.ShowWorkspaceOperationMessage(
                        $"{preset.Name} could not prepare its browser tabs.",
                        true);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (_windowLifetime.IsCancellationRequested)
        {
            await DisposePreparedWorkspaceHostsAsync(preparedHosts);
            return;
        }
        catch
        {
            await DisposePreparedWorkspaceHostsAsync(preparedHosts);
            _newTabPage.ShowWorkspaceOperationMessage(
                $"{preset.Name} could not prepare its browser tabs.",
                true);
            return;
        }

        var selectedIndex = Math.Clamp(preset.FirstTabIndex, 0, preparedHosts.Count - 1);
        var tabStates = preparedHosts.Select((prepared, index) => new BrowserTabState(
            prepared.TabId,
            groupId,
            prepared.Target,
            preset.Tabs[index].DisplayTitle,
            BrowserLoadState.Loading,
            false,
            false,
            _privacy.IsPrivate)).ToArray();
        var opened = await _workspaceCoordinator.ExecuteAsync(new OpenWorkspaceGroupAction(
            _windowId,
            CurrentRevision(),
            tabStates,
            groupId,
            string.IsNullOrWhiteSpace(preset.GroupName) ? preset.Name : preset.GroupName!,
            preset.ColorToken,
            preparedHosts[selectedIndex].TabId,
            InitiallyCollapsed: true,
            IsTemporary: false), _windowLifetime.Token);
        if (!opened.IsSuccess)
        {
            await DisposePreparedWorkspaceHostsAsync(preparedHosts);
            _newTabPage.ShowWorkspaceOperationMessage(
                $"{preset.Name} could not be opened as one complete live group.",
                true);
            return;
        }
        ApplyWorkspaceSnapshot(opened.Value!.Snapshot);
        ShowSelectedHost();
        _newTabPage.ShowWorkspaceOperationMessage(
            $"Opened {preset.Name} as a collapsed live group.",
            false);
    }

    private async Task DisposePreparedWorkspaceHostsAsync(
        IEnumerable<(BrowserTabId TabId, WebView2HostControl Host, Uri Target)> preparedHosts)
    {
        foreach (var prepared in preparedHosts)
        {
            DetachHost(prepared.Host);
            _hosts.Remove(prepared.TabId);
            _webSurface.Children.Remove(prepared.Host);
            await prepared.Host.DisposeAsync();
        }
    }

    private async void OnConfigureWorkspaceRequested(
        object? sender,
        ConfigureWorkspaceRequestedEventArgs args)
    {
        var dialog = new WorkspacePresetEditorDialog(
            args.Workspace,
            canModify: !_privacy.IsPrivate)
        {
            Owner = this,
            Icon = OrbitProgramIdentity.CreateWindowIcon(),
        };
        if (dialog.ShowDialog() != true || dialog.Draft is null)
        {
            return;
        }

        var draft = dialog.Draft;
        WorkspacePresetId? existingId = args.Workspace is null
            ? null
            : new WorkspacePresetId(args.Workspace.Id.ProfileId, args.Workspace.Id.Value);
        var save = await _workspacePresets.UpsertAsync(new(
            _privacy,
            _workspacePresetRevision,
            existingId,
            draft.Name,
            draft.GroupName,
            draft.Tabs.Select(tab => new WorkspacePresetTab(tab.Target, tab.DisplayTitle)
            {
                Note = tab.Note,
                FaviconPng = tab.FaviconPng.ToArray(),
            }).ToArray())
        {
            ColorToken = draft.ColorToken,
            Note = draft.Note,
            Artwork = ToStoredArtwork(draft.Artwork),
            FirstTabIndex = draft.FirstTabIndex,
        });
        if (!save.IsSuccess)
        {
            await RefreshWorkspaceDataAsync();
            _newTabPage.ShowWorkspaceOperationMessage(
                save.Error?.Code == ControllerErrorCode.Conflict
                    ? "Workspaces changed elsewhere. The latest list is shown."
                    : "The workspace could not be saved.",
                true);
            return;
        }

        _workspacePresetRevision = save.Value!.Revision;
        await RefreshWorkspaceDataAsync();
        _newTabPage.ShowWorkspaceOperationMessage("Workspace saved.", false);
    }

    private async void OnRemoveWorkspaceRequested(
        object? sender,
        RemoveWorkspaceRequestedEventArgs args)
    {
        if (_privacy.IsPrivate)
        {
            _newTabPage.ShowWorkspaceOperationMessage(
                "Workspace changes are unavailable in private windows.",
                true);
            return;
        }
        if (MessageBox.Show(
                this,
                $"Remove {args.WorkspaceName}?",
                "Orbit Navigator",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        var result = await _workspacePresets.RemoveAsync(new(
            _privacy,
            _workspacePresetRevision,
            new WorkspacePresetId(args.WorkspaceId.ProfileId, args.WorkspaceId.Value)));
        await RefreshWorkspaceDataAsync();
        _newTabPage.ShowWorkspaceOperationMessage(
            result.IsSuccess ? "Workspace removed." : "The workspace could not be removed.",
            !result.IsSuccess);
    }

    private async Task CreateTabAsync(CreateTabBrowserCommand command)
    {
        if (command.TabId.IsEmpty || _hosts.ContainsKey(command.TabId)) return;
        var context = new BrowsingContext(_privacy, _windowId, command.TabId, null);
        var prepared = await _prepareHost(context);
        if (prepared is null) return;
        var host = prepared.Host;
        _hosts.Add(command.TabId, host);
        AttachHost(host);
        _webSurface.Children.Add(host);
        var initialized = await prepared.InitializeAttachedAsync(
            TimeSpan.FromSeconds(20),
            _windowLifetime.Token);
        if (!initialized.IsSuccess)
        {
            DetachHost(host);
            _hosts.Remove(command.TabId);
            _webSurface.Children.Remove(host);
            return;
        }

        var added = await _workspaceCoordinator.ExecuteAsync(new AddWorkspaceTabAction(
            _windowId,
            _workspaceCoordinator.Current.Revision,
            NewTab(command.TabId, _privacy.IsPrivate) with { GroupId = command.GroupId },
            Select: true));
        if (!added.IsSuccess)
        {
            DetachHost(host);
            _hosts.Remove(command.TabId);
            _webSurface.Children.Remove(host);
            await host.DisposeAsync();
            return;
        }
        ApplyWorkspaceSnapshot(added.Value!.Snapshot);
        ShowSelectedHost(keepSelectedNewTabHostVisible: true);

        if (command.InitialTarget is not null)
        {
            var initialTarget = CanonicalWebAddress.Normalize(command.InitialTarget);
            if ((await host.NavigateAsync(initialTarget)).IsSuccess)
            {
                await UpdateTabAsync(command.TabId, tab => tab with
                {
                    Address = initialTarget,
                    Title = initialTarget.Host,
                    LoadState = BrowserLoadState.Loading,
                });
                await LoadProtectionAsync(host);
                await RecordHistoryAsync(host.Context, initialTarget, initialTarget.Host);
            }
        }
        ShowSelectedHost();
    }

    private async Task CloseTabAsync(BrowserTabId tabId)
    {
        if (!_hosts.TryGetValue(tabId, out var host) || _hosts.Count == 1) return;
        var result = await _workspaceCoordinator.ExecuteAsync(new CloseWorkspaceTabsAction(
            _windowId,
            _workspaceCoordinator.Current.Revision,
            [tabId]));
        if (!result.IsSuccess) return;
        _hosts.Remove(tabId);
        DetachHost(host);
        _webSurface.Children.Remove(host);
        await host.DisposeAsync();
        ApplyWorkspaceSnapshot(result.Value!.Snapshot);
        ShowSelectedHost();
    }

    private async Task SelectTabAsync(BrowserTabId tabId)
    {
        if (!_hosts.ContainsKey(tabId)) return;
        var result = await _workspaceCoordinator.ExecuteAsync(new SelectWorkspaceTabAction(
            _windowId,
            _workspaceCoordinator.Current.Revision,
            tabId));
        if (!result.IsSuccess) return;
        ApplyWorkspaceSnapshot(result.Value!.Snapshot);
        ShowSelectedHost();
        _ = LoadProtectionAsync(_hosts[tabId]);
    }

    private async Task MoveTabAsync(BrowserTabId tabId, int newIndex, BrowserTabGroupId? groupId)
    {
        var result = await _workspaceCoordinator.ExecuteAsync(new MoveWorkspaceTabAction(
            _windowId,
            _workspaceCoordinator.Current.Revision,
            tabId,
            newIndex,
            groupId));
        if (result.IsSuccess) ApplyWorkspaceSnapshot(result.Value!.Snapshot);
    }

    private async void OnTabGroupToggleRequested(object? sender, TabGroupToggleRequestedEventArgs args)
    {
        await _tabControllerSession.ExecuteAsync(new ToggleTabGroupControllerAction(
            Guid.NewGuid(), args.GroupId, args.IsCollapsed));
    }

    private async void OnTabGroupCreateRequested(
        object? sender,
        TabGroupCreateRequestedEventArgs args)
    {
        await _tabControllerSession.ExecuteAsync(new CreateTabGroupControllerAction(
            Guid.NewGuid(), args.TabIds, args.SuggestedName));
    }

    private async void OnTabGroupRenameRequested(
        object? sender,
        TabGroupRenameRequestedEventArgs args)
    {
        await _tabControllerSession.ExecuteAsync(new RenameTabGroupControllerAction(
            Guid.NewGuid(), args.GroupId, args.RequestedName));
    }

    private async void OnTabGroupUngroupRequested(
        object? sender,
        TabGroupUngroupRequestedEventArgs args)
    {
        await _tabControllerSession.ExecuteAsync(new UngroupTabGroupControllerAction(
            Guid.NewGuid(), args.GroupId));
    }

    private async void OnPrivateWindowRequested(object? sender, CreatePrivateWindowIntent intent)
    {
        var session = await _privateWindows.OpenAsync(intent);
        if (!session.IsSuccess) return;
        var window = await _createPrivateWindow(session.Value!);
        window?.Show();
    }

    private async void OnUtilitySurfaceRequested(object? sender, UtilitySurfaceRequestedEventArgs args)
    {
        switch (args.Drawer)
        {
            case UtilityDrawerKind.Bookmarks:
                await ShowBookmarksAsync();
                return;
            case UtilityDrawerKind.History:
                await ShowHistoryAsync();
                return;
            case UtilityDrawerKind.ClipboardShelf:
                await ShowClipboardShelfAsync();
                return;
            case UtilityDrawerKind.Downloads:
                return;
        }

        switch (args.Page)
        {
            case InternalPageKind.Settings:
                await ShowSettingsAsync();
                break;
            case InternalPageKind.NewTab:
                await CreateTabAsync(new CreateTabBrowserCommand(
                    _windowId,
                    new BrowserTabId(Guid.NewGuid()),
                    null,
                    null));
                break;
        }
    }

    private void ConfigureUtilitySurfaceAvailability()
    {
        _chrome.SetUtilitySurfaceAvailability(
            UtilityDrawerKind.Bookmarks,
            true,
            string.Empty);
        _chrome.SetUtilitySurfaceAvailability(
            UtilityDrawerKind.History,
            true,
            string.Empty);
        _chrome.SetUtilitySurfaceAvailability(
            UtilityDrawerKind.Downloads,
            false,
            "Download tracking is not available in this build.");
        _chrome.SetUtilitySurfaceAvailability(
            UtilityDrawerKind.ClipboardShelf,
            true,
            string.Empty);
        _chrome.SetUtilitySurfaceAvailability(
            InternalPageKind.Settings,
            true,
            string.Empty);
    }

    private async Task ShowBookmarksAsync()
    {
        var query = await _bookmarks.QueryAsync(new(_privacy, null, 500));
        var dialog = new BookmarkManagerDialog(
            _privacy,
            query.IsSuccess ? query.Value! : [],
            canEditBookmarks: false,
            notes: BookmarkNotes(query.IsSuccess ? query.Value! : []))
        {
            Owner = this,
            Icon = OrbitProgramIdentity.CreateWindowIcon(),
        };
        dialog.OpenRequested += async (_, bookmark) =>
            await NavigateSelectedTabAsync(bookmark.Target, bookmark.Title);
        dialog.SaveRequested += async (_, args) =>
        {
            if (_privacy.IsPrivate)
            {
                dialog.ShowOperationResult(
                    "Bookmark changes are unavailable in private windows.",
                    true);
                return;
            }
            if (args.Draft.ExistingId is not null)
            {
                dialog.ShowOperationResult(
                    "Editing existing bookmarks is unavailable in this build.",
                    true);
                return;
            }
            if (_browserState.SelectedTabId is not { } selectedTabId ||
                !_hosts.TryGetValue(selectedTabId, out var host))
            {
                dialog.ShowOperationResult("No active tab is available.", true);
                return;
            }

            var saved = await _bookmarks.AddAsync(new(
                host.Context,
                args.Draft.Target,
                args.Draft.Title)
            {
                Note = args.Draft.Note,
            });
            await RefreshBookmarkDialogAsync(dialog);
            await RefreshWorkspaceDataAsync();
            dialog.ShowOperationResult(
                saved.IsSuccess ? "Bookmark saved." : "The bookmark could not be saved.",
                !saved.IsSuccess);
        };
        dialog.DeleteRequested += async (_, args) =>
        {
            if (_privacy.IsPrivate)
            {
                dialog.ShowOperationResult(
                    "Bookmark changes are unavailable in private windows.",
                    true);
                return;
            }
            var removed = await _bookmarks.RemoveAsync(new(_privacy, args.Bookmark.Id));
            await RefreshBookmarkDialogAsync(dialog);
            await RefreshWorkspaceDataAsync();
            dialog.ShowOperationResult(
                removed.IsSuccess ? "Bookmark deleted." : "The bookmark could not be deleted.",
                !removed.IsSuccess);
        };
        if (!query.IsSuccess)
        {
            dialog.ShowOperationResult("Bookmarks could not be loaded.", true);
        }
        dialog.Show();
    }

    private async Task RefreshBookmarkDialogAsync(BookmarkManagerDialog dialog)
    {
        var latest = await _bookmarks.QueryAsync(new(_privacy, null, 500));
        var bookmarks = latest.IsSuccess ? latest.Value! : [];
        dialog.SetBookmarks(bookmarks, BookmarkNotes(bookmarks));
    }

    private async Task ShowHistoryAsync()
    {
        var window = new FoundationUtilityWindow(
            _privacy,
            _history,
            _settings,
            _clipboardShelf,
            _myOrbitAccountSettings,
            _betaUpdates,
            GetSelectedBrowsingContext,
            OpenUtilityTargetAsync)
        {
            Owner = this,
        };
        await window.ShowHistoryAsync();
        window.Show();
    }

    private async Task ShowClipboardShelfAsync()
    {
        if (_browserState.SelectedTabId is not { } selectedTabId ||
            !_hosts.TryGetValue(selectedTabId, out var host))
        {
            return;
        }
        await _clipboardShelf.LoadAsync(host.Context);
        var window = new FoundationUtilityWindow(
            _privacy,
            _history,
            _settings,
            _clipboardShelf,
            _myOrbitAccountSettings,
            _betaUpdates,
            GetSelectedBrowsingContext,
            OpenUtilityTargetAsync)
        {
            Owner = this,
        };
        window.ShowClipboardShelf();
        window.Show();
    }

    private async Task ShowSettingsAsync()
    {
        var window = new FoundationUtilityWindow(
            _privacy,
            _history,
            _settings,
            _clipboardShelf,
            _myOrbitAccountSettings,
            _betaUpdates,
            GetSelectedBrowsingContext,
            OpenUtilityTargetAsync)
        {
            Owner = this,
        };
        var initialization = window.ShowSettingsAsync();
        window.Show();
        await initialization;
    }

    private Task OpenUtilityTargetAsync(Uri target) => NavigateSelectedTabAsync(target, target.Host);

    private BrowsingContext? GetSelectedBrowsingContext() =>
        _browserState.SelectedTabId is { } selectedTabId && _hosts.TryGetValue(selectedTabId, out var host)
            ? host.Context
            : null;

    private async Task NavigateSelectedTabAsync(Uri target, string title)
    {
        if (_browserState.SelectedTabId is not { } selectedTabId ||
            !_hosts.TryGetValue(selectedTabId, out var host))
        {
            return;
        }
        target = CanonicalWebAddress.Normalize(target);
        if ((await host.NavigateAsync(target)).IsSuccess)
        {
            await UpdateTabAsync(selectedTabId, tab => tab with
            {
                Address = target,
                Title = title,
                LoadState = BrowserLoadState.Loading,
            });
            ShowSelectedHost();
            await LoadProtectionAsync(host);
            await RecordHistoryAsync(host.Context, target, title);
        }
    }

    private async Task RecordHistoryAsync(BrowsingContext context, Uri target, string title)
    {
        if (!_privacy.IsPrivate)
        {
            await _history.RecordVisitAsync(new(
                context,
                target,
                string.IsNullOrWhiteSpace(title) ? target.Host : title,
                DateTimeOffset.UtcNow));
        }
    }

    private async Task LoadWorkspacePreferencesAsync()
    {
        var result = await _workspacePreferences.LoadAsync(_privacy);
        if (!result.IsSuccess)
        {
            _chrome.ApplyWorkspacePreferences(BrowserWorkspacePreferences.Default);
            _newTabPage.ApplyWorkspacePreferences(BrowserWorkspacePreferences.Default);
            _chrome.ApplyCompactTabMode(false);
            RenderBrowserState();
            return;
        }
        ApplyWorkspacePreferences(result.Value!);
    }

    private async Task RestoreWorkspaceHostsAsync()
    {
        if (_restoredSession is null)
        {
            return;
        }

        var initialTab = _restoredSession.Tabs[0];
        if (initialTab.Address is { } initialAddress)
        {
            var canonical = CanonicalWebAddress.Normalize(initialAddress);
            if (!(await _initialHost.Host.NavigateAsync(canonical)).IsSuccess)
            {
                if (!await ClearRestoredAddressAsync(initialTab.TabId))
                {
                    throw new InvalidOperationException("The restored tab could not be repaired.");
                }
            }
            else
            {
                await LoadProtectionAsync(_initialHost.Host);
            }
        }

        foreach (var restoredTab in _restoredSession.Tabs.Skip(1))
        {
            if (_windowLifetime.IsCancellationRequested)
            {
                return;
            }

            SiteIdentity? site = null;
            if (restoredTab.Address is { } restoredAddress)
            {
                SiteIdentity.TryCreate(restoredAddress, out site);
            }

            var context = new BrowsingContext(
                _privacy,
                _windowId,
                restoredTab.TabId,
                site);
            var prepared = await _prepareHost(context);
            if (prepared is null)
            {
                if (!await RemoveUnrestorableTabAsync(restoredTab.TabId))
                {
                    throw new InvalidOperationException("The unrestorable tab could not be removed.");
                }
                continue;
            }

            var host = prepared.Host;
            _hosts.Add(restoredTab.TabId, host);
            AttachHost(host);
            _webSurface.Children.Add(host);
            var initialized = await prepared.InitializeAttachedAsync(
                TimeSpan.FromSeconds(20),
                _windowLifetime.Token);
            if (!initialized.IsSuccess)
            {
                DetachHost(host);
                _hosts.Remove(restoredTab.TabId);
                _webSurface.Children.Remove(host);
                await prepared.DisposeAsync();
                if (!await RemoveUnrestorableTabAsync(restoredTab.TabId))
                {
                    throw new InvalidOperationException("The failed tab could not be removed.");
                }
                continue;
            }

            if (restoredTab.Address is { } address)
            {
                var canonical = CanonicalWebAddress.Normalize(address);
                if (!(await host.NavigateAsync(canonical)).IsSuccess)
                {
                    if (!await ClearRestoredAddressAsync(restoredTab.TabId))
                    {
                        throw new InvalidOperationException("The restored address could not be repaired.");
                    }
                }
                else
                {
                    await LoadProtectionAsync(host);
                }
            }
        }
    }

    private async Task<bool> RemoveUnrestorableTabAsync(BrowserTabId tabId)
    {
        var snapshot = _workspaceCoordinator.Current;
        if (snapshot.Browser.Tabs.Count <= 1 ||
            snapshot.Browser.Tabs.All(tab => tab.TabId != tabId))
        {
            return true;
        }

        var result = await _workspaceCoordinator.ExecuteAsync(new CloseWorkspaceTabsAction(
            _windowId,
            snapshot.Revision,
            [tabId]));
        if (result.IsSuccess)
        {
            ApplyWorkspaceSnapshot(result.Value!.Snapshot);
        }
        return result.IsSuccess;
    }

    private async Task<bool> ClearRestoredAddressAsync(BrowserTabId tabId)
    {
        var snapshot = _workspaceCoordinator.Current;
        var tab = snapshot.Browser.Tabs.FirstOrDefault(candidate => candidate.TabId == tabId);
        if (tab is null)
        {
            return false;
        }
        var result = await _workspaceCoordinator.ExecuteAsync(new UpdateWorkspaceTabAction(
            _windowId,
            snapshot.Revision,
            tab with
            {
                Address = null,
                Title = _privacy.IsPrivate ? "Private tab" : "New Tab",
                LoadState = BrowserLoadState.Idle,
            }));
        if (result.IsSuccess)
        {
            ApplyWorkspaceSnapshot(result.Value!.Snapshot);
        }
        return result.IsSuccess;
    }

    private void ApplyWorkspacePreferences(WorkspaceUiPreferencesSnapshot snapshot)
    {
        _workspacePreferencesRevision = snapshot.Revision;
        var presentation = ToPresentationPreferences(snapshot);
        _chrome.ApplyWorkspacePreferences(presentation);
        _newTabPage.ApplyWorkspacePreferences(presentation);
        _chrome.ApplyCompactTabMode(snapshot.CompactTabs);
        RenderBrowserState();
    }

    private void AttachHost(WebView2HostControl host)
    {
        host.TabVisualStateChanged += OnHostTabVisualStateChanged;
        host.TabAudioStateChanged += OnHostTabAudioStateChanged;
    }

    private void DetachHost(WebView2HostControl host)
    {
        host.TabVisualStateChanged -= OnHostTabVisualStateChanged;
        host.TabAudioStateChanged -= OnHostTabAudioStateChanged;
    }

    private void OnHostTabAudioStateChanged(
        object? sender,
        WebViewTabAudioStateChangedEventArgs args)
    {
        if (sender is not WebView2HostControl host ||
            !_hosts.TryGetValue(args.State.TabId, out var currentHost) ||
            !ReferenceEquals(host, currentHost))
        {
            return;
        }

        _chrome.ApplyTabInteractionCapabilities(TabInteractionCapabilityMapper.FromHost(args.State));
    }

    private async void OnHostTabVisualStateChanged(
        object? sender,
        WebViewTabVisualStateChangedEventArgs args)
    {
        try
        {
            await _tabVisualUpdateGate.WaitAsync(_windowLifetime.Token);
            try
            {
                var visual = args.State;
                if (sender is not WebView2HostControl host ||
                    !_hosts.TryGetValue(visual.TabId, out var currentHost) ||
                    !ReferenceEquals(host, currentHost))
                {
                    return;
                }

                _tabVisualStates[visual.TabId] = visual;
                _chrome.ApplyTabVisualMetadata(new(
                    visual.TabId,
                    visual.Revision,
                    visual.PageTitle,
                    visual.SiteName,
                    visual.FaviconPng));
                await UpdateTabFromHostVisualAsync(visual);
            }
            finally
            {
                _tabVisualUpdateGate.Release();
            }
        }
        catch (OperationCanceledException) when (_windowLifetime.IsCancellationRequested)
        {
        }
    }

    private async Task UpdateTabFromHostVisualAsync(WebViewTabVisualState visual)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var snapshot = _workspaceCoordinator.Current;
            var current = snapshot.Browser.Tabs.FirstOrDefault(tab => tab.TabId == visual.TabId);
            if (current is null)
            {
                return;
            }

            var next = current with
            {
                Address = visual.Address,
                Title = visual.PageTitle,
                LoadState = visual.IsLoading ? BrowserLoadState.Loading : BrowserLoadState.Idle,
                CanGoBack = visual.CanGoBack,
                CanGoForward = visual.CanGoForward,
            };
            if (next == current)
            {
                return;
            }

            var result = await _workspaceCoordinator.ExecuteAsync(new UpdateWorkspaceTabAction(
                _windowId,
                snapshot.Revision,
                next));
            if (result.IsSuccess)
            {
                ApplyWorkspaceSnapshot(result.Value!.Snapshot);
                return;
            }
            if (result.Error?.Code != ControllerErrorCode.Conflict)
            {
                return;
            }
        }
    }

    private async Task LoadAffiliatedSitesVisibilityAsync()
    {
        var result = await _affiliatedSitesVisibility.LoadAsync(
            _privacy,
            _windowLifetime.Token);
        if (!result.IsSuccess)
        {
            _newTabPage.ApplyAffiliatedSites(
                AffiliatedSitesHostPolicy.Catalog,
                new AffiliatedSitesVisibilityPresentation(
                    false,
                    0,
                    false,
                    "Affiliated Sites preferences are unavailable for this profile."));
            return;
        }

        ApplyAffiliatedSitesVisibility(result.Value!);
    }

    private void ApplyAffiliatedSitesVisibility(AffiliatedSitesVisibilitySnapshot snapshot)
    {
        _newTabPage.ApplyAffiliatedSites(
            AffiliatedSitesHostPolicy.Catalog,
            new AffiliatedSitesVisibilityPresentation(
                snapshot.IsHidden,
                snapshot.Revision,
                !_privacy.IsPrivate,
                _privacy.IsPrivate
                    ? "Affiliated Sites visibility follows your normal profile and cannot be changed in private windows."
                    : null));
    }

    private async Task RefreshWorkspaceDataAsync()
    {
        var bookmarks = await _bookmarks.QueryAsync(new(_privacy, null, 24));
        var presets = await _workspacePresets.QueryAsync(_privacy);
        if (presets.IsSuccess)
        {
            _workspacePresetRevision = presets.Value!.Revision;
        }
        var bookmarkItems = bookmarks.IsSuccess ? bookmarks.Value! : [];
        var presetItems = presets.IsSuccess
            ? presets.Value!.Presets.Select(ToPresentationPreset).ToArray()
            : [];
        var faviconByAddress = _tabVisualStates.Values
            .Where(state => state.Address is not null && state.FaviconPng.Length > 0)
            .GroupBy(state => state.Address!.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (ReadOnlyMemory<byte>)group.Last().FaviconPng,
                StringComparer.OrdinalIgnoreCase);
        var bookmarkFavicons = bookmarkItems
            .Where(bookmark => faviconByAddress.ContainsKey(bookmark.Target.AbsoluteUri))
            .ToDictionary(bookmark => bookmark.Id, bookmark => faviconByAddress[bookmark.Target.AbsoluteUri]);
        _newTabPage.ApplyWorkspaceData(new(
            bookmarkItems,
            presetItems,
            !_privacy.IsPrivate)
        {
            BookmarkFavicons = bookmarkFavicons,
            BookmarkNotes = BookmarkNotes(bookmarkItems),
        });
        await ApplyWorkspaceArtworkSourcesAsync(presetItems);
    }

    private static IReadOnlyDictionary<BookmarkId, string> BookmarkNotes(
        IReadOnlyList<BookmarkEntry> bookmarks) =>
        bookmarks
            .Where(bookmark => !string.IsNullOrWhiteSpace(bookmark.Note))
            .ToDictionary(bookmark => bookmark.Id, bookmark => bookmark.Note!);

    private static BrowserWorkspacePreferences ToPresentationPreferences(
        WorkspaceUiPreferencesSnapshot snapshot) =>
        new(snapshot.TabStripPlacement switch
        {
            FoundationTabStripPlacement.Left => PresentationTabStripPlacement.Left,
            FoundationTabStripPlacement.Right => PresentationTabStripPlacement.Right,
            _ => PresentationTabStripPlacement.Top,
        }, snapshot.CollapseToActive, snapshot.ShowOrbitalGroupPreview)
        {
            NewTabMode = snapshot.NewTabMode == WorkspaceNewTabMode.Basic
                ? NewTabVisualMode.Basic : NewTabVisualMode.Stellar,
            WorkspacePreviewReveal = snapshot.PreviewMode == WorkspacePreviewMode.Click
                ? WorkspacePreviewRevealMode.Click : WorkspacePreviewRevealMode.Hover,
            AffiliatedRailPlacement = snapshot.AffiliatedRailPlacement == WorkspaceAffiliatedRailPlacement.Right
                ? AffiliatedRailPlacement.Right : AffiliatedRailPlacement.Left,
            ShowAffiliatedRail = snapshot.ShowAffiliatedRail,
            SideTabPanelWidth = snapshot.SideTabPanelWidth,
        };

    private static WorkspaceNewTabMode ToFoundationNewTabMode(NewTabVisualMode value) =>
        value == NewTabVisualMode.Basic ? WorkspaceNewTabMode.Basic : WorkspaceNewTabMode.Stellar;

    private static WorkspacePreviewMode ToFoundationPreviewMode(WorkspacePreviewRevealMode value) =>
        value == WorkspacePreviewRevealMode.Click ? WorkspacePreviewMode.Click : WorkspacePreviewMode.Hover;

    private static WorkspaceAffiliatedRailPlacement ToFoundationRailPlacement(AffiliatedRailPlacement value) =>
        value == AffiliatedRailPlacement.Right
            ? WorkspaceAffiliatedRailPlacement.Right
            : WorkspaceAffiliatedRailPlacement.Left;

    private static FoundationTabStripPlacement ToFoundationPlacement(
        PresentationTabStripPlacement placement) => placement switch
        {
            PresentationTabStripPlacement.Left => FoundationTabStripPlacement.Left,
            PresentationTabStripPlacement.Right => FoundationTabStripPlacement.Right,
            _ => FoundationTabStripPlacement.Top,
        };

    private static WorkspacePresetPresentation ToPresentationPreset(WorkspacePreset preset) =>
        new(
            new WorkspacePresetPresentationId(preset.Id.ProfileId, preset.Id.Value),
            preset.Name,
            preset.GroupName,
            preset.Tabs.Select(tab => new WorkspacePresetTabPresentation(
                tab.Target,
                tab.DisplayTitle)
            {
                Note = tab.Note,
                FaviconPng = tab.FaviconPng,
            }).ToArray())
        {
            ColorToken = preset.ColorToken,
            Note = preset.Note,
            Artwork = ToPresentationArtwork(preset.Artwork),
            FirstTabIndex = preset.FirstTabIndex,
        };

    private static WorkspaceArtworkPresentation ToPresentationArtwork(StoredWorkspaceArtwork artwork) =>
        new(artwork.Kind switch
        {
            StoredWorkspaceArtworkKind.Site => WorkspaceArtworkKind.Site,
            StoredWorkspaceArtworkKind.LocalImage => WorkspaceArtworkKind.LocalImage,
            _ => WorkspaceArtworkKind.None,
        }, artwork.SiteAddress, artwork.LocalAssetId, artwork.AccessibleDescription);

    private static StoredWorkspaceArtwork ToStoredArtwork(WorkspaceArtworkPresentation artwork) =>
        new(artwork.Kind switch
        {
            WorkspaceArtworkKind.Site => StoredWorkspaceArtworkKind.Site,
            WorkspaceArtworkKind.LocalImage => StoredWorkspaceArtworkKind.LocalImage,
            _ => StoredWorkspaceArtworkKind.None,
        }, artwork.SiteAddress, artwork.LocalAssetId, artwork.AccessibleDescription);

    private async Task ApplyWorkspaceArtworkSourcesAsync(
        IReadOnlyList<WorkspacePresetPresentation> presets)
    {
        var sources = new Dictionary<WorkspacePresetPresentationId, ImageSource>();
        foreach (var preset in presets)
        {
            ReadOnlyMemory<byte> png = ReadOnlyMemory<byte>.Empty;
            if (preset.Artwork.Kind == WorkspaceArtworkKind.LocalImage &&
                preset.Artwork.LocalAssetId is { } assetId)
            {
                var loaded = await _workspaceArtwork.LoadAsync(
                    _privacy,
                    assetId,
                    _windowLifetime.Token);
                if (loaded.IsSuccess) png = loaded.Value!.PngBytes;
            }
            else if (preset.Artwork.Kind == WorkspaceArtworkKind.Site &&
                preset.Artwork.SiteAddress is { } site)
            {
                png = preset.Tabs.FirstOrDefault(tab => tab.Target == site)?.FaviconPng ??
                    ReadOnlyMemory<byte>.Empty;
            }

            if (TryDecodeFrozenImage(png, out var source))
            {
                sources[preset.Id] = source;
            }
        }
        _newTabPage.ApplyWorkspaceArtworkSources(sources);
    }

    private WorkspaceArtworkPresentation? ImportWorkspaceArtwork(
        WorkspaceLocalArtworkImportRequest request)
    {
        if (_privacy.IsPrivate || request.GroupId.IsEmpty)
        {
            return null;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Choose local workspace artwork",
            Filter = "Image files (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return null;
        }

        try
        {
            var info = new FileInfo(dialog.FileName);
            if (!info.Exists || info.Length is <= 0 or > WorkspaceArtworkStore.MaximumPngBytes)
            {
                return null;
            }
            using var input = File.OpenRead(info.FullName);
            var decoder = BitmapDecoder.Create(
                input,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.FirstOrDefault();
            if (frame is null || frame.PixelWidth is < 1 or > 4096 || frame.PixelHeight is < 1 or > 4096)
            {
                return null;
            }
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(frame));
            using var output = new MemoryStream();
            encoder.Save(output);
            if (output.Length > WorkspaceArtworkStore.MaximumPngBytes)
            {
                return null;
            }
            var saved = _workspaceArtwork.ImportAsync(_privacy, output.ToArray(), _windowLifetime.Token)
                .AsTask().GetAwaiter().GetResult();
            return saved.IsSuccess
                ? new WorkspaceArtworkPresentation(
                    WorkspaceArtworkKind.LocalImage,
                    null,
                    saved.Value!.AssetId,
                    "Local artwork for this workspace")
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryDecodeFrozenImage(ReadOnlyMemory<byte> png, out ImageSource source)
    {
        source = null!;
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (png.Length is < 8 or > WorkspaceArtworkStore.MaximumPngBytes ||
            !png.Span[..8].SequenceEqual(signature))
        {
            return false;
        }
        try
        {
            using var stream = new MemoryStream(png.ToArray(), writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 512;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            if (bitmap.CanFreeze) bitmap.Freeze();
            source = bitmap;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task LoadOfflineReadingAsync()
    {
        if (_privacy.IsPrivate)
        {
            _offlineStatus = "Offline reading is unavailable in private browsing.";
            ApplyOfflineReading();
            return;
        }

        var loaded = await _offlineReading.LoadCatalogAsync(_privacy, _windowLifetime.Token);
        if (loaded.IsSuccess)
        {
            _offlineCatalog = loaded.Value!;
            _offlineStatus = "Saved pages stay on this device and are not live websites.";
        }
        else
        {
            _offlineStatus = "Offline reading storage is unavailable. No page was saved.";
        }
        ApplyOfflineReading();
    }

    private void ApplyOfflineReading()
    {
        if (_privacy.IsPrivate)
        {
            var unavailable = OfflineReadingCatalogPresentation.Unavailable(
                true,
                "Offline reading is unavailable in private browsing.");
            _chrome.ApplyOfflineReading(unavailable);
            _offlineLibraryControl?.Apply(unavailable);
            return;
        }

        var selected = SelectedTab();
        var address = IsNormalWebAddress(selected?.Address) ? selected!.Address : null;
        var presentation = new OfflineReadingCatalogPresentation(
            _offlineCatalog.Revision,
            false,
            address is not null && !_offlineBusy && SelectedHost() is not null,
            selected?.Title ?? string.Empty,
            address,
            _offlineBusy,
            _offlineStatus,
            _offlineCatalog.Items.Select(item => new OfflineReadingItemPresentation(
                new OrbitNavigator.Presentation.Offline.OfflineReadingItemId(item.ItemId.Value),
                item.Title,
                item.SourceAddress,
                item.SavedAtUtc,
                item.SizeBytes)).ToArray()).Validate();
        _chrome.ApplyOfflineReading(presentation);
        _offlineLibraryControl?.Apply(presentation);
    }

    private async void OnOfflineReadingActionRequested(
        object? sender,
        OfflineReadingActionRequestedEventArgs args)
    {
        try
        {
            if (_privacy.IsPrivate)
            {
                ApplyOfflineReading();
                return;
            }
            if (args.Action.ExpectedRevision != _offlineCatalog.Revision)
            {
                await LoadOfflineReadingAsync();
                return;
            }

            switch (args.Action)
            {
                case SavePageForOfflineAction:
                    await SaveSelectedPageForOfflineAsync();
                    break;
                case OpenOfflineLibraryAction:
                    ShowOfflineLibrary();
                    break;
                case OpenOfflineReadingItemAction open:
                    await OpenOfflineReadingItemAsync(open.ItemId);
                    break;
                case DeleteOfflineReadingItemAction delete:
                    await DeleteOfflineReadingItemAsync(delete.ItemId);
                    break;
            }
        }
        catch (OperationCanceledException) when (_windowLifetime.IsCancellationRequested)
        {
        }
    }

    private async Task SaveSelectedPageForOfflineAsync()
    {
        var host = SelectedHost();
        var selected = SelectedTab();
        if (host is null || selected is null || !IsNormalWebAddress(selected.Address))
        {
            _offlineStatus = "Open a normal website before saving an offline copy.";
            ApplyOfflineReading();
            return;
        }

        _offlineBusy = true;
        _offlineStatus = "Capturing a local viewport snapshot...";
        ApplyOfflineReading();
        try
        {
            var capture = await host.CaptureOfflineSnapshotAsync(_windowLifetime.Token);
            if (!capture.IsSuccess)
            {
                _offlineStatus = "Orbit could not capture this page. Nothing was saved.";
                return;
            }
            var saved = await _offlineReading.SaveAsync(new(
                _privacy,
                _offlineCatalog.Revision,
                capture.Value!.Title,
                capture.Value.SourceAddress,
                capture.Value.PngBytes), _windowLifetime.Token);
            if (saved.IsSuccess)
            {
                _offlineCatalog = saved.Value!;
                _offlineStatus = "Offline viewport snapshot saved locally. It is not live and does not sync.";
            }
            else
            {
                _offlineStatus = saved.Error?.Code == ControllerErrorCode.Conflict
                    ? "The offline library changed. Refresh and try saving again."
                    : "The offline snapshot could not be saved.";
            }
        }
        finally
        {
            _offlineBusy = false;
            ApplyOfflineReading();
        }
    }

    private void ShowOfflineLibrary()
    {
        if (_offlineLibraryWindow is { IsVisible: true })
        {
            _offlineLibraryWindow.Activate();
            return;
        }

        _offlineLibraryControl = _chrome.CreateOfflineLibraryControl();
        ApplyOfflineReading();
        var window = new Window
        {
            Owner = this,
            Title = "Offline library — Orbit Navigator",
            Icon = OrbitProgramIdentity.CreateWindowIcon(),
            Width = 780,
            Height = 620,
            MinWidth = 560,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = _offlineLibraryControl,
        };
        window.Closed += (_, _) =>
        {
            _offlineLibraryWindow = null;
            _offlineLibraryControl = null;
        };
        _offlineLibraryWindow = window;
        window.Show();
    }

    private async Task OpenOfflineReadingItemAsync(
        OrbitNavigator.Presentation.Offline.OfflineReadingItemId itemId)
    {
        var opened = await _offlineReading.OpenAsync(
            _privacy,
            new OrbitNavigator.Foundation.Offline.OfflineReadingItemId(itemId.Value),
            _windowLifetime.Token);
        if (!opened.IsSuccess)
        {
            _offlineStatus = "That offline copy is unavailable or damaged.";
            ApplyOfflineReading();
            return;
        }

        new OfflineSnapshotWindow(this, opened.Value!).Show();
    }

    private async Task DeleteOfflineReadingItemAsync(
        OrbitNavigator.Presentation.Offline.OfflineReadingItemId itemId)
    {
        var deleted = await _offlineReading.DeleteAsync(new(
            _privacy,
            _offlineCatalog.Revision,
            new OrbitNavigator.Foundation.Offline.OfflineReadingItemId(itemId.Value)),
            _windowLifetime.Token);
        if (deleted.IsSuccess)
        {
            _offlineCatalog = deleted.Value!;
            _offlineStatus = "Offline copy deleted from this device.";
        }
        else
        {
            _offlineStatus = deleted.Error?.Code == ControllerErrorCode.Conflict
                ? "The offline library changed. Refresh and try deleting again."
                : "The offline copy could not be deleted.";
        }
        ApplyOfflineReading();
    }

    private async Task LoadQuickViewBookmarksAsync()
    {
        _quickViewBookmarks.Clear();
        if (_privacy.IsPrivate)
        {
            RenderQuickView();
            return;
        }

        var result = await _bookmarks.QueryAsync(
            new BookmarkQuery(_privacy, null, 8),
            _windowLifetime.Token);
        if (result.IsSuccess)
        {
            foreach (var bookmark in result.Value!)
            {
                _quickViewBookmarks[bookmark.Id.Value.ToString("N")] = bookmark;
            }
        }
        RenderQuickView();
    }

    private void UpdateQuickViewAvailability()
    {
        var selectedAddress = SelectedTab()?.Address;
        if (!IsNormalWebAddress(selectedAddress) &&
            _quickViewState.Snapshot.HostState is QuickViewHostState.Opening or
                QuickViewHostState.Open or QuickViewHostState.Failed)
        {
            _ = CloseQuickViewForUnavailableSelectionAsync();
            return;
        }
        _quickViewState.SetSelectedSite(selectedAddress);
        RenderQuickView();
    }

    private async Task CloseQuickViewForUnavailableSelectionAsync()
    {
        try
        {
            await _quickViewGate.WaitAsync(_windowLifetime.Token);
            try
            {
                var state = _quickViewState.Snapshot;
                if (state.HostState is QuickViewHostState.Opening or QuickViewHostState.Open or
                    QuickViewHostState.Failed)
                {
                    await CloseQuickViewAsync(state.Revision);
                }
            }
            finally
            {
                _quickViewGate.Release();
            }
        }
        catch (OperationCanceledException) when (_windowLifetime.IsCancellationRequested)
        {
        }
    }

    private void RenderQuickView()
    {
        var state = _quickViewState.Snapshot;
        var presentation = new QuickViewPresentation(
            state.Revision,
            _privacy.IsPrivate,
            !_privacy.IsPrivate && IsNormalWebAddress(SelectedTab()?.Address),
            state.HostState,
            state.Title,
            state.Address,
            QuickViewStateTransferCapability.AddressReloadOnly,
            state.SafeStatusMessage,
            _quickViewBookmarks.Select(pair => new QuickViewBookmarkPresentation(
                pair.Key,
                pair.Value.Title,
                pair.Value.Target)).ToArray()).Validate();
        _chrome.ApplyQuickView(presentation);
    }

    private async void OnQuickViewActionRequested(
        object? sender,
        QuickViewActionRequestedEventArgs args)
    {
        try
        {
            await _quickViewGate.WaitAsync(_windowLifetime.Token);
            try
            {
                switch (args.Action)
                {
                    case OpenQuickViewAction open:
                        await OpenOrNavigateQuickViewAsync(
                            open.ExpectedRevision,
                            QuickViewTargetResolver.Resolve(
                                open.Query,
                                SelectedTab()?.Address),
                            createHost: true);
                        break;
                    case NavigateQuickViewAction navigate:
                        await OpenOrNavigateQuickViewAsync(
                            navigate.ExpectedRevision,
                            QuickViewTargetResolver.Resolve(
                                navigate.Query,
                                SelectedTab()?.Address,
                                _quickViewState.Snapshot.Address),
                            createHost: false);
                        break;
                    case OpenQuickViewBookmarkAction bookmark:
                        if (_quickViewBookmarks.TryGetValue(bookmark.BookmarkId, out var entry))
                        {
                            await OpenOrNavigateQuickViewAsync(
                                bookmark.ExpectedRevision,
                                entry.Target,
                                createHost: _quickViewHost is null);
                        }
                        break;
                    case ExpandQuickViewToTabAction expand:
                        await ExpandQuickViewAsync(expand.ExpectedRevision);
                        break;
                    case CloseQuickViewAction close:
                        await CloseQuickViewAsync(close.ExpectedRevision);
                        break;
                    case ResizeQuickViewAction resize:
                        if (_quickViewState.AcceptEphemeralAction(resize.ExpectedRevision).IsSuccess)
                        {
                            RenderQuickView();
                        }
                        break;
                }
            }
            finally
            {
                _quickViewGate.Release();
            }
        }
        catch (ArgumentException)
        {
            _quickViewState.MarkFailed("Enter a web address or search term for Quick View.");
            RenderQuickView();
        }
        catch (OperationCanceledException) when (_windowLifetime.IsCancellationRequested)
        {
        }
    }

    private async Task OpenOrNavigateQuickViewAsync(
        long expectedRevision,
        Uri target,
        bool createHost)
    {
        var begin = createHost
            ? _quickViewState.BeginOpen(expectedRevision, target)
            : _quickViewState.BeginNavigate(expectedRevision, target);
        if (!begin.IsSuccess)
        {
            RenderQuickView();
            return;
        }
        RenderQuickView();

        if (createHost)
        {
            if (_quickViewPrepared is not null || _quickViewHost is not null)
            {
                _quickViewState.MarkFailed("Quick View is already open.");
                RenderQuickView();
                return;
            }
            var context = new BrowsingContext(
                _privacy,
                _windowId,
                new BrowserTabId(Guid.NewGuid()),
                SiteIdentity.TryCreate(target, out var site) ? site : null);
            _quickViewPrepared = await _prepareHost(context);
            if (_quickViewPrepared is null)
            {
                _quickViewState.MarkFailed("Quick View could not prepare its temporary browser surface.");
                RenderQuickView();
                return;
            }
            _quickViewHost = _quickViewPrepared.Host;
            _quickViewHost.TabVisualStateChanged += OnQuickViewHostVisualStateChanged;
            StageQuickViewHostForInitialization(_quickViewHost);
            await Dispatcher.Yield(DispatcherPriority.Loaded);
            var initialized = await _quickViewPrepared.InitializeAttachedAsync(
                TimeSpan.FromSeconds(20),
                _windowLifetime.Token);
            if (!initialized.IsSuccess)
            {
                await FailAndDisposeQuickViewAsync("Quick View could not start its temporary browser surface.");
                return;
            }
            UnstageQuickViewHostAfterInitialization(_quickViewHost);
            _chrome.ApplyQuickViewWebContent(_quickViewHost);
        }

        target = CanonicalWebAddress.Normalize(target);
        if (_quickViewHost is null || !(await _quickViewHost.NavigateAsync(target, _windowLifetime.Token)).IsSuccess)
        {
            await FailAndDisposeQuickViewAsync("Quick View could not open that address.");
            return;
        }
        _quickViewState.CompleteOpen(target, _quickViewHost.CurrentDocumentTitle);
        RenderQuickView();
    }

    private async Task ExpandQuickViewAsync(long expectedRevision)
    {
        var accepted = _quickViewState.AcceptEphemeralAction(expectedRevision);
        if (!accepted.IsSuccess || accepted.Value!.Address is not { } target)
        {
            RenderQuickView();
            return;
        }
        await CreateTabAsync(new CreateTabBrowserCommand(
            _windowId,
            new BrowserTabId(Guid.NewGuid()),
            target,
            null));
        await CloseQuickViewAsync(_quickViewState.Snapshot.Revision);
    }

    private async Task CloseQuickViewAsync(long expectedRevision)
    {
        var closing = _quickViewState.BeginClose(expectedRevision);
        if (!closing.IsSuccess)
        {
            RenderQuickView();
            return;
        }
        RenderQuickView();
        _chrome.ApplyQuickViewWebContent(null);
        if (_quickViewHost is not null)
        {
            _webSurface.Children.Remove(_quickViewHost);
            _quickViewHost.TabVisualStateChanged -= OnQuickViewHostVisualStateChanged;
            _quickViewHost = null;
        }
        if (_quickViewPrepared is not null)
        {
            await _quickViewPrepared.DisposeAsync();
            _quickViewPrepared = null;
        }
        _chrome.ResetQuickViewForFreshUse();
        _quickViewState.CompleteClose(SelectedTab()?.Address);
        RenderQuickView();
    }

    private async Task FailAndDisposeQuickViewAsync(string safeMessage)
    {
        _chrome.ApplyQuickViewWebContent(null);
        if (_quickViewHost is not null)
        {
            _webSurface.Children.Remove(_quickViewHost);
            _quickViewHost.TabVisualStateChanged -= OnQuickViewHostVisualStateChanged;
            _quickViewHost = null;
        }
        if (_quickViewPrepared is not null)
        {
            await _quickViewPrepared.DisposeAsync();
            _quickViewPrepared = null;
        }
        _chrome.ResetQuickViewForFreshUse();
        _quickViewState.MarkFailed(safeMessage);
        RenderQuickView();
    }

    private void StageQuickViewHostForInitialization(WebView2HostControl host)
    {
        host.Visibility = Visibility.Hidden;
        host.IsHitTestVisible = false;
        host.Width = 1;
        host.Height = 1;
        host.HorizontalAlignment = HorizontalAlignment.Left;
        host.VerticalAlignment = VerticalAlignment.Bottom;
        Panel.SetZIndex(host, -1);
        _webSurface.Children.Add(host);
    }

    private void UnstageQuickViewHostAfterInitialization(WebView2HostControl host)
    {
        _webSurface.Children.Remove(host);
        host.ClearValue(WidthProperty);
        host.ClearValue(HeightProperty);
        host.ClearValue(HorizontalAlignmentProperty);
        host.ClearValue(VerticalAlignmentProperty);
        host.ClearValue(Panel.ZIndexProperty);
        host.IsHitTestVisible = true;
        host.Visibility = Visibility.Visible;
    }

    private void OnQuickViewHostVisualStateChanged(
        object? sender,
        WebViewTabVisualStateChangedEventArgs args)
    {
        if (!ReferenceEquals(sender, _quickViewHost) ||
            args.State.Address is not { } address)
        {
            return;
        }
        _quickViewState.UpdateOpenDocument(address, args.State.PageTitle);
        RenderQuickView();
    }

    private BrowserTabState? SelectedTab() => _browserState.SelectedTabId is { } selectedId
        ? _browserState.Tabs.FirstOrDefault(tab => tab.TabId == selectedId)
        : null;

    private WebView2HostControl? SelectedHost() => _browserState.SelectedTabId is { } selectedId &&
        _hosts.TryGetValue(selectedId, out var host)
            ? host
            : null;

    private static bool IsNormalWebAddress(Uri? address) =>
        address is { IsAbsoluteUri: true } &&
        address.Scheme is "http" or "https" &&
        string.IsNullOrEmpty(address.UserInfo) &&
        !string.IsNullOrWhiteSpace(address.IdnHost);

    private async void OnProtectionReloadRequested(
        object? sender,
        ProtectionReloadRequestedEventArgs args)
    {
        if (_hosts.TryGetValue(args.Context.TabId, out var host))
        {
            await host.ReloadAsync();
        }
    }

    private async Task LoadProtectionAsync(WebView2HostControl host)
    {
        if (host.Context.CurrentSite is { } site)
        {
            await _siteProtection.LoadAsync(host.Context, site);
        }
    }

    private async Task UpdateTabAsync(
        BrowserTabId id,
        Func<BrowserTabState, BrowserTabState> update)
    {
        var current = _workspaceCoordinator.Current.Browser.Tabs.FirstOrDefault(tab => tab.TabId == id);
        if (current is null) return;
        var result = await _workspaceCoordinator.ExecuteAsync(new UpdateWorkspaceTabAction(
            _windowId,
            _workspaceCoordinator.Current.Revision,
            update(current)));
        if (result.IsSuccess) ApplyWorkspaceSnapshot(result.Value!.Snapshot);
    }

    private void ShowSelectedHost(bool keepSelectedNewTabHostVisible = false)
    {
        var selectedTab = _browserState.SelectedTabId is { } selectedId
            ? _browserState.Tabs.FirstOrDefault(tab => tab.TabId == selectedId)
            : null;
        foreach (var pair in _hosts)
        {
            var isSelected = pair.Key == _browserState.SelectedTabId;
            var selectedIsNewTab = selectedTab is not null && selectedTab.Address is null;
            pair.Value.Visibility = isSelected && (!selectedIsNewTab || keepSelectedNewTabHostVisible)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        _newTabPage.Visibility = selectedTab is not null && selectedTab.Address is null
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (_browserState.SelectedTabId is { } tabId && _hosts.TryGetValue(tabId, out var host))
            _chrome.SetBrowsingContext(host.Context);
        ApplyOfflineReading();
        UpdateQuickViewAvailability();
    }

    private void ApplyWorkspaceSnapshot(BrowserWorkspaceSnapshot snapshot)
    {
        _browserState = snapshot.Browser;
        SynchronizeGroupCatalog(snapshot.Groups);
        RenderBrowserState();
        ApplyOfflineReading();
        UpdateQuickViewAvailability();
    }

    private void SynchronizeGroupCatalog(IReadOnlyList<WorkspaceTabGroupState> groups)
    {
        var incoming = groups.Select(group => group.GroupId).ToHashSet();
        foreach (var existing in _groupPresentations.Keys.Where(id => !incoming.Contains(id)).ToArray())
        {
            _groupPresentations.Remove(existing);
        }
        foreach (var group in groups)
        {
            _groupPresentations[group.GroupId] = new(
                group.GroupId,
                group.Name,
                group.IsCollapsed)
            {
                ColorToken = group.ColorToken,
                IsTemporary = group.IsTemporary,
            };
        }
    }

    private void RenderBrowserState()
    {
        _chrome.RenderTabs(_browserState, _groupPresentations);
        var projection = CreateTabControllerProjection();
        _tabControllerSession.AcceptProjection(projection);
        _workspaceAudit?.Record(new(
            DateTimeOffset.UtcNow,
            _windowId,
            _privacy.IsPrivate,
            _workspaceCoordinator.Current.Revision.Value,
            _browserState.Tabs.Count,
            _groupPresentations.Count,
            _hosts.Count,
            projection.Tabs.Entries.Count,
            "render"));
    }

    private RevisionedTabControllerProjection CreateTabControllerProjection()
    {
        var snapshot = _workspaceCoordinator.Current;
        var tabs = TabStripProjector.Project(
            snapshot.Browser,
            _groupPresentations,
            _chrome.WorkspacePreferences.CollapseToActive);
        return new RevisionedTabControllerProjection(
            _windowId,
            _privacy.IsPrivate,
            snapshot.Revision.Value,
            _privacy.IsPrivate ? TabControllerHostState.Docked : _tabControllerHostState,
            _chrome.WorkspacePreferences.TabStripPlacement,
            tabs)
        {
            PreviewRevealMode = _chrome.WorkspacePreferences.WorkspacePreviewReveal,
        };
    }

    private async Task LoadTabControllerLayoutAsync()
    {
        var loaded = await _tabControllerLayouts.LoadAsync(_privacy, _windowLifetime.Token);
        if (!loaded.IsSuccess) return;
        _tabControllerLayoutRevision = loaded.Value!.Revision;
        if (!_privacy.IsPrivate && loaded.Value.DockState == TabControllerDockState.Detached)
        {
            await DetachTabControllerAsync(focusSelected: false, loaded.Value.DetachedBounds);
        }
    }

    private async ValueTask<TabControllerCommandResult> ExecuteTabControllerCommandAsync(
        TabControllerCommand command,
        CancellationToken cancellationToken)
    {
        if (!Dispatcher.CheckAccess())
        {
            return await Dispatcher.InvokeAsync(
                () => ExecuteTabControllerCommandAsync(command, cancellationToken).AsTask()).Task.Unwrap();
        }
        if (command.WindowId != _windowId || command.IsPrivate != _privacy.IsPrivate)
        {
            return CommandResult(command, TabControllerCommandOutcome.PolicyDenied,
                "That tab action belongs to another window.");
        }
        if (command.ExpectedRevision != _workspaceCoordinator.Current.Revision.Value)
        {
            return CommandResult(command, TabControllerCommandOutcome.Stale,
                "Tabs changed elsewhere. The current list has been refreshed.");
        }

        switch (command.Action)
        {
            case SelectTabControllerAction select:
                return await ExecuteWorkspaceCommandAsync(
                    command,
                    new SelectWorkspaceTabAction(_windowId, CurrentRevision(), select.TabId),
                    async receipt =>
                    {
                        ApplyWorkspaceSnapshot(receipt.Snapshot);
                        ShowSelectedHost();
                        if (_hosts.TryGetValue(select.TabId, out var host)) await LoadProtectionAsync(host);
                    },
                    "Tab selected.",
                    cancellationToken);

            case SwitchToResourceTabControllerAction switchTo:
                return await ExecuteWorkspaceCommandAsync(
                    command,
                    new SelectWorkspaceTabAction(_windowId, CurrentRevision(), switchTo.TabId),
                    async receipt =>
                    {
                        ApplyWorkspaceSnapshot(receipt.Snapshot);
                        ShowSelectedHost();
                        if (_hosts.TryGetValue(switchTo.TabId, out var host)) await LoadProtectionAsync(host);
                    },
                    "Tab selected.",
                    cancellationToken);

            case CreateNewTabControllerAction createNewTab:
                var newTabId = new BrowserTabId(Guid.NewGuid());
                await CreateTabAsync(new CreateTabBrowserCommand(
                    _windowId,
                    newTabId,
                    InitialTarget: null,
                    createNewTab.GroupId));
                return _hosts.ContainsKey(newTabId)
                    ? CommandResult(command, TabControllerCommandOutcome.Accepted, "New tab opened.")
                    : CommandResult(command, TabControllerCommandOutcome.Failed,
                        "The new tab could not be opened.");

            case CloseTabControllerAction close:
                return await CloseTabsFromControllerAsync(command, [close.TabId], cancellationToken);

            case DuplicateTabControllerAction duplicate:
                var sourceTab = _workspaceCoordinator.Current.Browser.Tabs
                    .FirstOrDefault(tab => tab.TabId == duplicate.TabId);
                if (sourceTab is null)
                {
                    return CommandResult(command, TabControllerCommandOutcome.Failed,
                        "That tab is no longer available to duplicate.");
                }
                var duplicateTabId = new BrowserTabId(Guid.NewGuid());
                await CreateTabAsync(new CreateTabBrowserCommand(
                    _windowId,
                    duplicateTabId,
                    sourceTab.Address,
                    sourceTab.GroupId));
                return _hosts.ContainsKey(duplicateTabId)
                    ? CommandResult(command, TabControllerCommandOutcome.Accepted, "Tab duplicated.")
                    : CommandResult(command, TabControllerCommandOutcome.Failed,
                        "The tab could not be duplicated.");

            case SetTabMutedControllerAction mute:
                if (!_hosts.TryGetValue(mute.TabId, out var audioHost))
                {
                    return CommandResult(command, TabControllerCommandOutcome.Failed,
                        "That tab's audio is no longer available.");
                }
                var muted = await audioHost.SetMutedAsync(mute.IsMuted, cancellationToken);
                return muted.IsSuccess
                    ? CommandResult(
                        command,
                        TabControllerCommandOutcome.Accepted,
                        mute.IsMuted ? "Tab muted." : "Tab unmuted.")
                    : CommandResult(command, TabControllerCommandOutcome.Failed,
                        "The tab audio setting could not be changed.");

            case SetTabSiteContentControlControllerAction:
                return CommandResult(command, TabControllerCommandOutcome.PolicyDenied,
                    "Site content controls are unavailable until Orbit can enforce them safely.");

            case BulkCloseTabsControllerAction bulk:
                return await CloseTabsFromControllerAsync(command, bulk.TabIds, cancellationToken);

            case MoveTabControllerAction move:
                return await ExecuteWorkspaceCommandAsync(
                    command,
                    new MoveWorkspaceTabAction(_windowId, CurrentRevision(), move.TabId, move.NewIndex, move.GroupId),
                    receipt =>
                    {
                        ApplyWorkspaceSnapshot(receipt.Snapshot);
                        return Task.CompletedTask;
                    },
                    "Tab moved.",
                    cancellationToken);

            case CreateTabGroupControllerAction create:
                return await ExecuteWorkspaceCommandAsync(
                    command,
                    new CreateWorkspaceGroupAction(
                        _windowId,
                        CurrentRevision(),
                        new BrowserTabGroupId(Guid.NewGuid()),
                        create.Name,
                        create.TabIds),
                    receipt =>
                    {
                        ApplyWorkspaceSnapshot(receipt.Snapshot);
                        return Task.CompletedTask;
                    },
                    "Tab group created.",
                    cancellationToken);

            case RenameTabGroupControllerAction rename:
                return await ExecuteWorkspaceCommandAsync(
                    command,
                    new RenameWorkspaceGroupAction(
                        _windowId, CurrentRevision(), rename.GroupId, rename.RequestedName),
                    receipt =>
                    {
                        ApplyWorkspaceSnapshot(receipt.Snapshot);
                        return Task.CompletedTask;
                    },
                    "Tab group renamed.",
                    cancellationToken);

            case SetTabGroupColorControllerAction color:
                return await ExecuteWorkspaceCommandAsync(
                    command,
                    new SetWorkspaceGroupColorAction(
                        _windowId, CurrentRevision(), color.GroupId, color.ColorToken),
                    receipt =>
                    {
                        ApplyWorkspaceSnapshot(receipt.Snapshot);
                        return Task.CompletedTask;
                    },
                    "Tab group color changed.",
                    cancellationToken);

            case CloseTabGroupControllerAction closeGroup:
                var canonicalGroupTabs = _workspaceCoordinator.Current.Browser.Tabs
                    .Where(tab => tab.GroupId == closeGroup.GroupId)
                    .Select(tab => tab.TabId)
                    .ToArray();
                if (canonicalGroupTabs.Length == 0 ||
                    !canonicalGroupTabs.SequenceEqual(closeGroup.TabIds))
                {
                    return CommandResult(command, TabControllerCommandOutcome.Stale,
                        "The tab group changed. The current list has been refreshed.");
                }
                return await CloseTabsFromControllerAsync(command, canonicalGroupTabs, cancellationToken);

            case SaveTabGroupAsWorkspaceControllerAction saveGroup:
                if (_privacy.IsPrivate)
                {
                    return CommandResult(command, TabControllerCommandOutcome.PolicyDenied,
                        "Private tab groups cannot be saved as workspaces.");
                }
                if (!WorkspaceGroupSavePolicy.TryValidate(
                        _workspaceCoordinator.Current,
                        saveGroup.GroupId,
                        saveGroup.Draft,
                        out var draft))
                {
                    return CommandResult(command, TabControllerCommandOutcome.Stale,
                        "The live tab group changed. Review it before saving a workspace.");
                }
                var savedPreset = await _workspacePresets.UpsertAsync(new(
                    _privacy,
                    _workspacePresetRevision,
                    null,
                    draft.Name,
                    draft.GroupName,
                    draft.Tabs.Select(tab => new WorkspacePresetTab(tab.Target, tab.DisplayTitle)
                    {
                        Note = tab.Note,
                        FaviconPng = tab.FaviconPng.ToArray(),
                    }).ToArray())
                {
                    ColorToken = draft.ColorToken,
                    Note = draft.Note,
                    Artwork = ToStoredArtwork(draft.Artwork),
                    FirstTabIndex = draft.FirstTabIndex,
                }, cancellationToken);
                if (!savedPreset.IsSuccess)
                {
                    await RefreshWorkspaceDataAsync();
                    return CommandResult(command,
                        savedPreset.Error?.Code == ControllerErrorCode.Conflict
                            ? TabControllerCommandOutcome.Stale
                            : TabControllerCommandOutcome.Failed,
                        savedPreset.Error?.Code == ControllerErrorCode.Conflict
                            ? "Workspaces changed elsewhere. The current list has been refreshed."
                            : "The tab group could not be saved as a workspace.");
                }
                _workspacePresetRevision = savedPreset.Value!.Revision;
                var marked = await _workspaceCoordinator.ExecuteAsync(new MarkWorkspaceGroupSavedAction(
                    _windowId, CurrentRevision(), saveGroup.GroupId), cancellationToken);
                if (marked.IsSuccess) ApplyWorkspaceSnapshot(marked.Value!.Snapshot);
                await RefreshWorkspaceDataAsync();
                return CommandResult(command, TabControllerCommandOutcome.Accepted,
                    "Tab group saved as a workspace.");

            case ToggleTabGroupControllerAction toggle:
                return await ExecuteWorkspaceCommandAsync(
                    command,
                    new ToggleWorkspaceGroupAction(
                        _windowId, CurrentRevision(), toggle.GroupId, toggle.IsCollapsed),
                    receipt =>
                    {
                        ApplyWorkspaceSnapshot(receipt.Snapshot);
                        return Task.CompletedTask;
                    },
                    toggle.IsCollapsed ? "Tab group collapsed." : "Tab group expanded.",
                    cancellationToken);

            case UngroupTabGroupControllerAction ungroup:
                return await ExecuteWorkspaceCommandAsync(
                    command,
                    new UngroupWorkspaceTabsAction(_windowId, CurrentRevision(), ungroup.GroupId),
                    receipt =>
                    {
                        ApplyWorkspaceSnapshot(receipt.Snapshot);
                        return Task.CompletedTask;
                    },
                    "Tabs ungrouped.",
                    cancellationToken);

            case DetachTabControllerAction:
                if (_privacy.IsPrivate)
                {
                    return CommandResult(command, TabControllerCommandOutcome.PolicyDenied,
                        "Private tab controllers remain docked in this window.");
                }
                await DetachTabControllerAsync(focusSelected: true, bounds: null);
                return CommandResult(command, TabControllerCommandOutcome.Accepted,
                    "Tab controller opened in a separate window.");

            case DockTabControllerAction:
                await DockTabControllerAsync();
                return CommandResult(command, TabControllerCommandOutcome.Accepted,
                    "Tab controller docked in the browser window.");

            case SetTabPlacementControllerAction placement:
                return await SetTabPlacementAsync(command, placement.Placement);

            default:
                return CommandResult(command, TabControllerCommandOutcome.Failed,
                    "The tab action is not supported.");
        }
    }

    private async ValueTask<TabControllerCommandResult> CloseTabsFromControllerAsync(
        TabControllerCommand command,
        IReadOnlyList<BrowserTabId> tabIds,
        CancellationToken cancellationToken)
    {
        return await ExecuteWorkspaceCommandAsync(
            command,
            new CloseWorkspaceTabsAction(_windowId, CurrentRevision(), tabIds),
            async receipt =>
            {
                foreach (var tabId in receipt.ClosedTabIds)
                {
                    if (!_hosts.Remove(tabId, out var host)) continue;
                    _tabVisualStates.Remove(tabId);
                    DetachHost(host);
                    _webSurface.Children.Remove(host);
                    await host.DisposeAsync();
                }
                ApplyWorkspaceSnapshot(receipt.Snapshot);
                ShowSelectedHost();
            },
            tabIds.Count > 1 ? "Background tabs closed." : "Tab closed.",
            cancellationToken);
    }

    private async ValueTask<TabControllerCommandResult> ExecuteWorkspaceCommandAsync(
        TabControllerCommand source,
        BrowserWorkspaceAction action,
        Func<BrowserWorkspaceCommandReceipt, Task> accepted,
        string message,
        CancellationToken cancellationToken)
    {
        var result = await _workspaceCoordinator.ExecuteAsync(action, cancellationToken);
        if (result.IsSuccess)
        {
            await accepted(result.Value!);
            return CommandResult(source, TabControllerCommandOutcome.Accepted, message);
        }

        var outcome = result.Error?.Code switch
        {
            ControllerErrorCode.Conflict => TabControllerCommandOutcome.Stale,
            ControllerErrorCode.PolicyDenied => TabControllerCommandOutcome.PolicyDenied,
            _ => TabControllerCommandOutcome.Failed,
        };
        var safeMessage = outcome switch
        {
            TabControllerCommandOutcome.Stale => "Tabs changed elsewhere. The current list has been refreshed.",
            TabControllerCommandOutcome.PolicyDenied => "That tab action is unavailable in this window.",
            _ => "The tab action could not be completed.",
        };
        return CommandResult(source, outcome, safeMessage);
    }

    private BrowserWorkspaceRevision CurrentRevision() => _workspaceCoordinator.Current.Revision;

    private TabControllerCommandResult CommandResult(
        TabControllerCommand command,
        TabControllerCommandOutcome outcome,
        string safeMessage) =>
        new(command.Action.StableActionId, outcome, safeMessage, CreateTabControllerProjection());

    private async ValueTask<TabControllerCommandResult> SetTabPlacementAsync(
        TabControllerCommand command,
        PresentationTabStripPlacement placement)
    {
        var preferences = _chrome.WorkspacePreferences with { TabStripPlacement = placement };
        if (_privacy.IsPrivate)
        {
            _chrome.ApplyWorkspacePreferences(preferences);
            RenderBrowserState();
            return CommandResult(command, TabControllerCommandOutcome.Accepted, "Tab placement changed for this private window.");
        }

        var save = await _workspacePreferences.SaveAsync(new(
            _privacy,
            _workspacePreferencesRevision,
            ToFoundationPlacement(placement),
            preferences.CollapseToActive,
            preferences.ShowOrbitalGroupPreview,
            _chrome.IsCompactTabMode,
            ToFoundationNewTabMode(preferences.NewTabMode),
            ToFoundationPreviewMode(preferences.WorkspacePreviewReveal),
            ToFoundationRailPlacement(preferences.AffiliatedRailPlacement),
            preferences.ShowAffiliatedRail,
            preferences.SideTabPanelWidth));
        if (!save.IsSuccess)
        {
            await LoadWorkspacePreferencesAsync();
            RenderBrowserState();
            return CommandResult(
                command,
                save.Error?.Code == ControllerErrorCode.Conflict
                    ? TabControllerCommandOutcome.Stale
                    : TabControllerCommandOutcome.Failed,
                save.Error?.Code == ControllerErrorCode.Conflict
                    ? "Tab placement changed elsewhere and was refreshed."
                    : "Tab placement could not be saved.");
        }

        ApplyWorkspacePreferences(save.Value!);
        RenderBrowserState();
        return CommandResult(command, TabControllerCommandOutcome.Accepted, "Tab placement saved.");
    }

    private async Task DetachTabControllerAsync(
        bool focusSelected,
        TabControllerBoundsDip? bounds)
    {
        if (_privacy.IsPrivate)
        {
            return;
        }

        _tabControllerOriginFocus ??= Keyboard.FocusedElement;
        if (_tabControllerWindow is not null)
        {
            if (!_tabControllerWindow.IsVisible)
            {
                _tabControllerWindow.Show();
            }
            _tabControllerWindow.Activate();
            if (focusSelected)
            {
                _tabControllerWindow.FocusSelectedTab();
            }
            return;
        }

        _tabControllerHostState = TabControllerHostState.Opening;
        RenderBrowserState();
        var detachedTabs = _chrome.CreateDetachedTabControllerControl();
        var safeBounds = ClampToVirtualScreen(bounds);
        var tool = new FoundationTabControllerWindow(this, detachedTabs, safeBounds);
        tool.DockRequested += OnTabControllerDockRequested;
        tool.Closed += OnTabControllerWindowClosed;
        _tabControllerWindow = tool;
        tool.Show();
        _tabControllerHostState = TabControllerHostState.Detached;
        RenderBrowserState();
        await SaveTabControllerLayoutAsync(TabControllerDockState.Detached, tool.CurrentBounds);
        if (focusSelected)
        {
            tool.Activate();
            tool.FocusSelectedTab();
        }
    }

    private async Task DockTabControllerAsync()
    {
        if (_tabControllerWindow is null)
        {
            _tabControllerHostState = TabControllerHostState.Docked;
            RenderBrowserState();
            return;
        }

        _tabControllerHostState = TabControllerHostState.Closing;
        RenderBrowserState();
        var tool = _tabControllerWindow;
        var bounds = tool.CurrentBounds;
        tool.DockRequested -= OnTabControllerDockRequested;
        tool.Closed -= OnTabControllerWindowClosed;
        _tabControllerWindow = null;
        tool.CloseForOwner();
        _tabControllerHostState = TabControllerHostState.Docked;
        RenderBrowserState();
        await SaveTabControllerLayoutAsync(TabControllerDockState.Docked, bounds);
        RestoreControllerOriginFocus();
    }

    private async Task SaveTabControllerLayoutAsync(
        TabControllerDockState dockState,
        TabControllerBoundsDip? bounds)
    {
        if (_privacy.IsPrivate)
        {
            return;
        }

        var saved = await _tabControllerLayouts.SaveAsync(new(
            _privacy,
            _tabControllerLayoutRevision,
            dockState,
            bounds), _windowLifetime.Token);
        if (saved.IsSuccess)
        {
            _tabControllerLayoutRevision = saved.Value!.Revision;
            return;
        }

        if (saved.Error?.Code == ControllerErrorCode.Conflict)
        {
            var latest = await _tabControllerLayouts.LoadAsync(_privacy, _windowLifetime.Token);
            if (latest.IsSuccess)
            {
                _tabControllerLayoutRevision = latest.Value!.Revision;
            }
        }
    }

    private void OnTabControllerDockRequested(object? sender, EventArgs args) =>
        _ = DockTabControllerAsync();

    private async void OnShowTabsRequested(object? sender, EventArgs args) =>
        await DetachTabControllerAsync(focusSelected: true, bounds: null);

    private void OnTabControllerWindowClosed(object? sender, EventArgs args)
    {
        if (sender is FoundationTabControllerWindow tool)
        {
            tool.DockRequested -= OnTabControllerDockRequested;
            tool.Closed -= OnTabControllerWindowClosed;
        }
    }

    private void OnResourceMonitorRequested(object? sender, ResourceMonitorRequestedEventArgs args)
    {
        if (args.IsVisibleRequested)
        {
            var monitor = _chrome.CreateResourceTaskWindow(this);
            monitor.Icon = OrbitProgramIdentity.CreateWindowIcon();
            if (!ReferenceEquals(_resourceMonitorWindow, monitor))
            {
                if (_resourceMonitorWindow is not null)
                {
                    _resourceMonitorWindow.MonitorVisibilityChanged -= OnResourceMonitorVisibilityChanged;
                }
                _resourceMonitorWindow = monitor;
                _resourceMonitorWindow.MonitorVisibilityChanged += OnResourceMonitorVisibilityChanged;
            }
            monitor.ShowOrActivate();
            return;
        }

        _resourceMonitorWindow?.HideForReuse();
    }

    private void OnResourceMonitorVisibilityChanged(object? sender, bool visible)
    {
        _chrome.ApplyResourceMonitorVisibility(visible);
        if (visible)
        {
            StartResourceSampling();
            return;
        }

        StopResourceSampling();
    }

    private void StartResourceSampling()
    {
        _resourceSampleMisses = 0;
        _resourceSampling.SetVisible(true);
    }

    private void StopResourceSampling() => _resourceSampling.SetVisible(false);

    private async ValueTask SampleResourcesOnceAsync(CancellationToken cancellationToken)
    {
        var identities = _browserState.Tabs
            .Where(tab => _hosts.ContainsKey(tab.TabId))
            .Select(tab => _hosts[tab.TabId].CreateResourceIdentity(
                tab.TabId == _browserState.SelectedTabId,
                tab.LoadState == BrowserLoadState.Loading))
            .ToArray();
        var sample = await _resourceSampler.SampleAsync(identities, cancellationToken);
        if (sample.IsSuccess)
        {
            _resourceSampleMisses = 0;
            _tabControllerSession.AcceptResourceSample(ToPresentationSample(sample.Value!));
            return;
        }

        _resourceSampleMisses++;
    }

    private static ResourceSampleProjection ToPresentationSample(BrowserResourceSnapshot sample)
    {
        var pressure = (sample.CpuPressure, sample.MemoryPressure) switch
        {
            (Foundation.Resources.ResourcePressureLevel.High, _) or
            (_, Foundation.Resources.ResourcePressureLevel.High) => Presentation.Resources.ResourcePressureLevel.High,
            (Foundation.Resources.ResourcePressureLevel.Medium, _) or
            (_, Foundation.Resources.ResourcePressureLevel.Medium) => Presentation.Resources.ResourcePressureLevel.Medium,
            (Foundation.Resources.ResourcePressureLevel.Low, Foundation.Resources.ResourcePressureLevel.Low) => Presentation.Resources.ResourcePressureLevel.Low,
            _ => Presentation.Resources.ResourcePressureLevel.Unknown,
        };
        var state = sample.State switch
        {
            Foundation.Resources.ResourceSamplingState.Fresh => Presentation.Resources.ResourceSamplingState.Active,
            Foundation.Resources.ResourceSamplingState.Stale => Presentation.Resources.ResourceSamplingState.Stale,
            Foundation.Resources.ResourceSamplingState.Unavailable => Presentation.Resources.ResourceSamplingState.Unavailable,
            Foundation.Resources.ResourceSamplingState.Paused => Presentation.Resources.ResourceSamplingState.Paused,
            _ => Presentation.Resources.ResourceSamplingState.Starting,
        };
        var status = state switch
        {
            Presentation.Resources.ResourceSamplingState.Active => "Resource use updated just now.",
            Presentation.Resources.ResourceSamplingState.Stale => "Resource information is temporarily stale.",
            Presentation.Resources.ResourceSamplingState.Unavailable => "Resource information is unavailable.",
            Presentation.Resources.ResourceSamplingState.Paused => "Resource sampling is paused.",
            _ => "Starting resource sampling.",
        };
        return new ResourceSampleProjection(
            sample.SampleId,
            sample.SampledAtUtc,
            state,
            pressure,
            sample.OrbitCpuPercent,
            sample.OrbitPrivateBytes,
            sample.OrbitProcessCount,
            sample.ClosingTabsMayHelp == ClosingTabsHelpfulness.Likely,
            status,
            sample.Tabs.Select(tab => new TabResourceContributionProjection(
                tab.TabId,
                tab.Quality switch
                {
                    ResourceAttributionQuality.ExclusiveRendererMeasured => ResourceAttributionReliability.ExclusiveRendererProcesses,
                    ResourceAttributionQuality.SharedRenderer => ResourceAttributionReliability.SharedProcesses,
                    _ => ResourceAttributionReliability.Unavailable,
                },
                tab.Quality == ResourceAttributionQuality.ExclusiveRendererMeasured ? tab.CpuPercent : null,
                tab.Quality == ResourceAttributionQuality.ExclusiveRendererMeasured ? tab.PrivateBytes : null,
                tab.LinkedProcessCount,
                tab.SharedTabCount)).ToArray());
    }

    private void RestoreControllerOriginFocus()
    {
        Activate();
        if (_tabControllerOriginFocus is UIElement element && element.IsVisible && element.IsEnabled)
        {
            element.Focus();
        }
        else if (_browserState.SelectedTabId is { } selected)
        {
            _chrome.RestoreFocus(TabStripFocusTarget.Tab(selected));
        }
        else
        {
            _chrome.RestoreFocus(TabStripFocusTarget.Strip());
        }
        _tabControllerOriginFocus = null;
    }

    private static TabControllerBoundsDip? ClampToVirtualScreen(TabControllerBoundsDip? bounds)
    {
        if (bounds is not { IsValid: true })
        {
            return null;
        }

        var width = Math.Min(bounds.Width, Math.Max(320, SystemParameters.VirtualScreenWidth));
        var height = Math.Min(bounds.Height, Math.Max(240, SystemParameters.VirtualScreenHeight));
        var left = Math.Clamp(
            bounds.Left,
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - width);
        var top = Math.Clamp(
            bounds.Top,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - height);
        return new(left, top, width, height);
    }

    private sealed class WindowTabControllerCommandSink(FoundationWindow owner) : ITabControllerCommandSink
    {
        public ValueTask<TabControllerCommandResult> ExecuteAsync(
            TabControllerCommand command,
            CancellationToken cancellationToken = default) =>
            owner.ExecuteTabControllerCommandAsync(command, cancellationToken);
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
        _windowLifetime.Cancel();
        _initialHostReady.TrySetResult(false);
        _startupOverlayCleared.TrySetResult(false);
        _startupLoading.RetryRequested -= OnStartupRetryRequested;
        _startupLoading.Cleared -= OnStartupLoadingCleared;
        _newTabPage.NavigationRequested -= OnNewTabNavigationRequested;
        _newTabPage.BookmarkLaunchRequested -= OnBookmarkLaunchRequested;
        _newTabPage.ManageBookmarksRequested -= OnManageBookmarksRequested;
        _newTabPage.OpenWorkspaceRequested -= OnOpenWorkspaceRequested;
        _newTabPage.ConfigureWorkspaceRequested -= OnConfigureWorkspaceRequested;
        _newTabPage.RemoveWorkspaceRequested -= OnRemoveWorkspaceRequested;
        _newTabPage.AffiliatedSiteLaunchRequested -= OnAffiliatedSiteLaunchRequested;
        _newTabPage.AffiliatedSitesVisibilityChangeRequested -= OnAffiliatedSitesVisibilityChangeRequested;
        _chrome.BrowserCommandRequested -= OnBrowserCommandRequested;
        _chrome.TabGroupToggleRequested -= OnTabGroupToggleRequested;
        _chrome.TabGroupCreateRequested -= OnTabGroupCreateRequested;
        _chrome.TabGroupRenameRequested -= OnTabGroupRenameRequested;
        _chrome.TabGroupUngroupRequested -= OnTabGroupUngroupRequested;
        _chrome.PrivateWindowRequested -= OnPrivateWindowRequested;
        _chrome.UtilitySurfaceRequested -= OnUtilitySurfaceRequested;
        _chrome.WorkspacePreferencesChanged -= OnWorkspacePreferencesChanged;
        _chrome.CompactTabModeChangeRequested -= OnCompactTabModeChangeRequested;
        _chrome.ShowTabsRequested -= OnShowTabsRequested;
        _chrome.ResourceMonitorRequested -= OnResourceMonitorRequested;
        _chrome.OfflineReadingActionRequested -= OnOfflineReadingActionRequested;
        _chrome.QuickViewActionRequested -= OnQuickViewActionRequested;
        _siteProtection.ReloadRequested -= OnProtectionReloadRequested;
        _resourceSampling.Dispose();
        if (_resourceMonitorWindow is not null)
        {
            _resourceMonitorWindow.MonitorVisibilityChanged -= OnResourceMonitorVisibilityChanged;
            _resourceMonitorWindow.DisposeForOwner();
            _resourceMonitorWindow = null;
        }
        _tabControllerWindow?.CloseForOwner();
        _tabControllerWindow = null;
        if (_offlineLibraryWindow is not null)
        {
            _offlineLibraryWindow.Close();
            _offlineLibraryWindow = null;
            _offlineLibraryControl = null;
        }
        _chrome.ApplyQuickViewWebContent(null);
        if (_quickViewHost is not null)
        {
            _webSurface.Children.Remove(_quickViewHost);
            _quickViewHost.TabVisualStateChanged -= OnQuickViewHostVisualStateChanged;
            _quickViewHost = null;
        }
        if (_quickViewPrepared is not null)
        {
            await _quickViewPrepared.DisposeAsync();
            _quickViewPrepared = null;
        }
        _chrome.ResetQuickViewForFreshUse();
        _chrome.UnbindTabControllerSession();
        _chrome.WorkspaceLocalArtworkImporter = null;
        foreach (var host in _hosts.Values)
        {
            DetachHost(host);
            await host.DisposeAsync();
        }
        _permissionPrompts.Dispose();
        await LocalPrivacy.RevokeSessionAsync(_privacy);
        if (_privacy.IsPrivate)
            await _privateWindows.CloseAsync(new ClosePrivateWindowIntent(_privacy, _windowId));
        await _workspaceCoordinator.DisposeAsync();
        _quickViewGate.Dispose();
        _windowLifetime.Dispose();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        Closing -= OnClosing;
        _windowLifetime.Cancel();
    }

    private void CompleteInitialHostReady()
    {
        if (_initialHostReady.TrySetResult(true))
        {
            _startupLoading.Complete();
        }
    }

    private void ShowStartupFailure()
    {
        _startupFailureShown = true;
        _initialHostReady.TrySetResult(false);
        _startupLoading.ShowFailure(
            "Orbit Navigator could not start its web engine. Try again, or reinstall if the problem continues.",
            canRetry: true);
    }

    private void OnStartupRetryRequested(object? sender, EventArgs e)
    {
        if (!_startupFailureShown)
        {
            return;
        }

        try
        {
            _retryStartup();
            Close();
        }
        catch
        {
            _startupLoading.ShowFailure(
                "Orbit Navigator could not restart. Close the app and try again.",
                canRetry: false);
        }
    }

    private void OnStartupLoadingCleared(object? sender, EventArgs e) =>
        _startupOverlayCleared.TrySetResult(true);

    private static BrowserTabState NewTab(BrowserTabId id, bool isPrivate) =>
        new(id, null, null, "New Tab", BrowserLoadState.Idle, false, false, isPrivate);
}
