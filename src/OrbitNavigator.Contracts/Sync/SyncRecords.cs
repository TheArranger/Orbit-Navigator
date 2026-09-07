using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.Contracts.Sync;

public enum SyncRecordKind
{
    Upsert = 0,
    Tombstone = 1,
    Purge = 2,
}

public sealed record CanonicalSyncAad(
    int ProtocolVersion,
    int SchemaVersion,
    ProfileId ProfileId,
    DeviceId DeviceId,
    SyncKeysetId KeysetId,
    long KeyEpoch,
    SyncRecordKind RecordKind,
    SyncEnvelopeId EnvelopeId,
    SyncDataCategory Category,
    SyncEntityId EntityId,
    SyncOperationId? OperationId,
    long ClientGeneration,
    long ClientSequence);

public sealed record EncryptedSyncEnvelope(
    CanonicalSyncAad Aad,
    ReadOnlyMemory<byte> Nonce,
    ReadOnlyMemory<byte> Ciphertext,
    ReadOnlyMemory<byte> AuthenticationTag);

public sealed record EncryptedSyncTombstone(
    CanonicalSyncAad Aad,
    ReadOnlyMemory<byte> Nonce,
    ReadOnlyMemory<byte> Ciphertext,
    ReadOnlyMemory<byte> AuthenticationTag);

/// <summary>
/// Identity emitted only after the privacy-owned codec has authenticated and
/// decrypted an encrypted tombstone using its canonical AAD.
/// </summary>
public sealed record AuthenticatedSyncTombstoneReceipt(
    ProfileId ProfileId,
    DeviceId DeviceId,
    SyncKeysetId KeysetId,
    long KeyEpoch,
    SyncEnvelopeId EnvelopeId,
    SyncDataCategory Category,
    SyncEntityId EntityId,
    long ClientGeneration,
    long ClientSequence);

/// <summary>
/// An encrypted command for exactly one category. The decrypted marker must pass
/// <see cref="SyncContractRules.ValidatePurgeMarker"/> before the command is applied.
/// </summary>
public sealed record EncryptedPurgeCommand(
    CanonicalSyncAad Aad,
    ReadOnlyMemory<byte> Nonce,
    ReadOnlyMemory<byte> Ciphertext,
    ReadOnlyMemory<byte> AuthenticationTag);

public sealed record DecryptedPurgeMarker(
    SyncOperationId OperationId,
    ProfileId ProfileId,
    SyncDataCategory Category,
    long ClientGeneration,
    SyncKeysetId KeysetId);

public abstract record SyncRecordPayload(
    SyncEntityId EntityId,
    long Revision,
    DateTimeOffset ModifiedAtUtc)
{
    public abstract SyncDataCategory Category { get; }
}

public sealed record HistorySyncRecord(
    SyncEntityId EntityId,
    long Revision,
    DateTimeOffset ModifiedAtUtc,
    string AbsoluteUrl,
    string Title,
    DateTimeOffset LastVisitedAtUtc,
    int VisitCount)
    : SyncRecordPayload(EntityId, Revision, ModifiedAtUtc)
{
    public override SyncDataCategory Category => SyncDataCategory.History;
}

public sealed record OpenTabSyncRecord(
    SyncEntityId EntityId,
    long Revision,
    DateTimeOffset ModifiedAtUtc,
    string AbsoluteUrl,
    string Title,
    int Position,
    string? GroupLabel)
    : SyncRecordPayload(EntityId, Revision, ModifiedAtUtc)
{
    public override SyncDataCategory Category => SyncDataCategory.OpenTabs;
}

public abstract record SyncSettingValue;

public sealed record BooleanSyncSettingValue(bool Value) : SyncSettingValue;

public sealed record IntegerSyncSettingValue(int Value) : SyncSettingValue;

public sealed record DecimalSyncSettingValue(decimal Value) : SyncSettingValue;

public sealed record TextSyncSettingValue(string Value) : SyncSettingValue;

public sealed record SyncSettingEntry(
    SyncableSettingField Field,
    SyncSettingValue Value);

