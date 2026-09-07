using OrbitNavigator.Presentation.Workspace;
using OrbitNavigator.Presentation.Wpf;
using Xunit;

namespace OrbitNavigator.App.Tests;

public sealed class AffiliatedSitesHostPolicyTests
{
    [Fact]
    public void CatalogHasExactlyTwoApprovedTargetsAndOneNonNavigablePreview()
    {
        var catalog = AffiliatedSitesHostPolicy.Catalog;

        Assert.Equal("owner-approved-v1", catalog.CatalogId);
        Assert.Equal(1, catalog.Revision);
        Assert.Equal(
            ["https://beacon-spire.dps-games.cc/", "https://my-orbit.snap-it.cc/"],
            catalog.Sites.Select(site => site.Target.AbsoluteUri).ToArray());
        var preview = Assert.Single(catalog.Previews);
        Assert.Equal("Wedding Dreamer", preview.Title);
        Assert.Equal("Private preview / coming later.", preview.StatusCopy);
        Assert.Null(typeof(AffiliatedSitePreviewPresentation).GetProperty("Target"));
        Assert.DoesNotContain(catalog.Sites, site =>
            site.Target.Host.Contains("firststep", StringComparison.OrdinalIgnoreCase) ||
            site.Target.Host.Contains("metafree", StringComparison.OrdinalIgnoreCase) ||
            site.Target.Host.Contains("workflows", StringComparison.OrdinalIgnoreCase) ||
            site.Target.Host.Contains("infinity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HostAcceptsOnlyTheExactCompiledCatalogEntryAndRevision()
    {
        var catalog = AffiliatedSitesHostPolicy.Catalog;
        var site = catalog.Sites[0];
        var accepted = new AffiliatedSiteLaunchRequestedEventArgs(
            site,
            catalog.CatalogId,
            catalog.Revision);
        var wrongRevision = new AffiliatedSiteLaunchRequestedEventArgs(
            site,
            catalog.CatalogId,
            catalog.Revision + 1);
        var mutated = new AffiliatedSiteLaunchRequestedEventArgs(
            site with
            {
                Target = new Uri("https://example.invalid/"),
                TrustedDomain = "example.invalid",
            },
            catalog.CatalogId,
            catalog.Revision);

        Assert.True(AffiliatedSitesHostPolicy.TryResolveApprovedTarget(accepted, out var target));
        Assert.Equal(site.Target, target);
        Assert.False(AffiliatedSitesHostPolicy.TryResolveApprovedTarget(wrongRevision, out _));
        Assert.False(AffiliatedSitesHostPolicy.TryResolveApprovedTarget(mutated, out _));
    }
}
