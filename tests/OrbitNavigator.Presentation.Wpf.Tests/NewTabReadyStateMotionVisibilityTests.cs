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
public sealed class NewTabReadyStateMotionVisibilityTests
{
    [Fact]
    public void TunedCadenceIsVisibleButRemainsOnOneBoundedClock()
    {
        Assert.InRange(NewTabDecorationControl.SharedAnimationFramesPerSecond, 1, 24);
        Assert.InRange(NewTabDecorationControl.OrbitalRoutePeriod.TotalSeconds, 10, 15);
        Assert.InRange(NewTabDecorationControl.NearStarfieldHorizontalPeriod.TotalSeconds, 24, 32);
        Assert.InRange(OrbitNewTabCelestialOverlay.CometEvolutionPeriod.TotalSeconds, 2.2, 3.0);
        Assert.InRange(OrbitNewTabCelestialOverlay.PlanetEvolutionPeriod.TotalSeconds, 3.5, 4.5);
        Assert.InRange(OrbitNewTabCelestialOverlay.GalaxyEvolutionPeriod.TotalSeconds, 4.8, 6.0);
        Assert.InRange(OrbitNewTabCelestialOverlay.GlintEvolutionPeriod.TotalSeconds, 1.5, 2.1);
        Assert.InRange(OrbitStellarOrbitVisual.OuterRingPeriod.TotalSeconds, 9, 12);
        Assert.InRange(OrbitStellarOrbitVisual.InnerRingPeriod.TotalSeconds, 6, 8);
        Assert.InRange(OrbitStellarOrbitVisual.SatelliteOrbitPeriod.TotalSeconds, 8, 10);
        Assert.InRange(NewTabDecorationControl.SharedClockRenderingHandlerCount, 0, 1);
    }

    [Fact]
    public void ReadySceneShowsEdgeLocalEvolutionWhileQuietCenterStaysStill() => StaTest.Run(() =>
    {
        if (SystemParameters.HighContrast)
        {
            return;
        }

        var decoration = new NewTabDecorationControl { Width = 1200, Height = 800 };
        using var host = Show(decoration, 1200, 800);
        Assert.True(decoration.HasV6Asset);
        Assert.True(decoration.CelestialOverlay.UsesAnimatedArtwork);
        DetachAutomaticClock([decoration, decoration.CelestialOverlay]);

        SetDecorationTime(decoration, 2.0);
        var first = Capture(decoration, 1200, 800);
        SetDecorationTime(decoration, 3.0);
        var second = Capture(decoration, 1200, 800);

        var rightEdge = Changed(first, second, new Int32Rect(930, 0, 270, 470), 12);
        var leftEdge = Changed(first, second, new Int32Rect(0, 480, 390, 320), 12);
        var quietCenter = Changed(first, second, new Int32Rect(336, 120, 528, 320), 12);
        Assert.True(rightEdge.Percent >= 2.0,
            $"Right-edge celestial motion was too faint: {rightEdge.Percent:F3}%.");
        Assert.True(leftEdge.Percent >= 1.2,
            $"Left-edge scene motion was too faint: {leftEdge.Percent:F3}%.");
        Assert.True(quietCenter.Percent <= 0.20,
            $"Quiet center moved too much: {quietCenter.Percent:F3}%.");
    });

