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
    bool IsPrivate);

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

