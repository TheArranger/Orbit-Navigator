using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

using OrbitNavigator.App.Offline;
using OrbitNavigator.Foundation.Offline;

using Xunit;

namespace OrbitNavigator.App.Tests.Offline;

public sealed class OfflineSnapshotWindowTests
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public void ViewerContainsOnlyNativeNonNetworkImageSurface() => RunSta(() =>
    {
        var owner = new Window
        {
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            Width = 1,
            Height = 1,
            Left = -10000,
            Top = -10000,
        };
        owner.Show();
        owner.Hide();
        var content = new OfflineReadingContent(
            new OfflineReadingItem(
                new OfflineReadingItemId(Guid.NewGuid()),
                "Saved page",
                new Uri("https://example.test/"),
                DateTimeOffset.UtcNow,
                OnePixelPng.Length),
            OnePixelPng);
        var window = new OfflineSnapshotWindow(owner, content);
        window.Show();
        window.UpdateLayout();

        var descendants = Descendants(window.Content as DependencyObject).ToArray();
        Assert.Contains(descendants, element => element is Image);
        Assert.DoesNotContain(descendants, element => element is HwndHost);
        Assert.DoesNotContain(descendants, element => element.GetType().Name.Contains("WebView", StringComparison.Ordinal));
        window.Close();
        owner.Close();
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject? root)
    {
        if (root is null) yield break;
        yield return root;
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            foreach (var child in Descendants(System.Windows.Media.VisualTreeHelper.GetChild(root, index)))
            {
                yield return child;
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
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
