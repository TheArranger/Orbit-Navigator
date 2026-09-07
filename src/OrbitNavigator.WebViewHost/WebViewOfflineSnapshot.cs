using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.WebViewHost;

/// <summary>
/// A non-executable viewport image captured only after an explicit user action.
/// It contains no DOM, scripts, cookies, headers, or live network capability.
/// </summary>
public sealed record WebViewOfflineSnapshot(
    BrowserTabId TabId,
    string Title,
    Uri SourceAddress,
    ReadOnlyMemory<byte> PngBytes);
