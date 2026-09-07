using System.Runtime.ExceptionServices;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Foundation.Browser;
using OrbitNavigator.Foundation.Profiles;
using OrbitNavigator.Presentation.Wpf;
using OrbitNavigator.Presentation.Workspace;
using Xunit;

namespace OrbitNavigator.App.Tests;

public sealed class BookmarkNoteProjectionTests
{
    [Fact]
    public async Task CreatedNoteSurvivesFacadeReloadAndAppearsInHoverUiaHelpText()
    {
        var root = Path.Combine(Path.GetTempPath(), "orbit-bookmark-note-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var profile = new ProfileId(Guid.NewGuid());
            var privacy = new PrivacyContext(
                profile,
                new BrowserSessionId(Guid.NewGuid()),
                BrowserProfileMode.Normal);
            var context = new BrowsingContext(
                privacy,
                new BrowserWindowId(Guid.NewGuid()),
                new BrowserTabId(Guid.NewGuid()),
                null);
            var clock = new FixedClock(new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero));
            var storage = new FileProfileStorage(root);
            var created = await new BookmarksFacade(storage, clock).AddAsync(new(
                context,
                new Uri("https://my-orbit.snap-it.cc/"),
                "Durable bookmark")
            {
                Note = "Local reading note",
            });
            Assert.True(created.IsSuccess);

            var reloaded = await new BookmarksFacade(storage, clock).QueryAsync(new(
                privacy,
                null,
                10));
            var bookmark = Assert.Single(reloaded.Value!);
            Assert.Equal("Local reading note", bookmark.Note);

            RunSta(() =>
            {
                var page = new NewTabPageControl { ReducedMotion = true };
                var mapper = typeof(FoundationWindow).GetMethod(
                    "BookmarkNotes",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert.NotNull(mapper);
                var projectedNotes = Assert.IsAssignableFrom<IReadOnlyDictionary<BookmarkId, string>>(
                    mapper!.Invoke(null, [reloaded.Value!]));
                page.ApplyWorkspaceData(new NewTabWorkspaceData([bookmark], [], true)
                {
                    BookmarkNotes = projectedNotes,
                });
                var window = new Window
                {
                    Content = page,
                    Width = 1200,
                    Height = 800,
                    ShowInTaskbar = false,
                };
                window.Show();
                try
                {
                    window.UpdateLayout();
                    var item = Descendants(page).OfType<Button>().Single(button =>
                        AutomationProperties.GetName(button) == "Open Durable bookmark from bookmarks hub");
                    item.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
                    {
                        RoutedEvent = Mouse.MouseEnterEvent,
                        Source = item,
                    });
                    window.UpdateLayout();

                    var detail = Descendants(page).OfType<Border>().Single(border =>
                        AutomationProperties.GetName(border) == "Durable bookmark details");
                    var peer = UIElementAutomationPeer.CreatePeerForElement(detail);
                    Assert.NotNull(peer);
                    Assert.Equal("Bookmark details", peer.GetItemStatus());
                    Assert.Contains("https://my-orbit.snap-it.cc/", peer.GetHelpText());
                    Assert.Contains("Local reading note", peer.GetHelpText());
                    Assert.False(detail.IsHitTestVisible);
                    Assert.False(detail.Focusable);
                }
                finally
                {
                    window.Close();
                }
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
