#if ORBIT_WPF
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using OrbitNavigator.Presentation.QuickView;

namespace OrbitNavigator.Presentation.Wpf;

public sealed class QuickViewControl : Grid
{
    public const double InitialWidthRatio = 0.30;
    public const double InitialHeightRatio = 0.30;
    public const double MaximumViewportRatio = 0.75;
    public const double MinimumSurfaceWidth = 280;
    public const double MinimumSurfaceHeight = 220;

    private readonly Border surface;
    private readonly Grid overlayLayout = new();
    private readonly Popup overlayPopup = new()
    {
        AllowsTransparency = false,
        Placement = PlacementMode.Custom,
        StaysOpen = true,
        PopupAnimation = PopupAnimation.None,
    };
    private readonly ContentPresenter webContent = new();
    private readonly TextBox search = new()
    {
        Width = 270,
        Height = 44,
        VerticalContentAlignment = VerticalAlignment.Center,
        Padding = new Thickness(10, 0, 10, 0),
        Visibility = Visibility.Collapsed,
    };
    private readonly Button anchor;
    private readonly Button expand;
    private readonly Button close;
    private readonly TextBlock title = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock submitFeedback = new()
    {
        Width = 1,
        Height = 1,
        Opacity = 0.01,
        Focusable = false,
    };
    private readonly WrapPanel bookmarks = new();
    private readonly Thumb resize = new() { Width = 44, Height = 44, Cursor = Cursors.SizeNESW };
    private QuickViewPresentation presentation =
        QuickViewPresentation.Unavailable(false, "Quick View is not connected.");
    private Size ownerViewport = new(1200, 800);
    private double widthRatio = InitialWidthRatio;
    private double heightRatio = InitialHeightRatio;
    private bool imeSubmitQueued;

    public QuickViewControl()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        Background = null;
        Focusable = false;
        AutomationProperties.SetName(this, "Quick View");

