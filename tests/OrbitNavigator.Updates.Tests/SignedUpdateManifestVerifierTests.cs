using System.Security.Cryptography;
using System.Text.Json;
using OrbitNavigator.Contracts.Common;
using Xunit;

namespace OrbitNavigator.Updates.Tests;

public sealed class SignedUpdateManifestVerifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void ValidSignedManifestIsAccepted()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verifier = CreateVerifier(key);

        var result = verifier.Verify(SerializeSigned(key, CreateDocument()));

        Assert.True(result.IsSuccess);
        Assert.Equal(new Version(1, 1, 0), result.Value?.Package.Version);
        Assert.Equal(41, result.Value?.ReleaseSequence);
    }

    [Fact]
    public void SignedFieldMutationIsRejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verifier = CreateVerifier(key);
        var signed = Sign(key, CreateDocument());
        var tampered = signed with { Version = "9.9.9" };

        var result = verifier.Verify(JsonSerializer.SerializeToUtf8Bytes(tampered, JsonOptions));

        Assert.False(result.IsSuccess);
        Assert.Equal(ControllerErrorCode.IntegrityFailure, result.Error?.Code);
    }

    [Fact]
    public void ProperlySignedForeignPackageOriginIsRejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verifier = CreateVerifier(key);
        var foreign = CreateDocument() with
        {
            PackageUri = "https://downloads.example.test.evil.invalid/OrbitNavigator-1.1.0.exe",
        };

        var result = verifier.Verify(SerializeSigned(key, foreign));

        Assert.False(result.IsSuccess);
        Assert.Equal(ControllerErrorCode.PolicyDenied, result.Error?.Code);
    }

    [Fact]
    public void ProperlySignedCrossChannelPackagePathIsRejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verifier = CreateVerifier(key);
        var crossed = CreateDocument() with
        {
            PackageUri = "https://downloads.example.test/beta/OrbitNavigator-1.1.0-beta.1.exe",
        };

        var result = verifier.Verify(SerializeSigned(key, crossed));

        Assert.False(result.IsSuccess);
        Assert.Equal(ControllerErrorCode.PolicyDenied, result.Error?.Code);
    }

    [Fact]
    public void ReplayedReleaseSequenceIsRejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verifier = CreateVerifier(key);
        var replayed = CreateDocument() with { ReleaseSequence = 40 };

        var result = verifier.Verify(SerializeSigned(key, replayed));

        Assert.False(result.IsSuccess);
        Assert.Equal(ControllerErrorCode.StaleClient, result.Error?.Code);
    }

    [Fact]
    public void DowngradeIsRejectedEvenWhenProperlySigned()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verifier = CreateVerifier(key);
        var downgrade = CreateDocument() with { Version = "0.9.9" };

        var result = verifier.Verify(SerializeSigned(key, downgrade));

        Assert.False(result.IsSuccess);
        Assert.Equal(ControllerErrorCode.PolicyDenied, result.Error?.Code);
    }

    [Fact]
    public void ExpiredManifestIsRejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verifier = CreateVerifier(key);
        var expired = CreateDocument() with
        {
            PublishedAtUtc = Now.AddDays(-2),
            ExpiresAtUtc = Now.AddMinutes(-1),
        };

        var result = verifier.Verify(SerializeSigned(key, expired));

        Assert.False(result.IsSuccess);
        Assert.Equal(ControllerErrorCode.Expired, result.Error?.Code);
    }

    [Fact]
    public void RemoteHttpFeedIsNeverAllowed()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        Assert.Throws<ArgumentException>(() => CreatePolicy(
            key,
            new Uri("http://updates.example.test/primary/manifest.json"),
            new Uri("http://updates.example.test/"),
            allowLoopbackHttpForTesting: true));
    }

    [Fact]
    public void HttpIsAllowedOnlyForExplicitLoopbackTestFeed()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var policy = CreatePolicy(
            key,
            new Uri("http://127.0.0.1:8789/primary/manifest.json"),
            new Uri("http://127.0.0.1:8789/"),
            allowLoopbackHttpForTesting: true);
        var verifier = new SignedUpdateManifestVerifier(policy, new FixedTimeProvider(Now));
        var local = CreateDocument() with
        {
            PackageUri = "http://127.0.0.1:8789/primary/OrbitNavigator-1.1.0.exe",
        };

        var result = verifier.Verify(SerializeSigned(key, local));

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void BetaManifestCannotCrossIntoPrimaryPolicy()
    {
        using var primaryKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var betaKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var primaryVerifier = CreateVerifier(primaryKey);
        var betaDocument = CreateDocument() with
        {
            Channel = "beta",
            PackageUri = "https://downloads.example.test/beta/OrbitNavigator-1.1.0-beta.1.exe",
            KeyId = "beta-2026-a",
        };

        var result = primaryVerifier.Verify(SerializeSigned(betaKey, betaDocument));

        Assert.False(result.IsSuccess);
        Assert.Equal(ControllerErrorCode.IntegrityFailure, result.Error?.Code);
    }

    [Fact]
    public void BetaManifestIsAcceptedOnlyByItsOwnChannelAndKeyRing()
    {
        using var betaKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var betaPolicy = CreatePolicy(
            betaKey,
            new Uri("https://updates.example.test/beta/manifest.json"),
            new Uri("https://downloads.example.test/"),
            channel: "beta",
            keyId: "beta-2026-a");
        var betaVerifier = new SignedUpdateManifestVerifier(
            betaPolicy,
            new FixedTimeProvider(Now));
        var betaDocument = CreateDocument() with
        {
            Channel = "beta",
            PackageUri = "https://downloads.example.test/beta/OrbitNavigator-1.1.0-beta.1.exe",
            KeyId = "beta-2026-a",
        };

        var result = betaVerifier.Verify(SerializeSigned(betaKey, betaDocument));

        Assert.True(result.IsSuccess);
        Assert.Equal("beta", result.Value?.Channel);
    }

    [Fact]
    public void ChannelTrustCatalogRejectsSharedSigningKeyMaterial()
    {
        using var sharedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var primary = CreatePolicy(
            sharedKey,
            new Uri("https://updates.example.test/primary/manifest.json"),
            new Uri("https://downloads.example.test/"));
        var beta = CreatePolicy(
            sharedKey,
            new Uri("https://updates.example.test/beta/manifest.json"),
            new Uri("https://downloads.example.test/"),
            channel: "beta",
            keyId: "beta-2026-a");

        Assert.Throws<ArgumentException>(() => new UpdateChannelTrustCatalog(primary, beta));
    }

    [Fact]
    public void ChannelTrustCatalogKeepsPoliciesExclusive()
    {
        using var primaryKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var betaKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var primary = CreatePolicy(
            primaryKey,
            new Uri("https://updates.example.test/primary/manifest.json"),
            new Uri("https://downloads.example.test/"));
        var beta = CreatePolicy(
            betaKey,
            new Uri("https://updates.example.test/beta/manifest.json"),
            new Uri("https://downloads.example.test/"),
            channel: "beta",
            keyId: "beta-2026-a");
        var catalog = new UpdateChannelTrustCatalog(primary, beta);

        Assert.Same(primary, catalog.Get(UpdateReleaseChannel.Primary));
        Assert.Same(beta, catalog.Get(UpdateReleaseChannel.Beta));
    }

    [Fact]
    public void InvalidRolloutScheduleIsRejectedEvenWhenSigned()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verifier = CreateVerifier(key);
        var invalid = CreateDocument() with
        {
            InitialRolloutBasisPoints = 7_500,
            FinalRolloutBasisPoints = 5_000,
        };

        var result = verifier.Verify(SerializeSigned(key, invalid));

        Assert.False(result.IsSuccess);
        Assert.Equal(ControllerErrorCode.IntegrityFailure, result.Error?.Code);
    }

    private static SignedUpdateManifestVerifier CreateVerifier(ECDsa key) =>
        new(CreatePolicy(
            key,
            new Uri("https://updates.example.test/primary/manifest.json"),
            new Uri("https://downloads.example.test/")),
            new FixedTimeProvider(Now));

    private static UpdateFeedTrustPolicy CreatePolicy(
        ECDsa key,
        Uri manifestUri,
        Uri packageOrigin,
        bool allowLoopbackHttpForTesting = false,
        string channel = "primary",
        string keyId = "release-2026-a") =>
        new(
            manifestUri,
            packageOrigin,
            channel,
            new Version(1, 0, 0),
            40,
            [new UpdateManifestPublicKey(
                keyId,
                Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()))],
            allowLoopbackHttpForTesting);

    private static SignedUpdateManifestDocument CreateDocument() =>
        new(
            UpdateFeedTrustPolicy.CurrentSchemaVersion,
            "primary",
            41,
            "1.1.0",
            "https://downloads.example.test/primary/OrbitNavigator-1.1.0.exe",
            new string('A', 64),
            250_000_000,
            true,
            Now.AddMinutes(-5),
            Now.AddDays(7),
            Now.AddMinutes(-5),
            Now.AddDays(2),
            1_000,
            10_000,
            "release-2026-a",
            string.Empty);

    private static byte[] SerializeSigned(
        ECDsa key,
        SignedUpdateManifestDocument document) =>
        JsonSerializer.SerializeToUtf8Bytes(Sign(key, document), JsonOptions);

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

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
