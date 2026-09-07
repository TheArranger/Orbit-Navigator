using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;

using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Presentation.Resources;
using OrbitNavigator.Presentation.Tabs;
using OrbitNavigator.Presentation.Workspace;
using OrbitNavigator.Presentation.Wpf;

using Xunit;

namespace OrbitNavigator.Presentation.Wpf.Tests;

public sealed class ResourceTaskPanelVisualTests
{
    [Fact]
    public void SharedAndUnavailableContributionsAreNeverRenderedAsZeroOrLow() => StaTest.Run(() =>
    {
        var (session, first, second) = Session();
        var panel = new ResourceTaskPanelControl { ReducedMotion = true };
        panel.Bind(session);
        var at = DateTimeOffset.UtcNow;
        session.AcceptResourceSample(new(
            1,
            at,
            ResourceSamplingState.Active,
            ResourcePressureLevel.Medium,
            12,
            800 * 1024 * 1024,
            7,
            false,
            "Sampling is active.",
            [
                new(first, ResourceAttributionReliability.SharedProcesses, null, null, 2, 2),
                new(second, ResourceAttributionReliability.Unavailable, null, null, 0, 0),
            ]));
        StaTest.Prepare(panel, 480, 720);

        var copy = string.Join(" ", StaTest.Descendants(panel).OfType<TextBlock>().Select(value => value.Text));
        Assert.Contains("Shared — exact split unavailable", copy);
        Assert.Contains("Resource contribution unavailable", copy);
        Assert.DoesNotContain("0%", copy);
        Assert.Equal(0, panel.ActiveAnimationCount);
    });

    [Fact]
    public void DuplicateAndOutOfOrderSamplesDoNotCreateAnimations() => StaTest.Run(() =>
    {
        var (session, first, _) = Session();
        var panel = new ResourceTaskPanelControl { ReducedMotion = true };
        panel.Bind(session);
        var at = DateTimeOffset.UtcNow;
        session.AcceptResourceSample(Sample(5, at, first));
        session.AcceptResourceSample(Sample(5, at.AddSeconds(1), first));
        session.AcceptResourceSample(Sample(4, at.AddSeconds(2), first));

        Assert.Equal(5, panel.RenderedSampleId);
        Assert.Equal(0, panel.ActiveAnimationCount);
        panel.Unbind();
    });

