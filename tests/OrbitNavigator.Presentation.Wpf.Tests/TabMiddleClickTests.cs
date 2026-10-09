using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Presentation.Tabs;
using OrbitNavigator.Presentation.Workspace;
using OrbitNavigator.Presentation.Wpf;

using Xunit;

namespace OrbitNavigator.Presentation.Wpf.Tests;

[Collection("WPF focus-sensitive")]
public sealed class TabMiddleClickTests
{
    [Theory]
    [InlineData(TabStripPlacement.Top, 900, 120, false, false)]
    [InlineData(TabStripPlacement.Top, 420, 120, false, true)]
    [InlineData(TabStripPlacement.Left, 224, 640, false, false)]
    [InlineData(TabStripPlacement.Right, 224, 640, false, true)]
    [InlineData(TabStripPlacement.Top, 520, 720, true, false)]
    [InlineData(TabStripPlacement.Top, 520, 720, true, true)]
    public void MiddleClickOnTabChildrenClosesOnceWithoutSelectingMutingOrMutatingTabs(
        TabStripPlacement placement, double width, double height, bool detached, bool compact) => StaTest.Run(() =>
    {
        var (session, sink) = CreateSession(placement, detached: detached);
        var projection = session.Current!.Projection;
        foreach (var tab in projection.Tabs.Entries.OfType<BrowserTabEntry>())
        {
            session.AcceptTabInteractionCapabilities(Audio(tab.TabId));
        }

        var control = new TabControllerControl(
            detached ? TabControllerSurfaceKind.Detached : TabControllerSurfaceKind.Docked);
        control.Bind(session);
        control.ApplyCompactMode(compact);

        foreach (var childKind in new[] { "identity", "audio", "close" })
        {
            StaTest.Prepare(control, width, height);
            var select = StaTest.Descendants(control).OfType<Button>()
                .Last(button => button.Tag is BrowserTabEntry);
            var tab = Assert.IsType<BrowserTabEntry>(select.Tag);
            var card = Assert.IsType<Grid>(select.Parent);
            var button = childKind switch
            {
                "audio" => card.Children.OfType<Button>().Single(candidate =>
                    AutomationProperties.GetName(candidate).StartsWith("Mute tab", StringComparison.Ordinal)),
                "close" => card.Children.OfType<Button>().Single(candidate =>
                    AutomationProperties.GetName(candidate).StartsWith("Close tab", StringComparison.Ordinal)),
                _ => select,
            };
            Assert.True(button.MinWidth >= 44 && button.MinHeight >= 44);
            var child = StaTest.Descendants(button).OfType<UIElement>().Last();
            var priorCount = sink.Commands.Count;

            RouteButton(child, MouseButton.Middle, down: true);
            Assert.Equal(priorCount, sink.Commands.Count);
            RouteButton(child, MouseButton.Middle, down: false);

            var command = Assert.Single(sink.Commands.Skip(priorCount));
            Assert.Equal(tab.TabId, Assert.IsType<CloseTabControllerAction>(command.Action).TabId);
            Assert.Equal(projection.WindowId, command.WindowId);
            Assert.Equal(projection.Revision, command.ExpectedRevision);
            Assert.Same(projection, session.Current!.Projection);
            Assert.Equal(projection.Tabs.SelectedTabId, session.Current.Projection.Tabs.SelectedTabId);
        }
        Assert.Equal(3, sink.Commands.Count);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AlreadyHandledChildBubblingEventsStillCloseExactlyOnce(bool previewToo) => StaTest.Run(() =>
    {
        var (session, sink) = CreateSession();
        var control = new TabControllerControl();
        control.Bind(session);
        StaTest.Prepare(control);
        var select = StaTest.Descendants(control).OfType<Button>()
            .Last(button => button.Tag is BrowserTabEntry);
        var child = StaTest.Descendants(select).OfType<UIElement>().Last();
        child.AddHandler(Mouse.MouseDownEvent, new MouseButtonEventHandler((_, args) => args.Handled = true));
        child.AddHandler(Mouse.MouseUpEvent, new MouseButtonEventHandler((_, args) => args.Handled = true));

        RouteButton(child, MouseButton.Middle, down: true, previewToo);
        RouteButton(child, MouseButton.Middle, down: false, previewToo);
        RouteButton(child, MouseButton.Middle, down: false, previewToo);

        var command = Assert.Single(sink.Commands);
        Assert.Equal(Assert.IsType<BrowserTabEntry>(select.Tag).TabId,
            Assert.IsType<CloseTabControllerAction>(command.Action).TabId);
    });

    [Theory]
    [InlineData(MouseButton.Left)]
    [InlineData(MouseButton.Right)]
    [InlineData(MouseButton.XButton1)]
    [InlineData(MouseButton.XButton2)]
    public void OtherButtonsDoNotCloseTabs(MouseButton button) => StaTest.Run(() =>
    {
        var (session, sink) = CreateSession();
        var control = new TabControllerControl();
        control.Bind(session);
        StaTest.Prepare(control);
        var select = StaTest.Descendants(control).OfType<Button>()
            .First(candidate => candidate.Tag is BrowserTabEntry);
        var card = Assert.IsType<Grid>(select.Parent);

        Assert.False(RouteButton(card, button, down: true));
        Assert.False(RouteButton(card, button, down: false));
        Assert.Empty(sink.Commands);
    });

    [Fact]
    public void LeavingTabCancelsMiddlePressAndReleaseAloneDoesNotClose() => StaTest.Run(() =>
    {
        var (session, sink) = CreateSession();
        var control = new TabControllerControl();
        control.Bind(session);
        StaTest.Prepare(control);
        var select = StaTest.Descendants(control).OfType<Button>()
            .First(button => button.Tag is BrowserTabEntry);
        var card = Assert.IsType<Grid>(select.Parent);

        RouteButton(select, MouseButton.Middle, down: false);
        RouteButton(select, MouseButton.Middle, down: true);
        card.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
        {
            RoutedEvent = Mouse.MouseLeaveEvent,
        });
        RouteButton(select, MouseButton.Middle, down: false);

        Assert.Empty(sink.Commands);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SoleTabMiddleCloseDelegatesPolicyToOwnerAndPreservesPrivacy(bool isPrivate) => StaTest.Run(() =>
    {
        var (session, sink) = CreateSession(count: 1, isPrivate: isPrivate);
        sink.Outcome = TabControllerCommandOutcome.PolicyDenied;
        var projection = session.Current!.Projection;
        var control = new TabControllerControl();
        control.Bind(session);
        StaTest.Prepare(control);
        var select = StaTest.Descendants(control).OfType<Button>()
            .Single(button => button.Tag is BrowserTabEntry);

        RouteButton(select, MouseButton.Middle, down: true);
        RouteButton(select, MouseButton.Middle, down: false);

        var command = Assert.Single(sink.Commands);
        Assert.Equal(isPrivate, command.IsPrivate);
        Assert.Equal(projection.WindowId, command.WindowId);
        Assert.Equal(projection.Revision, command.ExpectedRevision);
        Assert.Equal(projection.Tabs.SelectedTabId, Assert.IsType<CloseTabControllerAction>(command.Action).TabId);
        Assert.Same(projection, session.Current!.Projection);
        Assert.Equal("Owner result.", session.Current.Announcement);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptedMiddleCloseRestoresAuthoritativeSelectedFocus(bool closeSelected) => StaTest.Run(() =>
    {
        var (session, sink) = CreateSession();
        var projection = session.Current!.Projection;
        var closing = projection.Tabs.Entries.OfType<BrowserTabEntry>()
            .Single(tab => tab.IsSelected == closeSelected);
        var remaining = projection.Tabs.Entries.OfType<BrowserTabEntry>()
            .Single(tab => tab.TabId != closing.TabId) with { IsSelected = true };
        var control = new TabControllerControl();
        control.Bind(session);
        var window = new Window { Content = control, Width = 900, Height = 180, ShowInTaskbar = false };
        window.Show();
        try
        {
            window.UpdateLayout();
            Assert.True(control.FocusSelectedTab());
            var selectedBefore = StaTest.Descendants(control).OfType<Button>()
                .Single(button => button.Tag is BrowserTabEntry { IsSelected: true });
            var target = StaTest.Descendants(control).OfType<Button>()
                .Single(button => button.Tag is BrowserTabEntry tab && tab.TabId == closing.TabId);
            RouteButton(target, MouseButton.Middle, down: true);
            Assert.True(selectedBefore.IsKeyboardFocused);
            RouteButton(target, MouseButton.Middle, down: false);
            Assert.Single(sink.Commands);
            Assert.Same(projection, session.Current!.Projection);

            session.AcceptProjection(projection with
            {
                Revision = projection.Revision + 1,
                Tabs = new(projection.WindowId, remaining.TabId, [remaining]),
            });
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            var selectedAfter = StaTest.Descendants(control).OfType<Button>()
                .Single(button => button.Tag is BrowserTabEntry { IsSelected: true });
            Assert.Equal(remaining.TabId, Assert.IsType<BrowserTabEntry>(selectedAfter.Tag).TabId);
            Assert.True(selectedAfter.IsKeyboardFocused);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void MiddlePressClearsAnyPreviouslyArmedLeftDrag() => StaTest.Run(() =>
    {
        var (session, sink) = CreateSession();
        var control = new TabControllerControl();
        control.Bind(session);
        StaTest.Prepare(control);
        var select = StaTest.Descendants(control).OfType<Button>()
            .First(button => button.Tag is BrowserTabEntry);
        select.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
        });
        var dragSource = typeof(TabControllerControl).GetField("dragSourceTabId",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.NotNull(dragSource.GetValue(control));

        RouteButton(select, MouseButton.Middle, down: true);

        Assert.Null(dragSource.GetValue(control));
        Assert.Empty(sink.Commands);
    });

    [Fact]
    public void OverflowRowMiddleClickClosesHiddenTabWithoutSwitching() => StaTest.Run(() =>
    {
        var (session, sink) = CreateSession(count: 20);
        var control = new TabControllerControl();
        control.Bind(session);
        StaTest.Prepare(control, 420, 120);
        var more = StaTest.Descendants(control).OfType<Button>().Single(button =>
            AutomationProperties.GetName(button).StartsWith("More tabs", StringComparison.Ordinal));
        more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var menu = Assert.IsType<ContextMenu>(more.ContextMenu);
        try
        {
            var row = menu.Items.OfType<MenuItem>().Last();
            RouteButton(row, MouseButton.Middle, down: true);
            RouteButton(row, MouseButton.Middle, down: false);
            var closed = Assert.IsType<CloseTabControllerAction>(Assert.Single(sink.Commands).Action);
            Assert.Equal(session.Current!.Projection.Tabs.Entries.OfType<BrowserTabEntry>().Last().TabId, closed.TabId);
        }
        finally
        {
            menu.IsOpen = false;
        }
    });

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public void GroupPreviewMiddleClickClosesRepresentedTabWithoutSwitching(int groupSize) => StaTest.Run(() =>
    {
        var (session, sink) = CreateSession();
        var projection = session.Current!.Projection;
        var groupId = new BrowserTabGroupId(Guid.NewGuid());
        var previews = Enumerable.Range(0, groupSize).Select(index => new TabGroupPreviewItemPresentation(
            new BrowserTabId(Guid.NewGuid()), $"Preview {index}", new Uri($"https://preview{index}.test/"),
            ReadOnlyMemory<byte>.Empty)).ToArray();
        var group = new TabGroupHeaderEntry(groupId, "Research", groupSize, true, false)
        {
            TabIds = previews.Select(tab => tab.TabId).ToArray(),
            TabPreviews = previews,
        };
        session.AcceptProjection(projection with
        {
            Tabs = projection.Tabs with { Entries = [group, projection.Tabs.Entries[0]] },
        });
        var control = new TabControllerControl { ReducedMotion = true };
        control.Bind(session);
        var window = new Window
        {
            Content = control, Width = 1100, Height = 180,
            ShowInTaskbar = false, ShowActivated = false,
        };
        window.Show();
        ContextMenu? popup = null;
        try
        {
            window.UpdateLayout();
            var header = StaTest.Descendants(control).OfType<Button>()
                .Single(button => button.Tag is TabGroupHeaderEntry);
            header.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            {
                RoutedEvent = Mouse.MouseEnterEvent,
            });
            window.UpdateLayout();
            UIElement target;
            if (groupSize >= 5)
            {
                popup = Assert.IsType<ContextMenu>(typeof(TabControllerControl).GetField(
                    "groupPreviewMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?.GetValue(control));
                target = popup.Items.OfType<MenuItem>().First();
            }
            else
            {
                target = StaTest.Descendants(control).OfType<Button>()
                    .Single(button => button.Tag is BrowserTabEntry tab && tab.TabId == previews[0].TabId);
            }

            RouteButton(target, MouseButton.Middle, down: true);
            RouteButton(target, MouseButton.Middle, down: false);
            Assert.Equal(previews[0].TabId,
                Assert.IsType<CloseTabControllerAction>(Assert.Single(sink.Commands).Action).TabId);
        }
        finally
        {
            if (popup is not null) popup.IsOpen = false;
            window.Close();
        }
    });

    [Theory]
    [InlineData(TabControllerCommandOutcome.Stale, true)]
    [InlineData(TabControllerCommandOutcome.Stale, false)]
    [InlineData(TabControllerCommandOutcome.PolicyDenied, true)]
    [InlineData(TabControllerCommandOutcome.PolicyDenied, false)]
    public void RejectedGroupPreviewCloseRetainsFocusAndDoesNotAnnounceClosure(
        TabControllerCommandOutcome outcome, bool retainedInTabIds) => StaTest.Run(() =>
    {
        var (session, sink) = CreateSession();
        var projection = session.Current!.Projection;
        var previews = Enumerable.Range(0, 2).Select(index => new TabGroupPreviewItemPresentation(
            new BrowserTabId(Guid.NewGuid()), $"Retained {index}", new Uri($"https://retained{index}.test/"),
            ReadOnlyMemory<byte>.Empty)).ToArray();
        var group = new TabGroupHeaderEntry(new BrowserTabGroupId(Guid.NewGuid()), "Retained group", 2, true, false)
        {
            TabIds = previews.Select(tab => tab.TabId).ToArray(),
            TabPreviews = previews,
        };
        session.AcceptProjection(projection with
        {
            Tabs = projection.Tabs with { Entries = [group, projection.Tabs.Entries[0]] },
        });
        sink.Outcome = outcome;
        sink.RefreshedProjection = projection with
        {
            Revision = projection.Revision + 1,
            Tabs = projection.Tabs with
            {
                Entries =
                [
                    group with
                    {
                        TabIds = retainedInTabIds ? group.TabIds : [],
                        TabPreviews = retainedInTabIds ? [] : previews,
                    },
                    projection.Tabs.Entries[0],
                ],
            },
        };
        var control = new TabControllerControl { ReducedMotion = true, Height = 120 };
        control.Bind(session);
        var editor = new TextBox { Text = "Keep keyboard focus here." };
        var content = new DockPanel();
        DockPanel.SetDock(control, Dock.Top);
        content.Children.Add(control);
        content.Children.Add(editor);
        var window = new Window { Content = content, Width = 1100, Height = 280, ShowInTaskbar = false };
        window.Show();
        try
        {
            window.UpdateLayout();
            var header = StaTest.Descendants(control).OfType<Button>()
                .Single(button => button.Tag is TabGroupHeaderEntry);
            header.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            {
                RoutedEvent = Mouse.MouseEnterEvent,
            });
            window.UpdateLayout();
            var target = StaTest.Descendants(control).OfType<Button>()
                .Single(button => button.Tag is BrowserTabEntry tab && tab.TabId == previews[0].TabId);
            Assert.Same(editor, Keyboard.Focus(editor));

            RouteButton(target, MouseButton.Middle, down: true);
            RouteButton(target, MouseButton.Middle, down: false);
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            Assert.Equal(previews[0].TabId,
                Assert.IsType<CloseTabControllerAction>(Assert.Single(sink.Commands).Action).TabId);
            Assert.Same(sink.RefreshedProjection, session.Current!.Projection);
            Assert.Equal(projection.Tabs.SelectedTabId, session.Current.Projection.Tabs.SelectedTabId);
            Assert.Same(editor, Keyboard.FocusedElement);
            var status = StaTest.FindByAutomationName<TextBlock>(control, "Tab controller status");
            Assert.Equal("Owner result.", status.Text);
            Assert.DoesNotContain("Tab closed", status.Text, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    });

    [Theory]
    [InlineData(TabStripPlacement.Top, false, 2)]
    [InlineData(TabStripPlacement.Left, true, 2)]
    [InlineData(TabStripPlacement.Right, false, 2)]
    [InlineData(TabStripPlacement.Top, true, 1)]
    public void CompatibilityTabCardsShareMiddleCloseRoutingAndOwnerPolicy(
        TabStripPlacement placement, bool isPrivate, int count) => StaTest.Run(() =>
    {
        var windowId = new BrowserWindowId(Guid.NewGuid());
        var tabs = Enumerable.Range(0, count).Select(index => new BrowserTabState(
            new BrowserTabId(Guid.NewGuid()), null, null, $"Fallback {index}",
            BrowserLoadState.Idle, false, false, isPrivate)).ToArray();
        var state = new BrowserState(windowId, tabs[0].TabId, tabs);
        var chrome = new BrowserChromeControl();
        chrome.ApplyWorkspacePreferences(BrowserWorkspacePreferences.Default with { TabStripPlacement = placement });
        chrome.RenderTabs(state, new Dictionary<BrowserTabGroupId, TabGroupPresentation>());
        StaTest.Prepare(chrome);
        Assert.Null(chrome.TabControllerSession);
        var commands = new List<BrowserCommand>();
        chrome.BrowserCommandRequested += (_, command) => commands.Add(command);
        var select = StaTest.FindByAutomationName<Button>(chrome,
            tabs[^1].Title + (isPrivate ? ", private tab" : string.Empty));
        var container = Assert.IsType<StackPanel>(select.Parent);
        foreach (var otherButton in new[] { MouseButton.Left, MouseButton.Right, MouseButton.XButton1, MouseButton.XButton2 })
        {
            RouteButton(container, otherButton, down: true);
            RouteButton(container, otherButton, down: false);
        }
        Assert.Empty(commands);

        foreach (var button in container.Children.OfType<Button>())
        {
            Assert.True(button.MinWidth >= 44 && button.MinHeight >= 44);
            var child = StaTest.Descendants(button).OfType<UIElement>().Last();
            child.AddHandler(Mouse.MouseDownEvent, new MouseButtonEventHandler((_, args) => args.Handled = true));
            child.AddHandler(Mouse.MouseUpEvent, new MouseButtonEventHandler((_, args) => args.Handled = true));
            var priorCount = commands.Count;
            RouteButton(child, MouseButton.Middle, down: true);
            RouteButton(child, MouseButton.Middle, down: false);
            RouteButton(child, MouseButton.Middle, down: false);

            var closed = Assert.IsType<CloseTabBrowserCommand>(Assert.Single(commands.Skip(priorCount)));
            Assert.Equal(windowId, closed.WindowId);
            Assert.Equal(tabs[^1].TabId, closed.TabId);
            Assert.Same(select, StaTest.FindByAutomationName<Button>(chrome,
                tabs[^1].Title + (isPrivate ? ", private tab" : string.Empty)));
            Assert.Equal(count == 1 ? "Selected" : string.Empty, AutomationProperties.GetItemStatus(select));
        }
        Assert.Equal(2, commands.Count);
    });

    private static bool RouteButton(UIElement source, MouseButton button, bool down, bool previewToo = true)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, button);
        if (previewToo)
        {
            args.RoutedEvent = down ? Mouse.PreviewMouseDownEvent : Mouse.PreviewMouseUpEvent;
            source.RaiseEvent(args);
        }
        args.RoutedEvent = down ? Mouse.MouseDownEvent : Mouse.MouseUpEvent;
        source.RaiseEvent(args);
        if (button == MouseButton.Middle) Assert.True(args.Handled);
        return args.Handled;
    }

    private static (TabControllerPresentationSession Session, RecordingSink Sink) CreateSession(
        TabStripPlacement placement = TabStripPlacement.Top, int count = 2, bool detached = false, bool isPrivate = false)
    {
        var windowId = new BrowserWindowId(Guid.NewGuid());
        var tabs = Enumerable.Range(0, count).Select(index => new BrowserTabEntry(
            new BrowserTabId(Guid.NewGuid()), null, $"Tab {index}", new Uri($"https://tab{index}.test/"),
            BrowserLoadState.Idle, index == 0, isPrivate, false, false)).ToArray();
        var sink = new RecordingSink();
        var session = new TabControllerPresentationSession(windowId, isPrivate, sink);
        session.AcceptProjection(new(windowId, isPrivate, 7,
            detached ? TabControllerHostState.Detached : TabControllerHostState.Docked, placement,
            new(windowId, tabs[0].TabId, tabs)));
        return (session, sink);
    }

    private static TabInteractionCapabilitiesPresentation Audio(BrowserTabId tabId) => new(
        tabId, 1, true, false,
        TabAudioFeatureCapabilityPresentation.Available(TabAudioFeature.Mute),
        TabAudioFeatureCapabilityPresentation.Unavailable(TabAudioFeature.Volume, "Unavailable."),
        TabAudioFeatureCapabilityPresentation.Unavailable(TabAudioFeature.Equalizer, "Unavailable."),
        TabAudioFeatureCapabilityPresentation.Unavailable(TabAudioFeature.Balance, "Unavailable."),
        TabAudioFeatureCapabilityPresentation.Unavailable(TabAudioFeature.OutputDevice, "Unavailable."), []);

    private sealed class RecordingSink : ITabControllerCommandSink
    {
        public List<TabControllerCommand> Commands { get; } = [];
        public TabControllerCommandOutcome Outcome { get; set; } = TabControllerCommandOutcome.Accepted;
        public RevisionedTabControllerProjection? RefreshedProjection { get; set; }

        public ValueTask<TabControllerCommandResult> ExecuteAsync(
            TabControllerCommand command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            return ValueTask.FromResult(new TabControllerCommandResult(
                command.Action.StableActionId, Outcome, "Owner result.", RefreshedProjection));
        }
    }
}
