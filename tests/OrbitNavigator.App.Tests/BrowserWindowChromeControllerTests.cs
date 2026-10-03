using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Shell;
using System.Windows.Threading;
using OrbitNavigator.App.Composition;
using Xunit;

namespace OrbitNavigator.App.Tests;

public sealed class BrowserWindowChromeControllerTests
{
    [Fact]
    public void CustomCaptionRemovesNativeTitlebarButKeepsNativeResizeFrame() => RunSta(() =>
    {
        var window = CreateWindow();
        using var controller = new BrowserWindowChromeController(window, _ => { });

        Assert.Equal(WindowStyle.None, window.WindowStyle);
        Assert.Equal(ResizeMode.CanResize, window.ResizeMode);
        var chrome = WindowChrome.GetWindowChrome(window);
        Assert.NotNull(chrome);
        Assert.Equal(0, chrome.CaptionHeight);
        Assert.Equal(new Thickness(6), chrome.ResizeBorderThickness);
        Assert.Equal(new Thickness(0), chrome.GlassFrameThickness);
        Assert.False(chrome.UseAeroCaptionButtons);
        Assert.False(window.AllowsTransparency);
        window.Close();
    });

    [Fact]
    public void CaptionCommandsMinimizeMaximizeRestoreAndCloseWithoutASecondTitleRow() => RunSta(() =>
    {
        var window = CreateWindow();
        using var controller = new BrowserWindowChromeController(window, _ => { });

        controller.ToggleMaximizeRestore();
        Assert.Equal(WindowState.Maximized, window.WindowState);
        controller.ToggleMaximizeRestore();
        Assert.Equal(WindowState.Normal, window.WindowState);
        Assert.Equal(920, window.Width);
        Assert.Equal(680, window.Height);
        controller.Minimize();
        Assert.Equal(WindowState.Minimized, window.WindowState);
        var closed = false;
        window.Closed += (_, _) => closed = true;
        controller.Close();
        Assert.True(closed);
    });

    [Theory]
    [InlineData(WindowState.Normal)]
    [InlineData(WindowState.Maximized)]
    public void FullscreenTemporarilySuspendsCustomFrameAndRestoresPriorState(WindowState initialState) => RunSta(() =>
    {
        var window = CreateWindow();
        using var caption = new BrowserWindowChromeController(window, _ => { });
        window.WindowState = initialState;
        var originalChrome = WindowChrome.GetWindowChrome(window);
        var applications = new List<bool>();
        var fullscreen = new WindowFullscreenController(window, enabled =>
        {
            caption.ApplyFullscreen(enabled);
            applications.Add(enabled);
        });

        fullscreen.Update(new object(), isSelected: true, isFullscreen: true);

        Assert.Null(WindowChrome.GetWindowChrome(window));
        Assert.Equal(WindowState.Maximized, window.WindowState);
        Assert.Equal(ResizeMode.NoResize, window.ResizeMode);
        caption.Minimize();
        caption.ToggleMaximizeRestore();
        Assert.Equal(WindowState.Maximized, window.WindowState);

        fullscreen.Exit();

        Assert.Same(originalChrome, WindowChrome.GetWindowChrome(window));
        Assert.Equal(initialState, window.WindowState);
        Assert.Equal(WindowStyle.None, window.WindowStyle);
        Assert.Equal(ResizeMode.CanResize, window.ResizeMode);
        Assert.Equal(130, window.Left);
        Assert.Equal(140, window.Top);
        Assert.Equal(920, window.Width);
        Assert.Equal(680, window.Height);
        Assert.Equal(new[] { true, false }, applications);
        window.Close();
    });

