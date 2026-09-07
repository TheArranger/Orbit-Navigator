#if ORBIT_WPF
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

using FilePath = System.IO.Path;

using OrbitNavigator.Presentation.Shell;

namespace OrbitNavigator.Presentation.Wpf;

/// <summary>
/// Decorative-only New Tab space field. A quiet full-bleed base, transparent
/// nebula and near-star companions, a deterministic far field, and one restrained
/// route move independently on the process-wide shared visual clock. Motion never
/// carries information.
/// </summary>
public sealed class NewTabDecorationControl : Border, IOrbitSharedVisualMotionTimelineTarget
{
    public const int TimelineFramesPerSecond = 30;
    public const int SharedAnimationFramesPerSecond = OrbitSharedVisualMotionClock.FramesPerSecond;
    // Compatibility value retained for callers that shipped with the v3 seam.
    public const int ArtworkVersion = 3;
    public const int LatestArtworkVersion = 6;
    public const string DefaultV3AssetRelativePath = "assets/new-tab/orbit-deep-space-field-v3.png";
    public const string DefaultV4BaseAssetRelativePath = "assets/new-tab/orbit-deep-space-base-v4.png";
    public const string DefaultV4NebulaAssetRelativePath = "assets/new-tab/orbit-nebula-veil-v4.png";
    public const string DefaultV4NearStarfieldAssetRelativePath = "assets/new-tab/orbit-near-starfield-v4.png";
    public const string DefaultV5BaseAssetRelativePath = "assets/new-tab/orbit-deep-space-base-v5.png";
    public const string DefaultV5CelestialAtlasRelativePath = OrbitNewTabCelestialOverlay.AtlasRelativePath;
    public const string DefaultV6BaseAssetARelativePath = "assets/new-tab/orbit-deep-space-base-v6-a.png";
    public const string DefaultV6BaseAssetBRelativePath = "assets/new-tab/orbit-deep-space-base-v6-b.png";
    public const string DefaultV6BaseAssetCRelativePath = "assets/new-tab/orbit-deep-space-base-v6-c.png";
    public const string DefaultV6CelestialAtlasRelativePath = OrbitNewTabCelestialOverlay.AnimatedAtlasRelativePath;
    public const int SceneVariantCount = 3;
    public const string LegacyV2AssetFileName = "orbit-navigation-field-v2.png";
    public const double MaximumParallaxOffset = 3.2;
    public const double MaximumScaleDelta = 0.003;
    public const int MaximumDecodedAssetWidth = 2560;
    public const long MaximumSceneRasterBytes = 3L * 2560 * 1440 * 4;
    public const int SceneLayerCount = 6;
    public const int AnimatedSceneLayerCount = 5;
    public const double StaticSceneTimeSeconds = 37.0;
    public const double OpeningRotationDurationSeconds = 2.4;
    public const double OpeningRotationStartAngle = 0;
    public static readonly TimeSpan FarStarfieldHorizontalPeriod = TimeSpan.FromSeconds(113);
    public static readonly TimeSpan FarStarfieldVerticalPeriod = TimeSpan.FromSeconds(157);
    public static readonly TimeSpan NearStarfieldHorizontalPeriod = TimeSpan.FromSeconds(29);
    public static readonly TimeSpan NearStarfieldVerticalPeriod = TimeSpan.FromSeconds(43);
    public static readonly TimeSpan NebulaHorizontalPeriod = TimeSpan.FromSeconds(97);
    public static readonly TimeSpan NebulaVerticalPeriod = TimeSpan.FromSeconds(131);
    public static readonly TimeSpan OrbitalRoutePeriod = TimeSpan.FromSeconds(13);

    private const int MaximumSharedAssetEntries = 8;
    private const double SharedMotionPhaseOffset = 0.17;
    private const double FarStarTileWidth = 384;
    private const double FarStarTileHeight = 288;
    private const double NearStarTileWidth = 556;
    private const double NearStarTileHeight = 416;
    private static readonly object SharedAssetGate = new();
    private static readonly Dictionary<string, ImageSource> SharedAssets = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Geometry NavigationRouteGeometry = CreateFrozenGeometry(
        "M-70,588 C188,704 430,676 650,566 C850,466 1010,334 1280,276");

