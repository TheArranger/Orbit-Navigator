#if ORBIT_WPF
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using OrbitNavigator.Presentation.Workspace;

namespace OrbitNavigator.Presentation.Wpf;

/// <summary>
/// Compact New Tab launcher for a local, owner-reviewed catalog. The control
/// never discovers sites, fetches remote icons, or opens a destination itself.
/// </summary>
public sealed class AffiliatedSitesControl : UserControl
{
    public const double PreferredRailWidth = 120;
    public const double PreferredRailItemWidth = 88;
    private static readonly AffiliatedSitesVisibilityPresentation InitialVisibility =
        new(false, 0, false, "Affiliated Sites preferences are unavailable until the profile is ready.");

    private readonly Border surface = OrbitVisualTheme.CreateSurface(14);
    private readonly StackPanel layout = new();
    private readonly TextBlock heading = new();
    private readonly Border ownerBadge = new();
    private readonly TextBlock ownerBadgeText = new();
    private readonly TextBlock privacyCopy = new();
    private readonly WrapPanel sitePanel = new();
    private readonly Border emptyState = OrbitVisualTheme.CreateSurface(10);
    private readonly StackPanel emptyLayout = new();
    private readonly Button visibilityButton = new();
    private AffiliatedSitesCatalogPresentation catalog = AffiliatedSitesCatalogPresentation.Empty;
    private AffiliatedSitesVisibilityPresentation visibility = InitialVisibility;
    private bool systemEventsAttached;
    private bool reducedMotion;
    private bool isRailMode;
    private bool showInlineVisibilityControl = true;

    public AffiliatedSitesControl()
    {
        Focusable = false;
        AutomationProperties.SetName(this, "Affiliated Sites");
        AutomationProperties.SetHelpText(
            this,
            "Owner-approved public sites only. Orbit Navigator does not use browsing history, telemetry, advertising, or tracking to choose these sites.");
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Continue);