    [Theory]
    [InlineData(0, 0, 1920, 1080, 0, 0, 1920, 1040, 0, 0)]
    [InlineData(-1920, 0, 1920, 1080, -1880, 0, 1880, 1080, 40, 0)]
    [InlineData(0, -1440, 2560, 1440, 0, -1400, 2560, 1400, 0, 40)]
    [InlineData(1920, 0, 3840, 2160, 1920, 0, 3840, 2100, 0, 0)]
    public void MaximizeUsesPerMonitorWorkAreaIncludingNegativeCoordinatesAndHighDpiPixels(
        int mx, int my, int mw, int mh, int wx, int wy, int ww, int wh, int expectedX, int expectedY)
    {
        var bounds = BrowserWindowChromeController.GetMaximizedBounds(
            new Rect(mx, my, mw, mh), new Rect(wx, wy, ww, wh), fullscreen: false);

        Assert.Equal(new Rect(expectedX, expectedY, ww, wh), bounds);
    }

    [Fact]
    public void FullscreenUsesWholeMonitorInsteadOfTaskbarWorkArea()
    {
        var bounds = BrowserWindowChromeController.GetMaximizedBounds(
            new Rect(-2560, -1440, 2560, 1440), new Rect(-2520, -1440, 2520, 1400), fullscreen: true);

        Assert.Equal(new Rect(0, 0, 2560, 1440), bounds);
    }

    [Theory]
    [InlineData(1, 1, 640, 480)]
    [InlineData(1.5, 1.5, 960, 720)]
    [InlineData(2, 2, 1280, 960)]
    public void NativeResizePreservesMinimumDipBoundsAtEachMonitorScale(double scaleX, double scaleY, double width, double height)
    {
        Assert.Equal(new Size(width, height), BrowserWindowChromeController.GetMinimumTrackSize(640, 480, scaleX, scaleY));
    }

    [Theory]
    [InlineData(WindowState.Normal, false, 6)]
    [InlineData(WindowState.Maximized, false, 0)]
    [InlineData(WindowState.Normal, true, 0)]
    [InlineData(WindowState.Maximized, true, 0)]
    public void ClientInsetMatchesNativeResizeBorderOnlyWhileWindowed(WindowState state, bool fullscreen, double inset)
    {
        Assert.Equal(new Thickness(inset), BrowserWindowChromeController.GetContentInset(state, fullscreen));
    }

    [Fact]
    public void NormalClientRightEdgeDoesNotOverlapNativeResizeHitRegion() => RunSta(() =>
    {
        var client = new System.Windows.Controls.Grid
        {
            Margin = BrowserWindowChromeController.GetContentInset(WindowState.Normal, fullscreen: false),
        };
        var root = new System.Windows.Controls.Grid { Width = 1200, Height = 800 };
        root.Children.Add(client);
        root.Measure(new Size(1200, 800));
        root.Arrange(new Rect(0, 0, 1200, 800));

        var rightEdge = client.TranslatePoint(new Point(client.ActualWidth, 0), root).X;
        Assert.Equal(1194, rightEdge);
        Assert.Equal(1188, client.ActualWidth);
        Assert.True(rightEdge <= root.ActualWidth - BrowserWindowChromeController.ResizeBorderWidth);
    });

    [Fact]
    public void ControllerAppliesAndRestoresClientInsetAcrossFullscreen() => RunSta(() =>
    {
        var window = CreateWindow();
        var insets = new List<Thickness>();
        using var controller = new BrowserWindowChromeController(window, _ => { }, insets.Add);
        Assert.Equal(new Thickness(6), insets.Last());

        controller.ApplyFullscreen(true);
        Assert.Equal(new Thickness(0), insets.Last());
        controller.ApplyFullscreen(false);
        Assert.Equal(new Thickness(6), insets.Last());
        window.Close();
    });

    private static Window CreateWindow() => new()
    {
        // These tests never Show/EnsureHandle or manipulate the interactive desktop.
        WindowStartupLocation = WindowStartupLocation.Manual,
        ShowActivated = false,
        ShowInTaskbar = false,
        Left = 130,
        Top = 140,
        Width = 920,
        Height = 680,
    };

    private static void RunSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The non-displaying STA window test exceeded its deadline.");
        failure?.Throw();
    }
}
