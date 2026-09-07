#if ORBIT_WPF
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace OrbitNavigator.Presentation.Wpf;

/// <summary>
/// Sparse, transparent, frame-animated celestial motion for New Tab. The
/// element-major v6 atlas supplies genuine comet, planet, galaxy, and glint
/// evolution while one process-wide clock owns the visible-only cadence.
/// </summary>
public sealed class OrbitNewTabCelestialOverlay : Grid, IOrbitSharedVisualMotionTimelineTarget
{
    // Compatibility constants for the shipped v5 single-frame atlas.
    public const int ArtworkVersion = 5;
    public const string AtlasRelativePath = "assets/new-tab/orbit-celestial-elements-atlas-v5.png";
    public const int AtlasColumns = 2;
    public const int AtlasRows = 2;
    public const int ElementCount = AtlasColumns * AtlasRows;

    public const int LatestArtworkVersion = 6;
    public const string AnimatedAtlasRelativePath = "assets/new-tab/orbit-celestial-frames-atlas-v6.png";
    public const int AnimatedAtlasColumns = 8;
    public const int AnimatedAtlasRows = 4;
    public const int AnimatedFramesPerElement = 8;
    public const int SharedAnimationFramesPerSecond = OrbitSharedVisualMotionClock.FramesPerSecond;
    public const double StaticSceneTimeSeconds = 17.0;

    public static readonly TimeSpan CometPeriod = TimeSpan.FromSeconds(19);
    public static readonly TimeSpan PlanetPeriod = TimeSpan.FromSeconds(31);
    public static readonly TimeSpan GalaxyPeriod = TimeSpan.FromSeconds(43);
    public static readonly TimeSpan GlintPeriod = TimeSpan.FromSeconds(9);
    public static readonly TimeSpan CometEvolutionPeriod = TimeSpan.FromSeconds(2.6);
    public static readonly TimeSpan PlanetEvolutionPeriod = TimeSpan.FromSeconds(4.0);
    public static readonly TimeSpan GalaxyEvolutionPeriod = TimeSpan.FromSeconds(5.4);
    public static readonly TimeSpan GlintEvolutionPeriod = TimeSpan.FromSeconds(1.8);

    public static readonly DependencyProperty ReducedMotionProperty = DependencyProperty.Register(
        nameof(ReducedMotion), typeof(bool), typeof(OrbitNewTabCelestialOverlay),
        new FrameworkPropertyMetadata(false, OnMotionStateChanged));

    public static readonly DependencyProperty ReduceVisualNoiseProperty = DependencyProperty.Register(
        nameof(ReduceVisualNoise), typeof(bool), typeof(OrbitNewTabCelestialOverlay),
        new FrameworkPropertyMetadata(false, OnMotionStateChanged));

    public static readonly DependencyProperty FreezeMotionProperty = DependencyProperty.Register(
        nameof(FreezeMotion), typeof(bool), typeof(OrbitNewTabCelestialOverlay),
        new FrameworkPropertyMetadata(false, OnMotionStateChanged));

    public static readonly DependencyProperty MotionEnabledProperty = DependencyProperty.Register(
        nameof(MotionEnabled), typeof(bool), typeof(OrbitNewTabCelestialOverlay),
        new FrameworkPropertyMetadata(true, OnMotionStateChanged));

    public static readonly DependencyProperty IsPrivateThemeProperty = DependencyProperty.Register(
        nameof(IsPrivateTheme), typeof(bool), typeof(OrbitNewTabCelestialOverlay),
        new FrameworkPropertyMetadata(false, OnMotionStateChanged));

    private const double DesignWidth = 1200;
    private const double DesignHeight = 760;
    private static readonly object AtlasGate = new();
    private static CelestialFrameSet? cachedFrameSet;