public sealed record SettingsSyncRecord(
    SyncEntityId EntityId,
    long Revision,
    DateTimeOffset ModifiedAtUtc,
    SyncSettingPersistenceScope Scope,
    IReadOnlyList<SyncSettingEntry> Settings)
    : SyncRecordPayload(EntityId, Revision, ModifiedAtUtc)
{
    public override SyncDataCategory Category => SyncDataCategory.Settings;
}

public sealed record SyncValidationIssue(string Code, string MessageKey);

public sealed record SyncValidationResult(IReadOnlyList<SyncValidationIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;

    public static SyncValidationResult Valid { get; } = new(Array.Empty<SyncValidationIssue>());
}

public static class SyncContractRules
{
    public static SyncValidationResult ValidateAad(CanonicalSyncAad? aad)
    {
        var issues = new List<SyncValidationIssue>();
        if (aad is null)
        {
            issues.Add(new("aad.required", "sync.validation.aad_required"));
            return new(issues);
        }

        if (aad.ProtocolVersion != SyncProtocol.CurrentProtocolVersion)
            issues.Add(new("aad.protocol", "sync.validation.protocol_version"));
        if (aad.SchemaVersion != SyncProtocol.CurrentSchemaVersion)
            issues.Add(new("aad.schema", "sync.validation.schema_version"));
        if (aad.ProfileId.IsEmpty)
            issues.Add(new("aad.profile", "sync.validation.profile.required"));
        if (aad.DeviceId.IsEmpty)
            issues.Add(new("aad.device", "sync.validation.device.required"));
        if (!aad.KeysetId.IsDefined)
            issues.Add(new("aad.keyset", "sync.validation.keyset_required"));
        if (aad.KeyEpoch < 0)
            issues.Add(new("aad.key-epoch", "sync.validation.key-epoch"));
        if (!aad.EnvelopeId.IsDefined)
            issues.Add(new("aad.envelope", "sync.validation.envelope_required"));
        if (!SyncAllowlist.IsAllowed(aad.Category))
            issues.Add(new("aad.category", "sync.validation.category_not_allowed"));
        if (!aad.EntityId.IsDefined)
            issues.Add(new("aad.entity", "sync.validation.entity_required"));
        if (aad.ClientGeneration < 0)
            issues.Add(new("aad.client-generation", "sync.validation.client-generation"));
        if (aad.ClientSequence < 0)
            issues.Add(new("aad.client-sequence", "sync.validation.client-sequence"));

        if (aad.RecordKind is SyncRecordKind.Upsert or SyncRecordKind.Tombstone)
        {
            if (aad.OperationId is not null)
                issues.Add(new("aad.operation", "sync.validation.operation_not_allowed"));
        }
        else if (aad.RecordKind == SyncRecordKind.Purge)
        {
            if (aad.OperationId is null || !aad.OperationId.Value.IsDefined)
                issues.Add(new("aad.operation", "sync.validation.operation_required"));
        }
        else
        {
            issues.Add(new("aad.kind", "sync.validation.record_kind"));
        }

        return new(issues);
    }

    public static SyncValidationResult ValidateEnvelope(EncryptedSyncEnvelope? value) =>
        ValidateEncrypted(value?.Aad, value?.Nonce, value?.Ciphertext, value?.AuthenticationTag, SyncRecordKind.Upsert);

    public static SyncValidationResult ValidateTombstone(EncryptedSyncTombstone? value) =>
        ValidateEncrypted(value?.Aad, value?.Nonce, value?.Ciphertext, value?.AuthenticationTag, SyncRecordKind.Tombstone);

    public static SyncValidationResult ValidateAuthenticatedTombstone(
        EncryptedSyncTombstone? tombstone,
        AuthenticatedSyncTombstoneReceipt? receipt)
    {
        var issues = new List<SyncValidationIssue>(ValidateTombstone(tombstone).Issues);
        if (tombstone is null || receipt is null)
        {
            issues.Add(new(
                "tombstone.receipt",
                "sync.validation.tombstone_authenticated_receipt_required"));
            return new(issues);
        }

        var aad = tombstone.Aad;
        if (receipt.ProfileId != aad.ProfileId ||
            receipt.DeviceId != aad.DeviceId ||
            receipt.KeysetId != aad.KeysetId ||
            receipt.KeyEpoch != aad.KeyEpoch ||
            receipt.EnvelopeId != aad.EnvelopeId ||
            receipt.Category != aad.Category ||
            receipt.EntityId != aad.EntityId ||
            receipt.ClientGeneration != aad.ClientGeneration ||
            receipt.ClientSequence != aad.ClientSequence)
        {
            issues.Add(new(
                "tombstone.receipt_identity",
                "sync.validation.tombstone_authenticated_identity_mismatch"));
        }

        return new(issues);
    }

