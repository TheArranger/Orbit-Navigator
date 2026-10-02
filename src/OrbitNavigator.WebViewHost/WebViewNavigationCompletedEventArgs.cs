using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.WebViewHost;

public sealed class WebViewNavigationCompletedEventArgs(
    BrowserTabId tabId,
    Uri address,
    string title) : EventArgs
{
    public BrowserTabId TabId { get; } = tabId;
    public Uri Address { get; } = address ?? throw new ArgumentNullException(nameof(address));
    public string Title { get; } = title ?? string.Empty;
}
