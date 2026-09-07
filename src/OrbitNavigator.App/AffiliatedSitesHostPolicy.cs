using OrbitNavigator.Presentation.Workspace;
using OrbitNavigator.Presentation.Wpf;

namespace OrbitNavigator.App;

internal static class AffiliatedSitesHostPolicy
{
    internal static AffiliatedSitesCatalogPresentation Catalog =>
        AffiliatedSitesCatalogPresentation.ApprovedV1;

    internal static bool TryResolveApprovedTarget(
        AffiliatedSiteLaunchRequestedEventArgs request,
        out Uri? target)
    {
        ArgumentNullException.ThrowIfNull(request);
        target = null;
        var catalog = Catalog;
        if (!string.Equals(request.CatalogId, catalog.CatalogId, StringComparison.Ordinal) ||
            request.ExpectedCatalogRevision != catalog.Revision)
        {
            return false;
        }

        var approved = catalog.Sites.SingleOrDefault(site => site.SiteId == request.Site.SiteId);
        if (approved is null || approved != request.Site)
        {
            return false;
        }

        target = approved.Target;
        return true;
    }
}
