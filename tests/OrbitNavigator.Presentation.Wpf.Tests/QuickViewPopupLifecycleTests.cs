using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OrbitNavigator.Presentation.QuickView;
using OrbitNavigator.Presentation.Wpf;
using Xunit;

namespace OrbitNavigator.Presentation.Wpf.Tests;

public sealed class QuickViewPopupLifecycleTests
{
    [Fact]
    public void IdleIconUsesTransparentPopupWithoutOpaqueBackingOrGoldRing() => StaTest.Run(() =>
    {
        var control = new QuickViewControl();
        control.Apply(State(QuickViewHostState.Ready));

        Assert.True(control.OverlayPopup.AllowsTransparency);
        Assert.Equal(Brushes.Transparent, Assert.IsType<Grid>(control.OverlayPopup.Child).Background);
        Assert.IsType<OrbitIcon>(control.AnchorButton.Content);
        Assert.Equal(48, control.AnchorButton.Width);
        Assert.Equal(48, control.AnchorButton.Height);
        Assert.True(control.AnchorButton.MinWidth >= 48 && control.AnchorButton.MinHeight >= 48);
        Assert.Equal("Submit Quick View search or address", AutomationProperties.GetName(control.AnchorButton));
        Assert.False(control.IsSearchExpanded);
        Assert.False(control.IsSurfaceVisible);
        if (!SystemParameters.HighContrast)
        {
            Assert.Equal(OrbitVisualTheme.Divider, control.LauncherSurface.BorderBrush);
            var idle = Assert.IsType<LinearGradientBrush>(control.LauncherSurface.Background);
            Assert.InRange(idle.GradientStops[0].Color.A, (byte)80, (byte)160);
        }
    });

    [Fact]
    public void HoverDarkensQuietLauncherAndRevealsTheExistingSearchField() => StaTest.Run(() =>
    {
        var control = new QuickViewControl();
        control.Apply(State(QuickViewHostState.Ready));
        control.LauncherSurface.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0)
        {
            RoutedEvent = Mouse.MouseEnterEvent,
        });

