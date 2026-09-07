using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Privacy;
using OrbitNavigator.Presentation.Permissions;
using OrbitNavigator.Presentation.Shell;
using OrbitNavigator.Presentation.Tabs;
using OrbitNavigator.Presentation.Workspace;
using OrbitNavigator.Presentation.Wpf;
using Xunit;

namespace OrbitNavigator.Presentation.Wpf.Tests;

[Collection("WPF focus-sensitive")]
public sealed class BrowserChromeVisualTests
{
    [Fact]
    public void ChromeUsesVectorGlyphsStyledControlsAndPrivateCue()
    {
        StaTest.Run(() =>
        {
            var context = Browsing(BrowserProfileMode.Private);
            var tab = Tab(context.TabId, "Private research", true);
            var chrome = new BrowserChromeControl();
            chrome.SetBrowsingContext(context);
            chrome.RenderTabs(new BrowserState(context.WindowId, tab.TabId, [tab]),
                new Dictionary<BrowserTabGroupId, TabGroupPresentation>());
            StaTest.Prepare(chrome);

            Assert.NotEmpty(StaTest.Descendants(chrome).OfType<OrbitIcon>());
            Assert.NotNull(StaTest.FindByAutomationName<Button>(chrome, "Go back").Template);
            Assert.NotNull(StaTest.FindByAutomationName<TextBox>(chrome, "Address and search").Template);
            Assert.False(StaTest.FindByAutomationName<Button>(chrome, "New private window").IsEnabled);
            Assert.Equal("Private window", AutomationProperties.GetName(
                StaTest.FindByAutomationName<Border>(chrome, "Private window")));
        });
    }

    [Fact]
    public void HomeAndNewTabControlsEmitTypedCommands()
    {
        StaTest.Run(() =>
        {
            var context = Browsing();
            var tab = Tab(context.TabId, "New tab", false);
            var chrome = new BrowserChromeControl();
            chrome.SetBrowsingContext(context);
            chrome.RenderTabs(new BrowserState(context.WindowId, tab.TabId, [tab]),
                new Dictionary<BrowserTabGroupId, TabGroupPresentation>());
            var commands = new List<BrowserCommand>();
            chrome.BrowserCommandRequested += (_, command) => commands.Add(command);
            StaTest.Prepare(chrome);

            StaTest.FindByAutomationName<Button>(chrome, "Go to home page")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            StaTest.FindByAutomationName<Button>(chrome, "Open new tab")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal("duckduckgo.com", Assert.IsType<NavigateBrowserCommand>(commands[0]).Target.Host);
            Assert.IsType<CreateTabBrowserCommand>(commands[1]);
        });
    }

    [Theory]
    [InlineData(TabStripPlacement.Left)]
    [InlineData(TabStripPlacement.Right)]
    public void SidePlacementKeepsCommandsVisibleAndExposesPersistentResizeHandle(TabStripPlacement placement)
    {
        StaTest.Run(() =>
        {
            var context = Browsing();
            var chrome = new BrowserChromeControl();
            var changes = new List<BrowserWorkspacePreferences>();
            chrome.WorkspacePreferencesChanged += (_, args) => changes.Add(args.Preferences);
            chrome.ApplyWorkspacePreferences(BrowserWorkspacePreferences.Default with
            {
                TabStripPlacement = placement,
            });
            chrome.BindTabControllerSession(ControllerSession(
                context,
                placement,
                TabControllerHostState.Docked,
                1));
            StaTest.Prepare(chrome, 1200, 800);

            Assert.Contains(chrome.ColumnDefinitions, column =>
                column.Width.IsAbsolute &&
                Math.Abs(column.Width.Value - BrowserWorkspacePreferences.DefaultSideTabPanelWidth) < .1);
            var handle = chrome.SideTabPanelResizeHandle;
            Assert.Equal(Visibility.Visible, handle.Visibility);
            Assert.Equal("Resize tab panel", AutomationProperties.GetName(handle));
            Assert.Equal(Cursors.SizeWE, handle.Cursor);

            var controller = StaTest.FindByAutomationName<TabControllerControl>(
                chrome,
                "Browser tab controller");
            var commandButtons = new[]
            {
                "More tabs",
                "Open new tab",
                "Browser resources",
                "Pop out tab controller",
            }.Select(name => StaTest.FindByAutomationName<Button>(controller, name)).ToArray();
            var commandBounds = commandButtons.Select(button =>
                button.TransformToAncestor(controller).TransformBounds(
                    new Rect(0, 0, button.ActualWidth, button.ActualHeight))).ToArray();
            Assert.All(commandButtons, button => Assert.True(button.ActualWidth >= 44));
            Assert.All(commandBounds, bounds =>
            {
                Assert.True(bounds.Left >= -.5);
                Assert.True(bounds.Right <= controller.ActualWidth + .5);
            });
            Assert.True(commandBounds.Max(bounds => bounds.Top) - commandBounds.Min(bounds => bounds.Top) < 1);

            var tabColumn = placement == TabStripPlacement.Left ? 0 : 1;
            chrome.ColumnDefinitions[tabColumn].Width = new GridLength(300);
            chrome.UpdateLayout();
            handle.RaiseEvent(new DragCompletedEventArgs(76, 0, false)
            {
                RoutedEvent = Thumb.DragCompletedEvent,
            });

            Assert.Equal(300, Assert.Single(changes).SideTabPanelWidth);
            Assert.Equal(300, chrome.WorkspacePreferences.SideTabPanelWidth);
        });
    }

    [Fact]
    public void TopPlacementHidesSideTabResizeHandle()
    {
        StaTest.Run(() =>
        {
            var chrome = new BrowserChromeControl();
            chrome.ApplyWorkspacePreferences(BrowserWorkspacePreferences.Default);
            StaTest.Prepare(chrome, 1200, 800);

            Assert.Equal(Visibility.Collapsed, chrome.SideTabPanelResizeHandle.Visibility);
        });
    }

    [Fact]
    public void PermissionPromptUsesHumanCopyAndSafestChoice()
    {
        StaTest.Run(() =>
        {
            var broker = new FakePermissionBroker();
            using var presenter = new PermissionPromptPresenter(broker);
            var context = Browsing();
            var site = SiteIdentity.Create(new Uri("https://media.example.test")).Value!;
            broker.Raise(new PermissionPromptState(
                new RequestId(Guid.NewGuid()),
                new ResponseToken(Guid.NewGuid()),
                context,
                site,
                site.DisplayOrigin,
                WebPermissionCapability.Popups,
                [PermissionAllowScope.Once],
                PermissionDecision.Deny,
                PermissionDecision.Deny,
                DateTimeOffset.UtcNow.AddMinutes(1)));
            var chrome = new BrowserChromeControl();
            chrome.SetBrowsingContext(context);
            chrome.RenderTabs(
                new BrowserState(context.WindowId, context.TabId, [Tab(context.TabId, "Media", false)]),
                new Dictionary<BrowserTabGroupId, TabGroupPresentation>());
            chrome.BindPermissionPrompt(presenter);
            StaTest.Prepare(chrome);

            Assert.True(chrome.IsPermissionPromptVisible);
            var permissionSurface = StaTest.FindByAutomationName<Border>(chrome, "Site permission request");
            Assert.Equal(3, Grid.GetRow(permissionSurface));
            Assert.InRange(permissionSurface.ActualWidth, 240, 420);
            Assert.InRange(permissionSurface.ActualHeight, 1, 280);
            Assert.Equal(HorizontalAlignment.Right, permissionSurface.HorizontalAlignment);
            Assert.IsType<ScrollViewer>(permissionSurface.Child);
            Assert.Contains(
                StaTest.Descendants(chrome).OfType<TextBlock>(),
                text => text.Text.Contains("asking to use pop-ups", StringComparison.Ordinal));
            var safest = StaTest.FindByAutomationName<Button>(chrome, "Keep blocked");
            Assert.True(safest.IsDefault);
            Assert.True(safest.MinHeight >= 44);
            Assert.Null(safest.ToolTip);
            Assert.Contains("site remains blocked", AutomationProperties.GetHelpText(safest));
            var allowOnce = StaTest.FindByAutomationName<Button>(chrome, "Allow once");
            Assert.Null(allowOnce.ToolTip);
            Assert.Contains("once", AutomationProperties.GetHelpText(allowOnce));
            safest.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(PermissionDecision.Deny, Assert.Single(broker.Responses).Decision);
        });
    }

