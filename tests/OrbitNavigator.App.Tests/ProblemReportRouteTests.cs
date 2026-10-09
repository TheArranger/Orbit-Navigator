using OrbitNavigator.App.Support;
using Xunit;

namespace OrbitNavigator.App.Tests;

public sealed class ProblemReportRouteTests
{
    private static readonly Version CurrentVersion = new(0, 2, 1, 0);

    [Fact]
    public void DefaultHandoffHasOnlyTheVerifiedProjectAndPlatformContext()
    {
        var route = ProblemReportRoute.Create(CurrentVersion, includeVersion: false);

        Assert.Equal("https://iamtheparadox.com/report-issue?project=orbit-navigator&platform=windows", route.AbsoluteUri);
        Assert.True(ProblemReportRoute.IsAllowed(route, CurrentVersion));
        Assert.Empty(route.UserInfo);
        Assert.Empty(route.Fragment);
    }

    [Fact]
    public void VersionIsOptInAndUsesOnlyNumericReleaseFields()
    {
        var route = ProblemReportRoute.Create(CurrentVersion, includeVersion: true);

        Assert.Equal("?project=orbit-navigator&platform=windows&version=0.2.1", route.Query);
        Assert.Equal("0.2.1", ProblemReportRoute.DisplayVersion(CurrentVersion));
        Assert.True(ProblemReportRoute.IsAllowed(route, CurrentVersion));
        Assert.DoesNotContain("version=", ProblemReportRoute.Create(null, true).Query);
        Assert.Equal("Unknown", ProblemReportRoute.DisplayVersion(null));
        Assert.Equal("1.2.0", ProblemReportRoute.DisplayVersion(new Version(1, 2)));
    }

    [Theory]
    [InlineData("http://iamtheparadox.com/report-issue?project=orbit-navigator&platform=windows")]
    [InlineData("https://iamtheparadox.com:444/report-issue?project=orbit-navigator&platform=windows")]
    [InlineData("https://iamtheparadox.com.evil.example/report-issue?project=orbit-navigator&platform=windows")]
    [InlineData("https://user:secret@iamtheparadox.com/report-issue?project=orbit-navigator&platform=windows")]
    [InlineData("https://iamtheparadox.com/report-issue?project=orbit-navigator&platform=windows#secret")]
    [InlineData("https://iamtheparadox.com/report-issue?project=orbit-navigator&platform=windows&url=https%3A%2F%2Fprivate.example")]
    [InlineData("https://iamtheparadox.com/report-issue?project=orbit-navigator&platform=windows&version=9.9.9")]
    [InlineData("https://iamtheparadox.com/api/bugs")]
    [InlineData("https://iamtheparadox.com/login?next=https://evil.example")]
    [InlineData("https://github.com/TheArranger/Orbit-Navigator/issues/new?template=bug_report.yml&body=secret")]
    [InlineData("/report-issue")]
    public void HandoffCannotAddSensitiveFieldsOrChangeTheVerifiedDestination(string address) =>
        Assert.False(ProblemReportRoute.IsAllowed(new Uri(address, UriKind.RelativeOrAbsolute), CurrentVersion));

    [Fact]
    public void ExistingFallbackAndPrivateSecurityRoutesRemainExplicitAndFixed()
    {
        Assert.True(ProblemReportRoute.IsAllowed(ProblemReportRoute.PublicBugForm, CurrentVersion));
        Assert.True(ProblemReportRoute.IsAllowed(ProblemReportRoute.PrivateSecurityForm, CurrentVersion));
        Assert.False(ProblemReportRoute.IsAllowed(null, CurrentVersion));
    }
}
