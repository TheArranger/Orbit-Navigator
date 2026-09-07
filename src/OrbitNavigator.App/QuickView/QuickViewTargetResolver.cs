using OrbitNavigator.Foundation.Browser;
using OrbitNavigator.Presentation.Navigation;

namespace OrbitNavigator.App.QuickView;

internal static class QuickViewTargetResolver
{
    public static Uri Resolve(string? query, Uri? selectedPage, Uri? currentQuickViewPage = null)
    {
        if (!string.IsNullOrWhiteSpace(query))
        {
            return CanonicalWebAddress.Normalize(new OmniboxTargetResolver().Resolve(query).Uri);
        }

        var fallback = currentQuickViewPage ?? selectedPage;
        if (fallback is { IsAbsoluteUri: true } && fallback.Scheme is "http" or "https")
        {
            return CanonicalWebAddress.Normalize(fallback);
        }

        throw new ArgumentException(
            "Quick View requires a normal web page or a search/address query.",
            nameof(query));
    }
}
