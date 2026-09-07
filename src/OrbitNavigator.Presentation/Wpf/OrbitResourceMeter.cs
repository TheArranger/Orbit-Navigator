#if ORBIT_WPF
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;

namespace OrbitNavigator.Presentation.Wpf;

public enum OrbitResourceMeterMode
{
    Measured = 0,
    SharedOrUnavailable = 1,
    Stale = 2,
}

/// <summary>
/// Visual-only meter for Presentation-accepted resource samples. It owns no
/// sampler, timer, or attribution logic. The containing panel drives all
/// visible meters from one shared transition clock by calling
/// <see cref="RenderTransitionFrame"/> for at most 140 ms after an accepted
/// monotonic sample.
/// </summary>
public sealed class OrbitResourceMeter : FrameworkElement
{
    public static readonly TimeSpan MaximumTransitionDuration = TimeSpan.FromMilliseconds(140);

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent),
        typeof(Brush),
        typeof(OrbitResourceMeter),
        new FrameworkPropertyMetadata(
            OrbitVisualTheme.SeaGlass,
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ReducedMotionProperty = DependencyProperty.Register(
        nameof(ReducedMotion),
        typeof(bool),
        typeof(OrbitResourceMeter),
        new FrameworkPropertyMetadata(false, OnReducedMotionChanged));

    private long lastAcceptedSampleId = -1;
    private double previousValue;
    private double targetValue;
    private double displayValue;
    private bool systemParameterEventsAttached;

    public OrbitResourceMeter()
    {
        MinHeight = 8;
        Focusable = false;
        IsHitTestVisible = false;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        RefreshAutomationState();
    }

    public Brush Accent
    {
        get => (Brush)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public bool ReducedMotion
    {
        get => (bool)GetValue(ReducedMotionProperty);
        set => SetValue(ReducedMotionProperty, value);
    }

    public long LastAcceptedSampleId => lastAcceptedSampleId;

    public double DisplayValue => displayValue;

    public double TargetValue => targetValue;

    public OrbitResourceMeterMode Mode { get; private set; } = OrbitResourceMeterMode.SharedOrUnavailable;

    public bool HasPendingTransition { get; private set; }

    /// <summary>
    /// Begins one transition for a newly accepted, monotonic Presentation
    /// sample. Duplicate and out-of-order sample IDs are ignored. The value is
    /// normalized to the inclusive 0..1 range and is meaningful only when the
    /// supplied mode is Measured.
    /// </summary>
    public bool BeginAcceptedSample(
        long sampleId,
        double normalizedValue,
        OrbitResourceMeterMode mode)
    {
        if (sampleId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleId));
        }
        if (!double.IsFinite(normalizedValue))
        {
            throw new ArgumentOutOfRangeException(nameof(normalizedValue));
        }
        if (sampleId <= lastAcceptedSampleId)
        {
            return false;
        }

        lastAcceptedSampleId = sampleId;
        Mode = mode;
        previousValue = displayValue;
        targetValue = mode == OrbitResourceMeterMode.Measured
            ? Math.Clamp(normalizedValue, 0, 1)
            : 0;
        HasPendingTransition = mode == OrbitResourceMeterMode.Measured &&
            !ReducedMotion &&
            !SystemParameters.HighContrast &&
            !DoubleEquals(previousValue, targetValue);
        if (!HasPendingTransition)
        {
            displayValue = targetValue;
        }

        RefreshAutomationState();
        InvalidateVisual();
        return true;
    }

    /// <summary>
    /// Applies a frame from the containing panel's one shared transition clock.
    /// The caller supplies normalized elapsed time; this method performs no
    /// scheduling and allocates no animation clocks.
    /// </summary>
    public void RenderTransitionFrame(double normalizedProgress)
    {
        if (!double.IsFinite(normalizedProgress))
        {
            throw new ArgumentOutOfRangeException(nameof(normalizedProgress));
        }
        if (!HasPendingTransition)
        {
            return;
        }

        var progress = Math.Clamp(normalizedProgress, 0, 1);
        var eased = progress * progress * (3 - (2 * progress));
        displayValue = previousValue + ((targetValue - previousValue) * eased);
        if (progress >= 1)
        {
            displayValue = targetValue;
            HasPendingTransition = false;
        }
        InvalidateVisual();
    }

    public void CompleteTransition()
    {
        displayValue = targetValue;
        HasPendingTransition = false;
        InvalidateVisual();
    }

    /// <summary>
    /// Applies an out-of-band quality state such as stale after missed samples.
    /// It never fabricates a new sample ID or animates to a false zero value.
    /// </summary>
    public void ShowQualityState(OrbitResourceMeterMode mode)
    {
        if (mode == OrbitResourceMeterMode.Measured)
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        Mode = mode;
        targetValue = 0;
        displayValue = 0;
        HasPendingTransition = false;
        RefreshAutomationState();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        const double desiredWidth = 120;
        const double desiredHeight = 8;
        return new Size(
            double.IsInfinity(availableSize.Width) ? desiredWidth : Math.Min(desiredWidth, availableSize.Width),
            double.IsInfinity(availableSize.Height) ? desiredHeight : Math.Min(desiredHeight, availableSize.Height));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var highContrast = SystemParameters.HighContrast;
        var track = highContrast ? SystemColors.ControlTextBrush : OrbitVisualTheme.Divider;
        var accent = highContrast ? SystemColors.HighlightBrush : Accent;
        var height = Math.Max(4, Math.Min(8, ActualHeight));
        var y = (ActualHeight - height) / 2;
        var rect = new Rect(0.5, y + 0.5, Math.Max(1, ActualWidth - 1), Math.Max(1, height - 1));
        drawingContext.DrawRoundedRectangle(
            highContrast ? SystemColors.ControlBrush : OrbitVisualTheme.Canvas,
            CreatePen(track, highContrast ? 2 : 1),
            rect,
            height / 2,
            height / 2);

        if (Mode == OrbitResourceMeterMode.Measured)
        {
            var fillWidth = Math.Max(0, (rect.Width - 2) * displayValue);
            if (fillWidth > 0)
            {
                var fill = new Rect(rect.X + 1, rect.Y + 1, fillWidth, Math.Max(1, rect.Height - 2));
                drawingContext.DrawRoundedRectangle(accent, null, fill, fill.Height / 2, fill.Height / 2);
            }
            return;
        }

        var patternPen = CreatePen(highContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.MutedInk, 1.4);
        if (Mode == OrbitResourceMeterMode.SharedOrUnavailable)
        {
            for (var x = 5d; x < ActualWidth - 4; x += 11)
            {
                drawingContext.DrawLine(patternPen, new Point(x, ActualHeight / 2), new Point(Math.Min(x + 6, ActualWidth - 4), ActualHeight / 2));
            }
            return;
        }

        for (var x = 4d; x < ActualWidth; x += 10)
        {
            drawingContext.DrawLine(patternPen, new Point(x, y + height - 1), new Point(Math.Min(x + 6, ActualWidth), y + 1));
        }
    }

    private static void OnReducedMotionChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if ((bool)args.NewValue)
        {
            ((OrbitResourceMeter)dependencyObject).CompleteTransition();
        }
    }

    private void RefreshAutomationState()
    {
        var status = Mode switch
        {
            OrbitResourceMeterMode.Measured => $"{displayValue:P0} measured",
            OrbitResourceMeterMode.SharedOrUnavailable => "Exact split unavailable",
            OrbitResourceMeterMode.Stale => "Resource sample stale",
            _ => "Resource use unavailable",
        };
        AutomationProperties.SetItemStatus(this, status);
    }

    private static bool DoubleEquals(double first, double second) => Math.Abs(first - second) < 0.000001d;

    private static Pen CreatePen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
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
        CompleteTransition();
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
            if (SystemParameters.HighContrast)
            {
                CompleteTransition();
            }
            InvalidateVisual();
        }
    }
}
#endif
