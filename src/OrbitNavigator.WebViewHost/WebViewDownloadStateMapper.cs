using Microsoft.Web.WebView2.Core;
using OrbitNavigator.Contracts.Browser;

namespace OrbitNavigator.WebViewHost;

public static class WebViewDownloadStateMapper
{
    public static DownloadLifecycleState Map(
        CoreWebView2DownloadState state,
        bool cancellationRequested) => state switch
    {
        CoreWebView2DownloadState.InProgress => DownloadLifecycleState.InProgress,
        CoreWebView2DownloadState.Completed => DownloadLifecycleState.Completed,
        CoreWebView2DownloadState.Interrupted when cancellationRequested =>
            DownloadLifecycleState.Cancelled,
        CoreWebView2DownloadState.Interrupted => DownloadLifecycleState.Failed,
        _ => DownloadLifecycleState.Failed,
    };

    public static long? NormalizeTotalBytes(ulong? totalBytes) =>
        totalBytes is { } value && value <= long.MaxValue ? (long)value : null;
}
