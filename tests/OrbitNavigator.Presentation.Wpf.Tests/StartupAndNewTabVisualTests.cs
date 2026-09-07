using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Presentation.Navigation;
using OrbitNavigator.Presentation.Shell;
using OrbitNavigator.Presentation.Workspace;
using OrbitNavigator.Presentation.Wpf;
using Xunit;

namespace OrbitNavigator.Presentation.Wpf.Tests;

public sealed class StartupAndNewTabVisualTests
{
    [Fact]
    public void StartupOverlayLoadsCoordinatorSequenceInFrozenOrderAndExposesStateCopy()
    {
        StaTest.Run(() =>
        {
            var overlay = new StartupLoadingOverlay { ReducedMotion = true };
            overlay.SetBackgroundAsset(FindCoordinatorAsset());
            Assert.True(overlay.SetSequenceAssetDirectory(FindCoordinatorSequenceDirectory()));
            overlay.SetState(StartupLoadingState.Create(
                StartupLoadingPhase.StartingBrowserEngine,
                "Starting secure web engine…",
                0.55));
            StaTest.Prepare(overlay);

            Assert.Equal(StartupLoadingPhase.StartingBrowserEngine, overlay.State.Phase);
            Assert.Equal(6, overlay.LoadedSequenceFrameCount);
            Assert.Equal(5, overlay.CurrentSequenceFrameIndex);
            Assert.Equal(30, StartupLoadingOverlay.TimelineFramesPerSecond);
            Assert.Equal(TimeSpan.FromSeconds(4), StartupLoadingOverlay.TimelineDuration);
            Assert.Equal(
                [
                    "frame-00-dormant.png",
                    "frame-01-awaken.png",
                    "frame-02-sweep.png",
                    "frame-03-converge.png",
                    "frame-04-apex.png",
                    "frame-05-settle.png",
                ],
                StartupLoadingOverlay.CoordinatorSequenceFileNames);
            Assert.Contains(
                StaTest.Descendants(overlay).OfType<TextBlock>(),
                text => text.Text == "Starting secure web engine…");
            Assert.Contains(
                StaTest.Descendants(overlay).OfType<System.Windows.Controls.Image>(),
                image => image.Source is not null);
            Assert.DoesNotContain(
                StaTest.Descendants(overlay),
                element => element is OrbitLoadingIndicator);
        });
    }

    [Fact]
    public void InitialHostReadyClearsImmediatelyForReducedMotion()
    {
        StaTest.Run(() =>
        {
            var overlay = new StartupLoadingOverlay { ReducedMotion = true };
            var cleared = 0;
            overlay.Cleared += (_, _) => cleared++;

            overlay.Complete();

            Assert.Equal(Visibility.Collapsed, overlay.Visibility);
            Assert.Equal(1, cleared);
            Assert.Equal(StartupLoadingPhase.Ready, overlay.State.Phase);
        });
    }

