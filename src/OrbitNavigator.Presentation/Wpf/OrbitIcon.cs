#if ORBIT_WPF
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace OrbitNavigator.Presentation.Wpf;

public enum OrbitIconKind
{
    Back = 0,
    Forward = 1,
    Reload = 2,
    Stop = 3,
    Home = 4,
    Add = 5,
    Private = 6,
    Menu = 7,
    Close = 8,
    ChevronDown = 9,
    ChevronRight = 10,
    Search = 11,
    Bookmark = 12,
    History = 13,
    Download = 14,
    Clipboard = 15,
    Settings = 16,
    Shield = 17,
    OrbitMark = 18,
}

/// <summary>Original, code-native 24px vector glyph family.</summary>
public sealed class OrbitIcon : FrameworkElement
{
    private static readonly IReadOnlyDictionary<OrbitIconKind, Geometry> Geometries =
        Enum.GetValues<OrbitIconKind>().ToDictionary(value => value, CreateGeometry);

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind),
        typeof(OrbitIconKind),
        typeof(OrbitIcon),
        new FrameworkPropertyMetadata(OrbitIconKind.OrbitMark, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke),
        typeof(Brush),
        typeof(OrbitIcon),
        new FrameworkPropertyMetadata(OrbitVisualTheme.Ink, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness),
        typeof(double),
        typeof(OrbitIcon),
        new FrameworkPropertyMetadata(1.9, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsFilledProperty = DependencyProperty.Register(
        nameof(IsFilled),
        typeof(bool),
        typeof(OrbitIcon),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public OrbitIconKind Kind
    {
        get => (OrbitIconKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    /// <summary>Fills closed status glyphs while preserving their familiar outline.</summary>
    public bool IsFilled
    {
        get => (bool)GetValue(IsFilledProperty);
        set => SetValue(IsFilledProperty, value);
    }

    public void BindStrokeToAncestorForeground()
    {
        SetBinding(
            StrokeProperty,
            new Binding("Foreground")
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(System.Windows.Controls.Control), 1),
            });
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(
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
        var offsetX = (ActualWidth - (24 * scale)) / 2d;
        var offsetY = (ActualHeight - (24 * scale)) / 2d;
        var pen = new Pen(Stroke, StrokeThickness / scale)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        if (pen.CanFreeze)
        {
            pen.Freeze();
        }

        drawingContext.PushTransform(new TranslateTransform(offsetX, offsetY));
        drawingContext.PushTransform(new ScaleTransform(scale, scale));
        var fill = IsFilled && Kind is OrbitIconKind.Bookmark or OrbitIconKind.Shield or OrbitIconKind.Stop
            ? Stroke
            : null;
        drawingContext.DrawGeometry(fill, pen, Geometries[Kind]);
        if (Kind == OrbitIconKind.OrbitMark)
        {
            drawingContext.DrawEllipse(
                SystemParameters.HighContrast ? SystemColors.HighlightBrush : OrbitVisualTheme.WaypointGold,
                null,
                new Point(4, 19),
                2.2,
                2.2);
        }

        drawingContext.Pop();
        drawingContext.Pop();
    }

    private static Geometry CreateGeometry(OrbitIconKind kind)
    {
        var data = kind switch
        {
            OrbitIconKind.Back => "M19,12 H6 M11,7 L6,12 11,17",
            OrbitIconKind.Forward => "M5,12 H18 M13,7 L18,12 13,17",
            OrbitIconKind.Reload => "M19,8 V4 H15 M18.5,5.5 C16.8,3.9 14.6,3 12.2,3 C7.2,3 3.2,7 3.2,12 C3.2,17 7.2,21 12.2,21 C16.2,21 19.6,18.4 20.7,14.8",
            OrbitIconKind.Stop => "M7,7 H17 V17 H7 Z",
            OrbitIconKind.Home => "M4,11 L12,4 20,11 M6.5,9.5 V20 H17.5 V9.5 M10,20 V14 H14 V20",
            OrbitIconKind.Add => "M12,5 V19 M5,12 H19",
            OrbitIconKind.Private => "M12,3 L20,6 V11.5 C20,16.6 16.8,20.2 12,22 C7.2,20.2 4,16.6 4,11.5 V6 Z M12,9 V15 M12,18 V18.1",
            OrbitIconKind.Menu => "M5,7 H19 M5,12 H19 M5,17 H19",
            OrbitIconKind.Close => "M6,6 L18,18 M18,6 L6,18",
            OrbitIconKind.ChevronDown => "M6,9 L12,15 18,9",
            OrbitIconKind.ChevronRight => "M9,6 L15,12 9,18",
            OrbitIconKind.Search => "M10.5,4 A6.5,6.5 0 1 0 10.5,17 A6.5,6.5 0 1 0 10.5,4 M15.2,15.2 L20,20",
            OrbitIconKind.Bookmark => "M7,4 H17 V21 L12,17.5 7,21 Z",
            OrbitIconKind.History => "M4,8 V3 M4,8 H9 M4.8,7 C6.7,3.8 10.5,2 14.2,3 C18.7,4.2 21.3,8.9 20.1,13.4 C18.9,17.9 14.2,20.5 9.7,19.3 C7.2,18.7 5.2,16.9 4.1,14.6 M12,7 V12 L15.5,14",
            OrbitIconKind.Download => "M12,3 V15 M7,10 L12,15 17,10 M5,20 H19",
            OrbitIconKind.Clipboard => "M8,5 H6 V21 H18 V5 H16 M9,3 H15 V7 H9 Z M9,11 H15 M9,15 H15",
            OrbitIconKind.Settings => "M12,8 A4,4 0 1 0 12,16 A4,4 0 1 0 12,8 M12,3 V5 M12,19 V21 M3,12 H5 M19,12 H21 M5.6,5.6 L7,7 M17,17 L18.4,18.4 M18.4,5.6 L17,7 M7,17 L5.6,18.4",
            OrbitIconKind.Shield => "M12,3 L20,6 V11.5 C20,16.6 16.8,20.2 12,22 C7.2,20.2 4,16.6 4,11.5 V6 Z M8.5,12 L11,14.5 16,9.5",
            OrbitIconKind.OrbitMark => "M4,19 C8,19 7,12 12,12 C16,12 16,7 19,5 M16,4 L21,4 19,9 M4,19 V19.1",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }
}
#endif