    private readonly Image image = new()
    {
        Stretch = Stretch.UniformToFill,
        Opacity = 0.72,
        IsHitTestVisible = false,
        Focusable = false,
    };
    private readonly Image nebulaImage = CreateLayerImage(0.16);
    private readonly Image nearStarfieldImage = CreateLayerImage(0.38);
    private readonly OrbitNewTabCelestialOverlay celestialOverlay = new();
    private readonly ScaleTransform openingSceneScale = new(1, 1);
    private readonly RotateTransform openingSceneRotate = new();
    private readonly ScaleTransform nebulaScale = new(1.018, 1.018);
    private readonly TranslateTransform nebulaTranslate = new();
    private readonly ScaleTransform nearStarfieldScale = new(1.024, 1.024);
    private readonly TranslateTransform nearStarfieldImageTranslate = new();
    private readonly TranslateTransform farStarfieldTranslate = new();
    private readonly TranslateTransform routeTranslate = new();
    private readonly RotateTransform routeRotate = new();
    private readonly System.Windows.Shapes.Path navigationRoutePath;
    private string? requestedAssetPath;
    private string? resolvedAssetPath;
    private string? compatibilityResolvedAssetPath;
    private string? cachedAssetPath;
    private ImageSource? cachedAsset;
    private ImageSource? cachedNebulaAsset;
    private ImageSource? cachedNearStarfieldAsset;
    private bool reducedVisualNoise;
    private bool reducedMotion;
    private bool freezeNonStellarMotion;
    private bool hostLoaded;
    private Window? hostWindow;
    private bool registeredWithSharedClock;
    private bool systemParameterEventsAttached;
    private long receivedMotionFrameCount;
    private double currentFarStarfieldPhase;
    private double currentNearStarfieldPhase;
    private double currentNebulaPhase;
    private double currentOrbitalRoutePhase;
    private double lastMotionElapsedSeconds = StaticSceneTimeSeconds;
    private double? openingEpochSeconds;
    private double currentOpeningRotationAngle;
    private double openingRotationProgress;
    private int sceneVariant;
    private bool isPrivateTheme;
    private double lastRawFrameElapsedSeconds;
    private double frozenRawFrameElapsedSeconds;
    private bool resumeNeedsRebase;

    public static IReadOnlyList<string> DefaultV6BaseAssetRelativePaths { get; } =
    [
        DefaultV6BaseAssetARelativePath,
        DefaultV6BaseAssetBRelativePath,
        DefaultV6BaseAssetCRelativePath,
    ];

    public NewTabDecorationControl()
    {
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        image.RenderTransformOrigin = new Point(0.5, 0.5);
        var openingTransform = new TransformGroup();
        openingTransform.Children.Add(openingSceneScale);
        openingTransform.Children.Add(openingSceneRotate);
        image.RenderTransform = openingTransform;
        ConfigureLayerTransform(nebulaImage, nebulaScale, nebulaTranslate);
        ConfigureLayerTransform(nearStarfieldImage, nearStarfieldScale, nearStarfieldImageTranslate);

        var layers = new Grid { IsHitTestVisible = false, Focusable = false };
        layers.Children.Add(image);
        layers.Children.Add(nebulaImage);
        layers.Children.Add(CreateStarfieldLayer(
            FarStarTileWidth,
            FarStarTileHeight,
            starCount: 23,
            seed: 17,
            opacity: 0.17,
            farStarfieldTranslate));
        layers.Children.Add(new Border
        {
            IsHitTestVisible = false,
            Background = new RadialGradientBrush(
                Color.FromArgb(72, 11, 17, 23),
                Color.FromArgb(0, 11, 17, 23))
            {
                Center = new Point(0.5, 0.46),
                GradientOrigin = new Point(0.5, 0.46),
                RadiusX = 0.58,
                RadiusY = 0.54,
            },
        });
        layers.Children.Add(nearStarfieldImage);
        layers.Children.Add(CreateNavigationOverlay(out navigationRoutePath));
        layers.Children.Add(celestialOverlay);
        Child = layers;

        Background = Brushes.Transparent;
        ClipToBounds = true;
        IsHitTestVisible = false;
        Focusable = false;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
        RefreshAccessibilityState();
    }

    public bool ReducedMotion
    {
        get => reducedMotion;
        set
        {
            if (reducedMotion == value)
            {
                return;
            }

            reducedMotion = value;
            RefreshAccessibilityState();
        }
    }

