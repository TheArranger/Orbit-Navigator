using System.Windows;

namespace OrbitNavigator.App.Composition;

/// <summary>Owns temporary native fullscreen without changing saved workspace preferences.</summary>
internal sealed class WindowFullscreenController(Window window, Action<bool> applyChrome)
{
    private WindowSnapshot? _snapshot;

    public bool IsFullscreen => _snapshot is not null;
    public object? Owner { get; private set; }

    public void Update(object owner, bool isSelected, bool isFullscreen)
    {
        if (!isFullscreen || !isSelected)
        {
            if (ReferenceEquals(Owner, owner)) Exit();
            return;
        }
        if (ReferenceEquals(Owner, owner)) return;
        Exit();

        var bounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.Width, window.Height)
            : window.RestoreBounds;
        var entry = new WindowSnapshot(window.WindowState, window.WindowStyle, window.ResizeMode, bounds);
        _snapshot = entry;
        Owner = owner;
        // Suspend the custom non-client frame before native maximization so the
        // host selects the full monitor, not its taskbar-excluding work area.
        applyChrome(true);
        // Applying browser chrome can synchronously reject fullscreen (for
        // example, an already-visible permission prompt requests Exit). Do not
        // re-enter native fullscreen after that callback restored the window.
        if (!ReferenceEquals(_snapshot, entry) || !ReferenceEquals(Owner, owner)) return;
        window.WindowState = WindowState.Normal;
        window.WindowStyle = WindowStyle.None;
        window.ResizeMode = ResizeMode.NoResize;
        window.WindowState = WindowState.Maximized;
    }

    public void Exit()
    {
        if (_snapshot is not { } snapshot) return;
        _snapshot = null;
        Owner = null;
        window.WindowState = WindowState.Normal;
        window.WindowStyle = snapshot.Style;
        window.ResizeMode = snapshot.ResizeMode;
        applyChrome(false);
        if (!snapshot.Bounds.IsEmpty)
        {
            window.Left = snapshot.Bounds.Left;
            window.Top = snapshot.Bounds.Top;
            window.Width = snapshot.Bounds.Width;
            window.Height = snapshot.Bounds.Height;
        }
        window.WindowState = snapshot.State;
    }

    private sealed record WindowSnapshot(
        WindowState State, WindowStyle Style, ResizeMode ResizeMode, Rect Bounds);
}
