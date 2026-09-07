using System.Runtime.ExceptionServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace OrbitNavigator.App.Tests;

public sealed class OrbitProgramIdentityTests
{
    [Fact]
    public void NormalWindowIdentityLoadsPackagedApprovedArtwork() => RunSta(() =>
    {
        using var temp = new IdentityTempDirectory();
        var assetPath = Path.Combine(
            temp.Path,
            "assets",
            "branding",
            "orbit-navigator-program-logo-v2.png");
        Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
        WriteTransparentTestPng(assetPath);

        var icon = OrbitProgramIdentity.CreateWindowIcon(highContrast: false, temp.Path);

        var bitmap = Assert.IsType<BitmapImage>(icon);
        Assert.Equal(2, bitmap.PixelWidth);
        Assert.Equal(2, bitmap.PixelHeight);
        Assert.True(bitmap.IsFrozen);
    });

    [Fact]
    public void HighContrastIgnoresRasterAndUsesFrozenMonochromeFallback() => RunSta(() =>
    {
        using var temp = new IdentityTempDirectory();

        var icon = OrbitProgramIdentity.CreateWindowIcon(highContrast: true, temp.Path);

        Assert.IsNotType<BitmapImage>(icon);
        Assert.True(icon.IsFrozen);
    });

    [Fact]
    public void MissingArtworkFailsSafelyToExistingVectorIdentity() => RunSta(() =>
    {
        using var temp = new IdentityTempDirectory();

        var icon = OrbitProgramIdentity.CreateWindowIcon(highContrast: false, temp.Path);

        Assert.IsNotType<BitmapImage>(icon);
        Assert.True(icon.IsFrozen);
    });

    private static void WriteTransparentTestPng(string path)
    {
        byte[] pixels =
        [
            0, 0, 0, 0,
            0, 128, 255, 255,
            128, 255, 0, 255,
            255, 255, 255, 128,
        ];
        var source = BitmapSource.Create(
            2,
            2,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            8);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private sealed class IdentityTempDirectory : IDisposable
    {
        private static readonly string AllowedRoot = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OrbitNavigator.App.Tests"));

        public IdentityTempDirectory()
        {
            Path = System.IO.Path.Combine(AllowedRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            var fullPath = System.IO.Path.GetFullPath(Path);
            if (fullPath.StartsWith(
                    AllowedRoot + System.IO.Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, recursive: true);
            }
        }
    }
}