    /// <summary>
    /// Hover/focus seam for UX data cards. Every background/overlay layer holds
    /// its current state while complete stellar atlases remain independently
    /// animated in their own primitive.
    /// </summary>
    public bool FreezeNonStellarMotion
    {
        get => freezeNonStellarMotion;
        set
        {
            if (freezeNonStellarMotion == value)
            {
                return;
            }

            if (value)
            {
                frozenRawFrameElapsedSeconds = lastRawFrameElapsedSeconds;
            }
            else
            {
                resumeNeedsRebase = true;
            }
            freezeNonStellarMotion = value;
            celestialOverlay.FreezeMotion = value;
            RefreshMotionRegistration();
        }
    }

    /// <summary>Zero-based packaged scene variant selected for a New Tab.</summary>
    public int SceneVariant
    {
        get => sceneVariant;
        set
        {
            var normalized = PositiveModulo(value, SceneVariantCount);
            if (sceneVariant == normalized)
            {
                return;
            }
            sceneVariant = normalized;
            cachedAssetPath = null;
            RefreshAccessibilityState();
        }
    }

    /// <summary>Applies a restrained private accent without changing HC behavior.</summary>
    public bool IsPrivateTheme
    {
        get => isPrivateTheme;
        set
        {
            if (isPrivateTheme == value)
            {
                return;
            }
            isPrivateTheme = value;
            celestialOverlay.IsPrivateTheme = value;
            navigationRoutePath.Stroke = value
                ? OrbitVisualTheme.PrivateViolet
                : OrbitVisualTheme.SeaGlass;
        }
    }

    public bool HasCoordinatorAsset => image.Source is not null;

    public bool HasV3Asset => image.Source is not null &&
        (HasV4Asset || string.Equals(
            FilePath.GetFileName(resolvedAssetPath),
            FilePath.GetFileName(DefaultV3AssetRelativePath),
            StringComparison.OrdinalIgnoreCase));

    public bool HasV4Asset => HasV5Asset || (image.Source is not null &&
        string.Equals(
            FilePath.GetFileName(resolvedAssetPath),
            FilePath.GetFileName(DefaultV4BaseAssetRelativePath),
            StringComparison.OrdinalIgnoreCase));

    public bool HasV5Asset => HasV6Asset || (image.Source is not null &&
        string.Equals(
            FilePath.GetFileName(resolvedAssetPath),
            FilePath.GetFileName(DefaultV5BaseAssetRelativePath),
            StringComparison.OrdinalIgnoreCase));

    public bool HasV6Asset => image.Source is not null &&
        DefaultV6BaseAssetRelativePaths.Any(path => string.Equals(
            FilePath.GetFileName(resolvedAssetPath),
            FilePath.GetFileName(path),
            StringComparison.OrdinalIgnoreCase));

    public bool HasV4CompanionLayers => nebulaImage.Source is not null && nearStarfieldImage.Source is not null;

    public bool HasV5CelestialOverlay => celestialOverlay.HasCelestialAtlas;

    public bool HasV6CelestialOverlay => celestialOverlay.UsesAnimatedArtwork;

    /// <summary>
    /// Compatibility diagnostic for the v2-to-v3 promotion seam. New code can
    /// use <see cref="ResolvedV4BaseAssetPath"/> to inspect the active v4 base.
    /// </summary>
    public string? ResolvedAssetPath => compatibilityResolvedAssetPath ?? resolvedAssetPath;

    public string? ResolvedV4BaseAssetPath => HasV4Asset ? resolvedAssetPath : null;

    public string? ResolvedV5BaseAssetPath => HasV5Asset ? resolvedAssetPath : null;

    public string? ResolvedV6BaseAssetPath => HasV6Asset ? resolvedAssetPath : null;

    public bool IsRegisteredWithSharedClock => registeredWithSharedClock;

    public long ReceivedMotionFrameCount => receivedMotionFrameCount;

    /// <summary>True when the full layered field is available to its host.</summary>
    public bool HasLayeredScene => image.Source is not null &&
        ShouldShowVisualField(SystemParameters.HighContrast, reducedVisualNoise);

    /// <summary>True only while the visible scene is subscribed to the shared clock.</summary>
    public bool IsLayeredMotionActive =>
        registeredWithSharedClock && IsMotionEligible && !FreezeNonStellarMotion;

    public double CurrentFarStarfieldPhase => currentFarStarfieldPhase;

    public double CurrentNearStarfieldPhase => currentNearStarfieldPhase;

    public double CurrentNebulaPhase => currentNebulaPhase;

    public double CurrentOrbitalRoutePhase => currentOrbitalRoutePhase;

    public double LastMotionElapsedSeconds => lastMotionElapsedSeconds;

