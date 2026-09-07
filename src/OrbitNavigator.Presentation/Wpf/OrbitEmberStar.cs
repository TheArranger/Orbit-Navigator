#if ORBIT_WPF
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace OrbitNavigator.Presentation.Wpf;

public enum OrbitEmberStarKind
{
    Favorite = 0,
    TabGroup = 1,
}

/// <summary>
/// Transparent favorite/tab-group status visual. Active state uses a coherent
/// whole-star plasma atlas; inactive, reduced-motion, and high-contrast states
/// retain static non-color shape cues.
/// </summary>
public sealed class OrbitEmberStar : FrameworkElement, IOrbitSharedVisualMotionTarget
{
    public const int MasterViewBoxSize = 28;
    public const int AtlasColumns = 4;
    public const int AtlasRows = 3;
    public const int AtlasFrameCount = AtlasColumns * AtlasRows;
    public const int LatestAtlasColumns = 4;
    public const int LatestAtlasRows = 4;
    public const int LatestAtlasFrameCount = LatestAtlasColumns * LatestAtlasRows;
    public const int PhaseStaggerSlotCount = 17;
    public const int SharedAnimationFramesPerSecond = OrbitSharedVisualMotionClock.FramesPerSecond;
    public const double MaximumAnimatedOffset = 0;
    public const double MaximumDetachedFragmentOffset = 0;
    // Compatibility value retained for callers that shipped with the v3 seam.
    public const int ArtworkVersion = 3;
    public const int LatestArtworkVersion = 6;
    public const double StellarCyclesPerSharedCycle = 2;
    public const string FavoriteAtlasRelativePath = "assets/new-tab/favorite-star-burn-atlas-v3.png";
    public const string TabGroupAtlasRelativePath = "assets/new-tab/tab-group-star-burn-atlas-v3.png";
    public const string FavoriteAtlasV4RelativePath = "assets/new-tab/favorite-star-flare-atlas-v4.png";
    public const string TabGroupAtlasV4RelativePath = "assets/new-tab/tab-group-star-flare-atlas-v4.png";
    public const string FavoriteAtlasV5RelativePath = "assets/new-tab/favorite-star-solar-atlas-v5.png";
    public const string TabGroupAtlasV5RelativePath = "assets/new-tab/workspace-star-solar-atlas-v5.png";
    public const string FavoriteAtlasV6RelativePath = "assets/new-tab/favorite-star-solar-atlas-v6.png";
    public const string TabGroupAtlasV6RelativePath = "assets/new-tab/workspace-star-solar-atlas-v6.png";
    public const string LegacyFavoriteAtlasRelativePath = "assets/new-tab/favorite-star-burn-atlas-v2.png";
    public const string LegacyTabGroupAtlasRelativePath = "assets/new-tab/tab-group-star-burn-atlas-v2.png";
    public static readonly TimeSpan SharedAnimationCycleDuration = OrbitSharedVisualMotionClock.CycleDuration;
    public static readonly TimeSpan EffectiveAnimationCycleDuration = TimeSpan.FromSeconds(
        OrbitSharedVisualMotionClock.CycleDuration.TotalSeconds / StellarCyclesPerSharedCycle);

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind),
        typeof(OrbitEmberStarKind),
        typeof(OrbitEmberStar),
        new FrameworkPropertyMetadata(
            OrbitEmberStarKind.Favorite,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnKindChanged));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive),
        typeof(bool),
        typeof(OrbitEmberStar),
        new FrameworkPropertyMetadata(
            false,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnMotionStateChanged));

    public static readonly DependencyProperty ReducedMotionProperty = DependencyProperty.Register(
        nameof(ReducedMotion),
        typeof(bool),
        typeof(OrbitEmberStar),
        new FrameworkPropertyMetadata(false, OnMotionStateChanged));

    public static readonly DependencyProperty MotionEnabledProperty = DependencyProperty.Register(
        nameof(MotionEnabled),
        typeof(bool),
        typeof(OrbitEmberStar),
            new FrameworkPropertyMetadata(true, OnMotionStateChanged));

    public static readonly DependencyProperty UseRasterArtworkProperty = DependencyProperty.Register(
        nameof(UseRasterArtwork),
        typeof(bool),
        typeof(OrbitEmberStar),
        new FrameworkPropertyMetadata(
            true,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnMotionStateChanged));

    public static readonly DependencyProperty MotionPhaseOffsetProperty = DependencyProperty.Register(
        nameof(MotionPhaseOffset),
        typeof(double),
        typeof(OrbitEmberStar),
        new FrameworkPropertyMetadata(0d, OnMotionStateChanged),
        IsValidMotionPhaseOffset);

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent),
        typeof(Brush),
        typeof(OrbitEmberStar),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnVisualBrushChanged));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke),
        typeof(Brush),
        typeof(OrbitEmberStar),
        new FrameworkPropertyMetadata(
            OrbitVisualTheme.Ink,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnVisualBrushChanged));

    public static readonly DependencyProperty ArtworkAtlasSourceProperty = DependencyProperty.Register(
        nameof(ArtworkAtlasSource),
        typeof(BitmapSource),
        typeof(OrbitEmberStar),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnArtworkAtlasSourceChanged));

    private const int StaticFrameIndex = 2;
    private const double StaticMotionPhase = StaticFrameIndex / (double)AtlasFrameCount;
    private static readonly Geometry FavoriteOrbitGeometry = CreateFrozenGeometry(
        "M7,17 C9,20 19,20 21,17");
    private static readonly Geometry GroupOrbitGeometry = CreateFrozenGeometry(
        "M7,11 C9,8 19,8 21,11 M7,17 C9,20 19,20 21,17");
    private static readonly Geometry StellarDetailGeometry = CreateFrozenGeometry(
        "M9.2,14.7 C11.5,10.6 15.9,10.2 18.4,11.5");
    private static readonly object DefaultAtlasGate = new();
    private static readonly Dictionary<OrbitEmberStarKind, AtlasFrameSet?> DefaultAtlases = [];
    private static readonly Dictionary<OrbitEmberStarKind, string?> DefaultAtlasPaths = [];
    private static int nextPhaseSlot = -1;

    private bool hostLoaded;
    private Window? hostWindow;
    private bool registeredWithSharedClock;
    private bool systemParameterEventsAttached;
    private double currentMotionPhase = StaticMotionPhase;
    private long receivedMotionFrameCount;
    private BitmapSource? cachedExplicitAtlas;
    private AtlasFrameSet? cachedExplicitFrames;
    private string? resolvedDefaultAtlasRelativePath;
    private readonly Dictionary<(Brush Brush, double Thickness), Pen> cachedPens = [];
    private readonly RectangleGeometry renderClip = new();
    private readonly TranslateTransform renderTranslate = new();
    private readonly ScaleTransform renderScale = new();

    /// <summary>
    /// True for standalone stars. Composite visual primitives set this false
    /// and expose one combined automation peer instead of duplicate images.
    /// Set before the control is loaded.
    /// </summary>
    public bool ExposeAutomationPeer { get; set; } = true;

    public OrbitEmberStar()
        : this(OrbitEmberStarKind.Favorite)
    {
    }

    public OrbitEmberStar(OrbitEmberStarKind kind)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Kind = kind;
        MotionPhaseOffset = NextMotionPhaseOffset(kind);
        Focusable = false;
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
        RefreshAutomationState();
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
    /// False selects the code-native stellar fallback. Reduced-noise hosts use
    /// this without pretending the star is inactive or changing its UIA state.
    /// </summary>
    public bool UseRasterArtwork
    {
        get => (bool)GetValue(UseRasterArtworkProperty);
        set => SetValue(UseRasterArtworkProperty, value);
    }

    /// <summary>
    /// Stable per-instance phase staggering on the one shared clock. This
    /// prevents repeated stars from looking mechanically synchronized without
    /// introducing a per-item timer or playback state.
    /// </summary>
    public double MotionPhaseOffset
    {
        get => (double)GetValue(MotionPhaseOffsetProperty);
        set => SetValue(MotionPhaseOffsetProperty, value);
    }

    /// <summary>
    /// Optional surface accent for the static fallback. Null selects waypoint
    /// gold for favorites and private violet for tab groups.
    /// </summary>
    public Brush? Accent
    {
        get => (Brush?)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    /// <summary>
    /// Optional 4-column whole-star atlas override. The compatibility atlas has
    /// three rows; v5 has four forward-only rows. When null, the control resolves
    /// the kind-specific relative path once from the application base folder.
    /// </summary>
    public BitmapSource? ArtworkAtlasSource
    {
        get => (BitmapSource?)GetValue(ArtworkAtlasSourceProperty);
        set => SetValue(ArtworkAtlasSourceProperty, value);
    }

    public string DefaultAtlasRelativePath => Kind == OrbitEmberStarKind.Favorite
        ? FavoriteAtlasRelativePath
        : TabGroupAtlasRelativePath;

    public string LatestAtlasRelativePath => Kind == OrbitEmberStarKind.Favorite
        ? FavoriteAtlasV6RelativePath
        : TabGroupAtlasV6RelativePath;

    public string V5AtlasRelativePath => Kind == OrbitEmberStarKind.Favorite
        ? FavoriteAtlasV5RelativePath
        : TabGroupAtlasV5RelativePath;

    public string PreviousAtlasRelativePath => Kind == OrbitEmberStarKind.Favorite
        ? FavoriteAtlasV4RelativePath
        : TabGroupAtlasV4RelativePath;

    public string LegacyAtlasRelativePath => Kind == OrbitEmberStarKind.Favorite
        ? LegacyFavoriteAtlasRelativePath
        : LegacyTabGroupAtlasRelativePath;

    public string? ResolvedDefaultAtlasRelativePath => resolvedDefaultAtlasRelativePath;

    public bool UsesV6Artwork => ArtworkAtlasSource is null && string.Equals(
            resolvedDefaultAtlasRelativePath,
            LatestAtlasRelativePath,
            StringComparison.OrdinalIgnoreCase);

    // Compatibility alias: v6 is a strict superseding transparent solar atlas.
    public bool UsesV5Artwork => UsesV6Artwork || (ArtworkAtlasSource is null && string.Equals(
            resolvedDefaultAtlasRelativePath,
            V5AtlasRelativePath,
            StringComparison.OrdinalIgnoreCase));

    public bool UsesV4Artwork => ArtworkAtlasSource is null && string.Equals(
            resolvedDefaultAtlasRelativePath,
            PreviousAtlasRelativePath,
            StringComparison.OrdinalIgnoreCase);

    // Compatibility alias: v4 is a strict superseding transparent atlas and is
    // accepted anywhere callers historically asked whether packaged v3 won.
    public bool UsesV3Artwork => UsesV5Artwork || UsesV4Artwork ||
        (ArtworkAtlasSource is null && string.Equals(
            resolvedDefaultAtlasRelativePath,
            DefaultAtlasRelativePath,
            StringComparison.OrdinalIgnoreCase));

    public string AccessibleName => Kind switch
    {
        OrbitEmberStarKind.Favorite => IsActive ? "Favorite star, active" : "Favorite star, inactive",
        OrbitEmberStarKind.TabGroup => IsActive ? "Tab group star, active" : "Tab group star, inactive",
        _ => throw new ArgumentOutOfRangeException(nameof(Kind)),
    };

    public string AccessibleHelpText =>
        IsActive
            ? "A filled, textured stellar disc with an attached corona indicates active. Whole-star plasma motion is decorative and may be disabled."
            : "A hollow stellar disc without plasma motion indicates inactive.";

    public string ShapeStateCue => IsActive
        ? "Filled stellar disc with attached corona"
        : "Hollow stellar disc without corona";

    public bool UsesTransparentBackground => true;

    public bool HasArtworkAtlas => ResolveFrameSet() is not null;

    public bool IsRegisteredWithSharedClock => registeredWithSharedClock;

    public double CurrentMotionPhase => currentMotionPhase;

    /// <summary>Compatibility surface. Whole-star v2 motion never offsets a fragment.</summary>
    public double CurrentEmberOffset => 0;

    public int ResolvedFrameCount => ResolveFrameSet()?.Frames.Count ?? AtlasFrameCount;

    public int CurrentFrameIndex => IsMotionEligible
        ? Math.Min(ResolvedFrameCount - 1, (int)Math.Floor(currentMotionPhase * ResolvedFrameCount))
        : StaticFrameIndex;

    public double CurrentFrameBlend => IsMotionEligible
        ? TransitionBlend(
            (currentMotionPhase * ResolvedFrameCount) - Math.Floor(currentMotionPhase * ResolvedFrameCount))
        : 0;

    public long ReceivedMotionFrameCount => receivedMotionFrameCount;

    public static int SharedClockSubscriberCount => OrbitSharedVisualMotionClock.SubscriberCount;

    public static int SharedClockRenderingHandlerCount => OrbitSharedVisualMotionClock.RenderingHandlerCount;

    public static long SharedClockDeliveredFrameCount => OrbitSharedVisualMotionClock.DeliveredFrameCount;

    protected override AutomationPeer OnCreateAutomationPeer() =>
        ExposeAutomationPeer ? new OrbitEmberStarAutomationPeer(this) : null!;

    protected override Size MeasureOverride(Size availableSize)
    {
        const double desired = 28;
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
        var stroke = highContrast ? SystemColors.WindowTextBrush : Stroke;
        var accent = highContrast
            ? SystemColors.HighlightBrush
            : Accent ?? (Kind == OrbitEmberStarKind.Favorite
                ? OrbitVisualTheme.WaypointGold
                : OrbitVisualTheme.PrivateViolet);
        var scale = size / MasterViewBoxSize;
        var offsetX = (ActualWidth - size) / 2;
        var offsetY = (ActualHeight - size) / 2;
        renderClip.Rect = new Rect(offsetX, offsetY, size, size);
        renderTranslate.X = offsetX;
        renderTranslate.Y = offsetY;
        renderScale.ScaleX = scale;
        renderScale.ScaleY = scale;
        drawingContext.PushClip(renderClip);
        drawingContext.PushTransform(renderTranslate);
        drawingContext.PushTransform(renderScale);

        if (IsActive)
        {
            var frames = highContrast || !UseRasterArtwork ? null : ResolveFrameSet();
            if (frames is not null)
            {
                DrawWholeStarFrames(drawingContext, frames);
            }
            else
            {
                DrawStaticStellarDisc(drawingContext, accent, stroke, highContrast, filled: true);
            }
        }
        else
        {
            DrawStaticStellarDisc(
                drawingContext,
                accent,
                highContrast ? stroke : accent,
                highContrast,
                filled: false);
        }

        // Normal artwork already provides a complete, coherent stellar body.
        // The legacy orbit marks read like a smile/belt at hub scale, so retain
        // them only for the raster-free high-contrast fallback.
        if (highContrast)
        {
            DrawKindCue(drawingContext, stroke, highContrast);
        }

        drawingContext.Pop();
        drawingContext.Pop();
        drawingContext.Pop();
    }

    internal void ApplySharedMotionFrame(double normalizedPhase)
    {
        Dispatcher.VerifyAccess();
        if (!IsMotionEligible || !double.IsFinite(normalizedPhase))
        {
            return;
        }

        var staggeredPhase = (normalizedPhase * StellarCyclesPerSharedCycle) + MotionPhaseOffset;
        currentMotionPhase = staggeredPhase - Math.Floor(staggeredPhase);
        receivedMotionFrameCount++;
        InvalidateVisual();
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
        UseRasterArtwork &&
        !ReducedMotion &&
        !SystemParameters.HighContrast;

    private bool IsHostWindowRenderable =>
        hostWindow is null || (hostWindow.IsVisible && hostWindow.WindowState != WindowState.Minimized);

    private void DrawWholeStarFrames(DrawingContext drawingContext, AtlasFrameSet frames)
    {
        var currentIndex = CurrentFrameIndex;
        var blend = CurrentFrameBlend;
        var nextIndex = (currentIndex + 1) % frames.Frames.Count;
        var destination = new Rect(0, 0, MasterViewBoxSize, MasterViewBoxSize);

        if (blend < 0.01)
        {
            drawingContext.DrawImage(frames.Frames[currentIndex], destination);
            return;
        }

        drawingContext.PushOpacity(1 - blend);
        drawingContext.DrawImage(frames.Frames[currentIndex], destination);
        drawingContext.Pop();
        drawingContext.PushOpacity(blend);
        drawingContext.DrawImage(frames.Frames[nextIndex], destination);
        drawingContext.Pop();
    }

    private void DrawStaticStellarDisc(
        DrawingContext drawingContext,
        Brush accent,
        Brush stroke,
        bool highContrast,
        bool filled)
    {
        var outline = GetPen(stroke, highContrast ? 2.6 : 1.55);
        drawingContext.DrawEllipse(
            filled ? accent : null,
            outline,
            new Point(14, 14),
            filled ? 9.8 : 8.8,
            filled ? 9.8 : 8.8);
        if (!filled)
        {
            drawingContext.DrawEllipse(null, GetPen(stroke, highContrast ? 1.8 : 1.05), new Point(14, 14), 4.7, 4.7);
            return;
        }

        var detailBrush = highContrast ? SystemColors.WindowBrush : stroke;
        drawingContext.DrawGeometry(
            null,
            GetPen(detailBrush, highContrast ? 1.7 : 0.9),
            StellarDetailGeometry);
    }

    private void DrawKindCue(DrawingContext drawingContext, Brush stroke, bool highContrast)
    {
        if (!IsActive)
        {
            return;
        }

        var supportPen = GetPen(stroke, highContrast ? 2.1 : 1.05);
        if (Kind == OrbitEmberStarKind.Favorite)
        {
            drawingContext.DrawGeometry(null, supportPen, FavoriteOrbitGeometry);
            return;
        }

        drawingContext.DrawGeometry(null, supportPen, GroupOrbitGeometry);
    }

    private AtlasFrameSet? ResolveFrameSet()
    {
        var explicitAtlas = ArtworkAtlasSource;
        if (explicitAtlas is not null)
        {
            if (!ReferenceEquals(explicitAtlas, cachedExplicitAtlas))
            {
                cachedExplicitAtlas = explicitAtlas;
                cachedExplicitFrames = CreateFrameSet(explicitAtlas);
            }
            return cachedExplicitFrames;
        }

        lock (DefaultAtlasGate)
        {
            if (DefaultAtlases.TryGetValue(Kind, out var cached))
            {
                _ = DefaultAtlasPaths.TryGetValue(Kind, out resolvedDefaultAtlasRelativePath);
                return cached;
            }

            var bitmap = LoadDefaultAtlas(out var relativePath);
            var frames = bitmap is null ? null : CreateFrameSet(bitmap);
            resolvedDefaultAtlasRelativePath = relativePath;
            DefaultAtlases.Add(Kind, frames);
            DefaultAtlasPaths.Add(Kind, relativePath);
            return frames;
        }
    }

    private BitmapSource? LoadDefaultAtlas(out string? relativePath)
    {
        foreach (var candidate in new[]
        {
            LatestAtlasRelativePath,
            V5AtlasRelativePath,
            PreviousAtlasRelativePath,
            DefaultAtlasRelativePath,
            LegacyAtlasRelativePath,
        })
        {
            var path = Path.Combine(
                AppContext.BaseDirectory,
                candidate.Replace('/', Path.DirectorySeparatorChar));
            var bitmap = LoadFrozenBitmap(path);
            if (bitmap is null)
            {
                continue;
            }

            relativePath = candidate;
            return bitmap;
        }

        relativePath = null;
        return null;
    }

    private static double TransitionBlend(double localFramePhase)
    {
        const double holdFraction = 0.28;
        var transition = Math.Clamp(
            (localFramePhase - holdFraction) / (1 - holdFraction),
            0,
            1);
        return transition * transition * (3 - (2 * transition));
    }

    private static AtlasFrameSet? CreateFrameSet(BitmapSource source)
    {
        if (source.PixelWidth <= 0 ||
            source.PixelHeight <= 0 ||
            source.PixelWidth % AtlasColumns != 0)
        {
            return null;
        }

        var frameWidth = source.PixelWidth / AtlasColumns;
        if (source.PixelHeight % frameWidth != 0)
        {
            return null;
        }

        var rows = source.PixelHeight / frameWidth;
        if (rows is not AtlasRows and not LatestAtlasRows)
        {
            return null;
        }

        var frames = new BitmapSource[AtlasColumns * rows];
        for (var index = 0; index < frames.Length; index++)
        {
            var x = (index % AtlasColumns) * frameWidth;
            var y = (index / AtlasColumns) * frameWidth;
            var frame = new CroppedBitmap(source, new Int32Rect(x, y, frameWidth, frameWidth));
            if (frame.CanFreeze)
            {
                frame.Freeze();
            }
            frames[index] = frame;
        }
        return new AtlasFrameSet(source, frames);
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
        currentMotionPhase = StaticMotionPhase;
        InvalidateVisual();
    }

    private void RefreshAutomationState()
    {
        AutomationProperties.SetName(this, AccessibleName);
        AutomationProperties.SetItemStatus(this, IsActive ? "Active" : "Inactive");
        AutomationProperties.SetHelpText(this, AccessibleHelpText);
    }

    private static void OnKindChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (!Enum.IsDefined((OrbitEmberStarKind)args.NewValue))
        {
            throw new ArgumentOutOfRangeException(nameof(args));
        }

        var star = (OrbitEmberStar)dependencyObject;
        star.cachedExplicitAtlas = null;
        star.cachedExplicitFrames = null;
        star.resolvedDefaultAtlasRelativePath = null;
        star.RefreshAutomationState();
        star.InvalidateVisual();
    }

    private static void OnMotionStateChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var star = (OrbitEmberStar)dependencyObject;
        star.RefreshAutomationState();
        star.RefreshMotionRegistration();
        star.InvalidateVisual();
    }

    private static bool IsValidMotionPhaseOffset(object value) =>
        value is double phase && double.IsFinite(phase) && phase is >= 0 and < 1;

    private static double NextMotionPhaseOffset(OrbitEmberStarKind kind)
    {
        var slot = Math.Abs(Interlocked.Increment(ref nextPhaseSlot) % PhaseStaggerSlotCount);
        var kindOffset = kind == OrbitEmberStarKind.TabGroup ? PhaseStaggerSlotCount / 2 : 0;
        return ((slot + kindOffset) % PhaseStaggerSlotCount) / (double)PhaseStaggerSlotCount;
    }

    private static void OnArtworkAtlasSourceChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var star = (OrbitEmberStar)dependencyObject;
        star.cachedExplicitAtlas = null;
        star.cachedExplicitFrames = null;
        star.InvalidateVisual();
    }

    private static Geometry CreateFrozenGeometry(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }

    private Pen GetPen(Brush brush, double thickness)
    {
        if (cachedPens.TryGetValue((brush, thickness), out var cached))
        {
            return cached;
        }

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
        cachedPens[(brush, thickness)] = pen;
        return pen;
    }

    private static void OnVisualBrushChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var star = (OrbitEmberStar)dependencyObject;
        star.cachedPens.Clear();
        star.InvalidateVisual();
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
        RefreshMotionRegistration();
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
            cachedPens.Clear();
            RefreshMotionRegistration();
            InvalidateVisual();
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

    private sealed class OrbitEmberStarAutomationPeer(OrbitEmberStar owner)
        : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(OrbitEmberStar);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
    }

    private sealed record AtlasFrameSet(BitmapSource Source, IReadOnlyList<BitmapSource> Frames);
}
#endif
