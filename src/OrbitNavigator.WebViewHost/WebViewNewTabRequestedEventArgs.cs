namespace OrbitNavigator.WebViewHost;

public sealed class WebViewNewTabRequestedEventArgs(Uri target, bool isUserInitiated) : EventArgs
{
    public Uri Target { get; } = target ?? throw new ArgumentNullException(nameof(target));

    public bool IsUserInitiated { get; } = isUserInitiated;
}
