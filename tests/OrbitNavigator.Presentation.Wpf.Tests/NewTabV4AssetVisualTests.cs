using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using OrbitNavigator.Presentation.Wpf;

using Xunit;

namespace OrbitNavigator.Presentation.Wpf.Tests;

[Collection("WPF focus-sensitive")]
public sealed class NewTabV4AssetVisualTests
{
    [Fact]
    public void SceneAssetsSeparateOpaqueBaseFromTransparentMotionLayers()
    {
        var background = Load(NewTabDecorationControl.DefaultV4BaseAssetRelativePath);
        var nebula = Load(NewTabDecorationControl.DefaultV4NebulaAssetRelativePath);
        var stars = Load(NewTabDecorationControl.DefaultV4NearStarfieldAssetRelativePath);

        Assert.Equal(new[] { 2560, 1440 }, new[] { background.PixelWidth, background.PixelHeight });
        Assert.Equal(background.PixelWidth, nebula.PixelWidth);
        Assert.Equal(background.PixelHeight, nebula.PixelHeight);
        Assert.Equal(background.PixelWidth, stars.PixelWidth);
        Assert.Equal(background.PixelHeight, stars.PixelHeight);

        var backgroundAlpha = Alpha(background);
        var nebulaAlpha = Alpha(nebula);
        var starsAlpha = Alpha(stars);
        Assert.All(backgroundAlpha, value => Assert.Equal(byte.MaxValue, value));
        Assert.True(nebulaAlpha.Count(value => value == 0) > nebulaAlpha.Length / 2);
        Assert.DoesNotContain(byte.MaxValue, nebulaAlpha);
        Assert.True(starsAlpha.Count(value => value == 0) > starsAlpha.Length * 9 / 10);
        Assert.DoesNotContain(byte.MaxValue, starsAlpha);
        Assert.Equal(0, starsAlpha[0]);
        Assert.Equal(0, starsAlpha[^1]);
    }

    [Theory]
    [InlineData(OrbitEmberStar.FavoriteAtlasV4RelativePath)]
    [InlineData(OrbitEmberStar.TabGroupAtlasV4RelativePath)]
    public void FlareAtlasChangesAttachedSilhouetteWithoutGlobalBrightnessPulse(string relativePath)
    {
        var atlas = Load(relativePath);
        Assert.Equal(768, atlas.PixelWidth);
        Assert.Equal(576, atlas.PixelHeight);

        var alphaHashes = new HashSet<string>(StringComparer.Ordinal);
        var luminances = new List<double>();
        for (var index = 0; index < OrbitEmberStar.AtlasFrameCount; index++)
        {
            var frame = Frame(atlas, index);
            var pixels = Pixels(frame);
            var alpha = AlphaFromBgra(pixels);
            alphaHashes.Add(Convert.ToHexString(SHA256.HashData(alpha)));
            luminances.Add(IntegratedLuminance(pixels));
            Assert.Equal(1, CountVisibleComponents(alpha, frame.PixelWidth, frame.PixelHeight));
            AssertTransparentOuterRing(alpha, frame.PixelWidth, frame.PixelHeight);
        }

        Assert.Equal(OrbitEmberStar.AtlasFrameCount, alphaHashes.Count);
        var mean = luminances.Average();
        Assert.True(
            (luminances.Max() - luminances.Min()) / mean < 0.02,
            $"Whole-star luminance varied enough to read as a pulse: {string.Join(", ", luminances.Select(value => value.ToString("F2")))}");

        var firstAlpha = AlphaFromBgra(Pixels(Frame(atlas, 0)));
        var evolvedAlpha = AlphaFromBgra(Pixels(Frame(atlas, 6)));
        var changed = firstAlpha.Zip(evolvedAlpha).Count(pair => Math.Abs(pair.First - pair.Second) >= 8);
        Assert.True(changed > firstAlpha.Length / 40, "The corona silhouette did not evolve materially.");
    }

