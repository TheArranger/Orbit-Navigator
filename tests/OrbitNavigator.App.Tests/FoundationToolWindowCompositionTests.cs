using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using OrbitNavigator.Foundation.Browser;
using OrbitNavigator.Presentation.Wpf;
using Xunit;

namespace OrbitNavigator.App.Tests;

public sealed class FoundationToolWindowCompositionTests
{
    [Fact]
    public void DetachedControllerHostContainsOnlyTabsAtCompactDarkDefaultSize() => RunSta(() =>
    {
        var owner = CreateOwner();
        var tabs = new TabControllerControl(TabControllerSurfaceKind.Detached);
        var tool = new FoundationTabControllerWindow(owner, tabs, bounds: null);

        Assert.Same(owner, tool.Owner);
        Assert.Same(tabs, tool.Content);
        Assert.Equal(480, tool.Width);
        Assert.Equal(560, tool.Height);
        Assert.Equal(360, tool.MinWidth);
        Assert.Equal(400, tool.MinHeight);
        Assert.Equal(
            SystemParameters.HighContrast ? SystemColors.WindowBrush : OrbitVisualTheme.Chrome,
            tool.Background);
        Assert.IsNotType<ResourceTaskPanelControl>(tool.Content);
        tool.CloseForOwner();
        owner.Close();
    });

    [Fact]
    public void DetachedControllerHostPreservesValidatedPersistedBounds() => RunSta(() =>
    {
        var owner = CreateOwner();
        var tabs = new TabControllerControl(TabControllerSurfaceKind.Detached);
        var bounds = new TabControllerBoundsDip(80, 90, 640, 520);
        var tool = new FoundationTabControllerWindow(owner, tabs, bounds);

        Assert.Equal(WindowStartupLocation.Manual, tool.WindowStartupLocation);
        Assert.Equal(bounds.Left, tool.Left);
        Assert.Equal(bounds.Top, tool.Top);
        Assert.Equal(bounds.Width, tool.Width);
        Assert.Equal(bounds.Height, tool.Height);
        tool.CloseForOwner();
        owner.Close();
    });

    private static Window CreateOwner()
    {
        var owner = new Window
        {
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            Width = 1,
            Height = 1,
            Left = -10000,
            Top = -10000,
        };
        owner.Show();
        owner.Hide();
        return owner;
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
