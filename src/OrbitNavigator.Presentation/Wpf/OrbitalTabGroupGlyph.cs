#if ORBIT_WPF
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace OrbitNavigator.Presentation.Wpf;

/// <summary>
/// Decorative group identity shown inside the standard accessible group
/// button. It never replaces the named button or participates in hit testing.
/// </summary>
public sealed class OrbitalTabGroupGlyph : FrameworkElement
{
    public static readonly DependencyProperty TabCountProperty = DependencyProperty.Register(
        nameof(TabCount),
        typeof(int),
        typeof(OrbitalTabGroupGlyph),
        new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly RotateTransform rotation = new();

    public OrbitalTabGroupGlyph()
    {
        Width = 27;
        Height = 27;
        IsHitTestVisible = false;
        Focusable = false;
        RenderTransform = rotation;
        RenderTransformOrigin = new Point(0.5, 0.5);
    }

    public int TabCount
    {
        get => (int)GetValue(TabCountProperty);
        set => SetValue(TabCountProperty, value);
    }

    public bool ReducedMotion { get; set; }

    public void Emphasize()
    {
        if (ReducedMotion || SystemParameters.HighContrast)
        {
            return;
        }

        rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        var animation = new DoubleAnimation(
            rotation.Angle,
            rotation.Angle + 72,
            TimeSpan.FromMilliseconds(440))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Timeline.SetDesiredFrameRate(animation, 30);
        rotation.BeginAnimation(RotateTransform.AngleProperty, animation);
    }

    protected override Size MeasureOverride(Size availableSize) => new(27, 27);

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var center = new Point(13.5, 13.5);
        var ringBrush = SystemParameters.HighContrast
            ? SystemColors.HighlightBrush
            : OrbitVisualTheme.SeaGlass;
        var nodeBrush = SystemParameters.HighContrast
            ? SystemColors.WindowTextBrush
            : OrbitVisualTheme.WaypointGold;
        var pen = new Pen(ringBrush, SystemParameters.HighContrast ? 1.8 : 1.1);
        drawingContext.DrawEllipse(null, pen, center, 9.2, 6.2);
        drawingContext.DrawEllipse(ringBrush, null, center, 2.1, 2.1);

        var visibleNodes = Math.Clamp(TabCount, 1, 4);
        for (var index = 0; index < visibleNodes; index++)
        {
            var angle = ((Math.PI * 2) / visibleNodes * index) - 0.55;
            var point = new Point(
                center.X + (Math.Cos(angle) * 9.2),
                center.Y + (Math.Sin(angle) * 6.2));
            drawingContext.DrawEllipse(nodeBrush, null, point, 1.65, 1.65);
        }
    }
}
#endif
