#if ORBIT_WPF
using System.Windows;
using System.Windows.Media;

namespace OrbitNavigator.Presentation.Wpf;

/// <summary>Original code-native navigation path, waypoint, and direction mark.</summary>
public sealed class OrbitNavigatorMark : FrameworkElement
{
    private Brush primary = OrbitVisualTheme.SeaGlass;
    private Brush accent = OrbitVisualTheme.WaypointGold;

    public Brush Primary { get => primary; set { primary = value; InvalidateVisual(); } }
    public Brush Accent { get => accent; set { accent = value; InvalidateVisual(); } }

    protected override Size MeasureOverride(Size availableSize) => new(88, 88);

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var scale = size / 88d;
        context.PushTransform(new ScaleTransform(scale, scale));
        var primary = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Primary;
        var accent = SystemParameters.HighContrast ? SystemColors.HighlightBrush : Accent;
        OrbitNavigatorIdentity.Draw(context, primary, accent, background: null);
        context.Pop();
    }
}

/// <summary>
/// Vector master shared by in-app marks and the Window/taskbar icon. It is an
/// open navigation route, not a planet, person, or My Orbit ring.
/// </summary>
public static class OrbitNavigatorIdentity
{
    public const int MasterViewBoxSize = 88;
    public const string RoutePathData = "M17,67 C30,67 28,42 43,43 C55,44 57,27 68,22";
    public const string PointerPathData = "M60,14 L76,18 68,33 66,24 Z";
    public static IReadOnlyList<int> RequiredIconPixelSizes { get; } =
        Array.AsReadOnly(new[] { 16, 20, 24, 32, 40, 48, 64, 256 });

    public static ImageSource CreateWindowIcon(bool monochrome = false)
        => CreateWindowIcon(
            monochrome ? SystemColors.WindowTextBrush : OrbitVisualTheme.SeaGlass,
            monochrome ? SystemColors.HighlightBrush : OrbitVisualTheme.WaypointGold,
            monochrome ? SystemColors.WindowBrush : new SolidColorBrush(Color.FromRgb(11, 17, 23)));

    public static ImageSource CreateWindowIcon(Brush route, Brush waypoint, Brush? background)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(waypoint);
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            if (background is not null)
            {
                context.DrawRoundedRectangle(background, null, new Rect(4, 4, 80, 80), 18, 18);
            }
            Draw(context, route, waypoint, background);
        }
        drawing.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    internal static void Draw(DrawingContext context, Brush route, Brush waypoint, Brush? background)
    {
        var routeGeometry = Geometry.Parse(RoutePathData);
        routeGeometry.Freeze();
        var pen = new Pen(route, 4.6)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        context.DrawGeometry(null, pen, routeGeometry);

        var pointer = Geometry.Parse(PointerPathData);
        pointer.Freeze();
        context.DrawGeometry(route, null, pointer);
        context.DrawEllipse(waypoint, null, new Point(17, 67), 6.5, 6.5);
        if (background is not null)
        {
            context.DrawEllipse(background, null, new Point(17, 67), 2.1, 2.1);
        }
    }
}
#endif
