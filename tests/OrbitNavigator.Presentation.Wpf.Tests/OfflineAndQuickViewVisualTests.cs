using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using OrbitNavigator.Presentation.Offline;
using OrbitNavigator.Presentation.QuickView;
using OrbitNavigator.Presentation.Wpf;

using Xunit;

namespace OrbitNavigator.Presentation.Wpf.Tests;

public sealed class OfflineAndQuickViewVisualTests
{
    [Fact]
    public void OfflineLibraryShowsLocalMetadataAndRoutesOpenDeleteAndSave() => StaTest.Run(() =>
    {
        var item = new OfflineReadingItemPresentation(
            new(Guid.NewGuid()),
            "Orbit field guide",
            new Uri("https://guide.test/field"),
            DateTimeOffset.UtcNow.AddMinutes(-20),
            1_572_864);
        var control = new OfflineLibraryControl();
        var actions = new List<OfflineReadingAction>();
        control.ActionRequested += (_, args) => actions.Add(args.Action);
        control.Apply(new(
            7,
            false,
            true,
            "Live guide",
            new Uri("https://guide.test/live"),
            false,
            "Ready to save a local copy.",
            [item]));
        StaTest.Prepare(control, 900, 620);

        Assert.Equal(1, control.VisibleItemCount);
        Assert.Contains(StaTest.Descendants(control).OfType<TextBlock>(), text =>
            text.Text.Contains("1.5 MB", StringComparison.Ordinal) &&
            text.Text.Contains("Offline copy", StringComparison.Ordinal));
        Assert.Contains(StaTest.Descendants(control).OfType<TextBlock>(), text =>
            text.Text.Contains("not live websites", StringComparison.OrdinalIgnoreCase));

        var save = StaTest.FindByAutomationName<Button>(control, "Save current page for offline");
        var open = StaTest.FindByAutomationName<Button>(control, "Open offline copy — Orbit field guide");
        var delete = StaTest.FindByAutomationName<Button>(control, "Delete offline copy — Orbit field guide");
        Assert.All(new[] { save, open, delete }, button =>
        {
            Assert.True(button.IsEnabled);
            Assert.True(button.MinWidth >= 44 && button.MinHeight >= 44);
        });
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        delete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Collection(actions,
            action => Assert.IsType<SavePageForOfflineAction>(action),
            action => Assert.IsType<OpenOfflineReadingItemAction>(action),
            action => Assert.IsType<DeleteOfflineReadingItemAction>(action));
    });

    [Fact]
    public void OfflineLibraryPrivateStateIsEmptyAndCannotSave() => StaTest.Run(() =>
    {
        var control = new OfflineLibraryControl();
        control.ActionRequested += (_, _) => { };
        control.Apply(OfflineReadingCatalogPresentation.Unavailable(
            true,
            "Offline reading is unavailable in private browsing."));
        StaTest.Prepare(control, 700, 500);

        Assert.Equal(0, control.VisibleItemCount);
        Assert.False(StaTest.FindByAutomationName<Button>(control, "Save current page for offline").IsEnabled);
        Assert.Contains("private", AutomationProperties.GetItemStatus(control), StringComparison.OrdinalIgnoreCase);
    });

