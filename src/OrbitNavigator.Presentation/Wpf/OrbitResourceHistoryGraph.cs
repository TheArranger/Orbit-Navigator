#if ORBIT_WPF
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Media;

using OrbitNavigator.Presentation.Resources;

namespace OrbitNavigator.Presentation.Wpf;

public enum OrbitResourceHistoryMetric
{
    Cpu = 0,
    Memory = 1,
}

/// <summary>A raw point from one accepted, active resource sample.</summary>
public readonly record struct OrbitResourceHistoryPoint(
    long SampleId,
    DateTimeOffset SampledAtUtc,
    double Value);

/// <summary>
/// Compact, timer-free history for Presentation-accepted resource samples.
/// The graph never samples, estimates, interpolates, backfills, or owns a clock.
/// It appends at most one point per monotonic active sample with a numeric value.
/// </summary>
public sealed class OrbitResourceHistoryGraph : FrameworkElement
{
    public const int MaximumHistoryPoints = 30;
    public static TimeSpan RecommendedSampleInterval { get; } = TimeSpan.FromSeconds(5);
    public static TimeSpan NominalRollingWindow { get; } =
        TimeSpan.FromSeconds((MaximumHistoryPoints - 1) * 5);
    private static readonly double[] MemoryScaleCandidatesMb =
        [64d, 128d, 256d, 512d, 1024d, 2048d, 4096d, 8192d, 16384d];

