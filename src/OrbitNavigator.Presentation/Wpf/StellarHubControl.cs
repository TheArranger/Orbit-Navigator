#if ORBIT_WPF
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.IO;

namespace OrbitNavigator.Presentation.Wpf;

public enum StellarHubKind
{
    Bookmarks = 0,
    Workspaces = 1,
}

public sealed record StellarHubItem(string Key, string Title, string Subtitle)
{
    public Uri? Address { get; init; }
    public string? Note { get; init; }
    public ReadOnlyMemory<byte> FaviconPng { get; init; }
    public ImageSource? OrbitingImageSource { get; init; }
    public string ColorToken { get; init; } = "SeaGlass";
}

public sealed class StellarHubItemActivatedEventArgs : EventArgs
{
    public StellarHubItemActivatedEventArgs(string key) => Key = key;
    public string Key { get; }
}

/// <summary>
/// Alternate visual presentation. Item buttons, labels, and orbit rings stay
/// stationary; only the non-interactive center star uses the shared motion clock.
/// </summary>
public sealed class StellarHubControl : Grid
{
    public const int MaximumVisibleItems = 4;
    public const string BookmarkAssetFileName = "bookmark-star-v1.png";
    public const string WorkspaceAssetFileName = "workspace-star-v1.png";

    private readonly Canvas itemCanvas = new() { Width = 430, Height = 280 };
    private readonly OrbitStellarOrbitVisual stellarOrbit;
    private readonly OrbitEmberStar centerStar;
    private readonly Border emptyInvitation = new();
    private readonly StellarHubDetailCard detailCard = new()
    {
        Visibility = Visibility.Collapsed,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Bottom,
        Margin = new Thickness(18, 0, 18, 8),
        Padding = new Thickness(14, 10, 14, 10),
        CornerRadius = new CornerRadius(12),
        IsHitTestVisible = false,
        Focusable = false,
    };
    private bool reducedMotion;
    private bool reducedVisualNoise;
    private string? legacyCenterAssetPath;
    private string? detailItemKey;

    public StellarHubControl()
        : this(StellarHubKind.Bookmarks)
    {
    }

    public StellarHubControl(StellarHubKind kind)
    {
        Kind = kind;
        stellarOrbit = new OrbitStellarOrbitVisual
        {
            Kind = kind == StellarHubKind.Bookmarks
                ? OrbitEmberStarKind.Favorite
                : OrbitEmberStarKind.TabGroup,
            Width = 172,
            Height = 172,
            IsActive = true,
            IsHitTestVisible = false,
        };
        centerStar = stellarOrbit.CentralStar;
        Height = 280;
        ClipToBounds = true;
        Background = Brushes.Transparent;
        AutomationProperties.SetName(this, "Stellar quick-launch hub");
        var view = new Viewbox { Stretch = Stretch.Uniform, Child = BuildCanvas() };
        Children.Add(view);
        Children.Add(detailCard);
        RefreshAccessibility();
    }

    public event EventHandler<StellarHubItemActivatedEventArgs>? ItemActivated;

    public event EventHandler<bool>? DetailVisibilityChanged;

    public StellarHubKind Kind { get; }

    public bool ReducedMotion
    {
        get => reducedMotion;
        set { reducedMotion = value; RefreshMotionPreferences(); }
    }

    public bool ReducedVisualNoise
    {
        get => reducedVisualNoise;
        set { reducedVisualNoise = value; RefreshAccessibility(); }
    }

    public bool HasCenterAsset => centerStar.HasArtworkAtlas;

    public BitmapSource? CenterArtworkSource => centerStar.ArtworkAtlasSource;

    public bool CenterArtworkUsesExtractedAlpha => false;

    public bool IsCenterFallbackVisible => !centerStar.HasArtworkAtlas;

    public bool UsesV3CenterArtwork => centerStar.UsesV3Artwork;

    public OrbitEmberStar CenterStar => centerStar;

    public OrbitStellarOrbitVisual StellarOrbit => stellarOrbit;

    public bool FreezeNonStellarMotion
    {
        get => stellarOrbit.FreezeNonStellarMotion;
        set => stellarOrbit.FreezeNonStellarMotion = value;
    }

    public string? LegacyCenterAssetPath => legacyCenterAssetPath;

    public bool IsDetailCardVisible => detailCard.Visibility == Visibility.Visible;

    public void SetCenterAsset(string? absolutePath)
    {
        // The App may still supply the legacy v1 single-frame path. Preserve it
        // for diagnostics, but the center intentionally resolves the packaged
        // v3 full-frame atlas and never re-enters the black-matte compositor.
        legacyCenterAssetPath = absolutePath;
        RefreshMotionPreferences();
    }

