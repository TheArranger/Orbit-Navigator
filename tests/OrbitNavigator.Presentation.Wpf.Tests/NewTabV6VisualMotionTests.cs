using System.Security.Cryptography;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OrbitNavigator.Presentation.Wpf;
using Xunit;

namespace OrbitNavigator.Presentation.Wpf.Tests;

[Collection("WPF focus-sensitive")]
public sealed class NewTabV6VisualMotionTests
{
    [Fact]
    public void V6RuntimeAssetsHaveExactProductionGeometryAndQuietCenters()
    {
        foreach (var relativePath in NewTabDecorationControl.DefaultV6BaseAssetRelativePaths)
        {
            var scene = Load(relativePath);
            Assert.Equal(2560, scene.PixelWidth);
            Assert.Equal(1440, scene.PixelHeight);
            var pixels = Pixels(scene);
            var values = new List<byte>();
            var x0 = (int)(scene.PixelWidth * 0.28);
            var x1 = (int)(scene.PixelWidth * 0.72);
            var y0 = (int)(scene.PixelHeight * 0.16);
            var y1 = (int)(scene.PixelHeight * 0.58);
            for (var y = y0; y < y1; y++)
            {
                for (var x = x0; x < x1; x++)
                {
                    var offset = ((y * scene.PixelWidth) + x) * 4;
                    values.Add((byte)Math.Round(
                        (0.0722 * pixels[offset]) +
                        (0.7152 * pixels[offset + 1]) +
                        (0.2126 * pixels[offset + 2])));
                }
            }
            values.Sort();
            Assert.InRange(values.Average(value => value), 0, 24);
            Assert.InRange(values[(int)(values.Count * 0.95)], 0, 45);
        }
    }

    [Fact]
    public void CelestialAtlasContainsRealTransparentEvolutionForEveryElement()
    {
        var atlas = Load(OrbitNewTabCelestialOverlay.AnimatedAtlasRelativePath);
        Assert.Equal(2048, atlas.PixelWidth);
        Assert.Equal(1024, atlas.PixelHeight);
        Assert.Equal(8, OrbitNewTabCelestialOverlay.AnimatedFramesPerElement);

        for (var row = 0; row < OrbitNewTabCelestialOverlay.AnimatedAtlasRows; row++)
        {
            var hashes = new HashSet<string>(StringComparer.Ordinal);
            var frames = Enumerable.Range(0, OrbitNewTabCelestialOverlay.AnimatedFramesPerElement)
                .Select(column => Frame(atlas, column, row, 256))
                .ToArray();
            foreach (var frame in frames)
            {
                var pixels = Pixels(frame);
                hashes.Add(Convert.ToHexString(SHA256.HashData(pixels)));
                AssertTransparentSafetyRing(pixels, 256, 16);
                Assert.Equal(1, CountComponents(pixels, 256, 6));
            }
            Assert.Equal(OrbitNewTabCelestialOverlay.AnimatedFramesPerElement, hashes.Count);
            for (var index = 0; index < frames.Length; index++)
            {
                var first = Pixels(frames[index]);
                var second = Pixels(frames[(index + 1) % frames.Length]);
                var changed = 0;
                for (var pixel = 0; pixel < first.Length; pixel += 4)
                {
                    if (Math.Max(
                        Math.Max(Math.Abs(first[pixel] - second[pixel]), Math.Abs(first[pixel + 1] - second[pixel + 1])),
                        Math.Max(Math.Abs(first[pixel + 2] - second[pixel + 2]), Math.Abs(first[pixel + 3] - second[pixel + 3]))) >= 12)
                    {
                        changed++;
                    }
                }
                Assert.True(changed >= 256 * 256 / 12, $"Element {row}, frame {index} did not evolve visibly.");
            }
        }
    }

