using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace OrbitNavigator.App.Composition;

/// <summary>Keeps native move/resize/maximize behavior while the browser draws its own caption.</summary>
internal sealed class BrowserWindowChromeController : IDisposable
{
    private const int GetMinMaxInfoMessage = 0x0024;
    private const int NonClientLeftButtonDownMessage = 0x00A1;
    private const int CaptionHit = 2;
    private const uint NearestMonitor = 2;
    internal const double ResizeBorderWidth = 6;
    private readonly Window _window;
    private readonly Action<bool> _applyWindowState;
    private readonly Action<Thickness>? _applyContentInset;
    private readonly WindowChrome _chrome = new()
    {
        CaptionHeight = 0,
        ResizeBorderThickness = new Thickness(ResizeBorderWidth),
        GlassFrameThickness = new Thickness(0),
        CornerRadius = new CornerRadius(0),
        UseAeroCaptionButtons = false,
    };
    private HwndSource? _source;
    private bool _fullscreen;
    private bool _disposed;

    public BrowserWindowChromeController(
        Window window,
        Action<bool> applyWindowState,
        Action<Thickness>? applyContentInset = null)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _applyWindowState = applyWindowState ?? throw new ArgumentNullException(nameof(applyWindowState));
        _applyContentInset = applyContentInset;
        window.WindowStyle = WindowStyle.None;
        window.ResizeMode = ResizeMode.CanResize;
        WindowChrome.SetWindowChrome(window, _chrome);
        window.SourceInitialized += OnSourceInitialized;
        window.StateChanged += OnStateChanged;
        window.PreviewKeyDown += OnPreviewKeyDown;
        ApplyWindowPresentation();
    }

    public void Minimize()
    {
        if (!_fullscreen) _window.WindowState = WindowState.Minimized;
    }

    public void ToggleMaximizeRestore()
    {
        if (_fullscreen) return;
        _window.WindowState = _window.WindowState == WindowState.Maximized
            ? WindowState.Normal : WindowState.Maximized;
    }

    public void Close() => _window.Close();

    public void BeginCaptionDrag(MouseButtonEventArgs args)
    {
        if (_fullscreen || args.ChangedButton != MouseButton.Left ||
            args.LeftButton != MouseButtonState.Pressed || _source is null) return;
        // Delegate to the native caption move loop: Windows retains drag-to-restore,
        // edge snapping, mixed-monitor movement, and the user's restore bounds.
        var position = _window.PointToScreen(args.GetPosition(_window));
        var packedPosition = unchecked((int)position.Y << 16 | ((int)position.X & 0xffff));
        ReleaseCapture();
        SendMessage(_source.Handle, NonClientLeftButtonDownMessage, CaptionHit, packedPosition);
        args.Handled = true;
    }

    public void ApplyFullscreen(bool fullscreen)
    {
        _fullscreen = fullscreen;
        WindowChrome.SetWindowChrome(_window, fullscreen ? null : _chrome);
        ApplyWindowPresentation();
    }

    private void OnSourceInitialized(object? sender, EventArgs args)
    {
        _source = HwndSource.FromHwnd(new WindowInteropHelper(_window).Handle);
        _source?.AddHook(WindowProcedure);
    }

    private void OnStateChanged(object? sender, EventArgs args) => ApplyWindowPresentation();

    private void ApplyWindowPresentation()
    {
        _applyWindowState(_window.WindowState == WindowState.Maximized);
        _applyContentInset?.Invoke(GetContentInset(_window.WindowState, _fullscreen));
    }

    // WindowChrome consumes these pixels for native edge resizing. Keep scrollbars
    // and other interactive content entirely inside them while the window floats.
    internal static Thickness GetContentInset(WindowState state, bool fullscreen) =>
        new(fullscreen || state == WindowState.Maximized ? 0 : ResizeBorderWidth);

    private void OnPreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (_fullscreen || args.Key != Key.System || args.SystemKey != Key.Space) return;
        SystemCommands.ShowSystemMenu(_window, _window.PointToScreen(new Point(8, 8)));
        args.Handled = true;
    }

    private nint WindowProcedure(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != GetMinMaxInfoMessage || lParam == 0) return 0;
        var monitor = MonitorFromWindow(window, NearestMonitor);
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref monitorInfo)) return 0;
        var target = GetMaximizedBounds(monitorInfo.Monitor.ToRect(), monitorInfo.Work.ToRect(), _fullscreen);
        var bounds = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        bounds.MaxPosition = new NativePoint((int)target.X, (int)target.Y);
        bounds.MaxSize = new NativePoint((int)target.Width, (int)target.Height);
        var dpi = VisualTreeHelper.GetDpi(_window);
        var minimum = GetMinimumTrackSize(_window.MinWidth, _window.MinHeight, dpi.DpiScaleX, dpi.DpiScaleY);
        bounds.MinTrackSize.X = Math.Max(bounds.MinTrackSize.X, (int)minimum.Width);
        bounds.MinTrackSize.Y = Math.Max(bounds.MinTrackSize.Y, (int)minimum.Height);
        Marshal.StructureToPtr(bounds, lParam, false);
        handled = true;
        return 0;
    }

    // WM_GETMINMAXINFO uses monitor-relative pixel coordinates, not WPF DIPs.
    // Work-area offsets handle taskbars on any edge and monitors left/above primary.
    internal static Rect GetMaximizedBounds(Rect monitor, Rect workArea, bool fullscreen)
    {
        var target = fullscreen ? monitor : workArea;
        return new Rect(target.Left - monitor.Left, target.Top - monitor.Top, target.Width, target.Height);
    }

    internal static Size GetMinimumTrackSize(double width, double height, double scaleX, double scaleY) =>
        new(Math.Ceiling(width * scaleX), Math.Ceiling(height * scaleY));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window.SourceInitialized -= OnSourceInitialized;
        _window.StateChanged -= OnStateChanged;
        _window.PreviewKeyDown -= OnPreviewKeyDown;
        _source?.RemoveHook(WindowProcedure);
        _source = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public readonly Rect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
}
