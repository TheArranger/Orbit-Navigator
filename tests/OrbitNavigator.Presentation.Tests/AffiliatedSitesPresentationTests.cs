using OrbitNavigator.Presentation.Workspace;

using Xunit;

namespace OrbitNavigator.Presentation.Tests;

public sealed class AffiliatedSitesPresentationTests
{
    [Fact]
    public void DefaultCatalogIsEmptyAndPointsToReviewableLocalOwnerPath()
    {
        var catalog = AffiliatedSitesCatalogPresentation.Empty.Validate();

        Assert.Empty(catalog.Sites);
        Assert.Empty(catalog.Previews);
        Assert.Equal("config/affiliated-sites.json", AffiliatedSitesCatalogPresentation.OwnerCatalogRelativePath);
        Assert.DoesNotContain("http", AffiliatedSitesCatalogPresentation.OwnerCatalogRelativePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApprovedCatalogContainsOnlyExplicitlyApprovedDestinations()
    {
        var catalog = AffiliatedSitesCatalogPresentation.ApprovedV1;

        Assert.Equal(1, catalog.Revision);
        Assert.Collection(
            catalog.Sites,
            beacon =>
            {
                Assert.Equal("Beacon Spire", beacon.Title);
                Assert.Equal("https://beacon-spire.dps-games.cc/", beacon.Target.AbsoluteUri);
                Assert.Equal("beacon-spire.dps-games.cc", beacon.TrustedDomain);
            },
            orbit =>
            {
                Assert.Equal("My Orbit", orbit.Title);
                Assert.Equal("https://my-orbit.snap-it.cc/", orbit.Target.AbsoluteUri);
                Assert.Equal("my-orbit.snap-it.cc", orbit.TrustedDomain);
                Assert.Contains("Account-based community and service", orbit.Purpose, StringComparison.Ordinal);
                Assert.Contains("sign-in", orbit.Purpose, StringComparison.OrdinalIgnoreCase);
            });

        Assert.DoesNotContain(
            catalog.Sites,
            site => site.Title.Contains("National Chat", StringComparison.OrdinalIgnoreCase) ||
                    site.TrustedDomain.Contains("nationalchat", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            catalog.Sites,
            site => site.Title is "First Step" or "MetaFree" or "Workflows");

        var preview = Assert.Single(catalog.Previews);
        Assert.Equal("Wedding Dreamer", preview.Title);
        Assert.Equal("Private preview / coming later.", preview.StatusCopy);
        Assert.DoesNotContain("://", preview.StatusCopy, StringComparison.Ordinal);
        Assert.Null(typeof(AffiliatedSitePreviewPresentation).GetProperty("Target"));
    }

    [Fact]
    public void ApprovedCatalogAllowsAuthenticationAtDestinationButNeverEmbedsCredentials()
    {
        var orbit = Assert.Single(
            AffiliatedSitesCatalogPresentation.ApprovedV1.Sites,
            site => site.Title == "My Orbit");

        Assert.Equal(Uri.UriSchemeHttps, orbit.Target.Scheme);
        Assert.Empty(orbit.Target.UserInfo);
        Assert.Empty(orbit.Target.Query);
        Assert.Empty(orbit.Target.Fragment);
    }

    [Theory]
    [InlineData("http://public.example.test/")]
    [InlineData("https://user:secret@public.example.test/")]
    [InlineData("https://public.example.test/?campaign=tracking")]
    [InlineData("https://public.example.test/#private")]
    [InlineData("https://127.0.0.1/")]
    public void CatalogRejectsUnsafeOrNonPublicTargets(string value)
    {
        var site = Site(new Uri(value), "public.example.test");

        Assert.Throws<ArgumentException>(site.Validate);
    }

    [Fact]
    public void CatalogRequiresExactTrustedDomainAndUniqueReviewedSites()
    {
        var site = Site(new Uri("https://public.example.test/ready"), "other.example.test");
        Assert.Throws<ArgumentException>(site.Validate);

        var valid = Site(new Uri("https://public.example.test/ready"), "public.example.test");
        var duplicate = valid with { SiteId = new AffiliatedSiteId(Guid.NewGuid()) };
        Assert.Throws<ArgumentException>(() => new AffiliatedSitesCatalogPresentation(
            "reviewed",
            1,
            [valid, duplicate],
            []).Validate());
    }

    [Fact]
    public void VisibilityMutationRequiresTruthfulHostCapability()
    {
        Assert.Throws<ArgumentException>(() => new AffiliatedSitesVisibilityPresentation(
            false,
            1,
            false,
            null).Validate());
        Assert.True(new AffiliatedSitesVisibilityPresentation(
            true,
            2,
            false,
            "Visibility changes are unavailable in private browsing.").Validate().IsHidden);
    }

    private static AffiliatedSitePresentation Site(Uri target, string domain) => new(
        new AffiliatedSiteId(Guid.NewGuid()),
        "Reviewed site",
        "A concise, owner-approved public destination.",
        target,
        domain,
        AffiliatedSiteIconKind.PublicSite);
}
