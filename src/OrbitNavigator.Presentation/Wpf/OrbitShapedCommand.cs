#if ORBIT_WPF
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace OrbitNavigator.Presentation.Wpf;

public enum OrbitCommandShape
{
    Keel = 0,
    Port = 1,
    Lens = 2,
    Fin = 3,
}

public enum OrbitCommandRole
{
    Neutral = 0,
    Destructive = 1,
}

public enum OrbitShapedCommandState
{
    Normal = 0,
    Hover = 1,
    Pressed = 2,
    Focused = 3,
    Disabled = 4,
    Selected = 5,
}

/// <summary>
/// Accessible rectangular command target with an original vector silhouette.
/// Hit testing, UI Automation, and keyboard focus retain the full 44px target;
/// only the painted chrome takes on the selected orbital shape.
/// </summary>
public sealed class OrbitShapedCommand : Button
{
    public const double MinimumHitTarget = 44;
    public const double FocusRingThickness = 3;
    public static readonly TimeSpan HoverTransitionDuration = TimeSpan.FromMilliseconds(120);
    public static readonly TimeSpan PressTransitionDuration = TimeSpan.FromMilliseconds(60);

    private static readonly Brush SelectedSurface = FrozenBrush(Color.FromArgb(102, 39, 135, 121));
    private static readonly IReadOnlyDictionary<OrbitCommandShape, Geometry> NormalizedShapes =
        Enum.GetValues<OrbitCommandShape>().ToDictionary(value => value, CreateShape);

    public static readonly DependencyProperty ShapeProperty = DependencyProperty.Register(
        nameof(Shape),
        typeof(OrbitCommandShape),
        typeof(OrbitShapedCommand),
        new FrameworkPropertyMetadata(
            OrbitCommandShape.Fin,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnAppearancePropertyChanged));