        BuildLayout();
        Content = surface;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        Render();
    }

    public event EventHandler<AffiliatedSiteLaunchRequestedEventArgs>? LaunchRequested;

    public event EventHandler<AffiliatedSitesVisibilityChangeRequestedEventArgs>? VisibilityChangeRequested;

    /// <summary>
    /// Affiliated Sites has no animation in either mode. This property lets the
    /// New Tab host propagate its accessibility mode without creating a clock.
    /// </summary>
    public bool ReducedMotion
    {
        get => reducedMotion;
        set => reducedMotion = value;
    }

    public AffiliatedSitesCatalogPresentation Catalog => catalog;

    public AffiliatedSitesVisibilityPresentation VisibilityState => visibility;

    public int VisibleSiteCount => visibility.IsHidden ? 0 : catalog.Sites.Count;

    public int VisiblePreviewCount => visibility.IsHidden ? 0 : catalog.Previews.Count;

    public bool IsRailMode
    {
        get => isRailMode;
        set
        {
            if (isRailMode == value) return;
            isRailMode = value;
            Render();
        }
    }

    public bool ShowInlineVisibilityControl
    {
        get => showInlineVisibilityControl;
        set
        {
            if (showInlineVisibilityControl == value) return;
            showInlineVisibilityControl = value;
            Render();
        }
    }

    public void Apply(
        AffiliatedSitesCatalogPresentation nextCatalog,
        AffiliatedSitesVisibilityPresentation nextVisibility)
    {
        catalog = (nextCatalog ?? throw new ArgumentNullException(nameof(nextCatalog))).Validate();
        visibility = (nextVisibility ?? throw new ArgumentNullException(nameof(nextVisibility))).Validate();
        Render();
    }

    private void BuildLayout()
    {
        surface.Padding = new Thickness(18, 15, 18, 17);
        surface.Background = Brushes.Transparent;

        var headingRow = new Grid();
        headingRow.ColumnDefinitions.Add(new ColumnDefinition());
        headingRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        heading.Text = "Affiliated Sites";
        heading.FontSize = 18;
        heading.FontWeight = FontWeights.SemiBold;
        heading.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        titleRow.Children.Add(heading);
        ownerBadge.Margin = new Thickness(9, 0, 0, 0);
        ownerBadge.Padding = new Thickness(7, 2, 7, 2);
        ownerBadge.CornerRadius = new CornerRadius(9);
        ownerBadge.BorderThickness = new Thickness(1);
        ownerBadgeText.Text = "Owner curated";
        ownerBadgeText.FontSize = 10.5;
        ownerBadgeText.FontWeight = FontWeights.SemiBold;
        ownerBadge.Child = ownerBadgeText;
        titleRow.Children.Add(ownerBadge);
        headingRow.Children.Add(titleRow);

        visibilityButton.MinWidth = 44;
        visibilityButton.MinHeight = 40;
        visibilityButton.Margin = new Thickness(12, 0, 0, 0);
        visibilityButton.Click += (_, _) => VisibilityChangeRequested?.Invoke(
            this,
            new AffiliatedSitesVisibilityChangeRequestedEventArgs(!visibility.IsHidden, visibility.Revision));
        Grid.SetColumn(visibilityButton, 1);
        headingRow.Children.Add(visibilityButton);
        layout.Children.Add(headingRow);

        privacyCopy.Text = "A reviewed list — never browser-history suggestions, ads, or tracking.";
        privacyCopy.FontSize = 12;
        privacyCopy.TextWrapping = TextWrapping.Wrap;
        privacyCopy.Margin = new Thickness(0, 4, 0, 12);
        layout.Children.Add(privacyCopy);

        sitePanel.Orientation = Orientation.Horizontal;
        sitePanel.HorizontalAlignment = HorizontalAlignment.Left;
        KeyboardNavigation.SetTabNavigation(sitePanel, KeyboardNavigationMode.Continue);
        layout.Children.Add(sitePanel);

        emptyState.Padding = new Thickness(14, 11, 14, 11);
        emptyState.Background = Brushes.Transparent;
        emptyLayout.Children.Add(new TextBlock
        {
            Text = "No affiliated sites are approved yet.",
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        emptyLayout.Children.Add(new TextBlock
        {
            Text = $"Owners can review {AffiliatedSitesCatalogPresentation.OwnerCatalogRelativePath}. Nothing is inferred or downloaded.",
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0),
        });
        emptyState.Child = emptyLayout;
        AutomationProperties.SetName(emptyState, "Affiliated Sites empty");
        AutomationProperties.SetHelpText(
            emptyState,
            $"No sites are listed. Owner catalog path: {AffiliatedSitesCatalogPresentation.OwnerCatalogRelativePath}.");
        layout.Children.Add(emptyState);
        surface.Child = layout;
    }

    private void Render()
    {
        sitePanel.Children.Clear();
        var highContrast = SystemParameters.HighContrast;
        surface.Padding = isRailMode ? new Thickness(8, 12, 8, 12) : new Thickness(18, 15, 18, 17);
        heading.Text = isRailMode ? "Sites" : "Affiliated Sites";
        heading.FontSize = isRailMode ? 14 : 18;
        ownerBadge.Visibility = isRailMode ? Visibility.Collapsed : Visibility.Visible;
        privacyCopy.Text = isRailMode
            ? "Approved only"
            : "A reviewed list — never browser-history suggestions, ads, or tracking.";
        privacyCopy.TextAlignment = isRailMode ? TextAlignment.Center : TextAlignment.Left;
        privacyCopy.FontSize = isRailMode ? 10.5 : 12;
        sitePanel.Width = isRailMode ? 92 : double.NaN;
        heading.Foreground = highContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.Ink;
        ownerBadge.BorderBrush = highContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.SeaGlassStrong;
        ownerBadgeText.Foreground = highContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.MutedInk;
        privacyCopy.Foreground = highContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.MutedInk;
        surface.BorderBrush = highContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.Divider;
        surface.BorderThickness = new Thickness(highContrast ? 2 : 1);

        foreach (var child in emptyLayout.Children.OfType<TextBlock>())
        {
            child.Foreground = highContrast
                ? SystemColors.WindowTextBrush
                : child.FontWeight == FontWeights.SemiBold
                    ? OrbitVisualTheme.Ink
                    : OrbitVisualTheme.MutedInk;
        }
        emptyState.BorderBrush = highContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.Divider;
        emptyState.BorderThickness = new Thickness(highContrast ? 2 : 1);

        var isHidden = visibility.IsHidden;
        privacyCopy.Visibility = isHidden ? Visibility.Collapsed : Visibility.Visible;
        sitePanel.Visibility = isHidden ? Visibility.Collapsed : Visibility.Visible;
        emptyState.Visibility = !isHidden && catalog.Sites.Count == 0 && catalog.Previews.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!isHidden)
        {
            foreach (var site in catalog.Sites)
            {
                sitePanel.Children.Add(CreateSiteButton(site));
            }
            foreach (var preview in catalog.Previews)
            {
                sitePanel.Children.Add(CreatePreviewCard(preview));
            }
        }

        visibilityButton.Visibility = showInlineVisibilityControl ? Visibility.Visible : Visibility.Collapsed;
        visibilityButton.Content = isHidden ? "Show" : "Hide";
        visibilityButton.IsEnabled = visibility.CanChange;
        visibilityButton.ToolTip = visibility.CanChange
            ? isHidden ? "Show Affiliated Sites" : "Hide Affiliated Sites"
            : visibility.UnavailableReason;
        AutomationProperties.SetName(
            visibilityButton,
            isHidden ? "Show Affiliated Sites" : "Hide Affiliated Sites");
        AutomationProperties.SetHelpText(
            visibilityButton,
            visibility.CanChange
                ? isHidden
                    ? "Shows the local owner-curated site list on New Tab."
                    : "Hides this local section. This does not change the catalog or send data."
                : visibility.UnavailableReason ?? string.Empty);
        AutomationProperties.SetItemStatus(
            this,
            isHidden
                ? "Hidden"
                : $"Visible, {catalog.Sites.Count} sites, {catalog.Previews.Count} non-interactive previews");
        OrbitVisualTheme.ApplyButton(visibilityButton, OrbitButtonRole.Quiet);
    }

    private Button CreateSiteButton(AffiliatedSitePresentation site)
    {
        var icon = new AffiliatedSiteIcon
        {
            Kind = site.IconKind,
            Width = isRailMode ? 30 : 34,
            Height = isRailMode ? 30 : 34,
            Margin = isRailMode ? new Thickness(0, 0, 0, 4) : new Thickness(0, 1, 12, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };

        if (isRailMode)
        {
            var compact = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            compact.Children.Add(icon);
            compact.Children.Add(new TextBlock
            {
                Text = site.Title,
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = SystemParameters.HighContrast ? SystemColors.ControlTextBrush : OrbitVisualTheme.Ink,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 80,
            });
            var compactButton = new Button
            {
                Content = compact,
                Width = PreferredRailItemWidth,
                Height = 84,
                MinWidth = 44,
                MinHeight = 44,
                Margin = new Thickness(2, 0, 2, 8),
                ToolTip = $"{site.Title}\n{site.Purpose}\nTrusted domain: {site.TrustedDomain}\nExternal HTTPS site",
                Tag = site.SiteId,
            };
            ConfigureSiteButton(compactButton, site);
            return compactButton;
        }

        var copy = new StackPanel { MaxWidth = 246 };
        copy.Children.Add(new TextBlock
        {
            Text = site.Title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = SystemParameters.HighContrast ? SystemColors.ControlTextBrush : OrbitVisualTheme.Ink,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        copy.Children.Add(new TextBlock
        {
            Text = site.Purpose,
            FontSize = 11.5,
            Foreground = SystemParameters.HighContrast ? SystemColors.ControlTextBrush : OrbitVisualTheme.MutedInk,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 38,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 3, 0, 5),
        });
        copy.Children.Add(new TextBlock
        {
            Text = $"{site.TrustedDomain}  ↗ External site",
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = SystemParameters.HighContrast ? SystemColors.ControlTextBrush : OrbitVisualTheme.SeaGlass,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon);
        row.Children.Add(copy);
        var button = new Button
        {
            Content = row,
            MinWidth = 250,
            MaxWidth = 320,
            MinHeight = 100,
            Margin = new Thickness(0, 0, 10, 10),
            Padding = new Thickness(14, 12, 14, 12),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Top,
            ToolTip = $"{site.Title}\n{site.Purpose}\nTrusted domain: {site.TrustedDomain}\nExternal HTTPS site",
            Tag = site.SiteId,
        };
        ConfigureSiteButton(button, site);
        return button;
    }

    private void ConfigureSiteButton(Button button, AffiliatedSitePresentation site)
    {
        OrbitVisualTheme.ApplyButton(button, OrbitButtonRole.Tab);
        AutomationProperties.SetName(button, $"Open {site.Title} — external site");
        AutomationProperties.SetItemStatus(button, $"Trusted domain: {site.TrustedDomain}; category: {CategoryLabel(site.IconKind)}");
        AutomationProperties.SetHelpText(
            button,
            $"{site.Purpose} Opens an external HTTPS site in a new tab. Trusted domain: {site.TrustedDomain}. Orbit Navigator adds no tracking.");
        button.Click += (_, _) => LaunchRequested?.Invoke(
            this,
            new AffiliatedSiteLaunchRequestedEventArgs(site, catalog.CatalogId, catalog.Revision));
    }

    private Border CreatePreviewCard(AffiliatedSitePreviewPresentation preview)
    {
        var icon = new AffiliatedSiteIcon
        {
            Kind = preview.IconKind,
            Width = isRailMode ? 30 : 34,
            Height = isRailMode ? 30 : 34,
            Opacity = SystemParameters.HighContrast ? 1 : 0.68,
            Margin = new Thickness(0, 1, 12, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        var copy = new StackPanel { MaxWidth = 246 };
        copy.Children.Add(new TextBlock
        {
            Text = preview.Title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.Ink,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        copy.Children.Add(new TextBlock
        {
            Text = preview.StatusCopy,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.MutedInk,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });
        copy.Children.Add(new TextBlock
        {
            Text = "Static preview · No destination",
            FontSize = 10.5,
            Foreground = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.MutedInk,
            Margin = new Thickness(0, 6, 0, 0),
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon);
        row.Children.Add(copy);
        var card = OrbitVisualTheme.CreateSurface(9);
        if (isRailMode)
        {
            var compact = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            row.Children.Remove(icon);
            icon.Margin = new Thickness(0, 0, 0, 4);
            compact.Children.Add(icon);
            var compactTitle = new TextBlock
            {
                Text = preview.Title,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.Ink,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 78,
            };
            var compactStatus = new TextBlock
            {
                Text = preview.StatusCopy,
                FontSize = 9,
                Foreground = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.MutedInk,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 78,
                Margin = new Thickness(0, 3, 0, 0),
            };
            AutomationProperties.SetName(compactTitle, preview.Title);
            AutomationProperties.SetName(compactStatus, preview.StatusCopy);
            AutomationProperties.SetName(compact, $"{preview.Title} — {preview.StatusCopy}");
            AutomationProperties.SetItemStatus(compact, preview.StatusCopy);
            compact.Children.Add(compactTitle);
            compact.Children.Add(compactStatus);
            card.Width = PreferredRailItemWidth;
            card.MinHeight = 120;
            card.MinWidth = 44;
            card.Margin = new Thickness(2, 0, 2, 8);
            card.Padding = new Thickness(6, 7, 6, 7);
            card.Child = compact;
        }
        else
        {
            card.MinWidth = 250;
            card.MaxWidth = 320;
            card.MinHeight = 100;
            card.Margin = new Thickness(0, 0, 10, 10);
            card.Padding = new Thickness(14, 12, 14, 12);
            card.Child = row;
        }
        card.Background = SystemParameters.HighContrast ? SystemColors.WindowBrush : PreviewVeilBrush();
        card.BorderBrush = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.Divider;
        card.BorderThickness = new Thickness(SystemParameters.HighContrast ? 2 : 1);
        card.Focusable = false;
        card.IsHitTestVisible = false;
        card.Tag = preview.PreviewId;
        AutomationProperties.SetName(card, $"{preview.Title} — {preview.StatusCopy}");
        AutomationProperties.SetItemStatus(card, preview.StatusCopy);
        AutomationProperties.SetHelpText(
            card,
            "Static, visually obscured preview only. It has no destination, action, readiness claim, or personal details.");
        return card;
    }

    private static Brush PreviewVeilBrush()
    {
        var line = new GeometryDrawing(
            null,
            new Pen(new SolidColorBrush(Color.FromArgb(40, 165, 182, 184)), 1),
            Geometry.Parse("M0,10 L10,0"));
        var brush = new DrawingBrush(line)
        {
            TileMode = TileMode.Tile,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 10, 10),
            Stretch = Stretch.None,
        };
        if (brush.CanFreeze)
        {
            brush.Freeze();
        }
        return brush;
    }

    private static string CategoryLabel(AffiliatedSiteIconKind kind) => kind switch
    {
        AffiliatedSiteIconKind.Community => "Community",
        AffiliatedSiteIconKind.Tools => "Tools",
        AffiliatedSiteIconKind.Media => "Media",
        AffiliatedSiteIconKind.Learning => "Learning",
        _ => "Public site",
    };

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (systemEventsAttached)
        {
            return;
        }

        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        systemEventsAttached = true;
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (!systemEventsAttached)
        {
            return;
        }

        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        systemEventsAttached = false;
    }

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SystemParameters.HighContrast))
        {
            Render();
        }
    }
}

/// <summary>Transparent code-native category glyph for an approved affiliated site.</summary>
public sealed class AffiliatedSiteIcon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind),
        typeof(AffiliatedSiteIconKind),
        typeof(AffiliatedSiteIcon),
        new FrameworkPropertyMetadata(AffiliatedSiteIconKind.PublicSite, FrameworkPropertyMetadataOptions.AffectsRender));

    public AffiliatedSiteIcon()
    {
        Focusable = false;
        IsHitTestVisible = false;
    }

    public AffiliatedSiteIconKind Kind
    {
        get => (AffiliatedSiteIconKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? 34 : Math.Min(34, availableSize.Width),
        double.IsInfinity(availableSize.Height) ? 34 : Math.Min(34, availableSize.Height));

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var scale = Math.Min(ActualWidth, ActualHeight) / 24d;
        var offsetX = (ActualWidth - (24 * scale)) / 2d;
        var offsetY = (ActualHeight - (24 * scale)) / 2d;
        var stroke = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.Ink;
        var accent = SystemParameters.HighContrast ? SystemColors.HighlightBrush : OrbitVisualTheme.SeaGlass;
        var primaryPen = Pen(stroke, 1.7 / scale);
        var accentPen = Pen(accent, 1.9 / scale);

        drawingContext.PushTransform(new TranslateTransform(offsetX, offsetY));
        drawingContext.PushTransform(new ScaleTransform(scale, scale));
        switch (Kind)
        {
            case AffiliatedSiteIconKind.Community:
                drawingContext.DrawEllipse(null, primaryPen, new Point(9, 8), 3, 3);
                drawingContext.DrawEllipse(null, primaryPen, new Point(16.5, 9), 2.5, 2.5);
                drawingContext.DrawGeometry(null, accentPen, Geometry.Parse("M3.5,19 C4.5,14 7,12.5 10,12.5 C13,12.5 15,14 16,19 M14,13 C18,12.5 20,15 20.5,18"));
                break;
            case AffiliatedSiteIconKind.Tools:
                drawingContext.DrawGeometry(null, primaryPen, Geometry.Parse("M14,4 C12,4 10,5 10,7 C10,8 10.4,9 11,9.7 L5,15.7 C4,16.7 4,18.3 5,19.3 C6,20.3 7.7,20.3 8.7,19.3 L14.7,13.3 C15.4,13.7 16.2,14 17,14 C19.2,14 21,12.2 21,10 C21,9.3 20.8,8.6 20.5,8 L17,11 L14,10 L13,7 L16,4.5 C15.4,4.2 14.7,4 14,4"));
                drawingContext.DrawEllipse(accent, null, new Point(7, 17.2), 1.15, 1.15);
                break;
            case AffiliatedSiteIconKind.Media:
                drawingContext.DrawRoundedRectangle(null, primaryPen, new Rect(3.5, 5, 17, 14), 2, 2);
                drawingContext.DrawGeometry(accent, null, Geometry.Parse("M10,9 L16,12 L10,15 Z"));
                break;
            case AffiliatedSiteIconKind.Learning:
                drawingContext.DrawGeometry(null, primaryPen, Geometry.Parse("M3.5,6 C7,5 9.5,6 12,8 V19 C9.5,17 7,16.5 3.5,17 Z M20.5,6 C17,5 14.5,6 12,8 V19 C14.5,17 17,16.5 20.5,17 Z"));
                drawingContext.DrawGeometry(null, accentPen, Geometry.Parse("M6,9 H9.5 M14.5,9 H18 M6,12 H9.5 M14.5,12 H18"));
                break;
            default:
                drawingContext.DrawEllipse(null, primaryPen, new Point(12, 12), 8, 8);
                drawingContext.DrawGeometry(null, accentPen, Geometry.Parse("M4,12 H20 M12,4 C9,7 9,17 12,20 M12,4 C15,7 15,17 12,20 M6,8 H18 M6,16 H18"));
                break;
        }
        drawingContext.Pop();
        drawingContext.Pop();
    }

    private static Pen Pen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        if (pen.CanFreeze)
        {
            pen.Freeze();
        }
        return pen;
    }
}

