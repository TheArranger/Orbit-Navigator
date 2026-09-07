#if ORBIT_WPF
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace OrbitNavigator.Presentation.Wpf;

/// <summary>A decorative orbital activity mark with a static reduced-motion fallback.</summary>
public sealed class OrbitLoadingIndicator : FrameworkElement
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive),
        typeof(bool),
        typeof(OrbitLoadingIndicator),
        new FrameworkPropertyMetadata(true, OnAnimationPropertyChanged));

    public static readonly DependencyProperty ReducedMotionProperty = DependencyProperty.Register(
        nameof(ReducedMotion),
        typeof(bool),
        typeof(OrbitLoadingIndicator),
        new FrameworkPropertyMetadata(false, OnAnimationPropertyChanged));

    private readonly RotateTransform rotation = new();

    public OrbitLoadingIndicator()
    {
        Width = 34;
        Height = 34;
        Focusable = false;
        IsHitTestVisible = false;
        RenderTransform = rotation;
        RenderTransformOrigin = new Point(0.5, 0.5);
        Loaded += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => rotation.BeginAnimation(RotateTransform.AngleProperty, null);
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

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var size = Math.Min(ActualWidth, ActualHeight);
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = Math.Max(2, (size / 2) - 3);
        var track = SystemParameters.HighContrast ? SystemColors.GrayTextBrush : OrbitVisualTheme.Divider;
        var accent = SystemParameters.HighContrast ? SystemColors.HighlightBrush : OrbitVisualTheme.SeaGlass;
        var waypoint = SystemParameters.HighContrast ? SystemColors.HighlightBrush : OrbitVisualTheme.WaypointGold;
        drawingContext.DrawEllipse(null, new Pen(track, 1.5), center, radius, radius);

        var figure = new PathFigure
        {
            StartPoint = new Point(center.X, center.Y - radius),
            IsClosed = false,
        };
        figure.Segments.Add(new ArcSegment(
            new Point(center.X + radius, center.Y),
            new Size(radius, radius),
            0,
            false,
            SweepDirection.Clockwise,
            true));
        figure.Segments.Add(new ArcSegment(
            new Point(center.X, center.Y + radius),
            new Size(radius, radius),
            0,
            false,
            SweepDirection.Clockwise,
            true));
        var geometry = new PathGeometry([figure]);
        drawingContext.DrawGeometry(null, new Pen(accent, 2.4), geometry);
        drawingContext.DrawEllipse(waypoint, null, new Point(center.X + radius, center.Y), 2.7, 2.7);
    }

    private static void OnAnimationPropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args) =>
        ((OrbitLoadingIndicator)dependencyObject).UpdateAnimation();

    private void UpdateAnimation()
    {
        var shouldAnimate = IsLoaded && IsActive && !ReducedMotion && !SystemParameters.HighContrast;
        if (!shouldAnimate)
        {
            rotation.BeginAnimation(RotateTransform.AngleProperty, null);
            rotation.Angle = 0;
            return;
        }

        rotation.BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(2.4))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            });
    }
}
#endif
