using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Presentation.Workspace;
using OrbitNavigator.Presentation.Wpf;
using Xunit;

namespace OrbitNavigator.Presentation.Wpf.Tests;

public sealed class OrbitDialogVisualTests
{
    [Fact]
    public void WorkspaceEditorAddsRepeatableAddressAndNicknameRows()
    {
        StaTest.Run(() =>
        {
            var dialog = new WorkspacePresetEditorDialog(null);
            StaTest.Prepare((FrameworkElement)dialog.Content, 760, 680);

            Assert.Single(
                StaTest.Descendants(dialog).OfType<TextBox>(),
                box => AutomationProperties.GetName(box) == "Tab address or URL");
            Assert.Single(
                StaTest.Descendants(dialog).OfType<TextBox>(),
                box => AutomationProperties.GetName(box) == "Tab nickname");

            StaTest.FindByAutomationName<Button>(dialog, "Add another workspace tab")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal(2, StaTest.Descendants(dialog).OfType<TextBox>().Count(
                box => AutomationProperties.GetName(box) == "Tab address or URL"));
            Assert.Equal(2, StaTest.Descendants(dialog).OfType<TextBox>().Count(
                box => AutomationProperties.GetName(box) == "Tab nickname"));
        });
    }

    [Fact]
    public void PrivateWorkspaceEditorKeepsContentReadableAndDisablesMutation()
    {
        StaTest.Run(() =>
        {
            var dialog = new WorkspacePresetEditorDialog(null, canModify: false);
            StaTest.Prepare((FrameworkElement)dialog.Content, 760, 680);

            Assert.True(StaTest.FindByAutomationName<TextBox>(dialog, "Workspace name").IsReadOnly);
            Assert.False(StaTest.FindByAutomationName<Button>(dialog, "Save workspace").IsEnabled);
            Assert.Contains(
                StaTest.Descendants(dialog).OfType<TextBlock>(),
                text => text.Text.Contains("private browsing", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public void BookmarkManagerValidatesDraftAndRequiresDeleteConfirmation()
    {
        StaTest.Run(() =>
        {
            var profile = new ProfileId(Guid.NewGuid());
            var bookmark = new BookmarkEntry(
                new BookmarkId(profile, Guid.NewGuid()),
                new Uri("https://saved.example.test"),
                "Saved",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);
            var dialog = new BookmarkManagerDialog(Context(profile, BrowserProfileMode.Normal), [bookmark], canEditBookmarks: true);
            BookmarkSaveDraft? draft = null;
            BookmarkEntry? deleted = null;
            dialog.SaveRequested += (_, args) => draft = args.Draft;
            dialog.DeleteRequested += (_, args) => deleted = args.Bookmark;
            StaTest.Prepare((FrameworkElement)dialog.Content, 720, 650);

            var favoriteStars = StaTest.Descendants(dialog).OfType<OrbitEmberStar>()
                .Where(star => star.Kind == OrbitEmberStarKind.Favorite)
                .ToArray();
            Assert.True(favoriteStars.Length >= 2);
            Assert.All(favoriteStars, star =>
            {
                Assert.True(star.IsActive);
                Assert.False(star.MotionEnabled);
            });

            StaTest.FindByAutomationName<Button>(dialog, "Add bookmark")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            StaTest.FindByAutomationName<TextBox>(dialog, "Bookmark address or URL").Text = "https://new.example.test/path";
            StaTest.FindByAutomationName<TextBox>(dialog, "Bookmark title").Text = "New place";
            StaTest.FindByAutomationName<Button>(dialog, "Save bookmark")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("New place", draft?.Title);
            Assert.Equal("new.example.test", draft?.Target.Host);

            var firstDelete = StaTest.FindByAutomationName<Button>(dialog, "Delete selected bookmark");
            firstDelete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Null(deleted);
            StaTest.FindByAutomationName<Button>(dialog, "Confirm delete bookmark Saved")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Same(bookmark, deleted);
        });
    }

    [Fact]
    public void PrivateBookmarkManagerAllowsOpenButDisablesEveryMutation()
    {
        StaTest.Run(() =>
        {
            var profile = new ProfileId(Guid.NewGuid());
            var bookmark = new BookmarkEntry(
                new BookmarkId(profile, Guid.NewGuid()),
                new Uri("https://read.example.test"),
                "Readable",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);
            var dialog = new BookmarkManagerDialog(Context(profile, BrowserProfileMode.Private), [bookmark], canEditBookmarks: true);
            StaTest.Prepare((FrameworkElement)dialog.Content, 720, 650);

            Assert.True(StaTest.FindByAutomationName<Button>(dialog, "Open selected bookmark").IsEnabled);
            Assert.False(StaTest.FindByAutomationName<Button>(dialog, "Add bookmark").IsEnabled);
            Assert.False(StaTest.FindByAutomationName<Button>(dialog, "Edit selected bookmark").IsEnabled);
            Assert.False(StaTest.FindByAutomationName<Button>(dialog, "Delete selected bookmark").IsEnabled);
        });
    }

    private static PrivacyContext Context(ProfileId profile, BrowserProfileMode mode) =>
        new(profile, new BrowserSessionId(Guid.NewGuid()), mode);
}