    public double CurrentOpeningRotationAngle => currentOpeningRotationAngle;

    public double OpeningRotationProgress => openingRotationProgress;

    public OrbitNewTabCelestialOverlay CelestialOverlay => celestialOverlay;

    public static int SharedClockSubscriberCount => OrbitSharedVisualMotionClock.SubscriberCount;

    public static int SharedClockRenderingHandlerCount => OrbitSharedVisualMotionClock.RenderingHandlerCount;

    public static bool ShouldShowVisualField(bool highContrast, bool reduceVisualNoise) =>
        !highContrast && !reduceVisualNoise;

    public void SetCoordinatorAsset(string? filePath, bool reduceVisualNoise)
    {
        requestedAssetPath = filePath;
        reducedVisualNoise = reduceVisualNoise;
        RefreshAccessibilityState();
    }

    public void RefreshAccessibilityState()
    {
        celestialOverlay.ReducedMotion = ReducedMotion;
        celestialOverlay.ReduceVisualNoise = reducedVisualNoise;
        celestialOverlay.FreezeMotion = FreezeNonStellarMotion;
        celestialOverlay.IsPrivateTheme = IsPrivateTheme;
        var show = ShouldShowVisualField(SystemParameters.HighContrast, reducedVisualNoise);
        Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            image.Source = null;
            nebulaImage.Source = null;
            nearStarfieldImage.Source = null;
            resolvedAssetPath = null;
            compatibilityResolvedAssetPath = null;
            RefreshMotionRegistration();
            return;
        }

        resolvedAssetPath = ResolveAssetPath(requestedAssetPath, SceneVariant);
        compatibilityResolvedAssetPath = ResolveCompatibilityAssetPath(requestedAssetPath);
        if (!string.Equals(cachedAssetPath, resolvedAssetPath, StringComparison.OrdinalIgnoreCase))
        {
            cachedAssetPath = resolvedAssetPath;
            cachedAsset = TryLoadBitmap(resolvedAssetPath);
        }