    [Fact]
    public void FailureStateKeepsEssentialCopyAndRetryActionVisible()
    {
        StaTest.Run(() =>
        {
            var overlay = new StartupLoadingOverlay { ReducedMotion = true };
            var retries = 0;
            overlay.RetryRequested += (_, _) => retries++;
            overlay.ShowFailure("The web engine could not start.");
            StaTest.Prepare(overlay);

            Assert.Contains(
                StaTest.Descendants(overlay).OfType<TextBlock>(),
                text => text.Text == "The web engine could not start.");
            var retry = StaTest.FindByAutomationName<Button>(overlay, "Try startup again");
            Assert.Equal(Visibility.Visible, retry.Visibility);
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, retries);
        });
    }

    [Fact]
    public void NewTabUsesDuckDuckGoAndKeepsProjectLinksSecondary()
    {
        StaTest.Run(() =>
        {
            var page = new NewTabPageControl
            {
                ShowDonationLink = true,
                ShowPortfolioLink = true,
                ReducedMotion = true,
            };
            OmniboxTarget? target = null;
            var donationRequests = 0;
            var portfolioRequests = 0;
            page.NavigationRequested += (_, args) => target = args.Target;
            page.DonationRequested += (_, _) => donationRequests++;
            page.PortfolioRequested += (_, _) => portfolioRequests++;
            StaTest.Prepare(page);
            var search = StaTest.FindByAutomationName<TextBox>(page, "Search or enter address");
            search.Text = "quiet web browser";
            StaTest.FindByAutomationName<Button>(page, "Search with DuckDuckGo")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal("duckduckgo.com", target?.Uri.Host);
            Assert.Equal(OmniboxTargetKind.Search, target?.Kind);
            Assert.Equal(
                Visibility.Visible,
                StaTest.FindByAutomationName<Button>(page, "Donate to Orbit Navigator on Ko-fi").Visibility);
            Assert.Equal(
                Visibility.Visible,
                StaTest.FindByAutomationName<Button>(page, "Open Paradox portfolio").Visibility);
            StaTest.FindByAutomationName<Button>(page, "Donate to Orbit Navigator on Ko-fi")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            StaTest.FindByAutomationName<Button>(page, "Open Paradox portfolio")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, donationRequests);
            Assert.Equal(1, portfolioRequests);
        });
    }

    [Fact]
    public void NewTabShowsOnlyApprovedAffiliatesAndForwardsRevisionedHostIntents()
    {
        StaTest.Run(() =>
        {
            var page = new NewTabPageControl { ReducedMotion = true };
            AffiliatedSiteLaunchRequestedEventArgs? launch = null;
            AffiliatedSitesVisibilityChangeRequestedEventArgs? visibility = null;
            page.AffiliatedSiteLaunchRequested += (_, args) => launch = args;
            page.AffiliatedSitesVisibilityChangeRequested += (_, args) => visibility = args;
            page.ApplyAffiliatedSites(
                AffiliatedSitesCatalogPresentation.ApprovedV1,
                new AffiliatedSitesVisibilityPresentation(false, 7, true, null));
            StaTest.Prepare(page);

            var affiliate = Assert.Single(StaTest.Descendants(page).OfType<AffiliatedSitesControl>());
            Assert.Equal(2, affiliate.VisibleSiteCount);
            Assert.Equal(1, affiliate.VisiblePreviewCount);
            Assert.Equal(AffiliatedSitesCatalogPresentation.ApprovedV1, page.AffiliatedSitesCatalog);
            Assert.DoesNotContain(
                affiliate.Catalog.Sites,
                site => site.Title.Contains("National Chat", StringComparison.OrdinalIgnoreCase));
            var preview = StaTest.FindByAutomationName<Border>(
                page,
                "Wedding Dreamer — Private preview / coming later.");
            Assert.False(preview.Focusable);
            Assert.False(preview.IsHitTestVisible);
            Assert.DoesNotContain(StaTest.Descendants(preview), element => element is Button);

            StaTest.FindByAutomationName<Button>(page, "Open My Orbit — external site")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.NotNull(launch);
            Assert.Equal("https://my-orbit.snap-it.cc/", launch.Site.Target.AbsoluteUri);
            Assert.Equal("owner-approved-v1", launch.CatalogId);
            Assert.Equal(1, launch.ExpectedCatalogRevision);

            StaTest.FindByAutomationName<Button>(page, "Hide Affiliated Sites")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.NotNull(visibility);
            Assert.True(visibility.IsHidden);
            Assert.Equal(7, visibility.ExpectedRevision);
            Assert.False(page.AffiliatedSitesVisibility.IsHidden);
        });
    }

    [Fact]
    public void NewTabRendersRealBookmarkAndWorkspaceDataAndEmitsTypedIntents()
    {
        StaTest.Run(() =>
        {
            var profileId = new ProfileId(Guid.NewGuid());
            var bookmark = new BookmarkEntry(
                new BookmarkId(profileId, Guid.NewGuid()),
                new Uri("https://docs.example.test"),
                "Project docs",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);
            var preset = new WorkspacePresetPresentation(
                new WorkspacePresetPresentationId(profileId, Guid.NewGuid()),
                "Daily orbit",
                "Daily",
                [
                    new(new Uri("https://mail.example.test"), "Mail"),
                    new(new Uri("https://calendar.example.test"), "Calendar"),
                ]);
            var page = new NewTabPageControl { ReducedMotion = true };
            BookmarkEntry? launchedBookmark = null;
            WorkspacePresetPresentation? openedWorkspace = null;
            WorkspacePresetPresentation? configuredWorkspace = null;
            WorkspacePresetPresentationId? removedWorkspace = null;
            page.BookmarkLaunchRequested += (_, args) => launchedBookmark = args.Bookmark;
            page.OpenWorkspaceRequested += (_, args) => openedWorkspace = args.Workspace;
            page.ConfigureWorkspaceRequested += (_, args) => configuredWorkspace = args.Workspace;
            page.RemoveWorkspaceRequested += (_, args) => removedWorkspace = args.WorkspaceId;
            page.SetWorkspaceData([bookmark], [preset]);
            StaTest.Prepare(page);

            StaTest.FindByAutomationName<Button>(page, "Open bookmark Project docs")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            StaTest.FindByAutomationName<Button>(page, "Open workspace Daily orbit, 2 tabs")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var actions = StaTest.FindByAutomationName<Button>(page, "Workspace actions for Daily orbit");
            actions.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var menu = Assert.IsType<ContextMenu>(actions.ContextMenu);
            menu.Items.OfType<MenuItem>().Single(item => AutomationProperties.GetName(item) == "Edit workspace Daily orbit")
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.Items.OfType<MenuItem>().Single(item => AutomationProperties.GetName(item) == "Remove workspace Daily orbit")
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

            Assert.Same(bookmark, launchedBookmark);
            Assert.Same(preset, openedWorkspace);
            Assert.Same(preset, configuredWorkspace);
            Assert.Equal(preset.Id, removedWorkspace);
        });
    }

    [Fact]
    public void NewTabEmptyStateIsHonestAndPrivateMutationControlsAreDisabled()
    {
        StaTest.Run(() =>
        {
            var page = new NewTabPageControl
            {
                ReducedMotion = true,
                CanModifyWorkspace = false,
            };
            page.SetWorkspaceData([], []);
            StaTest.Prepare(page);

            Assert.Contains(
                StaTest.Descendants(page).OfType<TextBlock>(),
                text => text.Text == "Make this space yours");
            Assert.Equal(
                Visibility.Visible,
                StaTest.FindByAutomationName<Border>(page, "Bookmark star invitation").Visibility);
            Assert.Equal(
                Visibility.Visible,
                StaTest.FindByAutomationName<Border>(page, "Workspace star invitation").Visibility);
            Assert.False(StaTest.FindByAutomationName<Button>(page, "Create workspace").IsEnabled);
            Assert.False(StaTest.FindByAutomationName<Button>(page, "Create a workspace").IsEnabled);
        });
    }

    [Fact]
    public void EmptyNewTabUsesReadableDarkSurfaceHeadings()
    {
        StaTest.Run(() =>
        {
            var page = new NewTabPageControl { ReducedMotion = true };
            page.SetWorkspaceData([], []);
            StaTest.Prepare(page);

            foreach (var heading in new[] { "Bookmarks", "Saved workspaces", "Make this space yours" })
            {
                var text = Assert.Single(
                    StaTest.Descendants(page).OfType<TextBlock>(),
                    candidate => candidate.Text == heading);
                Assert.Same(OrbitVisualTheme.Ink, text.Foreground);
            }
        });
    }

    [Fact]
    public void NewTabDecorationUsesExistingAssetWithStaticReducedMotionFallback()
    {
        StaTest.Run(() =>
        {
            var decoration = new NewTabDecorationControl { ReducedMotion = true };
            decoration.SetCoordinatorAsset(FindNewTabAsset(), reduceVisualNoise: false);
            StaTest.Prepare(decoration);

            Assert.Equal(Visibility.Visible, decoration.Visibility);
            Assert.True(decoration.HasCoordinatorAsset);
            Assert.True(decoration.HasV3Asset);
            Assert.Equal(24, NewTabDecorationControl.SharedAnimationFramesPerSecond);
            Assert.False(decoration.IsRegisteredWithSharedClock);
            Assert.Contains(
                StaTest.Descendants(decoration).OfType<System.Windows.Controls.Image>(),
                image => image.Source is not null);
        });
    }

    [Fact]
    public void MissingBackgroundUsesFallbackWhileHubIgnoresLegacyPathAndUsesPackagedV3()
    {
        StaTest.Run(() =>
        {
            var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
            var decoration = new NewTabDecorationControl { ReducedMotion = true };
            decoration.SetCoordinatorAsset(missing, reduceVisualNoise: false);
            var hub = new StellarHubControl(StellarHubKind.Bookmarks) { ReducedMotion = true };
            hub.SetCenterAsset(missing);
            hub.SetItems([]);
            StaTest.Prepare(decoration);
            StaTest.Prepare(hub, 850, 330);

            Assert.Equal(Visibility.Visible, decoration.Visibility);
            Assert.False(decoration.HasCoordinatorAsset);
            Assert.False(decoration.HasV3Asset);
            Assert.Equal(Visibility.Visible, hub.Visibility);
            Assert.True(hub.HasCenterAsset);
            Assert.True(hub.UsesV3CenterArtwork);
            Assert.False(hub.CenterArtworkUsesExtractedAlpha);
            Assert.Equal(
                Visibility.Visible,
                StaTest.FindByAutomationName<Border>(hub, "Bookmark star invitation").Visibility);
        });
    }

    [Theory]
    [InlineData(StellarHubKind.Bookmarks, "bookmark-star-v1.png", OrbitEmberStarKind.Favorite, "Bookmark star", "Add a bookmark to chart your first destination.")]
    [InlineData(StellarHubKind.Workspaces, "workspace-star-v1.png", OrbitEmberStarKind.TabGroup, "Workspace star", "Save a group of tabs to launch a familiar route.")]
    public void LegacyHubInputUsesWholeFrameTransparentV3StarWithoutMatteCompositor(
        StellarHubKind kind,
        string assetName,
        OrbitEmberStarKind starKind,
        string titleCopy,
        string detailCopy)
    {
        StaTest.Run(() =>
        {
            var hub = new StellarHubControl(kind) { ReducedMotion = true };
            hub.SetCenterAsset(FindNewTabAsset(assetName));
            hub.SetItems([]);
            StaTest.Prepare(hub, 850, 330);

            Assert.True(hub.HasCenterAsset);
            Assert.True(hub.UsesV3CenterArtwork);
            Assert.False(hub.CenterArtworkUsesExtractedAlpha);
            Assert.False(hub.IsCenterFallbackVisible);
            Assert.Equal(starKind, hub.CenterStar.Kind);
            Assert.True(hub.CenterStar.IsActive);
            Assert.True(hub.CenterStar.ReducedMotion);
            Assert.EndsWith("-v3.png", hub.CenterStar.DefaultAtlasRelativePath, StringComparison.Ordinal);
            Assert.Equal(0, hub.CenterStar.CurrentEmberOffset);

            var title = Assert.Single(
                StaTest.Descendants(hub).OfType<TextBlock>(),
                text => text.Text == titleCopy);
            var detail = Assert.Single(
                StaTest.Descendants(hub).OfType<TextBlock>(),
                text => text.Text == detailCopy);
            Assert.Equal(OrbitVisualTheme.Ink, title.Foreground);
            Assert.Equal(OrbitVisualTheme.MutedInk, detail.Foreground);
        });
    }

    [Fact]
    public void NewTabUsesOneSharedVisibleOnlyClockAndOwnsNoDispatcherTimer()
    {
        StaTest.Run(() =>
        {
            Assert.DoesNotContain(
                typeof(NewTabPageControl).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
                field => field.FieldType == typeof(System.Windows.Threading.DispatcherTimer));
            Assert.DoesNotContain(
                typeof(StellarHubControl).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
                field => field.FieldType == typeof(System.Windows.Threading.DispatcherTimer));

            var page = new NewTabPageControl { ReducedMotion = false };
            page.SetCoordinatorDecoration(FindNewTabAsset(), reduceVisualNoise: false);
            var window = new Window { Content = page, Width = 1280, Height = 900, ShowInTaskbar = false };
            window.Show();
            try
            {
                window.UpdateLayout();
                Assert.True(page.IsAnimationRunning);
                Assert.Equal(1, page.AnimationLifecycleStartCount);
                Assert.Equal(24, NewTabPageControl.AnimationFramesPerSecond);
                Assert.Equal(192, NewTabPageControl.AnimationLoopFrameCount);
                Assert.True(NewTabDecorationControl.SharedClockSubscriberCount >= 3);
                Assert.Equal(1, NewTabDecorationControl.SharedClockRenderingHandlerCount);

                page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                Assert.Equal(1, NewTabDecorationControl.SharedClockRenderingHandlerCount);

                page.ReducedMotion = true;
                Assert.False(page.IsAnimationRunning);
                Assert.Equal(0, NewTabDecorationControl.SharedClockRenderingHandlerCount);
            }
            finally
            {
                window.Close();
            }
            Assert.False(page.IsAnimationRunning);
        });
    }

    [Fact]
    public void NewTabAnimationPolicySuppressesMotionForEveryAccessibilityFallback()
    {
        Assert.True(NewTabPageControl.ShouldAnimateVisualField(false, false, false));
        Assert.False(NewTabPageControl.ShouldAnimateVisualField(true, false, false));
        Assert.False(NewTabPageControl.ShouldAnimateVisualField(false, true, false));
        Assert.False(NewTabPageControl.ShouldAnimateVisualField(false, false, true));
    }

    [Fact]
    public void HubInteractiveTargetsStayStationaryWhileV5OwnsDecorativeRingMotion()
    {
        StaTest.Run(() =>
        {
            var hub = new StellarHubControl(StellarHubKind.Bookmarks) { ReducedMotion = false };
            hub.SetItems(
            [
                new("one", "First destination", "first.test"),
                new("two", "Second destination", "second.test"),
            ]);
            StaTest.Prepare(hub, 850, 330);
            var buttons = StaTest.Descendants(hub).OfType<Button>().ToArray();
            var before = buttons.Select(button => button.TranslatePoint(new Point(), hub)).ToArray();

            hub.RenderTimelineFrame(37, 192);
            hub.RenderTimelineFrame(121, 192);
            hub.UpdateLayout();

            Assert.Equal(before, buttons.Select(button => button.TranslatePoint(new Point(), hub)).ToArray());
            Assert.All(buttons, button => Assert.True(
                button.RenderTransform is null || button.RenderTransform.Value.IsIdentity));
            Assert.NotNull(hub.StellarOrbit);
            Assert.Contains(
                StaTest.Descendants(hub.StellarOrbit).OfType<FrameworkElement>(),
                element => element.RenderTransform is System.Windows.Media.RotateTransform);
        });
    }

    [Fact]
    public void InstalledCallOrderKeepsBothAssetBackedEmptyHubsVisibleAfterLateDataRefresh()
    {
        StaTest.Run(() =>
        {
            var page = new NewTabPageControl { ReducedMotion = false };
            page.SetCoordinatorDecoration(FindNewTabAsset("orbit-navigation-field-v2.png"), reduceVisualNoise: false);
            page.SetStellarHubAssets(
                FindNewTabAsset("bookmark-star-v1.png"),
                FindNewTabAsset("workspace-star-v1.png"));
            page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            page.SetWorkspaceData([], []);
            StaTest.Prepare(page);

            Assert.True(Assert.Single(StaTest.Descendants(page).OfType<NewTabDecorationControl>()).HasCoordinatorAsset);
            Assert.True(Assert.Single(StaTest.Descendants(page).OfType<NewTabDecorationControl>()).HasV3Asset);
            var hubs = StaTest.Descendants(page).OfType<StellarHubControl>().ToArray();
            Assert.Equal(2, hubs.Length);
            Assert.All(hubs, hub =>
            {
                Assert.Equal(Visibility.Visible, hub.Visibility);
                Assert.True(hub.HasCenterAsset);
                Assert.True(hub.UsesV3CenterArtwork);
                Assert.False(hub.CenterArtworkUsesExtractedAlpha);
                Assert.False(hub.IsCenterFallbackVisible);
            });
            var origins = hubs.Select(hub => hub.TranslatePoint(new Point(0, 0), page)).ToArray();
            Assert.InRange(Math.Abs(origins[0].Y - origins[1].Y), 0, 2);
            Assert.True(Math.Abs(origins[0].X - origins[1].X) > 400);
            Assert.NotNull(StaTest.FindByAutomationName<Border>(page, "Bookmark star invitation"));
            Assert.NotNull(StaTest.FindByAutomationName<Border>(page, "Workspace star invitation"));
            page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        });
    }

    [Fact]
    public void NewTabStellarHubsRemainAlternateToReadableQuickLaunchControls()
    {
        StaTest.Run(() =>
        {
            var profileId = new ProfileId(Guid.NewGuid());
            var bookmark = new BookmarkEntry(
                new BookmarkId(profileId, Guid.NewGuid()),
                new Uri("https://reference.example.test"),
                "Reference",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);
            var preset = new WorkspacePresetPresentation(
                new WorkspacePresetPresentationId(profileId, Guid.NewGuid()),
                "Research orbit",
                null,
                [new(new Uri("https://research.example.test"), "Research")]);
            var page = new NewTabPageControl { ReducedMotion = true };
            BookmarkEntry? launched = null;
            page.BookmarkLaunchRequested += (_, args) => launched = args.Bookmark;
            page.SetWorkspaceData([bookmark], [preset]);
            page.SetStellarHubAssets(FindNewTabAsset("bookmark-star-v1.png"), FindNewTabAsset("workspace-star-v1.png"));
            StaTest.Prepare(page);

            var logo = Assert.Single(StaTest.Descendants(page).OfType<OrbitNewTabLogo>());
            Assert.True(logo.UsesTransparentBackground);
            Assert.Equal("New Tab page", OrbitNewTabLogo.IntendedSurface);
            var bookmarkHub = StaTest.Descendants(page).OfType<StellarHubControl>()
                .Single(hub => hub.Kind == StellarHubKind.Bookmarks);
            var favorite = bookmarkHub.CenterStar;
            Assert.True(favorite.IsActive);
            Assert.True(favorite.ReducedMotion);
            StaTest.FindByAutomationName<Button>(page, "Open Reference from bookmarks hub")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Same(bookmark, launched);

            StaTest.FindByAutomationName<Button>(page, "Show list view")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(
                Visibility.Visible,
                StaTest.FindByAutomationName<Button>(page, "Open bookmark Reference").Visibility);
        });
    }

    [Fact]
    public void NewTabLogoDoesNotLeakIntoBrowserChromeIdentity()
    {
        StaTest.Run(() =>
        {
            var chrome = new BrowserChromeControl();
            StaTest.Prepare(chrome);

            Assert.Empty(StaTest.Descendants(chrome).OfType<OrbitNewTabLogo>());
        });
    }

    [Fact]
    public void ReducedVisualNoiseSuppressesDecorationAndForcesStandardLists()
    {
        StaTest.Run(() =>
        {
            var profileId = new ProfileId(Guid.NewGuid());
            var bookmark = new BookmarkEntry(
                new BookmarkId(profileId, Guid.NewGuid()),
                new Uri("https://quiet.example.test"),
                "Quiet reference",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);
            var page = new NewTabPageControl { ReducedMotion = true };
            page.SetWorkspaceData([bookmark], []);
            page.SetCoordinatorDecoration(FindNewTabAsset("orbit-navigation-field-v2.png"), reduceVisualNoise: true);
            StaTest.Prepare(page);

            Assert.All(
                StaTest.Descendants(page).OfType<NewTabDecorationControl>(),
                decoration => Assert.Equal(Visibility.Collapsed, decoration.Visibility));
            Assert.All(
                StaTest.Descendants(page).OfType<StellarHubControl>(),
                hub => Assert.Equal(Visibility.Collapsed, hub.Visibility));
            Assert.Equal(
                Visibility.Visible,
                StaTest.FindByAutomationName<Button>(page, "Open bookmark Quiet reference").Visibility);
        });
    }

    private static string FindCoordinatorAsset()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "assets",
                "loading",
                "orbit-launch-field-v1.png");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new Xunit.Sdk.XunitException("Coordinator startup artwork was not found.");
    }

    private static string FindCoordinatorSequenceDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "assets",
                "loading",
                "sequence-v1");
            if (StartupLoadingOverlay.CoordinatorSequenceFileNames.All(
                fileName => File.Exists(Path.Combine(candidate, fileName))))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new Xunit.Sdk.XunitException("Coordinator startup sequence was not found.");
    }

    private static string FindNewTabAsset()
        => FindNewTabAsset("orbit-navigation-field-v2.png");

    private static string FindNewTabAsset(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "assets",
                "new-tab",
                fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new Xunit.Sdk.XunitException("Coordinator New Tab artwork was not found.");
    }
}
