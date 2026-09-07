#if ORBIT_WPF
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OrbitNavigator.Presentation.Wpf;

/// <summary>
/// Transparent Orbit Navigator program identity hosted by the New Tab surface.
/// The control keeps New Tab-specific automation semantics while sharing the
/// approved v2 artwork used by program identity surfaces.
/// </summary>
public sealed class OrbitNewTabLogo : FrameworkElement
{
    public const int MasterViewBoxSize = 96;
    public const string IntendedSurface = "New Tab page";
    public const string BrandingApprovalStatus = "Approved Orbit Navigator program-wide identity";
    public const string ProgramLogoRelativePath = "assets/branding/orbit-navigator-program-logo-v2.png";
    public const string ProgramLogoCandidateRelativePath = ProgramLogoRelativePath;
    public const string OrbitPathData =
        "M13,50 C13,28 30,12 52,12 C72,12 86,27 86,47 C86,68 69,84 47,84 C28,84 14,72 13,55";
    public const string RoutePathData =
        "M15,62 C31,75 61,76 81,55";
    public const string NavigationPointerPathData =
        "M55,19 L86,10 L77,41 L69,30 L57,34 Z";

    public static readonly DependencyProperty PrimaryProperty = DependencyProperty.Register(
        nameof(Primary),
        typeof(Brush),
        typeof(OrbitNewTabLogo),
        new FrameworkPropertyMetadata(
            OrbitVisualTheme.SeaGlass,
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent),
        typeof(Brush),
        typeof(OrbitNewTabLogo),
        new FrameworkPropertyMetadata(
            OrbitVisualTheme.WaypointGold,
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness),
        typeof(double),
        typeof(OrbitNewTabLogo),
        new FrameworkPropertyMetadata(
            4.4,
            FrameworkPropertyMetadataOptions.AffectsRender,
            null,
            CoerceStrokeThickness));

    public static readonly DependencyProperty ArtworkSourceProperty = DependencyProperty.Register(
        nameof(ArtworkSource),
        typeof(ImageSource),
        typeof(OrbitNewTabLogo),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Geometry OrbitGeometry = CreateFrozenGeometry(OrbitPathData);
    private static readonly Geometry RouteGeometry = CreateFrozenGeometry(RoutePathData);
    private static readonly Geometry NavigationPointerGeometry = CreateFrozenGeometry(NavigationPointerPathData);
    private static readonly object DefaultArtworkGate = new();
    private static ImageSource? defaultArtwork;
    private static bool defaultArtworkLoadAttempted;
    private bool systemParameterEventsAttached;

    public OrbitNewTabLogo()
    {
        Focusable = false;
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        AutomationProperties.SetName(this, "Orbit Navigator New Tab logo");
        AutomationProperties.SetHelpText(
            this,
            "The Orbit Navigator program logo on the New Tab page: an O-shaped orbital path, waypoints, and a north-east navigation pointer. The canvas is transparent.");
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public Brush Primary
    {
        get => (Brush)GetValue(PrimaryProperty);
        set => SetValue(PrimaryProperty, value);
    }

    public Brush Accent
    {
        get => (Brush)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    /// <summary>
    /// Optional source override. When null, the control resolves
    /// <see cref="ProgramLogoRelativePath"/> beneath the app base
    /// directory once and shares that frozen bitmap between instances.
    /// </summary>
    public ImageSource? ArtworkSource
    {
        get => (ImageSource?)GetValue(ArtworkSourceProperty);
        set => SetValue(ArtworkSourceProperty, value);
    }

    /// <summary>The visual never paints a canvas or background rectangle.</summary>
    public bool UsesTransparentBackground => true;

    /// <summary>True when explicit or packaged v2 artwork is available.</summary>
    public bool HasArtwork => EffectiveArtworkSource is not null;

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new OrbitNewTabLogoAutomationPeer(this);

    protected override Size MeasureOverride(Size availableSize)
    {
        const double desired = 96;
        return new Size(
            double.IsInfinity(availableSize.Width) ? desired : Math.Min(desired, availableSize.Width),
            double.IsInfinity(availableSize.Height) ? desired : Math.Min(desired, availableSize.Height));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0)
        {
            return;
        }

        var highContrast = SystemParameters.HighContrast;
        var artwork = highContrast ? null : EffectiveArtworkSource;
        var square = new Rect(
            (ActualWidth - size) / 2,
            (ActualHeight - size) / 2,
            size,
            size);
        if (artwork is not null)
        {
            drawingContext.DrawImage(artwork, square);
            return;
        }

        DrawCodeNativeIdentity(drawingContext, square, highContrast);
    }

    private ImageSource? EffectiveArtworkSource => ArtworkSource ?? GetDefaultArtwork();

    private void DrawCodeNativeIdentity(DrawingContext drawingContext, Rect square, bool highContrast)
    {
        var primary = highContrast ? SystemColors.WindowTextBrush : Primary;
        var accent = highContrast ? SystemColors.HighlightBrush : Accent;
        var scale = square.Width / MasterViewBoxSize;
        drawingContext.PushTransform(new TranslateTransform(square.X, square.Y));
        drawingContext.PushTransform(new ScaleTransform(scale, scale));

        var orbitPen = CreatePen(primary, highContrast ? 6.2 : StrokeThickness);
        var routePen = CreatePen(accent, highContrast ? 4.8 : Math.Max(3.2, StrokeThickness - 0.8));
        drawingContext.DrawGeometry(null, orbitPen, OrbitGeometry);
        drawingContext.DrawGeometry(null, routePen, RouteGeometry);

        // Different waypoint sizes remain legible when color is unavailable.
        drawingContext.DrawEllipse(accent, CreatePen(primary, highContrast ? 2.2 : 1.5), new Point(15, 62), 5.2, 5.2);
        drawingContext.DrawEllipse(primary, null, new Point(81, 55), 3.2, 3.2);

        // A bold filled navigation pointer provides the decisive direction cue.
        drawingContext.DrawGeometry(accent, CreatePen(primary, highContrast ? 3.2 : 2.2), NavigationPointerGeometry);
        drawingContext.DrawLine(CreatePen(primary, highContrast ? 3 : 2), new Point(69, 30), new Point(81, 17));

        drawingContext.Pop();
        drawingContext.Pop();
    }

    private static ImageSource? GetDefaultArtwork()
    {
        lock (DefaultArtworkGate)
        {
            if (defaultArtworkLoadAttempted)
            {
                return defaultArtwork;
            }

            defaultArtworkLoadAttempted = true;
            var path = Path.Combine(
                AppContext.BaseDirectory,
                ProgramLogoRelativePath.Replace('/', Path.DirectorySeparatorChar));
            defaultArtwork = LoadFrozenBitmap(path);
            return defaultArtwork;
        }
    }

    private static BitmapSource? LoadFrozenBitmap(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            if (frame.CanFreeze)
            {
                frame.Freeze();
            }
            return frame;
        }
        catch (IOException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static object CoerceStrokeThickness(DependencyObject dependencyObject, object baseValue) =>
        Math.Clamp((double)baseValue, 2.5, 8);

    private static Geometry CreateFrozenGeometry(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }

    private static Pen CreatePen(Brush brush, double thickness)
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

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (!systemParameterEventsAttached)
        {
            SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
            systemParameterEventsAttached = true;
        }
        InvalidateVisual();
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
            InvalidateVisual();
        }
    }

    private sealed class OrbitNewTabLogoAutomationPeer(OrbitNewTabLogo owner)
        : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(OrbitNewTabLogo);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
    }
}
#endif