    [Fact]
    public void PopulatedTabImpactListAndSelectorsExposeNoDefaultWhiteSurfaceInNormalTheme() => StaTest.Run(() =>
    {
        if (SystemParameters.HighContrast)
        {
            return;
        }

        var (session, first, second) = Session();
        var panel = new ResourceTaskPanelControl { ReducedMotion = true };
        panel.Bind(session);
        session.AcceptResourceSample(new(
            1,
            DateTimeOffset.UtcNow,
            ResourceSamplingState.Active,
            ResourcePressureLevel.Medium,
            8,
            384 * 1024 * 1024,
            5,
            true,
            "Sampling is active.",
            [
                new(first, ResourceAttributionReliability.ExclusiveRendererProcesses,
                    4, 192 * 1024 * 1024, 1, 0),
                new(second, ResourceAttributionReliability.SharedProcesses,
                    null, null, 2, 2),
            ]));
        var window = new Window
        {
            Content = panel,
            Width = 680,
            Height = 560,
            ShowInTaskbar = false,
            Background = OrbitVisualTheme.Canvas,
        };
        window.Show();
        try
        {
            window.UpdateLayout();
            var list = StaTest.FindByAutomationName<ListBox>(panel,
                "Tabs and measured resource contributions");
            Assert.Equal(OrbitVisualTheme.Chrome, list.Background);
            Assert.Equal(OrbitVisualTheme.Ink, list.Foreground);
            Assert.True(VirtualizingPanel.GetIsVirtualizing(list));
            Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(list));

            var items = VisualDescendants(list).OfType<ListBoxItem>().ToArray();
            Assert.Equal(2, items.Length);
            Assert.All(items, item =>
            {
                Assert.Equal(OrbitVisualTheme.Surface, item.Background);
                Assert.Equal(OrbitVisualTheme.Ink, item.Foreground);
                Assert.Equal(HorizontalAlignment.Stretch, item.HorizontalContentAlignment);
                Assert.False(IsDefaultWhite(item.Background));
                var row = Assert.IsType<Grid>(item.Content);
                Assert.Equal(Brushes.Transparent, row.Background);
                Assert.Equal(OrbitVisualTheme.Ink, TextElement.GetForeground(row));
            });

            var internalScroller = VisualDescendants(list).OfType<ScrollViewer>().Single();
            Assert.Equal(OrbitVisualTheme.Chrome, internalScroller.Background);
            Assert.False(IsDefaultWhite(internalScroller.Background));
            Assert.NotNull(VisualDescendants(list).OfType<ItemsPresenter>().SingleOrDefault());
            Assert.All(VisualDescendants(list).OfType<Panel>(), child =>
                Assert.False(IsDefaultWhite(child.Background)));
            Assert.All(VisualDescendants(list).OfType<Border>(), child =>
                Assert.False(IsDefaultWhite(child.Background)));

            var selectors = VisualDescendants(panel).OfType<ComboBox>().ToArray();
            Assert.Equal(2, selectors.Length);
            Assert.All(selectors, selector =>
            {
                Assert.Equal(OrbitVisualTheme.Surface, selector.Background);
                Assert.Equal(OrbitVisualTheme.Ink, selector.Foreground);
                Assert.False(IsDefaultWhite(selector.Background));
                var itemStyle = selector.ItemContainerStyle;
                Assert.NotNull(itemStyle);
                var generated = new ComboBoxItem { Style = itemStyle };
                Assert.Equal(OrbitVisualTheme.Surface, generated.Background);
                Assert.Equal(OrbitVisualTheme.Ink, generated.Foreground);

                selector.ApplyTemplate();
                var popup = Assert.IsType<Popup>(selector.Template.FindName("PART_Popup", selector));
                selector.IsDropDownOpen = true;
                var popupBorder = Assert.IsType<Border>(popup.Child);
                Assert.Equal(BrushColor(OrbitVisualTheme.Surface), BrushColor(popupBorder.Background));
                Assert.False(IsDefaultWhite(popupBorder.Background));
                var popupScroller = Assert.IsType<ScrollViewer>(popupBorder.Child);
                Assert.Equal(BrushColor(OrbitVisualTheme.Surface), BrushColor(popupScroller.Background));
                selector.IsDropDownOpen = false;
            });
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void TrendsAreAlwaysPresentAndHiddenMonitorFreezesWithoutOwningSampling() => StaTest.Run(() =>
    {
        var (session, first, _) = Session();
        var panel = new ResourceTaskPanelControl { ReducedMotion = true };
        panel.Bind(session);
        var window = new Window { Content = panel, Width = 520, Height = 720, ShowInTaskbar = false };
        window.Show();
        try
        {
            window.UpdateLayout();
            var at = DateTimeOffset.UtcNow;
            session.AcceptResourceSample(Sample(20, at, first));

            Assert.Equal(1, panel.CpuHistory.HistoryPointCount);
            Assert.Equal(1, panel.MemoryHistory.HistoryPointCount);
            Assert.False(panel.CpuHistory.HasActiveAnimation);
            Assert.False(panel.MemoryHistory.HasActiveAnimation);
            Assert.Equal(Visibility.Visible, panel.CpuTrend.Visibility);
            Assert.Equal(Visibility.Visible, panel.MemoryTrend.Visibility);
            var copy = string.Join(" ", StaTest.Descendants(panel).OfType<TextBlock>().Select(value => value.Text));
            Assert.Contains("Performance trends", copy);
            Assert.Contains("about every 5 seconds", copy);
            Assert.DoesNotContain("Usage history", copy, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Show usage history", copy, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(StaTest.Descendants(panel).OfType<OrbitResourceMeter>());

            window.Hide();
            session.AcceptResourceSample(Sample(21, at.AddSeconds(1), first));
            Assert.Equal(1, panel.CpuHistory.HistoryPointCount);
            Assert.Equal(1, panel.MemoryHistory.HistoryPointCount);
            Assert.Equal(21, panel.RenderedSampleId);

            window.Show();
            window.UpdateLayout();
            session.AcceptResourceSample(Sample(22, at.AddSeconds(6), first));
            Assert.Equal(2, panel.CpuTrend.HistoryPointCount);
            Assert.Equal(2, panel.MemoryTrend.HistoryPointCount);
            Assert.Equal(TimeSpan.FromSeconds(5), ResourceTaskPanelControl.RecommendedSamplingInterval);
        }
        finally
        {
            window.Close();
        }
    });

    private static (TabControllerPresentationSession Session, BrowserTabId First, BrowserTabId Second) Session()
    {
        var window = new BrowserWindowId(Guid.NewGuid());
        var first = new BrowserTabId(Guid.NewGuid());
        var second = new BrowserTabId(Guid.NewGuid());
        var tabs = new BrowserTabEntry[]
        {
            new(first, null, "Mail", new Uri("https://mail.test/"), BrowserLoadState.Idle, true, false, false, false),
            new(second, null, "News", new Uri("https://news.test/"), BrowserLoadState.Idle, false, false, false, false),
        };
        var session = new TabControllerPresentationSession(window, false, new Sink());
        session.AcceptProjection(new(window, false, 2, TabControllerHostState.Docked, TabStripPlacement.Top,
            new(window, first, tabs)));
        return (session, first, second);
    }

    private static ResourceSampleProjection Sample(long id, DateTimeOffset at, BrowserTabId tab) => new(
        id, at, ResourceSamplingState.Active, ResourcePressureLevel.Low, 2, 64 * 1024 * 1024, 3,
        false, "Sampling is active.",
        [new(tab, ResourceAttributionReliability.ExclusiveRendererProcesses, 1, 32 * 1024 * 1024, 1, 0)]);

    private sealed class Sink : ITabControllerCommandSink
    {
        public ValueTask<TabControllerCommandResult> ExecuteAsync(
            TabControllerCommand command,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new TabControllerCommandResult(
                command.Action.StableActionId,
                TabControllerCommandOutcome.Accepted,
                "Completed."));
    }

    private static bool IsDefaultWhite(Brush? brush) =>
        brush is SolidColorBrush solid && solid.Color == Colors.White;

    private static Color BrushColor(Brush brush) =>
        Assert.IsType<SolidColorBrush>(brush).Color;

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            foreach (var child in VisualDescendants(VisualTreeHelper.GetChild(root, index)))
            {
                yield return child;
            }
        }
    }
}
