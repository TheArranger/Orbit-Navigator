using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Updates;
using Xunit;

namespace OrbitNavigator.Updates.Tests;

public sealed class PrimaryUpdateCacheTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task UnchangedManifestKeepsAvailableOffer()
    {
        using var scenario = new Scenario();
        await using var client = scenario.CreateClient();
        await client.InitializeAsync();

        Assert.Equal(PrimaryUpdateLifecycle.Available, (await client.CheckAsync()).Value?.Lifecycle);
        Assert.Equal(PrimaryUpdateLifecycle.Available, (await client.CheckAsync()).Value?.Lifecycle);
        Assert.Equal(new string?[] { null, "\"primary-test\"" }, scenario.ConditionalTags);
        Assert.NotNull(client.Snapshot.AvailableManifest);
        Assert.Null((await scenario.StateStore.LoadOrCreateAsync()).PrimaryEntityTag);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecheckingDownloadedPackageKeepsReadyFor304And200(bool conditional)
    {
        using var scenario = new Scenario { RespondConditionally = conditional };
        await using var client = scenario.CreateClient();
        await client.CheckAsync();
        Assert.True((await client.DownloadAsync()).IsSuccess);

        var result = await client.CheckAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(PrimaryUpdateLifecycle.ReadyToInstall, result.Value?.Lifecycle);
        Assert.NotNull(result.Value?.StagedPackage);
        Assert.Equal(1, scenario.PackageRequests);
        Assert.False((await client.ApproveAndLaunchAsync(false)).IsSuccess);
        Assert.Equal(0, scenario.Launcher.Calls);
        Assert.True((await client.ApproveAndLaunchAsync(true)).IsSuccess);
        Assert.Equal(1, scenario.Launcher.Calls);
    }

    [Fact]
    public async Task RestartIgnoresPersistedEntityTagWithoutVerifiedBody()
    {
        using var scenario = new Scenario();
        await scenario.StateStore.SaveAsync(UpdateClientState.CreateNew().WithEntityTag(
            UpdateReleaseChannel.Primary, "\"primary-test\""));
        await using var client = scenario.CreateClient();
        await client.InitializeAsync();

        var result = await client.CheckAsync();

        Assert.Equal(PrimaryUpdateLifecycle.Available, result.Value?.Lifecycle);
        Assert.Null(Assert.Single(scenario.ConditionalTags));
    }

    [Fact]
    public async Task RestartRecoversExactAcceptedStagedPackageFromFullyVerified200()
    {
        using var scenario = new Scenario();
        await using (var first = scenario.CreateClient())
        {
            await first.CheckAsync();
            await first.DownloadAsync();
        }
        var accepted = await scenario.StateStore.LoadOrCreateAsync();
        Assert.Equal(1, accepted.PrimaryAcceptedReleaseSequence);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(scenario.Manifest)), accepted.PrimaryAcceptedManifestSha256);
        await using var restarted = scenario.CreateClient();
        await restarted.InitializeAsync();

        var result = await restarted.CheckAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(PrimaryUpdateLifecycle.ReadyToInstall, result.Value?.Lifecycle);
        Assert.Null(scenario.ConditionalTags.Last());
        Assert.Equal(1, scenario.PackageRequests);
    }

    [Theory]
    [InlineData("0.1.14")]
    [InlineData("0.1.14.0")]
    public async Task FullyVerifiedInstalledVersionIsUpToDateOn200And304(string version)
    {
        using var scenario = new Scenario();
        await using var client = scenario.CreateClient(new Version(version));

        Assert.Equal(PrimaryUpdateLifecycle.UpToDate, (await client.CheckAsync()).Value?.Lifecycle);
        Assert.Equal(PrimaryUpdateLifecycle.UpToDate, (await client.CheckAsync()).Value?.Lifecycle);
        Assert.Null(client.Snapshot.AvailableManifest);
        Assert.Null(client.Snapshot.StagedPackage);
    }

    [Fact]
    public async Task InstalledVersionDoesNotReofferPreviouslyAcceptedStagedInstaller()
    {
        using var scenario = new Scenario();
        await using (var first = scenario.CreateClient())
        {
            await first.CheckAsync();
            await first.DownloadAsync();
        }
        await using var installed = scenario.CreateClient(new Version(0, 1, 14, 0));

        Assert.Equal(PrimaryUpdateLifecycle.UpToDate, (await installed.CheckAsync()).Value?.Lifecycle);
        Assert.Null(installed.Snapshot.StagedPackage);
        Assert.False((await installed.ApproveAndLaunchAsync(true)).IsSuccess);
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("expired")]
    [InlineData("channel")]
    [InlineData("origin")]
    [InlineData("metadata")]
    [InlineData("rollout")]
    public async Task InstalledVersionStillRequiresEveryManifestTrustCheck(string mutation)
    {
        using var scenario = new Scenario();
        var document = mutation switch
        {
            "signature" => scenario.Document with { Signature = Convert.ToBase64String(new byte[64]) },
            "expired" => scenario.Document with { PublishedAtUtc = Now.AddDays(-2), ExpiresAtUtc = Now.AddMinutes(-1) },
            "channel" => scenario.Document with { Channel = "beta" },
            "origin" => scenario.Document with { PackageUri = "https://evil.example/primary/OrbitNavigator-0.1.14.exe" },
            "metadata" => scenario.Document with { Sha256 = new string('Z', 64) },
            "rollout" => scenario.Document with { InitialRolloutBasisPoints = 10_001 },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        scenario.SetDocument(document, sign: mutation != "signature");
        await scenario.StateStore.SaveAsync(UpdateClientState.CreateNew().WithAcceptedReleaseSequence(
            UpdateReleaseChannel.Primary, 1));
        await using var client = scenario.CreateClient(new Version(0, 1, 14, 0));

        Assert.False((await client.CheckAsync()).IsSuccess);
        Assert.Equal(PrimaryUpdateLifecycle.Failed, client.Snapshot.Lifecycle);
        Assert.Null(client.Snapshot.AvailableManifest);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("hash")]
    [InlineData("expiry")]
    [InlineData("whitespace")]
    public async Task AcceptedSequenceCannotAuthorizeChangedSignedEnvelope(string mutation)
    {
        using var scenario = new Scenario { RespondConditionally = false };
        await using var client = scenario.CreateClient();
        await client.CheckAsync();
        await client.DownloadAsync();
        var document = mutation switch
        {
            "version" => scenario.Document with { Version = "0.1.15", PackageUri = "https://orbit-nav-updater.snap-it.cc/primary/OrbitNavigator-0.1.15.exe" },
            "hash" => scenario.Document with { Sha256 = new string('A', 64) },
            "expiry" => scenario.Document with { ExpiresAtUtc = Now.AddDays(8) },
            _ => scenario.Document,
        };
        if (mutation == "whitespace")
            scenario.Manifest = [.. scenario.Manifest, (byte)'\n'];
        else
            scenario.SetDocument(document);

        var result = await client.CheckAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal("error.update.manifest_replayed", result.Error?.MessageKey);
        Assert.Null(client.Snapshot.StagedPackage);
        Assert.False((await client.DownloadAsync()).IsSuccess);
        Assert.False((await client.ApproveAndLaunchAsync(true)).IsSuccess);
    }

    [Fact]
    public async Task LowerSequenceIsRejectedDespiteNewerVersion()
    {
        using var scenario = new Scenario { RespondConditionally = false };
        scenario.SetDocument(scenario.Document with { ReleaseSequence = 2 });
        await using var client = scenario.CreateClient();
        await client.CheckAsync();
        await client.DownloadAsync();
        scenario.SetDocument(scenario.Document with
        {
            ReleaseSequence = 1,
            Version = "0.1.15",
            PackageUri = "https://orbit-nav-updater.snap-it.cc/primary/OrbitNavigator-0.1.15.exe",
        });

        Assert.Equal("error.update.manifest_replayed", (await client.CheckAsync()).Error?.MessageKey);
        Assert.Equal(2, (await scenario.StateStore.LoadOrCreateAsync()).PrimaryAcceptedReleaseSequence);
    }

    [Fact]
    public async Task LegacyAcceptedSequenceWithoutIdentityFailsClosed()
    {
        using var scenario = new Scenario();
        await scenario.StateStore.SaveAsync(UpdateClientState.CreateNew().WithAcceptedReleaseSequence(
            UpdateReleaseChannel.Primary, 1));
        await using var client = scenario.CreateClient();

        Assert.Equal("error.update.manifest_replayed", (await client.CheckAsync()).Error?.MessageKey);
        Assert.Null(client.Snapshot.AvailableManifest);
    }

    [Fact]
    public async Task LegacyAcceptedSequenceCanOnlyDescribeTheVersionAlreadyInstalled()
    {
        using var scenario = new Scenario();
        await scenario.StateStore.SaveAsync(UpdateClientState.CreateNew().WithAcceptedReleaseSequence(
            UpdateReleaseChannel.Primary, 1));
        await using var installed = scenario.CreateClient(new Version(0, 1, 14, 0));

        Assert.Equal(PrimaryUpdateLifecycle.UpToDate, (await installed.CheckAsync()).Value?.Lifecycle);
        Assert.Equal(PrimaryUpdateLifecycle.UpToDate, (await installed.CheckAsync()).Value?.Lifecycle);
        Assert.Null(installed.Snapshot.AvailableManifest);
        Assert.Null(installed.Snapshot.StagedPackage);
        Assert.Null((await scenario.StateStore.LoadOrCreateAsync()).PrimaryAcceptedManifestSha256);
        Assert.False((await installed.DownloadAsync()).IsSuccess);
        Assert.False((await installed.ApproveAndLaunchAsync(true)).IsSuccess);
    }

    [Fact]
    public async Task InstalledStatusNeverBypassesAnExistingManifestIdentityMismatch()
    {
        using var scenario = new Scenario();
        await using (var first = scenario.CreateClient())
        {
            await first.CheckAsync();
            await first.DownloadAsync();
        }
        scenario.SetDocument(scenario.Document with { ExpiresAtUtc = Now.AddDays(8) });
        await using var installed = scenario.CreateClient(new Version(0, 1, 14, 0));

        Assert.Equal("error.update.manifest_replayed", (await installed.CheckAsync()).Error?.MessageKey);
        Assert.Equal(PrimaryUpdateLifecycle.Failed, installed.Snapshot.Lifecycle);
    }

    [Fact]
    public async Task LegacyInstalledStatusCannotBypassALowerSequence()
    {
        using var scenario = new Scenario();
        await scenario.StateStore.SaveAsync(UpdateClientState.CreateNew().WithAcceptedReleaseSequence(
            UpdateReleaseChannel.Primary, 2));
        await using var installed = scenario.CreateClient(new Version(0, 1, 14, 0));

        Assert.Equal("error.update.manifest_replayed", (await installed.CheckAsync()).Error?.MessageKey);
    }

    [Fact]
    public async Task Cached304RevalidatesExpirationAndNextCheckFetchesAFullBody()
    {
        using var scenario = new Scenario();
        await using var client = scenario.CreateClient();
        await client.CheckAsync();
        scenario.Clock.UtcNow = Now.AddDays(8);

        Assert.Equal("error.update.manifest_expired", (await client.CheckAsync()).Error?.MessageKey);
        Assert.False((await client.DownloadAsync()).IsSuccess);
        scenario.SetDocument(scenario.Document with
        {
            ReleaseSequence = 2,
            PublishedAtUtc = scenario.Clock.UtcNow.AddMinutes(-1),
            ExpiresAtUtc = scenario.Clock.UtcNow.AddDays(7),
            RolloutStartUtc = scenario.Clock.UtcNow.AddMinutes(-1),
            RolloutEndUtc = scenario.Clock.UtcNow.AddMinutes(-1),
        });

        Assert.Equal(PrimaryUpdateLifecycle.Available, (await client.CheckAsync()).Value?.Lifecycle);
        Assert.Null(scenario.ConditionalTags.Last());
    }

    [Fact]
    public async Task ExpirationBetweenCheckAndDownloadPreventsPackageRequest()
    {
        using var scenario = new Scenario();
        await using var client = scenario.CreateClient();
        await client.CheckAsync();
        scenario.Clock.UtcNow = Now.AddDays(8);

        Assert.Equal("error.update.manifest_expired", (await client.DownloadAsync()).Error?.MessageKey);
        Assert.Equal(0, scenario.PackageRequests);
    }

    [Fact]
    public async Task ExpirationBetweenDownloadAndApprovalPreventsLaunch()
    {
        using var scenario = new Scenario();
        await using var client = scenario.CreateClient();
        await client.CheckAsync();
        await client.DownloadAsync();
        scenario.Clock.UtcNow = Now.AddDays(8);

        Assert.Equal("error.update.manifest_expired", (await client.ApproveAndLaunchAsync(true)).Error?.MessageKey);
        Assert.Equal(0, scenario.Launcher.Calls);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public async Task Cacheless304RetriesOnceWithoutClaimingUpToDate(int bodylessResponses, bool succeeds)
    {
        using var scenario = new Scenario { BodylessResponses = bodylessResponses };
        await using var client = scenario.CreateClient();

        var result = await client.CheckAsync();

        Assert.Equal(succeeds, result.IsSuccess);
        Assert.Equal(succeeds ? PrimaryUpdateLifecycle.Available : PrimaryUpdateLifecycle.Failed, client.Snapshot.Lifecycle);
        Assert.Equal(2, scenario.ConditionalTags.Count);
        Assert.All(scenario.ConditionalTags, tag => Assert.Null(tag));
    }

    [Fact]
    public async Task Cached304ReevaluatesRolloutAndPersistsTheRecheckDeadline()
    {
        using var scenario = new Scenario();
        scenario.SetDocument(scenario.Document with
        {
            RolloutStartUtc = Now.AddHours(1),
            RolloutEndUtc = Now.AddHours(1),
        });
        await using var client = scenario.CreateClient();

        var first = await client.CheckAsync();
        Assert.Null(first.Value?.AvailableManifest);
        Assert.Equal(Now.AddHours(1), (await scenario.StateStore.LoadOrCreateAsync()).NextCheckNotBeforeUtc);
        scenario.Clock.UtcNow = Now.AddHours(1);

        Assert.Equal(PrimaryUpdateLifecycle.Available, (await client.CheckAsync()).Value?.Lifecycle);
        Assert.Equal("\"primary-test\"", scenario.ConditionalTags.Last());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrTamperedStagedBytesAreNotReusedAndCanBeRedownloaded(bool missing)
    {
        using var scenario = new Scenario();
        await using var client = scenario.CreateClient();
        await client.CheckAsync();
        await client.DownloadAsync();
        var path = client.Snapshot.StagedPackage!.AbsolutePath;
        if (missing) File.Delete(path);
        else await File.WriteAllBytesAsync(path, [9, 9, 9, 9, 9, 9]);

        Assert.Equal(PrimaryUpdateLifecycle.Available, (await client.CheckAsync()).Value?.Lifecycle);
        Assert.Null(client.Snapshot.StagedPackage);
        Assert.False((await client.ApproveAndLaunchAsync(true)).IsSuccess);
        Assert.Equal(PrimaryUpdateLifecycle.ReadyToInstall, (await client.DownloadAsync()).Value?.Lifecycle);
    }

    [Fact]
    public async Task LaunchRechecksStagedHashEvenWithoutAnotherFeedCheck()
    {
        using var scenario = new Scenario();
        await using var client = scenario.CreateClient();
        await client.CheckAsync();
        await client.DownloadAsync();
        await File.WriteAllBytesAsync(client.Snapshot.StagedPackage!.AbsolutePath, [9, 9, 9, 9, 9, 9]);

        Assert.Equal("error.update.hash_mismatch", (await client.ApproveAndLaunchAsync(true)).Error?.MessageKey);
        Assert.Equal(0, scenario.Launcher.Calls);
    }

    [Theory]
    [InlineData(UpdatePublisherTrust.TrustedPublisher)]
    [InlineData(UpdatePublisherTrust.VerificationUnavailable)]
    [InlineData(UpdatePublisherTrust.InvalidSignature)]
    public async Task Cached304CannotReusePackageAfterPublisherTrustChanges(UpdatePublisherTrust trust)
    {
        using var scenario = new Scenario();
        await using var client = scenario.CreateClient();
        await client.CheckAsync();
        await client.DownloadAsync();
        scenario.Trust.Value = trust;

        Assert.False((await client.CheckAsync()).IsSuccess);
        Assert.Null(client.Snapshot.StagedPackage);
        Assert.False((await client.ApproveAndLaunchAsync(true)).IsSuccess);
        Assert.Equal(0, scenario.Launcher.Calls);
    }

    [Fact]
    public async Task NewSequenceWithSameFilenameDoesNotReuseOldPackageBytes()
    {
        using var scenario = new Scenario { RespondConditionally = false };
        await using var client = scenario.CreateClient();
        await client.CheckAsync();
        await client.DownloadAsync();
        scenario.Package = [9, 8, 7, 6, 5, 4];
        scenario.SetDocument(scenario.Document with
        {
            ReleaseSequence = 2,
            Sha256 = Convert.ToHexString(SHA256.HashData(scenario.Package)),
        });

        Assert.Equal(PrimaryUpdateLifecycle.Available, (await client.CheckAsync()).Value?.Lifecycle);
        Assert.Null(client.Snapshot.StagedPackage);
        Assert.Equal(PrimaryUpdateLifecycle.ReadyToInstall, (await client.DownloadAsync()).Value?.Lifecycle);
        Assert.Equal(2, (await scenario.StateStore.LoadOrCreateAsync()).PrimaryAcceptedReleaseSequence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FeedOutageOrTimeoutKeepsOnlyReverifiedReadyPackage(bool timeout)
    {
        using var scenario = new Scenario();
        await using var client = scenario.CreateClient();
        await client.CheckAsync();
        await client.DownloadAsync();
        scenario.FeedFailure = timeout ? new TaskCanceledException("Simulated HTTP timeout") : new HttpRequestException("Offline");

        Assert.False((await client.CheckAsync()).IsSuccess);
        Assert.Equal(PrimaryUpdateLifecycle.ReadyToInstall, client.Snapshot.Lifecycle);
        Assert.NotNull(client.Snapshot.StagedPackage);
        Assert.False((await client.ApproveAndLaunchAsync(false)).IsSuccess);
        scenario.FeedFailure = null;
        Assert.Equal(PrimaryUpdateLifecycle.ReadyToInstall, (await client.CheckAsync()).Value?.Lifecycle);
        Assert.Equal("\"primary-test\"", scenario.ConditionalTags.Last());
        Assert.Equal(0, (await scenario.StateStore.LoadOrCreateAsync()).ConsecutiveFailures);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("tampered")]
    [InlineData("publisher")]
    public async Task OfflineChecksNeverPreserveUnsafeStagedUpdates(string mutation)
    {
        using var scenario = new Scenario();
        await using var client = scenario.CreateClient();
        await client.CheckAsync();
        await client.DownloadAsync();
        if (mutation == "expired") scenario.Clock.UtcNow = Now.AddDays(8);
        else if (mutation == "publisher") scenario.Trust.Value = UpdatePublisherTrust.VerificationUnavailable;
        else await File.WriteAllBytesAsync(client.Snapshot.StagedPackage!.AbsolutePath, [9, 9, 9, 9, 9, 9]);
        scenario.FeedFailure = new HttpRequestException("Offline");

        Assert.False((await client.CheckAsync()).IsSuccess);
        Assert.Equal(PrimaryUpdateLifecycle.Failed, client.Snapshot.Lifecycle);
        Assert.Null(client.Snapshot.StagedPackage);
        Assert.False((await client.ApproveAndLaunchAsync(true)).IsSuccess);
        Assert.Equal(0, scenario.Launcher.Calls);
    }

    [Fact]
    public async Task TimeoutWithoutAReadyPackageExitsCheckingAndSchedulesBackoff()
    {
        using var scenario = new Scenario { FeedFailure = new TaskCanceledException("Simulated HTTP timeout") };
        await using var client = scenario.CreateClient();

        Assert.False((await client.CheckAsync()).IsSuccess);
        Assert.Equal(PrimaryUpdateLifecycle.Failed, client.Snapshot.Lifecycle);
        var state = await scenario.StateStore.LoadOrCreateAsync();
        Assert.Equal(1, state.ConsecutiveFailures);
        Assert.True(state.NextCheckNotBeforeUtc > Now);
    }

    private sealed class Scenario : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        public byte[] Package { get; set; } = [1, 7, 4, 2, 9, 5];
        public byte[] Manifest { get; set; } = [];
        public SignedUpdateManifestDocument Document { get; private set; }
        public FileUpdateClientStateStore StateStore { get; }
        public MutableClock Clock { get; } = new();
        public MutableTrust Trust { get; } = new();
        public FakeLauncher Launcher { get; } = new();
        public List<string?> ConditionalTags { get; } = [];
        public int PackageRequests { get; set; }
        public bool RespondConditionally { get; set; } = true;
        public int BodylessResponses { get; set; }
        public Exception? FeedFailure { get; set; }

        public Scenario()
        {
            StateStore = new FileUpdateClientStateStore(Path.Combine(_temp.Path, "client.json"));
            Document = new(1, "primary", 1, "0.1.14",
                "https://orbit-nav-updater.snap-it.cc/primary/OrbitNavigator-0.1.14.exe",
                Convert.ToHexString(SHA256.HashData(Package)), Package.Length, true,
                Now.AddMinutes(-1), Now.AddDays(7), Now.AddMinutes(-1), Now.AddMinutes(-1),
                10_000, 10_000, "primary-test", string.Empty);
            SetDocument(Document);
        }

        public void SetDocument(SignedUpdateManifestDocument document, bool sign = true)
        {
            Document = sign ? document with
            {
                Signature = Convert.ToBase64String(_key.SignData(
                    UpdateManifestCanonicalEncoding.Encode(document), HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation)),
            } : document;
            Manifest = JsonSerializer.SerializeToUtf8Bytes(Document, JsonOptions);
        }

        public PrimaryUpdateClient CreateClient(Version? version = null) => new(
            new HttpClient(new FeedHandler(this)), StateStore,
            new UpdateManifestPublicKey("primary-test", Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo())),
            Trust, Launcher, Path.Combine(_temp.Path, "staging"), version ?? new Version(0, 1, 13, 0), Clock);

        public void Dispose()
        {
            _key.Dispose();
            _temp.Dispose();
        }
    }

    private sealed class FeedHandler(Scenario scenario) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri != PrimaryUpdateClient.ManifestUri)
            {
                scenario.PackageRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(scenario.Package) });
            }
            var tag = request.Headers.IfNoneMatch.SingleOrDefault()?.ToString();
            scenario.ConditionalTags.Add(tag);
            if (scenario.FeedFailure is { } failure)
                return Task.FromException<HttpResponseMessage>(failure);
            if (scenario.BodylessResponses > 0)
            {
                scenario.BodylessResponses--;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            }
            if (scenario.RespondConditionally && tag == "\"primary-test\"")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(scenario.Manifest) };
            response.Headers.ETag = new EntityTagHeaderValue("\"primary-test\"");
            return Task.FromResult(response);
        }
    }

    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = Now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class MutableTrust : IUpdatePublisherTrustInspector
    {
        public UpdatePublisherTrust Value { get; set; } = UpdatePublisherTrust.Unsigned;
        public ValueTask<UpdatePublisherTrust> InspectAsync(string absolutePackagePath, CancellationToken cancellationToken = default) => ValueTask.FromResult(Value);
    }

    private sealed class FakeLauncher : IVisibleUpdateInstallerLauncher
    {
        public int Calls { get; private set; }
        public ValueTask<ControllerResult> LaunchVisibleAsync(StagedUpdatePackage package, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(ControllerResult.Success());
        }
    }
}
