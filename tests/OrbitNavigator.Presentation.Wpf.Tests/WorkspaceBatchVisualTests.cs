using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Presentation.QuickView;
using OrbitNavigator.Presentation.Tabs;
using OrbitNavigator.Presentation.Workspace;
using OrbitNavigator.Presentation.Wpf;

using Xunit;

namespace OrbitNavigator.Presentation.Wpf.Tests;

[Collection("WPF focus-sensitive")]
public sealed class WorkspaceBatchVisualTests
{
    [Fact]
    public void NewTabPersistsBasicModeAffiliateSideAndPrivateVisualStatus() => StaTest.Run(() =>
    {
        var page = new NewTabPageControl { ReducedMotion = true };
        page.ApplyWorkspacePreferences(BrowserWorkspacePreferences.Default with
        {
            NewTabMode = NewTabVisualMode.Basic,
            AffiliatedRailPlacement = AffiliatedRailPlacement.Right,
            ShowAffiliatedRail = true,
        });
        page.ApplyPrivateMode(true);
        StaTest.Prepare(page, 1280, 900);

        Assert.Equal(NewTabVisualMode.Basic, page.WorkspacePreferences.NewTabMode);
        Assert.True(page.IsPrivateMode);
        Assert.IsType<LinearGradientBrush>(page.Background);
        var rail = Assert.Single(StaTest.Descendants(page).OfType<AffiliatedSitesControl>());
        Assert.Equal(Dock.Right, DockPanel.GetDock(rail));
        Assert.True(rail.IsRailMode);
        Assert.InRange(rail.ActualWidth, 116, 122);
        Assert.Contains(StaTest.Descendants(page).OfType<TextBlock>(), text =>
            text.Text.Contains("PRIVATE BROWSING", StringComparison.Ordinal));
        Assert.All(StaTest.Descendants(page).OfType<StellarHubControl>(), hub =>
            Assert.Equal(Visibility.Collapsed, hub.Visibility));
        Assert.Equal(Visibility.Collapsed,
            Assert.Single(StaTest.Descendants(page).OfType<NewTabDecorationControl>()).Visibility);
        Assert.NotNull(StaTest.FindByAutomationName<Button>(page, "Basic view · switch to Stellar"));
        Assert.All(StaTest.Descendants(rail).OfType<Button>().Where(button =>
                AutomationProperties.GetName(button) is "Hide Affiliated Sites" or "Show Affiliated Sites"),
            button => Assert.Equal(Visibility.Collapsed, button.Visibility));
    });

    [Fact]
    public void StellarModeUsesGlobalSwitchAndDoesNotPutHubsInsideOneOpaqueBlock() => StaTest.Run(() =>
    {
        var page = new NewTabPageControl { ReducedMotion = true };
        page.ApplyWorkspacePreferences(BrowserWorkspacePreferences.Default with
        {
            NewTabMode = NewTabVisualMode.Stellar,
        });
        StaTest.Prepare(page, 1280, 900);

        Assert.NotNull(StaTest.FindByAutomationName<Button>(page, "Stellar view · switch to Basic"));
        var workspace = StaTest.FindByAutomationName<Border>(page, "Quick launch workspace");
        var background = Assert.IsType<SolidColorBrush>(workspace.Background);
        Assert.Equal(0, background.Color.A);
        Assert.Equal(new Thickness(0), workspace.BorderThickness);
        Assert.All(StaTest.Descendants(page).OfType<StellarHubControl>(), hub =>
            Assert.Equal(Visibility.Visible, hub.Visibility));
    });

