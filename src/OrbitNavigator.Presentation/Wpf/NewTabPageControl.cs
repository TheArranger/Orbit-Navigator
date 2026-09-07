#if ORBIT_WPF
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Presentation.Navigation;
using OrbitNavigator.Presentation.Workspace;

namespace OrbitNavigator.Presentation.Wpf;

/// <summary>Content-first DuckDuckGo search and real profile-backed quick-launch workspace.</summary>
public sealed class NewTabPageControl : Grid
{
    public const int AnimationFramesPerSecond = NewTabDecorationControl.SharedAnimationFramesPerSecond;
    public const int AnimationLoopFrameCount = AnimationFramesPerSecond * 8;

    private readonly NewTabDecorationControl decoration = new();
    private readonly OrbitNewTabLogo mark = new()
    {
        Width = 72,
        Height = 72,
        HorizontalAlignment = HorizontalAlignment.Center,
    };
    private readonly StellarHubControl bookmarkHub = new(StellarHubKind.Bookmarks);
    private readonly StellarHubControl workspaceHub = new(StellarHubKind.Workspaces);
    private readonly AffiliatedSitesControl affiliatedSites = new()
    {
        Margin = new Thickness(0, 18, 0, 0),
    };
    private readonly Button bookmarkViewToggle = new() { Content = "Show list view", MinHeight = 36, Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button workspaceViewToggle = new() { Content = "Show list view", MinHeight = 36, Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button visualModeToggle = new() { MinHeight = 36, Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBox searchBox = new()
    {
        Height = 48,
        MinWidth = 320,
        VerticalContentAlignment = VerticalAlignment.Center,
    };
    private readonly Button searchButton;
    private readonly Button donationButton = new()
    {
        Content = "Support Orbit Navigator",
        HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(0, 24, 0, 0),
    };
    private readonly Button manageBookmarksButton = new()
    {
        Content = "Manage bookmarks",
        MinHeight = 36,
        Margin = new Thickness(8, 0, 0, 0),
    };
    private readonly Button createWorkspaceButton = new()
    {
        Content = "Create workspace",
        MinHeight = 36,
        Margin = new Thickness(8, 0, 0, 0),
    };
    private readonly Button emptyManageButton = new() { Content = "Open bookmarks", Margin = new Thickness(4) };
    private readonly Button emptyCreateButton = new() { Content = "Create a workspace", Margin = new Thickness(4) };
    private readonly Border searchSurface = OrbitVisualTheme.CreateSurface(12);
    private readonly Border workspaceSurface = OrbitVisualTheme.CreateSurface(18);
    private readonly WrapPanel bookmarkTiles = new() { Orientation = Orientation.Horizontal };
    private readonly WrapPanel workspaceTiles = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel emptyState = new();
    private readonly StackPanel pageContent = new()
    {
        MaxWidth = 980,
        HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(32, 24, 32, 36),
    };
    private readonly DockPanel pageShell = new() { LastChildFill = true };
    private readonly TextBlock privateStatus = new()
    {
        Text = "PRIVATE BROWSING  ·  local session only  ·  sync and saved workspace changes stay off",
        FontWeight = FontWeights.SemiBold,
        HorizontalAlignment = HorizontalAlignment.Center,
        TextAlignment = TextAlignment.Center,
    };
    private readonly Border privateStatusSurface = new()
    {
        CornerRadius = new CornerRadius(14),
        Padding = new Thickness(18, 8, 18, 8),
        Margin = new Thickness(0, 8, 0, 10),
        HorizontalAlignment = HorizontalAlignment.Center,
        Visibility = Visibility.Collapsed,
    };
    private readonly Border visualModeSurface = OrbitVisualTheme.CreateSurface(12);
    private readonly TextBlock workspaceStatus = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 10, 0, 0),
        Visibility = Visibility.Collapsed,
    };
    private readonly OmniboxTargetResolver resolver = new();
    private NewTabWorkspaceData workspaceData = NewTabWorkspaceData.Empty;
    private IReadOnlyDictionary<WorkspacePresetPresentationId, ImageSource> workspaceArtworkSources =
        new Dictionary<WorkspacePresetPresentationId, ImageSource>();
    private bool reducedMotion;
    private bool canModifyWorkspace = true;
    private bool hostCanModifyWorkspace = true;
    private bool bookmarkListView;
    private bool workspaceListView;
    private bool reducedVisualNoise;
    private bool systemParameterEventsAttached;
    private bool isPrivateMode;
    private BrowserWorkspacePreferences preferences = BrowserWorkspacePreferences.Default;

    public NewTabPageControl()
    {
        Background = OrbitVisualTheme.Canvas;
        affiliatedSites.IsRailMode = true;
        affiliatedSites.ShowInlineVisibilityControl = false;
        AutomationProperties.SetName(this, "New tab workspace");
        searchButton = new Button
        {
            Content = Icon(OrbitIconKind.Search, 20),
            Width = 44,
            Height = 44,
            Margin = new Thickness(6, 2, 2, 2),
            ToolTip = "Search with DuckDuckGo",
        };
        AutomationProperties.SetName(searchButton, "Search with DuckDuckGo");
        searchButton.Click += (_, _) => Submit();
        bookmarkHub.ItemActivated += (_, args) =>
        {
            if (int.TryParse(args.Key, out var index) && index >= 0 && index < workspaceData.Bookmarks.Count)
                BookmarkLaunchRequested?.Invoke(this, new(workspaceData.Bookmarks[index]));
        };
        workspaceHub.ItemActivated += (_, args) =>
        {
            if (int.TryParse(args.Key, out var index) && index >= 0 && index < workspaceData.Presets.Count)
                OpenWorkspaceRequested?.Invoke(this, new(workspaceData.Presets[index]));
        };
        bookmarkHub.DetailVisibilityChanged += OnHubDetailVisibilityChanged;
        workspaceHub.DetailVisibilityChanged += OnHubDetailVisibilityChanged;
        affiliatedSites.LaunchRequested += (_, args) =>
            AffiliatedSiteLaunchRequested?.Invoke(this, args);
        affiliatedSites.VisibilityChangeRequested += (_, args) =>
            AffiliatedSitesVisibilityChangeRequested?.Invoke(this, args);
        affiliatedSites.Apply(
            AffiliatedSitesCatalogPresentation.ApprovedV1,
            new AffiliatedSitesVisibilityPresentation(
                false,
                0,
                false,
                "Affiliated Sites visibility preferences are unavailable until the profile is ready."));
        bookmarkViewToggle.Click += (_, _) => { bookmarkListView = !bookmarkListView; UpdateHubModes(); };
        workspaceViewToggle.Click += (_, _) => { workspaceListView = !workspaceListView; UpdateHubModes(); };
        visualModeToggle.Click += (_, _) =>
        {
            var mode = preferences.NewTabMode == NewTabVisualMode.Stellar
                ? NewTabVisualMode.Basic
                : NewTabVisualMode.Stellar;
            preferences = preferences with { NewTabMode = mode };
            ApplyWorkspacePreferences(preferences);
            WorkspacePreferencesChanged?.Invoke(this, new WorkspacePreferencesChangedEventArgs(preferences));
        };
        searchBox.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter)
            {
                Submit();
                args.Handled = true;
            }
        };
        BuildLayout();
        RenderWorkspace();
        ApplyTheme();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => UpdateResponsiveWidths();
    }

    public event EventHandler<NewTabNavigationRequestedEventArgs>? NavigationRequested;

    public event EventHandler? DonationRequested;

    public event EventHandler<BookmarkLaunchRequestedEventArgs>? BookmarkLaunchRequested;

    public event EventHandler? ManageBookmarksRequested;

    public event EventHandler<OpenWorkspaceRequestedEventArgs>? OpenWorkspaceRequested;

    public event EventHandler<ConfigureWorkspaceRequestedEventArgs>? ConfigureWorkspaceRequested;

    public event EventHandler<RemoveWorkspaceRequestedEventArgs>? RemoveWorkspaceRequested;

    public event EventHandler<AffiliatedSiteLaunchRequestedEventArgs>? AffiliatedSiteLaunchRequested;

    public event EventHandler<AffiliatedSitesVisibilityChangeRequestedEventArgs>? AffiliatedSitesVisibilityChangeRequested;

    public event EventHandler<WorkspacePreferencesChangedEventArgs>? WorkspacePreferencesChanged;

    public bool ShowDonationLink
    {
        get => donationButton.Visibility == Visibility.Visible;
        set => donationButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public bool ReducedMotion
    {
        get => reducedMotion;
        set
        {
            reducedMotion = value;
            decoration.ReducedMotion = value;
            bookmarkHub.ReducedMotion = value;
            workspaceHub.ReducedMotion = value;
            affiliatedSites.ReducedMotion = value;
            UpdateEmberMotionPreferences();
        }
    }

    public bool IsAnimationRunning =>
        decoration.IsRegisteredWithSharedClock ||
        bookmarkHub.CenterStar.IsRegisteredWithSharedClock ||
        workspaceHub.CenterStar.IsRegisteredWithSharedClock;

    public int AnimationLifecycleStartCount => IsAnimationRunning ? 1 : 0;

    public static bool ShouldAnimateVisualField(
        bool highContrast,
        bool reducedMotion,
        bool reducedVisualNoise) =>
        NewTabDecorationControl.ShouldShowVisualField(highContrast, reducedVisualNoise) && !reducedMotion;

    public bool CanModifyWorkspace
    {
        get => canModifyWorkspace;
        set
        {
            hostCanModifyWorkspace = value;
            canModifyWorkspace = value && !isPrivateMode;
            RenderWorkspace();
        }
    }

    public bool IsPrivateMode => isPrivateMode;

    public BrowserWorkspacePreferences WorkspacePreferences => preferences;

    public void ApplyPrivateMode(bool isPrivate)
    {
        isPrivateMode = isPrivate;
        privateStatusSurface.Visibility = isPrivate ? Visibility.Visible : Visibility.Collapsed;
        canModifyWorkspace = hostCanModifyWorkspace && !isPrivate;
        visualModeToggle.IsEnabled = !isPrivate;
        AutomationProperties.SetHelpText(visualModeToggle, isPrivate
            ? "New Tab appearance uses the normal profile preference and cannot be changed in private browsing."
            : $"Current New Tab view: {preferences.NewTabMode}. This preference is saved for the normal profile by the host.");
        RenderWorkspace();
        ApplyTheme();
        UpdateEmberMotionPreferences();
        AutomationProperties.SetName(this, isPrivate
            ? "Private New Tab workspace"
            : "New tab workspace");
    }

    public void ApplyWorkspacePreferences(BrowserWorkspacePreferences next)
    {
        preferences = (next ?? throw new ArgumentNullException(nameof(next))).Validate();
        visualModeToggle.Content = preferences.NewTabMode == NewTabVisualMode.Stellar
            ? "Stellar view · switch to Basic"
            : "Basic view · switch to Stellar";
        AutomationProperties.SetName(visualModeToggle, (string)visualModeToggle.Content);
        AutomationProperties.SetHelpText(visualModeToggle,
            $"Current New Tab view: {preferences.NewTabMode}. This preference is saved for the normal profile by the host.");
        RefreshAffiliatedRail();
        UpdateHubModes();
    }

    public void SetCoordinatorDecoration(string? absolutePath, bool reduceVisualNoise)
    {
        reducedVisualNoise = reduceVisualNoise;
        decoration.SetCoordinatorAsset(absolutePath, reduceVisualNoise);
        bookmarkHub.ReducedVisualNoise = reduceVisualNoise;
        workspaceHub.ReducedVisualNoise = reduceVisualNoise;
        UpdateEmberMotionPreferences();
        UpdateHubModes();
    }

    public void SetStellarHubAssets(string? bookmarkAssetPath, string? workspaceAssetPath)
    {
        bookmarkHub.SetCenterAsset(bookmarkAssetPath);
        workspaceHub.SetCenterAsset(workspaceAssetPath);
    }

    public void SetWorkspaceData(
        IReadOnlyList<BookmarkEntry> bookmarks,
        IReadOnlyList<WorkspacePresetPresentation> presets)
    {
        workspaceData = new NewTabWorkspaceData(
            bookmarks ?? throw new ArgumentNullException(nameof(bookmarks)),
            presets ?? throw new ArgumentNullException(nameof(presets)),
            hostCanModifyWorkspace).Validate();
        RenderWorkspace();
    }

    public void ApplyWorkspaceData(NewTabWorkspaceData data)
    {
        workspaceData = (data ?? throw new ArgumentNullException(nameof(data))).Validate();
        hostCanModifyWorkspace = data.CanConfigure;
        canModifyWorkspace = hostCanModifyWorkspace && !isPrivateMode;
        RenderWorkspace();
    }

    /// <summary>
    /// Applies host-decoded local workspace artwork by opaque preset ID. Presentation
    /// never receives a file path and never downloads artwork.
    /// </summary>
    public void ApplyWorkspaceArtworkSources(
        IReadOnlyDictionary<WorkspacePresetPresentationId, ImageSource> artworkSources)
    {
        ArgumentNullException.ThrowIfNull(artworkSources);
        workspaceArtworkSources = new Dictionary<WorkspacePresetPresentationId, ImageSource>(artworkSources);
        RenderWorkspace();
    }

    public AffiliatedSitesCatalogPresentation AffiliatedSitesCatalog => affiliatedSites.Catalog;

    public AffiliatedSitesVisibilityPresentation AffiliatedSitesVisibility => affiliatedSites.VisibilityState;

    public void ApplyAffiliatedSites(
        AffiliatedSitesCatalogPresentation catalog,
        AffiliatedSitesVisibilityPresentation visibility)
    {
        affiliatedSites.Apply(catalog, visibility);
        RefreshAffiliatedRail();
    }

    public void ShowWorkspaceOperationMessage(string safeMessage, bool isError)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeMessage);
        workspaceStatus.Text = safeMessage.Trim();
        workspaceStatus.Foreground = SystemParameters.HighContrast
            ? SystemColors.WindowTextBrush
            : isError ? OrbitVisualTheme.Danger : OrbitVisualTheme.SeaGlass;
        workspaceStatus.Visibility = Visibility.Visible;
        AutomationProperties.SetLiveSetting(
            workspaceStatus,
            isError ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
    }

    public void FocusSearch()
    {
        searchBox.Focus();
        searchBox.SelectAll();
    }

    private void BuildLayout()
    {
        Children.Add(decoration);
        var title = new TextBlock
        {
            Text = "Where to next?",
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontSize = 30,
            FontWeight = FontWeights.SemiBold,
            Foreground = OrbitVisualTheme.Ink,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 4),
        };
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level1);
        var subtitle = new TextBlock
        {
            Text = "Search privately with DuckDuckGo or enter an address.",
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 14,
            Foreground = OrbitVisualTheme.MutedInk,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 14),
        };
        AutomationProperties.SetName(searchBox, "Search or enter address");
        AutomationProperties.SetHelpText(searchBox, "Searches use DuckDuckGo by default.");
        var searchGrid = new Grid();
        searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(searchButton, 1);
        searchGrid.Children.Add(searchBox);
        searchGrid.Children.Add(searchButton);
        searchSurface.Padding = new Thickness(4);
        searchSurface.Width = 680;
        searchSurface.MaxWidth = 680;
        searchSurface.HorizontalAlignment = HorizontalAlignment.Center;
        searchSurface.Child = searchGrid;

        var privacy = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 16),
        };
        privacy.Children.Add(new OrbitIcon
        {
            Kind = OrbitIconKind.Shield,
            Width = 18,
            Height = 18,
            Stroke = OrbitVisualTheme.SeaGlass,
            Margin = new Thickness(0, 0, 8, 0),
        });
        privacy.Children.Add(new TextBlock
        {
            Text = "No browser-owned telemetry. Sign-in is optional.",
            Foreground = OrbitVisualTheme.MutedInk,
            VerticalAlignment = VerticalAlignment.Center,
        });

        workspaceSurface.Padding = new Thickness(16, 12, 16, 18);
        workspaceSurface.Child = BuildWorkspaceLayout();
        AutomationProperties.SetName(workspaceSurface, "Quick launch workspace");
        OrbitVisualTheme.ApplyButton(donationButton, OrbitButtonRole.Quiet);
        AutomationProperties.SetName(donationButton, "Support Orbit Navigator");
        donationButton.Click += (_, _) => DonationRequested?.Invoke(this, EventArgs.Empty);

        privateStatusSurface.Child = privateStatus;
        pageContent.Children.Add(mark);
        pageContent.Children.Add(privateStatusSurface);
        pageContent.Children.Add(new TextBlock
        {
            Text = "ORBIT NAVIGATOR",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = OrbitVisualTheme.SeaGlass,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
        });
        pageContent.Children.Add(title);
        pageContent.Children.Add(subtitle);
        pageContent.Children.Add(searchSurface);
        pageContent.Children.Add(privacy);
        pageContent.Children.Add(BuildVisualModeControl());
        pageContent.Children.Add(workspaceSurface);
        pageContent.Children.Add(donationButton);
        var scroll = new ScrollViewer
        {
            Content = pageContent,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        KeyboardNavigation.SetTabNavigation(scroll, KeyboardNavigationMode.Continue);
        pageShell.Children.Add(affiliatedSites);
        pageShell.Children.Add(scroll);
        Children.Add(pageShell);
        ApplyWorkspacePreferences(preferences);
    }

    private void UpdateResponsiveWidths()
    {
        var railExtent = affiliatedSites.Visibility == Visibility.Visible
            ? affiliatedSites.Width + affiliatedSites.Margin.Left + affiliatedSites.Margin.Right
            : 0;
        var available = Math.Max(360, ActualWidth - 64 - railExtent);
        pageContent.Width = Math.Min(980, available);
        searchSurface.Width = Math.Min(680, available);
    }

    private FrameworkElement BuildWorkspaceLayout()
    {
        var layout = new StackPanel();
        AutomationProperties.SetName(manageBookmarksButton, "Manage bookmarks");
        AutomationProperties.SetName(createWorkspaceButton, "Create workspace");
        bookmarkTiles.Margin = new Thickness(-6, 10, -6, 18);
        workspaceTiles.Margin = new Thickness(-6, 10, -6, 0);
        var destinations = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemWidth = 440,
        };
        destinations.Children.Add(HubSection(
            "Bookmarks",
            ActionCluster(bookmarkViewToggle, manageBookmarksButton),
            bookmarkHub,
            bookmarkTiles));
        destinations.Children.Add(HubSection(
            "Saved workspaces",
            ActionCluster(workspaceViewToggle, createWorkspaceButton),
            workspaceHub,
            workspaceTiles));
        layout.Children.Add(destinations);

        emptyState.HorizontalAlignment = HorizontalAlignment.Stretch;
        emptyState.Margin = new Thickness(0, 12, 0, 0);
        var emptyHeading = new TextBlock
        {
            Text = "Make this space yours",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = OrbitVisualTheme.Ink,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        AutomationProperties.SetHeadingLevel(emptyHeading, AutomationHeadingLevel.Level3);
        emptyState.Children.Add(new OrbitIcon
        {
            Kind = OrbitIconKind.OrbitMark,
            Width = 42,
            Height = 42,
            Stroke = OrbitVisualTheme.PrivateViolet,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 10),
        });
        emptyState.Children.Add(emptyHeading);
        emptyState.Children.Add(new TextBlock
        {
            Text = "Saved bookmarks and named tab-group presets will appear here. Nothing is added until you choose it.",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Foreground = OrbitVisualTheme.MutedInk,
            MaxWidth = 560,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 12),
        });
        var emptyActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        OrbitVisualTheme.ApplyButton(emptyManageButton, OrbitButtonRole.Quiet);
        OrbitVisualTheme.ApplyButton(emptyCreateButton, OrbitButtonRole.Primary);
        AutomationProperties.SetName(emptyManageButton, "Open bookmarks");
        AutomationProperties.SetName(emptyCreateButton, "Create a workspace");
        emptyManageButton.Click += (_, _) => ManageBookmarksRequested?.Invoke(this, EventArgs.Empty);
        emptyCreateButton.Click += (_, _) => ConfigureWorkspaceRequested?.Invoke(
            this,
            new ConfigureWorkspaceRequestedEventArgs(null));
        emptyActions.Children.Add(emptyManageButton);
        emptyActions.Children.Add(emptyCreateButton);
        emptyState.Children.Add(emptyActions);
        layout.Children.Add(emptyState);
        AutomationProperties.SetName(workspaceStatus, "Workspace status");
        layout.Children.Add(workspaceStatus);

        manageBookmarksButton.Click += (_, _) => ManageBookmarksRequested?.Invoke(this, EventArgs.Empty);
        createWorkspaceButton.Click += (_, _) => ConfigureWorkspaceRequested?.Invoke(
            this,
            new ConfigureWorkspaceRequestedEventArgs(null));
        return layout;
    }

    private FrameworkElement BuildVisualModeControl()
    {
        var row = new DockPanel { LastChildFill = true };
        var copy = new StackPanel { Margin = new Thickness(0, 0, 18, 0) };
        copy.Children.Add(new TextBlock
        {
            Text = "New Tab appearance",
            FontWeight = FontWeights.SemiBold,
            Foreground = OrbitVisualTheme.Ink,
        });
        copy.Children.Add(new TextBlock
        {
            Text = "Choose an animated stellar map or a quiet, motion-free basic workspace.",
            FontSize = 11.5,
            Foreground = OrbitVisualTheme.MutedInk,
            TextWrapping = TextWrapping.Wrap,
        });
        DockPanel.SetDock(visualModeToggle, Dock.Right);
        row.Children.Add(visualModeToggle);
        row.Children.Add(copy);
        visualModeSurface.Padding = new Thickness(14, 10, 14, 10);
        visualModeSurface.Margin = new Thickness(0, 0, 0, 10);
        visualModeSurface.HorizontalAlignment = HorizontalAlignment.Center;
        visualModeSurface.MaxWidth = 760;
        visualModeSurface.Child = row;
        AutomationProperties.SetName(visualModeSurface, "New Tab appearance mode");
        return visualModeSurface;
    }

    private static FrameworkElement HubSection(
        string heading,
        UIElement actions,
        StellarHubControl hub,
        Panel canonicalList)
    {
        var section = new StackPanel
        {
            Width = 426,
            Margin = new Thickness(7, 0, 7, 14),
        };
        section.Children.Add(SectionHeading(heading, actions));
        section.Children.Add(hub);
        section.Children.Add(canonicalList);
        return section;
    }

    private static StackPanel ActionCluster(params Button[] buttons)
    {
        var cluster = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var button in buttons) cluster.Children.Add(button);
        return cluster;
    }

    private static DockPanel SectionHeading(string heading, UIElement action)
    {
        var panel = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(action, Dock.Right);
        panel.Children.Add(action);
        var text = new TextBlock
        {
            Text = heading,
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            Foreground = OrbitVisualTheme.Ink,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetHeadingLevel(text, AutomationHeadingLevel.Level2);
        panel.Children.Add(text);
        return panel;
    }

    private void RenderWorkspace()
    {
        bookmarkTiles.Children.Clear();
        workspaceTiles.Children.Clear();
        // Bookmark management remains readable in private windows; the manager
        // itself independently disables every mutation.
        manageBookmarksButton.IsEnabled = true;
        createWorkspaceButton.IsEnabled = CanModifyWorkspace;
        emptyManageButton.IsEnabled = true;
        emptyCreateButton.IsEnabled = CanModifyWorkspace;
        var unavailable = "Workspace changes are unavailable in private browsing.";
        AutomationProperties.SetHelpText(manageBookmarksButton, "View bookmarks and manage them when policy allows.");
        AutomationProperties.SetHelpText(createWorkspaceButton, CanModifyWorkspace ? string.Empty : unavailable);
        AutomationProperties.SetHelpText(emptyManageButton, "View bookmarks and manage them when policy allows.");
        AutomationProperties.SetHelpText(emptyCreateButton, CanModifyWorkspace ? string.Empty : unavailable);

        foreach (var bookmark in workspaceData.Bookmarks)
        {
            bookmarkTiles.Children.Add(CreateBookmarkTile(bookmark));
        }

        foreach (var preset in workspaceData.Presets)
        {
            workspaceTiles.Children.Add(CreateWorkspaceTile(preset));
        }

        emptyState.Visibility = workspaceData.Bookmarks.Count == 0 && workspaceData.Presets.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        bookmarkHub.SetItems(workspaceData.Bookmarks.Select((bookmark, index) => new StellarHubItem(
            index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            string.IsNullOrWhiteSpace(bookmark.Title) ? bookmark.Target.Host : bookmark.Title,
            bookmark.Target.Host)
        {
            Address = bookmark.Target,
            Note = workspaceData.BookmarkNotes.TryGetValue(bookmark.Id, out var note) ? note : null,
            FaviconPng = workspaceData.BookmarkFavicons.TryGetValue(bookmark.Id, out var favicon)
                ? favicon
                : ReadOnlyMemory<byte>.Empty,
        }).ToArray());
        workspaceHub.SetItems(workspaceData.Presets.Select((preset, index) => new StellarHubItem(
            index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            preset.Name,
            $"{preset.Tabs.Count} tabs")
        {
            Address = preset.Tabs[preset.FirstTabIndex].Target,
            Note = preset.Note,
            FaviconPng = preset.Tabs[preset.FirstTabIndex].FaviconPng,
            OrbitingImageSource = workspaceArtworkSources.TryGetValue(preset.Id, out var artwork)
                ? artwork
                : null,
            ColorToken = preset.ColorToken,
        }).ToArray());
        UpdateHubModes();
    }

    private void UpdateHubModes()
    {
        var forceList = SystemParameters.HighContrast || reducedVisualNoise ||
                        preferences.NewTabMode == NewTabVisualMode.Basic;
        var showBookmarkList = forceList || bookmarkListView;
        var showWorkspaceList = forceList || workspaceListView;
        // Empty catalogs still show their distinct stellar invitation. Fake
        // items are never created; the canonical onboarding remains below.
        bookmarkHub.Visibility = !showBookmarkList ? Visibility.Visible : Visibility.Collapsed;
        workspaceHub.Visibility = !showWorkspaceList ? Visibility.Visible : Visibility.Collapsed;
        bookmarkTiles.Visibility = showBookmarkList && workspaceData.Bookmarks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        workspaceTiles.Visibility = showWorkspaceList && workspaceData.Presets.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        bookmarkViewToggle.Content = showBookmarkList ? "Show stellar hub" : "Show list view";
        workspaceViewToggle.Content = showWorkspaceList ? "Show stellar hub" : "Show list view";
        bookmarkViewToggle.Visibility = workspaceData.Bookmarks.Count > 0 && !forceList ? Visibility.Visible : Visibility.Collapsed;
        workspaceViewToggle.Visibility = workspaceData.Presets.Count > 0 && !forceList ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(bookmarkViewToggle, (string)bookmarkViewToggle.Content);
        AutomationProperties.SetName(workspaceViewToggle, (string)workspaceViewToggle.Content);
        decoration.Visibility = forceList || isPrivateMode ? Visibility.Collapsed : Visibility.Visible;
        if (!SystemParameters.HighContrast)
        {
            workspaceSurface.Background = forceList
                ? new SolidColorBrush(Color.FromArgb(224, 17, 26, 34))
                : Brushes.Transparent;
            workspaceSurface.BorderBrush = forceList ? OrbitVisualTheme.Divider : Brushes.Transparent;
            workspaceSurface.BorderThickness = forceList ? new Thickness(1) : new Thickness(0);
        }
    }

    private void RefreshAffiliatedRail()
    {
        DockPanel.SetDock(
            affiliatedSites,
            preferences.AffiliatedRailPlacement == AffiliatedRailPlacement.Left ? Dock.Left : Dock.Right);
        affiliatedSites.Width = AffiliatedSitesControl.PreferredRailWidth;
        affiliatedSites.Margin = preferences.AffiliatedRailPlacement == AffiliatedRailPlacement.Left
            ? new Thickness(8, 24, 8, 24)
            : new Thickness(8, 24, 8, 24);
        affiliatedSites.Visibility = preferences.ShowAffiliatedRail && !affiliatedSites.VisibilityState.IsHidden
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateResponsiveWidths();
        AutomationProperties.SetHelpText(
            affiliatedSites,
            $"Affiliated Sites rail is on the {preferences.AffiliatedRailPlacement.ToString().ToLowerInvariant()}. Only the approved local catalog is shown.");
    }

    private void UpdateEmberMotionPreferences()
    {
        foreach (var star in VisualDescendants(this).OfType<OrbitEmberStar>())
        {
            star.ReducedMotion = ReducedMotion;
            star.MotionEnabled = !reducedVisualNoise && !isPrivateMode;
        }
    }

    private void OnHubDetailVisibilityChanged(object? sender, bool visible) =>
        ApplyHubDetailFreeze(bookmarkHub.IsDetailCardVisible || workspaceHub.IsDetailCardVisible);

    private void ApplyHubDetailFreeze(bool freeze)
    {
        decoration.FreezeNonStellarMotion = freeze;
        bookmarkHub.FreezeNonStellarMotion = freeze;
        workspaceHub.FreezeNonStellarMotion = freeze;
    }

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in VisualDescendants(child))
            {
                yield return descendant;
            }
        }
    }

    private Button CreateBookmarkTile(BookmarkEntry bookmark)
    {
        var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left };
        content.Children.Add(new OrbitEmberStar(OrbitEmberStarKind.Favorite)
        {
            IsActive = true,
            ReducedMotion = ReducedMotion,
            MotionEnabled = !reducedVisualNoise,
            Width = 24,
            Height = 24,
            Stroke = OrbitVisualTheme.SeaGlass,
            HorizontalAlignment = HorizontalAlignment.Left,
        });
        content.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(bookmark.Title) ? bookmark.Target.Host : bookmark.Title,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 166,
            Margin = new Thickness(0, 8, 0, 2),
        });
        content.Children.Add(new TextBlock
        {
            Text = bookmark.Target.Host,
            Foreground = OrbitVisualTheme.MutedInk,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 166,
            FontSize = 11,
        });
        var button = new Button
        {
            Content = content,
            Width = 204,
            Height = 92,
            Margin = new Thickness(6),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            ToolTip = bookmark.Target.AbsoluteUri,
        };
        OrbitVisualTheme.ApplyButton(button, OrbitButtonRole.Tab);
        AutomationProperties.SetName(button, $"Open bookmark {bookmark.Title}");
        button.Click += (_, _) => BookmarkLaunchRequested?.Invoke(
            this,
            new BookmarkLaunchRequestedEventArgs(bookmark));
        return button;
    }

    private FrameworkElement CreateWorkspaceTile(WorkspacePresetPresentation preset)
    {
        var panel = new Grid { Width = 286, Height = 76, Margin = new Thickness(6) };
        var openContent = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        openContent.Children.Add(new OrbitEmberStar(OrbitEmberStarKind.TabGroup)
        {
            IsActive = true,
            ReducedMotion = ReducedMotion,
            MotionEnabled = !reducedVisualNoise,
            Width = 32,
            Height = 32,
            Margin = new Thickness(0, 0, 10, 0),
        });
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock
        {
            Text = preset.Name,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 210,
        });
        labels.Children.Add(new TextBlock
        {
            Text = $"{preset.Tabs.Count} tabs{(string.IsNullOrWhiteSpace(preset.GroupName) ? string.Empty : $" • {preset.GroupName}")}",
            Foreground = OrbitVisualTheme.MutedInk,
            FontSize = 11,
        });
        openContent.Children.Add(labels);
        var open = new Button
        {
            Content = openContent,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(14, 8, 46, 8),
        };
        OrbitVisualTheme.ApplyButton(open, OrbitButtonRole.Tab);
        AutomationProperties.SetName(open, $"Open workspace {preset.Name}, {preset.Tabs.Count} tabs");
        open.Click += (_, _) => OpenWorkspaceRequested?.Invoke(
            this,
            new OpenWorkspaceRequestedEventArgs(preset));
        panel.Children.Add(open);

        var actions = new Button
        {
            Content = "•••",
            Width = 38,
            Height = 38,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 7, 7, 0),
            IsEnabled = CanModifyWorkspace,
            ToolTip = CanModifyWorkspace ? "Workspace actions" : "Workspace changes are unavailable in private browsing.",
        };
        OrbitVisualTheme.ApplyButton(actions, OrbitButtonRole.Quiet);
        AutomationProperties.SetName(actions, $"Workspace actions for {preset.Name}");
        AutomationProperties.SetHelpText(
            actions,
            CanModifyWorkspace ? "Edit or remove this saved workspace." : "Workspace changes are unavailable in private browsing.");
        actions.Click += (_, _) => OpenWorkspaceActions(actions, preset);
        panel.Children.Add(actions);
        return panel;
    }

    private void OpenWorkspaceActions(Button target, WorkspacePresetPresentation preset)
    {
        var menu = new ContextMenu { PlacementTarget = target };
        var edit = new MenuItem { Header = "Edit workspace" };
        var remove = new MenuItem { Header = "Remove workspace" };
        AutomationProperties.SetName(edit, $"Edit workspace {preset.Name}");
        AutomationProperties.SetName(remove, $"Remove workspace {preset.Name}");
        edit.Click += (_, _) => ConfigureWorkspaceRequested?.Invoke(
            this,
            new ConfigureWorkspaceRequestedEventArgs(preset));
        remove.Click += (_, _) => RemoveWorkspaceRequested?.Invoke(
            this,
            new RemoveWorkspaceRequestedEventArgs(preset.Id, preset.Name));
        menu.Items.Add(edit);
        menu.Items.Add(remove);
        OrbitVisualTheme.ApplyContextMenu(menu);
        target.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void Submit()
    {
        try
        {
            var target = resolver.Resolve(searchBox.Text);
            NavigationRequested?.Invoke(this, new NewTabNavigationRequestedEventArgs(target));
        }
        catch (ArgumentException)
        {
            AutomationProperties.SetHelpText(searchBox, "Enter a web address or search term.");
            searchBox.Focus();
        }
    }

    private void ApplyTheme()
    {
        if (SystemParameters.HighContrast)
        {
            Background = SystemColors.WindowBrush;
            mark.Primary = SystemColors.WindowTextBrush;
            mark.Accent = SystemColors.HighlightBrush;
            searchSurface.Background = SystemColors.WindowBrush;
            searchSurface.BorderBrush = SystemColors.WindowTextBrush;
            workspaceSurface.Background = SystemColors.WindowBrush;
            workspaceSurface.BorderBrush = SystemColors.WindowTextBrush;
            visualModeSurface.Background = SystemColors.WindowBrush;
            visualModeSurface.BorderBrush = SystemColors.WindowTextBrush;
            privateStatusSurface.Background = SystemColors.HighlightBrush;
            privateStatusSurface.BorderBrush = SystemColors.WindowTextBrush;
            privateStatusSurface.BorderThickness = new Thickness(2);
        }
        else
        {
            Background = isPrivateMode
                ? new LinearGradientBrush(
                    Color.FromRgb(38, 15, 34),
                    Color.FromRgb(31, 24, 58),
                    new Point(0, 1),
                    new Point(1, 0))
                : OrbitVisualTheme.Canvas;
            mark.Primary = OrbitVisualTheme.SeaGlass;
            mark.Accent = OrbitVisualTheme.WaypointGold;
            searchSurface.Background = OrbitVisualTheme.Chrome;
            searchSurface.BorderBrush = OrbitVisualTheme.Divider;
            visualModeSurface.Background = new SolidColorBrush(Color.FromArgb(208, 17, 26, 34));
            visualModeSurface.BorderBrush = OrbitVisualTheme.Divider;
            privateStatusSurface.Background = new LinearGradientBrush(
                Color.FromArgb(232, 104, 25, 50),
                Color.FromArgb(232, 69, 42, 118),
                new Point(0, .5),
                new Point(1, .5));
            privateStatusSurface.BorderBrush = OrbitVisualTheme.PrivateViolet;
            privateStatusSurface.BorderThickness = new Thickness(1);
        }

        OrbitVisualTheme.ApplyTextBox(searchBox);
        OrbitVisualTheme.ApplyButton(searchButton, OrbitButtonRole.Primary);
        OrbitVisualTheme.ApplyButton(donationButton, OrbitButtonRole.Quiet);
        OrbitVisualTheme.ApplyButton(manageBookmarksButton, OrbitButtonRole.Quiet);
        OrbitVisualTheme.ApplyButton(createWorkspaceButton, OrbitButtonRole.Primary);
        OrbitVisualTheme.ApplyButton(bookmarkViewToggle, OrbitButtonRole.Quiet);
        OrbitVisualTheme.ApplyButton(workspaceViewToggle, OrbitButtonRole.Quiet);
        OrbitVisualTheme.ApplyButton(visualModeToggle, OrbitButtonRole.Quiet);
        RenderWorkspace();
    }

    private static OrbitIcon Icon(OrbitIconKind kind, double size)
    {
        var icon = new OrbitIcon { Kind = kind, Width = size, Height = size };
        icon.BindStrokeToAncestorForeground();
        return icon;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (!systemParameterEventsAttached)
        {
            SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
            systemParameterEventsAttached = true;
        }

        ApplyTheme();
        OrbitMotion.Reveal(searchSurface, ReducedMotion);
        OrbitMotion.Reveal(workspaceSurface, ReducedMotion);
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
            ApplyTheme();
            decoration.RefreshAccessibilityState();
            UpdateHubModes();
        }
    }
}

