using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Sync;
using OrbitNavigator.Contracts.Updates;
using Xunit;

namespace OrbitNavigator.Contracts.Tests.Browser;

public sealed class BrowserDataFacadeContractTests
{
    [Fact]
    public void EveryFacadeIntentCarriesProfileSessionOrBrowsingContext()
    {
        var intentTypes = new[]
        {
            typeof(BookmarkQuery),
            typeof(AddBookmarkIntent),
            typeof(RemoveBookmarkIntent),
            typeof(HistoryQuery),
            typeof(RecordHistoryVisitIntent),
            typeof(ClearHistoryIntent),
            typeof(DownloadsQuery),
            typeof(DownloadRecordIntent),
            typeof(ClearDownloadRecordsIntent),
            typeof(UpdateBrowserSettingsIntent),
            typeof(CreatePrivateWindowIntent),
            typeof(ClosePrivateWindowIntent),
            typeof(ReplaceTabGroupMetadataIntent),
        };

        Assert.All(intentTypes, type => Assert.Contains(
            type.GetProperties(),
            property => property.PropertyType == typeof(PrivacyContext) ||
                property.PropertyType == typeof(BrowsingContext)));
    }

    [Fact]
    public void DownloadRecordClearingCannotRepresentDownloadedFileDeletion()
    {
        var receipt = new ClearDownloadRecordsReceipt(
            4,
            DownloadedFilesDisposition.Preserved);

        Assert.Equal(DownloadedFilesDisposition.Preserved, receipt.DownloadedFiles);
        Assert.DoesNotContain(
            typeof(ClearDownloadRecordsIntent).GetProperties(),
            property => property.Name.Contains("File", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BrowserCoreSettingsKeepPolicyAndReadingPreferencesOutOfFoundationFacade()
    {
        var properties = typeof(BrowserCoreSettings).GetProperties();

        Assert.Contains(properties, property => property.Name == nameof(BrowserCoreSettings.UpdatePreference));
        Assert.DoesNotContain(properties, property =>
            property.Name.Contains("Protection", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Permission", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Reading", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TabGroupReplacementIsRevisionedAndWindowScoped()
    {
        var context = Privacy();
        var window = new BrowserWindowId(Guid.NewGuid());
        var revision = new TabGroupCatalogRevision(Guid.NewGuid());
        var group = new TabGroupMetadata(
            new BrowserTabGroupId(Guid.NewGuid()),
            "Research",
            true,
            [new BrowserTabId(Guid.NewGuid())],
            DateTimeOffset.UtcNow);
        var intent = new ReplaceTabGroupMetadataIntent(
            context,
            window,
            revision,
            [group]);

        Assert.Equal(context, intent.Context);
        Assert.Equal(window, intent.WindowId);
        Assert.Equal(revision, intent.ExpectedRevision);
        Assert.Single(intent.Groups);
    }

    [Fact]
    public void UpdatePreferenceDefaultsToNotifyOnlyAtContractBoundary()
    {
        var settings = new BrowserCoreSettings("duckduckgo", true, true, default);

        Assert.Equal(UpdatePreference.NotifyOnly, settings.UpdatePreference);
    }

    [Fact]
    public void BookmarkNoteIsOptionalUiSafeMetadataOnEntryAndWriteIntent()
    {
        var context = new BrowsingContext(
            Privacy(),
            new BrowserWindowId(Guid.NewGuid()),
            new BrowserTabId(Guid.NewGuid()),
            null);
        var intent = new AddBookmarkIntent(
            context,
            new Uri("https://example.com/"),
            "Example")
        {
            Note = "Read after lunch",
        };
        var entry = new BookmarkEntry(
            new BookmarkId(context.Privacy.ProfileId, Guid.NewGuid()),
            intent.Target,
            intent.Title,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow)
        {
            Note = intent.Note,
        };

        Assert.Equal("Read after lunch", intent.Note);
        Assert.Equal(intent.Note, entry.Note);
        Assert.DoesNotContain("token", nameof(BookmarkEntry.Note), StringComparison.OrdinalIgnoreCase);
    }

    private static PrivacyContext Privacy() =>
        new(
            new ProfileId(Guid.NewGuid()),
            new BrowserSessionId(Guid.NewGuid()),
            BrowserProfileMode.Normal);
}
