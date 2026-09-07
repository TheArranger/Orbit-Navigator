using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OrbitNavigator.Presentation.Wpf;

namespace OrbitNavigator.App;

public static class OrbitProgramIdentity
{
    public static ImageSource CreateWindowIcon() =>
        CreateWindowIcon(SystemParameters.HighContrast, AppContext.BaseDirectory);

    public static ImageSource CreateWindowIcon(bool highContrast, string payloadRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadRoot);
        if (highContrast)
        {
            return OrbitNavigatorIdentity.CreateWindowIcon(monochrome: true);
        }

        try
        {
            var root = Path.GetFullPath(payloadRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var relativePath = OrbitNewTabLogo.ProgramLogoRelativePath
                .Replace('/', Path.DirectorySeparatorChar);
            var artworkPath = Path.GetFullPath(Path.Combine(root, relativePath));
            if (!artworkPath.StartsWith(
                    root + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(artworkPath))
            {
                return OrbitNavigatorIdentity.CreateWindowIcon(monochrome: false);
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            image.UriSource = new Uri(artworkPath, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image.PixelWidth > 0 && image.PixelHeight > 0
                ? image
                : OrbitNavigatorIdentity.CreateWindowIcon(monochrome: false);
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or NotSupportedException)
        {
            return OrbitNavigatorIdentity.CreateWindowIcon(monochrome: false);
        }
    }
}
