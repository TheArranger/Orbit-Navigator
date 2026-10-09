using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using OrbitNavigator.App.Accounts;
using OrbitNavigator.Contracts.Common;
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

    [Fact]
    public async Task LaunchesResolvedExecutableDirectlyWithExactlyOneUriArgument()
    {
        ProcessStartInfo? captured = null;
        var resolveCalls = 0;
        var launcher = new WindowsMyOrbitSystemBrowserLauncher(Authority,
            () => { resolveCalls++; return External(); },
            info => { captured = info; return new Process(); });

        var result = await launcher.LaunchAsync(AuthorizationUri());

        Assert.True(result.IsSuccess);
        Assert.Equal(1, resolveCalls);
        Assert.NotNull(captured);
        Assert.Equal(External().ExecutablePath, captured.FileName);
        Assert.False(captured.UseShellExecute);
        Assert.Equal(AuthorizationUri().AbsoluteUri, Assert.Single(captured.ArgumentList));
        Assert.Equal(string.Empty, captured.Arguments);
        Assert.Equal(@"C:\External Browser", captured.WorkingDirectory);
    }

    [Fact]
    public async Task InvalidAuthorizationUriDoesNotResolveOrLaunchAnything()
    {
        var calls = 0;
        var launcher = new WindowsMyOrbitSystemBrowserLauncher(Authority,
            () => { calls++; return External(); }, _ => { calls++; return new Process(); });
        var result = await launcher.LaunchAsync(new Uri("https://attacker.invalid/oauth2/authorize"));
        Assert.Equal(ControllerErrorCode.PolicyDenied, result.Error?.Code);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task MissingAssociationFailsClosedWithoutShellFallback()
    {
        var launches = 0;
        var launcher = new WindowsMyOrbitSystemBrowserLauncher(Authority, () => null,
            _ => { launches++; return new Process(); });
        var result = await launcher.LaunchAsync(AuthorizationUri());
        Assert.Equal(ControllerErrorCode.Unavailable, result.Error?.Code);
        Assert.Equal("account.link.external-browser-unavailable", result.Error?.MessageKey);
        Assert.Equal(0, launches);
    }

    [Theory]
    [InlineData("Orbit Navigator.exe")]
    [InlineData("OrbitNavigator.App.exe")]
    [InlineData("OrbitNavigator.Launcher.exe")]
    [InlineData("ORBIT-NAVIGATOR.exe")]
    public async Task NavigatorExecutableNeverReceivesOAuthRequest(string filename)
    {
        var launches = 0;
        var launcher = new WindowsMyOrbitSystemBrowserLauncher(Authority,
            () => External() with { ExecutablePath = @"C:\Applications\" + filename },
            _ => { launches++; return new Process(); });
        Assert.Equal(ControllerErrorCode.Unavailable, (await launcher.LaunchAsync(AuthorizationUri())).Error?.Code);
        Assert.Equal(0, launches);
    }

    [Theory]
    [InlineData("Orbit Navigator", null, null, null)]
    [InlineData(null, "Orbit Navigator", null, null)]
    [InlineData(null, null, "OrbitNavigator.App.dll", null)]
    [InlineData(null, null, null, "Orbit Navigator.exe")]
    public void RenamedNavigatorIsRejectedByProductOrOriginalBinaryIdentity(
        string? product, string? description, string? original, string? internalName)
    {
        var renamed = new WindowsMyOrbitSystemBrowserLauncher.ExternalBrowserExecutable(
            @"C:\Applications\renamed-browser.exe", product, description, original, internalName);
        Assert.False(WindowsMyOrbitSystemBrowserLauncher.IsExternalBrowser(renamed, @"C:\Navigator\OrbitNavigator.App.exe"));
    }

    [Fact]
    public void RealNavigatorVersionResourceSurvivesRenamingAndIsRejected()
    {
        var directory = Path.Combine(Path.GetTempPath(), "orbit-oauth-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var copied = Path.Combine(directory, "renamed-browser.exe");
        try
        {
            // Use the real product's PE version resources, without executing it.
            File.Copy(typeof(WindowsMyOrbitSystemBrowserLauncher).Assembly.Location, copied);
            var inspected = WindowsMyOrbitSystemBrowserLauncher.InspectExecutable(copied);
            Assert.NotNull(inspected);
            Assert.Equal("Orbit Navigator", inspected.ProductName);
            Assert.False(WindowsMyOrbitSystemBrowserLauncher.IsExternalBrowser(inspected, null));
        }
        finally
        {
            File.Delete(copied);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void CurrentProcessIsRejectedEvenIfMetadataClaimsAnotherProduct()
    {
        Assert.False(WindowsMyOrbitSystemBrowserLauncher.IsExternalBrowser(External(),
            External().ExecutablePath.ToUpperInvariant()));
    }

    [Theory]
    [InlineData("browser.exe")]
    [InlineData("https://browser.invalid/app.exe")]
    [InlineData("\"C:\\External Browser\\browser.exe\"")]
    [InlineData("C:\\External Browser\\browser.exe --argument")]
    [InlineData("C:\\Windows\\System32\\rundll32.exe")]
    [InlineData("C:\\Windows\\explorer.exe")]
    [InlineData("C:\\Windows\\System32\\cmd.exe")]
    public void NonExecutablePathsAndShellBrokersAreNotExternalBrowsers(string path)
    {
        Assert.False(WindowsMyOrbitSystemBrowserLauncher.IsExternalBrowser(External() with { ExecutablePath = path }, null));
    }

    [Fact]
    public void MissingExecutableAndUnidentifiableExecutableFailClosed()
    {
        Assert.Null(WindowsMyOrbitSystemBrowserLauncher.InspectExecutable(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.exe")));
        Assert.False(WindowsMyOrbitSystemBrowserLauncher.IsExternalBrowser(
            new(@"C:\Applications\unknown.exe", null, null, null, null), null));
    }

    [Fact]
    public async Task ResolutionOrLaunchFailuresProduceOnlySafeUnavailableState()
    {
        var launcher = new WindowsMyOrbitSystemBrowserLauncher(Authority,
            () => throw new Win32Exception("untrusted path and provider URI"), _ => throw new InvalidOperationException());
        var resolution = await launcher.LaunchAsync(AuthorizationUri());
        Assert.Equal(ControllerErrorCode.Unavailable, resolution.Error?.Code);
        Assert.Equal("account.link.external-browser-unavailable", resolution.Error?.MessageKey);

        launcher = new WindowsMyOrbitSystemBrowserLauncher(Authority, External,
            _ => throw new Win32Exception("untrusted path and provider URI"));
        var launch = await launcher.LaunchAsync(AuthorizationUri());
        Assert.Equal(resolution.Error?.Code, launch.Error?.Code);
        Assert.Equal(resolution.Error?.MessageKey, launch.Error?.MessageKey);
        Assert.Empty(launch.Error!.FormattingArguments);
    }

    [Fact]
    public async Task NullStartResultDoesNotClaimBrowserLaunch()
    {
        var launcher = new WindowsMyOrbitSystemBrowserLauncher(Authority, External, _ => null);
        Assert.Equal(ControllerErrorCode.Unavailable, (await launcher.LaunchAsync(AuthorizationUri())).Error?.Code);
    }

    [Fact]
    public async Task CancellationPreventsResolutionAndLaunch()
    {
        var calls = 0;
        var launcher = new WindowsMyOrbitSystemBrowserLauncher(Authority,
            () => { calls++; return External(); }, _ => { calls++; return new Process(); });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await launcher.LaunchAsync(AuthorizationUri(), cancellation.Token));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task CancellationDuringAssociationResolutionStillPreventsLaunch()
    {
        var launches = 0;
        using var cancellation = new CancellationTokenSource();
        var launcher = new WindowsMyOrbitSystemBrowserLauncher(Authority,
            () => { cancellation.Cancel(); return External(); },
            _ => { launches++; return new Process(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await launcher.LaunchAsync(AuthorizationUri(), cancellation.Token));
        Assert.Equal(0, launches);
    }

    private static WindowsMyOrbitSystemBrowserLauncher.ExternalBrowserExecutable External() => new(
        @"C:\External Browser\browser.exe", "External Browser", "External Browser", "browser.exe", "browser");

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
