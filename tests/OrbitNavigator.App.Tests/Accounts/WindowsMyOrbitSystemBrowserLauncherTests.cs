using OrbitNavigator.App.Accounts;
using Xunit;

namespace OrbitNavigator.App.Tests.Accounts;

public sealed class WindowsMyOrbitSystemBrowserLauncherTests
{
    private static readonly Uri Authority = new("https://my-orbit.snap-it.cc/");
    private const string Handle = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public void AcceptsOnlyExactAuthorizationPathAndParShape()
    {
        var launcher = CreateLauncher();

        Assert.True(launcher.IsApprovedAuthorizationRequest(AuthorizationUri()));
        Assert.False(launcher.IsApprovedAuthorizationRequest(AuthorizationUri(path: "/oauth2/token")));
    }

    [Fact]
    public void RejectsExtraAndDuplicateParameters()
    {
        var launcher = CreateLauncher();

        Assert.False(launcher.IsApprovedAuthorizationRequest(AuthorizationUri(
            querySuffix: "&scope=orbit.navigator.link")));
        Assert.False(launcher.IsApprovedAuthorizationRequest(AuthorizationUri(
            querySuffix: "&client_id=orbit-navigator")));
        Assert.False(launcher.IsApprovedAuthorizationRequest(new Uri(
            $"{Authority}oauth2/authorize?request_uri={RequestUri()}&request_uri={RequestUri()}")));
    }

    [Fact]
    public void RejectsWrongClientAndMalformedParHandle()
    {
        var launcher = CreateLauncher();

        Assert.False(launcher.IsApprovedAuthorizationRequest(AuthorizationUri(clientId: "another-client")));
        Assert.False(launcher.IsApprovedAuthorizationRequest(AuthorizationUri(handle: Handle[..^1])));
        Assert.False(launcher.IsApprovedAuthorizationRequest(AuthorizationUri(handle: $"{Handle[..^1]}=")));
        Assert.False(launcher.IsApprovedAuthorizationRequest(AuthorizationUri(handle: $"{Handle[..^1]}!")));
    }

    [Fact]
    public void RejectsDecodedControlCharacters()
    {
        var launcher = CreateLauncher();

        Assert.False(launcher.IsApprovedAuthorizationRequest(new Uri(
            $"{Authority}oauth2/authorize?client_id=orbit-navigator%0A&request_uri={RequestUri()}")));
    }

    private static WindowsMyOrbitSystemBrowserLauncher CreateLauncher() =>
        new(Authority, _ => null);

    private static Uri AuthorizationUri(
        string path = "/oauth2/authorize",
        string clientId = "orbit-navigator",
        string handle = Handle,
        string querySuffix = "") => new(
            $"{Authority.GetLeftPart(UriPartial.Authority)}{path}?client_id={clientId}&request_uri={RequestUri(handle)}{querySuffix}");

    private static string RequestUri(string handle = Handle) =>
        $"urn:ietf:params:oauth:request_uri:mopr_{handle}";
}
