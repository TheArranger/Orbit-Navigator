#if ORBIT_WPF
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace OrbitNavigator.Presentation.Wpf;

/// <summary>A code-native speaker state glyph; the host button owns action semantics.</summary>
public sealed class TabAudioGlyph : FrameworkElement
{
    private static readonly Geometry Speaker = Frozen("M4,10 H8 L13,6 V18 L8,14 H4 Z");
    private static readonly Geometry Waves = Frozen("M15,9 C17,10.5 17,13.5 15,15 M17.5,6.5 C21,9.5 21,14.5 17.5,17.5");
    private static readonly Geometry Muted = Frozen("M15.5,9 L20.5,14 M20.5,9 L15.5,14");

    public static readonly DependencyProperty IsMutedProperty = DependencyProperty.Register(
        nameof(IsMuted),
        typeof(bool),
        typeof(TabAudioGlyph),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke),
        typeof(Brush),
        typeof(TabAudioGlyph),
        new FrameworkPropertyMetadata(OrbitVisualTheme.Ink, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool IsMuted
    {
        get => (bool)GetValue(IsMutedProperty);
        set => SetValue(IsMutedProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public void BindStrokeToAncestorForeground() => SetBinding(
        StrokeProperty,
        new Binding("Foreground")
        {
            RelativeSource = new RelativeSource(
                RelativeSourceMode.FindAncestor,
                typeof(System.Windows.Controls.Control),
                1),
        });

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? 20 : Math.Min(20, availableSize.Width),
        double.IsInfinity(availableSize.Height) ? 20 : Math.Min(20, availableSize.Height));

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var scale = Math.Min(ActualWidth, ActualHeight) / 24d;
        var pen = new Pen(SystemParameters.HighContrast ? SystemColors.ControlTextBrush : Stroke, 1.9 / scale)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        if (pen.CanFreeze)
        {
            pen.Freeze();
        }

        drawingContext.PushTransform(new TranslateTransform(
            (ActualWidth - (24 * scale)) / 2d,
            (ActualHeight - (24 * scale)) / 2d));
        drawingContext.PushTransform(new ScaleTransform(scale, scale));
        drawingContext.DrawGeometry(null, pen, Speaker);
        drawingContext.DrawGeometry(null, pen, IsMuted ? Muted : Waves);
        drawingContext.Pop();
        drawingContext.Pop();
    }

    private static Geometry Frozen(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }
}
#endif
