#if ORBIT_WPF
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;

using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Presentation.Resources;
using OrbitNavigator.Presentation.Tabs;

namespace OrbitNavigator.Presentation.Wpf;

/// <summary>Truthful, low-cost resource presentation over accepted session samples.</summary>
public sealed class ResourceTaskPanelControl : Grid
{
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock updated = new();
    private readonly TextBlock advice = new() { TextWrapping = TextWrapping.Wrap };
    private readonly OrbitResourceStatusGlyph pressure = new() { Height = 38, ShowLabel = true };
    private readonly OrbitResourceHistoryGraph cpuHistory = new()
    {
        Metric = OrbitResourceHistoryMetric.Cpu,
        Accent = OrbitVisualTheme.SeaGlass,
        Height = 210,
        IsSurfaceActive = false,
    };
    private readonly OrbitResourceHistoryGraph memoryHistory = new()
    {
        Metric = OrbitResourceHistoryMetric.Memory,
        Accent = OrbitVisualTheme.PrivateViolet,
        Height = 210,
        IsSurfaceActive = false,
    };
    private readonly ListBox rows = new();
    private readonly ComboBox sort = new();
    private readonly ComboBox filter = new();
    private readonly OrbitShapedCommand bulkClose = new()
    {
        Shape = OrbitCommandShape.Keel,
        Role = OrbitCommandRole.Destructive,
        Content = "Close background tabs",
        MinWidth = 178,
        MinHeight = 44,
    };
    private TabControllerPresentationSession? session;
    private ResourceTaskPanelPresentation state = ResourceTaskPanelPresentation.Empty;
    private long renderedSampleId;
    private bool bulkClosePending;

    /// <summary>
    /// Host sampling guidance for the visible monitor. Presentation remains
    /// timer-free and plots exactly one point for each accepted snapshot.
    /// </summary>
    public static TimeSpan RecommendedSamplingInterval { get; } = TimeSpan.FromSeconds(5);

    public ResourceTaskPanelControl()
    {
        Background = SystemParameters.HighContrast ? SystemColors.WindowBrush : OrbitVisualTheme.Chrome;
        MinWidth = 360;
        TextElement.SetForeground(this,
            SystemParameters.HighContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.Ink);
        AutomationProperties.SetName(this, "Browser resource manager");
        BuildLayout();
        IsVisibleChanged += (_, _) => UpdateHistorySurfaceActivity();
        Loaded += (_, _) => UpdateHistorySurfaceActivity();
        Unloaded += (_, _) =>
        {
            UpdateHistorySurfaceActivity();
        };
    }

    public bool ReducedMotion
    {
        get => cpuHistory.ReducedMotion;
        set
        {
            cpuHistory.ReducedMotion = value;
            memoryHistory.ReducedMotion = value;
        }
    }
    public long RenderedSampleId => renderedSampleId;
    public int ActiveAnimationCount => 0;

    public OrbitResourceHistoryGraph CpuTrend => cpuHistory;
    public OrbitResourceHistoryGraph MemoryTrend => memoryHistory;
    // Compatibility aliases for existing Presentation hosts.
    public OrbitResourceHistoryGraph CpuHistory => cpuHistory;
    public OrbitResourceHistoryGraph MemoryHistory => memoryHistory;

    public bool FocusFirstAction() => sort.Focus();

    public void Bind(TabControllerPresentationSession presentationSession)
    {
        ArgumentNullException.ThrowIfNull(presentationSession);
        if (ReferenceEquals(session, presentationSession)) return;
        Unbind();
        session = presentationSession;
        session.PresentationChanged += OnPresentationChanged;
        if (session.Current is { } current)
        {
            RenderAccepted(current.Resources);
        }
    }

    public void Unbind()
    {
        if (session is not null)
        {
            session.PresentationChanged -= OnPresentationChanged;
            cpuHistory.ResetHistory();
            memoryHistory.ResetHistory();
        }

        session = null;
    }