        anchor = CreateIconButton(OrbitIconKind.Search, "Submit Quick View search or address");
        anchor.Content = IconLabel(OrbitIconKind.Search, "Quick View search");
        anchor.MinWidth = 154;
        anchor.MinHeight = 48;
        AutomationProperties.SetHelpText(anchor,
            "Submit the adjacent search or address in Quick View. When the field is empty, open the current page.");
        anchor.Click += (_, _) => RequestOpenOrNavigate();
        anchor.GotKeyboardFocus += (_, _) => ExpandSearch();
        expand = TextButton("Expand to normal tab", RequestExpand);
        close = TextButton("Close Quick View", RequestClose);
        surface = BuildSurface();
        BuildLayout();
        overlayPopup.PlacementTarget = this;
        overlayPopup.CustomPopupPlacementCallback = PlaceOverlayAtLowerLeft;
        Loaded += (_, _) => RefreshOverlayPopup();
        Unloaded += (_, _) => overlayPopup.IsOpen = false;
        SizeChanged += (_, _) => RefreshOverlayPlacement();
        Apply(presentation);
    }

    public event EventHandler<QuickViewActionRequestedEventArgs>? ActionRequested;

    public bool ReducedMotion { get; set; }
    public QuickViewPresentation Presentation => presentation;
    public bool IsAnchorVisible => anchor.Visibility == Visibility.Visible;
    public bool IsSearchExpanded => search.Visibility == Visibility.Visible;
    public bool IsSurfaceVisible => surface.Visibility == Visibility.Visible;
    public bool UsesNativeOverlayPopup => true;
    public Size CurrentSurfaceSize => new(surface.Width, surface.Height);
    public FrameworkElement WebContentHost => webContent;
    public Button AnchorButton => anchor;
    public TextBox SearchBox => search;
    public Popup OverlayPopup => overlayPopup;

    public UIElement? WebContent
    {
        get => webContent.Content as UIElement;
        set => webContent.Content = value;
    }

    public void Apply(QuickViewPresentation state)
    {
        var previousHostState = presentation.HostState;
        var previousAddress = presentation.Address;
        presentation = (state ?? throw new ArgumentNullException(nameof(state))).Validate();
        if (presentation.HostState is QuickViewHostState.Ready or QuickViewHostState.Unavailable &&
            previousHostState is QuickViewHostState.Opening or QuickViewHostState.Open or
                QuickViewHostState.Closing or QuickViewHostState.Failed)
        {
            ResetForFreshUse();
        }
        anchor.Visibility = presentation.CanShowAnchor ? Visibility.Visible : Visibility.Collapsed;
        surface.Visibility = presentation.CanShowAnchor &&
            presentation.HostState is QuickViewHostState.Opening or QuickViewHostState.Open or
                QuickViewHostState.Closing or QuickViewHostState.Failed
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!presentation.CanShowAnchor)
        {
            search.Visibility = Visibility.Collapsed;
        }
        title.Text = presentation.Title;
        status.Text = presentation.SafeStatusMessage;
        webContent.Visibility = presentation.HostState == QuickViewHostState.Open && WebContent is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        status.Visibility = webContent.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
        expand.IsEnabled = presentation.HostState == QuickViewHostState.Open && ActionRequested is not null;
        close.IsEnabled = presentation.HostState is QuickViewHostState.Opening or QuickViewHostState.Open or
            QuickViewHostState.Failed;
        AutomationProperties.SetHelpText(expand,
            presentation.TransferCapability == QuickViewStateTransferCapability.PreserveCurrentPageState
                ? "Move this Quick View into a normal tab while preserving its current page state."
                : "Open the current address in a normal tab. The page will reload because state transfer is unavailable.");
        AutomationProperties.SetItemStatus(this, presentation.HostState.ToString());
        if (presentation.HostState == QuickViewHostState.Open && presentation.Address is { } address &&
            (previousHostState != QuickViewHostState.Open || previousAddress != address))
        {
            SetSubmitFeedback($"Quick View loaded {address.Host}.");
        }
        RenderBookmarks();
        UpdateSurfaceSize();
        RefreshOverlayPopup();
    }

    public void ApplyOwnerViewport(Size viewport)
    {
        if (viewport.Width <= 0 || viewport.Height <= 0)
        {
            return;
        }
        ownerViewport = viewport;
        UpdateSurfaceSize();
    }

    public void ResetForFreshUse()
    {
        search.Clear();
        search.Visibility = Visibility.Collapsed;
        WebContent = null;
        widthRatio = InitialWidthRatio;
        heightRatio = InitialHeightRatio;
        UpdateSurfaceSize();
    }

    private void BuildLayout()
    {
        overlayLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        overlayLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        overlayLayout.Background = SystemParameters.HighContrast
            ? SystemColors.WindowBrush
            : OrbitVisualTheme.Canvas;
        overlayLayout.Children.Add(surface);
        AutomationProperties.SetName(submitFeedback, "Quick View navigation status");
        AutomationProperties.SetLiveSetting(submitFeedback, AutomationLiveSetting.Polite);
        overlayLayout.Children.Add(submitFeedback);

        var launcher = new Border
        {
            Background = SystemParameters.HighContrast
                ? SystemColors.ControlBrush
                : new LinearGradientBrush(
                    Color.FromArgb(250, 18, 37, 43),
                    Color.FromArgb(250, 30, 45, 57),
                    new Point(0, .5),
                    new Point(1, .5)),
            BorderBrush = SystemParameters.HighContrast ? SystemColors.ControlTextBrush : OrbitVisualTheme.WaypointGold,
            BorderThickness = new Thickness(SystemParameters.HighContrast ? 2 : 2),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(5),
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(launcher, "Quick View lower-left launcher");
        AutomationProperties.SetItemStatus(launcher, "Available on this normal web page");
        var launcherRow = new StackPanel { Orientation = Orientation.Horizontal };
        launcherRow.Children.Add(anchor);
        search.PreviewKeyDown += OnSearchPreviewKeyDown;
        search.LostKeyboardFocus += (_, _) => CollapseSearchIfIdle();
        AutomationProperties.SetName(search, "Quick View search or address");
        AutomationProperties.SetHelpText(search, "Type a search or web address, then press Enter to open it in Quick View.");
        launcherRow.Children.Add(search);
        launcher.Child = launcherRow;
        launcher.MouseEnter += (_, _) => ExpandSearch();
        launcher.MouseLeave += (_, _) => CollapseSearchIfIdle();
        Grid.SetRow(launcher, 1);
        overlayLayout.Children.Add(launcher);
        overlayPopup.Child = overlayLayout;
        Children.Add(overlayPopup);
    }

    private CustomPopupPlacement[] PlaceOverlayAtLowerLeft(
        Size popupSize,
        Size targetSize,
        Point offset)
    {
        const double inset = 14;
        return
        [
            new CustomPopupPlacement(
                new Point(inset, Math.Max(inset, targetSize.Height - popupSize.Height - inset)),
                PopupPrimaryAxis.None),
        ];
    }

    private void RefreshOverlayPopup()
    {
        overlayPopup.IsOpen = IsLoaded && presentation.CanShowAnchor;
        if (overlayPopup.IsOpen)
        {
            RefreshOverlayPlacement();
        }
    }

    private void RefreshOverlayPlacement()
    {
        if (!overlayPopup.IsOpen)
        {
            return;
        }

        // WPF has no public Popup.Reposition API. A sub-pixel offset nudge asks
        // the native popup to rerun its custom placement without visible motion.
        var offset = overlayPopup.HorizontalOffset;
        overlayPopup.HorizontalOffset = offset + 0.01;
        overlayPopup.HorizontalOffset = offset;
    }

    private Border BuildSurface()
    {
        var shell = new Border
        {
            Background = SystemParameters.HighContrast ? SystemColors.WindowBrush : OrbitVisualTheme.Canvas,
            BorderBrush = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.SeaGlassStrong,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(14),
            Visibility = Visibility.Collapsed,
            ClipToBounds = true,
        };
        AutomationProperties.SetName(shell, "Quick View mini-browser");
        AutomationProperties.SetHelpText(shell,
            "A temporary mini-browser anchored to the lower-left of the current browser window.");
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Grid
        {
            Background = SystemParameters.HighContrast ? SystemColors.ControlBrush : OrbitVisualTheme.Chrome,
            Margin = new Thickness(0),
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.Foreground = ForegroundBrush();
        title.FontWeight = FontWeights.SemiBold;
        title.Margin = new Thickness(14, 12, 8, 12);
        header.Children.Add(title);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(expand);
        actions.Children.Add(close);
        actions.Children.Add(resize);
        resize.DragDelta += OnResizeDragDelta;
        resize.DragCompleted += OnResizeDragCompleted;
        AutomationProperties.SetName(resize, "Resize Quick View");
        AutomationProperties.SetHelpText(resize,
            "Drag up and right to grow Quick View, or down and left to shrink it.");
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);
        layout.Children.Add(header);

        var bookmarkBar = new Border
        {
            Background = SystemParameters.HighContrast ? SystemColors.ControlBrush : OrbitVisualTheme.Surface,
            BorderBrush = SystemParameters.HighContrast ? SystemColors.ControlTextBrush : OrbitVisualTheme.Divider,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 4, 8, 4),
            Child = bookmarks,
        };
        AutomationProperties.SetName(bookmarkBar, "Quick View bookmarks");
        Grid.SetRow(bookmarkBar, 1);
        layout.Children.Add(bookmarkBar);

        var body = new Grid();
        body.Children.Add(webContent);
        status.Foreground = MutedBrush();
        status.HorizontalAlignment = HorizontalAlignment.Center;
        status.VerticalAlignment = VerticalAlignment.Center;
        status.Margin = new Thickness(20);
        body.Children.Add(status);
        webContent.IsVisibleChanged += (_, _) =>
            status.Visibility = webContent.IsVisible ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRow(body, 2);
        layout.Children.Add(body);
        shell.Child = layout;
        return shell;
    }

    private void RenderBookmarks()
    {
        bookmarks.Children.Clear();
        foreach (var bookmark in presentation.Bookmarks.Take(8))
        {
            var button = TextButton(bookmark.Title, () => Raise(new OpenQuickViewBookmarkAction(
                Guid.NewGuid(), presentation.Revision, bookmark.StableId)));
            AutomationProperties.SetName(button, $"Open {bookmark.Title} in Quick View");
            AutomationProperties.SetHelpText(button, bookmark.Target.Host);
            bookmarks.Children.Add(button);
        }
    }

    private void ExpandSearch() => search.Visibility = Visibility.Visible;

    private void CollapseSearchIfIdle()
    {
        if (!IsMouseOver && !search.IsKeyboardFocusWithin && !anchor.IsKeyboardFocused && !IsSurfaceVisible)
        {
            search.Visibility = Visibility.Collapsed;
        }
    }

    private void OnSearchPreviewKeyDown(object sender, KeyEventArgs args)
    {
        var key = args.Key switch
        {
            Key.System => args.SystemKey,
            Key.ImeProcessed => args.ImeProcessedKey,
            _ => args.Key,
        };
        if (key == Key.Enter)
        {
            args.Handled = true;
            if (args.Key == Key.ImeProcessed)
            {
                // Let the IME commit its final text before reading the field. A
                // single queued submission prevents the composition key from
                // producing duplicate navigation.
                if (!imeSubmitQueued)
                {
                    imeSubmitQueued = true;
                    _ = Dispatcher.InvokeAsync(() =>
                    {
                        imeSubmitQueued = false;
                        RequestOpenOrNavigate();
                    }, DispatcherPriority.Input);
                }
            }
            else
            {
                RequestOpenOrNavigate();
            }
        }
        else if (key == Key.Escape && !IsSurfaceVisible)
        {
            search.Clear();
            search.Visibility = Visibility.Collapsed;
            anchor.Focus();
            args.Handled = true;
        }
    }

    private void RequestOpenOrNavigate()
    {
        var query = search.Text.Trim();
        var navigating = presentation.HostState == QuickViewHostState.Open;
        SetSubmitFeedback(navigating ? "Navigating Quick View." : "Opening Quick View.");
        Raise(navigating
            ? new NavigateQuickViewAction(Guid.NewGuid(), presentation.Revision, query)
            : new OpenQuickViewAction(Guid.NewGuid(), presentation.Revision, query));
        if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
        {
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (search.IsVisible && search.IsEnabled)
                {
                    search.Focus();
                    search.CaretIndex = search.Text.Length;
                }
            }, DispatcherPriority.Input);
        }
    }

    private void SetSubmitFeedback(string message)
    {
        submitFeedback.Text = message;
        AutomationProperties.SetItemStatus(search, message);
    }

    private void RequestExpand() => Raise(new ExpandQuickViewToTabAction(
        Guid.NewGuid(),
        presentation.Revision,
        presentation.TransferCapability == QuickViewStateTransferCapability.PreserveCurrentPageState));

    private void RequestClose() => Raise(new CloseQuickViewAction(Guid.NewGuid(), presentation.Revision));

    private void OnResizeDragDelta(object sender, DragDeltaEventArgs args)
    {
        var maximumWidth = Math.Max(MinimumSurfaceWidth, ownerViewport.Width * MaximumViewportRatio);
        var maximumHeight = Math.Max(MinimumSurfaceHeight, ownerViewport.Height * MaximumViewportRatio);
        surface.Width = Math.Clamp(surface.ActualWidth + args.HorizontalChange, MinimumSurfaceWidth, maximumWidth);
        surface.Height = Math.Clamp(surface.ActualHeight - args.VerticalChange, MinimumSurfaceHeight, maximumHeight);
        widthRatio = surface.Width / ownerViewport.Width;
        heightRatio = surface.Height / ownerViewport.Height;
    }

    private void OnResizeDragCompleted(object sender, DragCompletedEventArgs args) => Raise(
        new ResizeQuickViewAction(
            Guid.NewGuid(),
            presentation.Revision,
            Math.Clamp(widthRatio, 0, MaximumViewportRatio),
            Math.Clamp(heightRatio, 0, MaximumViewportRatio)));

    private void UpdateSurfaceSize()
    {
        surface.Width = Math.Clamp(
            ownerViewport.Width * widthRatio,
            Math.Min(MinimumSurfaceWidth, ownerViewport.Width),
            Math.Max(Math.Min(MinimumSurfaceWidth, ownerViewport.Width), ownerViewport.Width * MaximumViewportRatio));
        surface.Height = Math.Clamp(
            ownerViewport.Height * heightRatio,
            Math.Min(MinimumSurfaceHeight, ownerViewport.Height),
            Math.Max(Math.Min(MinimumSurfaceHeight, ownerViewport.Height), ownerViewport.Height * MaximumViewportRatio));
    }

    private Button CreateIconButton(OrbitIconKind iconKind, string name)
    {
        var icon = new OrbitIcon { Kind = iconKind, Width = 20, Height = 20 };
        icon.BindStrokeToAncestorForeground();
        var button = new Button
        {
            Content = icon,
            MinWidth = 44,
            MinHeight = 44,
            Padding = new Thickness(8),
        };
        OrbitVisualTheme.ApplyButton(button, OrbitButtonRole.Toolbar);
        AutomationProperties.SetName(button, name);
        button.ToolTip = name;
        return button;
    }

    private static FrameworkElement IconLabel(OrbitIconKind kind, string label)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new OrbitIcon
        {
            Kind = kind,
            Width = 18,
            Height = 18,
            Margin = new Thickness(0, 0, 7, 0),
        };
        icon.BindStrokeToAncestorForeground();
        row.Children.Add(icon);
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return row;
    }

    private Button TextButton(string label, Action action)
    {
        var button = new Button
        {
            Content = label,
            MinHeight = 44,
            MinWidth = 44,
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(2),
        };
        OrbitVisualTheme.ApplyButton(button, OrbitButtonRole.Toolbar);
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => action();
        return button;
    }

    private void Raise(QuickViewAction action) =>
        ActionRequested?.Invoke(this, new(action));

    private static Brush ForegroundBrush() =>
        SystemParameters.HighContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.Ink;

    private static Brush MutedBrush() =>
        SystemParameters.HighContrast ? SystemColors.GrayTextBrush : OrbitVisualTheme.MutedInk;
}
#endif