public sealed class NewTabNavigationRequestedEventArgs : EventArgs
{
    public NewTabNavigationRequestedEventArgs(OmniboxTarget target)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
    }

    public OmniboxTarget Target { get; }
}

public sealed class BookmarkLaunchRequestedEventArgs : EventArgs
{
    public BookmarkLaunchRequestedEventArgs(BookmarkEntry bookmark)
    {
        Bookmark = bookmark ?? throw new ArgumentNullException(nameof(bookmark));
    }

    public BookmarkEntry Bookmark { get; }
}

public sealed class OpenWorkspaceRequestedEventArgs : EventArgs
{
    public OpenWorkspaceRequestedEventArgs(WorkspacePresetPresentation workspace)
    {
        Workspace = (workspace ?? throw new ArgumentNullException(nameof(workspace))).Validate();
    }

    public WorkspacePresetPresentation Workspace { get; }

    /// <summary>Opening a saved workspace adds a new live group and leaves current tabs intact.</summary>
    public bool OpenAsNewLiveGroup => true;

    public bool InitiallyCollapsed => true;

    public bool SelectFirstSiteImmediately => true;

    public int FirstTabIndex => Workspace.FirstTabIndex;
}

public sealed class ConfigureWorkspaceRequestedEventArgs : EventArgs
{
    public ConfigureWorkspaceRequestedEventArgs(WorkspacePresetPresentation? workspace)
    {
        Workspace = workspace?.Validate();
    }

    public WorkspacePresetPresentation? Workspace { get; }
}

public sealed class RemoveWorkspaceRequestedEventArgs : EventArgs
{
    public RemoveWorkspaceRequestedEventArgs(WorkspacePresetPresentationId workspaceId, string workspaceName)
    {
        if (workspaceId.IsEmpty)
        {
            throw new ArgumentException("A workspace preset ID is required.", nameof(workspaceId));
        }

        WorkspaceId = workspaceId;
        WorkspaceName = string.IsNullOrWhiteSpace(workspaceName) ? "Workspace" : workspaceName.Trim();
    }

    public WorkspacePresetPresentationId WorkspaceId { get; }

    public string WorkspaceName { get; }
}
#endif
