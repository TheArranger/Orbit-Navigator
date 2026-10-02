using Microsoft.Web.WebView2.Core;
using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.WebViewHost;
using Xunit;

namespace OrbitNavigator.WebViewHost.Tests;

public sealed class WebViewDownloadStateMapperTests
{
    [Theory]
    [InlineData(CoreWebView2DownloadState.InProgress, false, DownloadLifecycleState.InProgress)]
    [InlineData(CoreWebView2DownloadState.Completed, false, DownloadLifecycleState.Completed)]
    [InlineData(CoreWebView2DownloadState.Interrupted, false, DownloadLifecycleState.Failed)]
    [InlineData(CoreWebView2DownloadState.Interrupted, true, DownloadLifecycleState.Cancelled)]
    public void MapsOnlyExplicitCancellationToCancelled(
        CoreWebView2DownloadState source,
        bool cancellationRequested,
        DownloadLifecycleState expected) =>
        Assert.Equal(expected, WebViewDownloadStateMapper.Map(source, cancellationRequested));

    [Fact]
    public void RejectsUnknownOrUnrepresentableTotalSizes()
    {
        Assert.Null(WebViewDownloadStateMapper.NormalizeTotalBytes(null));
        Assert.Null(WebViewDownloadStateMapper.NormalizeTotalBytes(ulong.MaxValue));
        Assert.Equal(123L, WebViewDownloadStateMapper.NormalizeTotalBytes(123));
    }
}