    private void BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var summary = new StackPanel();
        var heading = new TextBlock
        {
            Text = "Browser resources",
            FontSize = 21,
            FontWeight = FontWeights.SemiBold,
        };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level1);
        AutomationProperties.SetName(heading, "Browser resources");
        summary.Children.Add(heading);
        status.Foreground = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.MutedInk;
        status.Margin = new Thickness(0, 4, 0, 2);
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        summary.Children.Add(status);
        updated.FontSize = 11;
        updated.Foreground = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.MutedInk;
        summary.Children.Add(updated);
        pressure.Margin = new Thickness(0, 10, 0, 0);
        summary.Children.Add(pressure);

        root.Children.Add(summary);

        var details = new StackPanel { Margin = new Thickness(0, 4, 4, 8) };
        var historyHeading = new TextBlock
        {
            Text = "Performance trends",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 14, 0, 2),
        };
        AutomationProperties.SetHeadingLevel(historyHeading, AutomationHeadingLevel.Level2);
        details.Children.Add(historyHeading);
        details.Children.Add(new TextBlock
        {
            Text = "Browser-wide CPU and memory from accepted snapshots. Recommended refresh: about every 5 seconds.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.MutedInk,
            Margin = new Thickness(0, 0, 0, 10),
        });
        details.Children.Add(cpuHistory);
        memoryHistory.Margin = new Thickness(0, 12, 0, 0);
        details.Children.Add(memoryHistory);
        advice.Margin = new Thickness(0, 14, 0, 10);
        details.Children.Add(advice);

        var options = new StackPanel { Orientation = Orientation.Horizontal };
        sort.ItemsSource = Enum.GetValues<ResourceSortKind>();
        sort.SelectedItem = ResourceSortKind.CanonicalOrder;
        filter.ItemsSource = Enum.GetValues<ResourceFilterKind>();
        filter.SelectedItem = ResourceFilterKind.AllTabs;
        sort.MinHeight = filter.MinHeight = 44;
        sort.MinWidth = filter.MinWidth = 150;
        sort.Margin = new Thickness(0, 0, 8, 0);
        sort.Style = OrbitVisualTheme.CreateResourceComboBoxStyle();
        filter.Style = OrbitVisualTheme.CreateResourceComboBoxStyle();
        AutomationProperties.SetName(sort, "Sort resource tabs");
        AutomationProperties.SetName(filter, "Filter resource tabs");
        sort.SelectionChanged += (_, _) => RenderRows();
        filter.SelectionChanged += (_, _) => RenderRows();
        options.Children.Add(sort);
        options.Children.Add(filter);
        details.Children.Add(options);

        bulkClose.Margin = new Thickness(0, 12, 0, 0);
        bulkClose.HorizontalAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetName(bulkClose, "Close background tabs");
        AutomationProperties.SetHelpText(
            bulkClose,
            "Requires confirmation. Closing tabs discards their current in-memory page state.");
        bulkClose.Click += async (_, _) => await RequestBulkCloseAsync();
        details.Children.Add(bulkClose);

        rows.Margin = new Thickness(0, 12, 0, 0);
        rows.MinHeight = 240;
        rows.Style = OrbitVisualTheme.CreateResourceListBoxStyle();
        rows.ItemContainerStyle = OrbitVisualTheme.CreateResourceListBoxItemStyle();
        rows.Resources[typeof(ScrollBar)] = OrbitVisualTheme.CreateResourceScrollBarStyle();
        VirtualizingPanel.SetIsVirtualizing(rows, true);
        VirtualizingPanel.SetVirtualizationMode(rows, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(rows, true);
        AutomationProperties.SetName(rows, "Tabs and measured resource contributions");
        details.Children.Add(rows);
        var detailScroller = new ScrollViewer
        {
            Content = details,
            Background = SystemParameters.HighContrast ? SystemColors.WindowBrush : OrbitVisualTheme.Chrome,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        detailScroller.Resources[typeof(ScrollBar)] = OrbitVisualTheme.CreateResourceScrollBarStyle();
        Grid.SetRow(detailScroller, 1);
        root.Children.Add(detailScroller);
        Children.Add(root);
    }

    private void OnPresentationChanged(object? sender, TabControllerPresentationChangedEventArgs args)
    {
        if (args.Kind != TabControllerPresentationChangeKind.ResourceSample ||
            Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        var acceptedResources = args.State.Resources;
        if (Dispatcher.CheckAccess())
        {
            RenderIfCurrent(sender, acceptedResources);
        }
        else
        {
            _ = Dispatcher.InvokeAsync(() => RenderIfCurrent(sender, acceptedResources));
        }
    }

    private void RenderIfCurrent(object? sender, ResourceTaskPanelPresentation acceptedResources)
    {
        if (ReferenceEquals(sender, session))
        {
            RenderAccepted(acceptedResources);
        }
    }

    private void RenderAccepted(ResourceTaskPanelPresentation presentation)
    {
        if (presentation.SampleId != 0 && presentation.SampleId <= renderedSampleId)
        {
            return;
        }

        state = presentation;
        renderedSampleId = Math.Max(renderedSampleId, presentation.SampleId);
        status.Text = $"{StatusLabel(presentation.SamplingState)} — {presentation.StatusMessage}";
        updated.Text = presentation.SampleId == 0
            ? "No resource sample yet"
            : $"Last updated {presentation.SampledAtUtc.ToLocalTime():T}";
        pressure.Status = presentation.Pressure switch
        {
            ResourcePressureLevel.Low => OrbitResourceVisualStatus.Low,
            ResourcePressureLevel.Medium => OrbitResourceVisualStatus.Medium,
            ResourcePressureLevel.High => OrbitResourceVisualStatus.High,
            _ => OrbitResourceVisualStatus.SharedOrUnavailable,
        };
        cpuHistory.AcceptSample(presentation);
        memoryHistory.AcceptSample(presentation);
        advice.Text = presentation.ClosingTabsMayHelp
            ? "Closing measured background tabs may help reduce browser resource use. Closing a tab discards its in-memory page state."
            : "Closing tabs may help, but the exact effect is unavailable from this sample.";
        bulkClosePending = false;
        bulkClose.Content = "Close background tabs";
        bulkClose.IsEnabled = presentation.Rows.Count(value => !value.IsSelected) > 0;
        bulkClose.ReducedMotion = ReducedMotion;
        RenderRows();
    }

    private void UpdateHistorySurfaceActivity()
    {
        var active = IsLoaded && IsVisible;
        cpuHistory.IsSurfaceActive = active;
        memoryHistory.IsSurfaceActive = active;
    }

    private void RenderRows()
    {
        IEnumerable<TabResourceRowPresentation> projected = state.Rows;
        var selectedFilter = filter.SelectedItem is ResourceFilterKind kind ? kind : state.Filter;
        projected = selectedFilter switch
        {
            ResourceFilterKind.BackgroundTabs => projected.Where(value => !value.IsSelected),
            ResourceFilterKind.MeasuredOnly => projected.Where(value =>
                value.Reliability == ResourceAttributionReliability.ExclusiveRendererProcesses),
            _ => projected,
        };
        var selectedSort = sort.SelectedItem is ResourceSortKind order ? order : state.Sort;
        projected = selectedSort switch
        {
            ResourceSortKind.HighestMeasuredCpu => projected.OrderByDescending(value => value.MeasuredRendererCpuPercent),
            ResourceSortKind.HighestMeasuredMemory => projected.OrderByDescending(value => value.MeasuredRendererPrivateBytes),
            ResourceSortKind.Title => projected.OrderBy(value => value.Title, StringComparer.CurrentCultureIgnoreCase),
            _ => projected,
        };

        rows.Items.Clear();
        foreach (var row in projected)
        {
            rows.Items.Add(CreateRow(row));
        }
    }

    private ListBoxItem CreateRow(TabResourceRowPresentation row)
    {
        var content = new Grid
        {
            Margin = new Thickness(8, 6, 8, 6),
            Background = Brushes.Transparent,
        };
        TextElement.SetForeground(content,
            SystemParameters.HighContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.Ink);
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var copy = new StackPanel();
        copy.Children.Add(new TextBlock
        {
            Text = row.Title,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        copy.Children.Add(new TextBlock
        {
            Text = $"{row.SiteHost} — {row.ReliabilityLabel}",
            Foreground = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.MutedInk,
            FontSize = 11,
        });
        content.Children.Add(copy);
        var value = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Text = row.Reliability == ResourceAttributionReliability.ExclusiveRendererProcesses
                ? $"{row.MeasuredRendererCpuPercent:0.#}%  {FormatBytes(row.MeasuredRendererPrivateBytes)}"
                : "—",
        };
        value.Margin = new Thickness(8, 0, 8, 0);
        Grid.SetColumn(value, 1);
        content.Children.Add(value);
        var switchButton = RowCommand(
            OrbitControllerGlyphKind.SwitchToTab,
            "Switch to tab",
            OrbitCommandRole.Neutral,
            async () => await ExecuteAsync(new SwitchToResourceTabControllerAction(Guid.NewGuid(), row.TabId)));
        var closeButton = RowCommand(
            OrbitControllerGlyphKind.CloseTab,
            "Close tab",
            OrbitCommandRole.Destructive,
            async () => await ExecuteAsync(new CloseTabControllerAction(Guid.NewGuid(), row.TabId)));
        Grid.SetColumn(switchButton, 2);
        Grid.SetColumn(closeButton, 3);
        content.Children.Add(switchButton);
        content.Children.Add(closeButton);
        var item = new ListBoxItem { Tag = row.TabId, Content = content, MinHeight = 52 };
        AutomationProperties.SetName(item,
            $"{row.Title}, {row.SiteHost}, {row.ReliabilityLabel}{(row.IsSelected ? ", selected" : string.Empty)}");
        item.MouseDoubleClick += async (_, _) => await ExecuteAsync(
            new SwitchToResourceTabControllerAction(Guid.NewGuid(), row.TabId));
        return item;
    }

    private OrbitShapedCommand RowCommand(
        OrbitControllerGlyphKind glyphKind,
        string label,
        OrbitCommandRole role,
        Action action)
    {
        var glyph = new OrbitControllerGlyph { Kind = glyphKind, Width = 20, Height = 20 };
        glyph.BindStrokeToAncestorForeground();
        var button = new OrbitShapedCommand
        {
            Shape = OrbitCommandShape.Fin,
            Role = role,
            ReducedMotion = ReducedMotion,
            Content = glyph,
            MinWidth = 44,
            MinHeight = 44,
            Margin = new Thickness(2),
        };
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => action();
        return button;
    }

    private async ValueTask RequestBulkCloseAsync()
    {
        var background = state.Rows.Where(value => !value.IsSelected).Select(value => value.TabId).ToArray();
        if (background.Length == 0)
        {
            return;
        }

        if (!bulkClosePending)
        {
            bulkClosePending = true;
            bulkClose.Content = $"Confirm close {background.Length} tabs";
            AutomationProperties.SetName(bulkClose, $"Confirm close {background.Length} background tabs");
            status.Text = $"Close {background.Length} background tabs? Their current in-memory page state will be discarded.";
            AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Assertive);
            bulkClose.Focus();
            return;
        }

        bulkClosePending = false;
        bulkClose.Content = "Close background tabs";
        AutomationProperties.SetName(bulkClose, "Close background tabs");
        await ExecuteAsync(new BulkCloseTabsControllerAction(Guid.NewGuid(), background));
    }

    private async ValueTask ExecuteAsync(TabControllerAction action)
    {
        if (session is not null)
        {
            await session.ExecuteAsync(action);
        }
    }

    private static string StatusLabel(ResourceSamplingState value) => value switch
    {
        ResourceSamplingState.Active => "Sampling",
        ResourceSamplingState.Starting => "Starting",
        ResourceSamplingState.Paused => "Paused",
        ResourceSamplingState.Stale => "Sample is stale",
        _ => "Unavailable",
    };

    private static string FormatBytes(long? bytes)
    {
        if (bytes is null) return "Unavailable";
        var megabytes = bytes.Value / (1024d * 1024d);
        return megabytes >= 1024
            ? string.Create(CultureInfo.CurrentCulture, $"{megabytes / 1024:0.0} GB")
            : string.Create(CultureInfo.CurrentCulture, $"{megabytes:0} MB");
    }
}
#endif