    public static readonly DependencyProperty RoleProperty = DependencyProperty.Register(
        nameof(Role),
        typeof(OrbitCommandRole),
        typeof(OrbitShapedCommand),
        new FrameworkPropertyMetadata(
            OrbitCommandRole.Neutral,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnAppearancePropertyChanged));

    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
        nameof(IsSelected),
        typeof(bool),
        typeof(OrbitShapedCommand),
        new FrameworkPropertyMetadata(
            false,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnAppearancePropertyChanged));

    public static readonly DependencyProperty ReducedMotionProperty = DependencyProperty.Register(
        nameof(ReducedMotion),
        typeof(bool),
        typeof(OrbitShapedCommand),
        new FrameworkPropertyMetadata(false, OnReducedMotionChanged));

    private static readonly DependencyProperty VisualOffsetYProperty = DependencyProperty.Register(
        "VisualOffsetY",
        typeof(double),
        typeof(OrbitShapedCommand),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    private Geometry? bodyGeometry;
    private Geometry? focusGeometry;
    private Size cachedGeometrySize;
    private OrbitCommandShape cachedShape;
    private bool systemParameterEventsAttached;

    public OrbitShapedCommand()
    {
        MinWidth = MinimumHitTarget;
        MinHeight = MinimumHitTarget;
        Padding = new Thickness(10, 7, 10, 7);
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 13;
        FocusVisualStyle = null;
        SnapsToDevicePixels = true;
        Template = CreateContentOnlyTemplate();
        Foreground = OrbitVisualTheme.Ink;
        IsEnabledChanged += OnIsEnabledChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public OrbitCommandShape Shape
    {
        get => (OrbitCommandShape)GetValue(ShapeProperty);
        set => SetValue(ShapeProperty, value);
    }

    public OrbitCommandRole Role
    {
        get => (OrbitCommandRole)GetValue(RoleProperty);
        set => SetValue(RoleProperty, value);
    }

    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    public bool ReducedMotion
    {
        get => (bool)GetValue(ReducedMotionProperty);
        set => SetValue(ReducedMotionProperty, value);
    }

    public OrbitShapedCommandState VisualState => ResolveVisualState();

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        EnsureGeometryCache();
        var state = ResolveVisualState();
        var highContrast = SystemParameters.HighContrast;
        var fill = ResolveFill(state, highContrast);
        var border = ResolveBorder(state, highContrast);
        var borderThickness = highContrast ? 2d : state == OrbitShapedCommandState.Selected ? 2d : 1d;
        var borderPen = CreatePen(border, borderThickness);
        var offset = (double)GetValue(VisualOffsetYProperty);

        drawingContext.PushTransform(new TranslateTransform(0, offset));
        drawingContext.DrawGeometry(fill, borderPen, bodyGeometry);
        if (IsSelected && IsEnabled)
        {
            DrawSelectionRoute(drawingContext, highContrast);
        }
        drawingContext.Pop();

        if (IsKeyboardFocused)
        {
            var focus = highContrast ? SystemColors.HighlightBrush : OrbitVisualTheme.Focus;
            drawingContext.DrawGeometry(null, CreatePen(focus, FocusRingThickness), focusGeometry);
        }
    }

    protected override void OnMouseEnter(MouseEventArgs args)
    {
        base.OnMouseEnter(args);
        RefreshAppearance();
        AnimateChrome(-1, HoverTransitionDuration);
    }

    protected override void OnMouseLeave(MouseEventArgs args)
    {
        base.OnMouseLeave(args);
        RefreshAppearance();
        AnimateChrome(0, HoverTransitionDuration);
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs args)
    {
        base.OnPreviewMouseLeftButtonDown(args);
        RefreshAppearance();
        AnimateChrome(1, PressTransitionDuration);
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs args)
    {
        base.OnPreviewMouseLeftButtonUp(args);
        RefreshAppearance();
        AnimateChrome(IsMouseOver ? -1 : 0, PressTransitionDuration);
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs args)
    {
        base.OnGotKeyboardFocus(args);
        RefreshAppearance();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs args)
    {
        base.OnLostKeyboardFocus(args);
        RefreshAppearance();
    }

    private void OnIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        SetCurrentValue(OpacityProperty, IsEnabled ? 1d : SystemParameters.HighContrast ? 0.75d : 0.42d);
        RefreshAppearance();
        if (!IsEnabled)
        {
            AnimateChrome(0, TimeSpan.Zero);
        }
    }

    private static void OnAppearancePropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        var command = (OrbitShapedCommand)dependencyObject;
        command.bodyGeometry = null;
        command.focusGeometry = null;
        command.RefreshAppearance();
    }

    private static void OnReducedMotionChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        var command = (OrbitShapedCommand)dependencyObject;
        if ((bool)args.NewValue)
        {
            command.AnimateChrome(0, TimeSpan.Zero);
        }
    }

    private void RefreshAppearance()
    {
        var state = ResolveVisualState();
        var foreground = SystemParameters.HighContrast
            ? state is OrbitShapedCommandState.Hover or OrbitShapedCommandState.Pressed or OrbitShapedCommandState.Selected
                ? SystemColors.HighlightTextBrush
                : SystemColors.ControlTextBrush
            : OrbitVisualTheme.Ink;
        SetCurrentValue(ForegroundProperty, foreground);
        InvalidateVisual();
    }

    private OrbitShapedCommandState ResolveVisualState()
    {
        if (!IsEnabled)
        {
            return OrbitShapedCommandState.Disabled;
        }
        if (IsPressed)
        {
            return OrbitShapedCommandState.Pressed;
        }
        if (IsKeyboardFocused)
        {
            return OrbitShapedCommandState.Focused;
        }
        if (IsSelected)
        {
            return OrbitShapedCommandState.Selected;
        }
        return IsMouseOver ? OrbitShapedCommandState.Hover : OrbitShapedCommandState.Normal;
    }

    private Brush ResolveFill(OrbitShapedCommandState state, bool highContrast)
    {
        if (highContrast)
        {
            return state is OrbitShapedCommandState.Hover or OrbitShapedCommandState.Pressed or OrbitShapedCommandState.Selected
                ? SystemColors.HighlightBrush
                : SystemColors.ControlBrush;
        }

        return state switch
        {
            OrbitShapedCommandState.Hover or OrbitShapedCommandState.Focused => OrbitVisualTheme.SurfaceHover,
            OrbitShapedCommandState.Pressed => OrbitVisualTheme.SurfacePressed,
            OrbitShapedCommandState.Selected => SelectedSurface,
            _ => OrbitVisualTheme.Surface,
        };
    }

    private Brush ResolveBorder(OrbitShapedCommandState state, bool highContrast)
    {
        if (highContrast)
        {
            return state == OrbitShapedCommandState.Focused
                ? SystemColors.HighlightBrush
                : SystemColors.ControlTextBrush;
        }

        if (Role == OrbitCommandRole.Destructive && state is OrbitShapedCommandState.Hover or OrbitShapedCommandState.Focused)
        {
            return OrbitVisualTheme.Danger;
        }

        return state switch
        {
            OrbitShapedCommandState.Hover => OrbitVisualTheme.SeaGlassStrong,
            OrbitShapedCommandState.Focused => OrbitVisualTheme.Focus,
            OrbitShapedCommandState.Selected => OrbitVisualTheme.SeaGlass,
            _ => OrbitVisualTheme.Divider,
        };
    }

    private void DrawSelectionRoute(DrawingContext drawingContext, bool highContrast)
    {
        var brush = highContrast ? SystemColors.HighlightTextBrush : OrbitVisualTheme.SeaGlass;
        var y = Math.Max(8, ActualHeight - 8);
        var start = new Point(12, y);
        var end = new Point(Math.Max(12, ActualWidth - 12), y);
        drawingContext.DrawLine(CreatePen(brush, 2), start, end);
        drawingContext.DrawEllipse(brush, null, new Point(ActualWidth / 2, y), 2.3, 2.3);
    }

    private void AnimateChrome(double target, TimeSpan duration)
    {
        BeginAnimation(VisualOffsetYProperty, null);
        if (ReducedMotion || SystemParameters.HighContrast || duration == TimeSpan.Zero)
        {
            SetValue(VisualOffsetYProperty, ReducedMotion || SystemParameters.HighContrast ? 0d : target);
            return;
        }

        BeginAnimation(
            VisualOffsetYProperty,
            new DoubleAnimation(target, duration)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            },
            HandoffBehavior.SnapshotAndReplace);
    }

    private void EnsureGeometryCache()
    {
        var size = new Size(ActualWidth, ActualHeight);
        if (bodyGeometry is not null && focusGeometry is not null && cachedGeometrySize == size && cachedShape == Shape)
        {
            return;
        }

        cachedGeometrySize = size;
        cachedShape = Shape;
        var normalized = NormalizedShapes[Shape];
        bodyGeometry = TransformGeometry(normalized, new Rect(4, 4, Math.Max(1, ActualWidth - 8), Math.Max(1, ActualHeight - 8)));
        focusGeometry = TransformGeometry(normalized, new Rect(1.5, 1.5, Math.Max(1, ActualWidth - 3), Math.Max(1, ActualHeight - 3)));
    }

    private static Geometry TransformGeometry(Geometry normalized, Rect target)
    {
        var geometry = normalized.Clone();
        geometry.Transform = new MatrixTransform(
            target.Width / MinimumHitTarget,
            0,
            0,
            target.Height / MinimumHitTarget,
            target.X,
            target.Y);
        geometry.Freeze();
        return geometry;
    }

    private static Geometry CreateShape(OrbitCommandShape shape)
    {
        var path = shape switch
        {
            OrbitCommandShape.Keel => "M10,0 H30 L44,22 30,44 H10 L0,22 Z",
            OrbitCommandShape.Port => "M22,0 C35,0 44,8 44,20 C44,34 32,44 18,44 C7,44 0,34 0,20 C0,8 10,0 22,0 Z",
            OrbitCommandShape.Lens => "M20,0 H36 Q44,0 44,8 V24 Q44,44 24,44 H8 Q0,44 0,36 V20 Q0,0 20,0 Z",
            OrbitCommandShape.Fin => "M7,0 H25 Q44,0 44,19 V37 Q44,44 37,44 H19 Q0,44 0,25 V7 Q0,0 7,0 Z",
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        var geometry = Geometry.Parse(path);
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

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

#pragma warning disable CS0618
    private static ControlTemplate CreateContentOnlyTemplate()
    {
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentProperty));
        presenter.SetValue(ContentPresenter.ContentTemplateProperty, new TemplateBindingExtension(ContentTemplateProperty));
        presenter.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(PaddingProperty));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        return new ControlTemplate(typeof(OrbitShapedCommand)) { VisualTree = presenter };
    }
#pragma warning restore CS0618

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (!systemParameterEventsAttached)
        {
            SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
            systemParameterEventsAttached = true;
        }
        RefreshAppearance();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        BeginAnimation(VisualOffsetYProperty, null);
        SetValue(VisualOffsetYProperty, 0d);
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
            AnimateChrome(0, TimeSpan.Zero);
            SetCurrentValue(OpacityProperty, IsEnabled ? 1d : SystemParameters.HighContrast ? 0.75d : 0.42d);
            RefreshAppearance();
        }
    }
}
#endif
