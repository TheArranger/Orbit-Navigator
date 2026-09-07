namespace OrbitNavigator.Presentation.Workspace;

public readonly record struct AffiliatedSiteId(Guid Value)
{
    public bool IsEmpty => Value == Guid.Empty;
}

public enum AffiliatedSiteIconKind
{
    PublicSite = 0,
    Community = 1,
    Tools = 2,
    Media = 3,
    Learning = 4,
}

/// <summary>A reviewed owner-approved HTTPS destination; never inferred from browser activity.</summary>
public sealed record AffiliatedSitePresentation(
    AffiliatedSiteId SiteId,
    string Title,
    string Purpose,
    Uri Target,
    string TrustedDomain,
    AffiliatedSiteIconKind IconKind)
{
    public AffiliatedSitePresentation Validate()
    {
        if (SiteId.IsEmpty || string.IsNullOrWhiteSpace(Title) || Title.Trim().Length > 80 ||
            string.IsNullOrWhiteSpace(Purpose) || Purpose.Trim().Length > 180)
        {
            throw new ArgumentException("An affiliated site requires a stable ID and concise reviewed copy.");
        }

        ArgumentNullException.ThrowIfNull(Target);
        if (!Target.IsAbsoluteUri || !string.Equals(Target.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(Target.UserInfo) || !string.IsNullOrEmpty(Target.Query) ||
            !string.IsNullOrEmpty(Target.Fragment) || Target.IsLoopback)
        {
            throw new ArgumentException(
                "An affiliated site must be an approved HTTPS destination without embedded credentials, query, fragment, or loopback target.",
                nameof(Target));
        }

        if (string.IsNullOrWhiteSpace(TrustedDomain) || TrustedDomain.Trim().Length > 253 ||
            !string.Equals(Target.IdnHost, TrustedDomain.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The trusted domain must exactly match the reviewed destination host.", nameof(TrustedDomain));
        }

        if (!Enum.IsDefined(IconKind))
        {
            throw new ArgumentOutOfRangeException(nameof(IconKind));
        }

        return this;
    }
}

/// <summary>
/// A deliberately non-navigable preview. It has no destination field so a
/// placeholder can never be mistaken for, or converted into, a launch action.
/// </summary>
public sealed record AffiliatedSitePreviewPresentation(
    AffiliatedSiteId PreviewId,
    string Title,
    string StatusCopy,
    AffiliatedSiteIconKind IconKind)
{
    public AffiliatedSitePreviewPresentation Validate()
    {
        if (PreviewId.IsEmpty || string.IsNullOrWhiteSpace(Title) || Title.Trim().Length > 80 ||
            string.IsNullOrWhiteSpace(StatusCopy) || StatusCopy.Trim().Length > 120 ||
            Title.Contains("://", StringComparison.Ordinal) ||
            StatusCopy.Contains("://", StringComparison.Ordinal))
        {
            throw new ArgumentException("An affiliated preview requires a stable ID and concise URL-free copy.");
        }

        if (!Enum.IsDefined(IconKind))
        {
            throw new ArgumentOutOfRangeException(nameof(IconKind));
        }

        return this;
    }
}

/// <summary>
/// Local owner-curated catalog projection. Presentation never reads browsing
/// history, telemetry, advertisements, remote icon URLs, or network catalogs.
/// </summary>
public sealed record AffiliatedSitesCatalogPresentation(
    string CatalogId,
    long Revision,
    IReadOnlyList<AffiliatedSitePresentation> Sites,
    IReadOnlyList<AffiliatedSitePreviewPresentation> Previews)
{
    public const string OwnerCatalogRelativePath = "config/affiliated-sites.json";

    public static AffiliatedSitesCatalogPresentation Empty { get; } = new("owner-curated", 0, [], []);

    /// <summary>
    /// The explicitly approved launch catalog. Authentication at the destination
    /// is permitted; Navigator never receives or stores site credentials here.
    /// </summary>
    public static AffiliatedSitesCatalogPresentation ApprovedV1 { get; } = new AffiliatedSitesCatalogPresentation(
        "owner-approved-v1",
        1,
        [
            new AffiliatedSitePresentation(
                new AffiliatedSiteId(new Guid("ab59502e-c7b3-45ee-a649-f39a9f60eb5e")),
                "Beacon Spire",
                "Public browser game site by ParadoxTheCreator.",
                new Uri("https://beacon-spire.dps-games.cc/"),
                "beacon-spire.dps-games.cc",
                AffiliatedSiteIconKind.Media),
            new AffiliatedSitePresentation(
                new AffiliatedSiteId(new Guid("80aa8bdb-2637-4478-b11d-320cc9dac4b0")),
                "My Orbit",
                "Account-based community and service site; sign-in is required for account features.",
                new Uri("https://my-orbit.snap-it.cc/"),
                "my-orbit.snap-it.cc",
                AffiliatedSiteIconKind.Community),
        ],
        [
            new AffiliatedSitePreviewPresentation(
                new AffiliatedSiteId(new Guid("9497bf58-d31b-4640-bd07-75d7c1e2b107")),
                "Wedding Dreamer",
                "Private preview / coming later.",
                AffiliatedSiteIconKind.PublicSite),
        ]).Validate();

    public AffiliatedSitesCatalogPresentation Validate()
    {
        if (string.IsNullOrWhiteSpace(CatalogId) || CatalogId.Trim().Length > 80 || Revision < 0)
        {
            throw new ArgumentException("A reviewable catalog identity and revision are required.");
        }

        ArgumentNullException.ThrowIfNull(Sites);
        foreach (var site in Sites)
        {
            site.Validate();
        }

        ArgumentNullException.ThrowIfNull(Previews);
        foreach (var preview in Previews)
        {
            preview.Validate();
        }

        if (Sites.Select(site => site.SiteId).Distinct().Count() != Sites.Count ||
            Sites.Select(site => site.Target.IdnHost).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Sites.Count ||
            Previews.Select(preview => preview.PreviewId).Distinct().Count() != Previews.Count ||
            Sites.Select(site => site.SiteId).Intersect(Previews.Select(preview => preview.PreviewId)).Any())
        {
            throw new ArgumentException("Affiliated site and preview IDs and trusted domains must be unique.", nameof(Sites));
        }

        return this;
    }
}

public sealed record AffiliatedSitesVisibilityPresentation(
    bool IsHidden,
    long Revision,
    bool CanChange,
    string? UnavailableReason)
{
    public AffiliatedSitesVisibilityPresentation Validate()
    {
        if (Revision < 0 || (!CanChange && string.IsNullOrWhiteSpace(UnavailableReason)))
        {
            throw new ArgumentException("Visibility state requires a revision and truthful capability explanation.");
        }

        return this;
    }
}
