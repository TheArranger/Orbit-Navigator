using System.Windows.Media;
using System.Windows.Media.Imaging;

using OrbitNavigator.Presentation.Wpf;
using Xunit;

namespace OrbitNavigator.Presentation.Wpf.Tests;

public sealed class OrbitPlacementGlyphVisualTests
{
    [Theory]
    [InlineData(OrbitControllerGlyphKind.PlacementTop, "top")]
    [InlineData(OrbitControllerGlyphKind.PlacementLeft, "left")]
    [InlineData(OrbitControllerGlyphKind.PlacementRight, "right")]
    public void PlacementGlyphUsesTransparentWindowAndDirectionalRail(
        OrbitControllerGlyphKind kind,
        string expectedRail)
    {
        StaTest.Run(() =>
        {
            var glyph = new OrbitControllerGlyph
            {
                Kind = kind,
                Stroke = Brushes.White,
                Accent = Brushes.Red,
                Width = 24,
                Height = 24,
            };
            StaTest.Prepare(glyph, 24, 24);
            var bitmap = new RenderTargetBitmap(24, 24, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(glyph);
            var pixels = new byte[24 * 24 * 4];
            bitmap.CopyPixels(pixels, 24 * 4, 0);

            Assert.Equal(0, pixels[3]);
            Assert.Equal(0, Alpha(pixels, 23, 23));
            Assert.Equal(0, Alpha(pixels, 12, 12));

            var accentPixels = new List<(int X, int Y)>();
            for (var y = 0; y < 24; y++)
            {
                for (var x = 0; x < 24; x++)
                {
                    var offset = ((y * 24) + x) * 4;
                    if (pixels[offset + 2] > 140 && pixels[offset + 1] < 110 && pixels[offset] < 110)
                    {
                        accentPixels.Add((x, y));
                    }
                }
            }
            Assert.NotEmpty(accentPixels);
            var meanX = accentPixels.Average(pixel => pixel.X);
            var meanY = accentPixels.Average(pixel => pixel.Y);
            switch (expectedRail)
            {
                case "top":
                    Assert.True(meanY < 10, $"Top rail mean Y was {meanY:F2}.");
                    break;
                case "left":
                    Assert.True(meanX < 10, $"Left rail mean X was {meanX:F2}.");
                    break;
                default:
                    Assert.True(meanX > 14, $"Right rail mean X was {meanX:F2}.");
                    break;
            }
        });
    }

    private static byte Alpha(byte[] pixels, int x, int y) => pixels[(((y * 24) + x) * 4) + 3];
}
