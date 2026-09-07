using System.Collections.Frozen;

namespace OrbitNavigator.Contracts.Sync;

public enum SyncDataCategory
{
    History = 0,
    Settings = 1,
    OpenTabs = 2,
}

public enum SyncExcludedCategory
{
    Bookmarks = 0,
    Downloads = 1,
    ClipboardShelf = 2,
    PrivateBrowsing = 3,
    Cookies = 4,
    SavedPasswords = 5,
    FormEntriesAutofill = 6,
    SiteSessionsAuthTokens = 7,
    PermissionRules = 8,
    SiteProtectionExceptions = 9,
    EncryptionKeys = 10,
    RecoveryMaterial = 11,
    PerSiteReadingPreferences = 12,
    SessionReadingPreferences = 13,
    LocalDiagnostics = 14,
    LocalOrbitPasswordVault = 15,
}

public enum SyncableSettingField
{
    ThemeMode = 0,
    TextScale = 1,
    DyslexiaFriendlyFontEnabled = 2,
    ReadingEnabled = 3,
    ReadingFontFamily = 4,
    ReadingFontScale = 5,
    ReadingLineHeight = 6,
    ReadingLetterSpacing = 7,
    ReadingWordSpacing = 8,
    ReadingContrast = 9,
    ReadingTint = 10,
    ReadingLineFocus = 11,
}

public enum SyncSettingPersistenceScope
{
    GlobalPersistent = 0,
}

public enum ExcludedSettingPersistenceScope
{
    Site = 0,
    Session = 1,
    Private = 2,
}

public static class SyncAllowlist
{
    private static readonly FrozenSet<SyncDataCategory> Categories =
        new[] { SyncDataCategory.History, SyncDataCategory.Settings, SyncDataCategory.OpenTabs }
            .ToFrozenSet();

    private static readonly FrozenSet<SyncableSettingField> Settings =
        new[]
        {
            SyncableSettingField.ThemeMode,
            SyncableSettingField.TextScale,
            SyncableSettingField.DyslexiaFriendlyFontEnabled,
            SyncableSettingField.ReadingEnabled,
            SyncableSettingField.ReadingFontFamily,
            SyncableSettingField.ReadingFontScale,
            SyncableSettingField.ReadingLineHeight,
            SyncableSettingField.ReadingLetterSpacing,
            SyncableSettingField.ReadingWordSpacing,
            SyncableSettingField.ReadingContrast,
            SyncableSettingField.ReadingTint,
            SyncableSettingField.ReadingLineFocus,
        }.ToFrozenSet();

    private static readonly FrozenSet<SyncExcludedCategory> ExplicitExclusions =
        Enum.GetValues<SyncExcludedCategory>().ToFrozenSet();

    public static IReadOnlySet<SyncDataCategory> AllowedCategories => Categories;

    public static IReadOnlySet<SyncableSettingField> AllowedSettingFields => Settings;

    public static IReadOnlySet<SyncExcludedCategory> ExplicitlyExcludedData => ExplicitExclusions;

    public static bool IsAllowed(SyncDataCategory category) => Categories.Contains(category);

    public static bool IsAllowed(SyncableSettingField field) => Settings.Contains(field);
}

public readonly record struct SyncKeysetId(Guid Value)
{
    public bool IsDefined => Value != Guid.Empty;
}

public readonly record struct SyncEnvelopeId(Guid Value)
{
    public bool IsDefined => Value != Guid.Empty;
}

public readonly record struct SyncEntityId(Guid Value)
{
    public bool IsDefined => Value != Guid.Empty;
}

public readonly record struct SyncOperationId(Guid Value)
{
    public bool IsDefined => Value != Guid.Empty;
}

public readonly record struct SyncRecoveryAttemptId(Guid Value)
{
    public bool IsDefined => Value != Guid.Empty;
}

public readonly record struct RecoveryCodeLeaseId(Guid Value)
{
    public bool IsDefined => Value != Guid.Empty;
}

public sealed record ClientFence(
    OrbitNavigator.Contracts.Common.DeviceId DeviceId,
    long ClientGeneration,
    long MinimumAcceptedGeneration)
{
    public bool IsDefined =>
        !DeviceId.IsEmpty &&
        ClientGeneration >= 0 &&
        MinimumAcceptedGeneration >= 0 &&
        ClientGeneration >= MinimumAcceptedGeneration;
}

public static class SyncProtocol
{
    public const int CurrentProtocolVersion = 1;
    public const int CurrentSchemaVersion = 1;
    public const int NonceSizeBytes = 12;
    public const int AuthenticationTagSizeBytes = 16;
    public const int MaximumCiphertextSizeBytes = 4 * 1024 * 1024;
}

public sealed record SyncAvailability(
    bool LocalBrowsingAvailable,
    bool SyncAvailable,
    string? SyncUnavailableMessageKey);

public static class SyncAvailabilityPolicy
{
    public static SyncAvailability Evaluate(bool hasAccountAuthorization) =>
        hasAccountAuthorization
            ? new(true, true, null)
            : new(true, false, "sync.account.signed-out");
}
