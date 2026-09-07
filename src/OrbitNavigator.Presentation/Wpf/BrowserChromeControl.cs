#if ORBIT_WPF
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Privacy;
using OrbitNavigator.Presentation.Accessibility;
using OrbitNavigator.Presentation.Common;
using OrbitNavigator.Presentation.Navigation;
using OrbitNavigator.Presentation.Offline;
using OrbitNavigator.Presentation.Permissions;
using OrbitNavigator.Presentation.QuickView;
using OrbitNavigator.Presentation.Shell;
using OrbitNavigator.Presentation.Tabs;
using OrbitNavigator.Presentation.Workspace;

namespace OrbitNavigator.Presentation.Wpf;

public enum ResourceMonitorRequestSource
{
    DockedTabController = 0,
    DetachedTabController = 1,
    MainBrowserToolbar = 2,
}

public sealed record ResourceMonitorRequestedEventArgs(
    ResourceMonitorRequestSource Source,
    bool IsVisibleRequested,
    bool FocusWhenShown);

/// <summary>
/// Accessible WPF browser chrome. It renders immutable presentation state and
/// emits typed intents; WebView lifecycle, storage, privacy enforcement, and
/// command execution remain in their owning assemblies.
/// </summary>
public sealed class BrowserChromeControl : Grid
{
    private readonly StackPanel tabRow = new() { Orientation = Orientation.Horizontal };
    private readonly ScrollViewer tabScroller = new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        CanContentScroll = true,
    };
    private readonly Border tabSurface = OrbitVisualTheme.CreateSurface(10);
    private readonly GridSplitter sideTabPanelResizeHandle = new()
    {
        Width = 8,
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Stretch,
        ResizeDirection = GridResizeDirection.Columns,
        ResizeBehavior = GridResizeBehavior.CurrentAndNext,
        ShowsPreview = false,
        Cursor = Cursors.SizeWE,
        Focusable = true,
        Visibility = Visibility.Collapsed,
        ToolTip = "Drag to resize the tab panel",
    };
    private readonly DockPanel toolbar = new() { LastChildFill = true };
    private readonly Border toolbarSurface = OrbitVisualTheme.CreateSurface(12);
    private readonly TextBox omnibox = new()
    {
        MinWidth = 240,
        Height = 40,
        Margin = new Thickness(6, 4, 6, 4),
        VerticalContentAlignment = VerticalAlignment.Center,
    };
    private readonly Border privateIndicator = new()
    {
        Margin = new Thickness(6, 6, 4, 6),
        Padding = new Thickness(10, 4, 10, 4),
        CornerRadius = new CornerRadius(8),
        Visibility = Visibility.Collapsed,
    };
    private readonly TextBlock privateIndicatorText = new()
    {
        Text = "Private window",
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly Border permissionSurface = new()
    {
        Visibility = Visibility.Collapsed,
        Margin = new Thickness(12, 10, 16, 12),
        Padding = new Thickness(14),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12),
        MaxWidth = 420,
        MaxHeight = 280,
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Top,
    };
    private readonly StackPanel permissionLayout = new() { Orientation = Orientation.Vertical };
    private readonly ScrollViewer permissionScroller = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        CanContentScroll = true,
    };
    private readonly TextBlock statusAnnouncer = new()
    {
        Width = 1,
        Height = 1,
        Opacity = 0.01,
        IsHitTestVisible = false,
        Focusable = false,
    };
    private readonly ContentPresenter contentHost = new();
    private readonly QuickViewControl quickView = new();
    private readonly Dictionary<BrowserTabId, Button> tabButtons = [];
    private readonly Dictionary<BrowserTabId, FrameworkElement> tabContainers = [];
    private readonly Dictionary<BrowserTabId, TabVisualMetadataPresentation> tabVisuals = [];
    private readonly Dictionary<BrowserTabId, TabInteractionCapabilitiesPresentation> tabInteractions = [];
    private readonly Dictionary<BrowserTabGroupId, TabGroupHeaderButton> groupButtons = [];
    private readonly Dictionary<UtilityDrawerKind, UtilityRouteAvailability> drawerAvailability = [];
    private readonly Dictionary<InternalPageKind, UtilityRouteAvailability> pageAvailability = [];
    private readonly List<WeakReference<TabControllerControl>> detachedTabControllers = [];
    private readonly OmniboxTargetResolver omniboxResolver = new();
    private readonly Button backButton;
    private readonly Button forwardButton;
    private readonly Button reloadButton;
    private readonly Button homeButton;
    private readonly Button privateWindowButton;
    private readonly Button collapseModeButton;
    private readonly Button compactTabsButton;
    private readonly Button tabLayoutButton;
    private readonly Button affiliatedRailButton;
    private readonly Button showTabsButton;

    private BrowserState? browserState;
    private TabStripViewState? tabStrip;
    private IReadOnlyDictionary<BrowserTabGroupId, TabGroupPresentation> groupPresentations =
        new Dictionary<BrowserTabGroupId, TabGroupPresentation>();
    private BrowsingContext? browsingContext;
    private TabStripViewState? beforeClose;
    private BrowserTabId? pendingClosedTabId;
    private BrowserTabId? dragSourceTabId;
    private Point dragStart;
    private PermissionPromptPresenter? permissionPresenter;
    private RequestId? permissionSubmissionRequestId;
    private EventHandler<CreatePrivateWindowIntent>? privateWindowRequested;
    private EventHandler<WorkspacePreferencesChangedEventArgs>? workspacePreferencesChanged;
    private EventHandler<CompactTabModeChangeRequestedEventArgs>? compactTabModeChangeRequested;
    private ContextMenu? activeBrowserMenu;
    private bool systemParameterEventsAttached;
    private bool reducedMotion;
    private bool compactTabMode;
    private BrowserWorkspacePreferences workspacePreferences = BrowserWorkspacePreferences.Default;
    private Func<WorkspaceLocalArtworkImportRequest, WorkspaceArtworkPresentation?>? workspaceLocalArtworkImporter;
    private TabControllerPresentationSession? tabControllerSession;
    private TabControllerControl? dockedTabController;
    private ResourceTaskWindow? resourceTaskWindow;
    private bool resourceMonitorVisible;
    private bool tabPlacementTransitionInProgress;
    private TabControllerHostState tabControllerHostState = TabControllerHostState.Docked;
    private OfflineReadingCatalogPresentation offlineReading =
        OfflineReadingCatalogPresentation.Unavailable(false, "Offline reading is not connected.");

    public BrowserChromeControl()
    {
        MinHeight = 104;
        Focusable = true;
        AutomationProperties.SetName(this, "Orbit Navigator browser chrome");

        backButton = CreateNavigationButton("\u2190", "Go back", RequestBack);
        forwardButton = CreateNavigationButton("\u2192", "Go forward", RequestForward);
        reloadButton = CreateNavigationButton("\u21bb", "Reload page", RequestReloadOrStop);
        homeButton = CreateNavigationButton("Home", "Go to home page", RequestHome);
        privateWindowButton = CreateNavigationButton("Private +", "New private window", RequestPrivateWindow);
        collapseModeButton = CreateNavigationButton(
            Icon(OrbitIconKind.ChevronRight, 18),
            "Collapse inactive tabs",
            ToggleCollapseToActive);
        compactTabsButton = CreateNavigationButton(
            "Compact tabs",
            "Use compact favicon-only tabs",
            RequestCompactTabModeChange);
        tabLayoutButton = CreateNavigationButton(
            PlacementGlyph(BrowserWorkspacePreferences.Default.TabStripPlacement),
            "Change tab placement",
            OpenTabLayoutMenu);
        affiliatedRailButton = CreateNavigationButton(
            "Sites",
            "Show or hide Affiliated Sites rail",
            ToggleAffiliatedRail);
        affiliatedRailButton.ContextMenu = BuildAffiliatedRailMenu();
        showTabsButton = CreateNavigationButton("Show tabs", "Show detached tabs", RequestShowTabs);
        showTabsButton.Visibility = Visibility.Collapsed;
        AutomationProperties.SetHelpText(
            showTabsButton,
            "Activates the detached tab controller without changing or moving any tabs.");

        backButton.Content = Icon(OrbitIconKind.Back, 19);
        forwardButton.Content = Icon(OrbitIconKind.Forward, 19);
        reloadButton.Content = Icon(OrbitIconKind.Reload, 19);
        homeButton.Content = Icon(OrbitIconKind.Home, 19);
        privateWindowButton.Content = IconLabel(OrbitIconKind.Private, "Private +");
        InitializeUtilityAvailability();

        BuildLayout();
        quickView.ActionRequested += (_, args) => QuickViewActionRequested?.Invoke(this, args);
        SizeChanged += (_, _) =>
        {
            quickView.ApplyOwnerViewport(new(
                Math.Max(1, contentHost.ActualWidth),
                Math.Max(1, contentHost.ActualHeight)));
            UpdatePermissionSurfaceBounds();
        };
        ApplyPalette();
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public event EventHandler<BrowserCommand>? BrowserCommandRequested;

    public event EventHandler<OfflineReadingActionRequestedEventArgs>? OfflineReadingActionRequested;

    public event EventHandler<QuickViewActionRequestedEventArgs>? QuickViewActionRequested;

    public event EventHandler<CreatePrivateWindowIntent>? PrivateWindowRequested
    {
        add
        {
            privateWindowRequested += value;
            UpdatePrivateWindowAvailability();
        }
        remove
        {
            privateWindowRequested -= value;
            UpdatePrivateWindowAvailability();
        }
    }

    public event EventHandler<TabGroupToggleRequestedEventArgs>? TabGroupToggleRequested;

    public event EventHandler<TabGroupCreateRequestedEventArgs>? TabGroupCreateRequested;

    public event EventHandler<TabGroupRenameRequestedEventArgs>? TabGroupRenameRequested;

    public event EventHandler<TabGroupUngroupRequestedEventArgs>? TabGroupUngroupRequested;

    public event EventHandler<UtilitySurfaceRequestedEventArgs>? UtilitySurfaceRequested;

    public event EventHandler? ShowTabsRequested;

    public event EventHandler<ResourceMonitorRequestedEventArgs>? ResourceMonitorRequested;

    public event EventHandler<CompactTabModeChangeRequestedEventArgs>? CompactTabModeChangeRequested
    {
        add
        {
            compactTabModeChangeRequested += value;
            UpdateCompactTabModeControl();
        }
        remove
        {
            compactTabModeChangeRequested -= value;
            UpdateCompactTabModeControl();
        }
    }

    public event EventHandler<WorkspacePreferencesChangedEventArgs>? WorkspacePreferencesChanged
    {
        add
        {
            workspacePreferencesChanged += value;
            UpdateWorkspacePreferenceControls();
        }
        remove
        {
            workspacePreferencesChanged -= value;
            UpdateWorkspacePreferenceControls();
        }
    }

    public Uri HomeUri { get; set; } = new("https://duckduckgo.com/");

    public UIElement? WebContent
    {
        get => contentHost.Content as UIElement;
        set => contentHost.Content = value;
    }

    public bool IsPermissionPromptVisible => permissionSurface.Visibility == Visibility.Visible;

    public BrowserWorkspacePreferences WorkspacePreferences => workspacePreferences;

    public TabControllerPresentationSession? TabControllerSession => tabControllerSession;

    public bool IsDockedTabControllerVisible =>
        dockedTabController is not null && tabSurface.Visibility == Visibility.Visible;

    public bool IsShowTabsRecoveryVisible => showTabsButton.Visibility == Visibility.Visible;

    public bool IsResourceMonitorVisible => resourceMonitorVisible;

    public bool IsCompactTabMode => compactTabMode;

    public GridSplitter SideTabPanelResizeHandle => sideTabPanelResizeHandle;

    public QuickViewControl QuickView => quickView;

    /// <summary>
    /// Host-owned safe local artwork importer shared by the docked controller and
    /// every detached view for this owner window. The delegate returns only an
    /// opaque validated asset ID; no raw path is retained by Presentation.
    /// </summary>
    public Func<WorkspaceLocalArtworkImportRequest, WorkspaceArtworkPresentation?>? WorkspaceLocalArtworkImporter
    {
        get => workspaceLocalArtworkImporter;
        set
        {
            workspaceLocalArtworkImporter = value;
            if (dockedTabController is not null)
            {
                dockedTabController.LocalArtworkImporter = value;
            }
            foreach (var controller in LiveDetachedTabControllers())
            {
                controller.LocalArtworkImporter = value;
            }
        }
    }

    public bool ReducedMotion
    {
        get => reducedMotion;
        set
        {
            reducedMotion = value;
            if (dockedTabController is not null) dockedTabController.ReducedMotion = value;
            foreach (var controller in LiveDetachedTabControllers()) controller.ReducedMotion = value;
            if (resourceTaskWindow is { IsDisposed: false }) resourceTaskWindow.Panel.ReducedMotion = value;
        }
    }

    public void SetBrowsingContext(BrowsingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        browsingContext = context;
        SetPrivateMode(context.Privacy.IsPrivate);
        if (context.Privacy.IsPrivate)
        {
            ApplyOfflineReading(OfflineReadingCatalogPresentation.Unavailable(
                true,
                "Offline reading is unavailable in private browsing."));
            ApplyQuickView(QuickViewPresentation.Unavailable(
                true,
                "Quick View is unavailable in private browsing."));
        }
    }

    public void ApplyOfflineReading(OfflineReadingCatalogPresentation presentation)
    {
        offlineReading = (presentation ?? throw new ArgumentNullException(nameof(presentation))).Validate();
        if (activeBrowserMenu is not null)
        {
            RefreshMenuAvailability(activeBrowserMenu);
        }
    }

    public OfflineLibraryControl CreateOfflineLibraryControl()
    {
        var control = new OfflineLibraryControl();
        control.ActionRequested += (_, args) => OfflineReadingActionRequested?.Invoke(this, args);
        control.Apply(offlineReading);
        return control;
    }

    public void ApplyQuickView(QuickViewPresentation presentation) => quickView.Apply(presentation);

    public void ApplyQuickViewWebContent(UIElement? webContent) => quickView.WebContent = webContent;

    public void ResetQuickViewForFreshUse() => quickView.ResetForFreshUse();

    /// <summary>
    /// Replaces the legacy strip with the reusable, non-overlay controller. The
    /// supplied session remains the only state/command authority for both views.
    /// </summary>
    public void BindTabControllerSession(TabControllerPresentationSession presentationSession)
    {
        ArgumentNullException.ThrowIfNull(presentationSession);
        if (ReferenceEquals(tabControllerSession, presentationSession))
        {
            return;
        }

        if (dockedTabController is not null)
        {
            dockedTabController.ResourcePanelVisibilityChanged -= OnDockedResourcePanelVisibilityChanged;
            dockedTabController.Unbind();
        }
        if (tabControllerSession is not null)
        {
            tabControllerSession.PresentationChanged -= OnTabControllerPresentationChanged;
        }
        ReleaseDetachedTabControllers(unbind: true);
        resourceTaskWindow?.DisposeForOwner();
        resourceTaskWindow = null;
        ApplyResourceMonitorVisibility(false);
        tabControllerSession = presentationSession;
        dockedTabController = new TabControllerControl(TabControllerSurfaceKind.Docked)
        {
            ReducedMotion = reducedMotion,
            LocalArtworkImporter = workspaceLocalArtworkImporter,
        };
        dockedTabController.ResourcePanelVisibilityChanged += OnDockedResourcePanelVisibilityChanged;
        dockedTabController.Bind(presentationSession);
        dockedTabController.ApplyCompactMode(compactTabMode);
        presentationSession.PresentationChanged += OnTabControllerPresentationChanged;
        ReplayTabVisualMetadata();
        tabSurface.Child = dockedTabController;
        dockedTabController.ApplyLayoutPlacement(workspacePreferences.TabStripPlacement);
        AutomationProperties.SetName(tabSurface, "Tabs");
        if (presentationSession.Current is { } current)
        {
            ApplyTabControllerHostState(current.Projection.HostState);
        }
    }

    public TabControllerControl CreateDetachedTabControllerControl()
    {
        if (tabControllerSession is null)
        {
            throw new InvalidOperationException("Bind the owner window's tab controller session first.");
        }

        var controller = new TabControllerControl(TabControllerSurfaceKind.Detached)
        {
            ReducedMotion = reducedMotion,
            LocalArtworkImporter = workspaceLocalArtworkImporter,
        };
        controller.ResourcePanelVisibilityChanged += OnDetachedResourceMonitorVisibilityChanged;
        controller.Unloaded += OnDetachedTabControllerUnloaded;
        controller.Released += OnDetachedTabControllerReleased;
        controller.Bind(tabControllerSession);
        controller.ApplyCompactMode(compactTabMode);
        detachedTabControllers.Add(new(controller));
        controller.ApplyResourcePanelVisibility(resourceMonitorVisible);
        return controller;
    }

    public ResourceTaskWindow CreateResourceTaskWindow(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (tabControllerSession is null)
        {
            throw new InvalidOperationException("Bind the owner window's tab controller session first.");
        }

        if (resourceTaskWindow is { IsDisposed: false })
        {
            if (!ReferenceEquals(resourceTaskWindow.Owner, owner))
            {
                throw new InvalidOperationException("The resource monitor is already owned by another window.");
            }
            return resourceTaskWindow;
        }

        resourceTaskWindow = new ResourceTaskWindow(owner, tabControllerSession, reducedMotion);
        return resourceTaskWindow;
    }

    public void ApplyResourceMonitorVisibility(bool visible)
    {
        resourceMonitorVisible = visible;
        dockedTabController?.ApplyResourcePanelVisibility(visible);
        foreach (var controller in LiveDetachedTabControllers())
        {
            // Applying authoritative host visibility is deliberately silent;
            // TabControllerControl only emits requests from user activation.
            controller.ApplyResourcePanelVisibility(visible);
        }
    }

    public void ApplyCompactTabMode(bool enabled)
    {
        compactTabMode = enabled;
        dockedTabController?.ApplyCompactMode(enabled);
        foreach (var controller in LiveDetachedTabControllers())
        {
            controller.ApplyCompactMode(enabled);
        }
        UpdateCompactTabModeControl();
    }

    public bool ApplyTabVisualMetadata(TabVisualMetadataPresentation visual)
    {
        ArgumentNullException.ThrowIfNull(visual);
        var accepted = visual.Validate();
        var isNew = !tabVisuals.TryGetValue(accepted.TabId, out var current) ||
                    accepted.Revision > current.Revision;
        if (isNew)
        {
            tabVisuals[accepted.TabId] = accepted;
        }

        var projected = tabControllerSession?.AcceptTabVisualMetadata(accepted) ?? false;
        return isNew || projected;
    }

    public bool ApplyTabInteractionCapabilities(TabInteractionCapabilitiesPresentation capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        var accepted = capabilities.Validate();
        var isNew = !tabInteractions.TryGetValue(accepted.TabId, out var current) ||
                    accepted.Revision > current.Revision;
        if (isNew)
        {
            tabInteractions[accepted.TabId] = accepted;
        }

        var projected = tabControllerSession?.AcceptTabInteractionCapabilities(accepted) ?? false;
        return isNew || projected;
    }

    public void RequestResourceMonitor(
        bool visible = true,
        ResourceMonitorRequestSource source = ResourceMonitorRequestSource.MainBrowserToolbar) =>
        RaiseResourceMonitorRequest(source, visible);

    public bool FocusShowTabsRecovery() => showTabsButton.Focus();

    public void UnbindTabControllerSession()
    {
        ApplyResourceMonitorVisibility(false);
        if (tabControllerSession is not null)
        {
            tabControllerSession.PresentationChanged -= OnTabControllerPresentationChanged;
        }
        if (dockedTabController is not null)
        {
            dockedTabController.ResourcePanelVisibilityChanged -= OnDockedResourcePanelVisibilityChanged;
        }
        dockedTabController?.Unbind();
        ReleaseDetachedTabControllers(unbind: true);
        resourceTaskWindow?.DisposeForOwner();
        resourceTaskWindow = null;
        dockedTabController = null;
        tabControllerSession = null;
        tabControllerHostState = TabControllerHostState.Docked;
        showTabsButton.Visibility = Visibility.Collapsed;
        tabSurface.Visibility = Visibility.Visible;
        tabSurface.Child = tabScroller;
        ApplyTabStripPlacement();
    }

    public void SetUtilitySurfaceAvailability(
        UtilityDrawerKind drawer,
        bool available,
        string unavailableReason)
    {
        if (!Enum.IsDefined(drawer) || drawer == UtilityDrawerKind.None)
        {
            throw new ArgumentOutOfRangeException(nameof(drawer));
        }

        drawerAvailability[drawer] = UtilityRouteAvailability.Create(available, unavailableReason);
        if (activeBrowserMenu is not null)
        {
            RefreshMenuAvailability(activeBrowserMenu);
        }
    }

    public void SetUtilitySurfaceAvailability(
        InternalPageKind page,
        bool available,
        string unavailableReason)
    {
        if (!Enum.IsDefined(page) || page == InternalPageKind.None)
        {
            throw new ArgumentOutOfRangeException(nameof(page));
        }

        pageAvailability[page] = UtilityRouteAvailability.Create(available, unavailableReason);
        if (activeBrowserMenu is not null)
        {
            RefreshMenuAvailability(activeBrowserMenu);
        }
    }

    public void ApplyWorkspacePreferences(BrowserWorkspacePreferences preferences)
    {
        var next = (preferences ?? throw new ArgumentNullException(nameof(preferences))).Validate();
        var previous = workspacePreferences;
        var focusTarget = CaptureTabStripFocus();
        workspacePreferences = next;
        ApplyTabStripPlacement();
        dockedTabController?.ApplyLayoutPlacement(next.TabStripPlacement);
        UpdateWorkspacePreferenceControls();
        if (browserState is not null &&
            (previous.CollapseToActive != next.CollapseToActive ||
             previous.ShowOrbitalGroupPreview != next.ShowOrbitalGroupPreview))
        {
            RenderTabs(browserState, groupPresentations);
            RestoreFocusAfterPresentationChange(focusTarget);
        }
    }

    public void RenderTabs(
        BrowserState state,
        IReadOnlyDictionary<BrowserTabGroupId, TabGroupPresentation> groups)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(groups);
        browserState = state;
        groupPresentations = groups;
        var currentTabIds = state.Tabs.Select(tab => tab.TabId).ToHashSet();
        foreach (var staleTabId in tabVisuals.Keys.Where(id => !currentTabIds.Contains(id)).ToArray())
        {
            tabVisuals.Remove(staleTabId);
        }
        foreach (var staleTabId in tabInteractions.Keys.Where(id => !currentTabIds.Contains(id)).ToArray())
        {
            tabInteractions.Remove(staleTabId);
        }
        tabStrip = TabStripProjector.Project(state, groups, workspacePreferences.CollapseToActive);
        tabRow.Children.Clear();
        tabButtons.Clear();
        tabContainers.Clear();
        groupButtons.Clear();

        foreach (var entry in tabStrip.Entries)
        {
            switch (entry)
            {
                case TabGroupHeaderEntry group:
                    AddGroupHeader(group);
                    break;
                case BrowserTabEntry tab:
                    AddTab(tab);
                    break;
            }
        }

        AddNewTabButton();
        UpdateNavigationAvailability();
        RenderPermissionPrompt(permissionPresenter?.State ?? PermissionPromptViewState.Empty);

        if (beforeClose is not null &&
            pendingClosedTabId is { } closedTabId &&
            !state.Tabs.Any(tab => tab.TabId == closedTabId))
        {
            RestoreFocus(TabFocusRestorationPlanner.AfterTabClosed(beforeClose, tabStrip, closedTabId));
        }

        beforeClose = null;
        pendingClosedTabId = null;
    }

    public void SetPrivateMode(bool isPrivate)
    {
        privateIndicator.Visibility = isPrivate ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(privateIndicator, isPrivate ? "Private window" : string.Empty);
        UpdatePrivateWindowAvailability();
    }

    public void BindPermissionPrompt(PermissionPromptPresenter presenter)
    {
        ArgumentNullException.ThrowIfNull(presenter);
        if (permissionPresenter is not null)
        {
            permissionPresenter.StateChanged -= OnPermissionStateChanged;
        }

        permissionPresenter = presenter;
        presenter.StateChanged += OnPermissionStateChanged;
        RenderPermissionPrompt(presenter.State);
    }

    public void DisconnectPresentation()
    {
        UnbindTabControllerSession();
        if (permissionPresenter is not null)
        {
            permissionPresenter.StateChanged -= OnPermissionStateChanged;
            permissionPresenter = null;
        }
    }

    public void RestoreFocus(TabStripFocusTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        Dispatcher.BeginInvoke(() =>
        {
            switch (target.Kind)
            {
                case TabStripFocusTargetKind.Tab
                    when target.TabId is { } tabId && tabButtons.TryGetValue(tabId, out var tab):
                    tab.Focus();
                    break;
                case TabStripFocusTargetKind.GroupHeader
                    when target.GroupId is { } groupId && groupButtons.TryGetValue(groupId, out var group):
                    group.Focus();
                    break;
                default:
                    tabScroller.Focus();
                    break;
            }
        });
    }

    public bool TryHandleWorkspaceShortcut(Key key, ModifierKeys modifiers)
    {
        if (modifiers != (ModifierKeys.Alt | ModifierKeys.Shift))
        {
            return false;
        }

        switch (key)
        {
            case Key.C:
                ToggleCollapseToActive();
                return true;
            case Key.F:
                RequestCompactTabModeChange();
                return true;
            case Key.D1:
            case Key.NumPad1:
                RequestTabPlacementChange(TabStripPlacement.Top);
                return true;
            case Key.D2:
            case Key.NumPad2:
                RequestTabPlacementChange(TabStripPlacement.Left);
                return true;
            case Key.D3:
            case Key.NumPad3:
                RequestTabPlacementChange(TabStripPlacement.Right);
                return true;
            default:
                return false;
        }
    }

    private void InitializeUtilityAvailability()
    {
        drawerAvailability[UtilityDrawerKind.Bookmarks] =
            UtilityRouteAvailability.Unavailable("Bookmarks are unavailable until browser data is ready.");
        drawerAvailability[UtilityDrawerKind.History] =
            UtilityRouteAvailability.Unavailable("History is unavailable until browser data is ready.");
        drawerAvailability[UtilityDrawerKind.Downloads] =
            UtilityRouteAvailability.Unavailable("Downloads are unavailable until download tracking is ready.");
        drawerAvailability[UtilityDrawerKind.ClipboardShelf] =
            UtilityRouteAvailability.Unavailable("Clipboard Shelf is unavailable until browser data is ready.");
        pageAvailability[InternalPageKind.Settings] =
            UtilityRouteAvailability.Unavailable("Settings are unavailable until the settings page is ready.");
    }

    private void BuildLayout()
    {
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(56) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });

        tabScroller.Content = tabRow;
        tabScroller.Focusable = true;
        KeyboardNavigation.SetTabNavigation(tabScroller, KeyboardNavigationMode.Continue);
        AutomationProperties.SetName(tabScroller, "Tabs");
        tabSurface.Child = tabScroller;
        tabSurface.Margin = new Thickness(8, 5, 8, 1);
        tabSurface.Padding = new Thickness(4, 0, 4, 0);
        Children.Add(tabSurface);

        AutomationProperties.SetName(sideTabPanelResizeHandle, "Resize tab panel");
        AutomationProperties.SetHelpText(
            sideTabPanelResizeHandle,
            "Drag left or right, or use the arrow keys, to resize the side tab panel.");
        Grid.SetRow(sideTabPanelResizeHandle, 0);
        Grid.SetRowSpan(sideTabPanelResizeHandle, 4);
        Panel.SetZIndex(sideTabPanelResizeHandle, 30);
        sideTabPanelResizeHandle.DragCompleted += OnSideTabPanelResizeCompleted;
        Children.Add(sideTabPanelResizeHandle);

        Grid.SetRow(toolbarSurface, 1);
        toolbarSurface.Child = toolbar;
        toolbarSurface.Margin = new Thickness(8, 3, 8, 5);
        toolbarSurface.Padding = new Thickness(4, 0, 4, 0);
        AddToolbarLeft(backButton);
        AddToolbarLeft(forwardButton);
        AddToolbarLeft(reloadButton);
        AddToolbarLeft(homeButton);
        AddToolbarLeft(privateWindowButton);

        Button? menuButton = null;
        menuButton = CreateNavigationButton(
            Icon(OrbitIconKind.Menu, 19),
            "Open browser menu",
            () => OpenBrowserMenu(menuButton!));
        DockPanel.SetDock(menuButton, Dock.Right);
        toolbar.Children.Add(menuButton);
        DockPanel.SetDock(showTabsButton, Dock.Right);
        toolbar.Children.Add(showTabsButton);
        DockPanel.SetDock(tabLayoutButton, Dock.Right);
        toolbar.Children.Add(tabLayoutButton);
        DockPanel.SetDock(affiliatedRailButton, Dock.Right);
        toolbar.Children.Add(affiliatedRailButton);
        DockPanel.SetDock(compactTabsButton, Dock.Right);
        toolbar.Children.Add(compactTabsButton);
        DockPanel.SetDock(collapseModeButton, Dock.Right);
        toolbar.Children.Add(collapseModeButton);
        AutomationProperties.SetHelpText(
            collapseModeButton,
            "Shows inactive tabs in a compact form. Keyboard shortcut: Alt+Shift+C.");
        privateIndicator.Child = privateIndicatorText;
        DockPanel.SetDock(privateIndicator, Dock.Right);
        toolbar.Children.Add(privateIndicator);

        omnibox.KeyDown += OnOmniboxKeyDown;
        AutomationProperties.SetName(omnibox, "Address and search");
        AutomationProperties.SetHelpText(omnibox, "Enter a web address or search with DuckDuckGo.");
        toolbar.Children.Add(omnibox);
        Children.Add(toolbarSurface);

        permissionScroller.Content = permissionLayout;
        permissionSurface.Child = permissionScroller;
        AutomationProperties.SetName(permissionSurface, "Site permission request");
        AutomationProperties.SetLiveSetting(permissionSurface, AutomationLiveSetting.Polite);
        KeyboardNavigation.SetTabNavigation(permissionSurface, KeyboardNavigationMode.Continue);
        Grid.SetRow(permissionSurface, 3);
        Grid.SetColumnSpan(permissionSurface, 2);
        Panel.SetZIndex(permissionSurface, 60);
        Children.Add(permissionSurface);

        AutomationProperties.SetLiveSetting(statusAnnouncer, AutomationLiveSetting.Assertive);
        Grid.SetRow(statusAnnouncer, 2);
        Children.Add(statusAnnouncer);

        Grid.SetRow(contentHost, 3);
        KeyboardNavigation.SetTabNavigation(contentHost, KeyboardNavigationMode.Continue);
        Children.Add(contentHost);
        Grid.SetRow(quickView, 3);
        Panel.SetZIndex(quickView, 40);
        Children.Add(quickView);
        ApplyTabStripPlacement();
        UpdateWorkspacePreferenceControls();
        UpdateCompactTabModeControl();
    }

    private void AddToolbarLeft(Button button)
    {
        DockPanel.SetDock(button, Dock.Left);
        toolbar.Children.Add(button);
    }

    private void ApplyTabStripPlacement()
    {
        var placement = workspacePreferences.TabStripPlacement;
        var isVertical = placement is TabStripPlacement.Left or TabStripPlacement.Right;
        var controllerSuppressed = tabControllerSession is not null &&
            tabControllerHostState is TabControllerHostState.Opening or
                TabControllerHostState.Detached or
                TabControllerHostState.Closing;
        tabRow.Orientation = isVertical ? Orientation.Vertical : Orientation.Horizontal;
        // The reusable controller owns overflow with reserved controls. The
        // legacy fallback is clipped and never draws a scrollbar over tabs.
        tabScroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        tabScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        AutomationProperties.SetHelpText(tabScroller, $"Tabs are placed on the {placement.ToString().ToLowerInvariant()}.");

        if (!isVertical)
        {
            sideTabPanelResizeHandle.Visibility = Visibility.Collapsed;
            ColumnDefinitions[0].MinWidth = 0;
            ColumnDefinitions[0].MaxWidth = double.PositiveInfinity;
            ColumnDefinitions[1].MinWidth = 0;
            ColumnDefinitions[1].MaxWidth = double.PositiveInfinity;
            ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            ColumnDefinitions[1].Width = new GridLength(0);
            RowDefinitions[0].Height = controllerSuppressed
                ? new GridLength(0)
                : new GridLength(dockedTabController is null ? 48 : 61);
            RowDefinitions[1].Height = new GridLength(56);
            RowDefinitions[2].Height = GridLength.Auto;
            RowDefinitions[3].Height = new GridLength(1, GridUnitType.Star);
            Place(tabSurface, 0, 0, 1, 2);
            Place(toolbarSurface, 1, 0, 1, 2);
            Place(permissionSurface, 3, 0, 1, 2);
            Place(statusAnnouncer, 2, 0, 1, 2);
            Place(contentHost, 3, 0, 1, 2);
            Place(quickView, 3, 0, 1, 2);
            tabSurface.Margin = new Thickness(8, 5, 8, 1);
            tabSurface.Padding = new Thickness(4, 0, 4, 0);
            UpdatePermissionSurfaceBounds();
            return;
        }

        var tabColumn = placement == TabStripPlacement.Left ? 0 : 1;
        var contentColumn = placement == TabStripPlacement.Left ? 1 : 0;
        var tabColumnDefinition = ColumnDefinitions[tabColumn];
        var contentColumnDefinition = ColumnDefinitions[contentColumn];
        tabColumnDefinition.MinWidth = controllerSuppressed
            ? 0
            : BrowserWorkspacePreferences.MinimumSideTabPanelWidth;
        tabColumnDefinition.MaxWidth = controllerSuppressed
            ? double.PositiveInfinity
            : BrowserWorkspacePreferences.MaximumSideTabPanelWidth;
        contentColumnDefinition.MinWidth = 0;
        contentColumnDefinition.MaxWidth = double.PositiveInfinity;
        tabColumnDefinition.Width = controllerSuppressed
            ? new GridLength(0)
            : new GridLength(workspacePreferences.SideTabPanelWidth);
        contentColumnDefinition.Width = new GridLength(1, GridUnitType.Star);
        RowDefinitions[0].Height = new GridLength(56);
        RowDefinitions[1].Height = GridLength.Auto;
        RowDefinitions[2].Height = new GridLength(1, GridUnitType.Star);
        RowDefinitions[3].Height = new GridLength(0);
        Place(tabSurface, 0, tabColumn, 4, 1);
        Place(toolbarSurface, 0, contentColumn);
        Place(permissionSurface, 2, contentColumn, 2, 1);
        Place(statusAnnouncer, 1, contentColumn);
        Place(contentHost, 2, contentColumn, 2, 1);
        Place(quickView, 2, contentColumn, 2, 1);
        Grid.SetColumn(sideTabPanelResizeHandle, tabColumn);
        sideTabPanelResizeHandle.HorizontalAlignment = placement == TabStripPlacement.Left
            ? HorizontalAlignment.Right
            : HorizontalAlignment.Left;
        sideTabPanelResizeHandle.ResizeBehavior = placement == TabStripPlacement.Left
            ? GridResizeBehavior.CurrentAndNext
            : GridResizeBehavior.PreviousAndCurrent;
        sideTabPanelResizeHandle.Visibility = controllerSuppressed
            ? Visibility.Collapsed
            : Visibility.Visible;
        tabSurface.Margin = placement == TabStripPlacement.Left
            ? new Thickness(8, 6, 10, 8)
            : new Thickness(10, 6, 8, 8);
        tabSurface.Padding = new Thickness(4);
        UpdatePermissionSurfaceBounds();
    }

    private void OnSideTabPanelResizeCompleted(object sender, DragCompletedEventArgs args)
    {
        var placement = workspacePreferences.TabStripPlacement;
        if (args.Canceled ||
            placement is not (TabStripPlacement.Left or TabStripPlacement.Right))
        {
            ApplyTabStripPlacement();
            return;
        }

        var tabColumn = placement == TabStripPlacement.Left ? 0 : 1;
        var requestedWidth = Math.Round(Math.Clamp(
            ColumnDefinitions[tabColumn].ActualWidth,
            BrowserWorkspacePreferences.MinimumSideTabPanelWidth,
            BrowserWorkspacePreferences.MaximumSideTabPanelWidth));
        if (Math.Abs(requestedWidth - workspacePreferences.SideTabPanelWidth) < .5)
        {
            ApplyTabStripPlacement();
            return;
        }

        if (workspacePreferencesChanged is null)
        {
            ApplyTabStripPlacement();
            Announce("Tab panel size cannot be changed until profile preferences are ready.");
            return;
        }

        RequestWorkspacePreferenceChange(workspacePreferences with
        {
            SideTabPanelWidth = requestedWidth,
        });
    }

    private void UpdatePermissionSurfaceBounds()
    {
        var available = contentHost.ActualWidth > 0
            ? contentHost.ActualWidth - 32
            : ActualWidth - 32;
        permissionSurface.Width = Math.Max(240, Math.Min(420, available));
        permissionSurface.MaxHeight = Math.Max(180, Math.Min(280, contentHost.ActualHeight * .55));
    }

    private void OnTabControllerPresentationChanged(
        object? sender,
        TabControllerPresentationChangedEventArgs args)
    {
        if (args.Kind is not (TabControllerPresentationChangeKind.Projection or
            TabControllerPresentationChangeKind.CommandResult))
        {
            return;
        }

        var hostState = args.State.Projection.HostState;
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }
        if (Dispatcher.CheckAccess())
        {
            if (ReferenceEquals(sender, tabControllerSession))
            {
                ApplyTabControllerHostState(hostState);
                ReplayTabVisualMetadata();
            }
        }
        else
        {
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(sender, tabControllerSession))
                {
                    ApplyTabControllerHostState(hostState);
                    ReplayTabVisualMetadata();
                }
            });
        }
    }

    private void ReplayTabVisualMetadata()
    {
        if (tabControllerSession?.Current is null ||
            (tabVisuals.Count == 0 && tabInteractions.Count == 0))
        {
            return;
        }

        foreach (var visual in tabVisuals.Values)
        {
            tabControllerSession.AcceptTabVisualMetadata(visual);
        }
        foreach (var interaction in tabInteractions.Values)
        {
            tabControllerSession.AcceptTabInteractionCapabilities(interaction);
        }
    }

    private void ApplyTabControllerHostState(TabControllerHostState hostState)
    {
        var previousHostState = tabControllerHostState;
        tabControllerHostState = hostState;
        var suppressed = hostState is TabControllerHostState.Opening or
            TabControllerHostState.Detached or
            TabControllerHostState.Closing;
        tabSurface.Visibility = suppressed ? Visibility.Collapsed : Visibility.Visible;
        showTabsButton.Visibility = suppressed ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetItemStatus(showTabsButton, hostState switch
        {
            TabControllerHostState.Opening => "Controller opening",
            TabControllerHostState.Detached => "Controller detached",
            TabControllerHostState.Closing => "Controller docking",
            _ => "Controller docked",
        });
        if (suppressed)
        {
            ApplyResourceMonitorVisibility(false);
        }
        else if (hostState == TabControllerHostState.Docked &&
                 previousHostState is TabControllerHostState.Opening or
                     TabControllerHostState.Detached or
                     TabControllerHostState.Closing)
        {
            // The owner-native tool window is closed by the host. Release every
            // detached presentation instance as soon as the authoritative
            // projection returns to Docked so stale vertical rails cannot remain
            // discoverable or be mistaken for the newly selected docked layout.
            ReleaseDetachedTabControllers(unbind: true);
        }
        ApplyTabStripPlacement();
    }

    private void RequestShowTabs()
    {
        if (ShowTabsRequested is null)
        {
            Announce("The detached tab controller cannot be activated until the window host is ready.");
            return;
        }

        ShowTabsRequested.Invoke(this, EventArgs.Empty);
        Announce("Activating the detached tab controller.");
    }

    private void OnDockedResourcePanelVisibilityChanged(object? sender, bool visible) =>
        RaiseResourceMonitorRequest(ResourceMonitorRequestSource.DockedTabController, visible);

    private void OnDetachedResourceMonitorVisibilityChanged(object? sender, bool visible) =>
        RaiseResourceMonitorRequest(ResourceMonitorRequestSource.DetachedTabController, visible);

    private void OnDetachedTabControllerUnloaded(object sender, RoutedEventArgs args)
    {
        if (sender is not TabControllerControl controller)
        {
            return;
        }

        RemoveDetachedTabController(controller);
    }

    private void OnDetachedTabControllerReleased(object? sender, EventArgs args)
    {
        if (sender is TabControllerControl controller)
        {
            RemoveDetachedTabController(controller);
        }
    }

    private void RemoveDetachedTabController(TabControllerControl controller)
    {
        controller.ResourcePanelVisibilityChanged -= OnDetachedResourceMonitorVisibilityChanged;
        controller.Unloaded -= OnDetachedTabControllerUnloaded;
        controller.Released -= OnDetachedTabControllerReleased;
        for (var index = detachedTabControllers.Count - 1; index >= 0; index--)
        {
            if (!detachedTabControllers[index].TryGetTarget(out var candidate) ||
                ReferenceEquals(candidate, controller))
            {
                detachedTabControllers.RemoveAt(index);
            }
        }
    }

    private IReadOnlyList<TabControllerControl> LiveDetachedTabControllers()
    {
        var live = new List<TabControllerControl>(detachedTabControllers.Count);
        for (var index = detachedTabControllers.Count - 1; index >= 0; index--)
        {
            if (detachedTabControllers[index].TryGetTarget(out var controller))
            {
                live.Add(controller);
            }
            else
            {
                detachedTabControllers.RemoveAt(index);
            }
        }
        return live;
    }

    private void ReleaseDetachedTabControllers(bool unbind)
    {
        foreach (var controller in LiveDetachedTabControllers())
        {
            controller.ResourcePanelVisibilityChanged -= OnDetachedResourceMonitorVisibilityChanged;
            controller.Unloaded -= OnDetachedTabControllerUnloaded;
            controller.Released -= OnDetachedTabControllerReleased;
            if (unbind)
            {
                controller.Unbind();
            }
        }
        detachedTabControllers.Clear();
    }

    private void RaiseResourceMonitorRequest(ResourceMonitorRequestSource source, bool visible)
    {
        if (ResourceMonitorRequested is null)
        {
            dockedTabController?.ApplyResourcePanelVisibility(false);
            Announce("Browser resources are unavailable until the window host is ready.");
            return;
        }

        ResourceMonitorRequested.Invoke(this, new(source, visible, visible));
        Announce(visible ? "Opening Browser resources." : "Hiding Browser resources.");
    }

    private static void Place(
        FrameworkElement element,
        int row,
        int column,
        int rowSpan = 1,
        int columnSpan = 1)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        Grid.SetRowSpan(element, rowSpan);
        Grid.SetColumnSpan(element, columnSpan);
    }

    private void UpdateWorkspacePreferenceControls()
    {
        var canPersist = workspacePreferencesChanged is not null;
        collapseModeButton.IsEnabled = canPersist;
        tabLayoutButton.IsEnabled = canPersist && !tabPlacementTransitionInProgress;
        affiliatedRailButton.IsEnabled = canPersist;
        var placement = workspacePreferences.TabStripPlacement;
        var placementLabel = placement.ToString();
        tabLayoutButton.Content = PlacementGlyph(placement);
        tabLayoutButton.ToolTip = "Change tab placement";
        AutomationProperties.SetName(tabLayoutButton, "Change tab placement");
        AutomationProperties.SetItemStatus(tabLayoutButton, $"Tabs: {placementLabel}");
        collapseModeButton.Content = Icon(
            workspacePreferences.CollapseToActive ? OrbitIconKind.ChevronDown : OrbitIconKind.ChevronRight,
            18);
        AutomationProperties.SetName(
            collapseModeButton,
            workspacePreferences.CollapseToActive ? "Show full tabs" : "Collapse inactive tabs");
        AutomationProperties.SetItemStatus(
            collapseModeButton,
            workspacePreferences.CollapseToActive ? "On" : "Off");
        AutomationProperties.SetHelpText(
            collapseModeButton,
            canPersist
                ? "Shows inactive tabs in a compact form. Keyboard shortcut: Alt+Shift+C."
                : "Tab layout preferences are unavailable until the profile is ready.");
        AutomationProperties.SetHelpText(
            tabLayoutButton,
            tabPlacementTransitionInProgress
                ? "Docking the tab controller before changing its placement."
                : canPersist
                ? $"Tabs: {placementLabel}. Choose Top, Left, or Right. Keyboard shortcuts: Alt+Shift+1, 2, or 3."
                : "Tab layout preferences are unavailable until the profile is ready.");
        affiliatedRailButton.Content = workspacePreferences.ShowAffiliatedRail ? "Hide sites" : "Show sites";
        affiliatedRailButton.ToolTip = workspacePreferences.ShowAffiliatedRail
            ? "Hide Affiliated Sites rail"
            : "Show Affiliated Sites rail";
        AutomationProperties.SetName(
            affiliatedRailButton,
            workspacePreferences.ShowAffiliatedRail ? "Hide Affiliated Sites rail" : "Show Affiliated Sites rail");
        AutomationProperties.SetItemStatus(
            affiliatedRailButton,
            $"{(workspacePreferences.ShowAffiliatedRail ? "Visible" : "Hidden")}; rail on {workspacePreferences.AffiliatedRailPlacement.ToString().ToLowerInvariant()}");
        AutomationProperties.SetHelpText(
            affiliatedRailButton,
            canPersist
                ? "Shows or hides the owner-approved Affiliated Sites rail. Use its context menu to choose left or right."
                : "Affiliated Sites preferences are unavailable until the profile is ready.");
        UpdateAffiliatedRailMenuChecks();
    }

    private void UpdateCompactTabModeControl()
    {
        var available = compactTabModeChangeRequested is not null;
        compactTabsButton.IsEnabled = available;
        compactTabsButton.Content = compactTabMode ? "Show titles" : "Compact tabs";
        compactTabsButton.ToolTip = compactTabMode
            ? "Show tab titles (Alt+Shift+F)"
            : "Use compact favicon-only tabs (Alt+Shift+F)";
        AutomationProperties.SetName(
            compactTabsButton,
            compactTabMode ? "Show tab titles" : "Use compact favicon-only tabs");
        AutomationProperties.SetItemStatus(compactTabsButton, compactTabMode ? "Compact tabs on" : "Compact tabs off");
        AutomationProperties.SetHelpText(
            compactTabsButton,
            available
                ? "Switches between titled tabs and favicon-only tabs. Keyboard shortcut: Alt+Shift+F."
                : "Compact tab mode is unavailable until the profile host is ready.");
    }

    private void RequestCompactTabModeChange()
    {
        if (compactTabModeChangeRequested is null)
        {
            Announce("Compact tab mode is unavailable until the profile host is ready.");
            return;
        }

        compactTabModeChangeRequested.Invoke(this, new(!compactTabMode));
        Announce(compactTabMode ? "Requesting tab titles." : "Requesting compact favicon-only tabs.");
    }

    private void ToggleCollapseToActive() => RequestWorkspacePreferenceChange(
        workspacePreferences with { CollapseToActive = !workspacePreferences.CollapseToActive });

    private void ToggleAffiliatedRail() => RequestWorkspacePreferenceChange(
        workspacePreferences with { ShowAffiliatedRail = !workspacePreferences.ShowAffiliatedRail });

    private ContextMenu BuildAffiliatedRailMenu()
    {
        var menu = new ContextMenu();
        foreach (var placement in Enum.GetValues<AffiliatedRailPlacement>())
        {
            var captured = placement;
            var item = new MenuItem
            {
                Header = $"Rail on {placement.ToString().ToLowerInvariant()}",
                IsCheckable = true,
                Tag = placement,
            };
            AutomationProperties.SetName(item, $"Place Affiliated Sites rail on {placement.ToString().ToLowerInvariant()}");
            item.Click += (_, _) => RequestWorkspacePreferenceChange(
                workspacePreferences with { AffiliatedRailPlacement = captured, ShowAffiliatedRail = true });
            menu.Items.Add(item);
        }
        OrbitVisualTheme.ApplyContextMenu(menu);
        return menu;
    }

    private void UpdateAffiliatedRailMenuChecks()
    {
        if (affiliatedRailButton.ContextMenu is not { } menu) return;
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            item.IsChecked = item.Tag is AffiliatedRailPlacement placement &&
                             placement == workspacePreferences.AffiliatedRailPlacement;
        }
    }

    private void RequestWorkspacePreferenceChange(BrowserWorkspacePreferences next)
    {
        if (workspacePreferencesChanged is null)
        {
            Announce("Tab layout preferences are unavailable until the profile is ready.");
            return;
        }

        var previous = workspacePreferences;
        var focusTarget = CaptureTabStripFocus();
        workspacePreferences = next.Validate();
        ApplyTabStripPlacement();
        dockedTabController?.ApplyLayoutPlacement(workspacePreferences.TabStripPlacement);
        UpdateWorkspacePreferenceControls();
        if (browserState is not null)
        {
            RenderTabs(browserState, groupPresentations);
            RestoreFocusAfterPresentationChange(focusTarget);
        }

        workspacePreferencesChanged.Invoke(
            this,
            new WorkspacePreferencesChangedEventArgs(workspacePreferences));
        Announce(previous.CollapseToActive != workspacePreferences.CollapseToActive
                ? workspacePreferences.CollapseToActive
                ? "Inactive tabs are now compact."
                : "All tabs are now shown at full size."
            : previous.TabStripPlacement != workspacePreferences.TabStripPlacement
                ? $"Tabs are now placed on the {workspacePreferences.TabStripPlacement.ToString().ToLowerInvariant()}."
                : Math.Abs(previous.SideTabPanelWidth - workspacePreferences.SideTabPanelWidth) >= .5
                    ? "Tab panel resized."
                : previous.WorkspacePreviewReveal != workspacePreferences.WorkspacePreviewReveal
                    ? $"Large group previews now open on {workspacePreferences.WorkspacePreviewReveal.ToString().ToLowerInvariant()}."
                    : previous.ShowAffiliatedRail != workspacePreferences.ShowAffiliatedRail
                        ? workspacePreferences.ShowAffiliatedRail
                            ? "Affiliated Sites rail is shown."
                            : "Affiliated Sites rail is hidden."
                        : $"Affiliated Sites rail moved to the {workspacePreferences.AffiliatedRailPlacement.ToString().ToLowerInvariant()}.");
    }

    private async void RequestTabPlacementChange(TabStripPlacement placement)
    {
        if (!Enum.IsDefined(placement))
        {
            return;
        }
        if (tabPlacementTransitionInProgress)
        {
            Announce("The tab controller placement is already changing.");
            return;
        }
        if (workspacePreferencesChanged is null)
        {
            Announce("Tab layout preferences are unavailable until the profile is ready.");
            return;
        }
        if (tabControllerHostState is TabControllerHostState.Opening or TabControllerHostState.Closing)
        {
            Announce("Wait for the tab controller to finish moving, then choose its placement.");
            return;
        }

        tabPlacementTransitionInProgress = true;
        UpdateWorkspacePreferenceControls();
        try
        {
            if (tabControllerHostState == TabControllerHostState.Detached)
            {
                if (tabControllerSession is null)
                {
                    Announce("The detached tab controller cannot be docked until the window host is ready.");
                    return;
                }

                await tabControllerSession.ExecuteAsync(new DockTabControllerAction(Guid.NewGuid()));
                var acceptedHostState = tabControllerSession.Current?.Projection.HostState;
                if (acceptedHostState != TabControllerHostState.Docked)
                {
                    Announce("The tab controller remains detached. Dock it before changing placement.");
                    return;
                }

                // PresentationChanged may have crossed a dispatcher boundary.
                // Apply the already accepted host state now so the detached rail
                // is released before the new docked geometry is rendered.
                ApplyTabControllerHostState(TabControllerHostState.Docked);
            }

            RequestWorkspacePreferenceChange(
                workspacePreferences with { TabStripPlacement = placement });
        }
        catch (OperationCanceledException)
        {
            Announce("The tab controller placement change was canceled.");
        }
        catch (Exception)
        {
            Announce("The tab controller could not be docked. Its placement was not changed.");
        }
        finally
        {
            tabPlacementTransitionInProgress = false;
            UpdateWorkspacePreferenceControls();
        }
    }

    private TabStripFocusTarget? CaptureTabStripFocus()
    {
        foreach (var pair in tabContainers)
        {
            if (pair.Value.IsKeyboardFocusWithin)
            {
                return TabStripFocusTarget.Tab(pair.Key);
            }
        }

        foreach (var pair in groupButtons)
        {
            if (pair.Value.IsKeyboardFocusWithin)
            {
                return TabStripFocusTarget.GroupHeader(pair.Key);
            }
        }

        return tabScroller.IsKeyboardFocusWithin
            ? TabStripFocusTarget.Strip()
            : null;
    }

    private void RestoreFocusAfterPresentationChange(TabStripFocusTarget? target)
    {
        if (target is not null)
        {
            RestoreFocus(target);
        }
    }

    private void OpenTabLayoutMenu()
    {
        if (workspacePreferencesChanged is null)
        {
            Announce("Tab layout preferences are unavailable until the profile is ready.");
            return;
        }

        var menu = new ContextMenu { PlacementTarget = tabLayoutButton };
        AddPlacementItem(menu, TabStripPlacement.Top);
        AddPlacementItem(menu, TabStripPlacement.Left);
        AddPlacementItem(menu, TabStripPlacement.Right);
        menu.Items.Add(new Separator());
        AddPreviewRevealItem(menu, WorkspacePreviewRevealMode.Hover);
        AddPreviewRevealItem(menu, WorkspacePreviewRevealMode.Click);
        OrbitVisualTheme.ApplyContextMenu(menu);
        tabLayoutButton.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void AddPlacementItem(ContextMenu menu, TabStripPlacement placement)
    {
        var label = workspacePreferences.TabStripPlacement == placement
            ? $"Tabs: {placement}"
            : $"Move tabs to {placement}";
        var item = CreateMenuItem(
            label,
            () => RequestTabPlacementChange(placement));
        item.IsCheckable = true;
        item.IsChecked = workspacePreferences.TabStripPlacement == placement;
        menu.Items.Add(item);
    }

    private void AddPreviewRevealItem(ContextMenu menu, WorkspacePreviewRevealMode mode)
    {
        var item = CreateMenuItem(
            mode == WorkspacePreviewRevealMode.Hover
                ? "Reveal large-group previews on hover"
                : "Reveal large-group previews on click",
            () => RequestWorkspacePreferenceChange(workspacePreferences with { WorkspacePreviewReveal = mode }));
        item.IsCheckable = true;
        item.IsChecked = workspacePreferences.WorkspacePreviewReveal == mode;
        AutomationProperties.SetItemStatus(item, item.IsChecked ? "Selected" : "Not selected");
        menu.Items.Add(item);
    }

    private void AddGroupHeader(TabGroupHeaderEntry header)
    {
        var content = GroupHeaderContent(header);
        var button = new TabGroupHeaderButton
        {
            GroupName = header.Name,
            TabCount = header.TabCount,
            IsCollapsed = header.IsCollapsed,
            Content = content,
            Margin = new Thickness(2),
            MinHeight = 44,
            MinWidth = 44,
            MaxWidth = header.IsCompact ? 48 : double.PositiveInfinity,
            Padding = header.IsCompact ? new Thickness(7, 2, 7, 2) : new Thickness(8, 2, 8, 2),
            ToolTip = header.IsCompact
                ? $"Activate {header.Name} tab group"
                : header.IsCollapsed ? "Expand tab group" : "Collapse tab group",
        };
        AutomationProperties.SetName(
            button,
            header.IsCompact
                ? $"{header.Name}, {header.TabCount} tabs, compact"
                : $"{header.Name}, {header.TabCount} tabs");
        AutomationProperties.SetItemStatus(
            button,
            header.IsCompact ? "Compact" : header.IsCollapsed ? "Collapsed" : "Expanded");
        OrbitVisualTheme.ApplyCompactRailButton(button, OrbitButtonRole.Group);
        button.Click += (_, _) =>
        {
            if (header.IsCompact && browserState is not null)
            {
                var first = browserState.Tabs.FirstOrDefault(tab => tab.GroupId == header.GroupId);
                if (first is not null)
                {
                    Raise(TabCommandPlanner.Select(browserState, first.TabId));
                }

                return;
            }

            TabGroupToggleRequested?.Invoke(
                this,
                new TabGroupToggleRequestedEventArgs(header.GroupId, !header.IsCollapsed));
            if (!header.IsCollapsed)
            {
                RestoreFocus(TabFocusRestorationPlanner.AfterGroupCollapsed(header.GroupId));
            }
        };
        if (content is Panel panel && panel.Children.OfType<OrbitalTabGroupGlyph>().FirstOrDefault() is { } orbit)
        {
            button.MouseEnter += (_, _) => orbit.Emphasize();
            button.GotKeyboardFocus += (_, _) => orbit.Emphasize();
        }
        button.ContextMenu = CreateGroupContextMenu(header);
        groupButtons.Add(header.GroupId, button);
        tabRow.Children.Add(button);
    }

    private void AddTab(BrowserTabEntry tab)
    {
        var select = new Button
        {
            Content = CreateTabContent(tab),
            Margin = new Thickness(2),
            MinHeight = 44,
            MinWidth = tab.IsCompact ? 44 : 96,
            MaxWidth = tab.IsCompact ? 44 : 240,
            Padding = tab.IsCompact ? new Thickness(8, 2, 8, 2) : new Thickness(10, 2, 10, 2),
            ToolTip = tab.Address?.AbsoluteUri ?? tab.Title,
            AllowDrop = true,
        };
        OrbitVisualTheme.ApplyCompactRailButton(select, OrbitButtonRole.Tab);
        ApplyTabVisual(select, tab.IsSelected, false);
        AutomationProperties.SetName(
            select,
            tab.IsPrivate
                ? $"{tab.Title}, private tab{(tab.IsCompact ? ", compact" : string.Empty)}"
                : $"{tab.Title}{(tab.IsCompact ? ", compact" : string.Empty)}");
        AutomationProperties.SetItemStatus(select, tab.IsSelected ? "Selected" : tab.IsCompact ? "Compact" : string.Empty);
        select.Click += (_, _) => Raise(TabCommandPlanner.Select(browserState!, tab.TabId));
        select.ContextMenu = CreateTabContextMenu(tab);
        select.PreviewMouseLeftButtonDown += (_, args) =>
        {
            dragStart = args.GetPosition(this);
            dragSourceTabId = tab.TabId;
        };
        select.PreviewMouseMove += (_, args) => BeginTabDrag(select, tab, args);
        select.DragEnter += (_, args) => OnTabDragEnter(select, tab, args);
        select.DragLeave += (_, _) => ApplyTabVisual(select, tab.IsSelected, false);
        select.Drop += (_, args) => OnTabDrop(select, tab, args);

        var close = new Button
        {
            Content = Icon(OrbitIconKind.Close, 15),
            Margin = new Thickness(0, 2, 2, 2),
            MinHeight = 44,
            MinWidth = 40,
            ToolTip = $"Close {tab.Title}",
            Visibility = tab.IsCompact ? Visibility.Collapsed : Visibility.Visible,
        };
        OrbitVisualTheme.ApplyButton(close, OrbitButtonRole.Quiet);
        AutomationProperties.SetName(close, $"Close {tab.Title}");
        close.Click += (_, _) => RequestCloseTab(tab.TabId);
        var container = new StackPanel { Orientation = Orientation.Horizontal };
        container.Children.Add(select);
        container.Children.Add(close);
        tabButtons.Add(tab.TabId, select);
        tabContainers.Add(tab.TabId, container);
        tabRow.Children.Add(container);
    }

    private Button CreateNavigationButton(object content, string automationName, Action action)
    {
        var button = new Button
        {
            Content = content,
            Margin = new Thickness(3, 4, 3, 4),
            MinWidth = 44,
            MinHeight = 40,
            ToolTip = automationName,
        };
        OrbitVisualTheme.ApplyButton(button, OrbitButtonRole.Toolbar);
        AutomationProperties.SetName(button, automationName);
        button.Click += (_, _) => action();
        return button;
    }

    private void OnOmniboxKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter || browserState?.SelectedTabId is not { } tabId)
        {
            return;
        }

        try
        {
            var target = omniboxResolver.Resolve(omnibox.Text);
            Raise(new NavigateBrowserCommand(browserState.WindowId, tabId, target.Uri));
            Announce(target.Kind == OmniboxTargetKind.Search ? "Searching with DuckDuckGo." : "Navigating.");
        }
        catch (ArgumentException)
        {
            Announce("Enter a web address or search term.");
            omnibox.Focus();
        }

        args.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs args)
    {
        var modifiers = args.KeyboardDevice.Modifiers;
        var shortcutKey = args.Key == Key.System ? args.SystemKey : args.Key;
        if (TryHandleWorkspaceShortcut(shortcutKey, modifiers))
        {
            args.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && args.Key == Key.L)
        {
            omnibox.Focus();
            omnibox.SelectAll();
            args.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && args.Key == Key.T)
        {
            RequestNewTab();
            args.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && args.Key == Key.W)
        {
            if (dockedTabController is not null && IsDockedTabControllerVisible)
            {
                dockedTabController.TryHandleControllerShortcut(Key.W, modifiers);
            }
            else if (browserState?.SelectedTabId is { } selected)
            {
                RequestCloseTab(selected);
            }

            args.Handled = true;
        }
        else if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && args.Key == Key.N)
        {
            RequestPrivateWindow();
            args.Handled = true;
        }
        else if (modifiers.HasFlag(ModifierKeys.Control) && args.Key == Key.Tab)
        {
            SelectAdjacentTab(modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
            args.Handled = true;
        }
        else if (modifiers == ModifierKeys.Alt && args.SystemKey == Key.Left)
        {
            RequestBack();
            args.Handled = true;
        }
        else if (modifiers == ModifierKeys.Alt && args.SystemKey == Key.Right)
        {
            RequestForward();
            args.Handled = true;
        }
        else if (modifiers == ModifierKeys.Alt && args.SystemKey == Key.Home)
        {
            RequestHome();
            args.Handled = true;
        }
        else if (args.Key == Key.F5)
        {
            RequestReloadOrStop();
            args.Handled = true;
        }
        else if (args.Key == Key.Escape && permissionPresenter?.State.IsOpen == true)
        {
            permissionPresenter.Dismiss();
            args.Handled = true;
        }
    }

    private void OnPermissionStateChanged(
        object? sender,
        PresentationStateChangedEventArgs<PermissionPromptViewState> args) =>
        Dispatcher.BeginInvoke(() => RenderPermissionPrompt(args.State));

    private void RenderPermissionPrompt(PermissionPromptViewState state)
    {
        permissionLayout.Children.Clear();
        if (!state.IsOpen || state.Prompt is null ||
            (browserState is not null && state.Prompt.Context.WindowId != browserState.WindowId))
        {
            if (state.Announcement is not null)
            {
                Announce(PresentationTextCatalog.ResolvePermissionAnnouncement(
                    state.Announcement.MessageKey));
            }
            permissionSurface.Visibility = Visibility.Collapsed;
            AutomationProperties.SetItemStatus(permissionSurface, string.Empty);
            if (permissionSubmissionRequestId is not null)
            {
                permissionSubmissionRequestId = null;
                Dispatcher.BeginInvoke(() => omnibox.Focus());
            }
            return;
        }

        var heading = new TextBlock
        {
            Text = "Site permission",
            FontWeight = FontWeights.SemiBold,
            FontSize = 15,
        };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        permissionLayout.Children.Add(heading);

        var capability = PresentationTextCatalog.Resolve(state.CapabilityLabelKey);
        var requestCopy = new TextBlock
        {
            Text = $"{state.Prompt.DisplayOrigin} is asking to use {capability}.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 8),
        };
        permissionLayout.Children.Add(requestCopy);

        if (browserState?.SelectedTabId != state.Prompt.Context.TabId)
        {
            var requestingTab = browserState?.Tabs.FirstOrDefault(tab => tab.TabId == state.Prompt.Context.TabId);
            permissionLayout.Children.Add(new TextBlock
            {
                Text = $"Requested by tab: {requestingTab?.Title ?? "background tab"}",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            });
        }

        if (state.IsExpired)
        {
            var expired = new TextBlock
            {
                Text = PresentationTextCatalog.Resolve("ui.permission.request_expired"),
                Margin = new Thickness(0, 0, 0, 8),
            };
            AutomationProperties.SetLiveSetting(expired, AutomationLiveSetting.Assertive);
            permissionLayout.Children.Add(expired);
            var dismiss = CreateNavigationButton(
                "Dismiss",
                "Dismiss expired permission request",
                () => permissionPresenter?.Dismiss());
            permissionLayout.Children.Add(dismiss);
            permissionSurface.Visibility = Visibility.Visible;
            AutomationProperties.SetItemStatus(permissionSurface, "Permission request expired");
            if (permissionSubmissionRequestId is not null)
            {
                permissionSubmissionRequestId = null;
                Dispatcher.BeginInvoke(() => dismiss.Focus());
            }
            return;
        }

        var choices = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            IsEnabled = !state.IsBusy,
        };
        Button? safestAction = null;
        foreach (var choice in state.Choices)
        {
            var choiceCopy = choice;
            var label = PresentationTextCatalog.Resolve(choice.LabelKey);
            var button = new Button
            {
                Content = label,
                Margin = new Thickness(2),
                MinWidth = 88,
                MinHeight = 44,
                IsEnabled = !state.IsBusy,
                IsDefault = choice.IsSafestChoice,
            };
            OrbitVisualTheme.ApplyButton(
                button,
                choice.IsSafestChoice ? OrbitButtonRole.Quiet : OrbitButtonRole.Primary);
            AutomationProperties.SetName(button, label);
            AutomationProperties.SetHelpText(button, PermissionChoiceHelpText(choiceCopy));
            if (choice.IsSafestChoice)
            {
                safestAction = button;
            }
            button.Click += async (_, _) =>
            {
                var presenter = permissionPresenter;
                var current = presenter?.State;
                if (!choices.IsEnabled || presenter is null ||
                    current?.Prompt?.RequestId != state.Prompt.RequestId ||
                    current.Prompt.ResponseToken != state.Prompt.ResponseToken)
                {
                    return;
                }

                choices.IsEnabled = false;
                permissionSubmissionRequestId = state.Prompt.RequestId;
                AutomationProperties.SetItemStatus(permissionSurface, "Applying permission choice");
                var busy = new TextBlock
                {
                    Text = PresentationTextCatalog.Resolve("ui.permission.applying"),
                    Margin = new Thickness(2, 6, 2, 0),
                };
                AutomationProperties.SetName(busy, "Permission choice is being applied");
                AutomationProperties.SetLiveSetting(busy, AutomationLiveSetting.Polite);
                permissionLayout.Children.Add(busy);
                await presenter.RespondAsync(choiceCopy.Decision, choiceCopy.AllowScope);
            };
            choices.Children.Add(button);
        }

        permissionLayout.Children.Add(choices);
        if (state.IsBusy)
        {
            var busy = new TextBlock
            {
                Text = PresentationTextCatalog.Resolve("ui.permission.applying"),
                Margin = new Thickness(2, 6, 2, 0),
            };
            AutomationProperties.SetName(busy, "Permission choice is being applied");
            AutomationProperties.SetLiveSetting(busy, AutomationLiveSetting.Polite);
            permissionLayout.Children.Add(busy);
            AutomationProperties.SetItemStatus(permissionSurface, "Applying permission choice");
        }
        else
        {
            AutomationProperties.SetItemStatus(permissionSurface, "Awaiting permission choice");
        }
        if (state.Failure is not null)
        {
            var error = new TextBlock
            {
                Text = PresentationTextCatalog.ResolvePermissionFailure(state.Failure.MessageKey),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
            };
            AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Assertive);
            permissionLayout.Children.Add(error);
        }

        var wasVisible = permissionSurface.Visibility == Visibility.Visible;
        permissionSurface.Visibility = Visibility.Visible;
        if (!wasVisible)
        {
            OrbitMotion.Reveal(permissionSurface, ReducedMotion);
        }
        if (permissionSubmissionRequestId is not null && !state.IsBusy)
        {
            permissionSubmissionRequestId = null;
            if (safestAction is not null)
            {
                Dispatcher.BeginInvoke(() => safestAction.Focus());
            }
        }
    }

    private static string PermissionChoiceHelpText(PermissionChoiceViewState choice) => choice switch
    {
        { IsSafestChoice: true } => "Safest choice. The site remains blocked.",
        { AllowScope: PermissionAllowScope.Once } =>
            "Allows this use once. The site can ask again later.",
        { AllowScope: PermissionAllowScope.Session } =>
            "Allows this use for the current browsing session.",
        { AllowScope: PermissionAllowScope.Persistent } =>
            "Allows this use on future visits until you change the site permission.",
        _ => "Applies this permission choice.",
    };

    private void UpdateNavigationAvailability()
    {
        if (browserState?.SelectedTabId is not { } selected)
        {
            backButton.IsEnabled = false;
            forwardButton.IsEnabled = false;
            reloadButton.IsEnabled = false;
            homeButton.IsEnabled = false;
            return;
        }

        var tab = browserState.Tabs.FirstOrDefault(item => item.TabId == selected);
        backButton.IsEnabled = tab?.CanGoBack == true;
        forwardButton.IsEnabled = tab?.CanGoForward == true;
        reloadButton.IsEnabled = tab is not null;
        reloadButton.Content = Icon(
            tab?.LoadState == BrowserLoadState.Loading ? OrbitIconKind.Stop : OrbitIconKind.Reload,
            19);
        AutomationProperties.SetName(
            reloadButton,
            tab?.LoadState == BrowserLoadState.Loading ? "Stop loading" : "Reload page");
        homeButton.IsEnabled = tab is not null;
        if (tab?.Address is not null && !omnibox.IsKeyboardFocusWithin)
        {
            omnibox.Text = tab.Address.AbsoluteUri;
        }
        else if (tab?.Address is null && !omnibox.IsKeyboardFocusWithin)
        {
            omnibox.Text = string.Empty;
        }

        SetPrivateMode(tab?.IsPrivate ?? browsingContext?.Privacy.IsPrivate == true);
    }

    private void RequestBack()
    {
        if (browserState?.SelectedTabId is { } tabId && backButton.IsEnabled)
        {
            Raise(new GoBackBrowserCommand(browserState.WindowId, tabId));
        }
    }

    private void RequestForward()
    {
        if (browserState?.SelectedTabId is { } tabId && forwardButton.IsEnabled)
        {
            Raise(new GoForwardBrowserCommand(browserState.WindowId, tabId));
        }
    }

    private void RequestReloadOrStop()
    {
        if (browserState?.SelectedTabId is not { } tabId)
        {
            return;
        }

        var selected = browserState.Tabs.FirstOrDefault(tab => tab.TabId == tabId);
        Raise(selected?.LoadState == BrowserLoadState.Loading
            ? new StopBrowserCommand(browserState.WindowId, tabId)
            : new ReloadBrowserCommand(browserState.WindowId, tabId));
    }

    private void RequestHome()
    {
        if (browserState?.SelectedTabId is { } tabId)
        {
            Raise(new NavigateBrowserCommand(browserState.WindowId, tabId, HomeUri));
        }
    }

    private void RequestNewTab()
    {
        if (browserState is not null)
        {
            Raise(TabCommandPlanner.CreateNewTab(browserState.WindowId));
        }
    }

    private void RequestPrivateWindow()
    {
        if (browsingContext is null)
        {
            Announce("A browsing window must be ready before opening a private window.");
            return;
        }

        try
        {
            if (privateWindowRequested is null)
            {
                Announce("Private windows are unavailable until the browser host is ready.");
                return;
            }

            privateWindowRequested.Invoke(this, PrivateWindowRequestPlanner.Create(browsingContext));
        }
        catch (ArgumentException)
        {
            Announce("Open a new private window from a normal window.");
        }
    }

    private void UpdatePrivateWindowAvailability()
    {
        var isPrivate = browsingContext?.Privacy.IsPrivate == true;
        privateWindowButton.IsEnabled = browsingContext is not null && !isPrivate && privateWindowRequested is not null;
        AutomationProperties.SetHelpText(
            privateWindowButton,
            isPrivate
                ? "A private window cannot open another private window."
                : privateWindowRequested is null
                    ? "Private windows are unavailable until the browser host is ready."
                    : "Open a new isolated private window.");
    }

    private void RequestCloseTab(BrowserTabId tabId)
    {
        if (browserState is null || tabStrip is null)
        {
            return;
        }

        beforeClose = tabStrip;
        pendingClosedTabId = tabId;
        Raise(TabCommandPlanner.Close(browserState, tabId));
    }

    private void SelectAdjacentTab(int direction)
    {
        if (browserState?.SelectedTabId is not { } selected || browserState.Tabs.Count < 2)
        {
            return;
        }

        var index = browserState.Tabs.ToList().FindIndex(tab => tab.TabId == selected);
        var next = (index + direction + browserState.Tabs.Count) % browserState.Tabs.Count;
        Raise(TabCommandPlanner.Select(browserState, browserState.Tabs[next].TabId));
    }

    private void AddNewTabButton()
    {
        var newTab = new Button
        {
            Content = Icon(OrbitIconKind.Add, 18),
            Margin = new Thickness(2),
            MinHeight = 40,
            MinWidth = 44,
            ToolTip = "New tab (Ctrl+T)",
        };
        OrbitVisualTheme.ApplyButton(newTab, OrbitButtonRole.Toolbar);
        AutomationProperties.SetName(newTab, "Open new tab");
        AutomationProperties.SetHelpText(newTab, "Creates a new tab. Keyboard shortcut: Control+T.");
        newTab.Click += (_, _) => RequestNewTab();
        tabRow.Children.Add(newTab);
    }

    private ContextMenu CreateTabContextMenu(BrowserTabEntry tab)
    {
        var menu = new ContextMenu();
        menu.Items.Add(CreateMenuItem(
            "Move tab left",
            () => MoveTab(tab, -1),
            canExecute: () => BrowserCommandRequested is not null,
            unavailableReason: "Tab movement is unavailable until the browser host is ready."));
        menu.Items.Add(CreateMenuItem(
            "Move tab right",
            () => MoveTab(tab, 1),
            canExecute: () => BrowserCommandRequested is not null,
            unavailableReason: "Tab movement is unavailable until the browser host is ready."));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem(
            "Create new tab group",
            () => RequestTabGroupCreate([tab.TabId], TabGroupCreationMethod.ContextMenu),
            canExecute: () => TabGroupCreateRequested is not null,
            unavailableReason: "Tab groups are unavailable until the workspace is ready."));
        if (browserState?.SelectedTabId is { } selected && selected != tab.TabId)
        {
            menu.Items.Add(CreateMenuItem(
                "Group with selected tab",
                () => RequestTabGroupCreate([selected, tab.TabId], TabGroupCreationMethod.ContextMenu),
                canExecute: () => TabGroupCreateRequested is not null,
                unavailableReason: "Tab groups are unavailable until the workspace is ready."));
        }

        if (tab.GroupId is not null)
        {
            menu.Items.Add(CreateMenuItem(
                "Remove tab from group",
                () => MoveTabToGroup(tab, null),
                canExecute: () => BrowserCommandRequested is not null,
                unavailableReason: "Tab movement is unavailable until the browser host is ready."));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem(
            "Close tab",
            () => RequestCloseTab(tab.TabId),
            canExecute: () => BrowserCommandRequested is not null,
            unavailableReason: "Tab commands are unavailable until the browser host is ready."));
        menu.Opened += (_, _) => RefreshMenuAvailability(menu);
        OrbitVisualTheme.ApplyContextMenu(menu);
        return menu;
    }

    private ContextMenu CreateGroupContextMenu(TabGroupHeaderEntry group)
    {
        var menu = new ContextMenu();
        menu.Items.Add(CreateMenuItem(
            group.IsCollapsed ? "Expand group" : "Collapse group",
            () => TabGroupToggleRequested?.Invoke(
                this,
                new TabGroupToggleRequestedEventArgs(group.GroupId, !group.IsCollapsed)),
            canExecute: () => TabGroupToggleRequested is not null,
            unavailableReason: "Group state is unavailable until the workspace is ready."));
        menu.Items.Add(CreateMenuItem(
            "Rename group",
            () => ShowGroupRenameDialog(group),
            canExecute: () => TabGroupRenameRequested is not null,
            unavailableReason: "Group editing is unavailable until the workspace is ready."));
        menu.Items.Add(CreateMenuItem(
            "Ungroup tabs",
            () => TabGroupUngroupRequested?.Invoke(
                this,
                new TabGroupUngroupRequestedEventArgs(group.GroupId)),
            canExecute: () => TabGroupUngroupRequested is not null,
            unavailableReason: "Group editing is unavailable until the workspace is ready."));
        menu.Opened += (_, _) => RefreshMenuAvailability(menu);
        OrbitVisualTheme.ApplyContextMenu(menu);
        return menu;
    }

    private void ShowGroupRenameDialog(TabGroupHeaderEntry group)
    {
        var dialog = new TabGroupRenameDialog(
            group.GroupId,
            group.Name,
            browsingContext?.Privacy.IsPrivate == true);
        if (Window.GetWindow(this) is { } owner)
        {
            dialog.Owner = owner;
        }

        if (dialog.ShowDialog() == true && dialog.Draft is { } draft)
        {
            TabGroupRenameRequested?.Invoke(
                this,
                new TabGroupRenameRequestedEventArgs(draft.GroupId, draft.RequestedName));
        }

        if (groupButtons.TryGetValue(group.GroupId, out var header))
        {
            header.Focus();
        }
    }

    private ContextMenu CreateBrowserMenu(Button placementTarget)
    {
        var menu = new ContextMenu { PlacementTarget = placementTarget };
        menu.Items.Add(CreateMenuItem(
            "New tab",
            RequestNewTab,
            OrbitIconKind.Add,
            () => BrowserCommandRequested is not null,
            "Tab commands are unavailable until the browser host is ready."));
        menu.Items.Add(CreateMenuItem(
            "New private window",
            RequestPrivateWindow,
            OrbitIconKind.Private,
            () => browsingContext is not null && !browsingContext.Privacy.IsPrivate && privateWindowRequested is not null,
            "Private windows are unavailable in the current state."));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Bookmarks", () => RequestSurface(UtilityDrawerKind.Bookmarks), OrbitIconKind.Bookmark,
            () => IsUtilityAvailable(UtilityDrawerKind.Bookmarks),
            () => GetUtilityUnavailableReason(UtilityDrawerKind.Bookmarks)));
        menu.Items.Add(CreateMenuItem("History", () => RequestSurface(UtilityDrawerKind.History), OrbitIconKind.History,
            () => IsUtilityAvailable(UtilityDrawerKind.History),
            () => GetUtilityUnavailableReason(UtilityDrawerKind.History)));
        menu.Items.Add(CreateMenuItem("Downloads", () => RequestSurface(UtilityDrawerKind.Downloads), OrbitIconKind.Download,
            () => IsUtilityAvailable(UtilityDrawerKind.Downloads),
            () => GetUtilityUnavailableReason(UtilityDrawerKind.Downloads)));
        menu.Items.Add(CreateMenuItem("Clipboard Shelf", () => RequestSurface(UtilityDrawerKind.ClipboardShelf), OrbitIconKind.Clipboard,
            () => IsUtilityAvailable(UtilityDrawerKind.ClipboardShelf),
            () => GetUtilityUnavailableReason(UtilityDrawerKind.ClipboardShelf)));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem(
            "Save for offline",
            () => RequestOfflineReading(new SavePageForOfflineAction(Guid.NewGuid(), offlineReading.Revision)),
            OrbitIconKind.Download,
            () => OfflineReadingActionRequested is not null && offlineReading.CanSaveCurrentPage,
            () => OfflineReadingUnavailableReason(forSave: true)));
        menu.Items.Add(CreateMenuItem(
            "Offline library",
            () => RequestOfflineReading(new OpenOfflineLibraryAction(Guid.NewGuid(), offlineReading.Revision)),
            OrbitIconKind.Bookmark,
            () => OfflineReadingActionRequested is not null && !offlineReading.IsPrivate,
            () => OfflineReadingUnavailableReason(forSave: false)));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Settings", () => RequestSurface(InternalPageKind.Settings), OrbitIconKind.Settings,
            () => IsUtilityAvailable(InternalPageKind.Settings),
            () => GetUtilityUnavailableReason(InternalPageKind.Settings)));
        RefreshMenuAvailability(menu);
        OrbitVisualTheme.ApplyContextMenu(menu);
        return menu;
    }

    private MenuItem CreateMenuItem(
        string label,
        Action action,
        OrbitIconKind? iconKind = null,
        Func<bool>? canExecute = null,
        string unavailableReason = "This command is currently unavailable.") =>
        CreateMenuItemCore(label, action, iconKind, canExecute, () => unavailableReason);

    private MenuItem CreateMenuItem(
        string label,
        Action action,
        OrbitIconKind? iconKind,
        Func<bool> canExecute,
        Func<string> unavailableReason) =>
        CreateMenuItemCore(label, action, iconKind, canExecute, unavailableReason);

    private MenuItem CreateMenuItemCore(
        string label,
        Action action,
        OrbitIconKind? iconKind,
        Func<bool>? canExecute,
        Func<string> unavailableReason)
    {
        object header = label;
        if (iconKind is { } kind)
        {
            header = IconLabel(kind, label);
        }

        var item = new MenuItem
        {
            Header = header,
            Tag = new MenuRouteAvailability(label, iconKind, canExecute ?? (() => true), unavailableReason),
        };
        item.Click += (_, _) =>
        {
            var route = (MenuRouteAvailability)item.Tag;
            if (route.CanExecute())
            {
                action();
            }
            else
            {
                Announce(route.UnavailableReason());
            }
        };
        RefreshMenuItemAvailability(item);
        return item;
    }

    private void RefreshMenuAvailability(ContextMenu menu)
    {
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            RefreshMenuItemAvailability(item);
        }
    }

    private static void RefreshMenuItemAvailability(MenuItem item)
    {
        if (item.Tag is not MenuRouteAvailability route)
        {
            return;
        }

        var available = route.CanExecute();
        var unavailableReason = route.UnavailableReason();
        var visibleLabel = available ? route.Label : $"{route.Label} — unavailable";
        item.IsEnabled = available;
        item.Header = route.IconKind is { } kind ? IconLabel(kind, visibleLabel) : visibleLabel;
        item.ToolTip = available ? null : unavailableReason;
        AutomationProperties.SetName(item, available ? route.Label : $"{route.Label}, unavailable");
        AutomationProperties.SetHelpText(item, available ? string.Empty : unavailableReason);
    }

    private void OpenBrowserMenu(Button placementTarget)
    {
        var menu = CreateBrowserMenu(placementTarget);
        activeBrowserMenu = menu;
        placementTarget.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private bool IsUtilityAvailable(UtilityDrawerKind drawer) =>
        UtilitySurfaceRequested is not null &&
        drawerAvailability.TryGetValue(drawer, out var availability) &&
        availability.Available;

    private bool IsUtilityAvailable(InternalPageKind page) =>
        UtilitySurfaceRequested is not null &&
        pageAvailability.TryGetValue(page, out var availability) &&
        availability.Available;

    private string GetUtilityUnavailableReason(UtilityDrawerKind drawer) =>
        UtilitySurfaceRequested is null
            ? "This browser surface is unavailable until its host route is connected."
            : drawerAvailability.TryGetValue(drawer, out var availability)
                ? availability.UnavailableReason
                : "This browser surface is not available in the current version.";

    private string GetUtilityUnavailableReason(InternalPageKind page) =>
        UtilitySurfaceRequested is null
            ? "This browser page is unavailable until its host route is connected."
            : pageAvailability.TryGetValue(page, out var availability)
                ? availability.UnavailableReason
                : "This browser page is not available in the current version.";

    private void RequestSurface(UtilityDrawerKind drawer) =>
        UtilitySurfaceRequested?.Invoke(
            this,
            UtilitySurfaceRequestedEventArgs.ForDrawer(drawer));

    private void RequestSurface(InternalPageKind page) =>
        UtilitySurfaceRequested?.Invoke(
            this,
            UtilitySurfaceRequestedEventArgs.ForPage(page));

    private void RequestOfflineReading(OfflineReadingAction action) =>
        OfflineReadingActionRequested?.Invoke(this, new(action));

    private string OfflineReadingUnavailableReason(bool forSave)
    {
        if (offlineReading.IsPrivate)
        {
            return "Offline reading is unavailable in private browsing.";
        }
        if (OfflineReadingActionRequested is null)
        {
            return "Offline reading is unavailable until its local host is connected.";
        }
        return forSave && !offlineReading.CanSaveCurrentPage
            ? offlineReading.SafeStatusMessage
            : "The offline library is unavailable.";
    }

    private void MoveTab(BrowserTabEntry tab, int direction)
    {
        if (browserState is null)
        {
            return;
        }

        var index = browserState.Tabs.ToList().FindIndex(item => item.TabId == tab.TabId);
        if (index >= 0)
        {
            Raise(TabCommandPlanner.Move(browserState, tab.TabId, index + direction, tab.GroupId));
        }
    }

    private void MoveTabToGroup(BrowserTabEntry tab, BrowserTabGroupId? groupId)
    {
        if (browserState is null)
        {
            return;
        }

        var index = browserState.Tabs.ToList().FindIndex(item => item.TabId == tab.TabId);
        if (index >= 0)
        {
            Raise(TabCommandPlanner.Move(browserState, tab.TabId, index, groupId));
        }
    }

    private void BeginTabDrag(Button button, BrowserTabEntry tab, MouseEventArgs args)
    {
        if (args.LeftButton != MouseButtonState.Pressed || dragSourceTabId != tab.TabId)
        {
            return;
        }

        var current = args.GetPosition(this);
        if (Math.Abs(current.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        DragDrop.DoDragDrop(button, tab.TabId.Value.ToString("D"), DragDropEffects.Move);
    }

    private void OnTabDragEnter(Button button, BrowserTabEntry tab, DragEventArgs args)
    {
        if (dragSourceTabId is not null && dragSourceTabId != tab.TabId)
        {
            args.Effects = DragDropEffects.Move;
            ApplyTabVisual(button, tab.IsSelected, true);
            args.Handled = true;
        }
    }

    private void OnTabDrop(Button button, BrowserTabEntry tab, DragEventArgs args)
    {
        ApplyTabVisual(button, tab.IsSelected, false);
        if (dragSourceTabId is { } source && source != tab.TabId)
        {
            RequestTabGroupCreate([source, tab.TabId], TabGroupCreationMethod.Drag);
            args.Handled = true;
        }

        dragSourceTabId = null;
    }

    private void RequestTabGroupCreate(
        IReadOnlyList<BrowserTabId> tabIds,
        TabGroupCreationMethod method)
    {
        if (browserState is null)
        {
            return;
        }

        var valid = tabIds
            .Distinct()
            .Where(id => browserState.Tabs.Any(tab => tab.TabId == id))
            .ToArray();
        if (valid.Length == 0)
        {
            return;
        }

        TabGroupCreateRequested?.Invoke(
            this,
            new TabGroupCreateRequestedEventArgs(valid, "Tab group", method));
    }

    private void ApplyTabVisual(Button button, bool isSelected, bool isDragTarget)
    {
        if (SystemParameters.HighContrast)
        {
            button.Background = isSelected ? SystemColors.HighlightBrush : SystemColors.ControlBrush;
            button.Foreground = isSelected ? SystemColors.HighlightTextBrush : SystemColors.ControlTextBrush;
            button.BorderBrush = isDragTarget ? SystemColors.HighlightBrush : SystemColors.ControlTextBrush;
        }
        else
        {
            button.Background = isSelected ? OrbitVisualTheme.SeaGlassStrong : OrbitVisualTheme.Surface;
            button.Foreground = OrbitVisualTheme.Ink;
            button.BorderBrush = isDragTarget ? OrbitVisualTheme.Focus : isSelected
                ? OrbitVisualTheme.SeaGlass
                : OrbitVisualTheme.Divider;
        }

        button.BorderThickness = isDragTarget ? new Thickness(3) : new Thickness(1);
    }

    private void ApplyPalette()
    {
        if (SystemParameters.HighContrast)
        {
            Background = SystemColors.WindowBrush;
            tabSurface.Background = SystemColors.ControlBrush;
            tabSurface.BorderBrush = SystemColors.WindowTextBrush;
            sideTabPanelResizeHandle.Background = SystemColors.WindowTextBrush;
            toolbarSurface.Background = SystemColors.WindowBrush;
            toolbarSurface.BorderBrush = SystemColors.WindowTextBrush;
            toolbar.Background = SystemColors.WindowBrush;
            permissionSurface.Background = SystemColors.WindowBrush;
            permissionSurface.BorderBrush = SystemColors.WindowTextBrush;
            privateIndicator.Background = SystemColors.HighlightBrush;
            privateIndicatorText.Foreground = SystemColors.HighlightTextBrush;
        }
        else
        {
            Background = OrbitVisualTheme.Canvas;
            tabSurface.Background = OrbitVisualTheme.Chrome;
            tabSurface.BorderBrush = OrbitVisualTheme.Divider;
            sideTabPanelResizeHandle.Background = OrbitVisualTheme.SeaGlassStrong;
            toolbarSurface.Background = OrbitVisualTheme.Chrome;
            toolbarSurface.BorderBrush = OrbitVisualTheme.Divider;
            toolbar.Background = Brushes.Transparent;
            permissionSurface.Background = OrbitVisualTheme.Surface;
            permissionSurface.BorderBrush = OrbitVisualTheme.SeaGlass;
            privateIndicator.Background = OrbitVisualTheme.PrivateViolet;
            privateIndicatorText.Foreground = OrbitVisualTheme.Ink;
        }

        OrbitVisualTheme.ApplyButton(backButton, OrbitButtonRole.Toolbar);
        OrbitVisualTheme.ApplyButton(forwardButton, OrbitButtonRole.Toolbar);
        OrbitVisualTheme.ApplyButton(reloadButton, OrbitButtonRole.Toolbar);
        OrbitVisualTheme.ApplyButton(homeButton, OrbitButtonRole.Toolbar);
        OrbitVisualTheme.ApplyButton(privateWindowButton, OrbitButtonRole.Private);
        OrbitVisualTheme.ApplyButton(collapseModeButton, OrbitButtonRole.Toolbar);
        OrbitVisualTheme.ApplyButton(compactTabsButton, OrbitButtonRole.Toolbar);
        OrbitVisualTheme.ApplyButton(tabLayoutButton, OrbitButtonRole.Toolbar);
        OrbitVisualTheme.ApplyTextBox(omnibox);
    }

    private static FrameworkElement CreateTabContent(BrowserTabEntry tab)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var waypoint = new Ellipse
        {
            Width = 7,
            Height = 7,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Fill = SystemParameters.HighContrast
                ? SystemColors.HighlightBrush
                : tab.IsPrivate
                    ? OrbitVisualTheme.PrivateViolet
                    : tab.LoadState == BrowserLoadState.Loading
                        ? OrbitVisualTheme.WaypointGold
                        : OrbitVisualTheme.SeaGlass,
        };
        if (tab.IsCompact)
        {
            waypoint.Margin = new Thickness(0);
            waypoint.Width = 10;
            waypoint.Height = 10;
            row.Children.Add(waypoint);
            return row;
        }

        var title = new TextBlock
        {
            Text = tab.Title.Equals("New tab", StringComparison.OrdinalIgnoreCase) ? "New Tab" : tab.Title,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 176,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(waypoint);
        row.Children.Add(title);
        return row;
    }

    private FrameworkElement GroupHeaderContent(TabGroupHeaderEntry header)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new OrbitEmberStar(OrbitEmberStarKind.TabGroup)
        {
            IsActive = !header.IsCollapsed,
            ReducedMotion = ReducedMotion,
            MotionEnabled = workspacePreferences.ShowOrbitalGroupPreview,
            Width = header.IsCompact ? 20 : 22,
            Height = header.IsCompact ? 20 : 22,
            Margin = header.IsCompact ? new Thickness(0) : new Thickness(0, 0, 6, 0),
        });
        if (header.IsCompact)
        {
            return row;
        }

        row.Children.Add(Icon(
            header.IsCollapsed ? OrbitIconKind.ChevronRight : OrbitIconKind.ChevronDown,
            15));
        row.Children.Add(new Ellipse
        {
            Width = 7,
            Height = 7,
            Margin = new Thickness(7, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Fill = SystemParameters.HighContrast ? SystemColors.HighlightBrush : OrbitVisualTheme.WaypointGold,
        });
        row.Children.Add(new TextBlock
        {
            Text = header.Name,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var count = new Border
        {
            Background = SystemParameters.HighContrast ? SystemColors.ControlBrush : OrbitVisualTheme.Canvas,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(7, 0, 0, 0),
            Child = new TextBlock
            {
                Text = header.TabCount.ToString(System.Globalization.CultureInfo.CurrentCulture),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        row.Children.Add(count);
        return row;
    }

    private static StackPanel IconLabel(OrbitIconKind kind, string label)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Icon(kind, 18));
        row.Children.Add(new TextBlock
        {
            Text = label,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        return row;
    }

    private static OrbitIcon Icon(OrbitIconKind kind, double size)
    {
        var icon = new OrbitIcon
        {
            Kind = kind,
            Width = size,
            Height = size,
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.BindStrokeToAncestorForeground();
        return icon;
    }

    private static OrbitControllerGlyph PlacementGlyph(TabStripPlacement placement)
    {
        var glyph = new OrbitControllerGlyph
        {
            Kind = placement switch
            {
                TabStripPlacement.Left => OrbitControllerGlyphKind.PlacementLeft,
                TabStripPlacement.Right => OrbitControllerGlyphKind.PlacementRight,
                _ => OrbitControllerGlyphKind.PlacementTop,
            },
            Width = 18,
            Height = 18,
            VerticalAlignment = VerticalAlignment.Center,
        };
        glyph.BindStrokeToAncestorForeground();
        return glyph;
    }

    private void Announce(string message)
    {
        statusAnnouncer.Text = string.Empty;
        statusAnnouncer.Text = message;
    }

    private void Raise(BrowserCommand command) => BrowserCommandRequested?.Invoke(this, command);

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (!systemParameterEventsAttached)
        {
            SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
            systemParameterEventsAttached = true;
        }

        ApplyPalette();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (systemParameterEventsAttached)
        {
            SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
            systemParameterEventsAttached = false;
        }
    }

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(SystemParameters.HighContrast) or null)
        {
            ApplyPalette();
            if (browserState is not null)
            {
                RenderTabs(browserState, groupPresentations);
            }
        }
    }

    private sealed record MenuRouteAvailability(
        string Label,
        OrbitIconKind? IconKind,
        Func<bool> CanExecute,
        Func<string> UnavailableReason);

    private sealed record UtilityRouteAvailability(bool Available, string UnavailableReason)
    {
        public static UtilityRouteAvailability Create(bool available, string unavailableReason)
        {
            if (!available)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(unavailableReason);
            }

            return new UtilityRouteAvailability(
                available,
                available ? string.Empty : unavailableReason.Trim());
        }

        public static UtilityRouteAvailability Unavailable(string reason) => Create(false, reason);
    }
}

public enum TabGroupCreationMethod
{
    ContextMenu = 0,
    Drag = 1,
}

public sealed class TabGroupToggleRequestedEventArgs : EventArgs
{
    public TabGroupToggleRequestedEventArgs(BrowserTabGroupId groupId, bool isCollapsed)
    {
        GroupId = groupId;
        IsCollapsed = isCollapsed;
    }

    public BrowserTabGroupId GroupId { get; }

    public bool IsCollapsed { get; }
}

public sealed class TabGroupCreateRequestedEventArgs : EventArgs
{
    public TabGroupCreateRequestedEventArgs(
        IReadOnlyList<BrowserTabId> tabIds,
        string suggestedName,
        TabGroupCreationMethod method)
    {
        ArgumentNullException.ThrowIfNull(tabIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestedName);
        if (tabIds.Count == 0 || tabIds.Any(id => id.IsEmpty))
        {
            throw new ArgumentException("At least one valid tab is required.", nameof(tabIds));
        }

        TabIds = tabIds.Distinct().ToArray();
        SuggestedName = suggestedName;
        Method = method;
    }

    public IReadOnlyList<BrowserTabId> TabIds { get; }

    public string SuggestedName { get; }

    public TabGroupCreationMethod Method { get; }
}

public sealed class TabGroupRenameRequestedEventArgs : EventArgs
{
    public TabGroupRenameRequestedEventArgs(BrowserTabGroupId groupId, string requestedName)
    {
        if (groupId.IsEmpty)
        {
            throw new ArgumentException("A group ID is required.", nameof(groupId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(requestedName);
        var normalized = requestedName.Trim();
        if (normalized.Length > TabGroupPresentationCatalog.MaximumNameLength)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedName));
        }

        GroupId = groupId;
        RequestedName = normalized;
    }

    public BrowserTabGroupId GroupId { get; }

    public string RequestedName { get; }
}

public sealed class TabGroupUngroupRequestedEventArgs : EventArgs
{
    public TabGroupUngroupRequestedEventArgs(BrowserTabGroupId groupId)
    {
        if (groupId.IsEmpty)
        {
            throw new ArgumentException("A group ID is required.", nameof(groupId));
        }

        GroupId = groupId;
    }

    public BrowserTabGroupId GroupId { get; }
}

public sealed class UtilitySurfaceRequestedEventArgs : EventArgs
{
    private UtilitySurfaceRequestedEventArgs(
        UtilityDrawerKind drawer,
        InternalPageKind page)
    {
        Drawer = drawer;
        Page = page;
    }

    public UtilityDrawerKind Drawer { get; }

    public InternalPageKind Page { get; }

    public static UtilitySurfaceRequestedEventArgs ForDrawer(UtilityDrawerKind drawer)
    {
        if (!Enum.IsDefined(drawer) || drawer == UtilityDrawerKind.None)
        {
            throw new ArgumentOutOfRangeException(nameof(drawer));
        }

        return new UtilitySurfaceRequestedEventArgs(drawer, InternalPageKind.None);
    }

    public static UtilitySurfaceRequestedEventArgs ForPage(InternalPageKind page)
    {
        if (!Enum.IsDefined(page) || page == InternalPageKind.None)
        {
            throw new ArgumentOutOfRangeException(nameof(page));
        }

        return new UtilitySurfaceRequestedEventArgs(UtilityDrawerKind.None, page);
    }
}

public sealed record CompactTabModeChangeRequestedEventArgs(bool IsCompactModeRequested);
#endif
