using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.WebViewHost;

/// <summary>
/// Memory-only audio state emitted by one WebView-backed tab. It intentionally
/// contains no URL, page content, profile data, or persistence identity.
/// </summary>
public sealed record WebViewTabAudioState(
    BrowserTabId TabId,
    long Revision,
    bool IsPlayingAudio,
    bool IsMuted)
{
    public WebViewTabAudioState Validate()
    {
        if (TabId.IsEmpty)
        {
            throw new ArgumentException("A tab ID is required.", nameof(TabId));
        }
        if (Revision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Revision));
        }
        return this;
    }
}

public sealed class WebViewTabAudioStateChangedEventArgs(WebViewTabAudioState state) : EventArgs
{
    public WebViewTabAudioState State { get; } = state?.Validate() ??
        throw new ArgumentNullException(nameof(state));
}
