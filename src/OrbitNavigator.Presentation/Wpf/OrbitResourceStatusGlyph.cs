#if ORBIT_WPF
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;

namespace OrbitNavigator.Presentation.Wpf;

public enum OrbitResourceVisualStatus
{
    Low = 0,
    Medium = 1,
    High = 2,
    SharedOrUnavailable = 3,
}

/// <summary>
/// Non-color-only resource status mark. Rising one/two/three gauge marks and
/// visible text distinguish measured bands; shared or unavailable attribution
/// uses outlined dashes and can never look like zero or Low usage.
/// </summary>
public sealed class OrbitResourceStatusGlyph : FrameworkElement
{
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status),
        typeof(OrbitResourceVisualStatus),
        typeof(OrbitResourceStatusGlyph),
        new FrameworkPropertyMetadata(
            OrbitResourceVisualStatus.Low,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            OnStatusChanged));

    public static readonly DependencyProperty ShowLabelProperty = DependencyProperty.Register(
        nameof(ShowLabel),
        typeof(bool),
        typeof(OrbitResourceStatusGlyph),
        new FrameworkPropertyMetadata(
            true,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly FontFamily LabelFont = new("Segoe UI Variable Text, Segoe UI");
    private bool systemParameterEventsAttached;

    public OrbitResourceStatusGlyph()
    {
        Focusable = false;
        IsHitTestVisible = false;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        RefreshAutomationState();
    }

    public OrbitResourceVisualStatus Status
    {
        get => (OrbitResourceVisualStatus)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    public bool ShowLabel
    {
        get => (bool)GetValue(ShowLabelProperty);
        set => SetValue(ShowLabelProperty, value);
    }

    public string Label => Status switch
    {
        OrbitResourceVisualStatus.Low => "Low",
        OrbitResourceVisualStatus.Medium => "Medium",
        OrbitResourceVisualStatus.High => "High",
        OrbitResourceVisualStatus.SharedOrUnavailable => "Shared / unavailable",
        _ => "Unavailable",
    };

    protected override Size MeasureOverride(Size availableSize)
    {
        var desired = new Size(ShowLabel ? 142 : 38, 38);
        return new Size(
            double.IsInfinity(availableSize.Width) ? desired.Width : Math.Min(desired.Width, availableSize.Width),
            double.IsInfinity(availableSize.Height) ? desired.Height : Math.Min(desired.Height, availableSize.Height));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var highContrast = SystemParameters.HighContrast;
        var outline = highContrast ? SystemColors.WindowTextBrush : StatusBrush(Status);
        var fill = highContrast ? SystemColors.HighlightBrush : outline;
        var center = new Point(19, ActualHeight / 2);
        drawingContext.DrawEllipse(null, CreatePen(outline, highContrast ? 2 : 1.4), center, 17, 17);

        if (Status == OrbitResourceVisualStatus.SharedOrUnavailable)
        {
            for (var index = 0; index < 3; index++)
            {
                var y = center.Y - 6 + (index * 6);
                drawingContext.DrawLine(CreatePen(outline, 1.8), new Point(10 + (index * 2), y), new Point(16 + (index * 2), y));
            }
        }
        else
        {
            var filledCount = (int)Status + 1;
            for (var index = 0; index < 3; index++)
            {
                var height = 6 + (index * 5);
                var rect = new Rect(10 + (index * 6), center.Y + 9 - height, 4, height);
                var barFill = index < filledCount ? fill : Brushes.Transparent;
                drawingContext.DrawRoundedRectangle(barFill, CreatePen(outline, 1), rect, 2, 2);
            }
        }

        if (ShowLabel && ActualWidth > 48)
        {
            var textBrush = highContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.Ink;
            var text = new FormattedText(
                Label,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(LabelFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                12,
                textBrush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip)
            {
                MaxTextWidth = Math.Max(1, ActualWidth - 46),
                Trimming = TextTrimming.CharacterEllipsis,
            };
            drawingContext.DrawText(text, new Point(44, Math.Max(0, (ActualHeight - text.Height) / 2)));
        }
    }

    private static void OnStatusChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args) =>
        ((OrbitResourceStatusGlyph)dependencyObject).RefreshAutomationState();

    private void RefreshAutomationState()
    {
        AutomationProperties.SetName(this, $"{Label} resource use");
        AutomationProperties.SetItemStatus(this, Label);
    }

    private static Brush StatusBrush(OrbitResourceVisualStatus status) => status switch
    {
        OrbitResourceVisualStatus.Low => OrbitVisualTheme.SeaGlass,
        OrbitResourceVisualStatus.Medium => OrbitVisualTheme.WaypointGold,
        OrbitResourceVisualStatus.High => OrbitVisualTheme.Danger,
        _ => OrbitVisualTheme.MutedInk,
    };

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

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (!systemParameterEventsAttached)
        {
            SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
            systemParameterEventsAttached = true;
        }
        InvalidateVisual();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
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
            InvalidateVisual();
        }
    }
}
#endif
