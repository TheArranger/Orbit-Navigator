using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Updates;
using Xunit;

namespace OrbitNavigator.Updates.Tests;

public sealed class PrimaryUpdateClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task UnsignedPrimaryUsesSignedManifestAndRequiresPerPackageConfirmation()
    {
        using var temp = new TempDirectory();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] packageBytes = [1, 7, 4, 2, 9, 5];
        var packageHash = Convert.ToHexString(SHA256.HashData(packageBytes));
        var document = Sign(key, new(
            1,
            "primary",
            1,
            "0.1.14",
            "https://orbit-nav-updater.snap-it.cc/primary/OrbitNavigator-0.1.14.exe",
            packageHash,
            packageBytes.Length,
            true,
            Now.AddMinutes(-1),
            Now.AddDays(7),
            Now.AddMinutes(-1),
            Now.AddMinutes(-1),
            10_000,
            10_000,
            "primary-test",
            string.Empty));
        var handler = new FeedHandler(
            JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions),
            packageBytes);
        var launcher = new FakeLauncher();
        await using var client = new PrimaryUpdateClient(
            new HttpClient(handler),
            new FileUpdateClientStateStore(Path.Combine(temp.Path, "client.json")),
            new UpdateManifestPublicKey(
                "primary-test",
                Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())),
            new FixedTrustInspector(UpdatePublisherTrust.Unsigned),
            launcher,
            Path.Combine(temp.Path, "staging"),
            new Version(0, 1, 13),
            new FixedTimeProvider(Now));
        await client.InitializeAsync();

        var checkedResult = await client.CheckAsync();
        var downloaded = await client.DownloadAsync();
        var withoutConfirmation = await client.ApproveAndLaunchAsync(false);
        var withConfirmation = await client.ApproveAndLaunchAsync(true);

        Assert.Equal(PrimaryUpdateLifecycle.Available, checkedResult.Value?.Lifecycle);
        Assert.Equal(PrimaryUpdateLifecycle.ReadyToInstall, downloaded.Value?.Lifecycle);
        Assert.Equal(UpdatePublisherTrust.Unsigned, downloaded.Value?.PublisherTrust);
        Assert.False(withoutConfirmation.IsSuccess);
        Assert.Equal(ControllerErrorCode.PolicyDenied, withoutConfirmation.Error?.Code);
        Assert.True(withConfirmation.IsSuccess);
        Assert.Equal(1, launcher.Calls);
        Assert.Equal(1, handler.ManifestCalls);
        Assert.Equal(1, handler.PackageCalls);
    }

    [Fact]
    public async Task LegacyBetaSelectionMigratesToTheSinglePrimaryChannel()
    {
        using var temp = new TempDirectory();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var statePath = Path.Combine(temp.Path, "client.json");
        var stateStore = new FileUpdateClientStateStore(statePath);
        await stateStore.SaveAsync(UpdateClientState.CreateNew().SelectChannel(
            UpdateReleaseChannel.Beta,
            explicitUserOptIn: true));
        await using var client = new PrimaryUpdateClient(
            new HttpClient(new FeedHandler([], [])),
            stateStore,
            new UpdateManifestPublicKey(
                "primary-test",
                Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())),
            new FixedTrustInspector(UpdatePublisherTrust.Unsigned),
            new FakeLauncher(),
            Path.Combine(temp.Path, "staging"),
            new Version(0, 1, 13),
            new FixedTimeProvider(Now));

        await client.InitializeAsync();
        var migrated = await stateStore.LoadOrCreateAsync();

        Assert.Equal(UpdateReleaseChannel.Primary, migrated.Channel);
        Assert.False(migrated.BetaChannelOptIn);
        Assert.NotNull(migrated.NextCheckNotBeforeUtc);
        Assert.Equal(PrimaryUpdateLifecycle.Idle, client.Snapshot.Lifecycle);
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
            if (request.RequestUri == PrimaryUpdateClient.ManifestUri)
            {
                ManifestCalls++;
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(manifest),
                };
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"primary-1\"");
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
