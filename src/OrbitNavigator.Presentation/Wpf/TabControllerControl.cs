#if ORBIT_WPF
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Presentation.Tabs;
using OrbitNavigator.Presentation.Workspace;

namespace OrbitNavigator.Presentation.Wpf;

public enum TabControllerSurfaceKind
{
    Docked = 0,
    Detached = 1,
}

/// <summary>A reusable tab view over one Presentation-owned per-window session.</summary>
public sealed class TabControllerControl : Grid
{
    private readonly Grid rail = new();
    private readonly Grid entriesViewport = new() { ClipToBounds = true };
    private readonly WrapPanel entries = new();
    private readonly Panel commands;
    private readonly Button previous;
    private readonly Button next;
    private readonly Button more;
    private readonly Button newTab;
    private readonly Button resource;
    private readonly Button detachDock;
    private readonly ScrollBar overflowScrollBar = new()
    {
        Minimum = 0,
        SmallChange = 1,
        Visibility = Visibility.Collapsed,
        Focusable = true,
    };
    private readonly TextBlock announcer = new() { Width = 1, Height = 1, Opacity = 0.01, Focusable = false };
    private readonly Dictionary<BrowserTabId, (long Revision, ImageSource Source)> faviconCache = [];
    private TabControllerPresentationSession? session;
    private TabControllerPresentationState? state;
    private TabStripViewportProjection? viewport;
    private BrowserTabId? focusedTabId;
    private BrowserTabId? pendingCloseFocusTabId;
    private BrowserTabGroupId? hoverPreviewGroupId;
    private ContextMenu? groupPreviewMenu;
    private Window? detachedOwnerWindow;
    private TabStripPlacement? layoutPlacementOverride;
    private bool viewportNavigationActive;
    private bool updatingOverflowScrollBar;
    private bool compactMode;
    private int pageStart;
    private int visibleColumnCount = 1;
    private double visibleColumnWidth = TabStripViewportModel.PreferredVerticalColumnWidth;
    private BrowserTabId? dragSourceTabId;
    private Point dragStart;

    private const string TabDragFormat = "OrbitNavigator.TabController.TabId";

    public const double RecommendedDetachedWidth = 400;
    public const double RecommendedDetachedHeight = 520;
    public const double MinimumDetachedWidth = 320;
    public const double MinimumDetachedHeight = 360;

    public TabControllerControl(TabControllerSurfaceKind surfaceKind = TabControllerSurfaceKind.Docked)
    {
        SurfaceKind = surfaceKind;
        commands = surfaceKind == TabControllerSurfaceKind.Detached
            ? new UniformGrid { Rows = 1 }
            : new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
        Background = SystemParameters.HighContrast ? SystemColors.WindowBrush : OrbitVisualTheme.Chrome;
        Focusable = false;
        AutomationProperties.SetName(this, surfaceKind == TabControllerSurfaceKind.Detached
            ? "Detached tab controller"
            : "Browser tab controller");

        previous = CommandButton(OrbitControllerGlyphKind.PreviousPage, OrbitCommandShape.Port, "Previous tabs", PagePrevious);
        next = CommandButton(OrbitControllerGlyphKind.NextPage, OrbitCommandShape.Port, "Next tabs", PageNext);
        more = CommandButton(OrbitControllerGlyphKind.MoreTabs, OrbitCommandShape.Lens, "More tabs", OpenOverflow);
        newTab = NewTabButton();
        resource = CommandButton(OrbitControllerGlyphKind.Resources, OrbitCommandShape.Lens, "Browser resources", ToggleResources);
        detachDock = CommandButton(
            surfaceKind == TabControllerSurfaceKind.Detached ? OrbitControllerGlyphKind.Dock : OrbitControllerGlyphKind.Detach,
            OrbitCommandShape.Keel,
            surfaceKind == TabControllerSurfaceKind.Detached ? "Dock tab controller" : "Pop out tab controller",
            RequestDetachOrDock);
        BuildLayout();
        SizeChanged += (_, _) => RenderAcceptedState();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseWheel += OnPreviewMouseWheel;
        rail.MouseLeave += OnRailMouseLeave;
        overflowScrollBar.ValueChanged += OnOverflowScrollBarValueChanged;
    }

    public TabControllerSurfaceKind SurfaceKind { get; }
    public bool ReducedMotion { get; set; }
    public bool IsResourcePanelOpen { get; private set; }
    public int HiddenTabCount => viewport?.HiddenCount ?? 0;
    public bool HasOverlayScrollbar => false;
    public ScrollBar OverflowScrollBar => overflowScrollBar;
    public FrameworkElement ResourcePanelAnchor => resource;
    public bool IsCompactMode => compactMode;
    public int VisibleColumnCount => visibleColumnCount;
    public double VisibleColumnWidth => visibleColumnWidth;
    public bool HasActiveViewportTransition => entries.HasAnimatedProperties ||
        entries.RenderTransform is TranslateTransform { HasAnimatedProperties: true };
    public BrowserTabGroupId? ActiveHoverPreviewGroupId => hoverPreviewGroupId;
    public bool IsGroupPreviewDropdownOpen => groupPreviewMenu?.IsOpen == true;
    public int GroupPreviewDropdownItemCount => groupPreviewMenu?.Items.OfType<MenuItem>().Count() ?? 0;
    public IReadOnlyList<string> GroupPreviewDropdownLabels => groupPreviewMenu?.Items
        .OfType<MenuItem>()
        .Select(item => item.Header?.ToString() ?? string.Empty)
        .ToArray() ?? [];
    public bool IsInlineGroupPreviewVisible => hoverPreviewGroupId is not null &&
        Descendants(entries).OfType<Button>().Any(button =>
            AutomationProperties.GetItemStatus(button) == "Hover preview");

    /// <summary>
    /// Optional host-owned importer. It must return only a validated opaque local asset
    /// identifier; Presentation never stores or reopens the user's original file path.
    /// </summary>
    public Func<WorkspaceLocalArtworkImportRequest, WorkspaceArtworkPresentation?>? LocalArtworkImporter { get; set; }

    public event EventHandler<bool>? ResourcePanelVisibilityChanged;

    public event EventHandler? Released;

    public void Bind(TabControllerPresentationSession presentationSession)
    {
        ArgumentNullException.ThrowIfNull(presentationSession);
        if (ReferenceEquals(session, presentationSession))
        {
            return;
        }

        Unbind();
        session = presentationSession;
        session.PresentationChanged += OnPresentationChanged;
        state = session.Current;
        layoutPlacementOverride = null;
        RenderAcceptedState();
    }

