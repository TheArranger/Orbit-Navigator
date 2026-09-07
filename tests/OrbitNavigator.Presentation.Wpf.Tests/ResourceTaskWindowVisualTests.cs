using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Presentation.Tabs;
using OrbitNavigator.Presentation.Workspace;
using OrbitNavigator.Presentation.Wpf;

using Xunit;

namespace OrbitNavigator.Presentation.Wpf.Tests;

[Collection("WPF focus-sensitive")]
public sealed class ResourceTaskWindowVisualTests
{
    [Fact]
    public void ResourceMonitorIsSeparateOwnedDarkResizableWindowWithoutWebContent() => StaTest.Run(() =>
    {
        var owner = new Window { Width = 900, Height = 700, ShowInTaskbar = false };
        owner.Show();
        var session = Session();
        var monitor = new ResourceTaskWindow(owner, session, reducedMotion: true);
        var visibility = new List<bool>();
        monitor.MonitorVisibilityChanged += (_, visible) => visibility.Add(visible);
        monitor.ShowOrActivate();
        monitor.UpdateLayout();
        Assert.Same(owner, monitor.Owner);
        Assert.Equal(ResizeMode.CanResize, monitor.ResizeMode);
        Assert.False(monitor.ShowInTaskbar);
        Assert.True(monitor.ActualWidth >= ResourceTaskWindow.MinimumWidth);
        Assert.True(monitor.ActualHeight >= ResourceTaskWindow.MinimumHeight);
        Assert.Equal("Browser resources window", AutomationProperties.GetName(monitor));
        Assert.Equal(OrbitVisualTheme.Canvas, monitor.Background);
        Assert.Equal([true], visibility);
        Assert.DoesNotContain(StaTest.Descendants(monitor), value =>
            value.GetType().FullName?.Contains("WebView", StringComparison.OrdinalIgnoreCase) == true);

        var panel = StaTest.FindByAutomationName<ResourceTaskPanelControl>(monitor, "Browser resource manager");
        Assert.Equal(OrbitVisualTheme.Chrome, panel.Background);
        Assert.NotNull(StaTest.FindByAutomationName<TextBlock>(panel, "Browser resources"));
        Assert.NotNull(StaTest.FindByAutomationName<OrbitResourceHistoryGraph>(panel, "CPU trend graph"));
        Assert.NotNull(StaTest.FindByAutomationName<OrbitResourceHistoryGraph>(panel, "Memory trend graph"));
        Assert.DoesNotContain(
            StaTest.Descendants(panel).OfType<Button>(),
            button => AutomationProperties.GetName(button).Contains("history", StringComparison.OrdinalIgnoreCase));
        var scroller = StaTest.Descendants(panel).OfType<ScrollViewer>().Single();
        Assert.Equal(ScrollBarVisibility.Auto, scroller.VerticalScrollBarVisibility);
        Assert.Equal(OrbitVisualTheme.Chrome, scroller.Background);
        Assert.True(scroller.Resources.Contains(typeof(ScrollBar)));

        monitor.Close();
        Assert.False(monitor.IsVisible);
        Assert.False(monitor.IsDisposed);
        Assert.Equal([true, false], visibility);
        Assert.Same(panel, monitor.Panel);

        monitor.ShowOrActivate();
        Assert.True(monitor.IsVisible);
        Assert.False(monitor.IsDisposed);
        Assert.Same(panel, monitor.Panel);
        Assert.Equal([true, false, true], visibility);

        owner.Close();
        Assert.True(monitor.IsDisposed);
        Assert.Equal([true, false, true, false], visibility);
    });

    private static TabControllerPresentationSession Session()
    {
        var windowId = new BrowserWindowId(Guid.NewGuid());
        var tabId = new BrowserTabId(Guid.NewGuid());
        var session = new TabControllerPresentationSession(windowId, false, new Sink());
        session.AcceptProjection(new RevisionedTabControllerProjection(
            windowId,
            false,
            1,
            TabControllerHostState.Docked,
            TabStripPlacement.Top,
            new TabStripViewState(windowId, tabId,
            [new BrowserTabEntry(tabId, null, "Selected tab", null, BrowserLoadState.Idle, true, false, false, false)])));
        return session;
    }

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
}