        image.Source = cachedAsset;
        image.Visibility = image.Source is null ? Visibility.Collapsed : Visibility.Visible;
        cachedNebulaAsset = HasV5Asset
            ? null
            : TryLoadBitmap(ResolveCompanionAssetPath(
                resolvedAssetPath,
                DefaultV4NebulaAssetRelativePath));
        cachedNearStarfieldAsset = HasV5Asset
            ? null
            : TryLoadBitmap(ResolveCompanionAssetPath(
                resolvedAssetPath,
                DefaultV4NearStarfieldAssetRelativePath));
        nebulaImage.Source = cachedNebulaAsset;
        nearStarfieldImage.Source = cachedNearStarfieldAsset;
        nebulaImage.Visibility = nebulaImage.Source is null ? Visibility.Collapsed : Visibility.Visible;
        nearStarfieldImage.Visibility = nearStarfieldImage.Source is null ? Visibility.Collapsed : Visibility.Visible;
        RenderSceneAtElapsedSeconds(ReducedMotion ? StaticSceneTimeSeconds : 0);
        RefreshMotionRegistration();
    }

    /// <summary>
    /// Compatibility entry point for the retiring New Tab timer. Once loaded,
    /// the shared visual clock is authoritative and duplicate host frames are
    /// intentionally ignored.
    /// </summary>
    public void RenderTimelineFrame(int frame, int totalFrames)
    {
        if (totalFrames < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(totalFrames));
        }

        if (registeredWithSharedClock)
        {
            return;
        }

        if (ReducedMotion)
        {
            RenderSceneAtElapsedSeconds(StaticSceneTimeSeconds);
            return;
        }

        var normalized = PositiveModulo(frame, totalFrames) / (double)totalFrames;
        RenderSceneAtElapsedSeconds(normalized * FarStarfieldHorizontalPeriod.TotalSeconds);
    }

    internal void ApplySharedMotionFrame(double normalizedPhase)
    {
        Dispatcher.VerifyAccess();
        if (!IsMotionEligible || !double.IsFinite(normalizedPhase))
        {
            return;
        }

        receivedMotionFrameCount++;
        var staggeredPhase = normalizedPhase + SharedMotionPhaseOffset;
        RenderSceneAtElapsedSeconds(
            (staggeredPhase - Math.Floor(staggeredPhase)) * FarStarfieldHorizontalPeriod.TotalSeconds);
    }

    void IOrbitSharedVisualMotionTimelineTarget.ApplySharedMotionFrame(OrbitSharedMotionFrame frame)
    {
        Dispatcher.VerifyAccess();
        if (!IsMotionEligible || !double.IsFinite(frame.ElapsedSeconds))
        {
            return;
        }

        lastRawFrameElapsedSeconds = frame.ElapsedSeconds;
        openingEpochSeconds ??= frame.ElapsedSeconds;
        if (resumeNeedsRebase)
        {
            openingEpochSeconds += frame.ElapsedSeconds - frozenRawFrameElapsedSeconds;
            resumeNeedsRebase = false;
        }
        receivedMotionFrameCount++;
        RenderSceneAtElapsedSeconds(
            Math.Max(0, frame.ElapsedSeconds - openingEpochSeconds.Value) + SharedMotionPhaseOffset);
    }

    Dispatcher IOrbitSharedVisualMotionTarget.Dispatcher => Dispatcher;

    void IOrbitSharedVisualMotionTarget.ApplySharedMotionFrame(double normalizedPhase) =>
        ApplySharedMotionFrame(normalizedPhase);

    private bool IsMotionEligible =>
        hostLoaded &&
        IsVisible &&
        Visibility == Visibility.Visible &&
        IsHostWindowRenderable &&
        image.Source is not null &&
        !FreezeNonStellarMotion &&
        !ReducedMotion &&
        !reducedVisualNoise &&
        !SystemParameters.HighContrast;

    private bool IsHostWindowRenderable =>
        hostWindow is null || (hostWindow.IsVisible && hostWindow.WindowState != WindowState.Minimized);

    private FrameworkElement CreateNavigationOverlay(out System.Windows.Shapes.Path routePath)
    {
        var canvas = new Canvas
        {
            Width = 1200,
            Height = 760,
            Opacity = 0.28,
            IsHitTestVisible = false,
            Focusable = false,
        };
        routePath = new System.Windows.Shapes.Path
        {
            Data = NavigationRouteGeometry,
            Stroke = OrbitVisualTheme.SeaGlass,
            StrokeThickness = 1.15,
            StrokeDashArray = new DoubleCollection { 2, 14 },
            Opacity = 0.64,
        };
        canvas.Children.Add(routePath);
        AddWaypoint(canvas, 252, 664, OrbitVisualTheme.WaypointGold, 6);
        AddWaypoint(canvas, 650, 562, OrbitVisualTheme.PrivateViolet, 5);
        AddWaypoint(canvas, 1018, 329, OrbitVisualTheme.SeaGlass, 6);
        var transforms = new TransformGroup();
        transforms.Children.Add(routeRotate);
        transforms.Children.Add(routeTranslate);
        canvas.RenderTransform = transforms;
        canvas.RenderTransformOrigin = new Point(0.5, 0.5);
        return new Viewbox
        {
            Stretch = Stretch.UniformToFill,
            Child = canvas,
            IsHitTestVisible = false,
            Focusable = false,
        };
    }

    private static Image CreateLayerImage(double opacity) => new()
    {
        Stretch = Stretch.UniformToFill,
        Opacity = opacity,
        IsHitTestVisible = false,
        Focusable = false,
        RenderTransformOrigin = new Point(0.5, 0.5),
    };

    private static void ConfigureLayerTransform(
        Image layer,
        ScaleTransform scale,
        TranslateTransform translate)
    {
        RenderOptions.SetBitmapScalingMode(layer, BitmapScalingMode.HighQuality);
        var transforms = new TransformGroup();
        transforms.Children.Add(scale);
        transforms.Children.Add(translate);
        layer.RenderTransform = transforms;
    }

    private static Border CreateStarfieldLayer(
        double tileWidth,
        double tileHeight,
        int starCount,
        int seed,
        double opacity,
        TranslateTransform translate)
    {
        var stars = new DrawingGroup();
        for (var index = 0; index < starCount; index++)
        {
            var x = DeterministicUnit(seed, index, 13) * tileWidth;
            var y = DeterministicUnit(seed, index, 29) * tileHeight;
            var radius = 0.35 + (DeterministicUnit(seed, index, 47) * 0.9);
            var alpha = (byte)(78 + (DeterministicUnit(seed, index, 71) * 112));
            var color = index % 5 == 0
                ? Color.FromArgb(alpha, 157, 218, 236)
                : Color.FromArgb(alpha, 238, 246, 244);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            var geometry = new EllipseGeometry(new Point(x, y), radius, radius);
            geometry.Freeze();
            stars.Children.Add(new GeometryDrawing(brush, null, geometry));
        }
        stars.Freeze();

        var starBrush = new DrawingBrush(stars)
        {
            TileMode = TileMode.Tile,
            ViewboxUnits = BrushMappingMode.Absolute,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, tileWidth, tileHeight),
            Viewport = new Rect(0, 0, tileWidth, tileHeight),
            Stretch = Stretch.None,
            Transform = translate,
        };
        RenderOptions.SetCachingHint(starBrush, CachingHint.Cache);
        RenderOptions.SetCacheInvalidationThresholdMinimum(starBrush, 0.8);
        RenderOptions.SetCacheInvalidationThresholdMaximum(starBrush, 1.2);
        return new Border
        {
            Background = starBrush,
            Opacity = opacity,
            IsHitTestVisible = false,
            Focusable = false,
        };
    }

    private static double DeterministicUnit(int seed, int index, int salt)
    {
        var value = unchecked((uint)(seed * 374761393 + index * 668265263 + salt * 69069));
        value = (value ^ (value >> 13)) * 1274126177u;
        value ^= value >> 16;
        return value / (double)uint.MaxValue;
    }

    private static void AddWaypoint(Canvas canvas, double x, double y, Brush brush, double size)
    {
        var waypoint = new Ellipse
        {
            Width = size,
            Height = size,
            Fill = brush,
            Stroke = OrbitVisualTheme.Ink,
            StrokeThickness = 0.6,
        };
        Canvas.SetLeft(waypoint, x - (size / 2));
        Canvas.SetTop(waypoint, y - (size / 2));
        canvas.Children.Add(waypoint);
    }

    private static Geometry CreateFrozenGeometry(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }

    private static string? ResolveAssetPath(string? requestedPath, int sceneVariant)
    {
        var selectedV6RelativePath = DefaultV6BaseAssetRelativePaths[
            PositiveModulo(sceneVariant, SceneVariantCount)];
        var packagedV6Path = FilePath.Combine(
            AppContext.BaseDirectory,
            selectedV6RelativePath.Replace('/', FilePath.DirectorySeparatorChar));
        var packagedV5Path = FilePath.Combine(
            AppContext.BaseDirectory,
            DefaultV5BaseAssetRelativePath.Replace('/', FilePath.DirectorySeparatorChar));
        var packagedV4Path = FilePath.Combine(
            AppContext.BaseDirectory,
            DefaultV4BaseAssetRelativePath.Replace('/', FilePath.DirectorySeparatorChar));
        var packagedV3Path = FilePath.Combine(
            AppContext.BaseDirectory,
            DefaultV3AssetRelativePath.Replace('/', FilePath.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            if (File.Exists(packagedV6Path))
            {
                return packagedV6Path;
            }

            if (File.Exists(packagedV5Path))
            {
                return packagedV5Path;
            }

            if (File.Exists(packagedV4Path))
            {
                return packagedV4Path;
            }

            return File.Exists(packagedV3Path) ? packagedV3Path : null;
        }

        var requestedFileName = FilePath.GetFileName(requestedPath);
        if (string.Equals(requestedFileName, LegacyV2AssetFileName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                requestedFileName,
                FilePath.GetFileName(DefaultV3AssetRelativePath),
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                requestedFileName,
                FilePath.GetFileName(DefaultV4BaseAssetRelativePath),
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                requestedFileName,
                FilePath.GetFileName(DefaultV5BaseAssetRelativePath),
                StringComparison.OrdinalIgnoreCase))
        {
            var siblingV6 = FilePath.Combine(
                FilePath.GetDirectoryName(requestedPath) ?? string.Empty,
                FilePath.GetFileName(selectedV6RelativePath));
            if (File.Exists(siblingV6))
            {
                return siblingV6;
            }

            if (File.Exists(packagedV6Path))
            {
                return packagedV6Path;
            }

            var siblingV5 = FilePath.Combine(
                FilePath.GetDirectoryName(requestedPath) ?? string.Empty,
                FilePath.GetFileName(DefaultV5BaseAssetRelativePath));
            if (File.Exists(siblingV5))
            {
                return siblingV5;
            }

            if (File.Exists(packagedV5Path))
            {
                return packagedV5Path;
            }

            var siblingV4 = FilePath.Combine(
                FilePath.GetDirectoryName(requestedPath) ?? string.Empty,
                FilePath.GetFileName(DefaultV4BaseAssetRelativePath));
            if (File.Exists(siblingV4))
            {
                return siblingV4;
            }

            if (File.Exists(packagedV4Path))
            {
                return packagedV4Path;
            }

            if (string.Equals(requestedFileName, LegacyV2AssetFileName, StringComparison.OrdinalIgnoreCase))
            {
                var siblingV3 = FilePath.Combine(
                    FilePath.GetDirectoryName(requestedPath) ?? string.Empty,
                    FilePath.GetFileName(DefaultV3AssetRelativePath));
                if (File.Exists(siblingV3))
                {
                    return siblingV3;
                }
            }
        }

        return File.Exists(requestedPath) ? requestedPath : null;
    }

    private static string? ResolveCompanionAssetPath(string? basePath, string relativePath)
    {
        var fileName = FilePath.GetFileName(relativePath);
        if (!string.IsNullOrWhiteSpace(basePath))
        {
            var sibling = FilePath.Combine(FilePath.GetDirectoryName(basePath) ?? string.Empty, fileName);
            if (File.Exists(sibling))
            {
                return sibling;
            }
        }

        var packaged = FilePath.Combine(
            AppContext.BaseDirectory,
            relativePath.Replace('/', FilePath.DirectorySeparatorChar));
        return File.Exists(packaged) ? packaged : null;
    }

    private static string? ResolveCompatibilityAssetPath(string? requestedPath)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return null;
        }

        var requestedFileName = FilePath.GetFileName(requestedPath);
        if (string.Equals(
            requestedFileName,
            FilePath.GetFileName(DefaultV3AssetRelativePath),
            StringComparison.OrdinalIgnoreCase))
        {
            return File.Exists(requestedPath) ? requestedPath : null;
        }

        if (!string.Equals(requestedFileName, LegacyV2AssetFileName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var siblingV3 = FilePath.Combine(
            FilePath.GetDirectoryName(requestedPath) ?? string.Empty,
            FilePath.GetFileName(DefaultV3AssetRelativePath));
        if (File.Exists(siblingV3))
        {
            return siblingV3;
        }

        var packagedV3 = FilePath.Combine(
            AppContext.BaseDirectory,
            DefaultV3AssetRelativePath.Replace('/', FilePath.DirectorySeparatorChar));
        return File.Exists(packagedV3) ? packagedV3 : null;
    }

    private static ImageSource? TryLoadBitmap(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        var fullPath = FilePath.GetFullPath(path);
        lock (SharedAssetGate)
        {
            if (SharedAssets.TryGetValue(fullPath, out var shared))
            {
                return shared;
            }
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bitmap.DecodePixelWidth = MaximumDecodedAssetWidth;
            bitmap.UriSource = new Uri(fullPath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            lock (SharedAssetGate)
            {
                if (SharedAssets.Count >= MaximumSharedAssetEntries)
                {
                    using var keys = SharedAssets.Keys.GetEnumerator();
                    if (keys.MoveNext())
                    {
                        SharedAssets.Remove(keys.Current);
                    }
                }
                SharedAssets[fullPath] = bitmap;
            }
            return bitmap;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or
            UriFormatException or ArgumentException)
        {
            return null;
        }
    }

    private void RenderSceneAtElapsedSeconds(double elapsedSeconds)
    {
        if (!double.IsFinite(elapsedSeconds))
        {
            return;
        }

        lastMotionElapsedSeconds = elapsedSeconds;
        openingRotationProgress = ReducedMotion
            ? 1
            : Math.Clamp(elapsedSeconds / OpeningRotationDurationSeconds, 0, 1);
        currentOpeningRotationAngle = 0;
        openingSceneRotate.Angle = currentOpeningRotationAngle;
        currentFarStarfieldPhase = PeriodPhase(elapsedSeconds, FarStarfieldHorizontalPeriod);
        currentNearStarfieldPhase = PeriodPhase(elapsedSeconds, NearStarfieldHorizontalPeriod);
        currentNebulaPhase = PeriodPhase(elapsedSeconds, NebulaHorizontalPeriod);
        currentOrbitalRoutePhase = PeriodPhase(elapsedSeconds, OrbitalRoutePeriod);

        farStarfieldTranslate.X = -FarStarTileWidth * currentFarStarfieldPhase;
        farStarfieldTranslate.Y = -FarStarTileHeight * PeriodPhase(elapsedSeconds, FarStarfieldVerticalPeriod);

        var nebulaXPhase = currentNebulaPhase * Math.PI * 2;
        var nebulaYPhase = PeriodPhase(elapsedSeconds, NebulaVerticalPeriod) * Math.PI * 2;
        var scalePhase = PeriodPhase(elapsedSeconds, TimeSpan.FromSeconds(181)) * Math.PI * 2;
        var scale = 1.018 + (MaximumScaleDelta * Math.Sin(scalePhase));
        nebulaScale.ScaleX = scale;
        nebulaScale.ScaleY = scale;
        nebulaTranslate.X = MaximumParallaxOffset * Math.Sin(nebulaXPhase);
        nebulaTranslate.Y = (MaximumParallaxOffset * 0.58) * Math.Cos(nebulaYPhase);

        // The photographic near-star layer follows different non-harmonic axes.
        // Its small overdraw keeps every edge covered without tiling artifacts.
        var nearHorizontal = currentNearStarfieldPhase * Math.PI * 2;
        var nearVertical = PeriodPhase(elapsedSeconds, NearStarfieldVerticalPeriod) * Math.PI * 2;
        nearStarfieldImageTranslate.X = 10.0 * Math.Sin(nearHorizontal);
        nearStarfieldImageTranslate.Y = 6.5 * Math.Cos(nearVertical);
        var nearScale = 1.024 + (0.0015 * Math.Sin(
            PeriodPhase(elapsedSeconds, TimeSpan.FromSeconds(149)) * Math.PI * 2));
        nearStarfieldScale.ScaleX = nearScale;
        nearStarfieldScale.ScaleY = nearScale;

        navigationRoutePath.StrokeDashOffset = -(32 * currentOrbitalRoutePhase);
        routeTranslate.X = 1.4 * Math.Sin(PeriodPhase(elapsedSeconds, TimeSpan.FromSeconds(71)) * Math.PI * 2);
        routeTranslate.Y = 0.8 * Math.Cos(PeriodPhase(elapsedSeconds, TimeSpan.FromSeconds(103)) * Math.PI * 2);
        routeRotate.Angle = 0.12 * Math.Sin(PeriodPhase(elapsedSeconds, TimeSpan.FromSeconds(137)) * Math.PI * 2);
    }

    private static double PeriodPhase(double elapsedSeconds, TimeSpan period)
    {
        var duration = period.TotalSeconds;
        var remainder = elapsedSeconds % duration;
        if (remainder < 0)
        {
            remainder += duration;
        }
        return remainder / duration;
    }

    private static int PositiveModulo(int value, int divisor)
    {
        var remainder = value % divisor;
        return remainder < 0 ? remainder + divisor : remainder;
    }

    private void RefreshMotionRegistration()
    {
        if (IsMotionEligible)
        {
            if (!registeredWithSharedClock)
            {
                OrbitSharedVisualMotionClock.Register(this);
                registeredWithSharedClock = true;
            }
            return;
        }

        if (registeredWithSharedClock)
        {
            OrbitSharedVisualMotionClock.Unregister(this);
            registeredWithSharedClock = false;
        }
        if (ReducedMotion || reducedVisualNoise || SystemParameters.HighContrast)
        {
            RenderSceneAtElapsedSeconds(StaticSceneTimeSeconds);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        hostLoaded = true;
        openingEpochSeconds = null;
        AttachHostWindow();
        if (!systemParameterEventsAttached)
        {
            SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
            systemParameterEventsAttached = true;
        }

        RefreshAccessibilityState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        hostLoaded = false;
        openingEpochSeconds = null;
        RefreshMotionRegistration();
        DetachHostWindow();
        if (systemParameterEventsAttached)
        {
            SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
            systemParameterEventsAttached = false;
        }
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs args) =>
        RefreshMotionRegistration();

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(SystemParameters.HighContrast) or null)
        {
            RefreshAccessibilityState();
        }
    }

    private void AttachHostWindow()
    {
        var candidate = Window.GetWindow(this);
        if (ReferenceEquals(candidate, hostWindow))
        {
            return;
        }

        DetachHostWindow();
        hostWindow = candidate;
        if (hostWindow is not null)
        {
            hostWindow.StateChanged += OnHostWindowStateChanged;
        }
    }

    private void DetachHostWindow()
    {
        if (hostWindow is not null)
        {
            hostWindow.StateChanged -= OnHostWindowStateChanged;
            hostWindow = null;
        }
    }

    private void OnHostWindowStateChanged(object? sender, EventArgs args) =>
        RefreshMotionRegistration();
}
#endif