    public void Unbind()
    {
        var wasBound = session is not null;
        if (session is not null)
        {
            session.PresentationChanged -= OnPresentationChanged;
        }

        session = null;
        state = null;
        entries.Children.Clear();
        faviconCache.Clear();
        layoutPlacementOverride = null;
        viewportNavigationActive = false;
        hoverPreviewGroupId = null;
        if (groupPreviewMenu is not null)
        {
            groupPreviewMenu.IsOpen = false;
            groupPreviewMenu = null;
        }
        if (wasBound)
        {
            Released?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool FocusSelectedTab()
    {
        var selected = FindSelectedTabButton();
        if (selected is null || !selected.IsEnabled || !selected.Focusable || !selected.IsVisible)
        {
            return false;
        }

        selected.BringIntoView();
        selected.UpdateLayout();
        var scope = FocusManager.GetFocusScope(selected);
        FocusManager.SetFocusedElement(scope, selected);
        var focused = selected.Focus() || Keyboard.Focus(selected) is not null;
        return focused && (selected.IsKeyboardFocusWithin ||
                           ReferenceEquals(FocusManager.GetFocusedElement(scope), selected));
    }

    public void CloseActiveFlyout()
    {
        if (more.ContextMenu is { IsOpen: true } menu)
        {
            menu.IsOpen = false;
            more.Focus();
        }
    }

    public bool TryHandleControllerShortcut(Key key, ModifierKeys modifiers)
    {
        if (key == Key.T && modifiers == ModifierKeys.Control)
        {
            _ = ExecuteAsync(new CreateNewTabControllerAction(Guid.NewGuid()));
            return true;
        }

        if (key == Key.W && modifiers == ModifierKeys.Control &&
            state?.Projection.Tabs.SelectedTabId is { } selectedTabId)
        {
            RequestCloseTab(selectedTabId);
            return true;
        }

        if (key == Key.A && modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            OpenOverflow();
            return true;
        }

        return false;
    }

    public void ApplyResourcePanelVisibility(bool visible)
    {
        IsResourcePanelOpen = visible;
        resource.SetCurrentValue(OrbitShapedCommand.IsSelectedProperty, visible);
        AutomationProperties.SetItemStatus(resource, visible ? "Open" : "Closed");
        UpdateDetachedPanelAllocation();
    }

    public void ApplyCompactMode(bool enabled)
    {
        if (compactMode == enabled)
        {
            return;
        }

        compactMode = enabled;
        pageStart = 0;
        viewportNavigationActive = false;
        focusedTabId = state?.Projection.Tabs.SelectedTabId;
        RenderAcceptedState();
        AutomationProperties.SetItemStatus(this, enabled ? "Compact favicon-only tabs" : "Tabs show titles");
    }

    public void ApplyLayoutPlacement(TabStripPlacement placement)
    {
        if (!Enum.IsDefined(placement))
        {
            throw new ArgumentOutOfRangeException(nameof(placement));
        }
        if (SurfaceKind == TabControllerSurfaceKind.Detached)
        {
            return;
        }

        layoutPlacementOverride = placement;
        pageStart = 0;
        viewportNavigationActive = false;
        focusedTabId = state?.Projection.Tabs.SelectedTabId;
        RenderAcceptedState();
        InvalidateMeasure();
        InvalidateArrange();
        if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
        {
            _ = Dispatcher.InvokeAsync(() =>
            {
                RenderAcceptedState();
                UpdateLayout();
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void BuildLayout()
    {
        Children.Add(rail);
        Children.Add(announcer);
        AutomationProperties.SetLiveSetting(announcer, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(announcer, "Tab controller status");
        AutomationProperties.SetName(overflowScrollBar, "Tab overflow position");
        AutomationProperties.SetHelpText(
            overflowScrollBar,
            "Scroll through tabs that do not fit in the visible tab strip. Mouse wheel is also supported.");
    }

    private void RenderAcceptedState()
    {
        if (state is null)
        {
            return;
        }

        var placement = SurfaceKind == TabControllerSurfaceKind.Detached
            ? TabStripPlacement.Left
            : layoutPlacementOverride ?? state.Projection.Placement;
        var vertical = SurfaceKind == TabControllerSurfaceKind.Detached ||
                       placement is TabStripPlacement.Left or TabStripPlacement.Right;
        entries.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        entries.ItemWidth = double.NaN;
        rail.Children.Clear();
        rail.RowDefinitions.Clear();
        rail.ColumnDefinitions.Clear();
        entriesViewport.Children.Clear();
        entriesViewport.RowDefinitions.Clear();
        entriesViewport.ColumnDefinitions.Clear();
        if (commands is WrapPanel commandWrap)
        {
            commandWrap.Orientation = Orientation.Horizontal;
        }
        foreach (var shaped in new[] { previous, next, more, newTab, resource, detachDock }.OfType<OrbitShapedCommand>())
        {
            shaped.ReducedMotion = ReducedMotion;
        }
        if (commands.Children.Count == 0)
        {
            commands.Children.Add(previous);
            commands.Children.Add(next);
            commands.Children.Add(more);
            if (SurfaceKind != TabControllerSurfaceKind.Detached)
            {
                commands.Children.Add(newTab);
            }
            commands.Children.Add(resource);
            commands.Children.Add(detachDock);
        }

        var responsiveExtent = SurfaceKind == TabControllerSurfaceKind.Detached
            ? Window.GetWindow(this)?.ActualHeight ?? ActualHeight
            : vertical ? ActualHeight : ActualWidth;
        var isNarrow = vertical ? responsiveExtent < 520 : responsiveExtent < 560;
        // Side rails and the detached controller already expose a reserved,
        // non-overlay scrollbar gutter. Repeating page commands there wastes the
        // very space the user detached or moved the rail to recover.
        previous.Visibility = vertical || isNarrow ? Visibility.Collapsed : Visibility.Visible;
        next.Visibility = vertical || isNarrow ? Visibility.Collapsed : Visibility.Visible;
        // Resources is a primary detached-controller route. It remains pinned
        // even when the native tool is at its minimum height; the Auto-row
        // content height is not a valid proxy for the tool's usable viewport.
        resource.Visibility = Visibility.Visible;

        overflowScrollBar.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        overflowScrollBar.Style = OrbitVisualTheme.CreateTabViewportScrollBarStyle(overflowScrollBar.Orientation);
        entriesViewport.MinHeight = vertical ? 0 : 57;
        entriesViewport.MinWidth = vertical ? 132 : 0;
        if (vertical)
        {
            entriesViewport.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            entriesViewport.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(overflowScrollBar, 0);
            Grid.SetRow(overflowScrollBar, 0);
            Grid.SetColumn(entries, 1);
            Grid.SetRow(entries, 0);
        }
        else
        {
            entriesViewport.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            entriesViewport.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(entries, 0);
            Grid.SetColumn(entries, 0);
            Grid.SetRow(overflowScrollBar, 1);
            Grid.SetColumn(overflowScrollBar, 0);
        }
        entriesViewport.Children.Add(entries);
        entriesViewport.Children.Add(overflowScrollBar);
        entries.AllowDrop = true;
        entries.Drop -= OnEntriesDrop;
        entries.Drop += OnEntriesDrop;

        if (SurfaceKind == TabControllerSurfaceKind.Detached)
        {
            rail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rail.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            rail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(commands, 0);
            Grid.SetRow(entriesViewport, 1);
            Grid.SetRow(newTab, 2);
            newTab.Margin = new Thickness(8, 2, 0, 8);
            newTab.HorizontalAlignment = HorizontalAlignment.Left;
            commands.Margin = new Thickness(6, 6, 6, 8);
        }
        else if (vertical)
        {
            rail.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            rail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(commands, 1);
            Grid.SetColumn(commands, 0);
        }
        else
        {
            rail.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rail.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(commands, 1);
            Grid.SetRow(commands, 0);
        }

        rail.Children.Add(entriesViewport);
        rail.Children.Add(commands);
        if (SurfaceKind == TabControllerSurfaceKind.Detached)
        {
            rail.Children.Add(newTab);
        }
        foreach (var command in commands.Children.OfType<FrameworkElement>())
        {
            command.Margin = vertical ? new Thickness(0) : new Thickness(2);
        }
        var reserved = commands.Children.OfType<UIElement>()
            .Count(child => child.Visibility == Visibility.Visible) * 48d;
        var dockedCommandRows = Math.Max(
            1,
            (int)Math.Ceiling(
                commands.Children.OfType<UIElement>().Count(child => child.Visibility == Visibility.Visible) /
                (double)Math.Max(1, (int)Math.Floor(Math.Max(44, ActualWidth) / 44d))));
        var verticalAvailableHeight = Math.Max(
            TabStripViewportModel.SafeVerticalEntryExtent,
            ActualHeight - (SurfaceKind == TabControllerSurfaceKind.Detached ? 66d : dockedCommandRows * 48d) -
            (SurfaceKind == TabControllerSurfaceKind.Detached ? 52d : 0d));
        if (vertical)
        {
            var viewportWidth = entriesViewport.ActualWidth > 0
                ? entriesViewport.ActualWidth
                : ActualWidth;
            var contentWidth = Math.Max(
                TabStripViewportModel.MinimumVerticalColumnWidth,
                viewportWidth - TabStripViewportModel.ReservedVerticalScrollbarGutter);
            visibleColumnCount = Math.Max(
                1,
                (int)Math.Floor(contentWidth /
                    (TabStripViewportModel.MinimumVerticalColumnWidth +
                     TabStripViewportModel.VerticalColumnSpacing)));
            visibleColumnWidth = Math.Min(
                TabStripViewportModel.PreferredVerticalColumnWidth,
                Math.Floor(contentWidth / visibleColumnCount) -
                (TabStripViewportModel.VerticalColumnSpacing / 2));
            entries.ItemWidth = visibleColumnWidth;
        }
        else
        {
            visibleColumnCount = 1;
            visibleColumnWidth = TabStripViewportModel.PreferredVerticalColumnWidth;
        }
        var available = vertical
            ? Math.Max(1, Math.Floor(verticalAvailableHeight / TabStripViewportModel.SafeVerticalEntryExtent)) *
              TabStripViewportModel.SafeVerticalEntryExtent * visibleColumnCount
            : Math.Max(TabStripViewportModel.SafeTopTabExtent, ActualWidth - reserved);
        var canonical = state.Projection.Tabs.Entries;
        var audibleCompactMinimum = state.TabInteractions.Values.Any(interaction =>
            interaction.IsPlayingAudio && interaction.Mute.IsAvailable)
            ? TabStripViewportModel.AudibleCompactTopTabExtent
            : 0;
        viewport = TabStripViewportModel.Project(
            canonical,
            state.Projection.Tabs.SelectedTabId,
            focusedTabId,
            available,
            placement,
            SurfaceKind == TabControllerSurfaceKind.Detached,
            compactMode,
            audibleCompactMinimum);
        if (viewportNavigationActive)
        {
            var count = viewport.Count;
            var start = Math.Clamp(pageStart, 0, Math.Max(0, canonical.Count - count));
            var visible = canonical.Skip(start).Take(count).ToArray();
            var selectedIndex = canonical
                .Select((entry, index) => (entry, index))
                .Where(item => item.entry is BrowserTabEntry tab &&
                    tab.TabId == state.Projection.Tabs.SelectedTabId)
                .Select(item => item.index)
                .DefaultIfEmpty(-1)
                .First();
            if (visible.Length > 0 && selectedIndex >= 0 &&
                !visible.OfType<BrowserTabEntry>().Any(tab => tab.TabId == state.Projection.Tabs.SelectedTabId))
            {
                // The scroll viewport is ephemeral. Keep the authoritative selected tab
                // pinned at the nearest edge while the other slots expose the requested
                // contiguous range, so wheel navigation never makes selection disappear.
                visible[selectedIndex < start ? 0 : visible.Length - 1] = canonical[selectedIndex];
            }
            viewport = new(start, visible.Length, start, canonical.Count - start - visible.Length,
                start > 0, start + visible.Length < canonical.Count, visible);
        }

        entries.Children.Clear();
        var visibleEntries = ExpandHoveredSmallGroup(viewport.VisibleEntries);
        var viewportExtent = vertical
            ? entriesViewport.ActualHeight * visibleColumnCount
            : entriesViewport.ActualWidth;
        var renderedEntryExtent = hoverPreviewGroupId is not null && visibleEntries.Count > viewport.VisibleEntries.Count &&
                                  viewportExtent > 0
            ? Math.Clamp(
                viewportExtent / Math.Max(1, visibleEntries.Count),
                vertical ? TabStripViewportModel.SafeVerticalEntryExtent : TabStripViewportModel.CompactTopTabExtent,
                viewport.EntryExtent)
            : viewport.EntryExtent;
        foreach (var entry in visibleEntries)
        {
            var child = entry switch
            {
                BrowserTabEntry tab => CreateTabCard(tab, vertical, renderedEntryExtent),
                TabGroupHeaderEntry group => CreateGroupButton(group, vertical, renderedEntryExtent),
                _ => throw new InvalidOperationException("Unknown tab strip entry."),
            };
            if (entry is BrowserTabEntry tabEntry &&
                hoverPreviewGroupId is { } previewGroupId && tabEntry.GroupId == previewGroupId &&
                !viewport.VisibleEntries.OfType<BrowserTabEntry>().Any(tab => tab.TabId == tabEntry.TabId))
            {
                var previewAction = Descendants(child).OfType<Button>()
                    .FirstOrDefault(button => button.Tag is BrowserTabEntry);
                if (previewAction is not null)
                {
                    AutomationProperties.SetItemStatus(previewAction, "Hover preview");
                }
            }
            entries.Children.Add(child);
        }
        previous.IsEnabled = viewport.CanPagePrevious;
        next.IsEnabled = viewport.CanPageNext;
        more.IsEnabled = viewport.HiddenCount > 0;
        UpdateOverflowScrollBar(canonical.Count, viewport);
        newTab.IsEnabled = state.Projection.HostState is TabControllerHostState.Docked or TabControllerHostState.Detached;
        var moreLabel = viewport.HiddenCount == 0 ? "More tabs" : $"More tabs — {viewport.HiddenCount} hidden";
        AutomationProperties.SetName(more, moreLabel);
        AutomationProperties.SetHelpText(more, "Open the full tab list in canonical order.");
        SetVisibleCommandLabel(more, viewport.HiddenCount == 0 ? "More" : $"More ({viewport.HiddenCount})");
        detachDock.IsEnabled = state.Projection.HostState is TabControllerHostState.Docked or TabControllerHostState.Detached;
        announcer.Text = state.Announcement ?? string.Empty;
    }

    private void UpdateOverflowScrollBar(int canonicalCount, TabStripViewportProjection projection)
    {
        updatingOverflowScrollBar = true;
        try
        {
            var maximum = Math.Max(0, canonicalCount - projection.Count);
            overflowScrollBar.Maximum = maximum;
            overflowScrollBar.ViewportSize = Math.Max(1, projection.Count);
            overflowScrollBar.LargeChange = Math.Max(1, projection.Count);
            overflowScrollBar.Value = Math.Clamp(projection.StartIndex, 0, maximum);
            overflowScrollBar.Visibility = projection.HiddenCount > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            AutomationProperties.SetItemStatus(
                overflowScrollBar,
                projection.HiddenCount > 0
                    ? $"Showing tabs {projection.StartIndex + 1} through {projection.StartIndex + projection.Count} of {canonicalCount}."
                    : "All tabs are visible.");
        }
        finally
        {
            updatingOverflowScrollBar = false;
        }
    }

    private FrameworkElement CreateTabCard(BrowserTabEntry tab, bool vertical, double entryExtent)
    {
        var visual = ResolveTabVisual(tab);
        var interaction = ResolveTabInteraction(tab.TabId);
        var showAudio = interaction is { IsPlayingAudio: true, Mute.IsAvailable: true };
        var availableCardWidth = vertical
            ? Math.Clamp(
                visibleColumnWidth - TabStripViewportModel.VerticalColumnSpacing,
                TabStripViewportModel.MinimumVerticalColumnWidth - TabStripViewportModel.VerticalColumnSpacing,
                TabStripViewportModel.PreferredVerticalColumnWidth)
            : entryExtent;
        var faviconOnly = compactMode || tab.IsCompact ||
                          availableCardWidth < TabStripViewportModel.ReadableTitleThreshold;
        var titleWidth = vertical
            ? showAudio ? 50d : 94d
            : Math.Clamp(availableCardWidth - (showAudio ? 134d : 90d), 44d, 92d);
        var verticalCardWidth = faviconOnly
            ? Math.Min(availableCardWidth, showAudio ? 140d : 96d)
            : availableCardWidth;
        var card = new Grid
        {
            Height = 44,
            MinHeight = 44,
            MinWidth = showAudio
                ? TabStripViewportModel.AudibleCompactTopTabExtent
                : TabStripViewportModel.CompactTopTabExtent,
            MaxWidth = vertical
                ? verticalCardWidth
                : entryExtent,
            Width = vertical
                ? verticalCardWidth
                : availableCardWidth,
            Margin = vertical ? new Thickness(0, 0, 0, 2) : new Thickness(0, 0, 3, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var select = new Button
        {
            Tag = tab,
            Content = CreateTabIdentityContent(tab, visual, faviconOnly, titleWidth),
            MinHeight = 44,
            MinWidth = 44,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 0, 8, 0),
        };
        OrbitVisualTheme.ApplyCompactRailButton(select, OrbitButtonRole.Tab);
        AutomationProperties.SetName(select, $"{visual.Title}, tab");
        AutomationProperties.SetHelpText(select, BuildTabHelpText(tab, visual));
        select.Click += async (_, _) => await ExecuteAsync(new SelectTabControllerAction(Guid.NewGuid(), tab.TabId));
        select.GotKeyboardFocus += (_, _) => focusedTabId = tab.TabId;
        select.ContextMenu = BuildTabMenu(tab);
        select.PreviewMouseLeftButtonDown += (_, args) =>
        {
            dragSourceTabId = tab.TabId;
            dragStart = args.GetPosition(this);
        };
        select.PreviewMouseMove += BeginTabDrag;
        card.AllowDrop = true;
        card.DragOver += (_, args) =>
        {
            args.Effects = TryReadDraggedTab(args.Data, out var source) && source != tab.TabId
                ? DragDropEffects.Move
                : DragDropEffects.None;
            args.Handled = true;
        };
        card.Drop += async (_, args) =>
        {
            if (TryReadDraggedTab(args.Data, out var source))
            {
                args.Handled = await HandleTabDropAsync(source, tab.TabId);
            }
        };
        card.Children.Add(select);

        if (showAudio)
        {
            var audioGlyph = new TabAudioGlyph
            {
                IsMuted = interaction!.IsMuted,
                Width = 20,
                Height = 20,
            };
            audioGlyph.BindStrokeToAncestorForeground();
            var audioName = interaction.IsMuted
                ? $"Unmute tab — {visual.Title}"
                : $"Mute tab — {visual.Title}";
            var audio = new OrbitShapedCommand
            {
                Shape = OrbitCommandShape.Port,
                ReducedMotion = ReducedMotion,
                Content = audioGlyph,
                MinWidth = 44,
                MinHeight = 44,
                ToolTip = interaction.IsMuted ? "Unmute this tab" : "Mute this tab",
            };
            Grid.SetColumn(audio, 1);
            AutomationProperties.SetName(audio, audioName);
            AutomationProperties.SetItemStatus(audio, interaction.IsMuted
                ? "Audio playing, muted"
                : "Audio playing");
            AutomationProperties.SetHelpText(audio,
                $"{(interaction.IsMuted ? "Resume" : "Mute")} audio from this tab only.");
            audio.Click += async (_, _) => await ExecuteAsync(new SetTabMutedControllerAction(
                Guid.NewGuid(),
                tab.TabId,
                !interaction.IsMuted));
            card.Children.Add(audio);
        }

        var closeGlyph = new OrbitControllerGlyph
        {
            Kind = OrbitControllerGlyphKind.CloseTab,
            Width = 18,
            Height = 18,
        };
        closeGlyph.BindStrokeToAncestorForeground();
        var close = new OrbitShapedCommand
        {
            Shape = OrbitCommandShape.Fin,
            Role = OrbitCommandRole.Destructive,
            ReducedMotion = ReducedMotion,
            Content = closeGlyph,
            MinWidth = 44,
            MinHeight = 44,
            ToolTip = $"Close tab: {visual.Title} (Ctrl+W)",
            IsEnabled = CanCloseTabs(),
        };
        Grid.SetColumn(close, 2);
        AutomationProperties.SetName(close, $"Close tab — {visual.Title}");
        AutomationProperties.SetAcceleratorKey(close, "Ctrl+W");
        AutomationProperties.SetHelpText(close, close.IsEnabled
            ? $"Close {visual.Title}. This discards its current in-memory page state."
            : "The only tab stays open. Create another tab before closing this one.");
        close.Click += (_, _) => RequestCloseTab(tab.TabId);
        card.Children.Add(close);
        return card;
    }

    private Button CreateGroupButton(TabGroupHeaderEntry group, bool vertical, double entryExtent)
    {
        var availableGroupWidth = vertical
            ? Math.Clamp(
                visibleColumnWidth - TabStripViewportModel.VerticalColumnSpacing,
                TabStripViewportModel.MinimumVerticalColumnWidth - TabStripViewportModel.VerticalColumnSpacing,
                TabStripViewportModel.PreferredVerticalColumnWidth)
            : entryExtent;
        var faviconOnly = compactMode || group.IsCompact ||
                          availableGroupWidth < TabStripViewportModel.ReadableTitleThreshold;
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new OrbitEmberStar(OrbitEmberStarKind.TabGroup)
        {
            IsActive = !group.IsCollapsed,
            ReducedMotion = ReducedMotion,
            Width = 22,
            Height = 22,
            Margin = faviconOnly ? new Thickness(0) : new Thickness(0, 0, 7, 0),
        });
        if (!faviconOnly)
        {
            content.Children.Add(new TextBlock
            {
                Text = $"{group.Name}  {group.TabCount}",
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }
        var button = new TabGroupHeaderButton
        {
            Tag = group,
            Content = content,
            GroupName = group.Name,
            TabCount = group.TabCount,
            IsCollapsed = group.IsCollapsed,
            MinHeight = 44,
            MinWidth = faviconOnly ? 44 : 112,
            Width = vertical
                ? faviconOnly ? 48 : availableGroupWidth
                : Math.Min(entryExtent, TabStripViewportModel.PreferredTopTabExtent),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Margin = vertical ? new Thickness(0, 6, 0, 4) : new Thickness(6, 0, 4, 0),
        };
        OrbitVisualTheme.ApplyCompactRailButton(button, OrbitButtonRole.Group);
        AutomationProperties.SetName(button,
            $"{group.Name}, group, {group.TabCount} tabs, {(group.IsCollapsed ? "collapsed" : "expanded")}");
        button.ContextMenu = BuildGroupContextMenu(group, button);
        button.ContextMenuOpening += (_, _) => CloseGroupPreview();
        button.Click += async (_, _) =>
        {
            if (group.UsesPreviewDropdown)
            {
                OpenGroupPreview(group, button, restoreFocus: true);
                return;
            }

            await ExecuteAsync(new ToggleTabGroupControllerAction(
                Guid.NewGuid(), group.GroupId, !group.IsCollapsed));
        };
        if (state?.Projection.PreviewRevealMode == WorkspacePreviewRevealMode.Hover)
        {
            button.MouseEnter += (_, _) => RevealGroupOnHover(group, button);
            button.GotKeyboardFocus += (_, _) => RevealGroupOnFocus(group, button);
        }
        button.PreviewMouseRightButtonDown += (_, _) =>
        {
            CloseGroupPreview();
        };
        return button;
    }

    private IReadOnlyList<TabStripEntry> ExpandHoveredSmallGroup(IReadOnlyList<TabStripEntry> visible)
    {
        if (hoverPreviewGroupId is not { } groupId || state is null ||
            state.Projection.PreviewRevealMode != WorkspacePreviewRevealMode.Hover)
        {
            return visible;
        }

        var header = visible.OfType<TabGroupHeaderEntry>()
            .FirstOrDefault(group => group.GroupId == groupId && !group.UsesPreviewDropdown);
        if (header is null || header.TabPreviews.Count == 0)
        {
            return visible;
        }

        var result = new List<TabStripEntry>(visible.Count + header.TabPreviews.Count);
        var represented = visible.OfType<BrowserTabEntry>().Select(tab => tab.TabId).ToHashSet();
        foreach (var entry in visible)
        {
            result.Add(entry);
            if (!ReferenceEquals(entry, header))
            {
                continue;
            }

            foreach (var preview in header.TabPreviews.Where(preview => !represented.Contains(preview.TabId)))
            {
                result.Add(new BrowserTabEntry(
                    preview.TabId,
                    header.GroupId,
                    string.IsNullOrWhiteSpace(preview.Title) ? "New Tab" : preview.Title,
                    preview.Address,
                    BrowserLoadState.Idle,
                    preview.TabId == state.Projection.Tabs.SelectedTabId,
                    state.Projection.IsPrivate,
                    false,
                    false));
            }
        }
        return result;
    }

    private void RevealGroupOnHover(TabGroupHeaderEntry group, Button anchor)
    {
        if (group.UsesPreviewDropdown)
        {
            OpenGroupPreview(group, anchor, restoreFocus: false);
            return;
        }

        var available = entries.Orientation == Orientation.Horizontal
            ? entriesViewport.ActualWidth
            : entriesViewport.ActualHeight;
        var missingPreviewCount = group.TabPreviews.Count(preview =>
            viewport?.VisibleEntries.OfType<BrowserTabEntry>().All(tab => tab.TabId != preview.TabId) != false);
        var requiredEntryCount = Math.Max(1, viewport?.VisibleEntries.Count ?? 1) + missingPreviewCount;
        var requiredFloor = entries.Orientation == Orientation.Horizontal
            ? requiredEntryCount * TabStripViewportModel.CompactTopTabExtent
            : Math.Ceiling(requiredEntryCount / (double)Math.Max(1, visibleColumnCount)) *
              TabStripViewportModel.SafeVerticalEntryExtent;
        if (available > 0 && available < requiredFloor)
        {
            OpenGroupPreview(group, anchor, restoreFocus: false);
            announcer.Text = $"{group.Name} opened as a preview list because the tab strip is narrow.";
            return;
        }

        if (hoverPreviewGroupId == group.GroupId)
        {
            return;
        }
        hoverPreviewGroupId = group.GroupId;
        RenderAcceptedState(animateViewport: true, direction: 1);
        announcer.Text = $"{group.Name} preview expanded inline. {group.TabCount} tabs.";
    }

    private void RevealGroupOnFocus(TabGroupHeaderEntry group, Button anchor)
    {
        if (group.UsesPreviewDropdown)
        {
            return;
        }
        RevealGroupOnHover(group, anchor);
        if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
        {
            _ = Dispatcher.InvokeAsync(() =>
            {
                Descendants(entries).OfType<Button>()
                    .FirstOrDefault(button => button.Tag is TabGroupHeaderEntry candidate &&
                                              candidate.GroupId == group.GroupId)
                    ?.Focus();
            }, System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void OnRailMouseLeave(object sender, MouseEventArgs args)
    {
        if (hoverPreviewGroupId is null || rail.IsKeyboardFocusWithin || IsAnyGroupMenuOpen())
        {
            return;
        }
        hoverPreviewGroupId = null;
        RenderAcceptedState(animateViewport: true, direction: -1);
        announcer.Text = "Tab group hover preview closed.";
    }

    /// <summary>
    /// Projects one drag gesture into a typed revisioned controller action. Dropping
    /// onto an ungrouped tab creates a temporary group; dropping onto a grouped tab
    /// joins it; dropping on the rail removes the source from its current group.
    /// </summary>
    public async ValueTask<bool> HandleTabDropAsync(BrowserTabId sourceTabId, BrowserTabId? targetTabId)
    {
        if (state is null || sourceTabId.IsEmpty || sourceTabId == targetTabId)
        {
            return false;
        }

        var tabs = state.Projection.Tabs.Entries.OfType<BrowserTabEntry>().ToArray();
        var source = tabs.FirstOrDefault(tab => tab.TabId == sourceTabId);
        if (source is null)
        {
            return false;
        }

        if (targetTabId is null)
        {
            if (source.GroupId is null)
            {
                return false;
            }

            await ExecuteAsync(new MoveTabControllerAction(
                Guid.NewGuid(), sourceTabId, CanonicalTabIndex(sourceTabId), null));
            return true;
        }

        var target = tabs.FirstOrDefault(tab => tab.TabId == targetTabId.Value);
        if (target is null)
        {
            return false;
        }

        if (target.GroupId is { } targetGroupId)
        {
            await ExecuteAsync(new MoveTabControllerAction(
                Guid.NewGuid(), sourceTabId, CanonicalTabIndex(target.TabId), targetGroupId));
        }
        else
        {
            await ExecuteAsync(new CreateTabGroupControllerAction(
                Guid.NewGuid(), [target.TabId, sourceTabId], "New group"));
        }

        return true;
    }

    private void BeginTabDrag(object sender, MouseEventArgs args)
    {
        if (args.LeftButton != MouseButtonState.Pressed || dragSourceTabId is not { } tabId)
        {
            return;
        }

        var current = args.GetPosition(this);
        if (Math.Abs(current.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var data = new DataObject(TabDragFormat, tabId.Value.ToString("D"));
        dragSourceTabId = null;
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move);
    }

    private async void OnEntriesDrop(object sender, DragEventArgs args)
    {
        if (!args.Handled && TryReadDraggedTab(args.Data, out var source))
        {
            args.Handled = await HandleTabDropAsync(source, null);
        }
    }

    private static bool TryReadDraggedTab(IDataObject data, out BrowserTabId tabId)
    {
        tabId = default;
        return data.GetDataPresent(TabDragFormat) &&
               data.GetData(TabDragFormat) is string value &&
               Guid.TryParse(value, out var parsed) &&
               !(tabId = new BrowserTabId(parsed)).IsEmpty;
    }

    private int CanonicalTabIndex(BrowserTabId tabId)
    {
        if (state is null) return 0;
        var index = 0;
        foreach (var entry in state.Projection.Tabs.Entries)
        {
            switch (entry)
            {
                case BrowserTabEntry tab:
                    if (tab.TabId == tabId) return index;
                    index++;
                    break;
                case TabGroupHeaderEntry group:
                    var groupIndex = group.TabIds.ToList().FindIndex(id => id == tabId);
                    if (groupIndex >= 0) return index + groupIndex;
                    index += group.TabCount;
                    break;
            }
        }
        return Math.Max(0, index - 1);
    }

    private ContextMenu BuildGroupContextMenu(TabGroupHeaderEntry group, Button anchor)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem(group.IsCollapsed ? "Expand group" : "Collapse group", async () =>
            await ExecuteAsync(new ToggleTabGroupControllerAction(Guid.NewGuid(), group.GroupId, !group.IsCollapsed))));
        menu.Items.Add(MenuItem("Preview tabs", () => OpenGroupPreview(group, anchor, restoreFocus: true)));
        menu.Items.Add(MenuItem("Rename group…", () => RenameGroup(group)));

        var colors = new MenuItem { Header = "Group color" };
        AutomationProperties.SetName(colors, "Group color");
        foreach (var token in WorkspaceColorCatalog.Tokens)
        {
            var color = token;
            var item = MenuItem(color, async () => await ExecuteAsync(
                new SetTabGroupColorControllerAction(Guid.NewGuid(), group.GroupId, color)));
            item.IsCheckable = true;
            item.IsChecked = color.Equals(group.ColorToken, StringComparison.OrdinalIgnoreCase);
            colors.Items.Add(item);
        }
        menu.Items.Add(colors);
        if (group.IsTemporary)
        {
            menu.Items.Add(MenuItem("Save as workspace…", () => SaveGroupAsWorkspace(group)));
        }
        menu.Items.Add(MenuItem("Ungroup tabs", async () => await ExecuteAsync(
            new UngroupTabGroupControllerAction(Guid.NewGuid(), group.GroupId))));
        menu.Items.Add(MenuItem("Close group", () => CloseGroup(group)));
        menu.Closed += (_, _) => CollapseHoverPreviewIfInactive();
        OrbitVisualTheme.ApplyContextMenu(menu);
        return menu;
    }

    private void OpenGroupPreview(TabGroupHeaderEntry group, Button anchor, bool restoreFocus)
    {
        CloseGroupPreview();
        var menu = new ContextMenu
        {
            PlacementTarget = anchor,
            Placement = ResolveGroupPreviewPlacement(),
            HorizontalOffset = state?.Projection.Placement == TabStripPlacement.Top ? 0 : 4,
            VerticalOffset = state?.Projection.Placement == TabStripPlacement.Top ? 4 : 0,
        };
        foreach (var tab in group.TabPreviews)
        {
            var label = string.IsNullOrWhiteSpace(tab.Title) ? tab.Address?.Host ?? "New Tab" : tab.Title;
            var item = MenuItem(label, async () => await ExecuteAsync(
                new SelectTabControllerAction(Guid.NewGuid(), tab.TabId)));
            AutomationProperties.SetHelpText(item, tab.Address is null ? "Switch to this tab." : $"{tab.Address}. Switch to this tab.");
            menu.Items.Add(item);
        }
        if (group.TabPreviews.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "No tab previews available", IsEnabled = false });
        }
        groupPreviewMenu = menu;
        menu.PreviewMouseRightButtonDown += (_, args) =>
        {
            // A hover-opened ContextMenu has its own popup HWND. If the pointer reaches that
            // popup before the group header, route the user's group-action gesture back to
            // the stable header action menu instead of trapping it in the preview list.
            args.Handled = true;
            CloseGroupPreview();
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            {
                _ = Dispatcher.InvokeAsync(
                    () => OpenGroupActionMenu(anchor),
                    System.Windows.Threading.DispatcherPriority.Input);
            }
        };
        menu.PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Apps &&
                (args.Key != Key.F10 || Keyboard.Modifiers != ModifierKeys.Shift))
            {
                return;
            }

            args.Handled = true;
            CloseGroupPreview();
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            {
                _ = Dispatcher.InvokeAsync(
                    () => OpenGroupActionMenu(anchor),
                    System.Windows.Threading.DispatcherPriority.Input);
            }
        };
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(groupPreviewMenu, menu))
            {
                groupPreviewMenu = null;
            }
            if (restoreFocus)
            {
                anchor.Focus();
            }
            CollapseHoverPreviewIfInactive();
        };
        OrbitVisualTheme.ApplyContextMenu(menu);
        menu.IsOpen = true;
    }

    private PlacementMode ResolveGroupPreviewPlacement()
    {
        if (SurfaceKind == TabControllerSurfaceKind.Detached)
        {
            return PlacementMode.Right;
        }

        return state?.Projection.Placement switch
        {
            TabStripPlacement.Left => PlacementMode.Right,
            TabStripPlacement.Right => PlacementMode.Left,
            _ => PlacementMode.Bottom,
        };
    }

    private void OpenGroupActionMenu(Button anchor)
    {
        CloseGroupPreview();
        if (anchor.ContextMenu is not { } actionMenu)
        {
            return;
        }

        actionMenu.PlacementTarget = anchor;
        actionMenu.Placement = ResolveGroupPreviewPlacement();
        actionMenu.IsOpen = true;
    }

    private void CloseGroupPreview()
    {
        if (groupPreviewMenu is not { } preview)
        {
            return;
        }

        preview.IsOpen = false;
        if (ReferenceEquals(groupPreviewMenu, preview))
        {
            groupPreviewMenu = null;
        }
    }

    private bool IsAnyGroupMenuOpen() =>
        groupPreviewMenu?.IsOpen == true ||
        Descendants(entries).OfType<Button>().Any(button => button.ContextMenu?.IsOpen == true);

    private void CollapseHoverPreviewIfInactive()
    {
        if (hoverPreviewGroupId is null || rail.IsMouseOver || rail.IsKeyboardFocusWithin ||
            IsAnyGroupMenuOpen() || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(() =>
        {
            if (hoverPreviewGroupId is null || rail.IsMouseOver || rail.IsKeyboardFocusWithin ||
                IsAnyGroupMenuOpen())
            {
                return;
            }
            hoverPreviewGroupId = null;
            RenderAcceptedState(animateViewport: true, direction: -1);
            announcer.Text = "Tab group hover preview closed.";
        }, System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private void RenameGroup(TabGroupHeaderEntry group)
    {
        var dialog = new TabGroupRenameDialog(group.GroupId, group.Name, state?.Projection.IsPrivate == true)
        {
            Owner = Window.GetWindow(this),
        };
        if (dialog.ShowDialog() == true && dialog.Draft is { } draft)
        {
            _ = ExecuteAsync(new RenameTabGroupControllerAction(Guid.NewGuid(), draft.GroupId, draft.RequestedName));
        }
    }

    private void SaveGroupAsWorkspace(TabGroupHeaderEntry group)
    {
        var tabs = group.TabPreviews
            .Where(tab => tab.Address is { IsAbsoluteUri: true } address && address.Scheme is "http" or "https")
            .Select(tab => new WorkspacePresetTabPresentation(
            tab.Address!,
            string.IsNullOrWhiteSpace(tab.Title) ? tab.Address!.Host : tab.Title)
        {
            FaviconPng = tab.FaviconPng,
        }).ToArray();
        if (tabs.Length == 0)
        {
            announcer.Text = "This group has no saveable web pages.";
            return;
        }
        var dialog = new SaveTabGroupWorkspaceDialog(
            group.GroupId, group.Name, group.ColorToken, tabs, LocalArtworkImporter)
        {
            Owner = Window.GetWindow(this),
        };
        if (dialog.ShowDialog() == true && dialog.Draft is { } draft)
        {
            _ = ExecuteAsync(new SaveTabGroupAsWorkspaceControllerAction(Guid.NewGuid(), group.GroupId, draft));
        }
    }

    private void CloseGroup(TabGroupHeaderEntry group)
    {
        if (group.TabIds.Count > 1)
        {
            var dialog = new TabGroupCloseConfirmationDialog(group.Name, group.TabIds.Count)
            {
                Owner = Window.GetWindow(this),
            };
            if (dialog.ShowDialog() != true) return;
        }
        _ = ExecuteAsync(new CloseTabGroupControllerAction(Guid.NewGuid(), group.GroupId, group.TabIds));
    }

    private FrameworkElement CreateTabIdentityContent(
        BrowserTabEntry tab,
        (string Title, string SiteName, TabVisualMetadataPresentation? Metadata) visual,
        bool faviconOnly,
        double titleWidth)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(CreateFavicon(tab, visual));
        if (faviconOnly)
        {
            return row;
        }

        row.Children.Add(new TextBlock
        {
            Text = visual.Title,
            FontSize = 12.5,
            FontWeight = tab.IsSelected ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = titleWidth,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        return row;
    }

    private FrameworkElement CreateFavicon(
        BrowserTabEntry tab,
        (string Title, string SiteName, TabVisualMetadataPresentation? Metadata) visual)
    {
        if (!SystemParameters.HighContrast && visual.Metadata is { FaviconPng.IsEmpty: false } metadata)
        {
            if (!faviconCache.TryGetValue(tab.TabId, out var cached) || cached.Revision != metadata.Revision)
            {
                var decoded = DecodeFavicon(metadata.FaviconPng);
                if (decoded is not null)
                {
                    faviconCache[tab.TabId] = (metadata.Revision, decoded);
                    cached = (metadata.Revision, decoded);
                }
            }

            if (cached.Source is not null)
            {
                return new Image
                {
                    Source = cached.Source,
                    Width = 20,
                    Height = 20,
                    Stretch = Stretch.Uniform,
                    SnapsToDevicePixels = true,
                    IsHitTestVisible = false,
                };
            }
        }

        var fallbackText = visual.SiteName.FirstOrDefault(char.IsLetterOrDigit);
        return new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(6),
            Background = SystemParameters.HighContrast
                ? SystemColors.ControlBrush
                : tab.LoadState == BrowserLoadState.Loading
                    ? OrbitVisualTheme.WaypointGold
                    : OrbitVisualTheme.SeaGlassStrong,
            BorderBrush = SystemParameters.HighContrast ? SystemColors.ControlTextBrush : OrbitVisualTheme.SeaGlass,
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = fallbackText == default ? "•" : char.ToUpperInvariant(fallbackText).ToString(),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = SystemParameters.HighContrast ? SystemColors.ControlTextBrush : OrbitVisualTheme.Ink,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            IsHitTestVisible = false,
        };
    }

    private (string Title, string SiteName, TabVisualMetadataPresentation? Metadata) ResolveTabVisual(
        BrowserTabEntry tab)
    {
        TabVisualMetadataPresentation? metadata = null;
        if (state is not null)
        {
            state.TabVisuals.TryGetValue(tab.TabId, out metadata);
        }
        var addressHost = string.IsNullOrWhiteSpace(tab.Address?.Host) ? string.Empty : tab.Address.Host;
        var fallbackTitle = string.IsNullOrWhiteSpace(tab.Title) ? string.Empty : tab.Title.Trim();
        var isDefaultNewTabTitle = fallbackTitle.Equals("New tab", StringComparison.OrdinalIgnoreCase);
        var title = !string.IsNullOrWhiteSpace(metadata?.PageTitle)
            ? metadata.PageTitle
            : tab.Address is not null && (fallbackTitle.Length == 0 || isDefaultNewTabTitle)
                ? !string.IsNullOrWhiteSpace(metadata?.SiteName)
                    ? metadata.SiteName
                    : addressHost.Length > 0 ? addressHost : "New Tab"
                : fallbackTitle.Length == 0 || isDefaultNewTabTitle ? "New Tab" : fallbackTitle;
        var siteName = !string.IsNullOrWhiteSpace(metadata?.SiteName)
            ? metadata.SiteName
            : addressHost.Length > 0 ? addressHost : "New Tab";
        return (title, siteName, metadata);
    }

    private TabInteractionCapabilitiesPresentation? ResolveTabInteraction(BrowserTabId tabId)
    {
        if (state is not null && state.TabInteractions.TryGetValue(tabId, out var interaction))
        {
            return interaction;
        }

        return null;
    }

    private static string BuildTabHelpText(
        BrowserTabEntry tab,
        (string Title, string SiteName, TabVisualMetadataPresentation? Metadata) visual)
    {
        var selection = tab.IsSelected ? "Selected. " : string.Empty;
        var distinctSite = IsMateriallyDistinctSiteName(visual.Title, visual.SiteName)
            ? $"Site: {visual.SiteName}. "
            : string.Empty;
        return $"{selection}{distinctSite}Press Enter to switch.";
    }

    private static bool IsMateriallyDistinctSiteName(string title, string siteName) =>
        !string.IsNullOrWhiteSpace(siteName) &&
        !siteName.Equals("New Tab", StringComparison.OrdinalIgnoreCase) &&
        !title.Equals(siteName, StringComparison.OrdinalIgnoreCase);

    private static ImageSource? DecodeFavicon(ReadOnlyMemory<byte> bytes)
    {
        if (!TabVisualMetadataPresentation.IsSafePng(bytes.Span))
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(bytes.ToArray(), writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            image.DecodePixelWidth = 32;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private ContextMenu BuildTabMenu(BrowserTabEntry tab)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Switch to tab", async () => await ExecuteAsync(new SelectTabControllerAction(Guid.NewGuid(), tab.TabId))));
        menu.Items.Add(MenuItem("Duplicate tab", async () => await ExecuteAsync(
            new DuplicateTabControllerAction(Guid.NewGuid(), tab.TabId))));
        menu.Items.Add(MenuItem(
            "Close tab",
            () => RequestCloseTab(tab.TabId),
            CanCloseTabs(),
            "The only tab stays open. Create another tab before closing this one."));

        var groupMenu = new MenuItem { Header = "Tab group" };
        AutomationProperties.SetName(groupMenu, "Tab group");
        groupMenu.Items.Add(MenuItem(
            "Create new group with this tab",
            async () => await ExecuteAsync(new CreateTabGroupControllerAction(
                Guid.NewGuid(), [tab.TabId], "New group"))));
        var canonicalIndex = state?.Projection.Tabs.Entries
            .Select((entry, index) => (entry, index))
            .Where(item => item.entry is BrowserTabEntry candidate && candidate.TabId == tab.TabId)
            .Select(item => item.index)
            .DefaultIfEmpty(0)
            .First() ?? 0;
        var groups = state?.Projection.Tabs.Entries
            .OfType<TabGroupHeaderEntry>()
            .Where(group => group.GroupId != tab.GroupId)
            .GroupBy(group => group.GroupId)
            .Select(group => group.First())
            .ToArray() ?? [];
        if (groups.Length > 0)
        {
            var moveMenu = new MenuItem { Header = "Move to group" };
            AutomationProperties.SetName(moveMenu, "Move tab to group");
            foreach (var group in groups)
            {
                moveMenu.Items.Add(MenuItem(
                    group.Name,
                    async () => await ExecuteAsync(new MoveTabControllerAction(
                        Guid.NewGuid(), tab.TabId, canonicalIndex, group.GroupId))));
            }
            groupMenu.Items.Add(moveMenu);
        }
        if (tab.GroupId is not null)
        {
            groupMenu.Items.Add(MenuItem(
                "Remove from group",
                async () => await ExecuteAsync(new MoveTabControllerAction(
                    Guid.NewGuid(), tab.TabId, canonicalIndex, null))));
        }
        menu.Items.Add(groupMenu);

        menu.Items.Add(BuildTabAudioMenu(tab));

        var siteControls = ResolveTabInteraction(tab.TabId)?.SiteContentControls
            .Where(control => control.IsEnforced)
            .ToArray() ?? [];
        if (siteControls.Length > 0)
        {
            var contentMenu = new MenuItem { Header = "Site content controls" };
            AutomationProperties.SetName(contentMenu, "Site content controls");
            foreach (var control in siteControls)
            {
                var label = control.Kind switch
                {
                    TabSiteContentControlKind.AdBlocking => "Ad blocker",
                    TabSiteContentControlKind.ScriptBlocking => "Script blocker",
                    _ => throw new ArgumentOutOfRangeException(),
                };
                var item = MenuItem(
                    $"{label}: {(control.IsEnabled ? "On" : "Off")}",
                    async () => await ExecuteAsync(new SetTabSiteContentControlControllerAction(
                        Guid.NewGuid(), tab.TabId, control.Kind, !control.IsEnabled)),
                    control.CanToggle,
                    control.UnavailableReason);
                item.IsCheckable = true;
                item.IsChecked = control.IsEnabled;
                contentMenu.Items.Add(item);
            }
            menu.Items.Add(contentMenu);
        }
        OrbitVisualTheme.ApplyContextMenu(menu);
        return menu;
    }

    private MenuItem BuildTabAudioMenu(BrowserTabEntry tab)
    {
        var interaction = ResolveTabInteraction(tab.TabId) ??
            TabInteractionCapabilitiesPresentation.Unavailable(tab.TabId, 1);
        var menu = new MenuItem { Header = "Tab Audio" };
        AutomationProperties.SetName(menu, "Tab Audio");
        AutomationProperties.SetHelpText(menu, interaction.IsPlayingAudio
            ? "Audio controls for this tab."
            : "This tab is not currently playing audio.");

        var muteLabel = interaction.IsMuted ? "Unmute this tab" : "Mute this tab";
        var mute = MenuItem(
            muteLabel,
            async () => await ExecuteAsync(new SetTabMutedControllerAction(
                Guid.NewGuid(), tab.TabId, !interaction.IsMuted)),
            interaction.Mute.IsAvailable,
            interaction.Mute.UnavailableReason);
        mute.IsCheckable = true;
        mute.IsChecked = interaction.IsMuted;
        AutomationProperties.SetItemStatus(mute, interaction.IsMuted ? "Muted" : "Not muted");
        menu.Items.Add(mute);
        menu.Items.Add(new Separator());
        menu.Items.Add(UnavailableAudioItem("Volume", interaction.Volume));
        menu.Items.Add(UnavailableAudioItem("Equalizer", interaction.Equalizer));
        menu.Items.Add(UnavailableAudioItem("Left / right balance", interaction.Balance));
        menu.Items.Add(UnavailableAudioItem("Output device", interaction.OutputDevice));
        return menu;
    }

    private static MenuItem UnavailableAudioItem(
        string label,
        TabAudioFeatureCapabilityPresentation capability)
    {
        var item = new MenuItem
        {
            Header = capability.IsAvailable ? label : $"{label} — unavailable",
            IsEnabled = false,
        };
        AutomationProperties.SetName(item, label);
        AutomationProperties.SetItemStatus(item, capability.IsAvailable
            ? "Available when the verified host control is presented."
            : "Unavailable");
        AutomationProperties.SetHelpText(item, capability.IsAvailable
            ? "This capability is available, but no compact menu control is exposed here."
            : capability.UnavailableReason);
        return item;
    }

    private void OpenOverflow()
    {
        if (state is null)
        {
            return;
        }

        var menu = new ContextMenu { PlacementTarget = more };
        foreach (var entry in state.Projection.Tabs.Entries)
        {
            switch (entry)
            {
                case TabGroupHeaderEntry group:
                    menu.Items.Add(new MenuItem
                    {
                        Header = $"{group.Name} — {group.TabCount} tabs",
                        IsEnabled = false,
                    });
                    break;
                case BrowserTabEntry tab:
                    var visual = ResolveTabVisual(tab);
                    var contextLabel = IsMateriallyDistinctSiteName(visual.Title, visual.SiteName)
                        ? $"{visual.Title}  —  {visual.SiteName}"
                        : visual.Title;
                    var row = new MenuItem
                    {
                        Header = $"{contextLabel}{(tab.IsSelected ? "  •  Selected" : string.Empty)}",
                        IsCheckable = true,
                        IsChecked = tab.IsSelected,
                    };
                    AutomationProperties.SetName(row,
                        $"{contextLabel}, {(tab.IsSelected ? "selected" : "not selected")}");
                    row.Items.Add(MenuItem("Switch", async () => await ExecuteAsync(new SelectTabControllerAction(Guid.NewGuid(), tab.TabId))));
                    row.Items.Add(MenuItem(
                        "Close",
                        () => RequestCloseTab(tab.TabId),
                        CanCloseTabs(),
                        "The only tab stays open. Create another tab before closing this one."));
                    menu.Items.Add(row);
                    break;
            }
        }

        menu.Closed += (_, _) => more.Focus();
        OrbitVisualTheme.ApplyContextMenu(menu);
        more.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private async void PagePrevious()
    {
        if (viewport is null) return;
        pageStart = Math.Max(0, viewport.StartIndex - Math.Max(1, viewport.Count));
        focusedTabId = null;
        viewportNavigationActive = true;
        RenderAcceptedState();
        previous.Focus();
        announcer.Text = "Showing previous tabs.";
        await Task.CompletedTask;
    }

    private async void PageNext()
    {
        if (viewport is null) return;
        pageStart = viewport.StartIndex + Math.Max(1, viewport.Count);
        focusedTabId = null;
        viewportNavigationActive = true;
        RenderAcceptedState();
        next.Focus();
        announcer.Text = "Showing next tabs.";
        await Task.CompletedTask;
    }

    private void ToggleResources()
    {
        ApplyResourcePanelVisibility(!IsResourcePanelOpen);
        ResourcePanelVisibilityChanged?.Invoke(this, IsResourcePanelOpen);
        announcer.Text = IsResourcePanelOpen ? "Browser resources opened." : "Browser resources closed.";
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        AttachDetachedOwnerWindow();
        UpdateDetachedPanelAllocation();
        RenderAcceptedState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (detachedOwnerWindow is not null)
        {
            detachedOwnerWindow.SizeChanged -= OnDetachedOwnerSizeChanged;
            detachedOwnerWindow = null;
        }
    }

    private void AttachDetachedOwnerWindow()
    {
        if (SurfaceKind != TabControllerSurfaceKind.Detached)
        {
            return;
        }

        var owner = Window.GetWindow(this);
        if (ReferenceEquals(owner, detachedOwnerWindow))
        {
            return;
        }

        if (detachedOwnerWindow is not null)
        {
            detachedOwnerWindow.SizeChanged -= OnDetachedOwnerSizeChanged;
        }
        detachedOwnerWindow = owner;
        if (detachedOwnerWindow is not null)
        {
            detachedOwnerWindow.SizeChanged += OnDetachedOwnerSizeChanged;
        }
    }

    private void OnDetachedOwnerSizeChanged(object sender, SizeChangedEventArgs args)
    {
        UpdateDetachedPanelAllocation();
        RenderAcceptedState();
    }

    private void UpdateDetachedPanelAllocation()
    {
        if (SurfaceKind != TabControllerSurfaceKind.Detached)
        {
            return;
        }

        AttachDetachedOwnerWindow();
        // Resources is hosted in its own owner-native monitor. The detached
        // controller remains tab-focused and always consumes its own client
        // surface; changing monitor visibility must never reflow its tabs. Do
        // not copy the Window's outer height into this content control: the
        // non-client chrome would push the reserved New Tab row below the
        // native tool's minimum client area.
        MaxHeight = double.PositiveInfinity;
        MinHeight = 0;
        Height = double.NaN;
        VerticalAlignment = VerticalAlignment.Stretch;
    }

    private async void RequestDetachOrDock()
    {
        TabControllerAction action = SurfaceKind == TabControllerSurfaceKind.Detached
            ? new DockTabControllerAction(Guid.NewGuid())
            : new DetachTabControllerAction(Guid.NewGuid());
        await ExecuteAsync(action);
    }

    private async ValueTask ExecuteAsync(TabControllerAction action)
    {
        if (session is not null)
        {
            await session.ExecuteAsync(action);
        }
    }

    private bool CanCloseTabs() =>
        LogicalTabCount() > 1;

    private int LogicalTabCount()
    {
        if (state is null)
        {
            return 0;
        }

        var entries = state.Projection.Tabs.Entries;
        return entries.OfType<TabGroupHeaderEntry>().Sum(group => group.TabCount) +
               entries.OfType<BrowserTabEntry>().Count(tab => tab.GroupId is null);
    }

    private async void RequestCloseTab(BrowserTabId tabId)
    {
        if (!CanCloseTabs())
        {
            announcer.Text = "The only tab stays open. Create another tab before closing it.";
            return;
        }

        pendingCloseFocusTabId = tabId;
        await ExecuteAsync(new CloseTabControllerAction(Guid.NewGuid(), tabId));
    }

    private void OnPresentationChanged(object? sender, TabControllerPresentationChangedEventArgs args)
    {
        var acceptedState = args.State;
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }
        if (Dispatcher.CheckAccess())
        {
            AcceptAndRenderIfCurrent(sender, acceptedState);
        }
        else
        {
            _ = Dispatcher.InvokeAsync(() => AcceptAndRenderIfCurrent(sender, acceptedState));
        }
    }

    private void AcceptAndRenderIfCurrent(object? sender, TabControllerPresentationState acceptedState)
    {
        if (!ReferenceEquals(sender, session))
        {
            return;
        }

        var previousSelectedTabId = state?.Projection.Tabs.SelectedTabId;
        var previousPlacement = state?.Projection.Placement;
        var previousPreviewMode = state?.Projection.PreviewRevealMode;
        var nextSelectedTabId = acceptedState.Projection.Tabs.SelectedTabId;
        if (previousPreviewMode != acceptedState.Projection.PreviewRevealMode ||
            acceptedState.Projection.PreviewRevealMode != WorkspacePreviewRevealMode.Hover)
        {
            hoverPreviewGroupId = null;
        }
        if (previousSelectedTabId != nextSelectedTabId)
        {
            // An accepted selection change always becomes the viewport anchor.
            // Keeping a stale keyboard-focus anchor can otherwise push the
            // authoritative selection into overflow at the narrow floor.
            focusedTabId = nextSelectedTabId;
            pageStart = 0;
            viewportNavigationActive = false;
        }
        if (layoutPlacementOverride == acceptedState.Projection.Placement)
        {
            layoutPlacementOverride = null;
        }
        else if (layoutPlacementOverride is null && previousPlacement != acceptedState.Projection.Placement)
        {
            pageStart = 0;
            viewportNavigationActive = false;
            focusedTabId = nextSelectedTabId;
        }

        var restoreAfterClose = pendingCloseFocusTabId is { } closedTabId &&
            !acceptedState.Projection.Tabs.Entries.OfType<BrowserTabEntry>()
                .Any(tab => tab.TabId == closedTabId);
        state = acceptedState;
        if (restoreAfterClose)
        {
            pendingCloseFocusTabId = null;
            focusedTabId = acceptedState.Projection.Tabs.SelectedTabId;
            pageStart = 0;
            viewportNavigationActive = false;
        }
        RenderAcceptedState();
        if (restoreAfterClose)
        {
            _ = Dispatcher.InvokeAsync(
                RestoreAuthoritativeFocusAfterClose,
                System.Windows.Threading.DispatcherPriority.ContextIdle);
        }
    }

    private void RestoreAuthoritativeFocusAfterClose()
    {
        if (session is null || !IsLoaded || !IsVisible)
        {
            return;
        }

        UpdateLayout();
        if (FocusSelectedTab())
        {
            announcer.Text = "Tab closed. Focus moved to the selected tab.";
            return;
        }

        var firstFocusableTab = Descendants(entries).OfType<Button>()
            .FirstOrDefault(button => button.Tag is BrowserTabEntry &&
                                      button.IsEnabled && button.Focusable && button.IsVisible);
        if (firstFocusableTab is not null && Keyboard.Focus(firstFocusableTab) is not null)
        {
            announcer.Text = "Tab closed. Focus moved to the next available tab.";
            return;
        }

        var fallback = more.IsVisible && more.IsEnabled ? more : newTab;
        Keyboard.Focus(fallback);
        announcer.Text = fallback == more
            ? "Tab closed. Focus moved to More tabs."
            : "Tab closed. Focus moved to New tab.";
    }

    private Button? FindSelectedTabButton() =>
        Descendants(entries).OfType<Button>()
            .FirstOrDefault(value => value.Tag is BrowserTabEntry { IsSelected: true });

    private void OnPreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key == Key.Escape && hoverPreviewGroupId is { } groupId)
        {
            hoverPreviewGroupId = null;
            RenderAcceptedState();
            _ = Dispatcher.InvokeAsync(() =>
                Descendants(entries).OfType<Button>()
                    .FirstOrDefault(button => button.Tag is TabGroupHeaderEntry group && group.GroupId == groupId)
                    ?.Focus(),
                System.Windows.Threading.DispatcherPriority.Input);
            announcer.Text = "Tab group preview closed.";
            args.Handled = true;
        }
        else if (args.Key == Key.Escape && more.ContextMenu?.IsOpen == true)
        {
            CloseActiveFlyout();
            args.Handled = true;
        }
        else if (TryHandleControllerShortcut(args.Key, Keyboard.Modifiers))
        {
            args.Handled = true;
        }
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs args)
    {
        if (viewport is null || viewport.HiddenCount == 0 || args.Delta == 0)
        {
            return;
        }

        ScrollViewportTo(viewport.StartIndex + (args.Delta < 0 ? 1 : -1));
        args.Handled = true;
    }

    private void OnOverflowScrollBarValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> args)
    {
        if (!updatingOverflowScrollBar)
        {
            ScrollViewportTo((int)Math.Round(args.NewValue));
        }
    }

    private void ScrollViewportTo(int requestedStart)
    {
        if (state is null || viewport is null)
        {
            return;
        }

        var maximum = Math.Max(0, state.Projection.Tabs.Entries.Count - viewport.Count);
        var next = Math.Clamp(requestedStart, 0, maximum);
        if (viewportNavigationActive && pageStart == next)
        {
            return;
        }

        var direction = Math.Sign(next - pageStart);
        pageStart = next;
        focusedTabId = null;
        viewportNavigationActive = true;
        RenderAcceptedState(animateViewport: true, direction);
        announcer.Text = $"Showing tabs starting at position {pageStart + 1}.";
    }

    private void RenderAcceptedState(bool animateViewport, int direction)
    {
        RenderAcceptedState();
        if (!animateViewport || direction == 0 || ReducedMotion || SystemParameters.HighContrast || !IsLoaded)
        {
            entries.BeginAnimation(OpacityProperty, null);
            entries.RenderTransform = Transform.Identity;
            return;
        }

        var transform = new TranslateTransform();
        entries.RenderTransform = transform;
        var offset = entries.Orientation == Orientation.Horizontal ? 7d * direction : 5d * direction;
        var duration = new Duration(TimeSpan.FromMilliseconds(110));
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        if (entries.Orientation == Orientation.Horizontal)
        {
            transform.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(offset, 0, duration) { EasingFunction = easing });
        }
        else
        {
            transform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(offset, 0, duration) { EasingFunction = easing });
        }
        entries.BeginAnimation(OpacityProperty,
            new DoubleAnimation(.88, 1, duration) { EasingFunction = easing });
    }

    private Button NewTabButton()
    {
        var glyph = new OrbitIcon
        {
            Kind = OrbitIconKind.Add,
            Width = 21,
            Height = 21,
        };
        glyph.BindStrokeToAncestorForeground();
        var button = new OrbitShapedCommand
        {
            Shape = SurfaceKind == TabControllerSurfaceKind.Detached
                ? OrbitCommandShape.Port
                : OrbitCommandShape.Fin,
            Content = glyph,
            MinWidth = 44,
            MinHeight = 44,
            Margin = new Thickness(2),
            ToolTip = "New tab (Ctrl+T)",
        };
        AutomationProperties.SetName(button, "Open new tab");
        AutomationProperties.SetHelpText(button, "Creates and selects a new tab. Keyboard shortcut: Ctrl+T.");
        AutomationProperties.SetAcceleratorKey(button, "Ctrl+T");
        button.Click += async (_, _) => await ExecuteAsync(new CreateNewTabControllerAction(Guid.NewGuid()));
        return button;
    }

    private Button CommandButton(
        OrbitControllerGlyphKind kind,
        OrbitCommandShape shape,
        string name,
        Action action)
    {
        var glyph = new OrbitControllerGlyph { Kind = kind, Width = 22, Height = 22 };
        glyph.BindStrokeToAncestorForeground();
        var button = new OrbitShapedCommand
        {
            Content = SurfaceKind == TabControllerSurfaceKind.Detached
                ? LabeledCommandContent(glyph, VisibleCommandLabel(name))
                : glyph,
            Shape = shape,
            MinWidth = 44,
            MinHeight = 44,
            Margin = new Thickness(2),
        };
        AutomationProperties.SetName(button, name);
        button.ToolTip = name;
        button.Click += (_, _) => action();
        return button;
    }

    private static FrameworkElement LabeledCommandContent(FrameworkElement glyph, string label)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        content.Children.Add(glyph);
        content.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0),
            TextAlignment = TextAlignment.Center,
        });
        return content;
    }

    private static string VisibleCommandLabel(string automationName) => automationName switch
    {
        "Previous tabs" => "Previous",
        "Next tabs" => "Next",
        "More tabs" => "More",
        "Browser resources" => "Resources",
        "Dock tab controller" => "Dock",
        "Pop out tab controller" => "Pop out",
        _ => automationName,
    };

    private static void SetVisibleCommandLabel(Button button, string label)
    {
        if (button.Content is StackPanel panel && panel.Children.OfType<TextBlock>().LastOrDefault() is { } text)
        {
            text.Text = label;
        }
    }

    private static MenuItem MenuItem(
        string label,
        Action action,
        bool isEnabled = true,
        string? unavailableReason = null)
    {
        var item = new MenuItem { Header = label, IsEnabled = isEnabled };
        AutomationProperties.SetName(item, label);
        if (!isEnabled && !string.IsNullOrWhiteSpace(unavailableReason))
        {
            AutomationProperties.SetHelpText(item, unavailableReason);
        }
        item.Click += (_, _) => action();
        return item;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }
}
#endif
