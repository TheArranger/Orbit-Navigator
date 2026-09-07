using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Privacy;
using OrbitNavigator.Contracts.Sync;
using Xunit;

namespace OrbitNavigator.Contracts.Tests.Sync;

public sealed class SyncDeletionAndRecoveryContractTests
{
    [Fact]
    public void EverySyncedDeletionRequiresItsLocalCounterpart()
    {
        foreach (var category in Enum.GetValues<SyncDataCategory>())
        {
            var missing = new DataDeletionSelection(
                new HashSet<LocalDataCategory>(),
                new HashSet<SyncDataCategory> { category });

            var validation = DataDeletionRules.ValidateSelection(missing);

            Assert.False(validation.IsValid);
            Assert.Contains(validation.Issues, issue => issue.Code == "deletion.local_counterpart");
        }

        var complete = new DataDeletionSelection(
            new HashSet<LocalDataCategory>
            {
                LocalDataCategory.History,
                LocalDataCategory.Settings,
                LocalDataCategory.OpenTabs,
            },
            new HashSet<SyncDataCategory>(Enum.GetValues<SyncDataCategory>()));

        Assert.True(DataDeletionRules.ValidateSelection(complete).IsValid);
    }

    [Fact]
    public void LocalDeletionCategoriesIncludeFormEntriesAndSiteSessionsButNeverFiles()
    {
        Assert.Contains(LocalDataCategory.FormEntriesAutofill, Enum.GetValues<LocalDataCategory>());
        Assert.Contains(LocalDataCategory.SiteSessionsAuthTokens, Enum.GetValues<LocalDataCategory>());
        Assert.DoesNotContain(Enum.GetNames<LocalDataCategory>(), name =>
            name.Equals("DownloadedFiles", StringComparison.OrdinalIgnoreCase));
        Assert.True(DataDeletionRules.PreservesDownloadedFiles);
        Assert.Single(Enum.GetValues<DownloadedFilesDisposition>());
        Assert.Equal(DownloadedFilesDisposition.Preserved, Enum.GetValues<DownloadedFilesDisposition>()[0]);
    }

    [Fact]
    public void SyncedDeletionRequiresAuthorizationAndFence()
    {
        var browsing = Browsing();
        var operation = new SyncOperationId(Guid.NewGuid());
        var context = SyncOperationContext.Authorize(browsing, operation).Value!;
        var selection = new DataDeletionSelection(
            new HashSet<LocalDataCategory> { LocalDataCategory.History },
            new HashSet<SyncDataCategory> { SyncDataCategory.History });
        var request = new DataDeletionRequest(operation, selection, null, null);

        var validation = DataDeletionRules.ValidateRequest(context, request);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Issues, issue => issue.Code == "deletion.authorization");
        Assert.Contains(validation.Issues, issue => issue.Code == "deletion.fence");
    }

    [Fact]
    public void EncryptedResetMustCreateNewKeysetAndAdvanceFence()
    {
        var previous = new SyncKeysetId(Guid.NewGuid());
        var operation = new SyncOperationId(Guid.NewGuid());
        var privacy = Browsing().Privacy;
        var now = DateTimeOffset.UtcNow;
        var authorization = SensitiveActionAuthorizationToken.Create(
            new SensitiveActionAuthorizationTokenId(Guid.NewGuid()),
            privacy,
            SensitiveActionKind.ResetEncryptedSync,
            now,
            now.AddMinutes(1)).Value!;
        var request = new EncryptedSyncResetRequest(previous, 5, operation, authorization);
        var receipt = new EncryptedSyncResetReceipt(
            operation,
            previous,
            new SyncKeysetId(Guid.NewGuid()),
            new ClientFence(new DeviceId(Guid.NewGuid()), 6, 6),
            now);

        Assert.True(DataDeletionRules.ValidateReset(request, receipt).IsValid);
        Assert.NotEqual(receipt.PreviousKeysetId, receipt.NewKeysetId);
        Assert.True(receipt.Fence.ClientGeneration > request.PreviousGeneration);
    }

    [Fact]
    public void RecoveryBoundaryContainsNoRawEmailOrRecoveryCodePayload()
    {
        var emailProperties = typeof(EmailAccountRestorationRequest).GetProperties();
        Assert.Single(emailProperties);
        Assert.Equal(typeof(EmailVerificationProofHandle), emailProperties[0].PropertyType);

        var recoveryProperties = typeof(RecoveryCodeKeyRestorationRequest).GetProperties();
        Assert.DoesNotContain(recoveryProperties, property =>
            property.PropertyType == typeof(string) ||
            property.PropertyType == typeof(char[]) ||
            property.Name.Equals("RecoveryCode", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(recoveryProperties, property =>
            property.PropertyType == typeof(RecoveryCodeLeaseId));
        Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(SensitiveRecoveryCodeBuffer)));
    }

    [Fact]
    public void WrappedKeysetCarriesKeysetGenerationKdfSaltAndParameters()
    {
        var kdf = new SyncKdfParameters(
            SyncKdfAlgorithm.Argon2id,
            new byte[] { 1, 2, 3, 4 },
            3,
            65_536,
            2,
            32);
        var wrapped = new WrappedSyncKeyset(
            new SyncKeysetId(Guid.NewGuid()),
            7,
            SyncKeyWrapMethod.RecoveryCode,
            kdf,
            new byte[12],
            new byte[] { 8 },
            new byte[16]);

        Assert.True(wrapped.KeysetId.IsDefined);
        Assert.Equal(7, wrapped.Generation);
        Assert.False(wrapped.Kdf.Salt.IsEmpty);
        Assert.True(wrapped.Kdf.MemoryKiB > 0);
        Assert.True(wrapped.Kdf.DerivedKeySizeBytes > 0);
    }

    private static BrowsingContext Browsing() =>
        new(
            new PrivacyContext(
                new ProfileId(Guid.NewGuid()),
                new BrowserSessionId(Guid.NewGuid()),
                BrowserProfileMode.Normal),
            new BrowserWindowId(Guid.NewGuid()),
            new BrowserTabId(Guid.NewGuid()),
            null);
}