    [Fact]
    public void StellarOrbitItemFocusShowsReadableDetailWithoutMovingTheItem() => StaTest.Run(() =>
    {
        var hub = new StellarHubControl(StellarHubKind.Workspaces) { ReducedMotion = true };
        hub.SetItems([
            new StellarHubItem("route", "Morning route", "3 tabs")
            {
                Address = new Uri("https://start.example.test/"),
                Note = "News and planning",
            },
        ]);
        var window = new Window { Content = hub, Width = 480, Height = 340, ShowInTaskbar = false };
        window.Show();
        try
        {
            window.UpdateLayout();
            var item = StaTest.Descendants(hub).OfType<Button>()
                .Single(button => AutomationProperties.GetName(button).Contains("Morning route", StringComparison.Ordinal));
            var before = item.TransformToAncestor(hub).Transform(new Point());
            var visibleTitle = Assert.Single(
                StaTest.Descendants(item).OfType<TextBlock>(),
                text => text.Text == "Morning route");
            Assert.True(item.ActualWidth >= 96);
            Assert.True(item.ActualHeight >= 64);
            Assert.True(visibleTitle.ActualHeight > 0);
            item.Focus();
            window.UpdateLayout();
            var after = item.TransformToAncestor(hub).Transform(new Point());

            Assert.True(hub.IsDetailCardVisible);
            Assert.Equal(before, after);
            Assert.Contains("https://start.example.test/", AutomationProperties.GetHelpText(item));
            Assert.Contains("News and planning", AutomationProperties.GetHelpText(item));
        }
        finally
        {
            window.Close();
        }
    });