        Assert.True(control.IsSearchExpanded);
        Assert.True(control.OverlayPopup.AllowsTransparency);
        if (!SystemParameters.HighContrast)
        {
            Assert.Equal(OrbitVisualTheme.SeaGlassStrong, control.LauncherSurface.BorderBrush);
            Assert.InRange(Assert.IsType<LinearGradientBrush>(control.LauncherSurface.Background).GradientStops[0].Color.A,
                (byte)220, byte.MaxValue);
        }
    });

    [Theory]
    [InlineData(QuickViewHostState.Opening)]
    [InlineData(QuickViewHostState.Open)]
    [InlineData(QuickViewHostState.Closing)]
    [InlineData(QuickViewHostState.Failed)]
    public void ActiveSurfaceAlwaysUsesTheFixedOpaquePopup(QuickViewHostState state) => StaTest.Run(() =>
    {
        var control = new QuickViewControl();
        control.Apply(State(QuickViewHostState.Ready));
        var transparent = control.OverlayPopup;
        control.Apply(State(state));

        Assert.NotSame(transparent, control.OverlayPopup);
        Assert.False(control.OverlayPopup.AllowsTransparency);
        Assert.Null(transparent.Child);
        Assert.False(transparent.IsOpen);
        Assert.True(control.IsSurfaceVisible);
        Assert.Equal(SystemParameters.HighContrast ? SystemColors.WindowBrush : OrbitVisualTheme.Canvas,
            Assert.IsType<Grid>(control.OverlayPopup.Child).Background);
    });

    [Fact]
    public void AssigningHostBeforeOpeningSelectsOpaquePopupBeforeAttachment() => StaTest.Run(() =>
    {
        var control = new QuickViewControl();
        control.Apply(State(QuickViewHostState.Ready));
        var content = new TrackingHost();

        control.WebContent = content;

        Assert.False(control.OverlayPopup.AllowsTransparency);
        Assert.Same(content, control.WebContent);
        Assert.False(content.WasDisposed);
    });

    [Fact]
    public void LiveHostAndItsPopupRemainUnchangedThroughOpenNavigationAndClosing() => StaTest.Run(() =>
    {
        var control = new QuickViewControl();
        control.Apply(State(QuickViewHostState.Opening));
        var popup = control.OverlayPopup;
        var layout = popup.Child;
        var content = new TrackingHost();
        control.WebContent = content;

        foreach (var state in new[] { QuickViewHostState.Open, QuickViewHostState.Opening, QuickViewHostState.Open, QuickViewHostState.Closing })
        {
            control.Apply(State(state));
            Assert.Same(popup, control.OverlayPopup);
            Assert.Same(layout, popup.Child);
            Assert.Same(content, control.WebContent);
            Assert.False(popup.AllowsTransparency);
            Assert.False(content.WasDisposed);
        }
    });

    [Fact]
    public void ReturningReadyDetachesHostBeforeRestoringTransparentLauncherAndPreservesOwnership() => StaTest.Run(() =>
    {
        var control = new QuickViewControl();
        control.Apply(State(QuickViewHostState.Ready));
        var transparent = control.OverlayPopup;
        control.Apply(State(QuickViewHostState.Opening));
        var opaque = control.OverlayPopup;
        var content = new TrackingHost();
        control.WebContent = content;
        control.Apply(State(QuickViewHostState.Open));
        control.Apply(State(QuickViewHostState.Closing));
        control.WebContent = null;
        Assert.Same(opaque, control.OverlayPopup);

        control.Apply(State(QuickViewHostState.Ready));

        Assert.Null(control.WebContent);
        Assert.Same(transparent, control.OverlayPopup);
        Assert.True(transparent.AllowsTransparency);
        Assert.False(opaque.AllowsTransparency);
        Assert.Null(opaque.Child);
        Assert.False(content.WasDisposed); // App owns host disposal; presentation never disposes it.
        Assert.False(control.IsSearchExpanded);
    });

    [Fact]
    public void FullscreenSuppressionAndUnsuppressionNeverMoveOrDropLiveHost() => StaTest.Run(() =>
    {
        var control = new QuickViewControl();
        control.Apply(State(QuickViewHostState.Opening));
        var content = new TrackingHost();
        control.WebContent = content;
        control.Apply(State(QuickViewHostState.Open));
        var opaque = control.OverlayPopup;
        var layout = opaque.Child;

        control.IsOverlaySuppressed = true;
        Assert.All(control.Children.OfType<Popup>(), popup => Assert.False(popup.IsOpen));
        control.Apply(State(QuickViewHostState.Open));
        control.IsOverlaySuppressed = false;

        Assert.Same(opaque, control.OverlayPopup);
        Assert.Same(layout, opaque.Child);
        Assert.Same(content, control.WebContent);
        Assert.False(opaque.AllowsTransparency);
        Assert.False(content.WasDisposed);
    });

    [Fact]
    public void MovingEmptyLayoutBetweenPopupModesPreservesTypedSearchText() => StaTest.Run(() =>
    {
        var control = new QuickViewControl();
        control.Apply(State(QuickViewHostState.Ready));
        control.SearchBox.Text = "orbit privacy";
        control.SearchBox.Visibility = Visibility.Visible;

        control.Apply(State(QuickViewHostState.Opening));

        Assert.Equal("orbit privacy", control.SearchBox.Text);
        Assert.True(control.IsSearchExpanded);
        Assert.False(control.OverlayPopup.AllowsTransparency);
    });

    [Fact]
    [Trait("Category", "InteractiveDesktop")]
    public void LoadedCloseRestoresIconOnlyLauncherWidthAndLowerRightAnchor() => StaTest.Run(() =>
    {
        var control = new QuickViewControl { ReducedMotion = true };
        control.ApplyOwnerViewport(new(1200, 800));
        control.Apply(State(QuickViewHostState.Ready));
        var window = new Window { Content = control, Width = 1200, Height = 800, ShowInTaskbar = false };
        window.Show();
        try
        {
            DrainLayout();
            var idleWidth = control.LauncherSurface.ActualWidth;
            var idleRight = control.AnchorButton.PointToScreen(new Point(control.AnchorButton.ActualWidth, 0)).X;
            Assert.InRange(idleWidth, 53, 55);

            Assert.True(control.AnchorButton.Focus());
            control.SearchBox.Text = "orbit privacy";
            Assert.True(control.SearchBox.Focus());
            control.Apply(State(QuickViewHostState.Opening));
            control.WebContent = new TrackingHost();
            control.Apply(State(QuickViewHostState.Open));
            DrainLayout();
            Assert.False(control.OverlayPopup.AllowsTransparency);
            Assert.True(control.LauncherSurface.ActualWidth > idleWidth);

            StaTest.FindByAutomationName<Button>(control, "Close Quick View").Focus();
            control.Apply(State(QuickViewHostState.Closing));
            control.WebContent = null;
            control.ResetForFreshUse();
            control.Apply(State(QuickViewHostState.Ready));
            DrainLayout();

            Assert.True(control.OverlayPopup.AllowsTransparency);
            Assert.True(control.OverlayPopup.IsOpen);
            Assert.False(control.IsSearchExpanded);
            Assert.False(control.IsSurfaceVisible);
            Assert.Null(control.WebContent);
            Assert.InRange(control.LauncherSurface.ActualWidth, idleWidth - 1, idleWidth + 1);
            Assert.InRange(Assert.IsType<Grid>(control.OverlayPopup.Child).ActualWidth, idleWidth - 1, idleWidth + 1);
            Assert.InRange(control.AnchorButton.PointToScreen(new Point(control.AnchorButton.ActualWidth, 0)).X,
                idleRight - 2, idleRight + 2);
            Assert.All(control.Children.OfType<Popup>().Where(popup => !ReferenceEquals(popup, control.OverlayPopup)),
                popup => Assert.False(popup.IsOpen));
        }
        finally
        {
            window.Close();
        }

        static void DrainLayout() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    });

    private static QuickViewPresentation State(QuickViewHostState state) => new(
        1, false, true, state, "Reference", new Uri("https://example.test/"),
        QuickViewStateTransferCapability.AddressReloadOnly, "Quick View", []);

    private sealed class TrackingHost : Border, IDisposable
    {
        public bool WasDisposed { get; private set; }
        public void Dispose() => WasDisposed = true;
    }
}
