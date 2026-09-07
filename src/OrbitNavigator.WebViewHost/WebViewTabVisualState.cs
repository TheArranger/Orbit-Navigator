using System.Globalization;
using System.Text;

using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.WebViewHost;

/// <summary>
/// Memory-only visual/navigation state emitted by one WebView. It contains no
/// favicon URL and never causes a network request outside WebView2.
/// </summary>
public sealed record WebViewTabVisualState(
    BrowserTabId TabId,
    long Revision,
    Uri? Address,
    string PageTitle,
    string SiteName,
    bool IsLoading,
    bool CanGoBack,
    bool CanGoForward,
    ReadOnlyMemory<byte> FaviconPng)
{
    public const int MaximumFaviconBytes = 256 * 1024;
    public const int MaximumPageTitleLength = 512;
    public const int MaximumSiteNameLength = 253;

    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static WebViewTabVisualState Create(
        BrowserTabId tabId,
        long revision,
        Uri? address,
        string? documentTitle,
        string fallbackTitle,
        bool isLoading,
        bool canGoBack,
        bool canGoForward,
        ReadOnlySpan<byte> faviconPng)
    {
        if (tabId.IsEmpty)
        {
            throw new ArgumentException("A tab ID is required.", nameof(tabId));
        }
        if (revision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        var safeAddress = IsWebAddress(address) ? new Uri(address!.AbsoluteUri) : null;
        var siteName = Normalize(safeAddress?.IdnHost, MaximumSiteNameLength);
        var title = Normalize(documentTitle, MaximumPageTitleLength);
        if (title.Length == 0)
        {
            title = siteName.Length > 0
                ? siteName
                : Normalize(fallbackTitle, MaximumPageTitleLength);
        }
        if (title.Length == 0)
        {
            title = "New Tab";
        }
        else if (safeAddress is null &&
                 title.Equals("New tab", StringComparison.OrdinalIgnoreCase))
        {
            title = "New Tab";
        }

        var favicon = IsSafePng(faviconPng) ? faviconPng.ToArray() : [];
        return new(
            tabId,
            revision,
            safeAddress,
            title,
            siteName,
            isLoading,
            canGoBack,
            canGoForward,
            favicon);
    }

    public static bool IsSafePng(ReadOnlySpan<byte> bytes) =>
        bytes.Length is >= 8 and <= MaximumFaviconBytes &&
        bytes[..PngSignature.Length].SequenceEqual(PngSignature);

    private static bool IsWebAddress(Uri? address) =>
        address is { IsAbsoluteUri: true } &&
        address.Scheme is "http" or "https";

    private static string Normalize(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(value.Length, maximumLength));
        var pendingSpace = false;
        foreach (var character in value.Trim())
        {
            var category = char.GetUnicodeCategory(character);
            if (char.IsControl(character) || category == UnicodeCategory.Format)
            {
                continue;
            }
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (pendingSpace && builder.Length < maximumLength)
            {
                builder.Append(' ');
            }
            pendingSpace = false;
            if (builder.Length >= maximumLength)
            {
                break;
            }
            builder.Append(character);
        }
        return builder.ToString();
    }
}

public sealed class WebViewTabVisualStateChangedEventArgs(WebViewTabVisualState state) : EventArgs
{
    public WebViewTabVisualState State { get; } =
        state ?? throw new ArgumentNullException(nameof(state));
}
