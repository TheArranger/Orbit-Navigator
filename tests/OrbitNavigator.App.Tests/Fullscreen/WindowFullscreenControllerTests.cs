using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using OrbitNavigator.App.Composition;
using Xunit;

namespace OrbitNavigator.App.Tests.Fullscreen;

public sealed class WindowFullscreenControllerTests
{
    [Theory]
    [InlineData(WindowState.Normal)]
    [InlineData(WindowState.Maximized)]
    public void ExitRestoresOriginalWindowPropertiesAndChrome(WindowState originalState) => RunSta(() =>
    {
        // Deliberately never show this window. Native fullscreen must not affect
        // the interactive desktop while these state tests run.
        var window = CreateWindow();
        window.WindowState = originalState;
        var chrome = new List<bool>();
        var controller = new WindowFullscreenController(window, chrome.Add);
        var owner = new object();

        controller.Update(owner, isSelected: true, isFullscreen: true);

        Assert.True(controller.IsFullscreen);
        Assert.Same(owner, controller.Owner);
        Assert.Equal(WindowStyle.None, window.WindowStyle);
        Assert.Equal(new[] { true }, chrome);

        controller.Exit();

        Assert.False(controller.IsFullscreen);
        Assert.Null(controller.Owner);
        Assert.Equal(originalState, window.WindowState);
        Assert.Equal(WindowStyle.ThreeDBorderWindow, window.WindowStyle);
        Assert.Equal(ResizeMode.CanResizeWithGrip, window.ResizeMode);
        Assert.Equal(130, window.Left);
        Assert.Equal(140, window.Top);
        Assert.Equal(920, window.Width);
        Assert.Equal(680, window.Height);
        Assert.False(window.Topmost);
        Assert.Equal(new[] { true, false }, chrome);
        window.Close();
    });

    [Fact]
    public void BackgroundTabCannotEnterOrEndAnotherTabsFullscreen() => RunSta(() =>
    {
        var window = CreateWindow();
        var chrome = new List<bool>();
        var controller = new WindowFullscreenController(window, chrome.Add);
        var selected = new object();
        var background = new object();

        controller.Update(background, isSelected: false, isFullscreen: true);
        Assert.False(controller.IsFullscreen);
        Assert.Empty(chrome);

        controller.Update(selected, isSelected: true, isFullscreen: true);
        controller.Update(background, isSelected: false, isFullscreen: false);
        controller.Update(background, isSelected: false, isFullscreen: true);

        Assert.True(controller.IsFullscreen);
        Assert.Same(selected, controller.Owner);
        Assert.Equal(new[] { true }, chrome);

        controller.Update(selected, isSelected: true, isFullscreen: false);
        Assert.False(controller.IsFullscreen);
        Assert.Equal(new[] { true, false }, chrome);
        window.Close();
    });

    [Fact]
    public void RepeatedEventsDoNotReplaceRestoreSnapshotOrRepeatChromeChanges() => RunSta(() =>
    {
        var window = CreateWindow();
        var chrome = new List<bool>();
        var controller = new WindowFullscreenController(window, chrome.Add);
        var owner = new object();

        controller.Exit();
        controller.Update(owner, isSelected: true, isFullscreen: true);
        controller.Update(owner, isSelected: true, isFullscreen: true);
        controller.Exit();
        controller.Exit();

        Assert.Equal(new[] { true, false }, chrome);
        Assert.Equal(WindowStyle.ThreeDBorderWindow, window.WindowStyle);
        Assert.Equal(ResizeMode.CanResizeWithGrip, window.ResizeMode);
        Assert.Equal(WindowState.Normal, window.WindowState);
        Assert.Equal(920, window.Width);
        Assert.Equal(680, window.Height);
        window.Close();
    });

    [Fact]
    public void ExplicitExitForTabSwitchAllowsNextSelectedTabToEnter() => RunSta(() =>
    {
        var window = CreateWindow();
        var controller = new WindowFullscreenController(window, _ => { });
        var first = new object();
        var next = new object();

        controller.Update(first, isSelected: true, isFullscreen: true);
        controller.Exit();
        controller.Update(next, isSelected: true, isFullscreen: true);
        controller.Update(first, isSelected: false, isFullscreen: false);

        Assert.True(controller.IsFullscreen);
        Assert.Same(next, controller.Owner);
        controller.Exit();
        Assert.Equal(920, window.Width);
        Assert.Equal(680, window.Height);
        window.Close();
    });

    private static Window CreateWindow() => new()
    {
        WindowStartupLocation = WindowStartupLocation.Manual,
        WindowStyle = WindowStyle.ThreeDBorderWindow,
        ResizeMode = ResizeMode.CanResizeWithGrip,
        ShowActivated = false,
        ShowInTaskbar = false,
        Left = 130,
        Top = 140,
        Width = 920,
        Height = 680,
        Topmost = false,
    };

    private static void RunSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
