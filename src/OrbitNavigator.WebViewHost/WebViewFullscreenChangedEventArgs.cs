namespace OrbitNavigator.WebViewHost;

public sealed class WebViewFullscreenChangedEventArgs(bool isFullscreen) : EventArgs
{
    public bool IsFullscreen { get; } = isFullscreen;
}
