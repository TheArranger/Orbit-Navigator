namespace OrbitNavigator.Foundation.Browser;

public static class CanonicalWebAddress
{
    public static Uri Normalize(Uri target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!target.IsAbsoluteUri || target.Scheme is not ("http" or "https"))
        {
            return target;
        }

        var canonical = target;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (!(canonical.Host.Equals("http", StringComparison.OrdinalIgnoreCase) ||
                  canonical.Host.Equals("https", StringComparison.OrdinalIgnoreCase)) ||
                !canonical.AbsolutePath.StartsWith("//", StringComparison.Ordinal))
            {
                break;
            }

            var nested = $"{canonical.Host}:{canonical.PathAndQuery}{canonical.Fragment}";
            if (Uri.TryCreate(nested, UriKind.Absolute, out var parsed) &&
                parsed.Scheme is "http" or "https" &&
                string.IsNullOrEmpty(parsed.UserInfo) &&
                !string.IsNullOrWhiteSpace(parsed.IdnHost))
            {
                canonical = parsed;
                continue;
            }

            break;
        }

        return canonical;
    }
}
