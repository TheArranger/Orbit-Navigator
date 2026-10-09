using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using OrbitNavigator.App.Updates;
using OrbitNavigator.Contracts.Common;
using Xunit;
using Xunit.Abstractions;

namespace OrbitNavigator.Updates.Tests;

// Opt-in delivery gate for sealed local release files. The handler has no network
// transport, and the only installer launcher throws if accidentally invoked.
public sealed class StagedPrimaryClientIntegrationTests(ITestOutputHelper output)
{
    private const string ManifestVariable = "ORBIT_PRIMARY_MANIFEST_UNDER_TEST";
    private const string PackageVariable = "ORBIT_PRIMARY_PACKAGE_UNDER_TEST";
    private static readonly Version CandidateVersion = new(0, 2, 3);
    private static readonly Uri PackageUri = new(
        "https://orbit-nav-updater.snap-it.cc/primary/OrbitNavigator-0.2.3.exe");

    [StagedPrimaryFact]
    public async Task SealedCandidateOffersDownloadsRetainsAndRequiresConfirmationWithoutNetworkOrLaunch()
    {
        var manifestPath = Environment.GetEnvironmentVariable(ManifestVariable);
        var packagePath = Environment.GetEnvironmentVariable(PackageVariable);
        Assert.True(Path.IsPathFullyQualified(manifestPath ?? string.Empty) && File.Exists(manifestPath),
            "Set both staged release variables to existing absolute file paths.");
        Assert.True(Path.IsPathFullyQualified(packagePath ?? string.Empty) && File.Exists(packagePath),
            "Set both staged release variables to existing absolute file paths.");
        // Keep both inputs read-locked for the complete run; neither can be
        // rewritten while a verified envelope and its installer are under test.
        await using var sealedManifest = File.Open(manifestPath!, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var sealedPackage = File.Open(packagePath!, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.InRange(sealedManifest.Length, 1, UpdateFeedTrustPolicy.DefaultMaximumManifestBytes);
        Assert.InRange(sealedPackage.Length, 1, UpdateFeedTrustPolicy.DefaultMaximumPackageBytes);
        var manifestBytes = new byte[checked((int)sealedManifest.Length)];
        await sealedManifest.ReadExactlyAsync(manifestBytes);
        var manifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes));
        var root = Path.Combine(Path.GetTempPath(), "OrbitStagedPrimaryClientSmoke", Guid.NewGuid().ToString("N"));
        var statePath = Path.Combine(root, "client.json");
        var stagingPath = Path.Combine(root, "staging");
        var launcher = new ForbiddenLauncher();
        var responses = new List<HttpStatusCode>();
        var packageResponses = new List<HttpStatusCode>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var token = deadline.Token;
        try
        {
            var store = new FileUpdateClientStateStore(statePath);
            await store.SaveAsync(UpdateClientState.CreateNew().WithAcceptedReleaseSequence(
                UpdateReleaseChannel.Primary, 6), token);
            string stagedPath;
            long acceptedSequence;
            await using (var client = Create(new Version(0, 2, 2)))
            {
                await client.InitializeAsync(token);
                AssertSuccess(await client.CheckAsync(token), PrimaryUpdateLifecycle.Available);
                Assert.Equal(HttpStatusCode.OK, responses[^1]);
                var offer = Assert.IsType<VerifiedUpdateManifest>(client.Snapshot.AvailableManifest);
                Assert.Equal(CandidateVersion, offer.Package.Version);
                Assert.Equal(PackageUri, offer.Package.DownloadUri);
                Assert.True(offer.ReleaseSequence > 6);
                Assert.Equal(sealedPackage.Length, offer.Package.SizeBytes);
                Assert.Empty(packageResponses);
                acceptedSequence = offer.ReleaseSequence;
                AssertSuccess(await client.CheckAsync(token), PrimaryUpdateLifecycle.Available);
                Assert.Equal(HttpStatusCode.NotModified, responses[^1]);
                AssertSuccess(await client.DownloadAsync(token), PrimaryUpdateLifecycle.ReadyToInstall);
                Assert.Equal(UpdatePublisherTrust.Unsigned, client.Snapshot.PublisherTrust);
                stagedPath = Assert.IsType<StagedUpdatePackage>(client.Snapshot.StagedPackage).AbsolutePath;
                Assert.StartsWith(stagingPath + Path.DirectorySeparatorChar, stagedPath, StringComparison.OrdinalIgnoreCase);
                Assert.NotEqual(Path.GetFullPath(packagePath!), stagedPath);
                Assert.Equal(sealedPackage.Length, new FileInfo(stagedPath).Length);
                Assert.Single(packageResponses);
                Assert.Equal(HttpStatusCode.OK, packageResponses[0]);
                AssertSuccess(await client.CheckAsync(token), PrimaryUpdateLifecycle.ReadyToInstall);
                Assert.Equal(HttpStatusCode.NotModified, responses[^1]);
                Assert.Equal(stagedPath, client.Snapshot.StagedPackage?.AbsolutePath);
                var denied = await client.ApproveAndLaunchAsync(false, token);
                Assert.False(denied.IsSuccess);
                Assert.Equal(ControllerErrorCode.PolicyDenied, denied.Error?.Code);
                Assert.Equal(0, launcher.Calls);
                var state = await store.LoadOrCreateAsync(token);
                Assert.Equal(acceptedSequence, state.PrimaryAcceptedReleaseSequence);
                Assert.Equal(manifestHash, state.PrimaryAcceptedManifestSha256);
            }

            await using (var restarted = Create(new Version(0, 2, 2)))
            {
                await restarted.InitializeAsync(token);
                AssertSuccess(await restarted.CheckAsync(token), PrimaryUpdateLifecycle.ReadyToInstall);
                Assert.Equal(HttpStatusCode.OK, responses[^1]);
                Assert.Equal(stagedPath, restarted.Snapshot.StagedPackage?.AbsolutePath);
                AssertSuccess(await restarted.CheckAsync(token), PrimaryUpdateLifecycle.ReadyToInstall);
                Assert.Equal(HttpStatusCode.NotModified, responses[^1]);
                Assert.Equal(UpdatePublisherTrust.Unsigned, restarted.Snapshot.PublisherTrust);
                Assert.Equal(ControllerErrorCode.PolicyDenied,
                    (await restarted.ApproveAndLaunchAsync(false, token)).Error?.Code);
            }

            // Simulate only the running assembly version changing after install.
            // Nothing installs, executes, or alters the sealed input package.
            await using (var installed = Create(new Version(0, 2, 3, 0)))
            {
                await installed.InitializeAsync(token);
                AssertSuccess(await installed.CheckAsync(token), PrimaryUpdateLifecycle.UpToDate);
                Assert.Equal(HttpStatusCode.OK, responses[^1]);
                AssertSuccess(await installed.CheckAsync(token), PrimaryUpdateLifecycle.UpToDate);
                Assert.Equal(HttpStatusCode.NotModified, responses[^1]);
                Assert.Null(installed.Snapshot.AvailableManifest);
                Assert.Null(installed.Snapshot.StagedPackage);
            }
            Assert.Single(packageResponses);
            Assert.Equal(0, launcher.Calls);
            output.WriteLine($"Verified sealed 0.2.3 sequence {acceptedSequence} from installed 0.2.2/high-water 6: " +
                "200/304 offer, integrity-checked local download, restart recovery, mandatory confirmation, installed UpToDate. " +
                "Real pinned-key verification and Authenticode inspection; zero network and installer launches.");
        }
        finally
        {
            // This freshly generated GUID root contains only test-owned update
            // state and the copied package, never a normal browser profile.
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            Assert.False(Directory.Exists(root));
        }

        PrimaryUpdateClient Create(Version installedVersion) => new(
            new HttpClient(new SealedFeedHandler(manifestBytes, packagePath!, responses, packageResponses))
                { Timeout = TimeSpan.FromMinutes(4) },
            new FileUpdateClientStateStore(statePath),
            PrimaryUpdateTrust.ManifestKey,
            new WindowsAuthenticodeTrustInspector(),
            launcher,
            stagingPath,
            installedVersion);
    }