    private readonly SpriteLayer comet = new(218, 218, 0.32);
    private readonly SpriteLayer planet = new(150, 150, 0.30);
    private readonly SpriteLayer galaxy = new(380, 168, 0.17);
    private readonly SpriteLayer glintA = new(64, 64, 0.32);
    private readonly SpriteLayer glintB = new(50, 50, 0.28);
    private readonly SpriteLayer glintC = new(38, 38, 0.24);
    private readonly TranslateTransform cometTranslate = new();
    private readonly RotateTransform cometRotate = new(-42);
    private readonly TranslateTransform planetTranslate = new();
    private readonly TranslateTransform galaxyTranslate = new();
    private readonly RotateTransform galaxyRotate = new(-7);
    private bool hostLoaded;
    private Window? hostWindow;
    private bool registeredWithSharedClock;
    private bool systemParameterEventsAttached;
    private long receivedMotionFrameCount;
    private double currentCometPhase;
    private double currentPlanetPhase;
    private double currentGalaxyPhase;
    private double currentGlintPhase;
    private double lastElapsedSeconds = StaticSceneTimeSeconds;
    private double timelineOffsetSeconds;
    private bool resumeNeedsRebase;
    private double heldElapsedSeconds = StaticSceneTimeSeconds;
    private int currentCometFrameIndex;
    private int currentPlanetFrameIndex;
    private int currentGalaxyFrameIndex;
    private int currentGlintFrameIndex;
    private CelestialFrameSet? frames;

    public OrbitNewTabCelestialOverlay()
    {
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = true;
        Background = Brushes.Transparent;

        var designCanvas = new Canvas
        {
            Width = DesignWidth,
            Height = DesignHeight,
            IsHitTestVisible = false,
            Focusable = false,
        };
        ConfigureTransform(comet.Host, cometRotate, cometTranslate);
        ConfigureTransform(planet.Host, planetTranslate);
        ConfigureTransform(galaxy.Host, galaxyRotate, galaxyTranslate);
        Canvas.SetLeft(glintA.Host, 80);
        Canvas.SetTop(glintA.Host, 510);
        Canvas.SetLeft(glintB.Host, 1040);
        Canvas.SetTop(glintB.Host, 610);
        Canvas.SetLeft(glintC.Host, 1110);
        Canvas.SetTop(glintC.Host, 255);
        designCanvas.Children.Add(galaxy.Host);
        designCanvas.Children.Add(planet.Host);
        designCanvas.Children.Add(comet.Host);
        designCanvas.Children.Add(glintA.Host);
        designCanvas.Children.Add(glintB.Host);
        designCanvas.Children.Add(glintC.Host);

        Children.Add(new Viewbox
        {
            Stretch = Stretch.UniformToFill,
            Child = designCanvas,
            IsHitTestVisible = false,
            Focusable = false,
        });

        LoadAtlasFrames();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
        RefreshAccessibilityState();
    }

    public bool ReducedMotion
    {
        get => (bool)GetValue(ReducedMotionProperty);
        set => SetValue(ReducedMotionProperty, value);
    }

    public bool ReduceVisualNoise
    {
        get => (bool)GetValue(ReduceVisualNoiseProperty);
        set => SetValue(ReduceVisualNoiseProperty, value);
    }

    /// <summary>Freezes celestial movement and frames while stellar hubs continue.</summary>
    public bool FreezeMotion
    {
        get => (bool)GetValue(FreezeMotionProperty);
        set => SetValue(FreezeMotionProperty, value);
    }

    public bool MotionEnabled
    {
        get => (bool)GetValue(MotionEnabledProperty);
        set => SetValue(MotionEnabledProperty, value);
    }

    public bool IsPrivateTheme
    {
        get => (bool)GetValue(IsPrivateThemeProperty);
        set => SetValue(IsPrivateThemeProperty, value);
    }

    public bool HasCelestialAtlas => frames is not null;

    public bool UsesAnimatedArtwork => frames?.FramesPerElement == AnimatedFramesPerElement;

    public int ResolvedFramesPerElement => frames?.FramesPerElement ?? 0;

    public bool UsesTransparentBackground => true;

    public bool IsRegisteredWithSharedClock => registeredWithSharedClock;

    public bool IsMotionActive => registeredWithSharedClock && IsMotionEligible;

    public long ReceivedMotionFrameCount => receivedMotionFrameCount;

