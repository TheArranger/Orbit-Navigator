using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using OrbitNavigator.Presentation.Workspace;
using OrbitNavigator.Presentation.Wpf;
using Xunit;

namespace OrbitNavigator.Presentation.Wpf.Tests;

public sealed class AffiliatedSitesVisualTests
{
    [Fact]
    public void ApprovedCatalogShowsOnlyExplicitSitesWithExternalSafetyCopyAndTypedLaunch()
    {
        StaTest.Run(() =>
        {
            var control = new AffiliatedSitesControl();
            AffiliatedSiteLaunchRequestedEventArgs? launched = null;
            control.LaunchRequested += (_, args) => launched = args;
            control.Apply(
                AffiliatedSitesCatalogPresentation.ApprovedV1,
                new AffiliatedSitesVisibilityPresentation(false, 4, true, null));
            StaTest.Prepare(control, 780, 280);

            Assert.Equal(2, control.VisibleSiteCount);
            Assert.Equal(1, control.VisiblePreviewCount);
            Assert.Equal(
                ["Beacon Spire", "My Orbit"],
                control.Catalog.Sites.Select(site => site.Title).ToArray());
            Assert.Equal(
                ["beacon-spire.dps-games.cc", "my-orbit.snap-it.cc"],
                control.Catalog.Sites.Select(site => site.TrustedDomain).ToArray());
            Assert.DoesNotContain(
                StaTest.Descendants(control).OfType<TextBlock>(),
                text => text.Text.Contains("National Chat", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(
                StaTest.Descendants(control).OfType<TextBlock>(),
                text => text.Text.Contains("First Step", StringComparison.OrdinalIgnoreCase) ||
                        text.Text.Contains("MetaFree", StringComparison.OrdinalIgnoreCase) ||
                        text.Text.Contains("Workflows", StringComparison.OrdinalIgnoreCase) ||
                        text.Text.Contains("Infinity", StringComparison.OrdinalIgnoreCase));

            var beacon = StaTest.FindByAutomationName<Button>(control, "Open Beacon Spire — external site");
            Assert.Contains("external HTTPS site", AutomationProperties.GetHelpText(beacon));
            Assert.Contains("beacon-spire.dps-games.cc", AutomationProperties.GetItemStatus(beacon));
            Assert.Contains("External HTTPS site", Assert.IsType<string>(beacon.ToolTip));
            Assert.True(beacon.MinHeight >= 44);
            Assert.True(beacon.MinWidth >= 44);

            var myOrbit = StaTest.FindByAutomationName<Button>(control, "Open My Orbit — external site");
            Assert.Contains(
                StaTest.Descendants(myOrbit).OfType<TextBlock>(),
                text => text.Text == "Account-based community and service site; sign-in is required for account features.");

            var preview = StaTest.FindByAutomationName<Border>(
                control,
                "Wedding Dreamer — Private preview / coming later.");
            Assert.False(preview.Focusable);
            Assert.False(preview.IsHitTestVisible);
            Assert.Equal("Private preview / coming later.", AutomationProperties.GetItemStatus(preview));
            Assert.Contains("no destination", AutomationProperties.GetHelpText(preview), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(StaTest.Descendants(preview), element => element is Button);
            Assert.DoesNotContain(
                StaTest.Descendants(preview).OfType<TextBlock>(),
                text => text.Text.Contains("://", StringComparison.Ordinal));

            beacon.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.NotNull(launched);
            Assert.Equal(new Uri("https://beacon-spire.dps-games.cc/"), launched!.Site.Target);
            Assert.Equal("owner-approved-v1", launched.CatalogId);
            Assert.Equal(1, launched.ExpectedCatalogRevision);
        });
    }

    [Fact]
    public void CompactRailVisiblyAndAccessiblyNamesWeddingDreamerWithoutMakingItInteractive()
    {
        StaTest.Run(() =>
        {
            var control = new AffiliatedSitesControl { IsRailMode = true };
            var launchCount = 0;
            control.LaunchRequested += (_, _) => launchCount++;
            control.Apply(
                AffiliatedSitesCatalogPresentation.ApprovedV1,
                new AffiliatedSitesVisibilityPresentation(false, 4, true, null));
            var window = new Window
            {
                Content = control,
                Width = 120,
                Height = 520,
                ShowInTaskbar = false,
            };
            window.Show();
            try
            {
                window.UpdateLayout();
                var preview = StaTest.FindByAutomationName<Border>(
                    control,
                    "Wedding Dreamer — Private preview / coming later.");
                var visibleCopy = StaTest.Descendants(preview).OfType<TextBlock>().ToArray();
                var title = Assert.Single(visibleCopy, text => text.Text == "Wedding Dreamer");
                var status = Assert.Single(visibleCopy, text => text.Text == "Private preview / coming later.");

                Assert.Equal("Private preview / coming later.", AutomationProperties.GetItemStatus(preview));
                Assert.Equal("Wedding Dreamer", AutomationProperties.GetName(title));
                Assert.Equal("Private preview / coming later.", AutomationProperties.GetName(status));
                Assert.DoesNotContain(visibleCopy, text => text.Text == "Preview");
                Assert.True(title.ActualHeight > 0);
                Assert.True(status.ActualHeight > 0);
                var statusBottom = status.TranslatePoint(new Point(0, status.ActualHeight), preview).Y;
                Assert.InRange(statusBottom, 0, preview.ActualHeight);
                Assert.False(preview.Focusable);
                Assert.False(preview.IsHitTestVisible);
                Assert.DoesNotContain(StaTest.Descendants(preview), element => element is Button);
                Assert.Equal(0, launchCount);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void CompactRailWrapsApprovedSiteNamesWithoutTruncatingThem()
    {
        StaTest.Run(() =>
        {
            var control = new AffiliatedSitesControl { IsRailMode = true };
            control.Apply(
                AffiliatedSitesCatalogPresentation.ApprovedV1,
                new AffiliatedSitesVisibilityPresentation(false, 4, true, null));
            var window = new Window
            {
                Content = control,
                Width = AffiliatedSitesControl.PreferredRailWidth,
                Height = 520,
                ShowInTaskbar = false,
            };
            window.Show();
            try
            {
                window.UpdateLayout();
                foreach (var expected in new[] { "Beacon Spire", "My Orbit" })
                {
                    var button = StaTest.Descendants(control).OfType<Button>()
                        .Single(value => AutomationProperties.GetName(value) == $"Open {expected} — external site");
                    var title = Assert.Single(
                        StaTest.Descendants(button).OfType<TextBlock>(),
                        value => value.Text == expected);
                    Assert.True(button.ActualWidth >= AffiliatedSitesControl.PreferredRailItemWidth);
                    Assert.Equal(TextWrapping.Wrap, title.TextWrapping);
                    Assert.Equal(TextTrimming.None, title.TextTrimming);
                    Assert.True(title.ActualHeight > 0);
                    var titleBottom = title.TranslatePoint(new Point(0, title.ActualHeight), button).Y;
                    Assert.InRange(titleBottom, 0, button.ActualHeight);
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void EmptyCatalogInventsNoDestinationAndShowsReviewableOwnerPath()
    {
        StaTest.Run(() =>
        {
            var control = new AffiliatedSitesControl();
            control.Apply(
                AffiliatedSitesCatalogPresentation.Empty,
                new AffiliatedSitesVisibilityPresentation(false, 0, true, null));
            StaTest.Prepare(control, 760, 220);

            Assert.Equal(0, control.VisibleSiteCount);
            Assert.Equal(0, control.VisiblePreviewCount);
            Assert.DoesNotContain(
                StaTest.Descendants(control).OfType<Button>(),
                button => AutomationProperties.GetName(button).StartsWith("Open ", StringComparison.Ordinal));
            Assert.NotNull(StaTest.FindByAutomationName<Border>(control, "Affiliated Sites empty"));
            Assert.Contains(
                StaTest.Descendants(control).OfType<TextBlock>(),
                text => text.Text.Contains(AffiliatedSitesCatalogPresentation.OwnerCatalogRelativePath, StringComparison.Ordinal));
            Assert.Contains("does not use browsing history", AutomationProperties.GetHelpText(control));
        });
    }

    [Fact]
    public void HideAndShowRequestsCarryExpectedRevisionWithoutOptimisticMutation()
    {
        StaTest.Run(() =>
        {
            var control = new AffiliatedSitesControl();
            AffiliatedSitesVisibilityChangeRequestedEventArgs? requested = null;
            control.VisibilityChangeRequested += (_, args) => requested = args;
            control.Apply(
                AffiliatedSitesCatalogPresentation.ApprovedV1,
                new AffiliatedSitesVisibilityPresentation(false, 9, true, null));
            StaTest.Prepare(control, 760, 280);

            StaTest.FindByAutomationName<Button>(control, "Hide Affiliated Sites")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.NotNull(requested);
            Assert.True(requested!.IsHidden);
            Assert.Equal(9, requested.ExpectedRevision);
            Assert.False(control.VisibilityState.IsHidden);
            Assert.Equal(2, control.VisibleSiteCount);

            control.Apply(
                AffiliatedSitesCatalogPresentation.ApprovedV1,
                new AffiliatedSitesVisibilityPresentation(true, 10, true, null));
            StaTest.Prepare(control, 760, 160);
            Assert.Equal(0, control.VisibleSiteCount);
            Assert.Equal(0, control.VisiblePreviewCount);
            Assert.Equal("Hidden", AutomationProperties.GetItemStatus(control));
            StaTest.FindByAutomationName<Button>(control, "Show Affiliated Sites")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(requested.IsHidden);
            Assert.Equal(10, requested.ExpectedRevision);

            const string unavailable = "Visibility is locked by the local profile.";
            control.Apply(
                AffiliatedSitesCatalogPresentation.ApprovedV1,
                new AffiliatedSitesVisibilityPresentation(true, 11, false, unavailable));
            var disabled = StaTest.FindByAutomationName<Button>(control, "Show Affiliated Sites");
            Assert.False(disabled.IsEnabled);
            Assert.Equal(unavailable, AutomationProperties.GetHelpText(disabled));
        });
    }

    [Fact]
    public void CategoryIconsRemainTransparentDistinctAndStaticInReducedMotion()
    {
        StaTest.Run(() =>
        {
            var masks = new HashSet<ulong>();
            foreach (var kind in Enum.GetValues<AffiliatedSiteIconKind>())
            {
                var icon = new AffiliatedSiteIcon { Kind = kind };
                StaTest.Prepare(icon, 34, 34);
                var bitmap = new RenderTargetBitmap(34, 34, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(icon);
                var pixels = new byte[34 * 34 * 4];
                bitmap.CopyPixels(pixels, 34 * 4, 0);

                Assert.Equal(0, pixels[3]);
                Assert.Equal(0, pixels[((34 * 34) - 1) * 4 + 3]);
                Assert.True(pixels.Where((_, index) => index % 4 == 3).Count(alpha => alpha > 0) > 20);
                ulong mask = 1469598103934665603;
                for (var index = 3; index < pixels.Length; index += 4)
                {
                    mask = (mask ^ pixels[index]) * 1099511628211;
                }
                masks.Add(mask);
            }
            Assert.Equal(Enum.GetValues<AffiliatedSiteIconKind>().Length, masks.Count);

            var control = new AffiliatedSitesControl { ReducedMotion = true };
            control.Apply(
                AffiliatedSitesCatalogPresentation.ApprovedV1,
                new AffiliatedSitesVisibilityPresentation(false, 1, true, null));
            StaTest.Prepare(control, 760, 280);
            Assert.All(
                StaTest.Descendants(control).OfType<Animatable>(),
                animatable => Assert.False(animatable.HasAnimatedProperties));
            Assert.DoesNotContain(
                typeof(AffiliatedSitesControl).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public),
                field => field.FieldType == typeof(DispatcherTimer) || typeof(Clock).IsAssignableFrom(field.FieldType));
        });
    }
}
