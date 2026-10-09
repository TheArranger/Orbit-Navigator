namespace OrbitNavigator.WebViewHost;

public sealed class WebViewNewTabRequestedEventArgs(
    Uri target,
    bool isUserInitiated,
    bool activate = true) : EventArgs
{
    public Uri Target { get; } = target ?? throw new ArgumentNullException(nameof(target));

    public bool IsUserInitiated { get; } = isUserInitiated;

    /// <summary>Whether the owning browser should select the newly created tab.</summary>
    public bool Activate { get; } = activate;
}
