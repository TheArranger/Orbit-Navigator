using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.Contracts.Browser;

public readonly record struct BrowserTabGroupId(Guid Value)
{
    public bool IsEmpty => Value == Guid.Empty;
}

public enum BrowserLoadState
{
    Idle = 0,
    Loading = 1,
    Failed = 2,
}

/// <summary>
/// Identifies browser-owned content that participates in the ordinary tab model
/// without being loaded into a site WebView.
/// </summary>
public enum BrowserInternalPageKind
{
    None = 0,
    Settings = 1,
}

public abstract record BrowserCommand(
    BrowserWindowId WindowId,
    BrowserTabId TabId);

public sealed record NavigateBrowserCommand(
    BrowserWindowId WindowId,
    BrowserTabId TabId,
    Uri Target)
    : BrowserCommand(WindowId, TabId);

public sealed record GoBackBrowserCommand(
    BrowserWindowId WindowId,
    BrowserTabId TabId)
    : BrowserCommand(WindowId, TabId);

public sealed record GoForwardBrowserCommand(
    BrowserWindowId WindowId,
    BrowserTabId TabId)
    : BrowserCommand(WindowId, TabId);

public sealed record ReloadBrowserCommand(
    BrowserWindowId WindowId,
    BrowserTabId TabId)
    : BrowserCommand(WindowId, TabId);

public sealed record StopBrowserCommand(
    BrowserWindowId WindowId,
    BrowserTabId TabId)
    : BrowserCommand(WindowId, TabId);

public sealed record CreateTabBrowserCommand(
    BrowserWindowId WindowId,
    BrowserTabId TabId,
    Uri? InitialTarget,
    BrowserTabGroupId? GroupId)
    : BrowserCommand(WindowId, TabId);

public sealed record CloseTabBrowserCommand(
    BrowserWindowId WindowId,
    BrowserTabId TabId)
    : BrowserCommand(WindowId, TabId);

public sealed record SelectTabBrowserCommand(
    BrowserWindowId WindowId,
    BrowserTabId TabId)
    : BrowserCommand(WindowId, TabId);

public sealed record MoveTabBrowserCommand(
    BrowserWindowId WindowId,
    BrowserTabId TabId,
    int NewIndex,
    BrowserTabGroupId? GroupId)
    : BrowserCommand(WindowId, TabId);

public sealed record BrowserTabState(
    BrowserTabId TabId,
    BrowserTabGroupId? GroupId,
    Uri? Address,
    string Title,
    BrowserLoadState LoadState,
    bool CanGoBack,
    bool CanGoForward,
    bool IsPrivate)
{
    /// <summary>
    /// Browser-owned page displayed by this tab. Internal pages have no web
    /// address and therefore do not receive a WebView or site identity.
    /// </summary>
    public BrowserInternalPageKind InternalPage { get; init; }
}

public sealed record BrowserState(
    BrowserWindowId WindowId,
    BrowserTabId? SelectedTabId,
    IReadOnlyList<BrowserTabState> Tabs);

public sealed class BrowserStateChangedEventArgs : EventArgs
{
    public BrowserStateChangedEventArgs(BrowserState state)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
    }

    public BrowserState State { get; }
}

public interface IBrowserHost
{
    BrowserState CurrentState { get; }

    event EventHandler<BrowserStateChangedEventArgs>? StateChanged;

    ValueTask<ControllerResult> ExecuteAsync(
        BrowserCommand command,
        CancellationToken cancellationToken = default);
}