    [Fact]
    public void ControlsPreferV4WhileKeepingStaticAndAccessibilityCompatibility() => StaTest.Run(() =>
    {
        var favorite = new OrbitEmberStar(OrbitEmberStarKind.Favorite)
        {
            IsActive = true,
            ReducedMotion = true,
        };
        StaTest.Prepare(favorite, 96, 96);

        Assert.Equal(6, OrbitEmberStar.LatestArtworkVersion);
        Assert.Equal(OrbitEmberStar.FavoriteAtlasV6RelativePath, favorite.LatestAtlasRelativePath);
        Assert.True(favorite.HasArtworkAtlas);
        Assert.True(favorite.UsesV6Artwork);
        Assert.True(favorite.UsesV5Artwork);
        Assert.False(favorite.UsesV4Artwork);
        Assert.True(favorite.UsesV3Artwork);
        Assert.Equal(OrbitEmberStar.LatestAtlasFrameCount, favorite.ResolvedFrameCount);
        Assert.False(favorite.IsRegisteredWithSharedClock);
        var peer = UIElementAutomationPeer.CreatePeerForElement(favorite);
        Assert.NotNull(peer);
        Assert.Equal("Favorite star, active", peer.GetName());
        Assert.Contains("decorative", AutomationProperties.GetHelpText(favorite));

        if (SystemParameters.HighContrast)
        {
            return;
        }

        var decoration = new NewTabDecorationControl { ReducedMotion = true };
        decoration.SetCoordinatorAsset(Packaged(NewTabDecorationControl.DefaultV3AssetRelativePath), false);
        StaTest.Prepare(decoration, 1200, 800);
        Assert.Equal(6, NewTabDecorationControl.LatestArtworkVersion);
        Assert.True(decoration.HasV6Asset);
        Assert.True(decoration.HasV5Asset);
        Assert.True(decoration.HasV4Asset);
        Assert.False(decoration.HasV4CompanionLayers);
        Assert.True(decoration.HasV5CelestialOverlay);
        Assert.True(decoration.HasV6CelestialOverlay);
        Assert.True(decoration.HasV3Asset);
        Assert.True(decoration.HasLayeredScene);
        Assert.False(decoration.IsLayeredMotionActive);
        Assert.EndsWith("orbit-deep-space-base-v6-a.png", decoration.ResolvedV6BaseAssetPath);
        Assert.All(
            StaTest.Descendants(decoration).OfType<System.Windows.Controls.Image>(),
            layer => Assert.Null(layer.CacheMode));
    });

    [Fact]
    public void EmberStarSuspendsSharedClockWhileHostWindowIsMinimized() => StaTest.Run(() =>
    {
        if (SystemParameters.HighContrast)
        {
            return;
        }

        var baseline = OrbitEmberStar.SharedClockSubscriberCount;
        var star = new OrbitEmberStar(OrbitEmberStarKind.Favorite)
        {
            Width = 96,
            Height = 96,
            IsActive = true,
        };
        var window = new Window
        {
            Content = star,
            Width = 140,
            Height = 140,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
        };
        window.Show();
        try
        {
            window.UpdateLayout();
            Assert.True(star.IsRegisteredWithSharedClock);
            Assert.Equal(baseline + 1, OrbitEmberStar.SharedClockSubscriberCount);

            window.WindowState = WindowState.Minimized;
            window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background, () => { });
            Assert.False(star.IsRegisteredWithSharedClock);
            Assert.Equal(baseline, OrbitEmberStar.SharedClockSubscriberCount);

            window.WindowState = WindowState.Normal;
            window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background, () => { });
            Assert.True(star.IsRegisteredWithSharedClock);
            Assert.Equal(baseline + 1, OrbitEmberStar.SharedClockSubscriberCount);
        }
        finally
        {
            window.Close();
        }