    public double CurrentCometPhase => currentCometPhase;

    public double CurrentPlanetPhase => currentPlanetPhase;

    public double CurrentGalaxyPhase => currentGalaxyPhase;

    public double CurrentGlintPhase => currentGlintPhase;

    public int CurrentCometFrameIndex => currentCometFrameIndex;

    public int CurrentPlanetFrameIndex => currentPlanetFrameIndex;

    public int CurrentGalaxyFrameIndex => currentGalaxyFrameIndex;

    public int CurrentGlintFrameIndex => currentGlintFrameIndex;

    public double LastElapsedSeconds => lastElapsedSeconds;

    public static int SharedClockSubscriberCount => OrbitSharedVisualMotionClock.SubscriberCount;

    public static int SharedClockRenderingHandlerCount => OrbitSharedVisualMotionClock.RenderingHandlerCount;

    internal void ApplySharedMotionFrame(double normalizedPhase)
    {
        Dispatcher.VerifyAccess();
        if (!IsMotionEligible || !double.IsFinite(normalizedPhase))
        {
            return;
        }
        RenderAtElapsedSeconds(normalizedPhase * CometPeriod.TotalSeconds);
        receivedMotionFrameCount++;
    }

    void IOrbitSharedVisualMotionTimelineTarget.ApplySharedMotionFrame(OrbitSharedMotionFrame frame)
    {
        Dispatcher.VerifyAccess();
        if (!IsMotionEligible || !double.IsFinite(frame.ElapsedSeconds))
        {
            return;
        }
        if (resumeNeedsRebase)
        {
            timelineOffsetSeconds = heldElapsedSeconds - frame.ElapsedSeconds;
            resumeNeedsRebase = false;
        }
        RenderAtElapsedSeconds(frame.ElapsedSeconds + timelineOffsetSeconds);
        receivedMotionFrameCount++;
    }

    Dispatcher IOrbitSharedVisualMotionTarget.Dispatcher => Dispatcher;

    void IOrbitSharedVisualMotionTarget.ApplySharedMotionFrame(double normalizedPhase) =>
        ApplySharedMotionFrame(normalizedPhase);

    private bool IsMotionEligible =>
        hostLoaded && IsVisible && Visibility == Visibility.Visible && IsHostWindowRenderable &&
        HasCelestialAtlas && MotionEnabled && !FreezeMotion && !ReducedMotion &&
        !ReduceVisualNoise && !SystemParameters.HighContrast;

    private bool IsHostWindowRenderable =>
        hostWindow is null || (hostWindow.IsVisible && hostWindow.WindowState != WindowState.Minimized);

    private void LoadAtlasFrames()
    {
        frames = ResolveFrameSet();
        if (frames is null)
        {
            return;
        }
        RenderAtElapsedSeconds(StaticSceneTimeSeconds);
    }

