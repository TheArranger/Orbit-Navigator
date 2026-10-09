using System.Net;
using System.Globalization;
using OrbitNavigator.App.Updates;
using OrbitNavigator.Contracts.Common;
using Xunit;
using Xunit.Abstractions;

namespace OrbitNavigator.Updates.Tests;

// Explicitly opt-in: exercises the public feed/package, using only a fresh test
// state directory. It never starts an installer or touches a browser profile.
public sealed class PublishedPrimaryClientIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task PublicFeedRetainsPendingUpdateAcrossConditionalChecksAndRestart()
    {
        var expectedText = Environment.GetEnvironmentVariable("ORBIT_PRIMARY_CLIENT_SMOKE_VERSION");
        if (string.IsNullOrWhiteSpace(expectedText)) return;
        var expectedVersion = Version.Parse(expectedText);
        var installedText = Environment.GetEnvironmentVariable("ORBIT_PRIMARY_CLIENT_SMOKE_INSTALLED_VERSION");
        var installedVersion = string.IsNullOrWhiteSpace(installedText)
            ? new Version(0, 1, 26) : Version.Parse(installedText);
        var highWaterText = Environment.GetEnvironmentVariable("ORBIT_PRIMARY_CLIENT_SMOKE_HIGH_WATER");
        var highWater = string.IsNullOrWhiteSpace(highWaterText)
            ? 0 : long.Parse(highWaterText, NumberStyles.None, CultureInfo.InvariantCulture);
        Assert.True(highWater >= 0, "Installed release high-water mark must be nonnegative.");
        var root = Path.Combine(Path.GetTempPath(), "OrbitUpdateClientSmoke", Guid.NewGuid().ToString("N"));
        var statePath = Path.Combine(root, "client.json");
        var stagingPath = Path.Combine(root, "staging");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var token = deadline.Token;
        var launcher = new ForbiddenLauncher();
        var responses = new List<HttpStatusCode>();
        try
        {
            await new FileUpdateClientStateStore(statePath).SaveAsync(
                UpdateClientState.CreateNew().WithAcceptedReleaseSequence(UpdateReleaseChannel.Primary, highWater), token);
            await using (var client = Create(installedVersion))
            {
                await client.InitializeAsync(token);
                AssertSuccess(await client.CheckAsync(token), PrimaryUpdateLifecycle.Available);
                Assert.Equal(expectedVersion, client.Snapshot.AvailableManifest?.Package.Version);
                AssertSuccess(await client.CheckAsync(token), PrimaryUpdateLifecycle.Available);
                Assert.Equal(HttpStatusCode.NotModified, responses[^1]);
                AssertSuccess(await client.DownloadAsync(token), PrimaryUpdateLifecycle.ReadyToInstall);
                var stagedPath = client.Snapshot.StagedPackage!.AbsolutePath;
                AssertSuccess(await client.CheckAsync(token), PrimaryUpdateLifecycle.ReadyToInstall);
                Assert.Equal(stagedPath, client.Snapshot.StagedPackage?.AbsolutePath);
                Assert.Equal(HttpStatusCode.NotModified, responses[^1]);
                Assert.False((await client.ApproveAndLaunchAsync(false, token)).IsSuccess);
                output.WriteLine("Verified public 200 -> 304 Available -> download -> 304 ReadyToInstall; confirmation still mandatory.");
            }

            await using (var restarted = Create(installedVersion))
            {
                await restarted.InitializeAsync(token);
                AssertSuccess(await restarted.CheckAsync(token), PrimaryUpdateLifecycle.ReadyToInstall);
                Assert.Equal(HttpStatusCode.OK, responses[^1]);
                AssertSuccess(await restarted.CheckAsync(token), PrimaryUpdateLifecycle.ReadyToInstall);
                Assert.Equal(HttpStatusCode.NotModified, responses[^1]);
                output.WriteLine("Same isolated state after restart recovered and reverified the staged package, then retained it on 304.");
            }

            // The older client recorded the downloaded sequence but did not
            // record a manifest binding. Simulate its upgrade to this release.
            var stateStore = new FileUpdateClientStateStore(statePath);
            var legacyState = await stateStore.LoadOrCreateAsync(token);
            await stateStore.SaveAsync(legacyState with { PrimaryAcceptedManifestSha256 = null }, token);

            // WPF supplies the four-component assembly version, while the feed
            // uses the centralized three-component product version.
            await using (var installed = Create(new Version(
                expectedVersion.Major, expectedVersion.Minor, expectedVersion.Build, 0)))
            {
                await installed.InitializeAsync(token);
                AssertSuccess(await installed.CheckAsync(token), PrimaryUpdateLifecycle.UpToDate);
                AssertSuccess(await installed.CheckAsync(token), PrimaryUpdateLifecycle.UpToDate);
                Assert.Null(installed.Snapshot.AvailableManifest);
                Assert.Null(installed.Snapshot.StagedPackage);
                output.WriteLine("Only the matching installed version reports UpToDate; no installer was launched.");
            }
            Assert.Equal(0, launcher.Calls);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            Assert.False(Directory.Exists(root));
        }

        PrimaryUpdateClient Create(Version installedVersion) => new(
            new HttpClient(new ObserveFeedHandler(responses)) { Timeout = TimeSpan.FromMinutes(4) },
            new FileUpdateClientStateStore(statePath),
            PrimaryUpdateTrust.ManifestKey,
            new WindowsAuthenticodeTrustInspector(),
            launcher,
            stagingPath,
            installedVersion);
    }

    private static void AssertSuccess(
        ControllerResult<PrimaryUpdateSnapshot> result,
        PrimaryUpdateLifecycle expected)
    {
        Assert.True(result.IsSuccess, result.Error?.MessageKey);
        Assert.Equal(expected, result.Value?.Lifecycle);
    }

    private sealed class ObserveFeedHandler(List<HttpStatusCode> responses)
        : DelegatingHandler(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (request.RequestUri == PrimaryUpdateClient.ManifestUri) responses.Add(response.StatusCode);
            return response;
        }
    }

    private sealed class ForbiddenLauncher : IVisibleUpdateInstallerLauncher
    {
        public int Calls { get; private set; }

        public ValueTask<ControllerResult> LaunchVisibleAsync(
            StagedUpdatePackage package, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("The public-feed smoke must never launch an installer.");
        }
    }
}