    [Fact]
    public void HoverFreezeStopsRingsAndSceneButCompleteSolarFramesKeepEvolving() => StaTest.Run(() =>
    {
        if (SystemParameters.HighContrast)
        {
            return;
        }

        var favorite = new OrbitStellarOrbitVisual
        {
            Kind = OrbitEmberStarKind.Favorite,
            Width = 180,
            Height = 180,
        };
        var workspace = new OrbitStellarOrbitVisual
        {
            Kind = OrbitEmberStarKind.TabGroup,
            Width = 180,
            Height = 180,
        };
        var canvas = new Canvas { Width = 420, Height = 200, Background = Brushes.Transparent };
        Canvas.SetLeft(favorite, 10);
        Canvas.SetTop(favorite, 10);
        Canvas.SetLeft(workspace, 220);
        Canvas.SetTop(workspace, 10);
        canvas.Children.Add(favorite);
        canvas.Children.Add(workspace);
        using var host = Show(canvas, 420, 200);
        DetachAutomaticClock([favorite, workspace, favorite.CentralStar, workspace.CentralStar]);

        SetHubTime([favorite, workspace], 4.0);
        SetStarTime([favorite.CentralStar, workspace.CentralStar], 4.0);
        favorite.FreezeNonStellarMotion = true;
        workspace.FreezeNonStellarMotion = true;
        var first = Capture(canvas, 420, 200);
        SetStarTime([favorite.CentralStar, workspace.CentralStar], 4.5);
        var second = Capture(canvas, 420, 200);

        var favoriteDelta = Changed(first, second, new Int32Rect(10, 10, 180, 180), 12);
        var workspaceDelta = Changed(first, second, new Int32Rect(220, 10, 180, 180), 12);
        var between = Changed(first, second, new Int32Rect(190, 0, 30, 200), 1);
        Assert.True(favoriteDelta.Percent >= 18,
            $"Favorite solar frame evolution was too faint: {favoriteDelta.Percent:F3}%.");
        Assert.True(workspaceDelta.Percent >= 18,
            $"Workspace solar frame evolution was too faint: {workspaceDelta.Percent:F3}%.");
        Assert.Equal(0, between.ChangedPixels);
    });

    [Fact]
    public void AlignedRingsAndSatelliteReadWithinHalfASecondWithoutSolarPulse() => StaTest.Run(() =>
    {
        if (SystemParameters.HighContrast)
        {
            return;
        }

        var hub = new OrbitStellarOrbitVisual
        {
            Kind = OrbitEmberStarKind.Favorite,
            Width = 180,
            Height = 180,
        };
        using var host = Show(hub, 180, 180);
        DetachAutomaticClock([hub, hub.CentralStar]);
        hub.CentralStar.MotionEnabled = false;

        SetHubTime([hub], 2.0);
        var first = Capture(hub, 180, 180);
        var firstSatellitePhase = hub.CurrentSatellitePhase;
        SetHubTime([hub], 2.5);
        var second = Capture(hub, 180, 180);
        var delta = Changed(first, second, new Int32Rect(0, 0, 180, 180), 8);

        Assert.True(delta.ChangedPixels >= 450,
            $"Ring/satellite motion changed only {delta.ChangedPixels} pixels.");
        Assert.InRange(hub.CurrentSatellitePhase - firstSatellitePhase, 0.05, 0.06);
    });