    private static void AssertSuccess(ControllerResult<PrimaryUpdateSnapshot> result, PrimaryUpdateLifecycle expected)
    {
        Assert.True(result.IsSuccess, result.Error?.MessageKey);
        Assert.Equal(expected, result.Value?.Lifecycle);
    }

    private sealed class SealedFeedHandler(byte[] manifest, string packagePath,
        List<HttpStatusCode> manifestResponses, List<HttpStatusCode> packageResponses) : HttpMessageHandler
    {
        private readonly EntityTagHeaderValue _entityTag = new('"' + Convert.ToHexString(SHA256.HashData(manifest)) + '"');

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.Null(request.Headers.Range); // The actual client uses full, verified downloads.
            if (request.RequestUri?.AbsoluteUri == PrimaryUpdateClient.ManifestUri.AbsoluteUri)
            {
                var notModified = request.Headers.IfNoneMatch.Any(tag => tag.Equals(_entityTag));
                var response = new HttpResponseMessage(notModified ? HttpStatusCode.NotModified : HttpStatusCode.OK);
                response.Headers.ETag = _entityTag;
                if (!notModified)
                {
                    response.Content = new ByteArrayContent(manifest);
                    response.Content.Headers.ContentType = new("application/json");
                }
                manifestResponses.Add(response.StatusCode);
                return Task.FromResult(response);
            }
            Assert.Equal(PackageUri.AbsoluteUri, request.RequestUri?.AbsoluteUri);
            Assert.Empty(request.Headers.IfNoneMatch);
            var content = new StreamContent(File.Open(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read));
            content.Headers.ContentLength = new FileInfo(packagePath).Length;
            content.Headers.ContentType = new("application/vnd.microsoft.portable-executable");
            packageResponses.Add(HttpStatusCode.OK);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class ForbiddenLauncher : IVisibleUpdateInstallerLauncher
    {
        public int Calls { get; private set; }
        public ValueTask<ControllerResult> LaunchVisibleAsync(StagedUpdatePackage package, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("The staged release smoke must never launch an installer.");
        }
    }

    private sealed class StagedPrimaryFactAttribute : FactAttribute
    {
        public StagedPrimaryFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ManifestVariable)) &&
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PackageVariable)))
                Skip = "Opt-in sealed release delivery test. Set ORBIT_PRIMARY_MANIFEST_UNDER_TEST and ORBIT_PRIMARY_PACKAGE_UNDER_TEST.";
        }
    }
}
