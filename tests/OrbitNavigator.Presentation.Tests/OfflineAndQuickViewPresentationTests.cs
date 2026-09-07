using OrbitNavigator.Presentation.Offline;
using OrbitNavigator.Presentation.QuickView;
using Xunit;

namespace OrbitNavigator.Presentation.Tests;

public sealed class OfflineAndQuickViewPresentationTests
{
    [Fact]
    public void PrivateOfflineCatalogCannotExposeOrSaveNormalProfilePages()
    {
        var item = new OfflineReadingItemPresentation(
            new(Guid.NewGuid()),
            "Saved research",
            new Uri("https://example.test/research"),
            DateTimeOffset.UtcNow,
            2048);

        var state = new OfflineReadingCatalogPresentation(
            4,
            true,
            true,
            "Current page",
            new Uri("https://example.test/"),
            false,
            string.Empty,
            [item]).Validate();

        Assert.True(state.IsPrivate);
        Assert.False(state.CanSaveCurrentPage);
        Assert.Empty(state.Items);
        Assert.Contains("private", state.SafeStatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OfflineItemsRequireHttpsOrHttpAndExposeSavedMetadata()
    {
        var savedAt = DateTimeOffset.UtcNow;
        var item = new OfflineReadingItemPresentation(
            new(Guid.NewGuid()),
            "  Orbit guide  ",
            new Uri("https://guide.test/page"),
            savedAt,
            1_048_576).Validate();

        Assert.Equal("Orbit guide", item.Title);
        Assert.Equal(savedAt, item.SavedAtUtc);
        Assert.Equal(1_048_576, item.SizeBytes);
        Assert.Throws<ArgumentException>(() => (item with
        {
            SourceAddress = new Uri("file:///private/offline.html"),
        }).Validate());
    }

    [Fact]
    public void QuickViewIsNormalSiteOnlyAndPrivateFailsClosed()
    {
        var bookmark = new QuickViewBookmarkPresentation(
            "docs",
            "Docs",
            new Uri("https://docs.test/")).Validate();
        var normal = new QuickViewPresentation(
            3,
            false,
            true,
            QuickViewHostState.Ready,
            "Quick View",
            null,
            QuickViewStateTransferCapability.AddressReloadOnly,
            string.Empty,
            [bookmark]).Validate();
        Assert.True(normal.CanShowAnchor);
        Assert.Single(normal.Bookmarks);

        var privateState = (normal with { IsPrivate = true }).Validate();
        Assert.False(privateState.CanShowAnchor);
        Assert.Equal(QuickViewHostState.Unavailable, privateState.HostState);
        Assert.Empty(privateState.Bookmarks);
    }

    [Fact]
    public void QuickViewActionsCarryExpectedRevisionWithoutPageSecrets()
    {
        QuickViewAction open = new OpenQuickViewAction(Guid.NewGuid(), 8, "privacy browser");
        QuickViewAction expand = new ExpandQuickViewToTabAction(Guid.NewGuid(), 8, true);
        QuickViewAction resize = new ResizeQuickViewAction(Guid.NewGuid(), 8, .4, .5);

        Assert.Equal(8, open.ExpectedRevision);
        Assert.Equal("privacy browser", Assert.IsType<OpenQuickViewAction>(open).Query);
        Assert.True(Assert.IsType<ExpandQuickViewToTabAction>(expand).PreferStateTransfer);
        Assert.Equal(.4, Assert.IsType<ResizeQuickViewAction>(resize).WidthRatio);
    }
}