public sealed class AffiliatedSiteLaunchRequestedEventArgs : EventArgs
{
    public AffiliatedSiteLaunchRequestedEventArgs(
        AffiliatedSitePresentation site,
        string catalogId,
        long expectedCatalogRevision)
    {
        Site = (site ?? throw new ArgumentNullException(nameof(site))).Validate();
        CatalogId = string.IsNullOrWhiteSpace(catalogId)
            ? throw new ArgumentException("A catalog ID is required.", nameof(catalogId))
            : catalogId;
        ExpectedCatalogRevision = expectedCatalogRevision >= 0
            ? expectedCatalogRevision
            : throw new ArgumentOutOfRangeException(nameof(expectedCatalogRevision));
    }

    public AffiliatedSitePresentation Site { get; }

    public string CatalogId { get; }

    public long ExpectedCatalogRevision { get; }
}

public sealed class AffiliatedSitesVisibilityChangeRequestedEventArgs : EventArgs
{
    public AffiliatedSitesVisibilityChangeRequestedEventArgs(bool isHidden, long expectedRevision)
    {
        IsHidden = isHidden;
        ExpectedRevision = expectedRevision >= 0
            ? expectedRevision
            : throw new ArgumentOutOfRangeException(nameof(expectedRevision));
    }

    public bool IsHidden { get; }

    public long ExpectedRevision { get; }
}
#endif
