using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Privacy;

namespace OrbitNavigator.Privacy.Persistence;

public sealed record PersistentRuleSnapshot<T>(
    IReadOnlyList<T> Rules,
    ProfileStorageRevision? Revision);

/// <summary>
/// Persists the two Orbit-owned privacy rule documents. The documents are
/// deliberately separate, strictly versioned, bounded, and written using the
/// revision returned by <see cref="IProfileStorage"/>.
/// </summary>
public sealed class PrivacyRulePersistence
{
    public const int CurrentVersion = 1;
    public const int MaximumRules = 1_024;
    public const int MaximumPayloadBytes = 1_048_576;
    public const int MaximumOriginCharacters = 2_048;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        MaxDepth = 16,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly ProfileStorageNamespace ProtectionNamespace =
        Required(ProfileStorageNamespace.Create("privacy.protection"));
    private static readonly ProfileStorageNamespace PermissionNamespace =
        Required(ProfileStorageNamespace.Create("privacy.permissions"));
    private static readonly ProfileStorageKey PersistentRulesKey =
        Required(ProfileStorageKey.Create("persistent-rules-v1"));

    private readonly IProfileStorage _storage;

    public PrivacyRulePersistence(IProfileStorage storage)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    }

    public ValueTask<ControllerResult<PersistentRuleSnapshot<ProtectionException>>>
        ReadProtectionAsync(
            PrivacyContext context,
            CancellationToken cancellationToken = default) =>
        ReadAsync(
            context,
            ProtectionNamespace,
            DecodeProtection,
            cancellationToken);

    public ValueTask<ControllerResult<ProfileStorageWriteReceipt>> WriteProtectionAsync(
        PrivacyContext context,
        IReadOnlyList<ProtectionException> rules,
        ProfileStorageRevision? expectedRevision,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            context,
            ProtectionNamespace,
            EncodeProtection(context.ProfileId, rules),
            expectedRevision,
            cancellationToken);

    public ValueTask<ControllerResult<PersistentRuleSnapshot<PermissionRule>>>
        ReadPermissionsAsync(
            PrivacyContext context,
            CancellationToken cancellationToken = default) =>
        ReadAsync(
            context,
            PermissionNamespace,
            DecodePermissions,
            cancellationToken);

    public ValueTask<ControllerResult<ProfileStorageWriteReceipt>> WritePermissionsAsync(
        PrivacyContext context,
        IReadOnlyList<PermissionRule> rules,
        ProfileStorageRevision? expectedRevision,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            context,
            PermissionNamespace,
            EncodePermissions(context.ProfileId, rules),
            expectedRevision,
            cancellationToken);

    private async ValueTask<ControllerResult<PersistentRuleSnapshot<T>>> ReadAsync<T>(
        PrivacyContext context,
        ProfileStorageNamespace storageNamespace,
        Func<PrivacyContext, ReadOnlyMemory<byte>, ControllerResult<IReadOnlyList<T>>> decode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var addressResult = CreateAddress(context, storageNamespace);
        if (!addressResult.IsSuccess)
        {
            return ControllerResult<PersistentRuleSnapshot<T>>.Failure(
                addressResult.Error!);
        }

        var read = await _storage
            .ReadAsync(addressResult.Value!, cancellationToken)
            .ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            return read.Error?.Code == ControllerErrorCode.NotFound
                ? ControllerResult<PersistentRuleSnapshot<T>>.Success(
                    new PersistentRuleSnapshot<T>([], null))
                : ControllerResult<PersistentRuleSnapshot<T>>.Failure(read.Error!);
        }

        if (read.Value!.Payload.Length > MaximumPayloadBytes)
        {
            return ControllerResult<PersistentRuleSnapshot<T>>.Failure(
                IntegrityFailure("error.privacy_persistence.payload_too_large"));
        }

        var decoded = decode(context, read.Value.Payload);
        return decoded.IsSuccess
            ? ControllerResult<PersistentRuleSnapshot<T>>.Success(
                new PersistentRuleSnapshot<T>(
                    decoded.Value!,
                    read.Value.Revision))
            : ControllerResult<PersistentRuleSnapshot<T>>.Failure(decoded.Error!);
    }

    private async ValueTask<ControllerResult<ProfileStorageWriteReceipt>> WriteAsync(
        PrivacyContext context,
        ProfileStorageNamespace storageNamespace,
        ControllerResult<byte[]> encoded,
        ProfileStorageRevision? expectedRevision,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!encoded.IsSuccess)
        {
            return ControllerResult<ProfileStorageWriteReceipt>.Failure(encoded.Error!);
        }

        var addressResult = CreateAddress(context, storageNamespace);
        if (!addressResult.IsSuccess)
        {
            return ControllerResult<ProfileStorageWriteReceipt>.Failure(
                addressResult.Error!);
        }

        var request = ProfileStorageWriteRequest.Create(
            addressResult.Value,
            encoded.Value!,
            expectedRevision);
        if (!request.IsSuccess)
        {
            return ControllerResult<ProfileStorageWriteReceipt>.Failure(request.Error!);
        }

        var write = await _storage
            .WriteAsync(request.Value!, cancellationToken)
            .ConfigureAwait(false);
        return write.IsSuccess && write.Value!.Revision.IsEmpty
            ? ControllerResult<ProfileStorageWriteReceipt>.Failure(
                IntegrityFailure("error.privacy_persistence.revision_invalid"))
            : write;
    }

    private static ControllerResult<ProfileStorageAddress> CreateAddress(
        PrivacyContext context,
        ProfileStorageNamespace storageNamespace)
    {
        if (context is not { IsStructurallyValid: true } || context.IsPrivate)
        {
            return ControllerResult<ProfileStorageAddress>.Failure(
                ControllerError.Create(
                    context is { IsPrivate: true }
                        ? ControllerErrorCode.PolicyDenied
                        : ControllerErrorCode.InvalidRequest,
                    context is { IsPrivate: true }
                        ? "error.private.persistent_storage_denied"
                        : "error.privacy_persistence.context_invalid"));
        }

        return ProfileStorageAddress.Create(
            context,
            storageNamespace,
            PersistentRulesKey,
            ProfileStorageDurability.Persistent);
    }

    private static ControllerResult<byte[]> EncodeProtection(
        ProfileId profileId,
        IReadOnlyList<ProtectionException>? rules)
    {
        if (profileId.IsEmpty || rules is null || rules.Count > MaximumRules)
        {
            return ControllerResult<byte[]>.Failure(
                IntegrityFailure("error.privacy_persistence.protection_invalid"));
        }

        if (rules.Any(rule =>
            rule is null ||
            rule.Id.IsEmpty ||
            rule.Id.ProfileId != profileId ||
            rule.EffectiveDuration != ProtectionRelaxationDuration.Persistent ||
            rule.RequestedDuration != ProtectionRelaxationDuration.Persistent ||
            rule.SessionId is not null ||
            rule.ExpiresAtUtc is not null ||
            rule.Site.CanonicalOrigin.Length > MaximumOriginCharacters))
        {
            return ControllerResult<byte[]>.Failure(
                IntegrityFailure("error.privacy_persistence.protection_invalid"));
        }

        if (rules.Select(rule => rule.Id).Distinct().Count() != rules.Count ||
            rules.Select(rule => rule.Site.CanonicalOrigin)
                .Distinct(StringComparer.Ordinal).Count() != rules.Count)
        {
            return ControllerResult<byte[]>.Failure(
                IntegrityFailure("error.privacy_persistence.protection_duplicate"));
        }

        var document = new ProtectionDocument
        {
            Version = CurrentVersion,
            ProfileId = profileId.Value.ToString("N"),
            Rules = rules
                .OrderBy(rule => rule.Site.CanonicalOrigin, StringComparer.Ordinal)
                .Select(rule => new ProtectionRuleDocument
                {
                    Id = rule.Id.Value.ToString("N"),
                    ProfileId = rule.Id.ProfileId.Value.ToString("N"),
                    Origin = rule.Site.CanonicalOrigin,
                    Duration = rule.EffectiveDuration.ToString(),
                    CreatedAtUtc = rule.CreatedAtUtc.ToUniversalTime().ToString("O"),
                })
                .ToArray(),
        };
        return SerializeBounded(document);
    }

    private static ControllerResult<IReadOnlyList<ProtectionException>> DecodeProtection(
        PrivacyContext context,
        ReadOnlyMemory<byte> payload)
    {
        var parsed = Deserialize<ProtectionDocument>(payload);
        if (!parsed.IsSuccess ||
            parsed.Value is not { } document ||
            document.Version != CurrentVersion ||
            !TryParseGuid(document.ProfileId, out var profileGuid) ||
            profileGuid != context.ProfileId.Value ||
            document.Rules is null ||
            document.Rules.Length > MaximumRules)
        {
            return ControllerResult<IReadOnlyList<ProtectionException>>.Failure(
                IntegrityFailure("error.privacy_persistence.protection_corrupt"));
        }

        var browsing = HydrationBrowsingContext(context);
        var rules = new List<ProtectionException>(document.Rules.Length);
        var ids = new HashSet<ProtectionExceptionId>();
        var origins = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in document.Rules)
        {
            if (!TryParseGuid(item.Id, out var idGuid) ||
                !TryParseGuid(item.ProfileId, out var ruleProfileGuid) ||
                ruleProfileGuid != profileGuid ||
                item.Duration != nameof(ProtectionRelaxationDuration.Persistent) ||
                !TryCreateExactSite(item.Origin, out var site) ||
                !TryParseUtc(item.CreatedAtUtc, out var createdAtUtc))
            {
                return ControllerResult<IReadOnlyList<ProtectionException>>.Failure(
                    IntegrityFailure("error.privacy_persistence.protection_corrupt"));
            }

            var id = new ProtectionExceptionId(context.ProfileId, idGuid);
            if (!ids.Add(id) || !origins.Add(site!.CanonicalOrigin))
            {
                return ControllerResult<IReadOnlyList<ProtectionException>>.Failure(
                    IntegrityFailure("error.privacy_persistence.protection_duplicate"));
            }

            var created = ProtectionException.Create(
                browsing,
                id,
                site,
                ProtectionRelaxationDuration.Persistent,
                createdAtUtc);
            if (!created.IsSuccess)
            {
                return ControllerResult<IReadOnlyList<ProtectionException>>.Failure(
                    IntegrityFailure("error.privacy_persistence.protection_corrupt"));
            }

            rules.Add(created.Value!);
        }

        return ControllerResult<IReadOnlyList<ProtectionException>>.Success(rules);
    }

    private static ControllerResult<byte[]> EncodePermissions(
        ProfileId profileId,
        IReadOnlyList<PermissionRule>? rules)
    {
        if (profileId.IsEmpty || rules is null || rules.Count > MaximumRules)
        {
            return ControllerResult<byte[]>.Failure(
                IntegrityFailure("error.privacy_persistence.permissions_invalid"));
        }

        if (rules.Any(rule =>
            rule is null ||
            rule.Id.IsEmpty ||
            rule.Id.ProfileId != profileId ||
            rule.ProfileId != profileId ||
            rule.Scope != PermissionAllowScope.Persistent ||
            rule.SessionId is not null ||
            rule.ExpiresAtUtc is not null ||
            rule.Decision is not (PermissionDecision.Allow or PermissionDecision.Deny) ||
            rule.Capability == WebPermissionCapability.Unknown ||
            !Enum.IsDefined(rule.Capability) ||
            rule.Site.CanonicalOrigin.Length > MaximumOriginCharacters))
        {
            return ControllerResult<byte[]>.Failure(
                IntegrityFailure("error.privacy_persistence.permissions_invalid"));
        }

        if (rules.Select(rule => rule.Id).Distinct().Count() != rules.Count ||
            rules.Select(rule => (rule.Site.CanonicalOrigin, rule.Capability))
                .Distinct().Count() != rules.Count)
        {
            return ControllerResult<byte[]>.Failure(
                IntegrityFailure("error.privacy_persistence.permissions_duplicate"));
        }

        var document = new PermissionDocument
        {
            Version = CurrentVersion,
            ProfileId = profileId.Value.ToString("N"),
            Rules = rules
                .OrderBy(rule => rule.Site.CanonicalOrigin, StringComparer.Ordinal)
                .ThenBy(rule => rule.Capability)
                .Select(rule => new PermissionRuleDocument
                {
                    Id = rule.Id.Value.ToString("N"),
                    ProfileId = rule.ProfileId.Value.ToString("N"),
                    Origin = rule.Site.CanonicalOrigin,
                    Capability = rule.Capability.ToString(),
                    Decision = rule.Decision.ToString(),
                    Scope = rule.Scope.ToString(),
                    CreatedAtUtc = rule.CreatedAtUtc.ToUniversalTime().ToString("O"),
                })
                .ToArray(),
        };
        return SerializeBounded(document);
    }

    private static ControllerResult<IReadOnlyList<PermissionRule>> DecodePermissions(
        PrivacyContext context,
        ReadOnlyMemory<byte> payload)
    {
        var parsed = Deserialize<PermissionDocument>(payload);
        if (!parsed.IsSuccess ||
            parsed.Value is not { } document ||
            document.Version != CurrentVersion ||
            !TryParseGuid(document.ProfileId, out var profileGuid) ||
            profileGuid != context.ProfileId.Value ||
            document.Rules is null ||
            document.Rules.Length > MaximumRules)
        {
            return ControllerResult<IReadOnlyList<PermissionRule>>.Failure(
                IntegrityFailure("error.privacy_persistence.permissions_corrupt"));
        }

        var rules = new List<PermissionRule>(document.Rules.Length);
        var ids = new HashSet<PermissionRuleId>();
        var slots = new HashSet<(string Origin, WebPermissionCapability Capability)>();
        foreach (var item in document.Rules)
        {
            if (!TryParseGuid(item.Id, out var idGuid) ||
                !TryParseGuid(item.ProfileId, out var ruleProfileGuid) ||
                ruleProfileGuid != profileGuid ||
                !TryCreateExactSite(item.Origin, out var site) ||
                !TryParseEnum(item.Capability, out WebPermissionCapability capability) ||
                capability == WebPermissionCapability.Unknown ||
                !TryParseEnum(item.Decision, out PermissionDecision decision) ||
                decision is not (PermissionDecision.Allow or PermissionDecision.Deny) ||
                item.Scope != nameof(PermissionAllowScope.Persistent) ||
                !TryParseUtc(item.CreatedAtUtc, out var createdAtUtc))
            {
                return ControllerResult<IReadOnlyList<PermissionRule>>.Failure(
                    IntegrityFailure("error.privacy_persistence.permissions_corrupt"));
            }

            var id = new PermissionRuleId(context.ProfileId, idGuid);
            var slot = (site!.CanonicalOrigin, capability);
            if (!ids.Add(id) || !slots.Add(slot))
            {
                return ControllerResult<IReadOnlyList<PermissionRule>>.Failure(
                    IntegrityFailure("error.privacy_persistence.permissions_duplicate"));
            }

            rules.Add(new PermissionRule(
                id,
                context.ProfileId,
                null,
                site,
                capability,
                decision,
                PermissionAllowScope.Persistent,
                createdAtUtc,
                null));
        }

        return ControllerResult<IReadOnlyList<PermissionRule>>.Success(rules);
    }

    private static ControllerResult<byte[]> SerializeBounded<T>(T document)
        where T : class
    {
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
            return payload.Length <= MaximumPayloadBytes
                ? ControllerResult<byte[]>.Success(payload)
                : ControllerResult<byte[]>.Failure(
                    IntegrityFailure("error.privacy_persistence.payload_too_large"));
        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException)
        {
            return ControllerResult<byte[]>.Failure(
                IntegrityFailure("error.privacy_persistence.serialization_failed"));
        }
    }

    private static ControllerResult<T> Deserialize<T>(ReadOnlyMemory<byte> payload)
        where T : class
    {
        if (payload.Length == 0 || payload.Length > MaximumPayloadBytes)
        {
            return ControllerResult<T>.Failure(
                IntegrityFailure("error.privacy_persistence.payload_corrupt"));
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(payload.Span, JsonOptions);
            return value is null
                ? ControllerResult<T>.Failure(
                    IntegrityFailure("error.privacy_persistence.payload_corrupt"))
                : ControllerResult<T>.Success(value);
        }
        catch (JsonException)
        {
            return ControllerResult<T>.Failure(
                IntegrityFailure("error.privacy_persistence.payload_corrupt"));
        }
    }

    private static bool TryCreateExactSite(
        string? origin,
        out SiteIdentity? site)
    {
        site = null;
        if (string.IsNullOrWhiteSpace(origin) ||
            origin.Length > MaximumOriginCharacters ||
            !Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var created = SiteIdentity.Create(uri);
        if (!created.IsSuccess ||
            !string.Equals(
                created.Value!.CanonicalOrigin,
                origin,
                StringComparison.Ordinal))
        {
            return false;
        }

        site = created.Value;
        return true;
    }

    private static bool TryParseGuid(string? value, out Guid parsed) =>
        Guid.TryParseExact(value, "N", out parsed) && parsed != Guid.Empty;

    private static bool TryParseUtc(
        string? value,
        out DateTimeOffset parsed) =>
        DateTimeOffset.TryParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out parsed) &&
        parsed.Offset == TimeSpan.Zero;

    private static bool TryParseEnum<T>(string? value, out T parsed)
        where T : struct, Enum =>
        Enum.TryParse(value, ignoreCase: false, out parsed) &&
        Enum.IsDefined(parsed) &&
        string.Equals(parsed.ToString(), value, StringComparison.Ordinal);

    private static BrowsingContext HydrationBrowsingContext(PrivacyContext context) =>
        new(
            context,
            new BrowserWindowId(Guid.NewGuid()),
            new BrowserTabId(Guid.NewGuid()),
            null);

    private static ControllerError IntegrityFailure(string messageKey) =>
        ControllerError.Create(ControllerErrorCode.IntegrityFailure, messageKey);

    private static T Required<T>(ControllerResult<T> result)
        where T : class =>
        result.IsSuccess
            ? result.Value!
            : throw new InvalidOperationException(result.Error?.MessageKey);

    private sealed class ProtectionDocument
    {
        public int Version { get; init; }

        public string? ProfileId { get; init; }

        public ProtectionRuleDocument[]? Rules { get; init; }
    }

    private sealed class ProtectionRuleDocument
    {
        public string? Id { get; init; }

        public string? ProfileId { get; init; }

        public string? Origin { get; init; }

        public string? Duration { get; init; }

        public string? CreatedAtUtc { get; init; }
    }

    private sealed class PermissionDocument
    {
        public int Version { get; init; }

        public string? ProfileId { get; init; }

        public PermissionRuleDocument[]? Rules { get; init; }
    }

    private sealed class PermissionRuleDocument
    {
        public string? Id { get; init; }

        public string? ProfileId { get; init; }

        public string? Origin { get; init; }

        public string? Capability { get; init; }

        public string? Decision { get; init; }

        public string? Scope { get; init; }

        public string? CreatedAtUtc { get; init; }
    }
}