    public static readonly DependencyProperty MetricProperty = DependencyProperty.Register(
        nameof(Metric),
        typeof(OrbitResourceHistoryMetric),
        typeof(OrbitResourceHistoryGraph),
        new FrameworkPropertyMetadata(
            OrbitResourceHistoryMetric.Cpu,
            FrameworkPropertyMetadataOptions.AffectsMeasure |
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnMetricChanged));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent),
        typeof(Brush),
        typeof(OrbitResourceHistoryGraph),
        new FrameworkPropertyMetadata(
            OrbitVisualTheme.SeaGlass,
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ReducedMotionProperty = DependencyProperty.Register(
        nameof(ReducedMotion),
        typeof(bool),
        typeof(OrbitResourceHistoryGraph),
        new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty IsSurfaceActiveProperty = DependencyProperty.Register(
        nameof(IsSurfaceActive),
        typeof(bool),
        typeof(OrbitResourceHistoryGraph),
        new FrameworkPropertyMetadata(true, OnPresentationPropertyChanged));

    private readonly OrbitResourceHistoryPoint[] history = new OrbitResourceHistoryPoint[MaximumHistoryPoints];
    private int historyStart;
    private int historyCount;
    private long lastObservedSampleId = -1;
    private ResourceSamplingState samplingState = ResourceSamplingState.Paused;
    private DateTimeOffset? lastUpdatedAtUtc;
    private double? latestValue;
    private string? biggestMeasuredConsumerTitle;
    private double? biggestMeasuredConsumerValue;
    private bool systemParameterEventsAttached;

    public OrbitResourceHistoryGraph()
    {
        MinWidth = 180;
        MinHeight = 54;
        Focusable = false;
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        RefreshAutomationState();
    }

    public OrbitResourceHistoryMetric Metric
    {
        get => (OrbitResourceHistoryMetric)GetValue(MetricProperty);
        set => SetValue(MetricProperty, value);
    }

    public Brush Accent
    {
        get => (Brush)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    /// <summary>
    /// Compatibility state for the containing reduced-motion surface. This
    /// graph is static for all users and never creates an animation clock.
    /// </summary>
    public bool ReducedMotion
    {
        get => (bool)GetValue(ReducedMotionProperty);
        set => SetValue(ReducedMotionProperty, value);
    }

    /// <summary>
    /// Set by the containing surface. Samples received while inactive are not
    /// appended or backfilled, which keeps hidden surfaces frozen.
    /// </summary>
    public bool IsSurfaceActive
    {
        get => (bool)GetValue(IsSurfaceActiveProperty);
        set => SetValue(IsSurfaceActiveProperty, value);
    }

    public int HistoryPointCount => historyCount;

    public long LastObservedSampleId => lastObservedSampleId;

    public long? LastPlottedSampleId => historyCount == 0 ? null : PointAt(historyCount - 1).SampleId;

    public DateTimeOffset? LastUpdatedAtUtc => lastUpdatedAtUtc;

    public double? LatestValue => latestValue;

    public ResourceSamplingState SamplingState => samplingState;

    public string MetricLabel => Metric == OrbitResourceHistoryMetric.Cpu ? "CPU" : "Memory";

    public string CurrentValueText => latestValue is { } value ? FormatValue(value) : "Unavailable";

    public string LatestValueText => $"Latest {CurrentValueText}";

    public string LastUpdatedText => lastUpdatedAtUtc is { } sampledAt
        ? $"Last updated {sampledAt.ToLocalTime():T}"
        : "No accepted sample";

    public string SamplingStateText
    {
        get
        {
            if (!IsSurfaceActive)
            {
                return "Monitor hidden — trend paused";
            }

            return samplingState switch
            {
                ResourceSamplingState.Active when latestValue is not null => "Sampling active",
                ResourceSamplingState.Active => $"{MetricLabel} unavailable in active sample",
                ResourceSamplingState.Starting => "Sampling starting — trend paused",
                ResourceSamplingState.Paused => "Sampling paused — trend paused",
                ResourceSamplingState.Stale => "Sample stale — trend paused",
                _ => "Sampling unavailable — trend paused",
            };
        }
    }

    public string TimeWindowText
    {
        get
        {
            if (historyCount < 2)
            {
                return $"Rolling window up to {FormatDuration(NominalRollingWindow)}";
            }

            var duration = PointAt(historyCount - 1).SampledAtUtc - PointAt(0).SampledAtUtc;
            return $"Rolling window {FormatDuration(duration)} · {historyCount} accepted samples";
        }
    }

    public string ScaleText => Metric == OrbitResourceHistoryMetric.Cpu
        ? "Scale 0 to 100 percent"
        : $"Scale 0 to {FormatValue(MemoryCeiling())}";

    public string BiggestMeasuredConsumerText =>
        biggestMeasuredConsumerTitle is { } title && biggestMeasuredConsumerValue is { } value
            ? $"Top measured: {title} — {FormatValue(value)} measured renderer contribution"
            : "No exclusive measured consumer available";

    /// <summary>This visual never owns an animation or sampling clock.</summary>
    public bool HasActiveAnimation => false;

    /// <summary>
    /// Observes one already-accepted Presentation sample. Returns true only
    /// when that sample appends a real point to this metric's history.
    /// </summary>
    public bool AcceptSample(ResourceTaskPanelPresentation acceptedSample)
    {
        ArgumentNullException.ThrowIfNull(acceptedSample);
        if (acceptedSample.SampleId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(acceptedSample));
        }
        if (acceptedSample.SampleId <= lastObservedSampleId)
        {
            return false;
        }

        lastObservedSampleId = acceptedSample.SampleId;
        samplingState = acceptedSample.SamplingState;
        var numericValue = Metric == OrbitResourceHistoryMetric.Cpu
            ? acceptedSample.BrowserCpuPercent
            : acceptedSample.BrowserPrivateBytes is { } bytes
                ? bytes
                : null;

        var appended = acceptedSample.SampleId > 0 &&
            acceptedSample.SamplingState == ResourceSamplingState.Active &&
            IsSurfaceActive &&
            numericValue is { } value &&
            double.IsFinite(value) &&
            value >= 0;
        if (appended)
        {
            Append(new OrbitResourceHistoryPoint(
                acceptedSample.SampleId,
                acceptedSample.SampledAtUtc,
                numericValue!.Value));
            latestValue = numericValue.Value;
            lastUpdatedAtUtc = acceptedSample.SampledAtUtc;
            UpdateBiggestMeasuredConsumer(acceptedSample.Rows);
        }

        RefreshAutomationState();
        InvalidateVisual();
        return appended;
    }

    /// <summary>
    /// Returns an ordered copy for diagnostics and tests. Rendering uses the
    /// fixed ring buffer directly and does not allocate a history collection.
    /// </summary>
    public IReadOnlyList<OrbitResourceHistoryPoint> GetHistorySnapshot()
    {
        var snapshot = new OrbitResourceHistoryPoint[historyCount];
        for (var index = 0; index < historyCount; index++)
        {
            snapshot[index] = PointAt(index);
        }
        return snapshot;
    }

    /// <summary>Clears presentation-only history when the owning session changes.</summary>
    public void ResetHistory()
    {
        Array.Clear(history);
        historyStart = 0;
        historyCount = 0;
        lastObservedSampleId = -1;
        samplingState = ResourceSamplingState.Paused;
        lastUpdatedAtUtc = null;
        latestValue = null;
        biggestMeasuredConsumerTitle = null;
        biggestMeasuredConsumerValue = null;
        RefreshAutomationState();
        InvalidateVisual();
    }

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new OrbitResourceHistoryGraphAutomationPeer(this);

    protected override Size MeasureOverride(Size availableSize)
    {
        const double desiredHeight = 210d;
        return new Size(
            double.IsInfinity(availableSize.Width) ? 520 : availableSize.Width,
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
        var background = highContrast ? SystemColors.WindowBrush : OrbitVisualTheme.Surface;
        var plotBackground = highContrast ? SystemColors.WindowBrush : OrbitVisualTheme.Canvas;
        var foreground = highContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.Ink;
        var muted = highContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.MutedInk;
        var divider = highContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.Divider;
        var accent = highContrast ? SystemColors.HighlightBrush : Accent;
        var card = new Rect(0.5, 0.5, Math.Max(1, ActualWidth - 1), Math.Max(1, ActualHeight - 1));
        drawingContext.DrawRoundedRectangle(
            background,
            CreatePen(divider, highContrast ? 2 : 1),
            card,
            10,
            10);

        const double inset = 12;
        var contentWidth = Math.Max(1, ActualWidth - (inset * 2));
        DrawTextClipped(
            drawingContext,
            $"{MetricLabel} trend",
            new Rect(inset, 8, contentWidth * 0.55, 20),
            foreground,
            12,
            FontWeights.SemiBold);
        DrawTextClipped(
            drawingContext,
            LatestValueText,
            new Rect(inset + (contentWidth * 0.55), 8, contentWidth * 0.45, 20),
            foreground,
            12,
            FontWeights.SemiBold,
            rightAligned: true);

        DrawTextClipped(
            drawingContext,
            ScaleText,
            new Rect(inset, 31, contentWidth * 0.58, 16),
            muted,
            9.5,
            FontWeights.Normal);
        DrawTextClipped(
            drawingContext,
            historyCount == 0 ? "Waiting for first accepted snapshot" : TimeWindowText,
            new Rect(inset + (contentWidth * 0.42), 31, contentWidth * 0.58, 16),
            muted,
            9.5,
            FontWeights.Normal,
            rightAligned: true);

        const double scaleGutter = 44;
        var plot = new Rect(
            inset + scaleGutter,
            52,
            Math.Max(24, contentWidth - scaleGutter),
            Math.Max(44, ActualHeight - 114));
        drawingContext.DrawRoundedRectangle(
            plotBackground,
            CreatePen(divider, highContrast ? 2 : 1),
            plot,
            5,
            5);
        DrawPlotGrid(drawingContext, plot, divider, highContrast);
        DrawHistory(drawingContext, plot, accent, highContrast);

        var ceiling = Metric == OrbitResourceHistoryMetric.Cpu ? 100d : MemoryCeiling();
        DrawTextClipped(
            drawingContext,
            FormatValue(ceiling),
            new Rect(inset, plot.Top - 3, scaleGutter - 5, 16),
            muted,
            9,
            FontWeights.Normal,
            rightAligned: true);
        DrawTextClipped(
            drawingContext,
            Metric == OrbitResourceHistoryMetric.Cpu ? "0%" : "0 B",
            new Rect(inset, plot.Bottom - 13, scaleGutter - 5, 16),
            muted,
            9,
            FontWeights.Normal,
            rightAligned: true);

        var metadataY = plot.Bottom + 5;
        DrawTextClipped(
            drawingContext,
            TimeWindowText,
            new Rect(inset, metadataY, contentWidth, 15),
            muted,
            9.5,
            FontWeights.Normal);
        DrawTextClipped(
            drawingContext,
            $"{SamplingStateText} · {LastUpdatedText}",
            new Rect(inset, metadataY + 17, contentWidth, 15),
            muted,
            9.5,
            FontWeights.Normal);
        DrawTextClipped(
            drawingContext,
            BiggestMeasuredConsumerText,
            new Rect(inset, metadataY + 34, contentWidth, 15),
            muted,
            9.5,
            FontWeights.Normal);
    }

    private void Append(OrbitResourceHistoryPoint point)
    {
        if (historyCount < MaximumHistoryPoints)
        {
            history[(historyStart + historyCount) % MaximumHistoryPoints] = point;
            historyCount++;
            return;
        }

        history[historyStart] = point;
        historyStart = (historyStart + 1) % MaximumHistoryPoints;
    }

    private OrbitResourceHistoryPoint PointAt(int index) =>
        history[(historyStart + index) % MaximumHistoryPoints];

    private void UpdateBiggestMeasuredConsumer(IReadOnlyList<TabResourceRowPresentation> rows)
    {
        TabResourceRowPresentation? biggest = null;
        double? biggestValue = null;
        foreach (var row in rows)
        {
            if (row.Reliability != ResourceAttributionReliability.ExclusiveRendererProcesses)
            {
                continue;
            }

            var candidate = Metric == OrbitResourceHistoryMetric.Cpu
                ? row.MeasuredRendererCpuPercent
                : row.MeasuredRendererPrivateBytes is { } bytes
                    ? bytes
                    : null;
            if (candidate is not { } value || !double.IsFinite(value) || value < 0 ||
                (biggestValue is not null && value <= biggestValue.Value))
            {
                continue;
            }

            biggest = row;
            biggestValue = value;
        }

        biggestMeasuredConsumerTitle = biggest?.Title;
        biggestMeasuredConsumerValue = biggestValue;
    }

    private void DrawHistory(DrawingContext drawingContext, Rect plot, Brush accent, bool highContrast)
    {
        if (historyCount == 0)
        {
            DrawTextClipped(
                drawingContext,
                "Waiting for an active measured sample",
                new Rect(plot.X + 7, plot.Y + 14, Math.Max(1, plot.Width - 14), 16),
                highContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.MutedInk,
                9.5,
                FontWeights.Normal);
            return;
        }

        var inner = new Rect(plot.X + 5, plot.Y + 5, Math.Max(1, plot.Width - 10), Math.Max(1, plot.Height - 10));
        if (historyCount == 1)
        {
            var y = ValueY(PointAt(0).Value, inner);
            drawingContext.DrawEllipse(accent, null, new Point(inner.X + (inner.Width / 2), y), 2.8, 2.8);
            return;
        }

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var lineContext = line.Open())
        using (var areaContext = area.Open())
        {
            var first = PlotPoint(0, inner);
            lineContext.BeginFigure(first, false, false);
            areaContext.BeginFigure(new Point(first.X, inner.Bottom), true, true);
            areaContext.LineTo(first, true, false);
            for (var index = 1; index < historyCount; index++)
            {
                var point = PlotPoint(index, inner);
                lineContext.LineTo(point, true, false);
                areaContext.LineTo(point, true, false);
            }
            areaContext.LineTo(new Point(inner.Right, inner.Bottom), true, false);
        }
        line.Freeze();
        area.Freeze();

        if (!highContrast)
        {
            drawingContext.PushOpacity(0.16);
            drawingContext.DrawGeometry(accent, null, area);
            drawingContext.Pop();
        }
        drawingContext.DrawGeometry(null, CreatePen(accent, highContrast ? 2.5 : 2), line);
        var latest = PlotPoint(historyCount - 1, inner);
        drawingContext.DrawEllipse(accent, null, latest, highContrast ? 3.5 : 3, highContrast ? 3.5 : 3);
    }

    private Point PlotPoint(int index, Rect plot)
    {
        var first = PointAt(0).SampledAtUtc;
        var last = PointAt(historyCount - 1).SampledAtUtc;
        var totalTicks = (last - first).Ticks;
        var fraction = totalTicks > 0
            ? (double)Math.Clamp((PointAt(index).SampledAtUtc - first).Ticks, 0, totalTicks) / totalTicks
            : (double)index / Math.Max(1, historyCount - 1);
        var x = plot.X + (fraction * plot.Width);
        return new Point(x, ValueY(PointAt(index).Value, plot));
    }

    private double ValueY(double value, Rect plot)
    {
        var ceiling = Metric == OrbitResourceHistoryMetric.Cpu ? 100d : MemoryCeiling();
        var normalized = Math.Clamp(value / ceiling, 0, 1);
        return plot.Bottom - (normalized * plot.Height);
    }

    private double MemoryCeiling()
    {
        var maximum = 1d;
        for (var index = 0; index < historyCount; index++)
        {
            maximum = Math.Max(maximum, PointAt(index).Value);
        }

        var megabytes = maximum / (1024d * 1024d);
        var ceilingMegabytes = MemoryScaleCandidatesMb.FirstOrDefault(value => value >= megabytes * 1.1);
        if (ceilingMegabytes <= 0)
        {
            ceilingMegabytes = Math.Ceiling(megabytes / 4096d) * 4096d;
        }
        return Math.Max(64d, ceilingMegabytes) * 1024d * 1024d;
    }

    private static void DrawPlotGrid(
        DrawingContext drawingContext,
        Rect plot,
        Brush divider,
        bool highContrast)
    {
        var pen = CreatePen(divider, highContrast ? 1.5 : 1);
        if (!highContrast)
        {
            drawingContext.PushOpacity(0.55);
        }
        for (var step = 1; step <= 2; step++)
        {
            var y = plot.Y + ((plot.Height / 3) * step);
            drawingContext.DrawLine(pen, new Point(plot.X + 1, y), new Point(plot.Right - 1, y));
        }
        for (var step = 1; step <= 3; step++)
        {
            var x = plot.X + ((plot.Width / 4) * step);
            drawingContext.DrawLine(pen, new Point(x, plot.Y + 1), new Point(x, plot.Bottom - 1));
        }
        if (!highContrast)
        {
            drawingContext.Pop();
        }
    }

    private void DrawTextClipped(
        DrawingContext drawingContext,
        string text,
        Rect bounds,
        Brush brush,
        double size,
        FontWeight weight,
        bool rightAligned = false)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection,
            new Typeface(SystemFonts.MessageFontFamily, FontStyles.Normal, weight, FontStretches.Normal),
            size,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        formatted.MaxTextWidth = Math.Max(1, bounds.Width);
        formatted.MaxTextHeight = Math.Max(size + 3, bounds.Height);
        var x = rightAligned ? Math.Max(bounds.X, bounds.Right - formatted.Width) : bounds.X;
        drawingContext.PushClip(new RectangleGeometry(bounds));
        drawingContext.DrawText(formatted, new Point(x, bounds.Y));
        drawingContext.Pop();
    }

    private string FormatValue(double value)
    {
        if (Metric == OrbitResourceHistoryMetric.Cpu)
        {
            return string.Create(CultureInfo.CurrentCulture, $"{value:0.#}%");
        }

        var megabytes = value / (1024d * 1024d);
        return megabytes >= 1024
            ? string.Create(CultureInfo.CurrentCulture, $"{megabytes / 1024:0.0} GB")
            : string.Create(CultureInfo.CurrentCulture, $"{megabytes:0} MB");
    }

    private static string FormatDuration(TimeSpan duration)
    {
        duration = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        if (duration.TotalMinutes >= 1)
        {
            var minutes = (int)duration.TotalMinutes;
            var seconds = duration.Seconds;
            return seconds == 0 ? $"{minutes} min" : $"{minutes} min {seconds} sec";
        }

        return $"{Math.Max(0, (int)Math.Round(duration.TotalSeconds))} sec";
    }

    private void RefreshAutomationState()
    {
        AutomationProperties.SetName(this, $"{MetricLabel} trend graph");
        AutomationProperties.SetItemStatus(this, $"{CurrentValueText}. {SamplingStateText}. {LastUpdatedText}.");
        AutomationProperties.SetHelpText(
            this,
            $"{TimeWindowText}. {ScaleText}. " +
            $"{historyCount} accepted real sample{(historyCount == 1 ? string.Empty : "s")}. " +
            $"{BiggestMeasuredConsumerText}. No estimated or shared per-tab values are shown.");
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

    private static void OnMetricChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var graph = (OrbitResourceHistoryGraph)dependencyObject;
        if (!Enum.IsDefined((OrbitResourceHistoryMetric)args.NewValue))
        {
            throw new ArgumentOutOfRangeException(nameof(args));
        }
        graph.ResetHistory();
    }

    private static void OnPresentationPropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        var graph = (OrbitResourceHistoryGraph)dependencyObject;
        graph.RefreshAutomationState();
        graph.InvalidateVisual();
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
            RefreshAutomationState();
            InvalidateVisual();
        }
    }

    private sealed class OrbitResourceHistoryGraphAutomationPeer(OrbitResourceHistoryGraph owner)
        : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(OrbitResourceHistoryGraph);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
    }
}
#endif
