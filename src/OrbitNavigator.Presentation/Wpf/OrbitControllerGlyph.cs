#if ORBIT_WPF
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace OrbitNavigator.Presentation.Wpf;

public enum OrbitControllerGlyphKind
{
    PreviousPage = 0,
    NextPage = 1,
    MoreTabs = 2,
    Detach = 3,
    Dock = 4,
    PlacementTop = 5,
    PlacementLeft = 6,
    PlacementRight = 7,
    Resources = 8,
    Sort = 9,
    Filter = 10,
    SwitchToTab = 11,
    CloseTab = 12,
    MoveTab = 13,
    GroupTabs = 14,
    BulkClose = 15,
}

/// <summary>
/// Original 24px vector glyph family for the detachable tab controller and
/// resource panel. Glyphs carry no command behavior and remain decorative to
/// the owning, explicitly named button.
/// </summary>
public sealed class OrbitControllerGlyph : FrameworkElement
{
    private static readonly IReadOnlyDictionary<OrbitControllerGlyphKind, GlyphDefinition> Definitions =
        Enum.GetValues<OrbitControllerGlyphKind>().ToDictionary(value => value, CreateDefinition);

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind),
        typeof(OrbitControllerGlyphKind),
        typeof(OrbitControllerGlyph),
        new FrameworkPropertyMetadata(
            OrbitControllerGlyphKind.MoreTabs,
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke),
        typeof(Brush),
        typeof(OrbitControllerGlyph),
        new FrameworkPropertyMetadata(
            OrbitVisualTheme.Ink,
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent),
        typeof(Brush),
        typeof(OrbitControllerGlyph),
        new FrameworkPropertyMetadata(
            OrbitVisualTheme.WaypointGold,
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness),
        typeof(double),
        typeof(OrbitControllerGlyph),
        new FrameworkPropertyMetadata(
            1.9d,
            FrameworkPropertyMetadataOptions.AffectsRender));

    public OrbitControllerGlyph()
    {
        Focusable = false;
        IsHitTestVisible = false;
    }

    public OrbitControllerGlyphKind Kind
    {
        get => (OrbitControllerGlyphKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public Brush Accent
    {
        get => (Brush)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public void BindStrokeToAncestorForeground()
    {
        SetBinding(
            StrokeProperty,
            new Binding("Foreground")
            {
                RelativeSource = new RelativeSource(
                    RelativeSourceMode.FindAncestor,
                    typeof(System.Windows.Controls.Control),
                    1),
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

        var definition = Definitions[Kind];
        var scale = Math.Min(ActualWidth, ActualHeight) / 24d;
        var offsetX = (ActualWidth - (24 * scale)) / 2d;
        var offsetY = (ActualHeight - (24 * scale)) / 2d;
        var stroke = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Stroke;
        var accent = SystemParameters.HighContrast ? SystemColors.HighlightBrush : Accent;
        var primaryPen = CreatePen(stroke, StrokeThickness / scale);
        var accentPen = CreatePen(accent, Math.Max(1.7d, StrokeThickness) / scale);

        drawingContext.PushTransform(new TranslateTransform(offsetX, offsetY));
        drawingContext.PushTransform(new ScaleTransform(scale, scale));
        drawingContext.DrawGeometry(null, primaryPen, definition.Primary);
        if (definition.Accent is not null)
        {
            drawingContext.DrawGeometry(null, accentPen, definition.Accent);
        }

        if (definition.Waypoint is { } waypoint)
        {
            drawingContext.DrawEllipse(accent, null, waypoint, definition.WaypointRadius, definition.WaypointRadius);
        }

        drawingContext.Pop();
        drawingContext.Pop();
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

    private static GlyphDefinition CreateDefinition(OrbitControllerGlyphKind kind)
    {
        GlyphDefinition definition = kind switch
        {
            OrbitControllerGlyphKind.PreviousPage => new(
                "M16,5 L9,12 16,19",
                "M19,5 C15,8 15,16 19,19",
                new Point(8, 12)),
            OrbitControllerGlyphKind.NextPage => new(
                "M8,5 L15,12 8,19",
                "M5,5 C9,8 9,16 5,19",
                new Point(16, 12)),
            OrbitControllerGlyphKind.MoreTabs => new(
                "M4,7 H15 M4,12 H18 M4,17 H13",
                "M17,15 L20,18 17,21",
                new Point(4, 12)),
            OrbitControllerGlyphKind.Detach => new(
                "M4,8.5 V5 H15 V16 H11.5 M9,9 H20 V20 H9 Z",
                "M14,4 H20 V10 M20,4 L13,11",
                new Point(6, 18)),
            OrbitControllerGlyphKind.Dock => new(
                "M4,5 H20 V19 H4 Z M9,8 H17 V16 H9 Z",
                "M19,5 L12,12 M12,7 V12 H17",
                new Point(6, 17)),
            OrbitControllerGlyphKind.PlacementTop => new(
                "M4,5 H20 V19 H4 Z M6,10 H18 V17 H6 Z",
                "M6,8 H18 M8,8 V10 M12,8 V10",
                null),
            OrbitControllerGlyphKind.PlacementLeft => new(
                "M4,5 H20 V19 H4 Z M10,7 H18 V17 H10 Z",
                "M7,7 V17 M7,9 H10 M7,13 H10",
                null),
            OrbitControllerGlyphKind.PlacementRight => new(
                "M4,5 H20 V19 H4 Z M6,7 H14 V17 H6 Z",
                "M17,7 V17 M14,9 H17 M14,13 H17",
                null),
            OrbitControllerGlyphKind.Resources => new(
                "M4,15 A8,8 0 0 1 20,15 M7,15 A5,5 0 0 1 17,15",
                "M12,15 L16,10",
                new Point(12, 15),
                2),
            OrbitControllerGlyphKind.Sort => new(
                "M5,6 H16 M5,11 H13 M5,16 H10",
                "M18,5 V18 M15,15 L18,18 21,15",
                new Point(5, 6)),
            OrbitControllerGlyphKind.Filter => new(
                "M4,5 C8,9 10,11 10,15 V19 L14,17 V15 C14,11 16,9 20,5",
                null,
                new Point(12, 6)),
            OrbitControllerGlyphKind.SwitchToTab => new(
                "M4,6 H16 V18 H4 Z",
                "M12,9 L16,12 12,15 M16,12 H21",
                new Point(5, 17)),
            OrbitControllerGlyphKind.CloseTab => new(
                "M7,7 L17,17 M17,7 L7,17",
                "M4,12 A8,8 0 0 0 20,12",
                new Point(4, 12)),
            OrbitControllerGlyphKind.MoveTab => new(
                "M4,7 H15 V17 H4 Z",
                "M14,5 H20 V11 M20,5 L13,12",
                new Point(5, 16)),
            OrbitControllerGlyphKind.GroupTabs => new(
                "M4,7 H10 V12 H4 Z M14,7 H20 V12 H14 Z M7,12 V17 H17 V12",
                "M7,17 C10,20 14,20 17,17",
                new Point(12, 19)),
            OrbitControllerGlyphKind.BulkClose => new(
                "M3,6 H10 V12 H3 Z M8,12 V18 H15 M13,6 H20 V12 H13",
                "M15,15 L20,20 M20,15 L15,20",
                new Point(4, 7)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        return definition.Freeze();
    }

    private sealed record GlyphDefinition(
        Geometry Primary,
        Geometry? Accent,
        Point? Waypoint,
        double WaypointRadius)
    {
        public GlyphDefinition(
            string primary,
            string? accent,
            Point? waypoint,
            double waypointRadius = 1.7)
            : this(
                Geometry.Parse(primary),
                accent is null ? null : Geometry.Parse(accent),
                waypoint,
                waypointRadius)
        {
        }

        public GlyphDefinition Freeze()
        {
            Primary.Freeze();
            Accent?.Freeze();
            return this;
        }
    }
}
#endif
