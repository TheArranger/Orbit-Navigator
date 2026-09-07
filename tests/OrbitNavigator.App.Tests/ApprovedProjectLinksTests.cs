using Xunit;

namespace OrbitNavigator.App.Tests;

public sealed class ApprovedProjectLinksTests
{
    [Theory]
    [InlineData((int)ApprovedProjectLink.Donation, "https://ko-fi.com/paradoxthecreator")]
    [InlineData((int)ApprovedProjectLink.Portfolio, "https://iamtheparadox.com/")]
    public void ResolveReturnsExactTrackingFreeHttpsDestination(
        int linkValue,
        string expected)
    {
        var target = ApprovedProjectLinks.Resolve((ApprovedProjectLink)linkValue);

        Assert.Equal(expected, target.AbsoluteUri);
        Assert.Equal(Uri.UriSchemeHttps, target.Scheme);
        Assert.Empty(target.UserInfo);
        Assert.Empty(target.Query);
        Assert.Empty(target.Fragment);
    }

    [Fact]
    public void ResolveRejectsUnknownLinkKind() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ApprovedProjectLinks.Resolve((ApprovedProjectLink)99));
}
