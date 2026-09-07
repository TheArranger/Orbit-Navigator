using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Presentation.Tabs;
using OrbitNavigator.Presentation.Workspace;
using OrbitNavigator.Presentation.Wpf;

using Xunit;

namespace OrbitNavigator.Presentation.Wpf.Tests;

[Collection("WPF focus-sensitive")]
public sealed class TabControllerVisualTests
{
    [Theory]
    [InlineData(TabStripPlacement.Top, 640, 120)]
    [InlineData(TabStripPlacement.Left, 224, 640)]
    [InlineData(TabStripPlacement.Right, 224, 640)]
    public void ManyLongTabsUsePinnedNonOverlayOverflow(
        TabStripPlacement placement,
        double width,
        double height) => StaTest.Run(() =>
    {
        var control = new TabControllerControl();
        var session = Session(placement, 30);
        control.Bind(session);
        StaTest.Prepare(control, width, height);

        Assert.True(control.HiddenTabCount > 0);
        Assert.False(control.HasOverlayScrollbar);
        var overflow = Assert.Single(StaTest.Descendants(control).OfType<ScrollBar>());
        Assert.Equal(Visibility.Visible, overflow.Visibility);
        Assert.Equal(
            placement is TabStripPlacement.Left or TabStripPlacement.Right
                ? Orientation.Vertical
                : Orientation.Horizontal,
            overflow.Orientation);
        var selected = StaTest.Descendants(control).OfType<Button>()
            .Single(button => AutomationProperties.GetName(button).StartsWith("Selected tab", StringComparison.Ordinal));
        Assert.True(selected.ActualWidth >= 44);
        Assert.True(selected.ActualHeight >= 44);
        selected.ApplyTemplate();
        var visibleCard = Assert.IsType<Border>(selected.Template.FindName("Border", selected));
        Assert.InRange(visibleCard.ActualHeight, 30, 34);
        Assert.True(StaTest.FindByAutomationName<Button>(control, "Previous tabs").MinWidth >= 44);
        Assert.True(StaTest.FindByAutomationName<Button>(control, "Next tabs").MinHeight >= 44);
        Assert.Contains(StaTest.Descendants(control).OfType<Button>(),
            button => AutomationProperties.GetName(button).StartsWith("More tabs —", StringComparison.Ordinal));
        Assert.NotNull(StaTest.FindByAutomationName<Button>(control, "Pop out tab controller"));
    });

    [Theory]
    [InlineData(TabStripPlacement.Top, 640, 90)]
    [InlineData(TabStripPlacement.Left, 224, 640)]
    [InlineData(TabStripPlacement.Right, 224, 640)]
    public void OverflowScrollbarUsesReservedGutterAndNeverIntersectsCloseTargets(
        TabStripPlacement placement,
        double width,
        double height) => StaTest.Run(() =>
    {
        var control = new TabControllerControl();
        control.Bind(Session(placement, 36));
        var window = new Window { Content = control, Width = width, Height = height, ShowInTaskbar = false };
        window.Show();
        try
        {
            window.UpdateLayout();
            var scrollbar = control.OverflowScrollBar;
            Assert.Equal(Visibility.Visible, scrollbar.Visibility);
            Assert.True(scrollbar.ActualWidth > 0 && scrollbar.ActualHeight > 0);
            var scrollbarBounds = BoundsIn(scrollbar, control);
            var closes = StaTest.Descendants(control).OfType<Button>()
                .Where(button => AutomationProperties.GetName(button)
                    .StartsWith("Close tab —", StringComparison.Ordinal))
                .ToArray();
            Assert.NotEmpty(closes);
            Assert.All(closes, close =>
            {
                var closeBounds = BoundsIn(close, control);
                var overlapWidth = Math.Min(scrollbarBounds.Right, closeBounds.Right) -
                                   Math.Max(scrollbarBounds.Left, closeBounds.Left);
                var overlapHeight = Math.Min(scrollbarBounds.Bottom, closeBounds.Bottom) -
                                    Math.Max(scrollbarBounds.Top, closeBounds.Top);
                Assert.True(
                    overlapWidth <= 0 || overlapHeight <= 0,
                    $"Scrollbar {scrollbarBounds} overlaps close {closeBounds}.");
            });
            if (placement is TabStripPlacement.Left or TabStripPlacement.Right)
            {
                Assert.Equal(Orientation.Vertical, scrollbar.Orientation);
                Assert.Equal(0, Grid.GetColumn(scrollbar));
                Assert.All(closes, close => Assert.True(BoundsIn(close, control).Left >= scrollbarBounds.Right));
            }
            else
            {
                Assert.Equal(Orientation.Horizontal, scrollbar.Orientation);
                Assert.Equal(1, Grid.GetRow(scrollbar));
                Assert.All(closes, close => Assert.True(BoundsIn(close, control).Bottom <= scrollbarBounds.Top));
            }
        }
        finally
        {
            window.Close();
        }
    });

