using System.Diagnostics;
using System.Security.Cryptography;
using Xunit;

namespace OrbitNavigator.Updates.Tests;

public sealed class PublishedPrimaryManifestIntegrationTests
{
    [Fact]
    public void StagedManifestVerifiesAgainstThePinnedPrimaryKeyWhenRequested()
    {
        var path = Environment.GetEnvironmentVariable("ORBIT_PRIMARY_MANIFEST_UNDER_TEST");
        if (string.IsNullOrWhiteSpace(path)) return;
        var packagePath = Environment.GetEnvironmentVariable("ORBIT_PRIMARY_PACKAGE_UNDER_TEST");
        Assert.False(string.IsNullOrWhiteSpace(packagePath));

        var policy = new UpdateFeedTrustPolicy(
            PrimaryUpdateClient.ManifestUri,
            PrimaryUpdateClient.PackageOrigin,
            "primary",
            new Version(0, 1, 23),
            0,
            [new UpdateManifestPublicKey(
                "beta-2026-01",
                "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEzZG37N2DsbnjTeudIUh7XU0+eSbCxVtUoBI22iSs+77KAwQtlCeIN8THHz7l+/U7+SwMYRVF2sirkfjt0ExG8A==")]);
        var verified = new SignedUpdateManifestVerifier(policy).Verify(File.ReadAllBytes(path));

        Assert.True(verified.IsSuccess, verified.Error?.MessageKey);
        Assert.Equal("primary", verified.Value?.Channel);
        var manifest = Assert.IsType<VerifiedUpdateManifest>(verified.Value);
        var file = new FileInfo(packagePath!);
        using var stream = file.OpenRead();
        Assert.Equal(Convert.ToHexString(SHA256.HashData(stream)), manifest.Package.Sha256);
        Assert.Equal(file.Length, manifest.Package.SizeBytes);
        Assert.Equal(
            Version.Parse(FileVersionInfo.GetVersionInfo(file.FullName).ProductVersion!),
            manifest.Package.Version);
        Assert.Equal($"/primary/OrbitNavigator-{manifest.Package.Version}.exe",
            manifest.Package.DownloadUri.AbsolutePath);
    }
}
