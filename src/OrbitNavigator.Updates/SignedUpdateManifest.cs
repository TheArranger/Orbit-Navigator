using System.Buffers.Binary;
using System.Text;
using OrbitNavigator.Contracts.Updates;

namespace OrbitNavigator.Updates;

public sealed record SignedUpdateManifestDocument(
    int SchemaVersion,
    string Channel,
    long ReleaseSequence,
    string Version,
    string PackageUri,
    string Sha256,
    long SizeBytes,
    bool RequiresRestart,
    DateTimeOffset PublishedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset RolloutStartUtc,
    DateTimeOffset RolloutEndUtc,
    int InitialRolloutBasisPoints,
    int FinalRolloutBasisPoints,
    string KeyId,
    string Signature);

public sealed record UpdateManifestPublicKey(
    string KeyId,
    string SubjectPublicKeyInfoBase64);

public sealed record VerifiedUpdateManifest(
    UpdatePackageInfo Package,
    string Channel,
    long ReleaseSequence,
    DateTimeOffset PublishedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    UpdateRolloutSchedule Rollout,
    string KeyId);

public sealed record UpdateRolloutSchedule(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    int InitialBasisPoints,
    int FinalBasisPoints);

public enum UpdateReleaseChannel
{
    Primary = 0,
    Beta = 1,
}

public static class UpdateReleaseChannels
{
    public static string ToToken(UpdateReleaseChannel channel) => channel switch
    {
        UpdateReleaseChannel.Primary => "primary",
        UpdateReleaseChannel.Beta => "beta",
        _ => throw new ArgumentOutOfRangeException(nameof(channel)),
    };
}

public sealed class UpdateFeedTrustPolicy
{
    public const int CurrentSchemaVersion = 1;
    public const int DefaultMaximumManifestBytes = 64 * 1024;
    public const long DefaultMaximumPackageBytes = 1024L * 1024 * 1024;

    public UpdateFeedTrustPolicy(
        Uri manifestUri,
        Uri packageOrigin,
        string channel,
        Version currentVersion,
        long lastAcceptedReleaseSequence,
        IReadOnlyList<UpdateManifestPublicKey> trustedKeys,
        bool allowLoopbackHttpForTesting = false,
        int maximumManifestBytes = DefaultMaximumManifestBytes,
        long maximumPackageBytes = DefaultMaximumPackageBytes)
    {
        ArgumentNullException.ThrowIfNull(manifestUri);
        ArgumentNullException.ThrowIfNull(packageOrigin);
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(currentVersion);
        ArgumentNullException.ThrowIfNull(trustedKeys);

        if (!manifestUri.IsAbsoluteUri || !packageOrigin.IsAbsoluteUri)
        {
            throw new ArgumentException("Update feed locations must be absolute URIs.");
        }

        if (!IsAllowedTransport(manifestUri, allowLoopbackHttpForTesting) ||
            !IsAllowedTransport(packageOrigin, allowLoopbackHttpForTesting))
        {
            throw new ArgumentException(
                "Update feeds require HTTPS. HTTP is permitted only for explicit loopback tests.");
        }

        if (lastAcceptedReleaseSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lastAcceptedReleaseSequence));
        }

        if (maximumManifestBytes is <= 0 or > DefaultMaximumManifestBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumManifestBytes));
        }

        if (maximumPackageBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPackageBytes));
        }

        var keys = trustedKeys.ToArray();
        if (keys.Length is 0 or > 4 ||
            keys.Any(key => string.IsNullOrWhiteSpace(key.KeyId) ||
                string.IsNullOrWhiteSpace(key.SubjectPublicKeyInfoBase64)) ||
            keys.Select(key => key.KeyId).Distinct(StringComparer.Ordinal).Count() != keys.Length)
        {
            throw new ArgumentException(
                "The update trust policy requires one to four uniquely identified public keys.",
                nameof(trustedKeys));
        }

        ManifestUri = manifestUri;
        PackageOrigin = packageOrigin;
        Channel = channel;
        CurrentVersion = currentVersion;
        LastAcceptedReleaseSequence = lastAcceptedReleaseSequence;
        TrustedKeys = keys;
        AllowLoopbackHttpForTesting = allowLoopbackHttpForTesting;
        MaximumManifestBytes = maximumManifestBytes;
        MaximumPackageBytes = maximumPackageBytes;
    }

    public Uri ManifestUri { get; }

    public Uri PackageOrigin { get; }

    public string Channel { get; }

    public Version CurrentVersion { get; }

    public long LastAcceptedReleaseSequence { get; }

    public IReadOnlyList<UpdateManifestPublicKey> TrustedKeys { get; }

    public bool AllowLoopbackHttpForTesting { get; }

    public int MaximumManifestBytes { get; }

    public long MaximumPackageBytes { get; }

    internal static bool IsAllowedTransport(Uri uri, bool allowLoopbackHttpForTesting) =>
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
        (allowLoopbackHttpForTesting &&
            string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            uri.IsLoopback);
}