    [Theory]
    [InlineData(TabStripPlacement.Top, 640, 90)]
    [InlineData(TabStripPlacement.Left, 224, 640)]
    [InlineData(TabStripPlacement.Right, 224, 640)]
    public void MouseWheelMovesOverflowViewportWithoutChangingAuthoritativeTabs(
        TabStripPlacement placement,
        double width,
        double height) => StaTest.Run(() =>
    {
        var session = Session(placement, 40);
        var authoritativeEntries = session.Current!.Projection.Tabs.Entries.ToArray();
        var control = new TabControllerControl();
        control.Bind(session);
        var window = new Window { Content = control, Width = width, Height = height, ShowInTaskbar = false };
        window.Show();
        try
        {
            window.UpdateLayout();
            var before = control.OverflowScrollBar.Value;
            Assert.True(before > 0);
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
            {
                RoutedEvent = Mouse.PreviewMouseWheelEvent,
                Source = control,
            };
            control.RaiseEvent(wheel);
            control.UpdateLayout();

            Assert.True(wheel.Handled);
            Assert.Equal(before - 1, control.OverflowScrollBar.Value);
            Assert.True(control.HasActiveViewportTransition);
            Assert.Equal(authoritativeEntries, session.Current!.Projection.Tabs.Entries);
            Assert.Contains(StaTest.Descendants(control).OfType<Button>(), button =>
                button.Tag is BrowserTabEntry && button.Visibility == Visibility.Visible);
            Assert.Contains(StaTest.Descendants(control).OfType<Button>(), button =>
                button.Tag is BrowserTabEntry tab && tab.IsSelected && button.Visibility == Visibility.Visible);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void TabGroupUsesEmberStatusWithoutReplacingActionSemantics() => StaTest.Run(() =>
    {
        var windowId = new BrowserWindowId(Guid.NewGuid());
        var groupId = new BrowserTabGroupId(Guid.NewGuid());
        var tabId = new BrowserTabId(Guid.NewGuid());
        var session = new TabControllerPresentationSession(windowId, false, new Sink());
        session.AcceptProjection(new(
            windowId,
            false,
            1,
            TabControllerHostState.Docked,
            TabStripPlacement.Top,
            new(windowId, tabId,
            [
                new TabGroupHeaderEntry(groupId, "Research", 1, false, true),
                new BrowserTabEntry(tabId, groupId, "Selected", new Uri("https://research.test/"),
                    BrowserLoadState.Idle, true, false, false, false),
            ])));
        var control = new TabControllerControl { ReducedMotion = true };
        control.Bind(session);
        StaTest.Prepare(control, 720, 90);

        var groupButton = StaTest.Descendants(control).OfType<Button>()
            .Single(button => AutomationProperties.GetName(button)
                .StartsWith("Research, group", StringComparison.Ordinal));
        Assert.Contains("expanded", AutomationProperties.GetName(groupButton), StringComparison.Ordinal);
        var star = Assert.Single(StaTest.Descendants(groupButton).OfType<OrbitEmberStar>());
        Assert.Equal(OrbitEmberStarKind.TabGroup, star.Kind);
        Assert.True(star.IsActive);
        Assert.True(star.ReducedMotion);
    });

    [Fact]
    public void HoverDefaultExpandsOneToFourTabGroupInlineWithoutMutatingAuthoritativeState() => StaTest.Run(() =>
    {
        var windowId = new BrowserWindowId(Guid.NewGuid());
        var groupId = new BrowserTabGroupId(Guid.NewGuid());
        var selected = new BrowserTabId(Guid.NewGuid());
        var previewIds = Enumerable.Range(0, 3).Select(_ => new BrowserTabId(Guid.NewGuid())).ToArray();
        var header = new TabGroupHeaderEntry(groupId, "Planning", 3, true, false)
        {
            TabIds = previewIds,
            TabPreviews = previewIds.Select((id, index) => new TabGroupPreviewItemPresentation(
                id,
                $"Planning tab {index + 1}",
                new Uri($"https://planning{index + 1}.test/"),
                ReadOnlyMemory<byte>.Empty)).ToArray(),
        };
        TabStripEntry[] canonical =
        [
            header,
            new BrowserTabEntry(selected, null, "Selected", new Uri("https://selected.test/"),
                BrowserLoadState.Idle, true, false, false, false),
        ];
        var session = new TabControllerPresentationSession(windowId, false, new Sink());
        session.AcceptProjection(new(
            windowId, false, 1, TabControllerHostState.Docked, TabStripPlacement.Top,
            new(windowId, selected, canonical))
        {
            PreviewRevealMode = WorkspacePreviewRevealMode.Hover,
        });
        var control = new TabControllerControl { ReducedMotion = true };
        control.Bind(session);
        StaTest.Prepare(control, 900, 100);

        var groupButton = StaTest.FindByAutomationName<Button>(control,
            "Planning, group, 3 tabs, collapsed");
        groupButton.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
        {
            RoutedEvent = Mouse.MouseEnterEvent,
            Source = groupButton,
        });
        control.UpdateLayout();

        Assert.Equal(groupId, control.ActiveHoverPreviewGroupId);
        Assert.True(control.IsInlineGroupPreviewVisible);
        Assert.Equal(3, StaTest.Descendants(control).OfType<Button>()
            .Count(button => AutomationProperties.GetItemStatus(button) == "Hover preview"));
        Assert.Equal(canonical, session.Current!.Projection.Tabs.Entries);

        session.AcceptProjection(new(
            windowId, false, 2, TabControllerHostState.Docked, TabStripPlacement.Top,
            new(windowId, selected, canonical))
        {
            PreviewRevealMode = WorkspacePreviewRevealMode.Click,
        });
        control.UpdateLayout();
        groupButton = StaTest.FindByAutomationName<Button>(control,
            "Planning, group, 3 tabs, collapsed");
        groupButton.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
        {
            RoutedEvent = Mouse.MouseEnterEvent,
            Source = groupButton,
        });
        Assert.Null(control.ActiveHoverPreviewGroupId);
        Assert.False(control.IsInlineGroupPreviewVisible);
    });

    [Theory]
    [InlineData(TabStripPlacement.Left)]
    [InlineData(TabStripPlacement.Right)]
    public void SideRailsUseCompactSideBySideCommandsAndScrollbarInsteadOfDuplicatePaging(
        TabStripPlacement placement) => StaTest.Run(() =>
    {
        var control = new TabControllerControl();
        control.Bind(Session(placement, 30));
        StaTest.Prepare(control, 176, 640);

        Assert.Equal(Visibility.Collapsed,
            StaTest.FindByAutomationName<Button>(control, "Previous tabs").Visibility);
        Assert.Equal(Visibility.Collapsed,
            StaTest.FindByAutomationName<Button>(control, "Next tabs").Visibility);
        var commands = new[] { "More tabs", "Open new tab", "Browser resources", "Pop out tab controller" }
            .Select(prefix => StaTest.Descendants(control).OfType<Button>()
                .Single(button => AutomationProperties.GetName(button).StartsWith(prefix, StringComparison.Ordinal)))
            .ToArray();
        Assert.All(commands, command =>
        {
            Assert.Equal(Visibility.Visible, command.Visibility);
            Assert.True(command.MinWidth >= 44);
        });
        var top = commands[0].TranslatePoint(new Point(), control).Y;
        Assert.All(commands, command => Assert.InRange(
            Math.Abs(command.TranslatePoint(new Point(), control).Y - top), 0, 1));
        Assert.Equal(Orientation.Vertical, control.OverflowScrollBar.Orientation);
    });

    [Theory]
    [InlineData(TabStripPlacement.Left, false, 208, 1)]
    [InlineData(TabStripPlacement.Right, false, 360, 2)]
    [InlineData(TabStripPlacement.Left, false, 480, 3)]
    [InlineData(TabStripPlacement.Top, true, 320, 2)]
    [InlineData(TabStripPlacement.Top, true, 480, 3)]
    public void SideAndDetachedTabAreasAddColumnsAsTheirWidthGrows(
        TabStripPlacement placement,
        bool detached,
        double width,
        int expectedColumns) => StaTest.Run(() =>
    {
        var session = Session(placement, 30, detached);
        var canonical = session.Current!.Projection.Tabs.Entries.ToArray();
        var control = new TabControllerControl(detached
            ? TabControllerSurfaceKind.Detached
            : TabControllerSurfaceKind.Docked);
        control.Bind(session);
        StaTest.Prepare(control, width, 360);

        Assert.Equal(expectedColumns, control.VisibleColumnCount);
        Assert.InRange(
            control.VisibleColumnWidth,
            TabStripViewportModel.MinimumVerticalColumnWidth,
            TabStripViewportModel.PreferredVerticalColumnWidth);
        var visibleTabs = StaTest.Descendants(control).OfType<Button>()
            .Where(button => button.Tag is BrowserTabEntry && button.Visibility == Visibility.Visible)
            .ToArray();
        Assert.NotEmpty(visibleTabs);
        var columns = visibleTabs
            .Select(button => Math.Round(BoundsIn(button, control).Left, 1))
            .Distinct()
            .ToArray();
        Assert.True(columns.Length == expectedColumns,
            $"Expected {expectedColumns} rendered columns but found {columns.Length}; " +
            $"reported={control.VisibleColumnCount}, width={control.VisibleColumnWidth:0.##}, " +
            $"surface={control.ActualWidth:0.##}x{control.ActualHeight:0.##}, " +
            $"tab positions={string.Join(", ", visibleTabs.Select(button => BoundsIn(button, control).ToString()))}.");
        Assert.Contains(visibleTabs, button => button.Tag is BrowserTabEntry { IsSelected: true });
        Assert.Equal(canonical, session.Current!.Projection.Tabs.Entries);

        var scrollbarBounds = BoundsIn(control.OverflowScrollBar, control);
        foreach (var close in StaTest.Descendants(control).OfType<Button>().Where(button =>
                     AutomationProperties.GetName(button).StartsWith("Close tab —", StringComparison.Ordinal)))
        {
            var closeBounds = BoundsIn(close, control);
            Assert.True(close.ActualWidth >= 44 && close.ActualHeight >= 44);
            var overlap = Rect.Intersect(scrollbarBounds, closeBounds);
            Assert.True(overlap.IsEmpty || overlap.Width < .5 || overlap.Height < .5);
        }
    });

    [Theory]
    [InlineData(TabStripPlacement.Left, 168, 360, false)]
    [InlineData(TabStripPlacement.Right, 168, 360, false)]
    [InlineData(TabStripPlacement.Top, 320, 360, true)]
    public void NarrowSideAndDetachedCommandsStayInsideTheirSurfaceAtAccessibleSize(
        TabStripPlacement placement,
        double width,
        double height,
        bool detached) => StaTest.Run(() =>
    {
        var control = new TabControllerControl(detached
            ? TabControllerSurfaceKind.Detached
            : TabControllerSurfaceKind.Docked);
        control.Bind(Session(placement, 18, detached));
        StaTest.Prepare(control, width, height);

        var commandNames = detached
            ? new[] { "More tabs", "Open new tab", "Browser resources", "Dock tab controller" }
            : new[] { "More tabs", "Open new tab", "Browser resources", "Pop out tab controller" };
        var commands = commandNames.Select(name =>
            StaTest.Descendants(control).OfType<Button>().Single(button =>
                AutomationProperties.GetName(button).StartsWith(name, StringComparison.Ordinal))).ToArray();

        var surface = new Rect(0, 0, control.ActualWidth, control.ActualHeight);
        foreach (var command in commands)
        {
            var bounds = BoundsIn(command, control);
            Assert.Equal(Visibility.Visible, command.Visibility);
            Assert.True(command.ActualWidth >= 44 && command.ActualHeight >= 44);
            Assert.True(surface.Contains(bounds.TopLeft));
            Assert.True(bounds.Right <= surface.Right + .5,
                $"{AutomationProperties.GetName(command)} was clipped at {bounds.Right:0.##} of {surface.Right:0.##} DIP.");
            Assert.True(bounds.Bottom <= surface.Bottom + .5,
                $"{AutomationProperties.GetName(command)} was clipped at {bounds.Bottom:0.##} of {surface.Bottom:0.##} DIP.");
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(command)));
        }

        for (var left = 0; left < commands.Length; left++)
        {
            for (var right = left + 1; right < commands.Length; right++)
            {
                var overlap = Rect.Intersect(BoundsIn(commands[left], control), BoundsIn(commands[right], control));
                Assert.True(overlap.IsEmpty || overlap.Width < .5 || overlap.Height < .5,
                    $"{AutomationProperties.GetName(commands[left])} overlapped {AutomationProperties.GetName(commands[right])}.");
            }
        }

        // The 168-DIP side rail cannot contain four 44-DIP targets in one row.
        // It must adapt by wrapping instead of clipping or shrinking them.
        if (!detached)
        {
            Assert.True(commands.Select(command => Math.Round(BoundsIn(command, control).Top, 1)).Distinct().Count() >= 2);
        }
    });

    [Fact]
    public void CaptureNarrowControllerLayoutEvidenceWhenRequested() => StaTest.Run(() =>
    {
        var output = Environment.GetEnvironmentVariable("ORBIT_CAPTURE_NARROW_CONTROLLER_DIR");
        if (string.IsNullOrWhiteSpace(output))
        {
            return;
        }

        Directory.CreateDirectory(output);
        CaptureController(
            new TabControllerControl(),
            Session(TabStripPlacement.Left, 18),
            168,
            360,
            Path.Combine(output, "side-left-168x360.png"));
        CaptureController(
            new TabControllerControl(TabControllerSurfaceKind.Detached),
            Session(TabStripPlacement.Top, 18, detached: true),
            320,
            360,
            Path.Combine(output, "detached-min-320x360.png"));
        CaptureDetachedWindow(
            Session(TabStripPlacement.Top, 24, detached: true),
            Path.Combine(output, "detached-window-360x400.png"));
    });

    [Fact]
    public void HoverDefaultOpensFivePlusGroupAsCanonicalPreviewDropdown() => StaTest.Run(() =>
    {
        var windowId = new BrowserWindowId(Guid.NewGuid());
        var groupId = new BrowserTabGroupId(Guid.NewGuid());
        var selected = new BrowserTabId(Guid.NewGuid());
        var previews = Enumerable.Range(0, 5).Select(index => new TabGroupPreviewItemPresentation(
            new BrowserTabId(Guid.NewGuid()),
            $"Research {index + 1}",
            new Uri($"https://research{index + 1}.test/"),
            ReadOnlyMemory<byte>.Empty)).ToArray();
        var header = new TabGroupHeaderEntry(groupId, "Research", 5, true, false)
        {
            TabIds = previews.Select(item => item.TabId).ToArray(),
            TabPreviews = previews,
        };
        var session = new TabControllerPresentationSession(windowId, false, new Sink());
        session.AcceptProjection(new(
            windowId, false, 1, TabControllerHostState.Docked, TabStripPlacement.Top,
            new(windowId, selected,
            [
                header,
                new BrowserTabEntry(selected, null, "Selected", new Uri("https://selected.test/"),
                    BrowserLoadState.Idle, true, false, false, false),
            ]))
        {
            PreviewRevealMode = WorkspacePreviewRevealMode.Hover,
        });
        var control = new TabControllerControl { ReducedMotion = true };
        control.Bind(session);
        var window = new Window { Content = control, Width = 900, Height = 140, ShowInTaskbar = false };
        window.Show();
        try
        {
            window.UpdateLayout();
            var button = StaTest.FindByAutomationName<Button>(control,
                "Research, group, 5 tabs, collapsed");
            button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            {
                RoutedEvent = Mouse.MouseEnterEvent,
                Source = button,
            });
            window.UpdateLayout();

            Assert.NotNull(button.ContextMenu);
            Assert.False(button.ContextMenu!.IsOpen);
            Assert.Contains(button.ContextMenu.Items.OfType<MenuItem>(), item =>
                string.Equals(item.Header?.ToString(), "Rename group…", StringComparison.Ordinal));
            var previewField = typeof(TabControllerControl).GetField(
                "groupPreviewMenu",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var previewMenu = Assert.IsType<ContextMenu>(previewField?.GetValue(control));
            if (!previewMenu.IsOpen)
            {
                previewMenu.IsOpen = true;
            }
            Assert.True(control.IsGroupPreviewDropdownOpen);
            Assert.Equal(5, control.GroupPreviewDropdownItemCount);
            Assert.Equal(
                previews.Select(item => item.Title),
                control.GroupPreviewDropdownLabels);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void HoverPreviewRightClickRoutesToRenameColorAndSaveGroupActions() => StaTest.Run(() =>
    {
        var windowId = new BrowserWindowId(Guid.NewGuid());
        var groupId = new BrowserTabGroupId(Guid.NewGuid());
        var selected = new BrowserTabId(Guid.NewGuid());
        var previews = Enumerable.Range(0, 5).Select(index => new TabGroupPreviewItemPresentation(
            new BrowserTabId(Guid.NewGuid()),
            $"Workspace tab {index + 1}",
            new Uri($"https://workspace{index + 1}.test/"),
            ReadOnlyMemory<byte>.Empty)).ToArray();
        var header = new TabGroupHeaderEntry(groupId, "Project orbit", 5, true, false)
        {
            IsTemporary = true,
            TabIds = previews.Select(item => item.TabId).ToArray(),
            TabPreviews = previews,
        };
        var session = new TabControllerPresentationSession(windowId, false, new Sink());
        session.AcceptProjection(new(
            windowId, false, 1, TabControllerHostState.Docked, TabStripPlacement.Top,
            new(windowId, selected,
            [
                header,
                new BrowserTabEntry(selected, null, "Selected", new Uri("https://selected.test/"),
                    BrowserLoadState.Idle, true, false, false, false),
            ]))
        {
            PreviewRevealMode = WorkspacePreviewRevealMode.Hover,
        });
        var control = new TabControllerControl { ReducedMotion = true };
        control.Bind(session);
        var window = new Window { Content = control, Width = 900, Height = 140, ShowInTaskbar = false };
        window.Show();
        try
        {
            window.UpdateLayout();
            var button = StaTest.FindByAutomationName<Button>(control,
                "Project orbit, group, 5 tabs, collapsed");
            button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            {
                RoutedEvent = Mouse.MouseEnterEvent,
                Source = button,
            });
            window.UpdateLayout();

            var previewField = typeof(TabControllerControl).GetField(
                "groupPreviewMenu",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var preview = Assert.IsType<ContextMenu>(previewField?.GetValue(control));
            // A headless full-suite run can dismiss a context menu when another test
            // window loses activation. Reopen the hover-created preview so this test
            // exercises popup event ownership rather than desktop activation policy.
            if (!preview.IsOpen)
            {
                preview.IsOpen = true;
            }
            Assert.True(preview.IsOpen);
            Assert.Equal(PlacementMode.Bottom, preview.Placement);

            preview.RaiseEvent(new MouseButtonEventArgs(
                Mouse.PrimaryDevice,
                Environment.TickCount,
                MouseButton.Right)
            {
                RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent,
                Source = preview,
            });
            window.Dispatcher.Invoke(
                () => { },
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            var actions = Assert.IsType<ContextMenu>(button.ContextMenu);
            Assert.False(preview.IsOpen);
            Assert.True(actions.IsOpen);
            Assert.Contains(actions.Items.OfType<MenuItem>(), item =>
                string.Equals(item.Header?.ToString(), "Rename group…", StringComparison.Ordinal));
            Assert.Contains(actions.Items.OfType<MenuItem>(), item =>
                string.Equals(item.Header?.ToString(), "Group color", StringComparison.Ordinal));
            Assert.Contains(actions.Items.OfType<MenuItem>(), item =>
                string.Equals(item.Header?.ToString(), "Save as workspace…", StringComparison.Ordinal));
            actions.IsOpen = false;
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void OpenGroupActionMenuDoesNotCollapseOrRebuildInlineHoverPreview() => StaTest.Run(() =>
    {
        var windowId = new BrowserWindowId(Guid.NewGuid());
        var groupId = new BrowserTabGroupId(Guid.NewGuid());
        var selected = new BrowserTabId(Guid.NewGuid());
        var previewIds = Enumerable.Range(0, 3).Select(_ => new BrowserTabId(Guid.NewGuid())).ToArray();
        var header = new TabGroupHeaderEntry(groupId, "Planning", 3, true, false)
        {
            TabIds = previewIds,
            TabPreviews = previewIds.Select((id, index) => new TabGroupPreviewItemPresentation(
                id,
                $"Planning {index + 1}",
                new Uri($"https://planning{index + 1}.test/"),
                ReadOnlyMemory<byte>.Empty)).ToArray(),
        };
        var session = new TabControllerPresentationSession(windowId, false, new Sink());
        session.AcceptProjection(new(
            windowId, false, 1, TabControllerHostState.Docked, TabStripPlacement.Top,
            new(windowId, selected,
            [
                header,
                new BrowserTabEntry(selected, null, "Selected", new Uri("https://selected.test/"),
                    BrowserLoadState.Idle, true, false, false, false),
            ]))
        {
            PreviewRevealMode = WorkspacePreviewRevealMode.Hover,
        });
        var control = new TabControllerControl { ReducedMotion = true };
        control.Bind(session);
        StaTest.Prepare(control, 900, 120);

        var button = StaTest.FindByAutomationName<Button>(control,
            "Planning, group, 3 tabs, collapsed");
        button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
        {
            RoutedEvent = Mouse.MouseEnterEvent,
            Source = button,
        });
        control.UpdateLayout();
        button = StaTest.FindByAutomationName<Button>(control,
            "Planning, group, 3 tabs, collapsed");
        var actionMenu = Assert.IsType<ContextMenu>(button.ContextMenu);
        actionMenu.PlacementTarget = button;
        actionMenu.IsOpen = true;

        var rail = Assert.Single(LogicalTreeHelper.GetChildren(control).OfType<Grid>());
        rail.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
        {
            RoutedEvent = Mouse.MouseLeaveEvent,
            Source = rail,
        });
        control.UpdateLayout();

        Assert.True(actionMenu.IsOpen);
        Assert.True(control.IsInlineGroupPreviewVisible);
        Assert.Equal(groupId, control.ActiveHoverPreviewGroupId);
        Assert.Same(actionMenu, button.ContextMenu);
        Assert.Contains(actionMenu.Items.OfType<MenuItem>(), item =>
            string.Equals(item.Header?.ToString(), "Rename group…", StringComparison.Ordinal));
        actionMenu.IsOpen = false;
    });

    [Fact]
    public void DetachedSurfaceNamesDockAndNeverOwnsWebContent() => StaTest.Run(() =>
    {
        var control = new TabControllerControl(TabControllerSurfaceKind.Detached);
        control.Bind(Session(TabStripPlacement.Top, 8, detached: true));
        StaTest.Prepare(control, 420, 700);

        Assert.NotNull(StaTest.FindByAutomationName<Button>(control, "Dock tab controller"));
        Assert.DoesNotContain(StaTest.Descendants(control), value =>
            value.GetType().FullName?.Contains("WebView", StringComparison.OrdinalIgnoreCase) == true);
    });

    [Fact]
    public void DetachedClosedFillsToolAndUsesOneLabeledCommandRow() => StaTest.Run(() =>
    {
        var control = new TabControllerControl(TabControllerSurfaceKind.Detached);
        control.Bind(Session(TabStripPlacement.Top, 12, detached: true));
        var tool = new Window
        {
            Content = control,
            Width = TabControllerControl.RecommendedDetachedWidth,
            Height = TabControllerControl.RecommendedDetachedHeight,
            MinWidth = TabControllerControl.MinimumDetachedWidth,
            MinHeight = TabControllerControl.MinimumDetachedHeight,
            ShowInTaskbar = false,
        };
        tool.Show();
        try
        {
            tool.UpdateLayout();
            var newTab = StaTest.FindByAutomationName<Button>(control, "Open new tab");
            var resourceButton = StaTest.FindByAutomationName<Button>(control, "Browser resources");
            var dock = StaTest.FindByAutomationName<Button>(control, "Dock tab controller");

            Assert.Equal(Visibility.Visible, newTab.Visibility);
            Assert.Equal(Visibility.Visible, resourceButton.Visibility);
            Assert.Equal(Visibility.Visible, dock.Visibility);
            Assert.True(newTab.ActualWidth >= 44 && newTab.ActualHeight >= 44);
            Assert.True(resourceButton.ActualWidth >= 44 && resourceButton.ActualHeight >= 44);
            Assert.True(dock.ActualWidth >= 44 && dock.ActualHeight >= 44);
            Assert.InRange(control.ActualHeight, 1, tool.ActualHeight);

            var visibleLabels = StaTest.Descendants(control).OfType<TextBlock>()
                .Select(value => value.Text)
                .ToHashSet(StringComparer.Ordinal);
            Assert.Contains("Previous", visibleLabels);
            Assert.Contains("Next", visibleLabels);
            Assert.Contains(visibleLabels, label => label.StartsWith("More", StringComparison.Ordinal));
            Assert.Contains("Resources", visibleLabels);
            Assert.Contains("Dock", visibleLabels);

            var selected = StaTest.FindByAutomationName<Button>(control,
                StaTest.Descendants(control).OfType<Button>()
                    .Select(AutomationProperties.GetName)
                    .First(name => name.StartsWith("Selected tab", StringComparison.Ordinal)));
            Assert.True(newTab.TranslatePoint(new Point(), control).Y >
                        selected.TranslatePoint(new Point(), control).Y);

            var heightBeforeResourceRequest = control.ActualHeight;
            resourceButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            tool.UpdateLayout();
            Assert.Equal(heightBeforeResourceRequest, control.ActualHeight, 1);
        }
        finally
        {
            tool.Close();
        }
    });

    [Fact]
    public void DetachedNativeMinimumReservesNewTabBelowScrollableListInsideClientArea() => StaTest.Run(() =>
    {
        var control = new TabControllerControl(TabControllerSurfaceKind.Detached);
        control.Bind(Session(TabStripPlacement.Top, 24, detached: true));
        var tool = new Window
        {
            Content = control,
            Title = "Orbit Navigator — Tabs",
            Width = 360,
            Height = 400,
            MinWidth = 360,
            MinHeight = 400,
            ShowInTaskbar = false,
        };
        tool.Show();
        try
        {
            tool.UpdateLayout();
            PumpDispatcher();
            tool.UpdateLayout();

            var newTab = StaTest.FindByAutomationName<Button>(control, "Open new tab");
            var bounds = BoundsIn(newTab, control);
            Assert.True(control.ActualHeight < tool.ActualHeight,
                "The control must use the native client area, not the outer Window height.");
            Assert.True(newTab.ActualWidth >= 44 && newTab.ActualHeight >= 44);
            Assert.True(bounds.Top >= 0 && bounds.Bottom <= control.ActualHeight + .5,
                $"New Tab ended at {bounds.Bottom:0.##} of a {control.ActualHeight:0.##}-DIP client area.");
            Assert.Equal(Orientation.Vertical, control.OverflowScrollBar.Orientation);
            Assert.Equal(Visibility.Visible, control.OverflowScrollBar.Visibility);
            Assert.True(BoundsIn(control.OverflowScrollBar, control).Bottom <= bounds.Top + .5,
                "The scrollable tab viewport must end before the reserved New Tab row.");
        }
        finally
        {
            tool.Close();
        }
    });

    [Theory]
    [InlineData(520, 720)]
    [InlineData(480, 560)]
    [InlineData(360, 400)]
    public void DetachedResponsiveLayoutKeepsSelectedCommandsAndNewTabReadable(double width, double height) => StaTest.Run(() =>
    {
        var control = new TabControllerControl(TabControllerSurfaceKind.Detached);
        control.Bind(Session(TabStripPlacement.Top, 24, detached: true));
        StaTest.Prepare(control, width, height);

        var selected = StaTest.Descendants(control).OfType<Button>()
            .Single(button => AutomationProperties.GetName(button).StartsWith("Selected tab", StringComparison.Ordinal));
        Assert.Equal(Visibility.Visible, selected.Visibility);
        Assert.True(selected.ActualWidth >= 88 && selected.ActualHeight >= 44);
        foreach (var name in new[] { "More tabs", "Browser resources", "Dock tab controller", "Open new tab" })
        {
            var command = StaTest.Descendants(control).OfType<Button>()
                .Single(button => AutomationProperties.GetName(button).StartsWith(name, StringComparison.Ordinal));
            Assert.Equal(Visibility.Visible, command.Visibility);
            Assert.True(command.ActualWidth >= 44 && command.ActualHeight >= 44);
        }
    });

    [Theory]
    [InlineData(1280)]
    [InlineData(900)]
    [InlineData(640)]
    [InlineData(420)]
    [InlineData(360)]
    public void TopPlacementAlwaysShowsSelectedAndPinnedPrimaryControls(double width) => StaTest.Run(() =>
    {
        var control = new TabControllerControl();
        control.Bind(Session(TabStripPlacement.Top, 30));
        StaTest.Prepare(control, width, 80);

        var selected = StaTest.Descendants(control).OfType<Button>()
            .Single(button => AutomationProperties.GetName(button).StartsWith("Selected tab", StringComparison.Ordinal));
        Assert.Equal(Visibility.Visible, selected.Visibility);
        // At the favicon-only floor the select target remains 44 DIP and the
        // independently focusable Close target retains its own 44 DIP gutter.
        Assert.True(selected.ActualWidth >= 44);
        Assert.Contains(StaTest.Descendants(control).OfType<Button>(), button =>
            AutomationProperties.GetName(button).StartsWith("Close tab — Selected tab", StringComparison.Ordinal) &&
            button.Visibility == Visibility.Visible && button.ActualWidth >= 40);
        foreach (var prefix in new[] { "More tabs", "Open new tab", "Browser resources", "Pop out tab controller" })
        {
            Assert.Contains(StaTest.Descendants(control).OfType<Button>(), button =>
                AutomationProperties.GetName(button).StartsWith(prefix, StringComparison.Ordinal) &&
                button.Visibility == Visibility.Visible && button.ActualWidth >= 44);
        }
    });

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void TopPlacementSurvivesCommonDpiScaleAndDynamicPlacementChange(double scale) => StaTest.Run(() =>
    {
        var windowId = new BrowserWindowId(Guid.NewGuid());
        var selectedId = new BrowserTabId(Guid.NewGuid());
        var entries = Enumerable.Range(0, 18).Select(index =>
        {
            var id = index == 17 ? selectedId : new BrowserTabId(Guid.NewGuid());
            return (TabStripEntry)new BrowserTabEntry(
                id, null, index == 17 ? "Selected tab" : $"Tab {index}", null,
                BrowserLoadState.Idle, index == 17, false, false, false);
        }).ToArray();
        var session = new TabControllerPresentationSession(windowId, false, new Sink());
        session.AcceptProjection(new(
            windowId, false, 1, TabControllerHostState.Docked, TabStripPlacement.Left,
            new(windowId, selectedId, entries)));
        var control = new TabControllerControl { LayoutTransform = new System.Windows.Media.ScaleTransform(scale, scale) };
        control.Bind(session);
        StaTest.Prepare(control, 900 / scale, 100 / scale);

        session.AcceptProjection(new(
            windowId, false, 2, TabControllerHostState.Docked, TabStripPlacement.Top,
            new(windowId, selectedId, entries)));
        control.UpdateLayout();

        Assert.Contains(StaTest.Descendants(control).OfType<Button>(), button =>
            AutomationProperties.GetName(button) == "Selected tab, tab" &&
            button.Visibility == Visibility.Visible && button.ActualWidth >= 44);
        var selectedButton = StaTest.FindByAutomationName<Button>(control, "Selected tab, tab");
        Assert.True(selectedButton.ActualHeight >= 44);
        selectedButton.ApplyTemplate();
        Assert.InRange(
            Assert.IsType<Border>(selectedButton.Template.FindName("Border", selectedButton)).ActualHeight,
            30,
            34);
        Assert.Contains(StaTest.Descendants(control).OfType<Button>(), button =>
            AutomationProperties.GetName(button).StartsWith("Close tab — Selected tab", StringComparison.Ordinal) &&
            button.Visibility == Visibility.Visible);
    });

    [Theory]
    [InlineData(TabStripPlacement.Top, 900, 120, false)]
    [InlineData(TabStripPlacement.Top, 420, 120, false)]
    [InlineData(TabStripPlacement.Left, 224, 640, false)]
    [InlineData(TabStripPlacement.Right, 224, 640, false)]
    [InlineData(TabStripPlacement.Top, 520, 720, true)]
    public void NewTabIsPinnedReachableAndCtrlTEmitsTypedAction(
        TabStripPlacement placement,
        double width,
        double height,
        bool detached) => StaTest.Run(() =>
    {
        var windowId = new BrowserWindowId(Guid.NewGuid());
        var tabId = new BrowserTabId(Guid.NewGuid());
        var sink = new RecordingSink();
        var session = new TabControllerPresentationSession(windowId, false, sink);
        session.AcceptProjection(new(
            windowId,
            false,
            1,
            detached ? TabControllerHostState.Detached : TabControllerHostState.Docked,
            placement,
            new(windowId, tabId,
            [new BrowserTabEntry(tabId, null, "Existing tab", new Uri("https://example.test/"),
                BrowserLoadState.Idle, true, false, false, false)])));
        var control = new TabControllerControl(
            detached ? TabControllerSurfaceKind.Detached : TabControllerSurfaceKind.Docked);
        control.Bind(session);
        StaTest.Prepare(control, width, height);

        var newTab = StaTest.FindByAutomationName<Button>(control, "Open new tab");
        Assert.Equal(Visibility.Visible, newTab.Visibility);
        Assert.True(newTab.ActualWidth >= 44 && newTab.ActualHeight >= 44);
        Assert.Equal("Ctrl+T", AutomationProperties.GetAcceleratorKey(newTab));
        newTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.IsType<CreateNewTabControllerAction>(Assert.Single(sink.Actions));

        Assert.True(control.TryHandleControllerShortcut(Key.T, ModifierKeys.Control));
        Assert.Equal(2, sink.Actions.Count);
        Assert.All(sink.Actions, action => Assert.IsType<CreateNewTabControllerAction>(action));
        var onlyClose = StaTest.Descendants(control).OfType<Button>()
            .Single(button => AutomationProperties.GetName(button).StartsWith("Close tab —", StringComparison.Ordinal));
        Assert.False(onlyClose.IsEnabled);
        Assert.True(onlyClose.MinWidth >= 40 && onlyClose.MinHeight >= 40);
    });

    [Theory]
    [InlineData(TabStripPlacement.Top, 900, 120, false)]
    [InlineData(TabStripPlacement.Top, 420, 120, false)]
    [InlineData(TabStripPlacement.Left, 224, 640, false)]
    [InlineData(TabStripPlacement.Right, 224, 640, false)]
    [InlineData(TabStripPlacement.Top, 520, 720, true)]
    public void EveryVisibleTabHasIndependentCloseAndCtrlWUsesTypedAction(
        TabStripPlacement placement,
        double width,
        double height,
        bool detached) => StaTest.Run(() =>
    {
        var windowId = new BrowserWindowId(Guid.NewGuid());
        var first = new BrowserTabId(Guid.NewGuid());
        var second = new BrowserTabId(Guid.NewGuid());
        var sink = new RecordingSink();
        var session = new TabControllerPresentationSession(windowId, false, sink);
        session.AcceptProjection(new(
            windowId,
            false,
            1,
            detached ? TabControllerHostState.Detached : TabControllerHostState.Docked,
            placement,
            new(windowId, first,
            [
                new BrowserTabEntry(first, null, "Selected tab", new Uri("https://one.test/"), BrowserLoadState.Loading, true, false, false, false),
                new BrowserTabEntry(second, null, "Background tab", new Uri("https://two.test/"), BrowserLoadState.Idle, false, false, false, false),
            ])));
        var control = new TabControllerControl(
            detached ? TabControllerSurfaceKind.Detached : TabControllerSurfaceKind.Docked);
        control.Bind(session);
        StaTest.Prepare(control, width, height);

        var visibleTabs = StaTest.Descendants(control).OfType<Button>()
            .Where(button => button.Tag is BrowserTabEntry)
            .ToArray();
        var closes = StaTest.Descendants(control).OfType<Button>()
            .Where(button => AutomationProperties.GetName(button).StartsWith("Close tab —", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(visibleTabs);
        Assert.Equal(visibleTabs.Length, closes.Length);
        Assert.All(closes, close =>
        {
            Assert.True(close.IsEnabled);
            Assert.True(close.ActualWidth >= 40 && close.ActualHeight >= 40);
            Assert.Equal("Ctrl+W", AutomationProperties.GetAcceleratorKey(close));
        });

        closes[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.IsType<CloseTabControllerAction>(Assert.Single(sink.Actions));
        Assert.True(control.TryHandleControllerShortcut(Key.W, ModifierKeys.Control));
        Assert.Equal(2, sink.Actions.Count);
        Assert.All(sink.Actions, action => Assert.IsType<CloseTabControllerAction>(action));
    });

    [Fact]
    public void AcceptedCloseRestoresFocusToAuthoritativeSelectedTab() => StaTest.Run(() =>
    {
        var windowId = new BrowserWindowId(Guid.NewGuid());
        var closing = new BrowserTabId(Guid.NewGuid());
        var remaining = new BrowserTabId(Guid.NewGuid());
        var initial = new RevisionedTabControllerProjection(
            windowId, false, 1, TabControllerHostState.Docked, TabStripPlacement.Top,
            new(windowId, closing,
            [
                new BrowserTabEntry(closing, null, "Closing tab", new Uri("https://one.test/"), BrowserLoadState.Idle, true, false, false, false),
                new BrowserTabEntry(remaining, null, "Remaining tab", new Uri("https://two.test/"), BrowserLoadState.Idle, false, false, false, false),
            ]));
        var refreshed = new RevisionedTabControllerProjection(
            windowId, false, 2, TabControllerHostState.Docked, TabStripPlacement.Top,
            new(windowId, remaining,
            [new BrowserTabEntry(remaining, null, "Remaining tab", new Uri("https://two.test/"), BrowserLoadState.Idle, true, false, false, false)]));
        var session = new TabControllerPresentationSession(windowId, false, new RefreshingCloseSink(refreshed));
        session.AcceptProjection(initial);
        var control = new TabControllerControl();
        control.Bind(session);
        var window = new Window { Content = control, Width = 900, Height = 180, ShowInTaskbar = false };
        window.Show();
        try
        {
            window.UpdateLayout();
            StaTest.Descendants(control).OfType<Button>()
                .Single(button => AutomationProperties.GetName(button) == "Close tab — Closing tab")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpDispatcher();

            var selected = StaTest.FindByAutomationName<Button>(control, "Remaining tab, tab");
            Assert.True(selected.IsKeyboardFocused);
        }
        finally
        {
            window.Close();
        }
    });

    [Theory]
    [InlineData(TabStripPlacement.Top, false, 640, 90)]
    [InlineData(TabStripPlacement.Left, false, 224, 640)]
    [InlineData(TabStripPlacement.Right, false, 224, 640)]
    [InlineData(TabStripPlacement.Top, true, 480, 560)]
    public void CompactModeKeepsFaviconSelectionAndIndependentCloseTargets(
        TabStripPlacement placement,
        bool detached,
        double width,
        double height) => StaTest.Run(() =>
    {
        var control = new TabControllerControl(
            detached ? TabControllerSurfaceKind.Detached : TabControllerSurfaceKind.Docked);
        control.Bind(Session(placement, 18, detached));
        control.ApplyCompactMode(true);
        StaTest.Prepare(control, width, height);

        Assert.True(control.IsCompactMode);
        var selected = StaTest.Descendants(control).OfType<Button>()
            .Single(button => button.Tag is BrowserTabEntry { IsSelected: true });
        var close = StaTest.Descendants(control).OfType<Button>()
            .Single(button => AutomationProperties.GetName(button)
                .StartsWith("Close tab — Selected tab", StringComparison.Ordinal));
        Assert.True(selected.ActualWidth >= 44 && selected.ActualHeight >= 44);
        Assert.True(close.ActualWidth >= 44 && close.ActualHeight >= 44);
        Assert.DoesNotContain(
            StaTest.Descendants(selected).OfType<TextBlock>(),
            text => text.Text.StartsWith("Selected tab", StringComparison.Ordinal));
        Assert.Contains("site17.test", AutomationProperties.GetHelpText(selected), StringComparison.Ordinal);
    });

    [Fact]
    public void RevisionedPageTitleAndPngFaviconRenderOneLabelWithSiteOnlyInAccessibleContext() => StaTest.Run(() =>
    {
        var session = Session(TabStripPlacement.Top, 1);
        var selected = session.Current!.Projection.Tabs.SelectedTabId!.Value;
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9ZSmAAAAAASUVORK5CYII=");
        var control = new TabControllerControl { ReducedMotion = true };
        control.Bind(session);
        Assert.True(session.AcceptTabVisualMetadata(new(
            selected,
            8,
            "A real document title",
            "Example service",
            png)));
        StaTest.Prepare(control, 720, 90);

        var tab = StaTest.FindByAutomationName<Button>(
            control,
            "A real document title, tab");
        Assert.Contains(StaTest.Descendants(tab).OfType<Image>(), image => image.Source is not null);
        Assert.Single(StaTest.Descendants(tab).OfType<TextBlock>(), text => text.Text == "A real document title");
        Assert.DoesNotContain(StaTest.Descendants(tab).OfType<TextBlock>(), text => text.Text == "Example service");
        Assert.Contains("Site: Example service", AutomationProperties.GetHelpText(tab));
        Assert.True(tab.ActualHeight >= 44);
        tab.ApplyTemplate();
        Assert.InRange(
            Assert.IsType<Border>(tab.Template.FindName("Border", tab)).ActualHeight,
            30,
            34);
        Assert.DoesNotContain(
            StaTest.Descendants(tab).OfType<Image>(),
            image => image.Source is System.Windows.Media.Imaging.BitmapImage bitmap && bitmap.UriSource is not null);
    });

    [Fact]
    public void MissingOrInvalidFaviconUsesSafeSiteMonogramWithoutRemoteImageFetch() => StaTest.Run(() =>
    {
        var session = Session(TabStripPlacement.Top, 1);
        var selected = session.Current!.Projection.Tabs.SelectedTabId!.Value;
        var control = new TabControllerControl { ReducedMotion = true };
        control.Bind(session);
        Assert.True(session.AcceptTabVisualMetadata(new(
            selected,
            9,
            "My Orbit",
            "My Orbit",
            new byte[] { 1, 2, 3, 4 })));
        StaTest.Prepare(control, 720, 90);

        var tab = StaTest.FindByAutomationName<Button>(control, "My Orbit, tab");
        Assert.DoesNotContain(StaTest.Descendants(tab).OfType<Image>(), _ => true);
        Assert.Contains(StaTest.Descendants(tab).OfType<TextBlock>(), text => text.Text == "M");
        Assert.Single(StaTest.Descendants(tab).OfType<TextBlock>(), text => text.Text == "My Orbit");
    });

    [Theory]
    [InlineData(TabStripPlacement.Top, 720, 90)]
    [InlineData(TabStripPlacement.Left, 224, 640)]
    [InlineData(TabStripPlacement.Right, 224, 640)]
    public void BlankTabUsesNewTabOnceAcrossRailPlacements(
        TabStripPlacement placement,
        double width,
        double height) => StaTest.Run(() =>
    {
        var windowId = new BrowserWindowId(Guid.NewGuid());
        var tabId = new BrowserTabId(Guid.NewGuid());
        var session = new TabControllerPresentationSession(windowId, false, new Sink());
        session.AcceptProjection(new(
            windowId,
            false,
            1,
            TabControllerHostState.Docked,
            placement,
            new(windowId, tabId,
            [new BrowserTabEntry(tabId, null, "New tab", null,
                BrowserLoadState.Idle, true, false, false, false)])));
        var control = new TabControllerControl { ReducedMotion = true };
        control.Bind(session);
        StaTest.Prepare(control, width, height);

        var tab = StaTest.FindByAutomationName<Button>(control, "New Tab, tab");
        Assert.Single(
            StaTest.Descendants(tab).OfType<TextBlock>(),
            text => text.Text.Equals("New Tab", StringComparison.Ordinal));
        Assert.DoesNotContain(
            StaTest.Descendants(tab).OfType<TextBlock>(),
            text => text.Text.Equals("New tab", StringComparison.Ordinal));
        Assert.True(tab.ActualHeight >= 44);
        tab.ApplyTemplate();
        Assert.InRange(
            Assert.IsType<Border>(tab.Template.FindName("Border", tab)).ActualHeight,
            30,
            34);
    });

    [Theory]
    [InlineData(TabStripPlacement.Top, 640, 90, false)]
    [InlineData(TabStripPlacement.Left, 224, 640, false)]
    [InlineData(TabStripPlacement.Right, 224, 640, false)]
    [InlineData(TabStripPlacement.Top, 480, 560, true)]
    public void PlayingAudioUsesIndependentAccessibleMuteWithoutCollidingInCompactMode(
        TabStripPlacement placement,
        double width,
        double height,
        bool detached) => StaTest.Run(() =>
    {
        var windowId = new BrowserWindowId(Guid.NewGuid());
        var audible = new BrowserTabId(Guid.NewGuid());
        var quiet = new BrowserTabId(Guid.NewGuid());
        var sink = new RecordingSink();
        var session = new TabControllerPresentationSession(windowId, false, sink);
        session.AcceptProjection(new(
            windowId,
            false,
            9,
            detached ? TabControllerHostState.Detached : TabControllerHostState.Docked,
            placement,
            new(windowId, audible,
            [
                new BrowserTabEntry(audible, null, "Audio tab", new Uri("https://audio.test/"), BrowserLoadState.Idle, true, false, false, false),
                new BrowserTabEntry(quiet, null, "Quiet tab", new Uri("https://quiet.test/"), BrowserLoadState.Idle, false, false, false, false),
            ])));
        Assert.True(session.AcceptTabInteractionCapabilities(Interaction(audible, 1, true, false)));
        Assert.True(session.AcceptTabInteractionCapabilities(Interaction(quiet, 1, false, false)));
        var control = new TabControllerControl(
            detached ? TabControllerSurfaceKind.Detached : TabControllerSurfaceKind.Docked);
        control.Bind(session);
        control.ApplyCompactMode(true);
        StaTest.Prepare(control, width, height);

        var mute = StaTest.FindByAutomationName<Button>(control, "Mute tab — Audio tab");
        Assert.True(mute.ActualWidth >= 44 && mute.ActualHeight >= 44);
        Assert.Equal("Audio playing", AutomationProperties.GetItemStatus(mute));
        Assert.Contains("this tab only", AutomationProperties.GetHelpText(mute), StringComparison.Ordinal);
        Assert.Single(StaTest.Descendants(mute).OfType<TabAudioGlyph>());
        Assert.DoesNotContain(StaTest.Descendants(control).OfType<Button>(), button =>
            AutomationProperties.GetName(button).Contains("Quiet tab", StringComparison.Ordinal) &&
            AutomationProperties.GetName(button).StartsWith("Mute", StringComparison.Ordinal));

        var close = StaTest.FindByAutomationName<Button>(control, "Close tab — Audio tab");
        var muteBounds = BoundsIn(mute, control);
        var closeBounds = BoundsIn(close, control);
        Assert.True(muteBounds.Right <= closeBounds.Left || closeBounds.Right <= muteBounds.Left ||
                    muteBounds.Bottom <= closeBounds.Top || closeBounds.Bottom <= muteBounds.Top);

        mute.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var action = Assert.IsType<SetTabMutedControllerAction>(Assert.Single(sink.Actions));
        Assert.Equal(audible, action.TabId);
        Assert.True(action.IsMuted);
    });

    [Fact]
    public void ContextMenuConsolidatesRealTabActionsAndCapabilityGatesAudioAndSiteControls() => StaTest.Run(() =>
    {
        var windowId = new BrowserWindowId(Guid.NewGuid());
        var groupId = new BrowserTabGroupId(Guid.NewGuid());
        var grouped = new BrowserTabId(Guid.NewGuid());
        var selected = new BrowserTabId(Guid.NewGuid());
        var sink = new RecordingSink();
        var session = new TabControllerPresentationSession(windowId, false, sink);
        session.AcceptProjection(new(
            windowId,
            false,
            6,
            TabControllerHostState.Docked,
            TabStripPlacement.Top,
            new(windowId, selected,
            [
                new TabGroupHeaderEntry(groupId, "Research", 1, false, false),
                new BrowserTabEntry(grouped, groupId, "Grouped", new Uri("https://grouped.test/"), BrowserLoadState.Idle, false, false, false, false),
                new BrowserTabEntry(selected, null, "Selected", new Uri("https://selected.test/"), BrowserLoadState.Idle, true, false, false, false),
            ])));
        var interaction = Interaction(selected, 2, true, false) with
        {
            SiteContentControls =
            [
                new(TabSiteContentControlKind.AdBlocking, true, true, true, string.Empty),
                new(TabSiteContentControlKind.ScriptBlocking, false, false, false,
                    "No enforced script blocker is available."),
            ],
        };
        Assert.True(session.AcceptTabInteractionCapabilities(interaction));
        var control = new TabControllerControl();
        control.Bind(session);
        StaTest.Prepare(control, 900, 100);

        var tab = StaTest.FindByAutomationName<Button>(control, "Selected, tab");
        var menu = Assert.IsType<ContextMenu>(tab.ContextMenu);
        var topLevel = menu.Items.OfType<MenuItem>().ToDictionary(item => item.Header?.ToString() ?? string.Empty);
        Assert.Contains("Duplicate tab", topLevel.Keys);
        Assert.Contains("Close tab", topLevel.Keys);
        Assert.Contains("Tab group", topLevel.Keys);
        Assert.Contains("Tab Audio", topLevel.Keys);
        Assert.Contains("Site content controls", topLevel.Keys);

        topLevel["Duplicate tab"].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.IsType<DuplicateTabControllerAction>(Assert.Single(sink.Actions));

        var groupMenu = topLevel["Tab group"];
        Assert.Contains(groupMenu.Items.OfType<MenuItem>(), item =>
            item.Header?.ToString() == "Create new group with this tab");
        var move = groupMenu.Items.OfType<MenuItem>().Single(item => item.Header?.ToString() == "Move to group");
        var research = move.Items.OfType<MenuItem>().Single(item => item.Header?.ToString() == "Research");
        research.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.IsType<MoveTabControllerAction>(sink.Actions[1]);

        var audio = topLevel["Tab Audio"];
        var mute = audio.Items.OfType<MenuItem>().Single(item => item.Header?.ToString() == "Mute this tab");
        Assert.True(mute.IsEnabled);
        mute.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.IsType<SetTabMutedControllerAction>(sink.Actions[2]);
        foreach (var label in new[] { "Volume", "Equalizer", "Left / right balance", "Output device" })
        {
            var unavailable = audio.Items.OfType<MenuItem>()
                .Single(item => item.Header?.ToString() == $"{label} — unavailable");
            Assert.False(unavailable.IsEnabled);
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(unavailable)));
        }
        Assert.DoesNotContain(StaTest.Descendants(menu).OfType<Slider>(), _ => true);
        Assert.DoesNotContain(StaTest.Descendants(menu).OfType<ComboBox>(), _ => true);

        var site = topLevel["Site content controls"];
        Assert.Single(site.Items.OfType<MenuItem>());
        var ads = site.Items.OfType<MenuItem>().Single();
        Assert.Equal("Ad blocker: On", ads.Header);
        ads.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        var siteAction = Assert.IsType<SetTabSiteContentControlControllerAction>(sink.Actions[3]);
        Assert.Equal(TabSiteContentControlKind.AdBlocking, siteAction.Kind);
        Assert.False(siteAction.IsEnabled);
    });

    [Fact]
    public void ContextMenuOmitsUnenforcedSiteControlsAndSpeakerWhenAudioIsNotPlaying() => StaTest.Run(() =>
    {
        var session = Session(TabStripPlacement.Top, 2);
        var selected = session.Current!.Projection.Tabs.SelectedTabId!.Value;
        Assert.True(session.AcceptTabInteractionCapabilities(Interaction(selected, 1, false, false) with
        {
            SiteContentControls =
            [new(TabSiteContentControlKind.AdBlocking, false, false, false, "No enforced blocker is available.")],
        }));
        var control = new TabControllerControl();
        control.Bind(session);
        StaTest.Prepare(control, 720, 90);

        Assert.DoesNotContain(StaTest.Descendants(control).OfType<Button>(), button =>
            AutomationProperties.GetName(button).StartsWith("Mute tab —", StringComparison.Ordinal));
        var tab = StaTest.Descendants(control).OfType<Button>()
            .Single(button => button.Tag is BrowserTabEntry { IsSelected: true });
        var menu = Assert.IsType<ContextMenu>(tab.ContextMenu);
        Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), item =>
            item.Header?.ToString() == "Site content controls");
    });

    [Fact]
    public void BrowserChromeBuffersHostAudioProjectionUntilControllerSessionIsBound() => StaTest.Run(() =>
    {
        var session = Session(TabStripPlacement.Top, 2);
        var selected = session.Current!.Projection.Tabs.SelectedTabId!.Value;
        var chrome = new BrowserChromeControl();

        Assert.True(chrome.ApplyTabInteractionCapabilities(Interaction(selected, 5, true, true)));
        chrome.BindTabControllerSession(session);
        StaTest.Prepare(chrome, 1000, 180);

        var unmute = StaTest.FindByAutomationName<Button>(chrome, "Unmute tab — Selected tab " + new string('Z', 180));
        Assert.Equal("Audio playing, muted", AutomationProperties.GetItemStatus(unmute));
        Assert.False(chrome.ApplyTabInteractionCapabilities(Interaction(selected, 4, false, false)));
        Assert.NotNull(StaTest.FindByAutomationName<Button>(chrome, "Unmute tab — Selected tab " + new string('Z', 180)));
    });

    private static TabControllerPresentationSession Session(
        TabStripPlacement placement,
        int count,
        bool detached = false)
    {
        var window = new BrowserWindowId(Guid.NewGuid());
        var tabs = Enumerable.Range(0, count).Select(index => new BrowserTabEntry(
            new BrowserTabId(Guid.NewGuid()),
            null,
            index == count - 1 ? "Selected tab " + new string('Z', 180) : $"Tab {index} " + new string('A', 180),
            new Uri($"https://site{index}.test/"),
            BrowserLoadState.Idle,
            index == count - 1,
            false,
            false,
            false)).ToArray();
        var selected = tabs[^1].TabId;
        var session = new TabControllerPresentationSession(window, false, new Sink());
        session.AcceptProjection(new(
            window,
            false,
            1,
            detached ? TabControllerHostState.Detached : TabControllerHostState.Docked,
            placement,
            new(window, selected, tabs)));
        return session;
    }

    private static TabInteractionCapabilitiesPresentation Interaction(
        BrowserTabId tab,
        long revision,
        bool playing,
        bool muted) => new(
        tab,
        revision,
        playing,
        muted,
        TabAudioFeatureCapabilityPresentation.Available(TabAudioFeature.Mute),
        TabAudioFeatureCapabilityPresentation.Unavailable(
            TabAudioFeature.Volume,
            "Per-tab volume requires a verified browser audio engine."),
        TabAudioFeatureCapabilityPresentation.Unavailable(
            TabAudioFeature.Equalizer,
            "Per-tab equalizer controls require a verified browser audio engine."),
        TabAudioFeatureCapabilityPresentation.Unavailable(
            TabAudioFeature.Balance,
            "Per-tab balance requires a verified browser audio engine."),
        TabAudioFeatureCapabilityPresentation.Unavailable(
            TabAudioFeature.OutputDevice,
            "Per-tab output routing requires verified Windows audio-session routing."),
        []);

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

    private sealed class RecordingSink : ITabControllerCommandSink
    {
        public List<TabControllerAction> Actions { get; } = [];

        public ValueTask<TabControllerCommandResult> ExecuteAsync(
            TabControllerCommand command,
            CancellationToken cancellationToken = default)
        {
            Actions.Add(command.Action);
            return ValueTask.FromResult(new TabControllerCommandResult(
                command.Action.StableActionId,
                TabControllerCommandOutcome.Accepted,
                "Completed."));
        }
    }

    private sealed class RefreshingCloseSink(RevisionedTabControllerProjection refreshed) : ITabControllerCommandSink
    {
        public ValueTask<TabControllerCommandResult> ExecuteAsync(
            TabControllerCommand command,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new TabControllerCommandResult(
                command.Action.StableActionId,
                TabControllerCommandOutcome.Accepted,
                "Tab closed.",
                refreshed));
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.SystemIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static Rect BoundsIn(FrameworkElement element, UIElement relativeTo)
    {
        var topLeft = element.TranslatePoint(new Point(0, 0), relativeTo);
        return new Rect(topLeft, new Size(element.ActualWidth, element.ActualHeight));
    }

    private static void CaptureController(
        TabControllerControl control,
        TabControllerPresentationSession session,
        int width,
        int height,
        string path)
    {
        control.Bind(session);
        StaTest.Prepare(control, width, height);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(control);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void CaptureDetachedWindow(TabControllerPresentationSession session, string path)
    {
        var control = new TabControllerControl(TabControllerSurfaceKind.Detached);
        control.Bind(session);
        var window = new Window
        {
            Content = control,
            Title = "Orbit Navigator — Tabs",
            Width = 360,
            Height = 400,
            MinWidth = 360,
            MinHeight = 400,
            Left = -30000,
            Top = -30000,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        window.Show();
        try
        {
            window.UpdateLayout();
            PumpDispatcher();
            window.UpdateLayout();
            var width = Math.Max(1, (int)Math.Ceiling(control.ActualWidth));
            var height = Math.Max(1, (int)Math.Ceiling(control.ActualHeight));
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(control);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
        finally
        {
            window.Close();
        }
    }
}
