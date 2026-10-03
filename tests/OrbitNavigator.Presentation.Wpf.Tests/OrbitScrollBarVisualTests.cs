using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using OrbitNavigator.Presentation.Wpf;

using Xunit;

namespace OrbitNavigator.Presentation.Wpf.Tests;

[Collection("WPF focus-sensitive")]
public sealed class OrbitScrollBarVisualTests
{
    [Fact]
    public void NewTabScrollerUsesSlimReservedOrbitGutterWithUsableThumbAndTrackCommands() => StaTest.Run(() =>
    {
        var page = new NewTabPageControl { ReducedMotion = true };
        var window = new Window { Content = page, Width = 720, Height = 300, ShowInTaskbar = false };
        window.Show();
        try
        {
            window.UpdateLayout();
            var scroller = VisualDescendants(page).OfType<ScrollViewer>()
                .First(value => value.VerticalScrollBarVisibility == ScrollBarVisibility.Auto);
            scroller.ApplyTemplate();
            var vertical = Assert.IsType<ScrollBar>(
                scroller.Template.FindName("PART_VerticalScrollBar", scroller));
            vertical.ApplyTemplate();
            window.UpdateLayout();

            Assert.Same(page.Resources[typeof(ScrollBar)], vertical.Style);
            Assert.Equal(13, vertical.ActualWidth);
            Assert.Equal(Visibility.Visible, scroller.ComputedVerticalScrollBarVisibility);
            Assert.True(vertical.Focusable);
            var track = Assert.IsType<Track>(vertical.Template.FindName("PART_Track", vertical));
            Assert.True(track.Thumb.MinHeight >= 32);
            Assert.Equal(ScrollBar.PageUpCommand, track.DecreaseRepeatButton.Command);
            Assert.Equal(ScrollBar.PageDownCommand, track.IncreaseRepeatButton.Command);
            var beforePage = vertical.Value;
            var pageDown = Assert.IsType<RoutedCommand>(track.IncreaseRepeatButton.Command);
            Assert.True(pageDown.CanExecute(null, vertical));
            pageDown.Execute(null, vertical);
            window.UpdateLayout();
            Assert.True(vertical.Value > beforePage, "The styled track must still execute real page scrolling.");

            var presenter = Assert.IsType<ScrollContentPresenter>(
                scroller.Template.FindName("PART_ScrollContentPresenter", scroller));
            var presenterRight = presenter.TranslatePoint(new Point(presenter.ActualWidth, 0), scroller).X;
            var gutterLeft = vertical.TranslatePoint(new Point(), scroller).X;
            Assert.True(presenterRight <= gutterLeft + .5, "Scrollbar must reserve a gutter rather than overlay page content.");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void GenericOrbitScrollbarStyleSupportsBothOrientationsAndHighContrastPalette() => StaTest.Run(() =>
    {
        var host = new Grid();
        OrbitVisualTheme.ApplyScrollBarTheme(host);
        var vertical = new ScrollBar { Orientation = Orientation.Vertical, Maximum = 10, ViewportSize = 2 };
        var horizontal = new ScrollBar { Orientation = Orientation.Horizontal, Maximum = 10, ViewportSize = 2 };
        host.Children.Add(vertical);
        host.Children.Add(horizontal);
        StaTest.Prepare(host, 240, 240);

        Assert.NotNull(vertical.Template);
        Assert.NotNull(horizontal.Template);
        Assert.Equal(13, vertical.Width);
        Assert.Equal(13, horizontal.Height);
        Assert.Equal(
            SystemParameters.HighContrast ? SystemColors.ScrollBarBrush : OrbitVisualTheme.Canvas,
            vertical.Background);
        Assert.Equal(
            SystemParameters.HighContrast ? SystemColors.ControlTextBrush : OrbitVisualTheme.MutedInk,
            vertical.Foreground);
    });

    [Fact]
    public void ScrollbarThemeRefreshAndSystemListenerFollowRootLoadLifecycleWithoutDuplicates() => StaTest.Run(() =>
    {
        var root = new Grid();
        OrbitVisualTheme.ApplyScrollBarTheme(root);
        var initial = Assert.IsType<Style>(root.Resources[typeof(ScrollBar)]);
        Assert.False(OrbitVisualTheme.IsScrollBarThemeListeningForSystemChanges(root));

        OrbitVisualTheme.ApplyScrollBarTheme(root);
        var reapplied = Assert.IsType<Style>(root.Resources[typeof(ScrollBar)]);
        Assert.NotSame(initial, reapplied);
        Assert.False(OrbitVisualTheme.IsScrollBarThemeListeningForSystemChanges(root));

        var window = new Window { Content = root, Width = 320, Height = 240, ShowInTaskbar = false };
        window.Show();
        try
        {
            window.UpdateLayout();
            Assert.True(OrbitVisualTheme.IsScrollBarThemeListeningForSystemChanges(root));
            OrbitVisualTheme.ApplyScrollBarTheme(root);
            Assert.True(OrbitVisualTheme.IsScrollBarThemeListeningForSystemChanges(root));

            var beforeRefresh = Assert.IsType<Style>(root.Resources[typeof(ScrollBar)]);
            OrbitVisualTheme.RefreshScrollBarTheme(root);
            var afterRefresh = Assert.IsType<Style>(root.Resources[typeof(ScrollBar)]);
            Assert.NotSame(beforeRefresh, afterRefresh);

            window.Content = null;
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
            Assert.False(OrbitVisualTheme.IsScrollBarThemeListeningForSystemChanges(root));

            window.Content = root;
            window.UpdateLayout();
            root.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.True(OrbitVisualTheme.IsScrollBarThemeListeningForSystemChanges(root));
        }
        finally
        {
            window.Close();
        }

        root.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Assert.False(OrbitVisualTheme.IsScrollBarThemeListeningForSystemChanges(root));
    });

    [Fact]
    public void ContextMenuRendersOrbitChromeWithoutSystemGutterAndScrollsLongMenus() => StaTest.Run(() =>
    {
        var anchor = new Button { Content = "Menu", Width = 80, Height = 44 };
        var window = new Window { Content = anchor, Width = 360, Height = 260, ShowInTaskbar = false };
        var menu = new ContextMenu { PlacementTarget = anchor };
        var invoked = 0;
        for (var index = 0; index < 14; index++)
        {
            var item = new MenuItem
            {
                Header = $"Command {index + 1}",
                IsCheckable = index == 0,
                IsChecked = index == 0,
            };
            item.Click += (_, _) => invoked++;
            menu.Items.Add(item);
        }
        OrbitVisualTheme.ApplyContextMenu(menu);
        menu.MaxHeight = 176;
        anchor.ContextMenu = menu;

        window.Show();
        try
        {
            window.UpdateLayout();
            menu.IsOpen = true;
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            menu.UpdateLayout();

            var border = Assert.IsType<Border>(menu.Template.FindName("OrbitMenuBorder", menu));
            var scroller = Assert.IsType<ScrollViewer>(menu.Template.FindName("OrbitMenuScrollViewer", menu));
            var presenter = Assert.IsType<ItemsPresenter>(menu.Template.FindName("OrbitMenuItemsPresenter", menu));
            Assert.Same(menu.Background, border.Background);
            Assert.Same(menu.BorderBrush, border.BorderBrush);
            Assert.Equal(menu.BorderThickness, border.BorderThickness);
            Assert.Equal(ScrollBarVisibility.Auto, scroller.VerticalScrollBarVisibility);
            Assert.Equal(Visibility.Visible, scroller.ComputedVerticalScrollBarVisibility);
            Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetDirectionalNavigation(presenter));
            Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetTabNavigation(presenter));

            var checkedItem = Assert.IsType<MenuItem>(menu.Items[0]);
            checkedItem.ApplyTemplate();
            Assert.Equal(Visibility.Visible,
                Assert.IsType<TextBlock>(checkedItem.Template.FindName("CheckMark", checkedItem)).Visibility);
            Assert.True(checkedItem.Focusable);
            checkedItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(1, invoked);

            Assert.DoesNotContain(
                VisualDescendants(menu).OfType<Border>(),
                candidate => candidate.Background == SystemColors.ControlBrush &&
                             candidate.ActualWidth is >= 28 and <= 36);
        }
        finally
        {
            menu.IsOpen = false;
            window.Close();
        }
    });

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in VisualDescendants(child)) yield return descendant;
        }
    }
}