        Assert.Equal(baseline, OrbitEmberStar.SharedClockSubscriberCount);
    });

    [Fact]
    public void CaptureInstalledContextLikeReviewFramesWhenRequested() => StaTest.Run(() =>
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("ORBIT_CAPTURE_NEW_TAB_V4"),
            "1",
            StringComparison.Ordinal))
        {
            return;
        }

        if (SystemParameters.HighContrast)
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
        page.SetCoordinatorDecoration(Packaged(NewTabDecorationControl.DefaultV3AssetRelativePath), false);
        page.SetStellarHubAssets(
            Packaged("assets/new-tab/bookmark-star-v1.png"),
            Packaged("assets/new-tab/workspace-star-v1.png"));
        page.SetWorkspaceData([], []);
        var output = Path.Combine(FindRepositoryRoot(), "artifacts", "verification");
        Directory.CreateDirectory(output);
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
        window.Show();
        try
        {
            window.UpdateLayout();
            window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Render, () => { });
            var decoration = Assert.Single(StaTest.Descendants(page).OfType<NewTabDecorationControl>());
            var stars = StaTest.Descendants(page).OfType<OrbitEmberStar>().Where(star => star.IsActive).ToArray();
            Assert.Equal(2, stars.Length);
            DetachFromAutomaticClock(decoration, stars);

            var renderScene = typeof(NewTabDecorationControl).GetMethod(
                "RenderSceneAtElapsedSeconds",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var applyStarFrame = typeof(OrbitEmberStar).GetMethod(
                "ApplySharedMotionFrame",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            SetReviewPhase(renderScene, decoration, applyStarFrame, stars, 2);
            CaptureWithIntegrity(
                page,
                Path.Combine(output, "new-tab-v4-integrated-phase-a.png"),
                1200,
                800);
            SetReviewPhase(renderScene, decoration, applyStarFrame, stars, 6);
            CaptureWithIntegrity(
                page,
                Path.Combine(output, "new-tab-v4-integrated-phase-b.png"),
                1200,
                800);

            var motionFrames = Path.Combine(output, "new-tab-v4-motion-frames");
            Directory.CreateDirectory(motionFrames);
            const int framesPerSecond = 4;
            const int frameCount = framesPerSecond * 8;
            var observedStarFrames = stars.Select(_ => new HashSet<int>()).ToArray();
            var heroBrightPixelCounts = new List<int>();
            for (var frame = 0; frame < frameCount; frame++)
            {
                var elapsed = frame / (double)framesPerSecond;
                SetReviewPhase(renderScene, decoration, applyStarFrame, stars, elapsed);
                for (var index = 0; index < stars.Length; index++)
                {
                    observedStarFrames[index].Add(stars[index].CurrentFrameIndex);
                }
                var bitmap = CaptureWithIntegrity(
                    page,
                    Path.Combine(motionFrames, $"frame-{frame:D2}.png"),
                    1200,
                    800);
                heroBrightPixelCounts.Add(CountBrightPixels(bitmap, new Int32Rect(220, 35, 760, 350)));
            }

            Assert.All(observedStarFrames, frames => Assert.True(frames.Count >= 10));
            var averageHeroPixels = heroBrightPixelCounts.Average();
            Assert.True(
                (heroBrightPixelCounts.Max() - heroBrightPixelCounts.Min()) / averageHeroPixels < 0.08,
                "Fixed logo/search content changed enough to indicate a corrupt capture frame.");

            page.ReducedMotion = true;
            window.UpdateLayout();
            window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Render, () => { });
            CaptureWithIntegrity(
                page,
                Path.Combine(output, "new-tab-v4-reduced-motion.png"),
                1200,
                800);

            page.SetCoordinatorDecoration(Packaged(NewTabDecorationControl.DefaultV3AssetRelativePath), true);
            window.UpdateLayout();
            window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Render, () => { });
            CaptureWithIntegrity(
                page,
                Path.Combine(output, "new-tab-v4-reduced-noise.png"),
                1200,
                800);
        }
        finally
        {
            window.Close();
            if (window.Dispatcher.CheckAccess())
            {
                window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background, () => { });
            }
        }
    });

    private static BitmapSource Load(string relativePath)
    {
        var path = Packaged(relativePath);
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        if (frame.CanFreeze)
        {
            frame.Freeze();
        }
        return frame;
    }

    private static string Packaged(string relativePath)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Required visual asset was missing: {path}");
        return path;
    }

    private static BitmapSource Frame(BitmapSource atlas, int index)
    {
        const int frameSize = 192;
        var frame = new CroppedBitmap(
            atlas,
            new Int32Rect(
                (index % OrbitEmberStar.AtlasColumns) * frameSize,
                (index / OrbitEmberStar.AtlasColumns) * frameSize,
                frameSize,
                frameSize));
        frame.Freeze();
        return frame;
    }

    private static byte[] Pixels(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static byte[] Alpha(BitmapSource source) => AlphaFromBgra(Pixels(source));

    private static byte[] AlphaFromBgra(byte[] pixels)
    {
        var alpha = new byte[pixels.Length / 4];
        for (var pixel = 0; pixel < alpha.Length; pixel++)
        {
            alpha[pixel] = pixels[(pixel * 4) + 3];
        }
        return alpha;
    }

    private static double IntegratedLuminance(byte[] pixels)
    {
        var weighted = 0d;
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            var alpha = pixels[offset + 3];
            weighted += (0.0722 * pixels[offset] + 0.7152 * pixels[offset + 1] +
                0.2126 * pixels[offset + 2]) * alpha;
        }
        return weighted / (pixels.Length / 4d);
    }

    private static int CountVisibleComponents(byte[] alpha, int width, int height)
    {
        var visited = new bool[alpha.Length];
        var components = 0;
        var pending = new Queue<int>();
        for (var index = 0; index < alpha.Length; index++)
        {
            if (alpha[index] < 6 || visited[index])
            {
                continue;
            }

            components++;
            visited[index] = true;
            pending.Enqueue(index);
            while (pending.Count > 0)
            {
                var current = pending.Dequeue();
                var x = current % width;
                var y = current / width;
                Visit(x - 1, y);
                Visit(x + 1, y);
                Visit(x, y - 1);
                Visit(x, y + 1);
            }
        }
        return components;

        void Visit(int x, int y)
        {
            if (x < 0 || x >= width || y < 0 || y >= height)
            {
                return;
            }
            var candidate = (y * width) + x;
            if (visited[candidate] || alpha[candidate] < 6)
            {
                return;
            }
            visited[candidate] = true;
            pending.Enqueue(candidate);
        }
    }

    private static void AssertTransparentOuterRing(byte[] alpha, int width, int height)
    {
        for (var x = 0; x < width; x++)
        {
            Assert.Equal(0, alpha[x]);
            Assert.Equal(0, alpha[((height - 1) * width) + x]);
        }
        for (var y = 0; y < height; y++)
        {
            Assert.Equal(0, alpha[y * width]);
            Assert.Equal(0, alpha[(y * width) + width - 1]);
        }
    }

    private static void DetachFromAutomaticClock(
        NewTabDecorationControl decoration,
        IReadOnlyList<OrbitEmberStar> stars)
    {
        var clockType = typeof(NewTabDecorationControl).Assembly.GetType(
            "OrbitNavigator.Presentation.Wpf.OrbitSharedVisualMotionClock",
            throwOnError: true)!;
        var unregister = clockType.GetMethod(
            "Unregister",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        unregister.Invoke(null, [decoration]);
        foreach (var star in stars)
        {
            unregister.Invoke(null, [star]);
        }
    }

    private static void SetReviewPhase(
        MethodInfo renderScene,
        NewTabDecorationControl decoration,
        MethodInfo applyStarFrame,
        IReadOnlyList<OrbitEmberStar> stars,
        double elapsedSeconds)
    {
        renderScene.Invoke(decoration, [elapsedSeconds]);
        var normalizedStarPhase = elapsedSeconds % 8d / 8d;
        foreach (var star in stars)
        {
            applyStarFrame.Invoke(star, [normalizedStarPhase]);
        }

        decoration.Dispatcher.Invoke(
            System.Windows.Threading.DispatcherPriority.Render,
            () => { });
    }

    private static BitmapSource CaptureWithIntegrity(
        FrameworkElement element,
        string path,
        int width,
        int height)
    {
        Assert.True(element.IsLoaded, "Review captures require a loaded visual attached to a shown Window.");
        element.UpdateLayout();
        element.Dispatcher.Invoke(
            System.Windows.Threading.DispatcherPriority.Render,
            () => { });

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        bitmap.Freeze();

        Assert.Equal(width, bitmap.PixelWidth);
        Assert.Equal(height, bitmap.PixelHeight);
        Assert.True(
            CountBrightPixels(bitmap, new Int32Rect(0, 0, width, height)) > 500,
            "Review capture was unexpectedly blank or incomplete.");

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return bitmap;
    }

    private static int CountBrightPixels(BitmapSource source, Int32Rect bounds)
    {
        Assert.InRange(bounds.X, 0, source.PixelWidth - 1);
        Assert.InRange(bounds.Y, 0, source.PixelHeight - 1);
        Assert.InRange(bounds.X + bounds.Width, 1, source.PixelWidth);
        Assert.InRange(bounds.Y + bounds.Height, 1, source.PixelHeight);

        var cropped = new CroppedBitmap(source, bounds);
        var pixels = Pixels(cropped);
        var count = 0;
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            if (pixels[offset + 3] >= 32 &&
                Math.Max(pixels[offset], Math.Max(pixels[offset + 1], pixels[offset + 2])) >= 128)
            {
                count++;
            }
        }
        return count;
    }

    private static string FindRepositoryRoot()
    {
        var candidate = new DirectoryInfo(AppContext.BaseDirectory);
        while (candidate is not null)
        {
            if (File.Exists(Path.Combine(candidate.FullName, "OrbitNavigator.sln")))
            {
                return candidate.FullName;
            }
            candidate = candidate.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the Orbit Navigator repository root.");
    }
}
