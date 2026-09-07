using System.Text.RegularExpressions;
using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.Contracts.Infrastructure;

public enum ProfileStorageDurability
{
    Session = 0,
    Persistent = 1,
}

public sealed class ProfileStorageNamespace : IEquatable<ProfileStorageNamespace>
{
    private static readonly Regex NamePattern = new(
        "^[a-z0-9](?:[a-z0-9._-]{0,62}[a-z0-9])?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> ForbiddenNames = new(StringComparer.Ordinal)
    {
        "download",
        "downloads",
        "downloaded-file",
        "downloaded-files",
    };

    private ProfileStorageNamespace(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static ControllerResult<ProfileStorageNamespace> Create(string value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized) ||
            !NamePattern.IsMatch(normalized) ||
            ForbiddenNames.Contains(normalized))
        {
            return ControllerResult<ProfileStorageNamespace>.Failure(ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "error.profile_storage.namespace_invalid"));
        }

        return ControllerResult<ProfileStorageNamespace>.Success(
            new ProfileStorageNamespace(normalized));
    }

    public bool Equals(ProfileStorageNamespace? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) =>
        obj is ProfileStorageNamespace other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;
}

public sealed class ProfileStorageKey : IEquatable<ProfileStorageKey>
{
    private static readonly Regex KeyPattern = new(
        "^[A-Za-z0-9](?:[A-Za-z0-9._:-]{0,254}[A-Za-z0-9])?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private ProfileStorageKey(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static ControllerResult<ProfileStorageKey> Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !KeyPattern.IsMatch(value))
        {
            return ControllerResult<ProfileStorageKey>.Failure(ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "error.profile_storage.key_invalid"));
        }

        return ControllerResult<ProfileStorageKey>.Success(new ProfileStorageKey(value));
    }

    public bool Equals(ProfileStorageKey? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is ProfileStorageKey other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;
}

public readonly record struct ProfileStorageRevision(Guid Value)
{
    public bool IsEmpty => Value == Guid.Empty;
}

public sealed class ProfileStorageAddress
{
    private ProfileStorageAddress(
        PrivacyContext context,
        ProfileStorageNamespace storageNamespace,
        ProfileStorageKey key,
        ProfileStorageDurability durability)
    {
        Context = context;
        Namespace = storageNamespace;
        Key = key;
        Durability = durability;
    }

    public PrivacyContext Context { get; }

    public ProfileStorageNamespace Namespace { get; }

    public ProfileStorageKey Key { get; }

    public ProfileStorageDurability Durability { get; }

    public static ControllerResult<ProfileStorageAddress> Create(
        PrivacyContext? context,
        ProfileStorageNamespace? storageNamespace,
        ProfileStorageKey? key,
        ProfileStorageDurability durability)
    {
        if (context is not { IsStructurallyValid: true } ||
            storageNamespace is null ||
            key is null ||
            !Enum.IsDefined(durability))
        {
            return ControllerResult<ProfileStorageAddress>.Failure(ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "error.profile_storage.address_invalid"));
        }

        if (context.IsPrivate && durability == ProfileStorageDurability.Persistent)
        {
            return ControllerResult<ProfileStorageAddress>.Failure(ControllerError.Create(
                ControllerErrorCode.PolicyDenied,
                "error.private.persistent_storage_denied"));
        }

        return ControllerResult<ProfileStorageAddress>.Success(new ProfileStorageAddress(
            context,
            storageNamespace,
            key,
            durability));
    }
}

public sealed class ProfileStorageEntry
{
    private readonly byte[] _payload;

    private ProfileStorageEntry(ProfileStorageRevision revision, byte[] payload)
    {
        Revision = revision;
        _payload = payload;
    }

    public ProfileStorageRevision Revision { get; }

    public ReadOnlyMemory<byte> Payload => _payload;

    public static ControllerResult<ProfileStorageEntry> Create(
        ProfileStorageRevision revision,
        ReadOnlySpan<byte> payload)
    {
        if (revision.IsEmpty)
        {
            return ControllerResult<ProfileStorageEntry>.Failure(ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "error.profile_storage.revision_invalid"));
        }

        return ControllerResult<ProfileStorageEntry>.Success(
            new ProfileStorageEntry(revision, payload.ToArray()));
    }
}

public sealed class ProfileStorageWriteRequest
{
    private readonly byte[] _payload;

    private ProfileStorageWriteRequest(
        ProfileStorageAddress address,
        byte[] payload,
        ProfileStorageRevision? expectedRevision)
    {
        Address = address;
        _payload = payload;
        ExpectedRevision = expectedRevision;
    }

    public ProfileStorageAddress Address { get; }

    public ReadOnlyMemory<byte> Payload => _payload;

    public ProfileStorageRevision? ExpectedRevision { get; }

    public static ControllerResult<ProfileStorageWriteRequest> Create(
        ProfileStorageAddress? address,
        ReadOnlySpan<byte> payload,
        ProfileStorageRevision? expectedRevision = null)
    {
        if (address is null || expectedRevision is { IsEmpty: true })
        {
            return ControllerResult<ProfileStorageWriteRequest>.Failure(ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "error.profile_storage.write_invalid"));
        }

        return ControllerResult<ProfileStorageWriteRequest>.Success(
            new ProfileStorageWriteRequest(address, payload.ToArray(), expectedRevision));
    }
}

public sealed record ProfileStorageWriteReceipt(ProfileStorageRevision Revision);

public interface IProfileStorage
{
    ValueTask<ControllerResult<ProfileStorageEntry>> ReadAsync(
        ProfileStorageAddress address,
        CancellationToken cancellationToken = default);

    ValueTask<ControllerResult<ProfileStorageWriteReceipt>> WriteAsync(
        ProfileStorageWriteRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<ControllerResult> DeleteAsync(
        ProfileStorageAddress address,
        ProfileStorageRevision? expectedRevision = null,
        CancellationToken cancellationToken = default);
}