    [Theory]
    [InlineData(StellarHubKind.Bookmarks, "Bookmark details")]
    [InlineData(StellarHubKind.Workspaces, "Workspace details")]
    public void StellarPointerHoverShowsNonBlockingUiaDetailCard(
        StellarHubKind kind,
        string expectedStatus) => StaTest.Run(() =>
    {
        var hub = new StellarHubControl(kind) { ReducedMotion = true };
        hub.SetItems([
            new StellarHubItem("route", "Morning route", "3 tabs")
            {
                Address = new Uri("https://start.example.test/"),
                Note = "News and planning",
            },
        ]);
        var window = new Window { Content = hub, Width = 480, Height = 340, ShowInTaskbar = false };
        window.Show();
        try
        {
            window.UpdateLayout();
            var item = StaTest.Descendants(hub).OfType<Button>()
                .Single(button => AutomationProperties.GetName(button).Contains("Morning route", StringComparison.Ordinal));
            item.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            {
                RoutedEvent = Mouse.MouseEnterEvent,
                Source = item,
            });
            window.UpdateLayout();

            Assert.True(hub.IsDetailCardVisible);
            var detail = StaTest.Descendants(hub).OfType<Border>()
                .Single(border => AutomationProperties.GetName(border) == "Morning route details");
            var peer = UIElementAutomationPeer.CreatePeerForElement(detail);
            Assert.False(detail.IsHitTestVisible);
            Assert.False(detail.Focusable);
            Assert.Equal(Visibility.Visible, detail.Visibility);
            Assert.NotNull(peer);
            Assert.Equal("Morning route details", peer.GetName());
            Assert.Contains("https://start.example.test/", peer.GetHelpText());
            Assert.Contains("News and planning", peer.GetHelpText());
            Assert.Equal(expectedStatus, peer.GetItemStatus());
            Assert.True(peer.IsControlElement());
            Assert.Contains(StaTest.Descendants(detail).OfType<TextBlock>(), text => text.Text == "Morning route");
            Assert.Contains(StaTest.Descendants(detail).OfType<TextBlock>(), text => text.Text == "News and planning");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    [Trait("Category", "InteractiveDesktop")]
    public void StellarWorkspaceItemsRemainReadableAboveFoldAtCommonWindowSize() => StaTest.Run(() =>
    {
        var profile = new ProfileId(Guid.NewGuid());
        var preset = new WorkspacePresetPresentation(
            new WorkspacePresetPresentationId(profile, Guid.NewGuid()),
            "Morning route",
            "Morning route",
            [new WorkspacePresetTabPresentation(new Uri("https://start.example.test/"), "Start")])
        {
            Note = "News and planning",
        };
        var page = new NewTabPageControl { ReducedMotion = true };
        page.ApplyWorkspaceData(new NewTabWorkspaceData([], [preset], true));
        var window = new Window { Content = page, Width = 1200, Height = 800, ShowInTaskbar = false };
        window.Show();
        try
        {
            window.UpdateLayout();
            var item = StaTest.Descendants(page).OfType<Button>()
                .Single(button => AutomationProperties.GetName(button).Contains(
                    "Open Morning route from workspaces hub",
                    StringComparison.Ordinal));
            var bounds = item.TransformToAncestor(page).TransformBounds(
                new Rect(0, 0, item.ActualWidth, item.ActualHeight));

            Assert.True(bounds.Top >= 0);
            Assert.True(bounds.Bottom <= page.ActualHeight);
            Assert.Contains(StaTest.Descendants(item).OfType<TextBlock>(), text =>
                text.Text == "Morning route" && text.ActualHeight > 0);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void StellarDetailFocusFreezesOnlyNonStellarSceneMotion() => StaTest.Run(() =>
    {
        var profile = new ProfileId(Guid.NewGuid());
        var bookmark = new BookmarkEntry(
            new BookmarkId(profile, Guid.NewGuid()),
            new Uri("https://focus.example.test/"),
            "Focus site",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        var page = new NewTabPageControl { ReducedMotion = false };
        page.ApplyWorkspaceData(new NewTabWorkspaceData([bookmark], [], true));
        var window = new Window { Content = page, Width = 1280, Height = 900, ShowInTaskbar = false };
        window.Show();
        try
        {
            window.UpdateLayout();
            var item = StaTest.Descendants(page).OfType<Button>()
                .Single(button => AutomationProperties.GetName(button).Contains("Open Focus site from bookmarks hub", StringComparison.Ordinal));
            item.Focus();
            window.UpdateLayout();
            var scene = Assert.Single(StaTest.Descendants(page).OfType<NewTabDecorationControl>());
            Assert.True(scene.FreezeNonStellarMotion);
            Assert.All(StaTest.Descendants(page).OfType<StellarHubControl>(), hub =>
            {
                Assert.True(hub.FreezeNonStellarMotion);
                Assert.False(hub.StellarOrbit.IsNonStellarMotionActive);
            });
            Assert.Contains(StaTest.Descendants(page).OfType<OrbitEmberStar>(), star => star.IsActive);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void WorkspaceHubConsumesOnlyHostDecodedArtworkSourceByPresetId() => StaTest.Run(() =>
    {
        var profile = new ProfileId(Guid.NewGuid());
        var preset = new WorkspacePresetPresentation(
            new WorkspacePresetPresentationId(profile, Guid.NewGuid()),
            "Planning",
            "Planning",
            [new WorkspacePresetTabPresentation(new Uri("https://plan.example.test/"), "Plan")])
        {
            Artwork = new WorkspaceArtworkPresentation(
                WorkspaceArtworkKind.LocalImage, null, "asset:planning", "Planning artwork"),
        };
        var drawing = new DrawingImage(new GeometryDrawing(
            Brushes.Gold,
            new Pen(Brushes.White, 1),
            new RectangleGeometry(new Rect(0, 0, 16, 16))));
        drawing.Freeze();
        var page = new NewTabPageControl { ReducedMotion = true };
        page.ApplyWorkspaceData(new NewTabWorkspaceData([], [preset], true));
        page.ApplyWorkspaceArtworkSources(new Dictionary<WorkspacePresetPresentationId, ImageSource>
        {
            [preset.Id] = drawing,
        });
        StaTest.Prepare(page, 1280, 900);

        var hub = StaTest.Descendants(page).OfType<StellarHubControl>()
            .Single(value => value.Kind == StellarHubKind.Workspaces);
        Assert.Same(drawing, hub.StellarOrbit.OrbitingImageSource);
        Assert.Equal("Planning", hub.StellarOrbit.OrbitingImageLabel);
        Assert.Equal(OrbitEmberStarKind.TabGroup, hub.StellarOrbit.Kind);
    });

    [Fact]
    public void DragOntoUngroupedTabEmitsOneTypedTemporaryGroupIntent() => StaTest.Run(() =>
    {
        var windowId = new BrowserWindowId(Guid.NewGuid());
        var first = Tab(null, "One");
        var second = Tab(null, "Two");
        var sink = new RecordingSink();
        var session = new TabControllerPresentationSession(windowId, false, sink);
        session.AcceptProjection(new(
            windowId, false, 7, TabControllerHostState.Docked, TabStripPlacement.Top,
            new(windowId, first.TabId, [first, second])));
        var control = new TabControllerControl();
        control.Bind(session);
        StaTest.Prepare(control, 900, 120);

        Assert.True(control.HandleTabDropAsync(first.TabId, second.TabId).AsTask().GetAwaiter().GetResult());

        var create = Assert.IsType<CreateTabGroupControllerAction>(Assert.Single(sink.Actions));
        Assert.Equal([second.TabId, first.TabId], create.TabIds);
        Assert.Equal("New group", create.Name);
    });

    [Fact]
    public void GroupDialogsConfirmOnlyMultiTabCloseAndDescribeSavedWorkspaceBehavior() => StaTest.Run(() =>
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TabGroupCloseConfirmationDialog("Solo", 1));

        var confirmation = new TabGroupCloseConfirmationDialog("Research", 3);
        var confirmationRoot = Assert.IsAssignableFrom<FrameworkElement>(confirmation.Content);
        StaTest.Prepare(confirmationRoot, 460, 250);
        var close = StaTest.FindByAutomationName<Button>(
            confirmationRoot,
            "Close all 3 tabs in Research");
        Assert.True(close.ActualHeight >= 44);
        Assert.Contains(StaTest.Descendants(confirmationRoot).OfType<TextBlock>(), text =>
            text.Text.Contains("closes all 3 tabs", StringComparison.OrdinalIgnoreCase));

        var tabs = new[]
        {
            new WorkspacePresetTabPresentation(new Uri("https://first.example.test/"), "First site"),
            new WorkspacePresetTabPresentation(new Uri("https://second.example.test/"), "Second site"),
        };
        var save = new SaveTabGroupWorkspaceDialog(
            new BrowserTabGroupId(Guid.NewGuid()),
            "Research",
            "SeaGlass",
            tabs);
        var saveRoot = Assert.IsAssignableFrom<FrameworkElement>(save.Content);
        StaTest.Prepare(saveRoot, 620, 690);

        Assert.Contains(StaTest.Descendants(saveRoot).OfType<TextBlock>(), text =>
            text.Text.Contains("adds a new collapsed live group", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(StaTest.FindByAutomationName<TextBox>(saveRoot, "Workspace note"));
        Assert.Equal(2, StaTest.FindByAutomationName<ComboBox>(saveRoot, "First workspace site").Items.Count);
        Assert.Equal(3, StaTest.FindByAutomationName<ComboBox>(saveRoot, "Workspace artwork").Items.Count);
        var localArtwork = StaTest.FindByAutomationName<Button>(saveRoot, "Choose local workspace artwork");
        Assert.False(localArtwork.IsEnabled);
        Assert.Contains("secure host importer", AutomationProperties.GetHelpText(localArtwork));
    });

    [Fact]
    public void QuickViewLauncherIsVisibleNamedAndEnterEmitsRealOpenIntent() => StaTest.Run(() =>
    {
        var control = new QuickViewControl { ReducedMotion = true };
        var actions = new List<QuickViewAction>();
        control.ActionRequested += (_, args) => actions.Add(args.Action);
        control.Apply(new QuickViewPresentation(
            1,
            false,
            true,
            QuickViewHostState.Ready,
            "Quick View",
            null,
            QuickViewStateTransferCapability.AddressReloadOnly,
            "Ready",
            []));
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
            var anchor = control.AnchorButton;
            Assert.Equal(Visibility.Visible, anchor.Visibility);
            Assert.True(anchor.ActualWidth >= 154 && anchor.ActualHeight >= 48);
            Assert.True(control.OverlayPopup.IsOpen);
            Assert.NotNull(StaTest.FindByAutomationName<Border>(control.OverlayPopup, "Quick View lower-left launcher"));
            anchor.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.IsType<OpenQuickViewAction>(Assert.Single(actions));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void BookmarkEditorCarriesOptionalNoteWithoutChangingSingleSiteSemantics() => StaTest.Run(() =>
    {
        var profile = new ProfileId(Guid.NewGuid());
        var bookmark = new BookmarkEntry(
            new BookmarkId(profile, Guid.NewGuid()),
            new Uri("https://bookmark.example.test/"),
            "Bookmark",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        var dialog = new BookmarkManagerDialog(
            new PrivacyContext(profile, new BrowserSessionId(Guid.NewGuid()), BrowserProfileMode.Normal),
            [bookmark],
            canEditBookmarks: true,
            new Dictionary<BookmarkId, string> { [bookmark.Id] = "Original note" });
        BookmarkSaveDraft? saved = null;
        dialog.SaveRequested += (_, args) => saved = args.Draft;
        dialog.Show();
        try
        {
            dialog.UpdateLayout();
            StaTest.FindByAutomationName<Button>(dialog, "Edit selected bookmark")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var note = StaTest.FindByAutomationName<TextBox>(dialog, "Bookmark note");
            Assert.Equal("Original note", note.Text);
            note.Text = "Updated note";
            StaTest.FindByAutomationName<Button>(dialog, "Save bookmark")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.NotNull(saved);
            Assert.Equal(bookmark.Id, saved!.ExistingId);
            Assert.Equal(bookmark.Target, saved.Target);
            Assert.Equal("Updated note", saved.Note);
        }
        finally
        {
            dialog.Close();
        }
    });

    [Fact]
    public void BrowserChromeMirrorsLocalArtworkImporterToDockedAndDetachedControllers() => StaTest.Run(() =>
    {
        var windowId = new BrowserWindowId(Guid.NewGuid());
        var tab = Tab(null, "One");
        var session = new TabControllerPresentationSession(windowId, false, new RecordingSink());
        session.AcceptProjection(new(
            windowId,
            false,
            1,
            TabControllerHostState.Docked,
            TabStripPlacement.Top,
            new(windowId, tab.TabId, [tab])));
        Func<WorkspaceLocalArtworkImportRequest, WorkspaceArtworkPresentation?> first = _ =>
            new WorkspaceArtworkPresentation(
                WorkspaceArtworkKind.LocalImage, null, "asset:first", "First local artwork");
        Func<WorkspaceLocalArtworkImportRequest, WorkspaceArtworkPresentation?> second = _ =>
            new WorkspaceArtworkPresentation(
                WorkspaceArtworkKind.LocalImage, null, "asset:second", "Second local artwork");
        var chrome = new BrowserChromeControl { WorkspaceLocalArtworkImporter = first };
        chrome.BindTabControllerSession(session);
        StaTest.Prepare(chrome, 1000, 700);

        var docked = StaTest.Descendants(chrome).OfType<TabControllerControl>()
            .Single(control => control.SurfaceKind == TabControllerSurfaceKind.Docked);
        var detached = chrome.CreateDetachedTabControllerControl();
        Assert.Same(first, docked.LocalArtworkImporter);
        Assert.Same(first, detached.LocalArtworkImporter);

        chrome.WorkspaceLocalArtworkImporter = second;
        Assert.Same(second, docked.LocalArtworkImporter);
        Assert.Same(second, detached.LocalArtworkImporter);
        detached.Unbind();
    });

    private static BrowserTabEntry Tab(BrowserTabGroupId? groupId, string title) => new(
        new BrowserTabId(Guid.NewGuid()),
        groupId,
        title,
        new Uri($"https://{title.ToLowerInvariant()}.example.test/"),
        BrowserLoadState.Idle,
        false,
        false,
        false,
        false);

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
                "Accepted."));
        }
    }
}