    public void SetItems(IReadOnlyList<StellarHubItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        itemCanvas.Children.Clear();
        if (detailCard.Visibility == Visibility.Visible)
        {
            detailCard.Visibility = Visibility.Collapsed;
            detailItemKey = null;
            DetailVisibilityChanged?.Invoke(this, false);
        }
        var visible = items.Take(MaximumVisibleItems).ToArray();
        if (visible.FirstOrDefault() is { } lead)
        {
            stellarOrbit.OrbitingImageSource = lead.OrbitingImageSource ??
                (TryDecodePng(lead.FaviconPng, out var decodedLead) ? decodedLead : null);
            stellarOrbit.OrbitingImageLabel = lead.Title;
        }
        else
        {
            stellarOrbit.OrbitingImageSource = null;
            stellarOrbit.OrbitingImageLabel = string.Empty;
        }
        if (visible.Length == 0)
        {
            ConfigureEmptyInvitation();
            itemCanvas.Children.Add(emptyInvitation);
        }
        for (var index = 0; index < visible.Length; index++)
        {
            var item = visible[index];
            var content = CreateOrbitalItemVisual(item);
            var button = new Button
            {
                Content = content,
                Width = 112,
                Height = 72,
                MinWidth = 44,
                MinHeight = 44,
                Padding = new Thickness(8, 5, 8, 5),
                Tag = item.Key,
                ToolTip = $"{item.Title} — {item.Subtitle}",
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            OrbitVisualTheme.ApplyButton(button, OrbitButtonRole.Tab);
            AutomationProperties.SetName(button, $"Open {item.Title} from {Kind.ToString().ToLowerInvariant()} hub");
            AutomationProperties.SetHelpText(button, BuildDetailText(item));
            button.Click += (_, _) => ItemActivated?.Invoke(this, new((string)button.Tag));
            button.MouseEnter += (_, _) => ShowDetail(item);
            button.MouseMove += (_, _) => ShowDetail(item);
            button.GotKeyboardFocus += (_, _) => ShowDetail(item);
            button.MouseLeave += (_, _) => HideDetailUnlessFocused(button);
            button.LostKeyboardFocus += (_, _) => HideDetailUnlessFocused(button);
            itemCanvas.Children.Add(button);
            var angle = ((Math.PI * 2) / Math.Max(visible.Length, 1) * index) - (Math.PI / 2);
            const double radiusX = 146;
            var radiusY = Kind == StellarHubKind.Bookmarks ? 96 : 98;
            Canvas.SetLeft(button, 215 + Math.Cos(angle) * radiusX - button.Width / 2);
            Canvas.SetTop(button, 140 + Math.Sin(angle) * radiusY - button.Height / 2);
        }
        RefreshMotionPreferences();
    }

    private FrameworkElement CreateOrbitalItemVisual(StellarHubItem item)
    {
        FrameworkElement symbol;
        if (!SystemParameters.HighContrast && TryDecodePng(item.FaviconPng, out var favicon))
        {
            symbol = new Image
            {
                Source = favicon,
                Width = 30,
                Height = 30,
                Stretch = Stretch.Uniform,
                IsHitTestVisible = false,
            };
        }
        else
        {
            symbol = new OrbitEmberStar(
                Kind == StellarHubKind.Bookmarks ? OrbitEmberStarKind.Favorite : OrbitEmberStarKind.TabGroup)
            {
                IsActive = true,
                ReducedMotion = ReducedMotion,
                MotionEnabled = !ReducedVisualNoise,
                Width = 34,
                Height = 34,
                IsHitTestVisible = false,
            };
        }

        var label = new TextBlock
        {
            Text = item.Title,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = SystemParameters.HighContrast ? SystemColors.ControlTextBrush : OrbitVisualTheme.Ink,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 94,
            Margin = new Thickness(0, 3, 0, 0),
            IsHitTestVisible = false,
        };
        var content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        content.Children.Add(symbol);
        content.Children.Add(label);
        return content;
    }

    private void ShowDetail(StellarHubItem item)
    {
        if (detailCard.Visibility == Visibility.Visible && detailItemKey == item.Key)
        {
            return;
        }

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = item.Title,
            FontWeight = FontWeights.SemiBold,
            Foreground = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.Ink,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        panel.Children.Add(new TextBlock
        {
            Text = item.Address?.AbsoluteUri ?? item.Subtitle,
            FontSize = 11,
            Foreground = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.MutedInk,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (!string.IsNullOrWhiteSpace(item.Note))
        {
            panel.Children.Add(new TextBlock
            {
                Text = item.Note,
                FontSize = 11.5,
                Foreground = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.MutedInk,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 310,
                Margin = new Thickness(0, 4, 0, 0),
            });
        }
        detailCard.Child = panel;
        detailCard.Background = SystemParameters.HighContrast ? SystemColors.WindowBrush : OrbitVisualTheme.Chrome;
        detailCard.BorderBrush = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.Divider;
        detailCard.BorderThickness = new Thickness(SystemParameters.HighContrast ? 2 : 1);
        detailCard.Visibility = Visibility.Visible;
        detailItemKey = item.Key;
        DetailVisibilityChanged?.Invoke(this, true);
        AutomationProperties.SetName(detailCard, $"{item.Title} details");
        AutomationProperties.SetHelpText(detailCard, BuildDetailText(item));
        AutomationProperties.SetItemStatus(
            detailCard,
            Kind == StellarHubKind.Bookmarks ? "Bookmark details" : "Workspace details");
        AutomationProperties.SetLiveSetting(detailCard, AutomationLiveSetting.Polite);
    }

    private void HideDetailUnlessFocused(Button button)
    {
        if (!button.IsMouseOver && !button.IsKeyboardFocusWithin)
        {
            detailCard.Visibility = Visibility.Collapsed;
            detailItemKey = null;
            DetailVisibilityChanged?.Invoke(this, false);
        }
    }

    private static string BuildDetailText(StellarHubItem item) =>
        string.Join(". ", new[] { item.Title, item.Address?.AbsoluteUri ?? item.Subtitle, item.Note }
            .Where(value => !string.IsNullOrWhiteSpace(value)));

    private static bool TryDecodePng(ReadOnlyMemory<byte> bytes, out ImageSource? imageSource)
    {
        imageSource = null;
        if (bytes.Length < 8 || !bytes.Span[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            return false;
        }
        try
        {
            using var stream = new MemoryStream(bytes.ToArray(), writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 64;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            imageSource = image;
            return true;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException)
        {
            return false;
        }
    }

    private void ConfigureEmptyInvitation()
    {
        var title = Kind == StellarHubKind.Bookmarks ? "Bookmark star" : "Workspace star";
        var detail = Kind == StellarHubKind.Bookmarks
            ? "Add a bookmark to chart your first destination."
            : "Save a group of tabs to launch a familiar route.";
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.Ink,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        content.Children.Add(new TextBlock
        {
            Text = detail,
            FontSize = 11.5,
            Foreground = OrbitVisualTheme.MutedInk,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 280,
            Margin = new Thickness(0, 3, 0, 0),
        });
        emptyInvitation.Child = content;
        emptyInvitation.Width = 300;
        emptyInvitation.Padding = new Thickness(12, 9, 12, 9);
        emptyInvitation.Background = new SolidColorBrush(Color.FromArgb(210, 17, 26, 34));
        emptyInvitation.BorderBrush = OrbitVisualTheme.Divider;
        emptyInvitation.BorderThickness = new Thickness(1);
        emptyInvitation.CornerRadius = new CornerRadius(12);
        Canvas.SetLeft(emptyInvitation, 65);
        Canvas.SetTop(emptyInvitation, 204);
        AutomationProperties.SetName(emptyInvitation, $"{title} invitation");
    }

    private Canvas BuildCanvas()
    {
        var root = new Canvas { Width = 430, Height = 280 };
        Canvas.SetLeft(stellarOrbit, 129);
        Canvas.SetTop(stellarOrbit, 54);
        root.Children.Add(stellarOrbit);
        root.Children.Add(itemCanvas);
        return root;
    }

    private sealed class StellarHubDetailCard : Border
    {
        protected override AutomationPeer OnCreateAutomationPeer() =>
            new FrameworkElementAutomationPeer(this);
    }

    /// <summary>
    /// Compatibility entry point for older hosts. The v3 center owns its shared,
    /// visible-only clock; rings and interactive items intentionally do not move.
    /// </summary>
    public void RenderTimelineFrame(int frame, int totalFrames)
    {
        if (totalFrames < 1) throw new ArgumentOutOfRangeException(nameof(totalFrames));
        RefreshMotionPreferences();
    }

    private void RefreshMotionPreferences()
    {
        centerStar.ReducedMotion = ReducedMotion;
        centerStar.MotionEnabled = !ReducedVisualNoise;
        centerStar.IsActive = true;
        stellarOrbit.ReducedMotion = ReducedMotion;
        stellarOrbit.MotionEnabled = !ReducedVisualNoise;
        stellarOrbit.ReduceVisualNoise = ReducedVisualNoise;
        stellarOrbit.IsActive = true;
    }

    private void RefreshAccessibility()
    {
        Visibility = SystemParameters.HighContrast || ReducedVisualNoise ? Visibility.Collapsed : Visibility.Visible;
        RefreshMotionPreferences();
    }
}
#endif