    [Fact]
    public void PermissionClickImmediatelyDisablesAllChoicesAndUsesOnePresenterDispatch()
    {
        StaTest.Run(() =>
        {
            var pending = new TaskCompletionSource<ControllerResult<PermissionHostCompletion>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var broker = new FakePermissionBroker { PendingResponse = pending };
            using var presenter = new PermissionPromptPresenter(broker);
            var context = Browsing();
            var prompt = PermissionPrompt(context);
            broker.Raise(prompt);
            var chrome = PermissionChrome(context, presenter);
            StaTest.Prepare(chrome);

            var allow = StaTest.FindByAutomationName<Button>(chrome, "Allow once");
            allow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            var actions = new[]
            {
                StaTest.FindByAutomationName<Button>(chrome, "Keep blocked"),
                StaTest.FindByAutomationName<Button>(chrome, "Allow once"),
            };
            Assert.All(actions, action => Assert.False(action.IsEnabled));
            Assert.All(actions, action => Assert.Null(action.ToolTip));
            Assert.Equal("Applying permission choice", AutomationProperties.GetItemStatus(
                StaTest.FindByAutomationName<Border>(chrome, "Site permission request")));
            allow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Single(broker.Responses);

            var response = broker.Responses[0];
            pending.SetResult(ControllerResult<PermissionHostCompletion>.Success(
                PermissionHostCompletion.FromAcceptedResponse(
                    response.RequestId,
                    response.Context.TabId,
                    WebPermissionCapability.Popups,
                    response.Decision)));
            PumpDispatcherUntil(() => !chrome.IsPermissionPromptVisible, TimeSpan.FromSeconds(2));
            Assert.Single(broker.Responses);
        });
    }

    [Fact]
    public void PermissionFailureTerminatesWithSafeAnnouncementAndNeverReenablesActions()
    {
        StaTest.Run(() =>
        {
            var context = Browsing();
            var rawKey = "error.permission.internal_raw_key";
            var broker = new FakePermissionBroker
            {
                ResponseError = ControllerError.Create(ControllerErrorCode.InternalFailure, rawKey),
            };
            using var presenter = new PermissionPromptPresenter(broker);
            broker.Raise(PermissionPrompt(context));
            var chrome = PermissionChrome(context, presenter);
            StaTest.Prepare(chrome);

            StaTest.FindByAutomationName<Button>(chrome, "Allow once")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpDispatcherUntil(
                () => !presenter.State.IsOpen && !chrome.IsPermissionPromptVisible,
                TimeSpan.FromSeconds(2));

            var visibleCopy = string.Join(" ", StaTest.Descendants(chrome)
                .OfType<TextBlock>()
                .Select(text => text.Text));
            Assert.Contains("Orbit could not apply that permission choice", visibleCopy);
            Assert.DoesNotContain(rawKey, visibleCopy);
            Assert.Equal("ui.permission.response_failed", presenter.State.Announcement?.MessageKey);
            Assert.Single(broker.Responses);
            Assert.DoesNotContain(
                StaTest.Descendants(chrome).OfType<TextBlock>(),
                text => text.Text.Contains("response_replayed", StringComparison.Ordinal));
        });
    }

    [Theory]
    [InlineData("Keep blocked", PermissionDecision.Deny, null)]
    [InlineData("Allow once", PermissionDecision.Allow, PermissionAllowScope.Once)]
    [InlineData("Allow for this session", PermissionDecision.Allow, PermissionAllowScope.Session)]
    [InlineData("Always allow", PermissionDecision.Allow, PermissionAllowScope.Persistent)]
    public void EveryPermissionChoiceDispatchesExactlyOnceAndClosesTerminally(
        string label,
        PermissionDecision expectedDecision,
        PermissionAllowScope? expectedScope)
    {
        StaTest.Run(() =>
        {
            var context = Browsing();
            var site = SiteIdentity.Create(new Uri("https://media.example.test")).Value!;
            var broker = new FakePermissionBroker();
            using var presenter = new PermissionPromptPresenter(broker);
            broker.Raise(new PermissionPromptState(
                new RequestId(Guid.NewGuid()),
                new ResponseToken(Guid.NewGuid()),
                context,
                site,
                site.DisplayOrigin,
                WebPermissionCapability.Notifications,
                [PermissionAllowScope.Once, PermissionAllowScope.Session, PermissionAllowScope.Persistent],
                PermissionDecision.Deny,
                PermissionDecision.Deny,
                DateTimeOffset.UtcNow.AddMinutes(1)));
            var chrome = PermissionChrome(context, presenter);
            StaTest.Prepare(chrome);

            var action = StaTest.FindByAutomationName<Button>(chrome, label);
            action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpDispatcherUntil(() => !chrome.IsPermissionPromptVisible, TimeSpan.FromSeconds(2));

            var response = Assert.Single(broker.Responses);
            Assert.Equal(expectedDecision, response.Decision);
            Assert.Equal(expectedScope, response.AllowScope);
            action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Single(broker.Responses);
            Assert.False(presenter.State.IsOpen);
        });
    }

