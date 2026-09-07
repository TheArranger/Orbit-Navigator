using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Updates;
using Xunit;

namespace OrbitNavigator.Updates.Tests;

public sealed class BetaUpdateClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task UnsignedBetaRequiresOptInAndSeparatePerPackageConfirmation()
    {
        using var temp = new TempDirectory();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] packageBytes = [1, 7, 4, 2, 9, 5];
        var packageHash = Convert.ToHexString(SHA256.HashData(packageBytes));
        var document = Sign(key, new(
            1,
            "beta",
            1,
            "0.1.14",
            "https://orbit-nav-updater.snap-it.cc/beta/OrbitNavigator-0.1.14-beta.1.exe",
            packageHash,
            packageBytes.Length,
            true,
            Now.AddMinutes(-1),
            Now.AddDays(7),
            Now.AddMinutes(-1),
            Now.AddMinutes(-1),
            10_000,
            10_000,
            "beta-test",
            string.Empty));
        var handler = new FeedHandler(
            JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions),
            packageBytes);
        var launcher = new FakeLauncher();
        await using var client = new BetaUpdateClient(
            new HttpClient(handler),
            new FileUpdateClientStateStore(Path.Combine(temp.Path, "client.json")),
            new UpdateManifestPublicKey(
                "beta-test",
                Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())),
            new FixedTrustInspector(UpdatePublisherTrust.Unsigned),
            launcher,
            Path.Combine(temp.Path, "staging"),
            new Version(0, 1, 13),
            new FixedTimeProvider(Now));
        await client.InitializeAsync();

        var disabled = await client.CheckAsync();
        var optedIn = await client.SetBetaOptInAsync(true);
        var checkedResult = await client.CheckAsync();
        var downloaded = await client.DownloadAsync();
        var withoutConfirmation = await client.ApproveAndLaunchAsync(false);
        var withConfirmation = await client.ApproveAndLaunchAsync(true);

        Assert.False(disabled.IsSuccess);
        Assert.True(optedIn.IsSuccess);
        Assert.Equal(BetaUpdateLifecycle.Available, checkedResult.Value?.Lifecycle);
        Assert.Equal(BetaUpdateLifecycle.ReadyToInstall, downloaded.Value?.Lifecycle);
        Assert.Equal(UpdatePublisherTrust.Unsigned, downloaded.Value?.PublisherTrust);
        Assert.False(withoutConfirmation.IsSuccess);
        Assert.Equal(ControllerErrorCode.PolicyDenied, withoutConfirmation.Error?.Code);
        Assert.True(withConfirmation.IsSuccess);
        Assert.Equal(1, launcher.Calls);
        Assert.Equal(1, handler.ManifestCalls);
        Assert.Equal(1, handler.PackageCalls);
    }

    [Fact]
    public async Task AuthenticodeInspectorReportsCurrentUnsignedSetupAsUnsigned()
    {
        var projectRoot = FindProjectRoot();
        var setup = Path.Combine(projectRoot, "dist", "Setup.exe");
        if (!File.Exists(setup)) return;

        var result = await new WindowsAuthenticodeTrustInspector().InspectAsync(setup);

        Assert.Equal(UpdatePublisherTrust.Unsigned, result);
    }

    private static string FindProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OrbitNavigator.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Project root not found.");
    }

    private static SignedUpdateManifestDocument Sign(
        ECDsa key,
        SignedUpdateManifestDocument document)
    {
        var signature = key.SignData(
            UpdateManifestCanonicalEncoding.Encode(document),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return document with { Signature = Convert.ToBase64String(signature) };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed class FeedHandler(byte[] manifest, byte[] package) : HttpMessageHandler
    {
        public int ManifestCalls { get; private set; }
        public int PackageCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri == BetaUpdateClient.ManifestUri)
            {
                ManifestCalls++;
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(manifest),
                };
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"beta-1\"");
                response.Content.Headers.ContentType = new("application/json");
                return Task.FromResult(response);
            }

            PackageCalls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(package),
            });
        }
    }

    private sealed class FixedTrustInspector(UpdatePublisherTrust trust)
        : IUpdatePublisherTrustInspector
    {
        public ValueTask<UpdatePublisherTrust> InspectAsync(
            string absolutePackagePath,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(trust);
    }

    private sealed class FakeLauncher : IVisibleUpdateInstallerLauncher
    {
        public int Calls { get; private set; }

        public ValueTask<ControllerResult> LaunchVisibleAsync(
            StagedUpdatePackage package,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(ControllerResult.Success());
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
