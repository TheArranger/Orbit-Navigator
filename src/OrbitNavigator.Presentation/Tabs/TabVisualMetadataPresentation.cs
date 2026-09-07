using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.Presentation.Tabs;

/// <summary>
/// Host-supplied, privacy-local visual identity for one tab. The favicon is
/// decoded by the WPF view only; Presentation never performs a network fetch.
/// </summary>
public sealed record TabVisualMetadataPresentation(
    BrowserTabId TabId,
    long Revision,
    string PageTitle,
    string SiteName,
    ReadOnlyMemory<byte> FaviconPng = default)
{
    public const int MaximumFaviconBytes = 256 * 1024;
    public const int MaximumTitleLength = 512;
    public const int MaximumSiteNameLength = 253;

    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    public TabVisualMetadataPresentation Validate()
    {
        if (TabId.IsEmpty)
        {
            throw new ArgumentException("A tab visual identity requires a tab ID.", nameof(TabId));
        }
        if (Revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Revision));
        }

        var title = Normalize(PageTitle, MaximumTitleLength);
        var siteName = Normalize(SiteName, MaximumSiteNameLength);
        var favicon = IsSafePng(FaviconPng.Span) ? FaviconPng.ToArray() : [];
        return this with
        {
            PageTitle = title,
            SiteName = siteName,
            FaviconPng = favicon,
        };
    }

    public static bool IsSafePng(ReadOnlySpan<byte> bytes) =>
        bytes.Length is >= 8 and <= MaximumFaviconBytes &&
        bytes[..PngSignature.Length].SequenceEqual(PngSignature);

    private static string Normalize(string? value, int maximumLength)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }
}
