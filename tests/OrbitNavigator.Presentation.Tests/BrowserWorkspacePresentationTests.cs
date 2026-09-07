using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Presentation.Workspace;
using Xunit;

namespace OrbitNavigator.Presentation.Tests;

public sealed class BrowserWorkspacePresentationTests
{
    [Fact]
    public void DefaultWorkspacePreviewRevealIsHover()
    {
        Assert.Equal(WorkspacePreviewRevealMode.Hover,
            BrowserWorkspacePreferences.Default.WorkspacePreviewReveal);
    }

    [Theory]
    [InlineData(TabStripPlacement.Top)]
    [InlineData(TabStripPlacement.Left)]
    [InlineData(TabStripPlacement.Right)]
    public void EverySupportedPlacementProducesValidPresentationPreferences(TabStripPlacement placement)
    {
        var preferences = new BrowserWorkspacePreferences(placement, true, true);

        Assert.Same(preferences, preferences.Validate());
    }

    [Fact]
    public void WorkspacePresetPreservesNamedOrderedHttpTargets()
    {
        var profileId = new ProfileId(Guid.NewGuid());
        var preset = new WorkspacePresetPresentation(
            new WorkspacePresetPresentationId(profileId, Guid.NewGuid()),
            "Morning round",
            "Daily",
            [
                new(new Uri("https://mail.example.test"), "Mail"),
                new(new Uri("https://calendar.example.test"), "Calendar"),
            ]);

        Assert.Same(preset, preset.Validate());
        Assert.Equal(["Mail", "Calendar"], preset.Tabs.Select(tab => tab.DisplayTitle));
    }

    [Fact]
    public void WorkspacePresetRejectsNonWebTargets()
    {
        var preset = new WorkspacePresetPresentation(
            new WorkspacePresetPresentationId(new ProfileId(Guid.NewGuid()), Guid.NewGuid()),
            "Unsafe",
            null,
            [new(new Uri("file:///private.txt"), "Local file")]);

        Assert.Throws<ArgumentException>(() => preset.Validate());
    }

    [Fact]
    public void ExtendedWorkspacePreferencesAreValidatedWithoutBreakingV12Constructor()
    {
        var preferences = BrowserWorkspacePreferences.Default with
        {
            NewTabMode = NewTabVisualMode.Basic,
            WorkspacePreviewReveal = WorkspacePreviewRevealMode.Click,
            AffiliatedRailPlacement = AffiliatedRailPlacement.Right,
            ShowAffiliatedRail = false,
        };

        Assert.Same(preferences, preferences.Validate());
        Assert.Equal(NewTabVisualMode.Basic, preferences.NewTabMode);
        Assert.Equal(WorkspacePreviewRevealMode.Click, preferences.WorkspacePreviewReveal);
        Assert.False(preferences.ShowAffiliatedRail);
    }

    [Fact]
    public void WorkspaceCarriesColorNoteArtworkAndFirstSiteWithoutRawLocalPath()
    {
        var profile = new ProfileId(Guid.NewGuid());
        var tabs = new[]
        {
            new WorkspacePresetTabPresentation(new Uri("https://one.example.test/"), "One"),
            new WorkspacePresetTabPresentation(new Uri("https://two.example.test/"), "Two") { Note = "Start here" },
        };
        var preset = new WorkspacePresetPresentation(
            new WorkspacePresetPresentationId(profile, Guid.NewGuid()), "Route", "Route", tabs)
        {
            ColorToken = "Violet",
            Note = "A saved group",
            FirstTabIndex = 1,
            Artwork = new WorkspaceArtworkPresentation(
                WorkspaceArtworkKind.LocalImage, null, "asset:7f1", "Purple route artwork"),
        }.Validate();

        Assert.Equal(1, preset.FirstTabIndex);
        Assert.Equal("Violet", preset.ColorToken);
        Assert.Equal("asset:7f1", preset.Artwork.LocalAssetId);
        Assert.DoesNotContain("\\", preset.Artwork.LocalAssetId);
    }

    [Fact]
    public void BookmarkNotesRemainOptionalMetadataOnSingleSavedSites()
    {
        var profile = new ProfileId(Guid.NewGuid());
        var bookmark = new BookmarkEntry(
            new BookmarkId(profile, Guid.NewGuid()),
            new Uri("https://notes.example.test/"),
            "Notes",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        var data = new NewTabWorkspaceData([bookmark], [], true)
        {
            BookmarkNotes = new Dictionary<BookmarkId, string> { [bookmark.Id] = "Read after lunch" },
        };

        Assert.Same(data, data.Validate());
        Assert.Single(data.Bookmarks);
        Assert.Equal("Read after lunch", data.BookmarkNotes[bookmark.Id]);
    }
}
