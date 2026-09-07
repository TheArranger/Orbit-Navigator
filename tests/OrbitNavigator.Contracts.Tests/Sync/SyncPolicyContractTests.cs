using OrbitNavigator.Contracts.Sync;
using Xunit;

namespace OrbitNavigator.Contracts.Tests.Sync;

public sealed class SyncPolicyContractTests
{
    [Fact]
    public void CategoryAllowlistIsExactlyHistorySettingsAndOpenTabs()
    {
        Assert.Equal(
            new[] { SyncDataCategory.History, SyncDataCategory.Settings, SyncDataCategory.OpenTabs },
            SyncAllowlist.AllowedCategories.OrderBy(value => value));
        Assert.Equal(3, Enum.GetValues<SyncDataCategory>().Length);
    }

    [Fact]
    public void ExplicitExclusionsContainEveryApprovedSensitiveCategory()
    {
        var expected = new[]
        {
            SyncExcludedCategory.Bookmarks,
            SyncExcludedCategory.Downloads,
            SyncExcludedCategory.ClipboardShelf,
            SyncExcludedCategory.PrivateBrowsing,
            SyncExcludedCategory.Cookies,
            SyncExcludedCategory.SavedPasswords,
            SyncExcludedCategory.FormEntriesAutofill,
            SyncExcludedCategory.SiteSessionsAuthTokens,
            SyncExcludedCategory.PermissionRules,
            SyncExcludedCategory.SiteProtectionExceptions,
            SyncExcludedCategory.EncryptionKeys,
            SyncExcludedCategory.RecoveryMaterial,
            SyncExcludedCategory.PerSiteReadingPreferences,
            SyncExcludedCategory.SessionReadingPreferences,
            SyncExcludedCategory.LocalDiagnostics,
            SyncExcludedCategory.LocalOrbitPasswordVault,
        };

        Assert.Equal(expected, SyncAllowlist.ExplicitlyExcludedData.OrderBy(value => value));
        Assert.Equal(expected.Length, Enum.GetValues<SyncExcludedCategory>().Length);
    }

    [Fact]
    public void SettingsAllowlistIsNamedAndExact()
    {
        var expected = new[]
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
        };

        Assert.Equal(expected, SyncAllowlist.AllowedSettingFields.OrderBy(value => value));
        Assert.Equal(expected.Length, Enum.GetValues<SyncableSettingField>().Length);
    }

    [Fact]
    public void UnrecognizedPayloadTypeIsRejectedEvenWhenItClaimsAllowedCategory()
    {
        var payload = new UnrecognizedSyncRecord(
            new SyncEntityId(Guid.NewGuid()),
            1,
            DateTimeOffset.UtcNow);

        var validation = SyncContractRules.ValidateRecord(payload);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Issues, issue => issue.Code == "record.type");
    }

    [Fact]
    public void SignedOutBrowsingRemainsLocalAndSyncInactive()
    {
        var availability = SyncAvailabilityPolicy.Evaluate(hasAccountAuthorization: false);

        Assert.True(availability.LocalBrowsingAvailable);
        Assert.False(availability.SyncAvailable);
        Assert.NotNull(availability.SyncUnavailableMessageKey);
    }

    private sealed record UnrecognizedSyncRecord(
        SyncEntityId EntityId,
        long Revision,
        DateTimeOffset ModifiedAtUtc)
        : SyncRecordPayload(EntityId, Revision, ModifiedAtUtc)
    {
        public override SyncDataCategory Category => SyncDataCategory.History;
    }
}
