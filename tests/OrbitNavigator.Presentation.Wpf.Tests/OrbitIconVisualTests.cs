using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;

using OrbitNavigator.Presentation.Wpf;
using Xunit;

namespace OrbitNavigator.Presentation.Wpf.Tests;

public sealed class OrbitIconVisualTests
{
    [Fact]
    public void EveryToolbarGlyphRendersAtCommonWindowsScaleFactors()
    {
        StaTest.Run(() =>
        {
            foreach (var kind in Enum.GetValues<OrbitIconKind>())
            foreach (var dpi in new[] { 96d, 120d, 144d, 192d })
            {
                var icon = new OrbitIcon
                {
                    Kind = kind,
                    Width = 24,
                    Height = 24,
                    Stroke = Brushes.White,
                };
                StaTest.Prepare(icon, 24, 24);
                var pixelsWide = (int)Math.Ceiling(24 * dpi / 96d);
                var bitmap = new RenderTargetBitmap(pixelsWide, pixelsWide, dpi, dpi, PixelFormats.Pbgra32);
                bitmap.Render(icon);
                var pixels = new byte[pixelsWide * pixelsWide * 4];
                bitmap.CopyPixels(pixels, pixelsWide * 4, 0);

                Assert.True(
                    pixels.Where((_, index) => index % 4 == 3).Any(alpha => alpha > 0),
                    $"{kind} did not render at {dpi} DPI.");
            }
        });
    }

    [Fact]
    public void FilledStatusIconsKeepTheirVectorOutline()
    {
        StaTest.Run(() =>
        {
            foreach (var kind in new[] { OrbitIconKind.Bookmark, OrbitIconKind.Shield, OrbitIconKind.Stop })
            {
                var icon = new OrbitIcon { Kind = kind, Width = 24, Height = 24, IsFilled = true };
                StaTest.Prepare(icon, 24, 24);
                Assert.True(icon.IsFilled);
                Assert.InRange(icon.StrokeThickness, 1.75, 2.0);
            }
        });
    }

    [Fact]
    public void NavigationPathApplicationMarkRemainsLegibleAtRequiredIconSizes()
    {
        StaTest.Run(() =>
        {
            Assert.Equal(new[] { 16, 20, 24, 32, 40, 48, 64, 256 }, OrbitNavigatorIdentity.RequiredIconPixelSizes);
            foreach (var monochrome in new[] { false, true })
            foreach (var size in new[] { 16, 24, 32, 48 })
            {
                var image = new Image
                {
                    Source = OrbitNavigatorIdentity.CreateWindowIcon(monochrome),
                    Width = size,
                    Height = size,
                };
                StaTest.Prepare(image, size, size);
                var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(image);
                var pixels = new byte[size * size * 4];
                bitmap.CopyPixels(pixels, size * 4, 0);
                var visiblePixels = pixels.Where((_, index) => index % 4 == 3).Count(alpha => alpha > 24);

                Assert.True(visiblePixels >= Math.Max(12, size), $"Identity mark was too faint at {size}px.");
            }
        });
    }
}