    [Fact]
    public void PersistentPolicyDenialClosesWithTruthfulBlockedCopyAndNoReenabledChoices()
    {
        StaTest.Run(() =>
        {
            var context = Browsing();
            var site = SiteIdentity.Create(new Uri("https://media.example.test")).Value!;
            var broker = new FakePermissionBroker
            {
                ResponseError = ControllerError.Create(
                    ControllerErrorCode.PolicyDenied,
                    "error.permission.private_persistent_denied"),
            };
            using var presenter = new PermissionPromptPresenter(broker);
            broker.Raise(new PermissionPromptState(
                new RequestId(Guid.NewGuid()),
                new ResponseToken(Guid.NewGuid()),
                context,
                site,
                site.DisplayOrigin,
                WebPermissionCapability.Notifications,
                [PermissionAllowScope.Once, PermissionAllowScope.Session, PermissionAllowScope.Persistent],
                PermissionDecision.Deny,
                PermissionDecision.Deny,
                DateTimeOffset.UtcNow.AddMinutes(1)));
            var chrome = PermissionChrome(context, presenter);
            StaTest.Prepare(chrome);

            StaTest.FindByAutomationName<Button>(chrome, "Always allow")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpDispatcherUntil(() => !chrome.IsPermissionPromptVisible, TimeSpan.FromSeconds(2));

            var response = Assert.Single(broker.Responses);
            Assert.Equal(PermissionAllowScope.Persistent, response.AllowScope);
            Assert.Equal("ui.permission.response_not_allowed", presenter.State.Announcement?.MessageKey);
            var announcedCopy = string.Join(" ", StaTest.Descendants(chrome)
                .OfType<TextBlock>()
                .Select(text => text.Text));
            Assert.Contains("not allowed here", announcedCopy, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("site remains blocked", announcedCopy, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("private_persistent_denied", announcedCopy, StringComparison.Ordinal);
            Assert.DoesNotContain(StaTest.Descendants(chrome).OfType<Button>(), button =>
                new[] { "Keep blocked", "Allow once", "Allow for this session", "Always allow" }
                    .Contains(AutomationProperties.GetName(button), StringComparer.Ordinal));
        });
    }

    [Fact]
    public void GroupHeaderExposesExpandCollapseAutomationPattern()
    {
        StaTest.Run(() =>
        {
            var header = new TabGroupHeaderButton
            {
                GroupName = "Research",
                TabCount = 3,
                IsCollapsed = true,
            };
            var clicks = 0;
            header.Click += (_, _) => clicks++;
            StaTest.Prepare(header, 180, 44);
            var peer = UIElementAutomationPeer.CreatePeerForElement(header);
            var provider = Assert.IsAssignableFrom<IExpandCollapseProvider>(
                peer!.GetPattern(PatternInterface.ExpandCollapse));

            Assert.Equal(System.Windows.Automation.ExpandCollapseState.Collapsed, provider.ExpandCollapseState);
            provider.Expand();
            Assert.Equal(1, clicks);
        });
    }

    [Fact]
    public void PlacementAndCollapseShortcutsEmitPreferencesAndKeepInactiveTabsReachable()
    {
        StaTest.Run(() =>
        {
            var context = Browsing();
            var selected = Tab(context.TabId, "Selected", false);
            var inactive = Tab(new BrowserTabId(Guid.NewGuid()), "Reference", false);
            var chrome = new BrowserChromeControl();
            chrome.SetBrowsingContext(context);
            chrome.RenderTabs(
                new BrowserState(context.WindowId, selected.TabId, [selected, inactive]),
                new Dictionary<BrowserTabGroupId, TabGroupPresentation>());
            var changes = new List<BrowserWorkspacePreferences>();
            chrome.WorkspacePreferencesChanged += (_, args) => changes.Add(args.Preferences);
            StaTest.Prepare(chrome);

            Assert.True(chrome.TryHandleWorkspaceShortcut(
                Key.D2,
                ModifierKeys.Alt | ModifierKeys.Shift));
            Assert.Equal(TabStripPlacement.Left, chrome.WorkspacePreferences.TabStripPlacement);
            var tabScroller = StaTest.FindByAutomationName<ScrollViewer>(chrome, "Tabs");
            Assert.Equal(ScrollBarVisibility.Disabled, tabScroller.VerticalScrollBarVisibility);

            Assert.True(chrome.TryHandleWorkspaceShortcut(
                Key.C,
                ModifierKeys.Alt | ModifierKeys.Shift));
            Assert.True(chrome.WorkspacePreferences.CollapseToActive);
            Assert.NotNull(StaTest.FindByAutomationName<Button>(chrome, "Reference, compact"));
            Assert.NotNull(StaTest.FindByAutomationName<Button>(chrome, "Selected"));
            Assert.Equal(2, changes.Count);
        });
    }

    [Theory]
    [InlineData(TabStripPlacement.Top, OrbitControllerGlyphKind.PlacementTop)]
    [InlineData(TabStripPlacement.Left, OrbitControllerGlyphKind.PlacementLeft)]
    [InlineData(TabStripPlacement.Right, OrbitControllerGlyphKind.PlacementRight)]
    public void TabPlacementControlUsesNonBrandLayoutGlyphAndTextState(
        TabStripPlacement placement,
        OrbitControllerGlyphKind expectedGlyph)
    {
        StaTest.Run(() =>
        {
            var chrome = new BrowserChromeControl();
            chrome.WorkspacePreferencesChanged += (_, _) => { };
            chrome.ApplyWorkspacePreferences(
                BrowserWorkspacePreferences.Default with { TabStripPlacement = placement });
            StaTest.Prepare(chrome);

            var button = StaTest.FindByAutomationName<Button>(chrome, "Change tab placement");
            var glyph = Assert.IsType<OrbitControllerGlyph>(button.Content);
            Assert.Equal(expectedGlyph, glyph.Kind);
            Assert.Equal("Change tab placement", button.ToolTip);
            Assert.Equal($"Tabs: {placement}", AutomationProperties.GetItemStatus(button));
            Assert.Contains($"Tabs: {placement}", AutomationProperties.GetHelpText(button));
            Assert.True(button.IsEnabled);
            Assert.True(button.MinWidth >= 44);
            Assert.True(button.MinHeight >= 40);

            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var menu = Assert.IsType<ContextMenu>(button.ContextMenu);
            var labels = menu.Items.OfType<MenuItem>()
                .Select(item => AutomationProperties.GetName(item))
                .ToArray();
            Assert.Equal(
                Enum.GetValues<TabStripPlacement>()
                    .Select(option => option == placement ? $"Tabs: {option}" : $"Move tabs to {option}")
                    .ToArray(),
                labels.Take(3).ToArray());
            Assert.Contains("Reveal large-group previews on hover", labels);
            Assert.Contains("Reveal large-group previews on click", labels);
        });
    }

    [Fact]
    public void ReconstructedChromeProjectsPersistedClickAsSelectedAndKeepsHoverDefault()
    {
        StaTest.Run(() =>
        {
            var defaultChrome = new BrowserChromeControl();
            defaultChrome.WorkspacePreferencesChanged += (_, _) => { };
            defaultChrome.ApplyWorkspacePreferences(BrowserWorkspacePreferences.Default);
            StaTest.Prepare(defaultChrome);
            var defaultButton = StaTest.FindByAutomationName<Button>(defaultChrome, "Change tab placement");
            defaultButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var defaultMenu = Assert.IsType<ContextMenu>(defaultButton.ContextMenu);
            var hover = defaultMenu.Items.OfType<MenuItem>().Single(item =>
                AutomationProperties.GetName(item) == "Reveal large-group previews on hover");
            var click = defaultMenu.Items.OfType<MenuItem>().Single(item =>
                AutomationProperties.GetName(item) == "Reveal large-group previews on click");
            Assert.True(hover.IsChecked);
            Assert.Equal("Selected", AutomationProperties.GetItemStatus(hover));
            Assert.False(click.IsChecked);
            Assert.Equal("Not selected", AutomationProperties.GetItemStatus(click));
            defaultMenu.IsOpen = false;

            var persisted = BrowserWorkspacePreferences.Default with
            {
                WorkspacePreviewReveal = WorkspacePreviewRevealMode.Click,
            };
            var reconstructed = new BrowserChromeControl();
            reconstructed.WorkspacePreferencesChanged += (_, _) => { };
            reconstructed.ApplyWorkspacePreferences(persisted);
            StaTest.Prepare(reconstructed);
            var reconstructedButton = StaTest.FindByAutomationName<Button>(
                reconstructed,
                "Change tab placement");
            reconstructedButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var reconstructedMenu = Assert.IsType<ContextMenu>(reconstructedButton.ContextMenu);
            var persistedClick = reconstructedMenu.Items.OfType<MenuItem>().Single(item =>
                AutomationProperties.GetName(item) == "Reveal large-group previews on click");
            var persistedHover = reconstructedMenu.Items.OfType<MenuItem>().Single(item =>
                AutomationProperties.GetName(item) == "Reveal large-group previews on hover");
            var peer = new MenuItemAutomationPeer(persistedClick);
            var toggle = Assert.IsAssignableFrom<IToggleProvider>(
                peer.GetPattern(PatternInterface.Toggle));

            Assert.Equal(WorkspacePreviewRevealMode.Click,
                reconstructed.WorkspacePreferences.WorkspacePreviewReveal);
            Assert.True(persistedClick.IsChecked);
            Assert.Equal("Selected", AutomationProperties.GetItemStatus(persistedClick));
            Assert.Equal(ToggleState.On, toggle.ToggleState);
            Assert.False(persistedHover.IsChecked);
            Assert.Equal("Not selected", AutomationProperties.GetItemStatus(persistedHover));
        });
    }

    [Theory]
    [InlineData(TabStripPlacement.Top, Orientation.Horizontal)]
    [InlineData(TabStripPlacement.Left, Orientation.Vertical)]
    [InlineData(TabStripPlacement.Right, Orientation.Vertical)]
    public void PersistedDetachedControllerDocksBeforePlacementAndReleasesStaleRail(
        TabStripPlacement requestedPlacement,
        Orientation expectedOverflowOrientation)
    {
        StaTest.Run(() =>
        {
            var context = Browsing();
            var tabs = Enumerable.Range(0, 16)
                .Select(index => (TabStripEntry)new BrowserTabEntry(
                    index == 0 ? context.TabId : new BrowserTabId(Guid.NewGuid()),
                    null,
                    index == 0 ? "Selected tab" : $"Restored tab {index + 1}",
                    new Uri($"https://restored-{index + 1}.example.test/"),
                    BrowserLoadState.Idle,
                    index == 0,
                    false,
                    false,
                    false))
                .ToArray();
            var initial = new RevisionedTabControllerProjection(
                context.WindowId,
                false,
                1,
                TabControllerHostState.Detached,
                TabStripPlacement.Right,
                new(context.WindowId, context.TabId, tabs));
            var docked = initial with
            {
                Revision = 2,
                HostState = TabControllerHostState.Docked,
            };
            var sink = new DockingPlacementSink(docked);
            var session = new TabControllerPresentationSession(context.WindowId, false, sink);
            session.AcceptProjection(initial);
            var chrome = new BrowserChromeControl();
            chrome.ApplyWorkspacePreferences(BrowserWorkspacePreferences.Default with
            {
                TabStripPlacement = TabStripPlacement.Right,
            });
            chrome.BindTabControllerSession(session);
            var detached = chrome.CreateDetachedTabControllerControl();
            chrome.WorkspacePreferencesChanged += (_, args) =>
            {
                session.AcceptProjection(docked with
                {
                    Revision = 3,
                    Placement = args.Preferences.TabStripPlacement,
                });
                chrome.ApplyWorkspacePreferences(args.Preferences);
            };
            var window = new Window { Content = chrome, Width = 1200, Height = 800, ShowInTaskbar = false };
            window.Show();
            try
            {
                window.UpdateLayout();
                Assert.True(chrome.IsShowTabsRecoveryVisible);
                Assert.False(chrome.IsDockedTabControllerVisible);
                Assert.Contains(StaTest.Descendants(detached).OfType<Button>(), button =>
                    button.Tag is BrowserTabEntry);

                var placementButton = StaTest.FindByAutomationName<Button>(chrome, "Change tab placement");
                placementButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var menu = Assert.IsType<ContextMenu>(placementButton.ContextMenu);
                var label = requestedPlacement == TabStripPlacement.Right
                    ? "Tabs: Right"
                    : $"Move tabs to {requestedPlacement}";
                var placementItem = menu.Items.OfType<MenuItem>()
                    .Single(item => AutomationProperties.GetName(item) == label);
                placementItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

                PumpDispatcherUntil(
                    () => chrome.WorkspacePreferences.TabStripPlacement == requestedPlacement &&
                          chrome.IsDockedTabControllerVisible &&
                          !chrome.IsShowTabsRecoveryVisible,
                    TimeSpan.FromSeconds(2));

                Assert.Collection(sink.Actions, action => Assert.IsType<DockTabControllerAction>(action));
                Assert.Equal(TabControllerHostState.Docked, session.Current!.Projection.HostState);
                Assert.Equal(requestedPlacement, session.Current.Projection.Placement);
                Assert.DoesNotContain(StaTest.Descendants(detached).OfType<Button>(), button =>
                    button.Tag is BrowserTabEntry);
                var dockedControl = StaTest.Descendants(chrome).OfType<TabControllerControl>()
                    .Single(control => control.SurfaceKind == TabControllerSurfaceKind.Docked);
                var overflow = StaTest.FindByAutomationName<ScrollBar>(dockedControl, "Tab overflow position");
                Assert.Equal(expectedOverflowOrientation, overflow.Orientation);
                Assert.Equal(Visibility.Visible, overflow.Visibility);
                Assert.Contains(StaTest.Descendants(dockedControl).OfType<Button>(), button =>
                    AutomationProperties.GetName(button) == "Selected tab, tab" && button.IsVisible);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void MainMenuMarksEveryUnavailableRouteInsteadOfAcceptingDeadClicks()
    {
        StaTest.Run(() =>
        {
            var context = Browsing();
            var chrome = new BrowserChromeControl();
            chrome.SetBrowsingContext(context);
            chrome.RenderTabs(
                new BrowserState(context.WindowId, context.TabId, [Tab(context.TabId, "New tab", false)]),
                new Dictionary<BrowserTabGroupId, TabGroupPresentation>());
            StaTest.Prepare(chrome);

            var menuButton = StaTest.FindByAutomationName<Button>(chrome, "Open browser menu");
            menuButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var menu = Assert.IsType<ContextMenu>(menuButton.ContextMenu);
            var commands = menu.Items.OfType<MenuItem>().ToArray();

            Assert.Equal(9, commands.Length);
            Assert.All(commands, command =>
            {
                Assert.False(command.IsEnabled);
                Assert.EndsWith(", unavailable", AutomationProperties.GetName(command));
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(command)));
            });
        });
    }

    [Fact]
    public void MainMenuRoutesSettingsAndAllVisibleWorkflowsWhenHostIsReady()
    {
        StaTest.Run(() =>
        {
            var context = Browsing();
            var chrome = new BrowserChromeControl();
            chrome.SetBrowsingContext(context);
            chrome.RenderTabs(
                new BrowserState(context.WindowId, context.TabId, [Tab(context.TabId, "New tab", false)]),
                new Dictionary<BrowserTabGroupId, TabGroupPresentation>());
            chrome.BrowserCommandRequested += (_, _) => { };
            chrome.PrivateWindowRequested += (_, _) => { };
            UtilitySurfaceRequestedEventArgs? requested = null;
            chrome.UtilitySurfaceRequested += (_, args) => requested = args;
            chrome.OfflineReadingActionRequested += (_, _) => { };
            chrome.ApplyOfflineReading(new(
                1,
                false,
                true,
                "New Tab",
                new Uri("https://example.test/"),
                false,
                "Ready to save.",
                []));
            chrome.SetUtilitySurfaceAvailability(UtilityDrawerKind.Bookmarks, true, string.Empty);
            chrome.SetUtilitySurfaceAvailability(UtilityDrawerKind.History, true, string.Empty);
            chrome.SetUtilitySurfaceAvailability(UtilityDrawerKind.Downloads, true, string.Empty);
            chrome.SetUtilitySurfaceAvailability(UtilityDrawerKind.ClipboardShelf, true, string.Empty);
            chrome.SetUtilitySurfaceAvailability(InternalPageKind.Settings, true, string.Empty);
            StaTest.Prepare(chrome);

            var menuButton = StaTest.FindByAutomationName<Button>(chrome, "Open browser menu");
            menuButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var menu = Assert.IsType<ContextMenu>(menuButton.ContextMenu);
            var commands = menu.Items.OfType<MenuItem>().ToArray();
            Assert.All(commands, command => Assert.True(command.IsEnabled));

            var settings = commands.Single(command => AutomationProperties.GetName(command) == "Settings");
            settings.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(InternalPageKind.Settings, requested?.Page);
        });
    }

    [Fact]
    public void MainMenuTracksEachUtilityCapabilityIndependently()
    {
        StaTest.Run(() =>
        {
            var context = Browsing();
            var chrome = new BrowserChromeControl();
            chrome.SetBrowsingContext(context);
            chrome.RenderTabs(
                new BrowserState(context.WindowId, context.TabId, [Tab(context.TabId, "New tab", false)]),
                new Dictionary<BrowserTabGroupId, TabGroupPresentation>());
            chrome.UtilitySurfaceRequested += (_, _) => { };
            chrome.SetUtilitySurfaceAvailability(UtilityDrawerKind.Bookmarks, true, string.Empty);
            chrome.SetUtilitySurfaceAvailability(InternalPageKind.Settings, true, string.Empty);
            chrome.SetUtilitySurfaceAvailability(
                UtilityDrawerKind.Downloads,
                false,
                "Downloads will be available after download tracking is connected.");
            StaTest.Prepare(chrome);

            var menuButton = StaTest.FindByAutomationName<Button>(chrome, "Open browser menu");
            menuButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var menu = Assert.IsType<ContextMenu>(menuButton.ContextMenu);
            var commands = menu.Items.OfType<MenuItem>().ToArray();

            Assert.True(commands.Single(item => AutomationProperties.GetName(item) == "Bookmarks").IsEnabled);
            Assert.True(commands.Single(item => AutomationProperties.GetName(item) == "Settings").IsEnabled);
            var downloads = commands.Single(item => AutomationProperties.GetName(item) == "Downloads, unavailable");
            Assert.False(downloads.IsEnabled);
            Assert.Equal(
                "Downloads will be available after download tracking is connected.",
                AutomationProperties.GetHelpText(downloads));
        });
    }

    [Theory]
    [InlineData(TabStripPlacement.Top)]
    [InlineData(TabStripPlacement.Left)]
    [InlineData(TabStripPlacement.Right)]
    public void DetachedProjectionReclaimsDockedSpaceAndShowTabsRequestsActivation(
        TabStripPlacement placement)
    {
        StaTest.Run(() =>
        {
            var context = Browsing();
            var session = ControllerSession(context, placement, TabControllerHostState.Docked, 1);
            var chrome = new BrowserChromeControl();
            chrome.ApplyWorkspacePreferences(BrowserWorkspacePreferences.Default with { TabStripPlacement = placement });
            chrome.BindTabControllerSession(session);
            var activationRequests = 0;
            chrome.ShowTabsRequested += (_, _) => activationRequests++;
            StaTest.Prepare(chrome, 900, 700);
            Assert.True(chrome.IsDockedTabControllerVisible);

            session.AcceptProjection(ControllerProjection(
                context,
                placement,
                TabControllerHostState.Detached,
                2));

            Assert.False(chrome.IsDockedTabControllerVisible);
            Assert.True(chrome.IsShowTabsRecoveryVisible);
            if (placement == TabStripPlacement.Top)
            {
                Assert.Equal(0, chrome.RowDefinitions[0].Height.Value);
            }
            else
            {
                Assert.Contains(chrome.ColumnDefinitions, column => column.Width.Value == 0);
            }

            StaTest.FindByAutomationName<Button>(chrome, "Show detached tabs")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, activationRequests);

            session.AcceptProjection(ControllerProjection(
                context,
                placement,
                TabControllerHostState.Docked,
                3));
            Assert.True(chrome.IsDockedTabControllerVisible);
            Assert.False(chrome.IsShowTabsRecoveryVisible);
        });
    }

    [Fact]
    public void MainAndDetachedResourcesRequestOneHostMonitorWithoutEmbeddedPopup()
    {
        StaTest.Run(() =>
        {
            var context = Browsing();
            var session = ControllerSession(context, TabStripPlacement.Top, TabControllerHostState.Docked, 1);
            var chrome = new BrowserChromeControl { ReducedMotion = true };
            chrome.BindTabControllerSession(session);
            var requests = new List<ResourceMonitorRequestedEventArgs>();
            chrome.ResourceMonitorRequested += (_, request) => requests.Add(request);
            var window = new Window { Content = chrome, Width = 900, Height = 700, ShowInTaskbar = false };
            window.Show();
            try
            {
                StaTest.Prepare(chrome, 900, 700);
                var resources = StaTest.FindByAutomationName<Button>(chrome, "Browser resources");
                resources.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                var dockedRequest = Assert.Single(requests);
                Assert.Equal(ResourceMonitorRequestSource.DockedTabController, dockedRequest.Source);
                Assert.True(dockedRequest.IsVisibleRequested);
                Assert.True(dockedRequest.FocusWhenShown);
                Assert.False(chrome.IsResourceMonitorVisible);
                var presentationPopups = StaTest.Descendants(chrome).OfType<Popup>().ToArray();
                Assert.Single(presentationPopups);
                Assert.Same(chrome.QuickView.OverlayPopup, presentationPopups[0]);
                Assert.False(presentationPopups[0].IsOpen);

                chrome.ApplyResourceMonitorVisibility(true);
                Assert.True(chrome.IsResourceMonitorVisible);
                Assert.True(((OrbitShapedCommand)resources).IsSelected);

                var detached = chrome.CreateDetachedTabControllerControl();
                var detachedWindow = new Window
                {
                    Content = detached,
                    Width = 480,
                    Height = 560,
                    ShowInTaskbar = false,
                };
                detachedWindow.Show();
                detachedWindow.UpdateLayout();
                var detachedResources = StaTest.FindByAutomationName<OrbitShapedCommand>(detached, "Browser resources");
                Assert.True(detached.IsResourcePanelOpen);
                Assert.True(detachedResources.IsSelected);

                chrome.ApplyResourceMonitorVisibility(false);
                Assert.False(chrome.IsResourceMonitorVisible);
                Assert.False(((OrbitShapedCommand)resources).IsSelected);
                Assert.False(detached.IsResourcePanelOpen);
                Assert.False(detachedResources.IsSelected);

                detachedResources.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(ResourceMonitorRequestSource.DetachedTabController, requests[^1].Source);
                Assert.True(requests[^1].IsVisibleRequested);
                chrome.ApplyResourceMonitorVisibility(true);
                Assert.True(((OrbitShapedCommand)resources).IsSelected);
                Assert.True(detachedResources.IsSelected);

                chrome.RequestResourceMonitor(source: ResourceMonitorRequestSource.MainBrowserToolbar);
                Assert.Equal(ResourceMonitorRequestSource.MainBrowserToolbar, requests[^1].Source);
                Assert.Equal(3, requests.Count);

                var monitor = chrome.CreateResourceTaskWindow(window);
                Assert.Same(monitor, chrome.CreateResourceTaskWindow(window));

                detachedWindow.Close();
                detached.Unbind();
                var requestCountAfterUnload = requests.Count;
                detachedResources.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(requestCountAfterUnload, requests.Count);
                chrome.ApplyResourceMonitorVisibility(false);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void PlacementSwitchImmediatelyReorientsBoundControllerWithoutTabMutation()
    {
        StaTest.Run(() =>
        {
            var context = Browsing();
            var projection = ControllerProjection(
                context,
                TabStripPlacement.Left,
                TabControllerHostState.Docked,
                1);
            var session = new TabControllerPresentationSession(
                context.WindowId,
                false,
                new RoutedShortcutSink(projection));
            session.AcceptProjection(projection);
            var chrome = new BrowserChromeControl();
            chrome.ApplyWorkspacePreferences(new(TabStripPlacement.Left, false, true));
            chrome.RenderTabs(
                BrowserStateFromProjection(projection),
                new Dictionary<BrowserTabGroupId, TabGroupPresentation>());
            chrome.BindTabControllerSession(session);
            var window = new Window { Content = chrome, Width = 1200, Height = 760, ShowInTaskbar = false };
            window.Show();
            try
            {
                window.UpdateLayout();
                chrome.ApplyWorkspacePreferences(new(TabStripPlacement.Top, false, true));
                PumpDispatcherUntil(() =>
                {
                    var controller = StaTest.FindByAutomationName<TabControllerControl>(
                        chrome, "Browser tab controller");
                    return controller.OverflowScrollBar.Orientation == Orientation.Horizontal;
                }, TimeSpan.FromSeconds(2));
                window.UpdateLayout();

                var bound = StaTest.FindByAutomationName<TabControllerControl>(
                    chrome, "Browser tab controller");
                Assert.Equal(TabStripPlacement.Left, session.Current!.Projection.Placement);
                Assert.Equal(TabStripPlacement.Top, chrome.WorkspacePreferences.TabStripPlacement);
                Assert.Equal(Orientation.Horizontal, bound.OverflowScrollBar.Orientation);
                var selected = StaTest.FindByAutomationName<Button>(chrome, "Selected tab, tab");
                Assert.Equal(Visibility.Visible, selected.Visibility);
                Assert.True(selected.ActualWidth >= 88 && selected.ActualHeight >= 40);
                foreach (var prefix in new[]
                         { "Open new tab", "More tabs", "Browser resources", "Pop out tab controller" })
                {
                    Assert.Contains(StaTest.Descendants(bound).OfType<Button>(), button =>
                        AutomationProperties.GetName(button).StartsWith(prefix, StringComparison.Ordinal) &&
                        button.Visibility == Visibility.Visible);
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void AsyncDetachResultMarshalsEveryPresentationMutationToTheWpfDispatcher()
    {
        StaTest.Run(() =>
        {
            var context = Browsing();
            var initial = ControllerProjection(
                context,
                TabStripPlacement.Top,
                TabControllerHostState.Docked,
                1);
            var detached = ControllerProjection(
                context,
                TabStripPlacement.Top,
                TabControllerHostState.Detached,
                2);
            var sink = new WorkerThreadDetachSink(detached);
            var session = new TabControllerPresentationSession(context.WindowId, false, sink);
            session.AcceptProjection(initial);
            var chrome = new BrowserChromeControl();
            chrome.RenderTabs(
                BrowserStateFromProjection(initial),
                new Dictionary<BrowserTabGroupId, TabGroupPresentation>());
            chrome.BindTabControllerSession(session);
            var window = new Window { Content = chrome, Width = 900, Height = 700, ShowInTaskbar = false };
            Exception? dispatcherFailure = null;
            DispatcherUnhandledExceptionEventHandler failureHandler = (_, args) =>
            {
                dispatcherFailure = args.Exception;
                args.Handled = true;
            };
            Dispatcher.CurrentDispatcher.UnhandledException += failureHandler;
            window.Show();
            try
            {
                StaTest.Prepare(chrome, 900, 700);
                StaTest.FindByAutomationName<Button>(chrome, "Pop out tab controller")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                PumpDispatcherUntil(
                    () => chrome.IsShowTabsRecoveryVisible,
                    TimeSpan.FromSeconds(3));

                Assert.Null(dispatcherFailure);
                Assert.NotEqual(Environment.CurrentManagedThreadId, sink.CompletionThreadId);
                Assert.True(chrome.Dispatcher.CheckAccess());
                Assert.False(chrome.IsDockedTabControllerVisible);
                Assert.True(chrome.IsShowTabsRecoveryVisible);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.UnhandledException -= failureHandler;
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(900)]
    [InlineData(420)]
    public void RoutedCtrlTThenCtrlWRestoresUiaFocusToAuthoritativeSurvivingTab(double width)
    {
        StaTest.Run(() =>
        {
            var context = Browsing();
            var survivor = context.TabId;
            var created = new BrowserTabId(Guid.NewGuid());
            var initial = ProjectionWithTabs(
                context,
                1,
                survivor,
                (survivor, "Surviving tab"));
            var afterCreate = ProjectionWithTabs(
                context,
                2,
                created,
                (survivor, "Surviving tab"),
                (created, "Created tab"));
            var afterClose = ProjectionWithTabs(
                context,
                3,
                survivor,
                (survivor, "Surviving tab"));
            var session = new TabControllerPresentationSession(
                context.WindowId,
                false,
                new RoutedShortcutSink(afterClose));
            session.AcceptProjection(initial);
            var chrome = new BrowserChromeControl();
            chrome.RenderTabs(
                BrowserStateFromProjection(initial),
                new Dictionary<BrowserTabGroupId, TabGroupPresentation>());
            chrome.BindTabControllerSession(session);
            chrome.BrowserCommandRequested += (_, command) =>
            {
                if (command is CreateTabBrowserCommand)
                {
                    chrome.RenderTabs(
                        BrowserStateFromProjection(afterCreate),
                        new Dictionary<BrowserTabGroupId, TabGroupPresentation>());
                    session.AcceptProjection(afterCreate);
                }
            };
            var window = new Window { Content = chrome, Width = width, Height = 700, ShowInTaskbar = false };
            window.Show();
            try
            {
                window.UpdateLayout();
                var keyboard = new ControlKeyboardDevice(InputManager.Current);
                var initialButton = StaTest.FindByAutomationName<Button>(chrome, "Surviving tab, tab");
                Keyboard.Focus(initialButton);
                RaiseCtrlKey(initialButton, keyboard, Key.T);
                PumpDispatcherUntil(
                    () => session.Current?.Projection.Revision == 2,
                    TimeSpan.FromSeconds(2));

                var createdButton = StaTest.FindByAutomationName<Button>(chrome, "Created tab, tab");
                Keyboard.Focus(createdButton);
                RaiseCtrlKey(createdButton, keyboard, Key.W);
                PumpDispatcherUntil(
                    () => session.Current?.Projection.Revision == 3 &&
                          Keyboard.FocusedElement is Button focused &&
                          AutomationProperties.GetName(focused) == "Surviving tab, tab",
                    TimeSpan.FromSeconds(2));

                var survivorButton = StaTest.FindByAutomationName<Button>(chrome, "Surviving tab, tab");
                Assert.Same(survivorButton, Keyboard.FocusedElement);
                Assert.True(survivorButton.IsKeyboardFocusWithin);
                Assert.NotSame(window, Keyboard.FocusedElement);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void CompactTabToggleWaitsForHostAcceptanceAndAppliesToDockedAndDetachedViews()
    {
        StaTest.Run(() =>
        {
            var context = Browsing();
            var session = ControllerSession(context, TabStripPlacement.Top, TabControllerHostState.Docked, 1);
            var chrome = new BrowserChromeControl();
            chrome.BindTabControllerSession(session);
            CompactTabModeChangeRequestedEventArgs? request = null;
            chrome.CompactTabModeChangeRequested += (_, args) => request = args;
            var window = new Window { Content = chrome, Width = 1100, Height = 700, ShowInTaskbar = false };
            window.Show();
            try
            {
                window.UpdateLayout();
                var toggle = StaTest.FindByAutomationName<Button>(chrome, "Use compact favicon-only tabs");
                Assert.True(toggle.IsEnabled);
                toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(request?.IsCompactModeRequested);
                Assert.False(chrome.IsCompactTabMode);

                chrome.ApplyCompactTabMode(true);
                Assert.True(chrome.IsCompactTabMode);
                Assert.Equal("Compact tabs on", AutomationProperties.GetItemStatus(
                    StaTest.FindByAutomationName<Button>(chrome, "Show tab titles")));
                var docked = StaTest.FindByAutomationName<TabControllerControl>(chrome, "Browser tab controller");
                Assert.True(docked.IsCompactMode);
                var detached = chrome.CreateDetachedTabControllerControl();
                Assert.True(detached.IsCompactMode);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void VisualMetadataQueuedBeforeFirstProjectionIsReplayedToTheBoundController()
    {
        StaTest.Run(() =>
        {
            var context = Browsing();
            var session = new TabControllerPresentationSession(context.WindowId, false, new ControllerSink());
            var chrome = new BrowserChromeControl();
            chrome.BindTabControllerSession(session);
            var png = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9ZSmAAAAAASUVORK5CYII=");

            Assert.True(chrome.ApplyTabVisualMetadata(new(
                context.TabId,
                1,
                "Queued document title",
                "Queued service",
                png)));
            session.AcceptProjection(ControllerProjection(
                context,
                TabStripPlacement.Top,
                TabControllerHostState.Docked,
                1));
            StaTest.Prepare(chrome, 1000, 700);

            var tab = StaTest.FindByAutomationName<Button>(
                chrome,
                "Queued document title, tab");
            Assert.Single(StaTest.Descendants(tab).OfType<TextBlock>(),
                text => text.Text == "Queued document title");
            Assert.DoesNotContain(StaTest.Descendants(tab).OfType<TextBlock>(),
                text => text.Text == "Queued service");
            Assert.Contains("Site: Queued service", AutomationProperties.GetHelpText(tab));
        });
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void FixedHeightAddressAndNewTabInputsKeepCenteredUnclippedText(double scale)
    {
        StaTest.Run(() =>
        {
            var chrome = new BrowserChromeControl();
            var chromeWindow = new Window { Content = chrome, Width = 1100, Height = 700, ShowInTaskbar = false };
            chromeWindow.Show();
            try
            {
                var address = StaTest.FindByAutomationName<TextBox>(chrome, "Address and search");
                address.Text = "https://example.test/glyphs?q=gjpqy";
                AssertFixedHeightTextLayout(address, 40, scale);
            }
            finally
            {
                chromeWindow.Close();
            }

            var newTab = new NewTabPageControl { ReducedMotion = true };
            var newTabWindow = new Window { Content = newTab, Width = 1100, Height = 800, ShowInTaskbar = false };
            newTabWindow.Show();
            try
            {
                var search = StaTest.FindByAutomationName<TextBox>(newTab, "Search or enter address");
                search.Text = "Search glyphs gjpqy";
                AssertFixedHeightTextLayout(search, 48, scale);
            }
            finally
            {
                newTabWindow.Close();
            }
        });
    }

    private static void AssertFixedHeightTextLayout(TextBox textBox, double expectedHeight, double scale)
    {
        textBox.LayoutTransform = new ScaleTransform(scale, scale);
        textBox.ApplyTemplate();
        textBox.UpdateLayout();
        var host = Assert.IsType<ScrollViewer>(textBox.Template.FindName("PART_ContentHost", textBox));
        var origin = host.TranslatePoint(new Point(), textBox);
        Assert.Equal(expectedHeight, textBox.Height);
        Assert.Equal(3, textBox.Padding.Top);
        Assert.True(host.ActualHeight >= textBox.FontSize,
            $"Content host {host.ActualHeight} is shorter than font size {textBox.FontSize} at {scale:P0}.");
        Assert.True(origin.Y >= 0);
        Assert.True(origin.Y + host.ActualHeight <= textBox.ActualHeight + 0.5);
    }

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in VisualDescendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static BrowsingContext Browsing(BrowserProfileMode mode = BrowserProfileMode.Normal) =>
        new(
            new PrivacyContext(
                new ProfileId(Guid.NewGuid()),
                new BrowserSessionId(Guid.NewGuid()),
                mode),
            new BrowserWindowId(Guid.NewGuid()),
            new BrowserTabId(Guid.NewGuid()),
            null);

    private static PermissionPromptState PermissionPrompt(BrowsingContext context)
    {
        var site = SiteIdentity.Create(new Uri("https://media.example.test")).Value!;
        return new PermissionPromptState(
            new RequestId(Guid.NewGuid()),
            new ResponseToken(Guid.NewGuid()),
            context,
            site,
            site.DisplayOrigin,
            WebPermissionCapability.Popups,
            [PermissionAllowScope.Once],
            PermissionDecision.Deny,
            PermissionDecision.Deny,
            DateTimeOffset.UtcNow.AddMinutes(1));
    }

    private static BrowserChromeControl PermissionChrome(
        BrowsingContext context,
        PermissionPromptPresenter presenter)
    {
        var chrome = new BrowserChromeControl();
        chrome.SetBrowsingContext(context);
        chrome.RenderTabs(
            new BrowserState(context.WindowId, context.TabId, [Tab(context.TabId, "Media", false)]),
            new Dictionary<BrowserTabGroupId, TabGroupPresentation>());
        chrome.BindPermissionPrompt(presenter);
        return chrome;
    }

    private static BrowserTabState Tab(BrowserTabId id, string title, bool isPrivate) =>
        new(id, null, null, title, BrowserLoadState.Idle, false, false, isPrivate);

    private static TabControllerPresentationSession ControllerSession(
        BrowsingContext context,
        TabStripPlacement placement,
        TabControllerHostState hostState,
        long revision)
    {
        var session = new TabControllerPresentationSession(context.WindowId, context.Privacy.IsPrivate, new ControllerSink());
        session.AcceptProjection(ControllerProjection(context, placement, hostState, revision));
        return session;
    }

    private static RevisionedTabControllerProjection ControllerProjection(
        BrowsingContext context,
        TabStripPlacement placement,
        TabControllerHostState hostState,
        long revision)
    {
        var tab = new BrowserTabEntry(
            context.TabId,
            null,
            "Selected tab",
            new Uri("https://example.test/"),
            BrowserLoadState.Idle,
            true,
            context.Privacy.IsPrivate,
            false,
            false);
        return new(
            context.WindowId,
            context.Privacy.IsPrivate,
            revision,
            hostState,
            placement,
            new(context.WindowId, context.TabId, [tab]));
    }

    private sealed class ControllerSink : ITabControllerCommandSink
    {
        public ValueTask<TabControllerCommandResult> ExecuteAsync(
            TabControllerCommand command,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new TabControllerCommandResult(
                command.Action.StableActionId,
                TabControllerCommandOutcome.Accepted,
                "Completed."));
    }

    private sealed class DockingPlacementSink(
        RevisionedTabControllerProjection docked) : ITabControllerCommandSink
    {
        public List<TabControllerAction> Actions { get; } = [];

        public ValueTask<TabControllerCommandResult> ExecuteAsync(
            TabControllerCommand command,
            CancellationToken cancellationToken = default)
        {
            Actions.Add(command.Action);
            Assert.IsType<DockTabControllerAction>(command.Action);
            return ValueTask.FromResult(new TabControllerCommandResult(
                command.Action.StableActionId,
                TabControllerCommandOutcome.Accepted,
                "Tab controller docked.",
                docked));
        }
    }

    private sealed class WorkerThreadDetachSink(RevisionedTabControllerProjection detached) : ITabControllerCommandSink
    {
        public int CompletionThreadId { get; private set; }

        public async ValueTask<TabControllerCommandResult> ExecuteAsync(
            TabControllerCommand command,
            CancellationToken cancellationToken = default)
        {
            await Task.Run(() => Thread.Sleep(20), cancellationToken).ConfigureAwait(false);
            CompletionThreadId = Environment.CurrentManagedThreadId;
            return new(
                command.Action.StableActionId,
                TabControllerCommandOutcome.Accepted,
                "Tab controller detached.",
                detached);
        }
    }

    private static RevisionedTabControllerProjection ProjectionWithTabs(
        BrowsingContext context,
        long revision,
        BrowserTabId selected,
        params (BrowserTabId Id, string Title)[] tabs) =>
        new(
            context.WindowId,
            false,
            revision,
            TabControllerHostState.Docked,
            TabStripPlacement.Top,
            new(
                context.WindowId,
                selected,
                tabs.Select(tab => new BrowserTabEntry(
                    tab.Id,
                    null,
                    tab.Title,
                    new Uri($"https://{tab.Id.Value:N}.test/"),
                    BrowserLoadState.Idle,
                    tab.Id == selected,
                    false,
                    false,
                false)).ToArray()));

    private static BrowserState BrowserStateFromProjection(RevisionedTabControllerProjection projection) =>
        new(
            projection.WindowId,
            projection.Tabs.SelectedTabId,
            projection.Tabs.Entries.OfType<BrowserTabEntry>().Select(tab => new BrowserTabState(
                tab.TabId,
                tab.GroupId,
                tab.Address,
                tab.Title,
                tab.LoadState,
                tab.CanGoBack,
                tab.CanGoForward,
                tab.IsPrivate)).ToArray());

    private sealed class RoutedShortcutSink(
        RevisionedTabControllerProjection afterClose) : ITabControllerCommandSink
    {
        public ValueTask<TabControllerCommandResult> ExecuteAsync(
            TabControllerCommand command,
            CancellationToken cancellationToken = default)
        {
            var projection = command.Action switch
            {
                CloseTabControllerAction => afterClose,
                _ => throw new InvalidOperationException("Unexpected shortcut action."),
            };
            return ValueTask.FromResult(new TabControllerCommandResult(
                command.Action.StableActionId,
                TabControllerCommandOutcome.Accepted,
                "Tab closed.",
                projection));
        }
    }

    private sealed class ControlKeyboardDevice(InputManager inputManager) : KeyboardDevice(inputManager)
    {
        protected override KeyStates GetKeyStatesFromSystem(Key key) =>
            key is Key.LeftCtrl or Key.RightCtrl ? KeyStates.Down : KeyStates.None;
    }

    private static void RaiseCtrlKey(UIElement source, KeyboardDevice keyboard, Key key)
    {
        var presentationSource = PresentationSource.FromVisual(source)
            ?? throw new InvalidOperationException("The routed-key source must be connected to a PresentationSource.");
        var args = new KeyEventArgs(keyboard, presentationSource, Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
            Source = source,
        };
        source.RaiseEvent(args);
        Assert.True(args.Handled);
    }

    private static void PumpDispatcherUntil(Func<bool> condition, TimeSpan timeout)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(10),
            DispatcherPriority.Background,
            (_, _) =>
            {
                if (condition())
                {
                    frame.Continue = false;
                }
            },
            Dispatcher.CurrentDispatcher);
        var timeoutTimer = new DispatcherTimer(
            timeout,
            DispatcherPriority.Send,
            (_, _) => frame.Continue = false,
            Dispatcher.CurrentDispatcher);
        timer.Start();
        timeoutTimer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        timeoutTimer.Stop();
        Assert.True(condition(), "The dispatcher did not reach the expected Presentation state before timeout.");
    }

    private sealed class FakePermissionBroker : IPermissionBroker
    {
        public event EventHandler<PermissionPromptEventArgs>? PromptRequested;

        public List<PermissionResponse> Responses { get; } = [];

        public ControllerError? ResponseError { get; set; }

        public TaskCompletionSource<ControllerResult<PermissionHostCompletion>>? PendingResponse { get; init; }

        public void Raise(PermissionPromptState prompt) =>
            PromptRequested?.Invoke(this, new PermissionPromptEventArgs(prompt));

        public ValueTask<ControllerResult<PermissionPromptState>> IngestAsync(
            PermissionBrokerRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ControllerResult<PermissionPromptState>.Failure(Error()));

        public ValueTask<ControllerResult<PermissionHostCompletion>> RespondAsync(
            PermissionResponse response,
            CancellationToken cancellationToken)
        {
            Responses.Add(response);
            if (PendingResponse is not null)
            {
                return new ValueTask<ControllerResult<PermissionHostCompletion>>(PendingResponse.Task);
            }
            if (ResponseError is not null)
            {
                return ValueTask.FromResult(
                    ControllerResult<PermissionHostCompletion>.Failure(ResponseError));
            }
            return ValueTask.FromResult(ControllerResult<PermissionHostCompletion>.Success(
                PermissionHostCompletion.FromAcceptedResponse(
                    response.RequestId,
                    response.Context.TabId,
                    WebPermissionCapability.Popups,
                    response.Decision)));
        }

        public ValueTask<ControllerResult<SitePermissionState>> GetCurrentSiteStateAsync(
            BrowsingContext context,
            SiteIdentity site,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ControllerResult<SitePermissionState>.Failure(Error()));

        public ValueTask<ControllerResult> ResetAsync(
            ResetPermissionRuleIntent intent,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ControllerResult.Failure(Error()));

        private static ControllerError Error() => ControllerError.Create(
            ControllerErrorCode.NotSupported,
            "error.test.not_used");
    }
}