    private static CelestialFrameSet? ResolveFrameSet()
    {
        lock (AtlasGate)
        {
            if (cachedFrameSet is not null)
            {
                return cachedFrameSet;
            }
            var animated = LoadFrozenBitmap(Path.Combine(
                AppContext.BaseDirectory,
                AnimatedAtlasRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (animated is not null &&
                animated.PixelWidth % AnimatedAtlasColumns == 0 &&
                animated.PixelHeight % AnimatedAtlasRows == 0)
            {
                var cellWidth = animated.PixelWidth / AnimatedAtlasColumns;
                var cellHeight = animated.PixelHeight / AnimatedAtlasRows;
                if (cellWidth == cellHeight)
                {
                    cachedFrameSet = CropFrameSet(
                        animated, AnimatedAtlasColumns, AnimatedAtlasRows, AnimatedFramesPerElement);
                    return cachedFrameSet;
                }
            }

            var legacy = LoadFrozenBitmap(Path.Combine(
                AppContext.BaseDirectory,
                AtlasRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (legacy is null || legacy.PixelWidth % AtlasColumns != 0 ||
                legacy.PixelHeight % AtlasRows != 0)
            {
                return null;
            }
            var legacyWidth = legacy.PixelWidth / AtlasColumns;
            var legacyHeight = legacy.PixelHeight / AtlasRows;
            if (legacyWidth != legacyHeight)
            {
                return null;
            }
            var elementFrames = new BitmapSource[ElementCount][];
            for (var index = 0; index < ElementCount; index++)
            {
                var crop = new CroppedBitmap(legacy, new Int32Rect(
                    (index % AtlasColumns) * legacyWidth,
                    (index / AtlasColumns) * legacyHeight,
                    legacyWidth,
                    legacyHeight));
                crop.Freeze();
                elementFrames[index] = [crop];
            }
            cachedFrameSet = new CelestialFrameSet(elementFrames, 1);
            return cachedFrameSet;
        }
    }

    private static CelestialFrameSet CropFrameSet(
        BitmapSource source,
        int columns,
        int rows,
        int framesPerElement)
    {
        var cellWidth = source.PixelWidth / columns;
        var cellHeight = source.PixelHeight / rows;
        var elements = new BitmapSource[rows][];
        for (var row = 0; row < rows; row++)
        {
            var rowFrames = new BitmapSource[framesPerElement];
            for (var column = 0; column < framesPerElement; column++)
            {
                var crop = new CroppedBitmap(source, new Int32Rect(
                    column * cellWidth, row * cellHeight, cellWidth, cellHeight));
                crop.Freeze();
                rowFrames[column] = crop;
            }
            elements[row] = rowFrames;
        }
        return new CelestialFrameSet(elements, framesPerElement);
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
                stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            return frame;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException)
        {
            return null;
        }
    }

    private static void ConfigureTransform(FrameworkElement element, params Transform[] transforms)
    {
        var group = new TransformGroup();
        foreach (var transform in transforms)
        {
            group.Children.Add(transform);
        }
        element.RenderTransform = group;
    }

    private void RenderAtElapsedSeconds(double elapsedSeconds)
    {
        if (!double.IsFinite(elapsedSeconds) || frames is null)
        {
            return;
        }

        lastElapsedSeconds = elapsedSeconds;
        currentCometPhase = PeriodPhase(elapsedSeconds, CometPeriod);
        currentPlanetPhase = PeriodPhase(elapsedSeconds, PlanetPeriod);
        currentGalaxyPhase = PeriodPhase(elapsedSeconds, GalaxyPeriod);
        currentGlintPhase = PeriodPhase(elapsedSeconds, GlintPeriod);

        UpdateSprite(comet, frames.Elements[0],
            PeriodPhase(elapsedSeconds, CometEvolutionPeriod), ref currentCometFrameIndex);
        UpdateSprite(planet, frames.Elements[1],
            PeriodPhase(elapsedSeconds, PlanetEvolutionPeriod), ref currentPlanetFrameIndex);
        UpdateSprite(galaxy, frames.Elements[2],
            PeriodPhase(elapsedSeconds, GalaxyEvolutionPeriod), ref currentGalaxyFrameIndex);
        UpdateSprite(glintA, frames.Elements[3],
            PeriodPhase(elapsedSeconds, GlintEvolutionPeriod), ref currentGlintFrameIndex);
        var ignored = 0;
        UpdateSprite(glintB, frames.Elements[3],
            PeriodPhase(elapsedSeconds + 0.83, GlintEvolutionPeriod), ref ignored);
        UpdateSprite(glintC, frames.Elements[3],
            PeriodPhase(elapsedSeconds + 1.51, GlintEvolutionPeriod), ref ignored);

        var cometVisibility = EdgeVisibility(currentCometPhase, 0.10);
        comet.Host.Opacity = 0.32 * cometVisibility;
        cometTranslate.X = 1224 - (282 * currentCometPhase);
        cometTranslate.Y = -72 + (276 * currentCometPhase) +
            (10 * Math.Sin(currentCometPhase * Math.PI * 2));
        cometRotate.Angle = -42 + (1.6 * Math.Sin(currentCometPhase * Math.PI * 2));

        var planetAngle = currentPlanetPhase * Math.PI * 2;
        planetTranslate.X = 1036 + (18 * Math.Cos(planetAngle));
        planetTranslate.Y = 70 + (14 * Math.Sin(planetAngle));

        var galaxyAngle = currentGalaxyPhase * Math.PI * 2;
        galaxyTranslate.X = -92 + (12 * Math.Cos(galaxyAngle));
        galaxyTranslate.Y = 540 + (8 * Math.Sin(galaxyAngle));
        galaxyRotate.Angle = -7 + (1.2 * Math.Sin(galaxyAngle));
    }

    private static void UpdateSprite(
        SpriteLayer layer,
        IReadOnlyList<BitmapSource> elementFrames,
        double phase,
        ref int currentIndex)
    {
        var progress = phase * elementFrames.Count;
        currentIndex = Math.Min(elementFrames.Count - 1, (int)Math.Floor(progress));
        var nextIndex = (currentIndex + 1) % elementFrames.Count;
        var local = progress - Math.Floor(progress);
        var blend = TransitionBlend(local);
        if (!ReferenceEquals(layer.Primary.Source, elementFrames[currentIndex]))
        {
            layer.Primary.Source = elementFrames[currentIndex];
        }
        if (!ReferenceEquals(layer.Secondary.Source, elementFrames[nextIndex]))
        {
            layer.Secondary.Source = elementFrames[nextIndex];
        }
        layer.Primary.Opacity = 1 - blend;
        layer.Secondary.Opacity = blend;
    }

    private static double TransitionBlend(double localFramePhase)
    {
        const double holdFraction = 0.24;
        var transition = Math.Clamp(
            (localFramePhase - holdFraction) / (1 - holdFraction), 0, 1);
        return transition * transition * (3 - (2 * transition));
    }

    private static double EdgeVisibility(double phase, double fadeFraction)
    {
        if (phase < fadeFraction)
        {
            return phase / fadeFraction;
        }
        if (phase > 1 - fadeFraction)
        {
            return (1 - phase) / fadeFraction;
        }
        return 1;
    }

    private static double PeriodPhase(double elapsedSeconds, TimeSpan period)
    {
        var remainder = elapsedSeconds % period.TotalSeconds;
        if (remainder < 0)
        {
            remainder += period.TotalSeconds;
        }
        return remainder / period.TotalSeconds;
    }

    private void RefreshAccessibilityState()
    {
        var show = !SystemParameters.HighContrast && !ReduceVisualNoise && HasCelestialAtlas;
        Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (ReducedMotion)
        {
            RenderAtElapsedSeconds(StaticSceneTimeSeconds);
        }
        RefreshMotionRegistration();
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
    }

    private static void OnMotionStateChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        var overlay = (OrbitNewTabCelestialOverlay)dependencyObject;
        if (args.Property == FreezeMotionProperty)
        {
            if ((bool)args.NewValue)
            {
                overlay.heldElapsedSeconds = overlay.lastElapsedSeconds;
            }
            else if ((bool)args.OldValue)
            {
                overlay.resumeNeedsRebase = true;
            }
        }
        overlay.RefreshAccessibilityState();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        hostLoaded = true;
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

    private sealed class SpriteLayer
    {
        public SpriteLayer(double width, double height, double opacity)
        {
            Primary = CreateImage();
            Secondary = CreateImage();
            Host = new Grid
            {
                Width = width,
                Height = height,
                Opacity = opacity,
                RenderTransformOrigin = new Point(0.5, 0.5),
                IsHitTestVisible = false,
                Focusable = false,
            };
            Host.Children.Add(Primary);
            Host.Children.Add(Secondary);
        }

        public Grid Host { get; }

        public Image Primary { get; }

        public Image Secondary { get; }

        private static Image CreateImage()
        {
            var image = new Image
            {
                Stretch = Stretch.Uniform,
                IsHitTestVisible = false,
                Focusable = false,
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            return image;
        }
    }

    private sealed record CelestialFrameSet(BitmapSource[][] Elements, int FramesPerElement);
}
#endif