    public static SyncValidationResult ValidatePurgeCommand(EncryptedPurgeCommand? value) =>
        ValidateEncrypted(value?.Aad, value?.Nonce, value?.Ciphertext, value?.AuthenticationTag, SyncRecordKind.Purge);

    public static SyncValidationResult ValidatePurgeMarker(
        CanonicalSyncAad aad,
        DecryptedPurgeMarker? marker)
    {
        var issues = new List<SyncValidationIssue>(ValidateAad(aad).Issues);
        if (aad.RecordKind != SyncRecordKind.Purge)
            issues.Add(new("purge.kind", "sync.validation.purge_kind"));
        if (marker is null)
        {
            issues.Add(new("purge.marker", "sync.validation.purge_marker_required"));
            return new(issues);
        }

        if (marker.OperationId != aad.OperationId)
            issues.Add(new("purge.operation", "sync.validation.purge_operation_mismatch"));
        if (marker.ProfileId != aad.ProfileId)
            issues.Add(new("purge.profile", "sync.validation.purge.profile-mismatch"));
        if (marker.Category != aad.Category)
            issues.Add(new("purge.category", "sync.validation.purge_category_mismatch"));
        if (marker.ClientGeneration != aad.ClientGeneration)
            issues.Add(new("purge.client-generation", "sync.validation.purge.generation-mismatch"));
        if (marker.KeysetId != aad.KeysetId)
            issues.Add(new("purge.keyset", "sync.validation.purge.keyset-mismatch"));

        return new(issues);
    }

    public static SyncValidationResult ValidateRecord(SyncRecordPayload? record)
    {
        var issues = new List<SyncValidationIssue>();
        if (record is null)
        {
            issues.Add(new("record.required", "sync.validation.record_required"));
            return new(issues);
        }

        if (!record.EntityId.IsDefined)
            issues.Add(new("record.entity", "sync.validation.entity_required"));
        if (record.Revision < 0)
            issues.Add(new("record.revision", "sync.validation.revision"));
        if (!SyncAllowlist.IsAllowed(record.Category))
            issues.Add(new("record.category", "sync.validation.category_not_allowed"));

        switch (record)
        {
            case HistorySyncRecord history:
                ValidateUrl(history.AbsoluteUrl, issues);
                if (history.Title.Length > 1024 || history.VisitCount < 0)
                    issues.Add(new("history.fields", "sync.validation.history_fields"));
                break;
            case OpenTabSyncRecord tab:
                ValidateUrl(tab.AbsoluteUrl, issues);
                if (tab.Title.Length > 1024 || tab.Position < 0 || (tab.GroupLabel?.Length ?? 0) > 256)
                    issues.Add(new("tab.fields", "sync.validation.open_tab_fields"));
                break;
            case SettingsSyncRecord settings:
                if (settings.Scope != SyncSettingPersistenceScope.GlobalPersistent)
                    issues.Add(new("settings.scope", "sync.validation.settings.scope"));
                ValidateSettings(settings.Settings, issues);
                break;
            default:
                issues.Add(new("record.type", "sync.validation.record_type_not_allowed"));
                break;
        }

        return new(issues);
    }

