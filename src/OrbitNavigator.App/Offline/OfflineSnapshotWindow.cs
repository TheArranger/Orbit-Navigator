using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

using OrbitNavigator.Foundation.Offline;

namespace OrbitNavigator.App.Offline;

/// <summary>
/// Native, image-only viewer for a stored viewport snapshot. It has no WebView,
/// navigation service, URI launcher, or network-capable content surface.
/// </summary>
public sealed class OfflineSnapshotWindow : Window
{
    public OfflineSnapshotWindow(Window owner, OfflineReadingContent content)
    {
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        ArgumentNullException.ThrowIfNull(content);
        var imageSource = DecodePng(content.PngBytes);
        Title = $"{content.Item.Title} — Offline copy — Orbit Navigator";
        Icon = OrbitProgramIdentity.CreateWindowIcon();
        Width = 920;
        Height = 720;
        MinWidth = 480;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var image = new Image
        {
            Source = imageSource,
            Stretch = System.Windows.Media.Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        AutomationProperties.SetName(image, $"Offline snapshot of {content.Item.Title}");
        AutomationProperties.SetHelpText(
            image,
            "Saved local image. This is not a live website and cannot make network requests.");
        var banner = new TextBlock
        {
            Text = $"Offline copy • Not live • Not synced\nSaved {content.Item.SavedAtUtc.ToLocalTime():g} from {content.Item.SourceAddress.Host}",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16, 12, 16, 12),
        };
        AutomationProperties.SetName(banner, "Offline copy status");
        var layout = new DockPanel();
        DockPanel.SetDock(banner, Dock.Top);
        layout.Children.Add(banner);
        layout.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = image,
        });
        Content = layout;
    }

    private static BitmapSource DecodePng(ReadOnlyMemory<byte> bytes)
    {
        using var stream = new MemoryStream(bytes.ToArray(), writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