    [Fact]
    public void QuickViewNormalSiteAnchorOpensResizableSurfaceAndRoutesActions() => StaTest.Run(() =>
    {
        var control = new QuickViewControl { ReducedMotion = true };
        var actions = new List<QuickViewAction>();
        control.ActionRequested += (_, args) => actions.Add(args.Action);
        control.ApplyOwnerViewport(new(1200, 800));
        control.Apply(State(QuickViewHostState.Ready));
        var window = new Window
        {
            Content = control,
            Width = 900,
            Height = 700,
            ShowInTaskbar = false,
        };
        window.Show();
        try
        {
            window.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

            Assert.True(control.IsAnchorVisible);
            Assert.True(control.UsesNativeOverlayPopup);
            Assert.True(control.OverlayPopup.IsOpen);
            Assert.False(control.IsSurfaceVisible);
            Assert.InRange(control.CurrentSurfaceSize.Width, 1200 * .295, 1200 * .305);
            Assert.InRange(control.CurrentSurfaceSize.Height, 800 * .295, 800 * .305);
            var anchor = control.AnchorButton;
            Assert.True(anchor.MinWidth >= 44 && anchor.MinHeight >= 44);
            anchor.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsType<OpenQuickViewAction>(Assert.Single(actions));

            var content = new Border { Background = System.Windows.Media.Brushes.Navy };
            control.WebContent = content;
            control.Apply(State(
                QuickViewHostState.Open,
                QuickViewStateTransferCapability.PreserveCurrentPageState));
            window.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Assert.True(control.IsSurfaceVisible);
            Assert.Same(content, control.WebContent);
            Assert.InRange(control.WebContentHost.ActualWidth, 1, 900);

            var expand = StaTest.FindByAutomationName<Button>(control.OverlayPopup, "Expand to normal tab");
            Assert.Contains("preserving", AutomationProperties.GetHelpText(expand), StringComparison.OrdinalIgnoreCase);
            expand.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(Assert.IsType<ExpandQuickViewToTabAction>(actions[1]).PreferStateTransfer);
            StaTest.FindByAutomationName<Button>(control.OverlayPopup, "Close Quick View")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsType<CloseQuickViewAction>(actions[2]);

            control.Apply(State(QuickViewHostState.Ready));
            Assert.Null(control.WebContent);
            Assert.False(control.IsSearchExpanded);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void QuickViewPrivateOrNonSiteStateHasNoAnchorAndResetClearsWebContent() => StaTest.Run(() =>
    {
        var control = new QuickViewControl();
        control.WebContent = new Border();
        control.Apply(QuickViewPresentation.Unavailable(
            true,
            "Quick View is unavailable in private browsing."));
        StaTest.Prepare(control, 700, 500);

        Assert.False(control.IsAnchorVisible);
        Assert.False(control.IsSurfaceVisible);
        control.ResetForFreshUse();
        Assert.Null(control.WebContent);
        Assert.False(control.IsSearchExpanded);
    });

    [Fact]
    public void QuickViewKeyboardFocusExpandsSearchAndEnterEmitsTypedOpen() => StaTest.Run(() =>
    {
        var control = new QuickViewControl { ReducedMotion = true };
        var actions = new List<QuickViewAction>();
        control.ActionRequested += (_, args) => actions.Add(args.Action);
        control.Apply(State(QuickViewHostState.Ready));
        var window = new Window
        {
            Content = control,
            Width = 900,
            Height = 650,
            ShowInTaskbar = false,
        };
        window.Show();
        try
        {
            window.UpdateLayout();
            var anchor = control.AnchorButton;
            Assert.True(anchor.Focus());
            Assert.True(control.IsSearchExpanded);
            var search = StaTest.FindByAutomationName<TextBox>(control, "Quick View search or address");
            search.Text = "orbit privacy";
            search.Focus();
            var enter = new KeyEventArgs(
                Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(search)!,
                Environment.TickCount,
                Key.Enter)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            };
            search.RaiseEvent(enter);

            Assert.True(enter.Handled);
            Assert.Equal("orbit privacy", Assert.IsType<OpenQuickViewAction>(Assert.Single(actions)).Query);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Input);
            Assert.True(search.IsKeyboardFocused);
            Assert.Contains("Opening", AutomationProperties.GetItemStatus(search), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void QuickViewPhysicalEnterAndVisibleSearchButtonNavigateTheActiveSurface() => StaTest.Run(() =>
    {
        var control = new QuickViewControl { ReducedMotion = true };
        var actions = new List<QuickViewAction>();
        control.ActionRequested += (_, args) => actions.Add(args.Action);
        control.ApplyOwnerViewport(new(1200, 800));
        control.WebContent = new Border { Background = System.Windows.Media.Brushes.Navy };
        control.Apply(State(QuickViewHostState.Open));
        var window = new Window
        {
            Content = control,
            Width = 1200,
            Height = 800,
            ShowInTaskbar = false,
        };
        window.Show();
        try
        {
            window.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            var search = control.SearchBox;
            search.Visibility = Visibility.Visible;
            search.Text = "example.test/changed";
            Assert.True(search.Focus());
            var enter = new KeyEventArgs(
                Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(search)!,
                Environment.TickCount,
                Key.Enter)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            };
            search.RaiseEvent(enter);

            var entered = Assert.IsType<NavigateQuickViewAction>(Assert.Single(actions));
            Assert.Equal("example.test/changed", entered.Query);
            Assert.True(enter.Handled);
            Assert.Contains("Navigating", AutomationProperties.GetItemStatus(search), StringComparison.Ordinal);

            actions.Clear();
            search.Text = "orbit browser privacy";
            control.AnchorButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var clicked = Assert.IsType<NavigateQuickViewAction>(Assert.Single(actions));
            Assert.Equal("orbit browser privacy", clicked.Query);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Input);
            Assert.True(search.IsKeyboardFocused);

            control.Apply(State(QuickViewHostState.Open) with
            {
                Revision = 6,
                Address = new Uri("https://example.test/changed"),
                Title = "Changed result",
            });
            Assert.Contains("example.test", AutomationProperties.GetItemStatus(search), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    [Trait("Category", "InteractiveDesktop")]
    public void BrowserChromePlacesQuickViewAnchorAtLowerLeftAndForwardsActions() => StaTest.Run(() =>
    {
        var chrome = new BrowserChromeControl();
        QuickViewAction? requested = null;
        chrome.QuickViewActionRequested += (_, args) => requested = args.Action;
        chrome.ApplyQuickView(State(QuickViewHostState.Ready));
        var window = new Window
        {
            Content = chrome,
            Width = 1200,
            Height = 800,
            ShowInTaskbar = false,
        };
        window.Show();
        try
        {
            window.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            var anchor = chrome.QuickView.AnchorButton;
            var chromeOrigin = chrome.PointToScreen(new Point());
            var position = anchor.PointToScreen(new Point());
            Assert.True(position.X - chromeOrigin.X < 180,
                $"Expected lower-left anchor, actual X={position.X - chromeOrigin.X}.");
            Assert.True(position.Y - chromeOrigin.Y > 650,
                $"Expected lower-left anchor, actual Y={position.Y - chromeOrigin.Y}.");
            Assert.True(chrome.QuickView.OverlayPopup.IsOpen);
            Assert.NotSame(PresentationSource.FromVisual(chrome), PresentationSource.FromVisual(anchor));
            anchor.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsType<OpenQuickViewAction>(requested);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void CaptureQuickViewThirtyPercentFootprintWhenRequested() => StaTest.Run(() =>
    {
        var output = Environment.GetEnvironmentVariable("ORBIT_CAPTURE_QUICK_VIEW_DIR");
        if (string.IsNullOrWhiteSpace(output))
        {
            return;
        }

        Directory.CreateDirectory(output);
        var control = new QuickViewControl { ReducedMotion = true };
        control.ActionRequested += (_, _) => { };
        control.ApplyOwnerViewport(new(1200, 800));
        control.WebContent = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(17, 35, 48)),
            Child = new TextBlock
            {
                Text = "Quick View result",
                Foreground = Brushes.White,
                FontSize = 26,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        control.Apply(State(QuickViewHostState.Open));
        var window = new Window
        {
            Content = control,
            Width = 1200,
            Height = 800,
            Left = -30000,
            Top = -30000,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        window.Show();
        try
        {
            window.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);
            var visual = Assert.IsAssignableFrom<FrameworkElement>(control.OverlayPopup.Child);
            var width = Math.Max(1, (int)Math.Ceiling(visual.ActualWidth));
            var height = Math.Max(1, (int)Math.Ceiling(visual.ActualHeight));
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output, "quick-view-open-30-percent.png"));
            encoder.Save(stream);
        }
        finally
        {
            window.Close();
        }
    });

    private static QuickViewPresentation State(
        QuickViewHostState state,
        QuickViewStateTransferCapability transfer = QuickViewStateTransferCapability.AddressReloadOnly) => new(
        5,
        false,
        true,
        state,
        "Reference",
        new Uri("https://reference.test/"),
        transfer,
        state == QuickViewHostState.Open ? "Quick View is open." : "Quick View is ready.",
        [new("home", "Home", new Uri("https://reference.test/"))]);
}
