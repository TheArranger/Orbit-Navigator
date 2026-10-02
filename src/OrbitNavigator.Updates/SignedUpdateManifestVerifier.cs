using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Updates;

namespace OrbitNavigator.Updates;

public sealed partial class SignedUpdateManifestVerifier
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly UpdateFeedTrustPolicy _policy;
    private readonly TimeProvider _timeProvider;

    public SignedUpdateManifestVerifier(
        UpdateFeedTrustPolicy policy,
        TimeProvider? timeProvider = null)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ControllerResult<VerifiedUpdateManifest> Verify(ReadOnlySpan<byte> utf8Json) =>
        VerifyCore(utf8Json, allowInstalledVersion: false, acceptedManifestSha256: null);

    // The primary client's idempotent checks may revisit an installed version or the
    // exact signed envelope accepted when downloading. Neither exception relaxes
    // signature, channel, origin, package, expiry, or rollout validation.
    internal ControllerResult<VerifiedUpdateManifest> VerifyCurrentFeed(
        ReadOnlySpan<byte> utf8Json,
        string? acceptedManifestSha256) =>
        VerifyCore(utf8Json, allowInstalledVersion: true, acceptedManifestSha256);

    internal static bool IsSameVersion(Version left, Version right) =>
        left.Major == right.Major && left.Minor == right.Minor &&
        Math.Max(0, left.Build) == Math.Max(0, right.Build) &&
        Math.Max(0, left.Revision) == Math.Max(0, right.Revision);

    private ControllerResult<VerifiedUpdateManifest> VerifyCore(
        ReadOnlySpan<byte> utf8Json,
        bool allowInstalledVersion,
        string? acceptedManifestSha256)
    {
        if (utf8Json.IsEmpty || utf8Json.Length > _policy.MaximumManifestBytes)
        {
            return Failure(ControllerErrorCode.InvalidRequest, "error.update.manifest_size_invalid");
        }

        SignedUpdateManifestDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<SignedUpdateManifestDocument>(utf8Json, JsonOptions);
        }
        catch (JsonException)
        {
            return Failure(ControllerErrorCode.IntegrityFailure, "error.update.manifest_invalid");
        }

        if (document is null || !HasBoundedFields(document))
        {
            return Failure(ControllerErrorCode.IntegrityFailure, "error.update.manifest_invalid");
        }

        var key = _policy.TrustedKeys.SingleOrDefault(candidate =>
            string.Equals(candidate.KeyId, document.KeyId, StringComparison.Ordinal));
        if (key is null || !VerifySignature(document, key))
        {
            return Failure(ControllerErrorCode.IntegrityFailure, "error.update.manifest_signature_invalid");
        }

        if (document.SchemaVersion != UpdateFeedTrustPolicy.CurrentSchemaVersion ||
            !string.Equals(document.Channel, _policy.Channel, StringComparison.Ordinal))
        {
            return Failure(ControllerErrorCode.PolicyDenied, "error.update.manifest_policy_mismatch");
        }

        var hasVersion = Version.TryParse(document.Version, out var version);
        var isInstalledVersion = hasVersion && IsSameVersion(version!, _policy.CurrentVersion);
        var isExactAcceptedManifest = acceptedManifestSha256 is not null &&
            string.Equals(
                Convert.ToHexString(SHA256.HashData(utf8Json)),
                acceptedManifestSha256,
                StringComparison.OrdinalIgnoreCase);
        // Older clients persisted only the sequence when downloading. After that
        // installer has actually become the running version, an unbound equal
        // sequence can describe installed status only; it can never offer or stage
        // an update. An existing identity mismatch never gets this exception.
        var isLegacyInstalledStatus = allowInstalledVersion && isInstalledVersion &&
            acceptedManifestSha256 is null && _policy.LastAcceptedReleaseSequence > 0;
        if (document.ReleaseSequence < _policy.LastAcceptedReleaseSequence ||
            document.ReleaseSequence == _policy.LastAcceptedReleaseSequence &&
                !isExactAcceptedManifest && !isLegacyInstalledStatus)
        {
            return Failure(ControllerErrorCode.StaleClient, "error.update.manifest_replayed");
        }

        if (!hasVersion || version! <= _policy.CurrentVersion &&
                !(allowInstalledVersion && isInstalledVersion))
        {
            return Failure(ControllerErrorCode.PolicyDenied, "error.update.version_not_newer");
        }

        if (!Uri.TryCreate(document.PackageUri, UriKind.Absolute, out var packageUri) ||
            !UpdateFeedTrustPolicy.IsAllowedTransport(
                packageUri,
                _policy.AllowLoopbackHttpForTesting) ||
            !HasSameOrigin(packageUri, _policy.PackageOrigin) ||
            !HasChannelBoundPackagePath(packageUri, document.Channel))
        {
            return Failure(ControllerErrorCode.PolicyDenied, "error.update.package_origin_denied");
        }

        if (!Sha256Pattern().IsMatch(document.Sha256) ||
            document.SizeBytes <= 0 ||
            document.SizeBytes > _policy.MaximumPackageBytes)
        {
            return Failure(ControllerErrorCode.IntegrityFailure, "error.update.package_metadata_invalid");
        }

        var now = _timeProvider.GetUtcNow();
        if (document.PublishedAtUtc > now.AddMinutes(5) ||
            document.ExpiresAtUtc <= now ||
            document.ExpiresAtUtc <= document.PublishedAtUtc ||
            document.ExpiresAtUtc - document.PublishedAtUtc > TimeSpan.FromDays(31))
        {
            return Failure(ControllerErrorCode.Expired, "error.update.manifest_expired");
        }

        if (document.RolloutStartUtc < document.PublishedAtUtc ||
            document.RolloutEndUtc < document.RolloutStartUtc ||
            document.RolloutEndUtc > document.ExpiresAtUtc ||
            document.InitialRolloutBasisPoints is < 0 or > 10_000 ||
            document.FinalRolloutBasisPoints is < 0 or > 10_000 ||
            document.FinalRolloutBasisPoints < document.InitialRolloutBasisPoints)
        {
            return Failure(ControllerErrorCode.IntegrityFailure, "error.update.rollout_invalid");
        }

        return ControllerResult<VerifiedUpdateManifest>.Success(new VerifiedUpdateManifest(
            new UpdatePackageInfo(
                version!,
                packageUri,
                document.Sha256.ToUpperInvariant(),
                document.SizeBytes,
                document.RequiresRestart),
            document.Channel,
            document.ReleaseSequence,
            document.PublishedAtUtc,
            document.ExpiresAtUtc,
            new UpdateRolloutSchedule(
                document.RolloutStartUtc,
                document.RolloutEndUtc,
                document.InitialRolloutBasisPoints,
                document.FinalRolloutBasisPoints),
            document.KeyId));
    }

    private static bool HasBoundedFields(SignedUpdateManifestDocument document) =>
        document.Channel is { Length: > 0 and <= 32 } &&
        document.Version is { Length: > 0 and <= 32 } &&
        document.PackageUri is { Length: > 0 and <= 2048 } &&
        document.Sha256 is { Length: 64 } &&
        document.KeyId is { Length: > 0 and <= 64 } &&
        document.Signature is { Length: > 0 and <= 256 };

    private static bool VerifySignature(
        SignedUpdateManifestDocument document,
        UpdateManifestPublicKey key)
    {
        try
        {
            var publicKey = Convert.FromBase64String(key.SubjectPublicKeyInfoBase64);
            var signature = Convert.FromBase64String(document.Signature);
            if (signature.Length != 64)
            {
                return false;
            }

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKey, out var bytesRead);
            return bytesRead == publicKey.Length && ecdsa.VerifyData(
                UpdateManifestCanonicalEncoding.Encode(document),
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            return false;
        }
    }

    private static bool HasSameOrigin(Uri candidate, Uri expected) =>
        string.Equals(candidate.Scheme, expected.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(candidate.IdnHost, expected.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        candidate.Port == expected.Port;

    private static bool HasChannelBoundPackagePath(Uri packageUri, string channel)
    {
        var escapedChannel = Regex.Escape(channel);
        return Regex.IsMatch(
            packageUri.AbsolutePath,
            $"^/{escapedChannel}/OrbitNavigator-[0-9]+\\.[0-9]+\\.[0-9]+" +
            (channel == "beta" ? "(?:-beta\\.[0-9]+)?" : string.Empty) +
            "\\.exe$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    }

    private static ControllerResult<VerifiedUpdateManifest> Failure(
        ControllerErrorCode code,
        string messageKey) =>
        ControllerResult<VerifiedUpdateManifest>.Failure(ControllerError.Create(code, messageKey));

    [GeneratedRegex("^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();
}