    private static SyncValidationResult ValidateEncrypted(
        CanonicalSyncAad? aad,
        ReadOnlyMemory<byte>? nonce,
        ReadOnlyMemory<byte>? ciphertext,
        ReadOnlyMemory<byte>? tag,
        SyncRecordKind expectedKind)
    {
        var issues = new List<SyncValidationIssue>(ValidateAad(aad).Issues);
        if (aad?.RecordKind != expectedKind)
            issues.Add(new("encrypted.kind", "sync.validation.encrypted_kind"));
        if (nonce is null || nonce.Value.Length != SyncProtocol.NonceSizeBytes)
            issues.Add(new("encrypted.nonce", "sync.validation.nonce_size"));
        if (tag is null || tag.Value.Length != SyncProtocol.AuthenticationTagSizeBytes)
            issues.Add(new("encrypted.tag", "sync.validation.authentication_tag_size"));
        if (ciphertext is null || ciphertext.Value.IsEmpty || ciphertext.Value.Length > SyncProtocol.MaximumCiphertextSizeBytes)
            issues.Add(new("encrypted.ciphertext", "sync.validation.ciphertext_size"));
        return new(issues);
    }

    private static void ValidateUrl(string value, List<SyncValidationIssue> issues)
    {
        if (value.Length > 16_384 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            issues.Add(new("record.url", "sync.validation.absolute_http_url"));
        }
    }

    private static void ValidateSettings(
        IReadOnlyList<SyncSettingEntry>? settings,
        List<SyncValidationIssue> issues)
    {
        if (settings is null || settings.Count > SyncAllowlist.AllowedSettingFields.Count)
        {
            issues.Add(new("settings.count", "sync.validation.settings_count"));
            return;
        }

        var seen = new HashSet<SyncableSettingField>();
        foreach (var entry in settings)
        {
            if (!SyncAllowlist.IsAllowed(entry.Field) || !seen.Add(entry.Field))
                issues.Add(new("settings.field", "sync.validation.settings_field_not_allowed"));
            if (!IsExpectedValue(entry) || entry.Value is TextSyncSettingValue { Value.Length: > 256 })
                issues.Add(new("settings.value", "sync.validation.settings_value"));
        }
    }

    private static bool IsExpectedValue(SyncSettingEntry entry) => entry.Field switch
    {
        SyncableSettingField.ThemeMode or
        SyncableSettingField.ReadingFontFamily or
        SyncableSettingField.ReadingTint or
        SyncableSettingField.ReadingLineFocus => entry.Value is TextSyncSettingValue,

        SyncableSettingField.DyslexiaFriendlyFontEnabled or
        SyncableSettingField.ReadingEnabled => entry.Value is BooleanSyncSettingValue,

        SyncableSettingField.TextScale or
        SyncableSettingField.ReadingFontScale => entry.Value is IntegerSyncSettingValue integer &&
            integer.Value is >= 50 and <= 300,

        SyncableSettingField.ReadingLineHeight or
        SyncableSettingField.ReadingLetterSpacing or
        SyncableSettingField.ReadingWordSpacing or
        SyncableSettingField.ReadingContrast => entry.Value is DecimalSyncSettingValue decimalValue &&
            decimalValue.Value is >= 0 and <= 5,

        _ => false,
    };

    public static byte[] EncodeCanonicalAad(CanonicalSyncAad aad)
    {
        var validation = ValidateAad(aad);
        if (!validation.IsValid)
            throw new ArgumentException("AAD must pass validation before canonical encoding.", nameof(aad));

        static string Id(Guid value) => value.ToString("N", System.Globalization.CultureInfo.InvariantCulture);
        var entity = aad.EntityId is { } entityId ? Id(entityId.Value) : "-";
        var operation = aad.OperationId is { } operationId ? Id(operationId.Value) : "-";
        var canonical = string.Join(
            '|',
            "orbit-navigator",
            "sync-aad",
            $"protocol={aad.ProtocolVersion}",
            $"schema={aad.SchemaVersion}",
            $"profile={Id(aad.ProfileId.Value)}",
            $"device={Id(aad.DeviceId.Value)}",
            $"keyset={Id(aad.KeysetId.Value)}",
            $"keyepoch={aad.KeyEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            $"kind={aad.RecordKind.ToString().ToLowerInvariant()}",
            $"envelope={Id(aad.EnvelopeId.Value)}",
            $"category={aad.Category.ToString().ToLowerInvariant()}",
            $"entity={Id(aad.EntityId.Value)}",
            $"operation={operation}",
            $"clientgeneration={aad.ClientGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            $"clientsequence={aad.ClientSequence.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        return System.Text.Encoding.UTF8.GetBytes(canonical);
    }
}