    [Fact]
    public void CaptureReadyStateTemporalEvidenceWhenRequested() => StaTest.Run(() =>
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("ORBIT_CAPTURE_READY_STATE_V6"),
                "1",
                StringComparison.Ordinal) ||
            SystemParameters.HighContrast)
        {
            return;
        }

        var output = Path.Combine(
            FindRepositoryRoot(),
            "artifacts",
            "verification",
            "new-tab-v6-ready-state-tuning-20260819");
        Directory.CreateDirectory(output);
        foreach (var (width, height, label) in new[]
                 {
                     (1200, 800, "1200x800"),
                     (1906, 1009, "1906x1009"),
                 })
        {
            var page = new NewTabPageControl
            {
                Width = width,
                Height = height,
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
            using var host = Show(page, width, height);

            // NewTabPageControl has a bounded content reveal. The capture seam
            // starts only after it is loaded and the reveal has fully settled.
            Thread.Sleep(220);
            page.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Render, () => { });
            var decoration = Assert.Single(StaTest.Descendants(page).OfType<NewTabDecorationControl>());
            var hubs = StaTest.Descendants(page).OfType<OrbitStellarOrbitVisual>().ToArray();
            var stars = hubs.Select(item => item.CentralStar).ToArray();
            Assert.Equal(2, hubs.Length);
            DetachAutomaticClock([decoration, decoration.CelestialOverlay, .. hubs, .. stars]);

            foreach (var elapsed in new[] { 2.0, 2.5, 3.0, 6.0, 12.0, 26.0 })
            {
                SetDecorationTime(decoration, elapsed);
                SetHubTime(hubs, elapsed);
                SetStarTime(stars, elapsed);
                var suffix = (elapsed - 2).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                Capture(page, width, height, Path.Combine(output, $"ready-{label}-t{suffix}.png"));
            }

            page.ReducedMotion = true;
            var reducedA = Capture(page, width, height,
                Path.Combine(output, $"reduced-{label}-a.png"));
            var reducedB = Capture(page, width, height,
                Path.Combine(output, $"reduced-{label}-b.png"));
            Assert.Equal(Pixels(reducedA), Pixels(reducedB));
        }
    });

    private static TestWindow Show(FrameworkElement content, int width, int height)
    {
        var window = new Window
        {
            Content = content,
            Width = width,
            Height = height,
            Left = -30000,
            Top = -30000,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
        };
        window.Show();
        window.UpdateLayout();
        window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Render, () => { });
        Assert.True(content.IsLoaded);
        return new TestWindow(window);
    }

    private static void SetDecorationTime(NewTabDecorationControl decoration, double elapsedSeconds)
    {
        InvokeElapsed(decoration, "RenderSceneAtElapsedSeconds", elapsedSeconds);
        InvokeElapsed(decoration.CelestialOverlay, "RenderAtElapsedSeconds", elapsedSeconds);
        decoration.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Render, () => { });
    }

    private static void SetHubTime(
        IEnumerable<OrbitStellarOrbitVisual> hubs,
        double elapsedSeconds)
    {
        foreach (var hub in hubs)
        {
            InvokeElapsed(hub, "RenderAtElapsedSeconds", elapsedSeconds);
        }
    }

    private static void SetStarTime(IEnumerable<OrbitEmberStar> stars, double elapsedSeconds)
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

    private static BitmapSource Capture(
        FrameworkElement element,
        int width,
        int height,
        string? path = null)
    {
        element.UpdateLayout();
        element.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Render, () => { });
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        bitmap.Freeze();
        Assert.True(Pixels(bitmap).Count(value => value >= 96) > 1000,
            "Ready-state capture was blank or incomplete.");
        if (path is not null)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
        return bitmap;
    }

    private static (int ChangedPixels, double Percent) Changed(
        BitmapSource first,
        BitmapSource second,
        Int32Rect region,
        int threshold)
    {
        var firstPixels = Pixels(first);
        var secondPixels = Pixels(second);
        var changed = 0;
        for (var y = region.Y; y < region.Y + region.Height; y++)
        {
            for (var x = region.X; x < region.X + region.Width; x++)
            {
                var offset = ((y * first.PixelWidth) + x) * 4;
                var delta = Math.Max(
                    Math.Max(Math.Abs(firstPixels[offset] - secondPixels[offset]),
                        Math.Abs(firstPixels[offset + 1] - secondPixels[offset + 1])),
                    Math.Max(Math.Abs(firstPixels[offset + 2] - secondPixels[offset + 2]),
                        Math.Abs(firstPixels[offset + 3] - secondPixels[offset + 3])));
                if (delta >= threshold)
                {
                    changed++;
                }
            }
        }
        return (changed, 100d * changed / (region.Width * region.Height));
    }

    private static byte[] Pixels(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        return pixels;
    }

    private static string Packaged(string relativePath)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Missing test asset: {path}");
        return path;
    }

    private static string FindRepositoryRoot()
    {
        var candidate = new DirectoryInfo(AppContext.BaseDirectory);
        while (candidate is not null && !File.Exists(Path.Combine(candidate.FullName, "OrbitNavigator.sln")))
        {
            candidate = candidate.Parent;
        }
        return candidate?.FullName ??
            throw new DirectoryNotFoundException("Could not locate OrbitNavigator.sln.");
    }

    private sealed class TestWindow(Window window) : IDisposable
    {
        public void Dispose()
        {
            window.Close();
            window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background, () => { });
        }
    }
}
