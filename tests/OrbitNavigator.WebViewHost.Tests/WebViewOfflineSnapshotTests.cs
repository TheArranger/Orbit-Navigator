using OrbitNavigator.Contracts.Common;

using Xunit;

namespace OrbitNavigator.WebViewHost.Tests;

public sealed class WebViewOfflineSnapshotTests
{
    [Fact]
    public void PrivateCaptureIsPolicyDeniedBeforeWebViewInitialization()
    {
        RunSta(() =>
        {
            var host = new WebView2HostControl(Context(BrowserProfileMode.Private));
            try
            {
                var result = host.CaptureOfflineSnapshotAsync().AsTask().GetAwaiter().GetResult();

                Assert.False(result.IsSuccess);
                Assert.Equal(ControllerErrorCode.PolicyDenied, result.Error?.Code);
                Assert.Null(host.CoreWebView);
            }
            finally
            {
                host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public void NormalUninitializedCaptureFailsClosed()
    {
        RunSta(() =>
        {
            var host = new WebView2HostControl(Context(BrowserProfileMode.Normal));
            try
            {
                var result = host.CaptureOfflineSnapshotAsync().AsTask().GetAwaiter().GetResult();

                Assert.False(result.IsSuccess);
                Assert.Equal(ControllerErrorCode.Unavailable, result.Error?.Code);
            }
            finally
            {
                host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw failure;
        }
    }

    private static BrowsingContext Context(BrowserProfileMode mode)
    {
        var privacy = new PrivacyContext(
            new ProfileId(Guid.NewGuid()),
            new BrowserSessionId(Guid.NewGuid()),
            mode);
        return new BrowsingContext(
            privacy,
            new BrowserWindowId(Guid.NewGuid()),
            new BrowserTabId(Guid.NewGuid()),
            null);
    }
}
