using Xunit;

namespace OrbitNavigator.WebViewHost.Tests;

public sealed class WebViewNewTabRequestedEventArgsTests
{
    [Fact]
    public void ExplicitRequestsKeepTheirForegroundDefault()
    {
        var request = new WebViewNewTabRequestedEventArgs(new Uri("https://example.test/"), true);

        Assert.True(request.Activate);
        Assert.True(request.IsUserInitiated);
    }

    [Fact]
    public void BackgroundDispositionIsIndependentOfUserInitiation()
    {
        var request = new WebViewNewTabRequestedEventArgs(new Uri("https://example.test/"), false, activate: false);

        Assert.False(request.Activate);
        Assert.False(request.IsUserInitiated);
        Assert.Equal("https://example.test/", request.Target.AbsoluteUri);
    }
}
