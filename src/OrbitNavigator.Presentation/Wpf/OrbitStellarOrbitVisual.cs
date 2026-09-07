#if ORBIT_WPF
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace OrbitNavigator.Presentation.Wpf;

/// <summary>
/// Reusable New Tab bookmark/workspace stellar centerpiece. The complete solar
/// atlas remains animated while rings and the orbiting favicon/workspace image
/// can be frozen independently for a hover/focus data card.
/// </summary>
public sealed class OrbitStellarOrbitVisual : Grid, IOrbitSharedVisualMotionTimelineTarget
{
    public const double DesignSize = 160;
    public const double RecommendedDisplaySize = 148;
    public const int SharedAnimationFramesPerSecond = OrbitSharedVisualMotionClock.FramesPerSecond;
    public const double StaticSceneTimeSeconds = 19.0;
    public static readonly TimeSpan OuterRingPeriod = TimeSpan.FromSeconds(11);
    public static readonly TimeSpan InnerRingPeriod = TimeSpan.FromSeconds(7);
    public static readonly TimeSpan SatelliteOrbitPeriod = TimeSpan.FromSeconds(9);

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind),
        typeof(OrbitEmberStarKind),
        typeof(OrbitStellarOrbitVisual),
        new FrameworkPropertyMetadata(OrbitEmberStarKind.Favorite, OnVisualStateChanged));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive),
        typeof(bool),
        typeof(OrbitStellarOrbitVisual),
        new FrameworkPropertyMetadata(true, OnVisualStateChanged));

    public static readonly DependencyProperty ReducedMotionProperty = DependencyProperty.Register(
        nameof(ReducedMotion),
        typeof(bool),
        typeof(OrbitStellarOrbitVisual),
        new FrameworkPropertyMetadata(false, OnVisualStateChanged));

    public static readonly DependencyProperty MotionEnabledProperty = DependencyProperty.Register(
        nameof(MotionEnabled),
        typeof(bool),
        typeof(OrbitStellarOrbitVisual),
        new FrameworkPropertyMetadata(true, OnVisualStateChanged));

    public static readonly DependencyProperty FreezeNonStellarMotionProperty = DependencyProperty.Register(
        nameof(FreezeNonStellarMotion),
        typeof(bool),
        typeof(OrbitStellarOrbitVisual),
        new FrameworkPropertyMetadata(false, OnVisualStateChanged));

    public static readonly DependencyProperty ReduceVisualNoiseProperty = DependencyProperty.Register(
        nameof(ReduceVisualNoise),
        typeof(bool),
        typeof(OrbitStellarOrbitVisual),
        new FrameworkPropertyMetadata(false, OnVisualStateChanged));

    public static readonly DependencyProperty OrbitingImageSourceProperty = DependencyProperty.Register(
        nameof(OrbitingImageSource),
        typeof(ImageSource),
        typeof(OrbitStellarOrbitVisual),
        new FrameworkPropertyMetadata(null, OnOrbitingImageChanged));

    public static readonly DependencyProperty OrbitingImageLabelProperty = DependencyProperty.Register(
        nameof(OrbitingImageLabel),
        typeof(string),
        typeof(OrbitStellarOrbitVisual),
        new FrameworkPropertyMetadata(string.Empty, OnVisualStateChanged));

    public static readonly DependencyProperty IsPrivateThemeProperty = DependencyProperty.Register(
        nameof(IsPrivateTheme),
        typeof(bool),
        typeof(OrbitStellarOrbitVisual),
        new FrameworkPropertyMetadata(false, OnVisualStateChanged));

    private static readonly Geometry SatelliteCueGeometry = CreateFrozenGeometry(
        "M8,14 L14,8 L20,14 L14,20 Z M10.5,14 L14,10.5 L17.5,14 L14,17.5 Z");

    private readonly OrbitEmberStar star = new()
    {
        // The v6 atlases intentionally retain a 16px+ transparent safety ring.
        // A 140-DIP canvas maps their visible solar bounds to roughly 90-108
        // physical pixels at the recommended host size instead of shrinking the
        // complete star to the old 70px decorative-orb scale.
        Width = 140,
        Height = 140,
        IsActive = true,
        ExposeAutomationPeer = false,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly Ellipse outerRing = new()
    {
        Width = 136,
        Height = 68,
        StrokeThickness = 1.6,
        StrokeDashArray = new DoubleCollection { 8, 5, 2, 5 },
        RenderTransformOrigin = new Point(0.5, 0.5),
        IsHitTestVisible = false,
    };
    private readonly Ellipse innerRing = new()
    {
        Width = 108,
        Height = 54,
        StrokeThickness = 1.2,
        StrokeDashArray = new DoubleCollection { 2, 7 },
        RenderTransformOrigin = new Point(0.5, 0.5),
        IsHitTestVisible = false,
    };
    private readonly Ellipse satellite = new()
    {
        Width = 24,
        Height = 24,
        StrokeThickness = 1.4,
        IsHitTestVisible = false,
    };
    private readonly System.Windows.Shapes.Path satelliteCue = new()
    {
        Data = SatelliteCueGeometry,
        StrokeThickness = 1.2,
        Fill = null,
        IsHitTestVisible = false,
    };
    private readonly ImageBrush satelliteImageBrush = new()
    {
        Stretch = Stretch.UniformToFill,
        AlignmentX = AlignmentX.Center,
        AlignmentY = AlignmentY.Center,
    };
    private readonly RotateTransform outerRingRotate = new(-16);
    private readonly RotateTransform innerRingRotate = new(24);
    private readonly TranslateTransform satelliteTranslate = new();
    private bool hostLoaded;
    private Window? hostWindow;
    private bool registeredWithSharedClock;
    private bool systemParameterEventsAttached;
    private long receivedMotionFrameCount;
    private double currentOuterRingPhase;
    private double currentInnerRingPhase;
    private double currentSatellitePhase;
    private double lastElapsedSeconds = StaticSceneTimeSeconds;
    private double timelineOffsetSeconds;
    private double heldElapsedSeconds = StaticSceneTimeSeconds;
    private bool resumeNeedsRebase;

    public OrbitStellarOrbitVisual()
    {
        Width = RecommendedDisplaySize;
        Height = RecommendedDisplaySize;
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = false;
        Background = Brushes.Transparent;

        outerRing.RenderTransform = outerRingRotate;
        innerRing.RenderTransform = innerRingRotate;
        var satelliteHost = new Grid
        {
            Width = 24,
            Height = 24,
            RenderTransform = satelliteTranslate,
            IsHitTestVisible = false,
        };
        satelliteHost.Children.Add(satellite);
        satelliteHost.Children.Add(satelliteCue);

        var canvas = new Canvas
        {
            Width = DesignSize,
            Height = DesignSize,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(outerRing, 12);
        Canvas.SetTop(outerRing, 46);
        Canvas.SetLeft(innerRing, 26);
        Canvas.SetTop(innerRing, 53);
        Canvas.SetLeft(star, 10);
        Canvas.SetTop(star, 10);
        canvas.Children.Add(outerRing);
        canvas.Children.Add(innerRing);
        canvas.Children.Add(star);
        canvas.Children.Add(satelliteHost);

        Children.Add(new Viewbox
        {
            Stretch = Stretch.Uniform,
            Child = canvas,
            IsHitTestVisible = false,
        });

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
        RefreshVisualState();
    }

    public OrbitEmberStarKind Kind
    {
        get => (OrbitEmberStarKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public bool ReducedMotion
    {
        get => (bool)GetValue(ReducedMotionProperty);
        set => SetValue(ReducedMotionProperty, value);
    }

    public bool MotionEnabled
    {
        get => (bool)GetValue(MotionEnabledProperty);
        set => SetValue(MotionEnabledProperty, value);
    }

    /// <summary>
    /// Hover/focus seam: freezes rings and orbiting media only. The central
    /// stellar plasma continues on the shared clock unless ReducedMotion is set.
    /// </summary>
    public bool FreezeNonStellarMotion
    {
        get => (bool)GetValue(FreezeNonStellarMotionProperty);
        set => SetValue(FreezeNonStellarMotionProperty, value);
    }

    public bool ReduceVisualNoise
    {
        get => (bool)GetValue(ReduceVisualNoiseProperty);
        set => SetValue(ReduceVisualNoiseProperty, value);
    }

    public ImageSource? OrbitingImageSource
    {
        get => (ImageSource?)GetValue(OrbitingImageSourceProperty);
        set => SetValue(OrbitingImageSourceProperty, value);
    }

    public string OrbitingImageLabel
    {
        get => (string)GetValue(OrbitingImageLabelProperty);
        set => SetValue(OrbitingImageLabelProperty, value ?? string.Empty);
    }

    public bool IsPrivateTheme
    {
        get => (bool)GetValue(IsPrivateThemeProperty);
        set => SetValue(IsPrivateThemeProperty, value);
    }

    public OrbitEmberStar CentralStar => star;

    public bool UsesTransparentBackground => true;

    public bool IsRegisteredWithSharedClock => registeredWithSharedClock;

    public bool IsNonStellarMotionActive =>
        registeredWithSharedClock && IsMotionEligible && !FreezeNonStellarMotion;

    public long ReceivedMotionFrameCount => receivedMotionFrameCount;

    public double CurrentOuterRingPhase => currentOuterRingPhase;

    public double CurrentInnerRingPhase => currentInnerRingPhase;

    public double CurrentSatellitePhase => currentSatellitePhase;

    public double LastElapsedSeconds => lastElapsedSeconds;

    public string AccessibleName => Kind == OrbitEmberStarKind.Favorite
        ? "Favorite star with orbiting site icon"
        : "Workspace star with orbiting selected image";

    public string AccessibleHelpText
    {
        get
        {
            var media = string.IsNullOrWhiteSpace(OrbitingImageLabel)
                ? "The orbiting image is decorative."
                : $"Orbiting image: {OrbitingImageLabel}.";
            return $"{media} Animated rings and orbit are decorative and freeze while details are shown; the filled stellar shape indicates active state.";
        }
    }

    public static int SharedClockSubscriberCount => OrbitSharedVisualMotionClock.SubscriberCount;

    public static int SharedClockRenderingHandlerCount => OrbitSharedVisualMotionClock.RenderingHandlerCount;

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new OrbitStellarOrbitVisualAutomationPeer(this);

    internal void ApplySharedMotionFrame(double normalizedPhase)
    {
        Dispatcher.VerifyAccess();
        if (!IsMotionEligible || !double.IsFinite(normalizedPhase))
        {
            return;
        }

        RenderAtElapsedSeconds(normalizedPhase * SatelliteOrbitPeriod.TotalSeconds);
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
        hostLoaded &&
        IsVisible &&
        IsHostWindowRenderable &&
        IsActive &&
        MotionEnabled &&
        !FreezeNonStellarMotion &&
        !ReducedMotion &&
        !ReduceVisualNoise &&
        !SystemParameters.HighContrast;

    private bool IsHostWindowRenderable =>
        hostWindow is null || (hostWindow.IsVisible && hostWindow.WindowState != WindowState.Minimized);

    private void RenderAtElapsedSeconds(double elapsedSeconds)
    {
        if (!double.IsFinite(elapsedSeconds))
        {
            return;
        }

        lastElapsedSeconds = elapsedSeconds;
        currentOuterRingPhase = PeriodPhase(elapsedSeconds, OuterRingPeriod);
        currentInnerRingPhase = PeriodPhase(elapsedSeconds, InnerRingPeriod);
        currentSatellitePhase = PeriodPhase(elapsedSeconds, SatelliteOrbitPeriod);

        // Ring planes stay fixed. Dash progression supplies orbital motion while
        // the satellite follows the same tilted ellipse instead of drifting on
        // an unrelated unrotated path.
        outerRingRotate.Angle = -16;
        innerRingRotate.Angle = 24;
        outerRing.StrokeDashOffset = -(40 * currentOuterRingPhase);
        innerRing.StrokeDashOffset = 18 * currentInnerRingPhase;

        var angle = currentSatellitePhase * Math.PI * 2;
        var ellipseX = 59 * Math.Cos(angle);
        var ellipseY = 27 * Math.Sin(angle);
        var tilt = -16 * Math.PI / 180;
        var rotatedX = (ellipseX * Math.Cos(tilt)) - (ellipseY * Math.Sin(tilt));
        var rotatedY = (ellipseX * Math.Sin(tilt)) + (ellipseY * Math.Cos(tilt));
        satelliteTranslate.X = 68 + rotatedX;
        satelliteTranslate.Y = 68 + rotatedY;
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

    private void RefreshVisualState()
    {
        star.Kind = Kind;
        star.IsActive = IsActive;
        star.MotionEnabled = MotionEnabled;
        star.ReducedMotion = ReducedMotion;
        if (IsPrivateTheme)
        {
            star.Accent = OrbitVisualTheme.PrivateViolet;
            star.Stroke = OrbitVisualTheme.SeaGlass;
        }
        else
        {
            star.ClearValue(OrbitEmberStar.AccentProperty);
            star.ClearValue(OrbitEmberStar.StrokeProperty);
        }
        star.UseRasterArtwork = !ReduceVisualNoise;
        RefreshBrushes();
        RefreshAutomationState();
        if (ReducedMotion || ReduceVisualNoise || SystemParameters.HighContrast)
        {
            RenderAtElapsedSeconds(StaticSceneTimeSeconds);
        }
        RefreshMotionRegistration();
    }

    private void RefreshBrushes()
    {
        var highContrast = SystemParameters.HighContrast;
        var accent = highContrast
            ? SystemColors.HighlightBrush
            : Kind == OrbitEmberStarKind.Favorite
                ? OrbitVisualTheme.WaypointGold
                : OrbitVisualTheme.SeaGlass;
        var stroke = highContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.Ink;
        outerRing.Stroke = highContrast
            ? SystemColors.WindowTextBrush
            : IsPrivateTheme ? OrbitVisualTheme.PrivateViolet : OrbitVisualTheme.SeaGlass;
        innerRing.Stroke = highContrast
            ? SystemColors.HighlightBrush
            : IsPrivateTheme ? OrbitVisualTheme.SeaGlass : OrbitVisualTheme.WaypointGold;
        outerRing.Opacity = ReduceVisualNoise ? 0.40 : 0.60;
        innerRing.Opacity = ReduceVisualNoise ? 0.30 : 0.44;
        satellite.Stroke = stroke;
        satelliteCue.Stroke = highContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.Ink;

        var showRasterMedia = !highContrast && !ReduceVisualNoise && OrbitingImageSource is not null;
        if (showRasterMedia)
        {
            satelliteImageBrush.ImageSource = OrbitingImageSource;
            satellite.Fill = satelliteImageBrush;
            satelliteCue.Visibility = Visibility.Collapsed;
        }
        else
        {
            satelliteImageBrush.ImageSource = null;
            satellite.Fill = accent;
            satelliteCue.Visibility = Visibility.Visible;
        }
    }

    private void RefreshAutomationState()
    {
        AutomationProperties.SetName(this, AccessibleName);
        AutomationProperties.SetItemStatus(this, IsActive ? "Active" : "Inactive");
        AutomationProperties.SetHelpText(this, AccessibleHelpText);
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

    private static void OnVisualStateChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        var visual = (OrbitStellarOrbitVisual)dependencyObject;
        if (args.Property == FreezeNonStellarMotionProperty)
        {
            if ((bool)args.NewValue)
            {
                visual.heldElapsedSeconds = visual.lastElapsedSeconds;
            }
            else if ((bool)args.OldValue)
            {
                visual.resumeNeedsRebase = true;
            }
        }
        visual.RefreshVisualState();
    }

    private static void OnOrbitingImageChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        var visual = (OrbitStellarOrbitVisual)dependencyObject;
        visual.RefreshBrushes();
        visual.RefreshAutomationState();
    }

    private static Geometry CreateFrozenGeometry(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
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
        RefreshVisualState();
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
            RefreshVisualState();
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

    private sealed class OrbitStellarOrbitVisualAutomationPeer(OrbitStellarOrbitVisual owner)
        : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(OrbitStellarOrbitVisual);

        protected override AutomationControlType GetAutomationControlTypeCore() =>
            AutomationControlType.Image;
    }
}
#endif
