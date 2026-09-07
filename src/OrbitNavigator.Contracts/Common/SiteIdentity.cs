namespace OrbitNavigator.Contracts.Common;

public sealed class SiteIdentity : IEquatable<SiteIdentity>
{
    private SiteIdentity(string canonicalOrigin, string displayOrigin)
    {
        CanonicalOrigin = canonicalOrigin;
        DisplayOrigin = displayOrigin;
    }

    public string CanonicalOrigin { get; }

    public string DisplayOrigin { get; }

    public static ControllerResult<SiteIdentity> Create(Uri trustedAbsoluteUri)
    {
        return TryCreate(trustedAbsoluteUri, out var site)
            ? ControllerResult<SiteIdentity>.Success(site!)
            : ControllerResult<SiteIdentity>.Failure(ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "error.site.invalid"));
    }

    public static bool TryCreate(Uri? trustedAbsoluteUri, out SiteIdentity? site)
    {
        site = null;
        if (trustedAbsoluteUri is not { IsAbsoluteUri: true } ||
            trustedAbsoluteUri.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(trustedAbsoluteUri.IdnHost) ||
            !string.IsNullOrEmpty(trustedAbsoluteUri.UserInfo))
        {
            return false;
        }

        try
        {
            var scheme = trustedAbsoluteUri.Scheme.ToLowerInvariant();
            var host = trustedAbsoluteUri.IdnHost.ToLowerInvariant();
            var port = trustedAbsoluteUri.IsDefaultPort ? -1 : trustedAbsoluteUri.Port;
            var canonicalBuilder = new UriBuilder(scheme, host, port);
            var canonical = canonicalBuilder.Uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            var display = trustedAbsoluteUri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            site = new SiteIdentity(canonical, display);
            return true;
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    public bool Equals(SiteIdentity? other) =>
        other is not null &&
        string.Equals(CanonicalOrigin, other.CanonicalOrigin, StringComparison.Ordinal);

    public override bool Equals(object? obj) =>
        obj is SiteIdentity other && Equals(other);

    public override int GetHashCode() =>
        StringComparer.Ordinal.GetHashCode(CanonicalOrigin);

    public override string ToString() => DisplayOrigin;
}