public sealed class UpdateChannelTrustCatalog
{
    public UpdateChannelTrustCatalog(
        UpdateFeedTrustPolicy primary,
        UpdateFeedTrustPolicy beta)
    {
        Primary = primary ?? throw new ArgumentNullException(nameof(primary));
        Beta = beta ?? throw new ArgumentNullException(nameof(beta));
        if (!string.Equals(
                primary.Channel,
                UpdateReleaseChannels.ToToken(UpdateReleaseChannel.Primary),
                StringComparison.Ordinal) ||
            !string.Equals(
                beta.Channel,
                UpdateReleaseChannels.ToToken(UpdateReleaseChannel.Beta),
                StringComparison.Ordinal))
        {
            throw new ArgumentException("Update trust policies are assigned to the wrong channels.");
        }

        var primaryKeyIds = primary.TrustedKeys
            .Select(key => key.KeyId)
            .ToHashSet(StringComparer.Ordinal);
        var primaryKeyMaterial = primary.TrustedKeys
            .Select(key => key.SubjectPublicKeyInfoBase64)
            .ToHashSet(StringComparer.Ordinal);
        if (beta.TrustedKeys.Any(key =>
            primaryKeyIds.Contains(key.KeyId) ||
            primaryKeyMaterial.Contains(key.SubjectPublicKeyInfoBase64)))
        {
            throw new ArgumentException(
                "Primary and Beta must use independent signing key identifiers and key material.");
        }
    }

    public UpdateFeedTrustPolicy Primary { get; }

    public UpdateFeedTrustPolicy Beta { get; }

    public UpdateFeedTrustPolicy Get(UpdateReleaseChannel channel) => channel switch
    {
        UpdateReleaseChannel.Primary => Primary,
        UpdateReleaseChannel.Beta => Beta,
        _ => throw new ArgumentOutOfRangeException(nameof(channel)),
    };
}

public static class UpdateManifestCanonicalEncoding
{
    private static readonly byte[] Domain = Encoding.ASCII.GetBytes("OrbitNavigator.UpdateManifest.v1");

    public static byte[] Encode(SignedUpdateManifestDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        using var stream = new MemoryStream();
        WriteBytes(stream, Domain);
        WriteInt32(stream, document.SchemaVersion);
        WriteString(stream, document.Channel);
        WriteInt64(stream, document.ReleaseSequence);
        WriteString(stream, document.Version);
        WriteString(stream, document.PackageUri);
        WriteString(stream, document.Sha256);
        WriteInt64(stream, document.SizeBytes);
        stream.WriteByte(document.RequiresRestart ? (byte)1 : (byte)0);
        WriteString(stream, document.PublishedAtUtc.ToUniversalTime().ToString("O"));
        WriteString(stream, document.ExpiresAtUtc.ToUniversalTime().ToString("O"));
        WriteString(stream, document.RolloutStartUtc.ToUniversalTime().ToString("O"));
        WriteString(stream, document.RolloutEndUtc.ToUniversalTime().ToString("O"));
        WriteInt32(stream, document.InitialRolloutBasisPoints);
        WriteInt32(stream, document.FinalRolloutBasisPoints);
        WriteString(stream, document.KeyId);
        return stream.ToArray();
    }

    private static void WriteString(Stream stream, string? value) =>
        WriteBytes(stream, Encoding.UTF8.GetBytes(value ?? string.Empty));

    private static void WriteBytes(Stream stream, ReadOnlySpan<byte> value)
    {
        WriteInt32(stream, value.Length);
        stream.Write(value);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteInt64(Stream stream, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        stream.Write(bytes);
    }
}