    [Theory]
    [InlineData(OrbitEmberStar.FavoriteAtlasV6RelativePath)]
    [InlineData(OrbitEmberStar.TabGroupAtlasV6RelativePath)]
    public void SolarV6FramesChangeSilhouetteWithoutWholeStarPulse(string relativePath)
    {
        var atlas = Load(relativePath);
        Assert.Equal(1024, atlas.PixelWidth);
        Assert.Equal(1024, atlas.PixelHeight);
        var luminances = new List<double>();
        var alphaHashes = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < 16; index++)
        {
            var frame = Frame(atlas, index % 4, index / 4, 256);
            var pixels = Pixels(frame);
            AssertTransparentSafetyRing(pixels, 256, 16);
            Assert.Equal(1, CountComponents(pixels, 256, 6));
            var alpha = Enumerable.Range(0, pixels.Length / 4)
                .Select(pixel => pixels[(pixel * 4) + 3])
                .ToArray();
            alphaHashes.Add(Convert.ToHexString(SHA256.HashData(alpha)));
            luminances.Add(IntegratedLuminance(pixels));
        }
        Assert.Equal(16, alphaHashes.Count);
        var spread = (luminances.Max() - luminances.Min()) / luminances.Average();
        Assert.InRange(spread, 0, 0.02);
    }

    [Fact]
    public void RuntimePrefersAnimatedFramesAndHoverUnsubscribesOnlyNonStellarMotion() => StaTest.Run(() =>
    {
        if (SystemParameters.HighContrast)
        {
            return;
        }
        var baseline = OrbitSharedSubscriberCount();
        var decoration = new NewTabDecorationControl();
        var hub = new OrbitStellarOrbitVisual { IsActive = true };
        var host = new Grid();
        host.Children.Add(decoration);
        host.Children.Add(hub);
        var window = new Window
        {
            Content = host,
            Width = 800,
            Height = 600,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
        };
        window.Show();
        try
        {
            window.UpdateLayout();
            Assert.True(decoration.HasV6Asset);
            Assert.True(decoration.CelestialOverlay.UsesAnimatedArtwork);
            Assert.Equal(8, decoration.CelestialOverlay.ResolvedFramesPerElement);
            Assert.True(hub.CentralStar.UsesV6Artwork);
            Assert.Equal(baseline + 4, OrbitSharedSubscriberCount());

            decoration.FreezeNonStellarMotion = true;
            hub.FreezeNonStellarMotion = true;
            Assert.False(decoration.IsRegisteredWithSharedClock);
            Assert.False(decoration.CelestialOverlay.IsRegisteredWithSharedClock);
            Assert.False(hub.IsRegisteredWithSharedClock);
            Assert.True(hub.CentralStar.IsRegisteredWithSharedClock);
            Assert.Equal(baseline + 1, OrbitSharedSubscriberCount());
        }
        finally
        {
            window.Close();
        }
        Assert.Equal(baseline, OrbitSharedSubscriberCount());
    });

    [Fact]
    public void PrivateThemeIsCodeNativeAndReducedMotionRemainsStatic() => StaTest.Run(() =>
    {
        var decoration = new NewTabDecorationControl
        {
            IsPrivateTheme = true,
            ReducedMotion = true,
        };
        var hub = new OrbitStellarOrbitVisual
        {
            IsPrivateTheme = true,
            ReducedMotion = true,
        };
        StaTest.Prepare(decoration, 1200, 760);
        StaTest.Prepare(hub, 180, 180);
        Assert.True(decoration.CelestialOverlay.IsPrivateTheme);
        Assert.Equal(OrbitVisualTheme.PrivateViolet, hub.CentralStar.Accent);
        Assert.Equal(OrbitVisualTheme.SeaGlass, hub.CentralStar.Stroke);
        Assert.False(decoration.IsRegisteredWithSharedClock);
        Assert.False(hub.IsRegisteredWithSharedClock);
        Assert.False(hub.CentralStar.IsRegisteredWithSharedClock);
        Assert.Equal(0, decoration.CurrentOpeningRotationAngle);
    });

    [Fact]
    public void CaptureLoadedV6TemporalReviewEvidenceWhenRequested() => StaTest.Run(() =>
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("ORBIT_CAPTURE_NEW_TAB_V6"),
            "1",
            StringComparison.Ordinal) ||
            SystemParameters.HighContrast)
        {
            return;
        }

        var page = new NewTabPageControl
        {
            Width = 1200,
            Height = 800,
            ReducedMotion = false,
            ShowDonationLink = false,
        };
        page.SetCoordinatorDecoration(
            Packaged(NewTabDecorationControl.DefaultV5BaseAssetRelativePath),
            reduceVisualNoise: false);
        page.SetStellarHubAssets(
            Packaged("assets/new-tab/bookmark-star-v1.png"),
            Packaged("assets/new-tab/workspace-star-v1.png"));
        page.SetWorkspaceData([], []);

        var window = new Window
        {
            Content = page,
            Width = 1200,
            Height = 800,
            Left = -30000,
            Top = -30000,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
        };
        var output = Path.Combine(FindRepositoryRoot(), "artifacts", "verification");
        Directory.CreateDirectory(output);
        window.Show();
        try
        {
            window.UpdateLayout();
            window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Render, () => { });
            window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background, () => { });
            // The UX host intentionally has a 160ms one-shot content reveal.
            // Let that unrelated transition settle before comparing scene time.
            Thread.Sleep(220);
            window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Render, () => { });
            var decoration = Assert.Single(StaTest.Descendants(page).OfType<NewTabDecorationControl>());
            var hubs = StaTest.Descendants(page).OfType<OrbitStellarOrbitVisual>().ToArray();
            Assert.Equal(2, hubs.Length);
            var stars = hubs.Select(hub => hub.CentralStar).ToArray();
            DetachAutomaticClock([decoration, decoration.CelestialOverlay, .. hubs, .. stars]);

            SetReviewTime(decoration, hubs, stars, 2.0);
            Capture(page, Path.Combine(output, "new-tab-v6-handoff-settled.png"), 1200, 800);
            var settledStarFrames = stars.Select(star => star.CurrentFrameIndex).ToArray();
            var settledCelestialFrames = CelestialFrameIndices(decoration.CelestialOverlay);

            SetReviewTime(decoration, hubs, stars, 6.5);
            Capture(page, Path.Combine(output, "new-tab-v6-handoff-temporal.png"), 1200, 800);
            Assert.Contains(stars.Select((star, index) => star.CurrentFrameIndex != settledStarFrames[index]), changed => changed);
            Assert.All(hubs, hub => Assert.True(hub.CurrentSatellitePhase > 0));
            Assert.NotEqual(settledCelestialFrames, CelestialFrameIndices(decoration.CelestialOverlay));

            SetReviewTime(decoration, hubs, stars, 9.0);
            decoration.FreezeNonStellarMotion = true;
            foreach (var hub in hubs)
            {
                hub.FreezeNonStellarMotion = true;
            }
            var heldScene = decoration.LastMotionElapsedSeconds;
            var heldRings = hubs.Select(hub => hub.LastElapsedSeconds).ToArray();
            var hoverA = Capture(page, Path.Combine(output, "new-tab-v6-hover-star-a.png"), 1200, 800);
            SetStarTime(stars, 9.5);
            var hoverB = Capture(page, Path.Combine(output, "new-tab-v6-hover-star-b.png"), 1200, 800);
            Assert.Equal(heldScene, decoration.LastMotionElapsedSeconds);
            Assert.Equal(heldRings, hubs.Select(hub => hub.LastElapsedSeconds).ToArray());
            Assert.All(stars, star => Assert.True(star.IsRegisteredWithSharedClock));
            var hoverDifference = CountChangedPixels(
                hoverA,
                hoverB,
                [new Int32Rect(300, 550, 220, 220), new Int32Rect(720, 550, 240, 220)]);
            Assert.True(hoverDifference.Inside >= 10_000, "The complete solar frames did not visibly evolve.");
            Assert.Equal(0, hoverDifference.Outside);

            page.ReducedMotion = true;
            var reducedA = Capture(page, Path.Combine(output, "new-tab-v6-reduced-motion-a.png"), 1200, 800);
            var reducedB = Capture(page, Path.Combine(output, "new-tab-v6-reduced-motion-b.png"), 1200, 800);
            Assert.Equal(Pixels(reducedA), Pixels(reducedB));

            page.ReducedMotion = false;
            page.ApplyPrivateMode(true);
            Capture(page, Path.Combine(output, "new-tab-v6-private-static.png"), 1200, 800);
        }
        finally
        {
            window.Close();
            window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background, () => { });
        }
    });

    private static int OrbitSharedSubscriberCount() => NewTabDecorationControl.SharedClockSubscriberCount;

    private static string CelestialFrameIndices(OrbitNewTabCelestialOverlay overlay) =>
        $"{overlay.CurrentCometFrameIndex}:{overlay.CurrentPlanetFrameIndex}:" +
        $"{overlay.CurrentGalaxyFrameIndex}:{overlay.CurrentGlintFrameIndex}";

    private static string Packaged(string relativePath)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Missing test asset: {path}");
        return path;
    }

    private static void DetachAutomaticClock(IEnumerable<FrameworkElement> targets)
    {
        var clock = typeof(NewTabDecorationControl).Assembly.GetType(
            "OrbitNavigator.Presentation.Wpf.OrbitSharedVisualMotionClock",
            throwOnError: true)!;
        var unregister = clock.GetMethod("Unregister", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var target in targets)
        {
            unregister.Invoke(null, [target]);
        }
    }

    private static void SetReviewTime(
        NewTabDecorationControl decoration,
        IReadOnlyList<OrbitStellarOrbitVisual> hubs,
        IReadOnlyList<OrbitEmberStar> stars,
        double elapsedSeconds)
    {
        InvokeElapsed(decoration, "RenderSceneAtElapsedSeconds", elapsedSeconds);
        InvokeElapsed(decoration.CelestialOverlay, "RenderAtElapsedSeconds", elapsedSeconds);
        foreach (var hub in hubs)
        {
            InvokeElapsed(hub, "RenderAtElapsedSeconds", elapsedSeconds);
        }
        SetStarTime(stars, elapsedSeconds);
        decoration.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Render, () => { });
    }

    private static void SetStarTime(IReadOnlyList<OrbitEmberStar> stars, double elapsedSeconds)
    {
        var apply = typeof(OrbitEmberStar).GetMethod(
            "ApplySharedMotionFrame",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var phase = (elapsedSeconds % 8d) / 8d;
        foreach (var star in stars)
        {
            apply.Invoke(star, [phase]);
        }
    }

    private static void InvokeElapsed(object target, string methodName, double elapsedSeconds)
    {
        var method = target.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(target, [elapsedSeconds]);
    }

    private static BitmapSource Capture(FrameworkElement element, string path, int width, int height)
    {
        Assert.True(element.IsLoaded);
        element.UpdateLayout();
        element.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Render, () => { });
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        bitmap.Freeze();
        Assert.True(Pixels(bitmap).Count(value => value >= 96) > 1000, "Capture was blank or incomplete.");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return bitmap;
    }

    private static (int Inside, int Outside) CountChangedPixels(
        BitmapSource first,
        BitmapSource second,
        IReadOnlyList<Int32Rect> includedRegions)
    {
        var firstPixels = Pixels(first);
        var secondPixels = Pixels(second);
        Assert.Equal(first.PixelWidth, second.PixelWidth);
        Assert.Equal(first.PixelHeight, second.PixelHeight);
        var inside = 0;
        var outside = 0;
        for (var y = 0; y < first.PixelHeight; y++)
        {
            for (var x = 0; x < first.PixelWidth; x++)
            {
                var offset = ((y * first.PixelWidth) + x) * 4;
                var changed = Math.Max(
                    Math.Max(Math.Abs(firstPixels[offset] - secondPixels[offset]),
                        Math.Abs(firstPixels[offset + 1] - secondPixels[offset + 1])),
                    Math.Max(Math.Abs(firstPixels[offset + 2] - secondPixels[offset + 2]),
                        Math.Abs(firstPixels[offset + 3] - secondPixels[offset + 3]))) >= 12;
                if (!changed)
                {
                    continue;
                }
                if (includedRegions.Any(region =>
                    x >= region.X && x < region.X + region.Width &&
                    y >= region.Y && y < region.Y + region.Height))
                {
                    inside++;
                }
                else
                {
                    outside++;
                }
            }
        }
        return (inside, outside);
    }

    private static string FindRepositoryRoot()
    {
        var candidate = new DirectoryInfo(AppContext.BaseDirectory);
        while (candidate is not null && !File.Exists(Path.Combine(candidate.FullName, "OrbitNavigator.sln")))
        {
            candidate = candidate.Parent;
        }
        return candidate?.FullName ?? throw new DirectoryNotFoundException("Could not find OrbitNavigator.sln.");
    }

    private static BitmapSource Load(string relativePath)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Missing test asset: {path}");
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(
            stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    private static BitmapSource Frame(BitmapSource atlas, int column, int row, int size)
    {
        var crop = new CroppedBitmap(atlas, new Int32Rect(column * size, row * size, size, size));
        crop.Freeze();
        return crop;
    }

    private static byte[] Pixels(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        return pixels;
    }

    private static void AssertTransparentSafetyRing(byte[] pixels, int size, int margin)
    {
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (x >= margin && x < size - margin && y >= margin && y < size - margin)
                {
                    continue;
                }
                Assert.Equal(0, pixels[(((y * size) + x) * 4) + 3]);
            }
        }
    }

    private static int CountComponents(byte[] pixels, int size, byte threshold)
    {
        var visited = new bool[size * size];
        var components = 0;
        for (var index = 0; index < visited.Length; index++)
        {
            if (visited[index] || pixels[(index * 4) + 3] < threshold)
            {
                continue;
            }
            components++;
            var queue = new Queue<int>();
            queue.Enqueue(index);
            visited[index] = true;
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var x = current % size;
                var y = current / size;
                foreach (var (nx, ny) in new[] { (x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1) })
                {
                    if (nx < 0 || nx >= size || ny < 0 || ny >= size)
                    {
                        continue;
                    }
                    var next = (ny * size) + nx;
                    if (visited[next] || pixels[(next * 4) + 3] < threshold)
                    {
                        continue;
                    }
                    visited[next] = true;
                    queue.Enqueue(next);
                }
            }
        }
        return components;
    }

    private static double IntegratedLuminance(byte[] pixels)
    {
        var total = 0.0;
        for (var index = 0; index < pixels.Length; index += 4)
        {
            total += ((0.0722 * pixels[index]) +
                      (0.7152 * pixels[index + 1]) +
                      (0.2126 * pixels[index + 2])) * pixels[index + 3];
        }
        return total;
    }
}
