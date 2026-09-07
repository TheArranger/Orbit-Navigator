using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using OrbitNavigator.Presentation.Wpf;

using Xunit;

namespace OrbitNavigator.Presentation.Wpf.Tests;

[Collection("WPF focus-sensitive")]
public sealed class NewTabV5VisualMotionTests
{
    [Fact]
    public void SceneAndCelestialAssetsHaveHonestAlphaAndSafetyMargins()
    {
        var scene = Load(NewTabDecorationControl.DefaultV5BaseAssetRelativePath);
        Assert.Equal(new[] { 2560, 1440 }, new[] { scene.PixelWidth, scene.PixelHeight });
        Assert.All(Alpha(scene), value => Assert.Equal(byte.MaxValue, value));

        var elements = Load(OrbitNewTabCelestialOverlay.AtlasRelativePath);
        Assert.Equal(new[] { 1024, 1024 }, new[] { elements.PixelWidth, elements.PixelHeight });
        for (var index = 0; index < OrbitNewTabCelestialOverlay.ElementCount; index++)
        {
            var element = Frame(elements, index, 2, 2);
            var alpha = Alpha(element);
            Assert.Equal(1, CountVisibleComponents(alpha, element.PixelWidth, element.PixelHeight));
            AssertTransparentOuterRing(alpha, element.PixelWidth, element.PixelHeight);
            Assert.True(alpha.Count(value => value == 0) > alpha.Length / 2);
        }
    }

    [Theory]
    [InlineData(OrbitEmberStar.FavoriteAtlasV5RelativePath)]
    [InlineData(OrbitEmberStar.TabGroupAtlasV5RelativePath)]
    public void SolarAtlasesEvolveCoronaWithoutPulseMatteOrDetachedFragments(string relativePath)
    {
        var atlas = Load(relativePath);
        Assert.Equal(new[] { 1024, 1024 }, new[] { atlas.PixelWidth, atlas.PixelHeight });

        var alphaHashes = new HashSet<string>(StringComparer.Ordinal);
        var luminances = new List<double>();
        var centers = new List<Point>();
        var frames = new List<BitmapSource>();
        for (var index = 0; index < OrbitEmberStar.LatestAtlasFrameCount; index++)
        {
            var frame = Frame(
                atlas,
                index,
                OrbitEmberStar.LatestAtlasColumns,
                OrbitEmberStar.LatestAtlasRows);
            frames.Add(frame);
            var pixels = Pixels(frame);
            var alpha = AlphaFromBgra(pixels);
            alphaHashes.Add(Convert.ToHexString(SHA256.HashData(alpha)));
            luminances.Add(IntegratedLuminance(pixels));
            centers.Add(AlphaCentroid(alpha, frame.PixelWidth, frame.PixelHeight));
            Assert.Equal(1, CountVisibleComponents(alpha, frame.PixelWidth, frame.PixelHeight));
            AssertTransparentOuterRing(alpha, frame.PixelWidth, frame.PixelHeight);
        }

        Assert.Equal(OrbitEmberStar.LatestAtlasFrameCount, alphaHashes.Count);
        var meanLuminance = luminances.Average();
        Assert.True(
            (luminances.Max() - luminances.Min()) / meanLuminance < 0.02,
            "The complete star varied enough in emitted light to read as a pulse.");
        Assert.InRange(centers.Max(point => point.X) - centers.Min(point => point.X), 0, 8);
        Assert.InRange(centers.Max(point => point.Y) - centers.Min(point => point.Y), 0, 8);

        for (var index = 0; index < frames.Count; index++)
        {
            var current = Alpha(frames[index]);
            var next = Alpha(frames[(index + 1) % frames.Count]);
            var changed = current.Zip(next).Count(pair => Math.Abs(pair.First - pair.Second) >= 8);
            Assert.True(changed > current.Length / 20, $"Frame {index} did not evolve the attached corona materially.");
        }
    }

    [Fact]
    public void V5PrimitivesUseOneBoundedClockAndExposeFreezeAndAccessibilitySeams() => StaTest.Run(() =>
    {
        var decoration = new NewTabDecorationControl();
        decoration.SetCoordinatorAsset(Packaged(NewTabDecorationControl.DefaultV4BaseAssetRelativePath), false);
        StaTest.Prepare(decoration, 1200, 800);
        Assert.True(decoration.HasV5Asset);
        Assert.True(decoration.HasV5CelestialOverlay);
        decoration.RenderTimelineFrame(0, 1130);
        Assert.Equal(NewTabDecorationControl.OpeningRotationStartAngle, decoration.CurrentOpeningRotationAngle, 8);
        decoration.RenderTimelineFrame(30, 1130);
        Assert.Equal(1, decoration.OpeningRotationProgress, 8);
        Assert.Equal(0, decoration.CurrentOpeningRotationAngle, 8);
        decoration.FreezeNonStellarMotion = true;
        Assert.True(decoration.CelestialOverlay.FreezeMotion);

        var hub = new OrbitStellarOrbitVisual
        {
            Kind = OrbitEmberStarKind.TabGroup,
            OrbitingImageLabel = "Selected workspace image",
            ReducedMotion = true,
        };
        StaTest.Prepare(hub, 160, 160);
        Assert.True(hub.UsesTransparentBackground);
        Assert.True(hub.CentralStar.UsesV5Artwork);
        Assert.Equal(OrbitEmberStar.LatestAtlasFrameCount, hub.CentralStar.ResolvedFrameCount);
        var peer = UIElementAutomationPeer.CreatePeerForElement(hub);
        Assert.NotNull(peer);
        Assert.Equal("Workspace star with orbiting selected image", peer.GetName());
        Assert.Contains("freeze", peer.GetHelpText(), StringComparison.OrdinalIgnoreCase);
        Assert.Null(UIElementAutomationPeer.CreatePeerForElement(hub.CentralStar));

        foreach (var type in new[]
        {
            typeof(NewTabDecorationControl),
            typeof(OrbitNewTabCelestialOverlay),
            typeof(OrbitStellarOrbitVisual),
            typeof(OrbitEmberStar),
        })
        {
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.DoesNotContain(fields, field => typeof(DispatcherTimer).IsAssignableFrom(field.FieldType));
            Assert.DoesNotContain(fields, field => typeof(AnimationClock).IsAssignableFrom(field.FieldType));
        }
        Assert.InRange(OrbitStellarOrbitVisual.SharedAnimationFramesPerSecond, 1, 24);
    });

    [Fact]
    public void HoverFreezeStopsRingsButNotTheCompleteSolarAtlas() => StaTest.Run(() =>
    {
        if (SystemParameters.HighContrast)
        {
            return;
        }

        var baseline = OrbitStellarOrbitVisual.SharedClockSubscriberCount;
        var hub = new OrbitStellarOrbitVisual
        {
            Kind = OrbitEmberStarKind.Favorite,
            IsActive = true,
        };
        var window = new Window
        {
            Content = hub,
            Width = 220,
            Height = 220,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
        };
        window.Show();
        try
        {
            window.UpdateLayout();
            Assert.True(hub.IsRegisteredWithSharedClock);
            Assert.True(hub.CentralStar.IsRegisteredWithSharedClock);
            Assert.Equal(baseline + 2, OrbitStellarOrbitVisual.SharedClockSubscriberCount);
            Assert.Equal(1, OrbitStellarOrbitVisual.SharedClockRenderingHandlerCount);

            DetachFromAutomaticClock(hub, hub.CentralStar);
            hub.FreezeNonStellarMotion = true;
            var ringPhase = hub.CurrentOuterRingPhase;
            var ringFrames = hub.ReceivedMotionFrameCount;
            var starFrames = hub.CentralStar.ReceivedMotionFrameCount;
            ApplyCompatibilityFrame(hub, 0.76);
            ApplyCompatibilityFrame(hub.CentralStar, 0.76);
            Assert.Equal(ringPhase, hub.CurrentOuterRingPhase, 10);
            Assert.Equal(ringFrames, hub.ReceivedMotionFrameCount);
            Assert.Equal(starFrames + 1, hub.CentralStar.ReceivedMotionFrameCount);

            hub.ReduceVisualNoise = true;
            Assert.False(hub.CentralStar.UseRasterArtwork);
            Assert.False(hub.IsNonStellarMotionActive);
        }
        finally
        {
            window.Close();
        }
        Assert.Equal(baseline, OrbitStellarOrbitVisual.SharedClockSubscriberCount);
    });

    [Fact]
    public void StellarOrbitSuspendsBothSubscribersWhileHostWindowIsMinimized() => StaTest.Run(() =>
    {
        if (SystemParameters.HighContrast)
        {
            return;
        }

        var baseline = OrbitStellarOrbitVisual.SharedClockSubscriberCount;
        var hub = new OrbitStellarOrbitVisual();
        var window = new Window
        {
            Content = hub,
            Width = 220,
            Height = 220,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
        };
        window.Show();
        try
        {
            window.UpdateLayout();
            Assert.Equal(baseline + 2, OrbitStellarOrbitVisual.SharedClockSubscriberCount);

            window.WindowState = WindowState.Minimized;
            window.Dispatcher.Invoke(DispatcherPriority.Background, () => { });
            Assert.False(hub.IsRegisteredWithSharedClock);
            Assert.False(hub.CentralStar.IsRegisteredWithSharedClock);
            Assert.Equal(baseline, OrbitStellarOrbitVisual.SharedClockSubscriberCount);

            window.WindowState = WindowState.Normal;
            window.Dispatcher.Invoke(DispatcherPriority.Background, () => { });
            Assert.Equal(baseline + 2, OrbitStellarOrbitVisual.SharedClockSubscriberCount);
        }
        finally
        {
            window.Close();
        }
        Assert.Equal(baseline, OrbitStellarOrbitVisual.SharedClockSubscriberCount);
    });

    [Fact]
    public void CaptureV5HandoffReviewFramesWhenRequested() => StaTest.Run(() =>
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("ORBIT_CAPTURE_NEW_TAB_V5"),
            "1",
            StringComparison.Ordinal) ||
            SystemParameters.HighContrast)
        {
            return;
        }

        var output = Path.Combine(FindRepositoryRoot(), "artifacts", "verification");
        Directory.CreateDirectory(output);
        CaptureReviewScenario(output, "new-tab-v5-handoff-opening.png", 0.35, ReviewMode.Normal);
        CaptureReviewScenario(output, "new-tab-v5-handoff-settled.png", 4.8, ReviewMode.Normal);
        CaptureReviewScenario(output, "new-tab-v5-handoff-celestial-phase.png", 12.4, ReviewMode.Normal);
        CaptureReviewScenario(output, "new-tab-v5-handoff-hover-freeze.png", 12.4, ReviewMode.HoverFreeze);
        CaptureReviewScenario(output, "new-tab-v5-handoff-reduced-motion.png", 12.4, ReviewMode.ReducedMotion);
        CaptureReviewScenario(output, "new-tab-v5-handoff-reduced-noise.png", 12.4, ReviewMode.ReducedNoise);
    });

    private static void CaptureReviewScenario(
        string output,
        string fileName,
        double elapsedSeconds,
        ReviewMode mode)
    {
        var decoration = new NewTabDecorationControl();
        decoration.SetCoordinatorAsset(Packaged(NewTabDecorationControl.DefaultV4BaseAssetRelativePath), false);
        var favorite = new OrbitStellarOrbitVisual
        {
            Kind = OrbitEmberStarKind.Favorite,
            OrbitingImageSource = CreateReviewMedia(OrbitVisualTheme.SeaGlass, favorite: true),
            OrbitingImageLabel = "Example site favicon",
        };
        var workspace = new OrbitStellarOrbitVisual
        {
            Kind = OrbitEmberStarKind.TabGroup,
            OrbitingImageSource = CreateReviewMedia(OrbitVisualTheme.PrivateViolet, favorite: false),
            OrbitingImageLabel = "Example selected workspace image",
        };
        var detailCard = new Border
        {
            Visibility = Visibility.Collapsed,
            Padding = new Thickness(14, 10, 14, 10),
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromArgb(238, 20, 31, 43)),
            BorderBrush = OrbitVisualTheme.SeaGlass,
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = "Workspace details · motion held while this card is visible",
                Foreground = OrbitVisualTheme.Ink,
                FontSize = 14,
            },
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 28),
        };
        var root = CreateReviewSurface(decoration, favorite, workspace, detailCard);
        var window = new Window
        {
            Content = root,
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
            window.Dispatcher.Invoke(DispatcherPriority.Render, () => { });
            DetachFromAutomaticClock(
                decoration,
                decoration.CelestialOverlay,
                favorite,
                favorite.CentralStar,
                workspace,
                workspace.CentralStar);
            SetReviewPhase(decoration, favorite, workspace, elapsedSeconds);

            if (mode == ReviewMode.HoverFreeze)
            {
                decoration.FreezeNonStellarMotion = true;
                favorite.FreezeNonStellarMotion = true;
                workspace.FreezeNonStellarMotion = true;
                detailCard.Visibility = Visibility.Visible;
                ApplyCompatibilityFrame(favorite.CentralStar, 0.72);
                ApplyCompatibilityFrame(workspace.CentralStar, 0.31);
            }
            else if (mode == ReviewMode.ReducedMotion)
            {
                decoration.ReducedMotion = true;
                favorite.ReducedMotion = true;
                workspace.ReducedMotion = true;
            }
            else if (mode == ReviewMode.ReducedNoise)
            {
                decoration.SetCoordinatorAsset(
                    Packaged(NewTabDecorationControl.DefaultV4BaseAssetRelativePath),
                    reduceVisualNoise: true);
                favorite.ReduceVisualNoise = true;
                workspace.ReduceVisualNoise = true;
            }

            window.UpdateLayout();
            window.Dispatcher.Invoke(DispatcherPriority.Render, () => { });
            Capture(root, Path.Combine(output, fileName), 1200, 800);
        }
        finally
        {
            window.Close();
        }
    }

    private static Grid CreateReviewSurface(
        NewTabDecorationControl decoration,
        OrbitStellarOrbitVisual favorite,
        OrbitStellarOrbitVisual workspace,
        Border detailCard)
    {
        var root = new Grid
        {
            Width = 1200,
            Height = 800,
            Background = new SolidColorBrush(Color.FromRgb(9, 16, 25)),
        };
        root.Children.Add(decoration);

        var foreground = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(80, 34, 80, 24),
        };
        foreground.Children.Add(new OrbitNewTabLogo
        {
            Width = 116,
            Height = 116,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        foreground.Children.Add(new TextBlock
        {
            Text = "Orbit Navigator",
            Foreground = OrbitVisualTheme.Ink,
            FontSize = 29,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 4),
        });
        foreground.Children.Add(new TextBlock
        {
            Text = "Navigate your next orbit",
            Foreground = OrbitVisualTheme.MutedInk,
            FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 18),
        });
        foreground.Children.Add(new Border
        {
            Width = 650,
            Height = 52,
            CornerRadius = new CornerRadius(18),
            Background = new SolidColorBrush(Color.FromArgb(224, 20, 31, 43)),
            BorderBrush = OrbitVisualTheme.SeaGlass,
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = "Search or enter an address",
                Foreground = OrbitVisualTheme.MutedInk,
                FontSize = 15,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(20, 0, 20, 0),
            },
        });

        var cards = new Grid { Width = 720, Margin = new Thickness(0, 34, 0, 0) };
        cards.ColumnDefinitions.Add(new ColumnDefinition());
        cards.ColumnDefinitions.Add(new ColumnDefinition());
        cards.Children.Add(CreateReviewCard(favorite, "Favorites", "Sites stay recognizable while a full solar corona evolves."));
        var workspaceCard = CreateReviewCard(workspace, "Workspaces", "A chosen workspace image travels on its own calm orbit.");
        Grid.SetColumn(workspaceCard, 1);
        cards.Children.Add(workspaceCard);
        foreground.Children.Add(cards);
        root.Children.Add(foreground);
        root.Children.Add(detailCard);
        return root;
    }

    private static Border CreateReviewCard(OrbitStellarOrbitVisual visual, string title, string description)
    {
        var stack = new StackPanel();
        stack.Children.Add(visual);
        stack.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = OrbitVisualTheme.Ink,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 6),
        });
        stack.Children.Add(new TextBlock
        {
            Text = description,
            Foreground = OrbitVisualTheme.MutedInk,
            FontSize = 13,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Width = 255,
        });
        return new Border
        {
            Margin = new Thickness(10),
            Padding = new Thickness(18),
            CornerRadius = new CornerRadius(24),
            Background = new SolidColorBrush(Color.FromArgb(218, 18, 29, 41)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(120, 86, 182, 205)),
            BorderThickness = new Thickness(1),
            Child = stack,
        };
    }

    private static ImageSource CreateReviewMedia(Brush brush, bool favorite)
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(
            brush,
            new Pen(OrbitVisualTheme.Ink, 1.3),
            favorite
                ? Geometry.Parse("M3,3 H21 V21 H3 Z M7,7 H17 V17 H7 Z")
                : Geometry.Parse("M3,20 L9,10 L13,15 L17,7 L22,20 Z")));
        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private static void SetReviewPhase(
        NewTabDecorationControl decoration,
        OrbitStellarOrbitVisual favorite,
        OrbitStellarOrbitVisual workspace,
        double elapsedSeconds)
    {
        InvokeRenderAtElapsedSeconds(decoration, elapsedSeconds);
        InvokeRenderAtElapsedSeconds(decoration.CelestialOverlay, elapsedSeconds);
        InvokeRenderAtElapsedSeconds(favorite, elapsedSeconds);
        InvokeRenderAtElapsedSeconds(workspace, elapsedSeconds);
        ApplyCompatibilityFrame(favorite.CentralStar, (elapsedSeconds % 8) / 8);
        ApplyCompatibilityFrame(workspace.CentralStar, ((elapsedSeconds + 2.7) % 8) / 8);
        decoration.Dispatcher.Invoke(DispatcherPriority.Render, () => { });
    }

    private static void InvokeRenderAtElapsedSeconds(object target, double elapsedSeconds)
    {
        var method = target.GetType().GetMethod(
            "RenderAtElapsedSeconds",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            target.GetType().GetMethod(
                "RenderSceneAtElapsedSeconds",
                BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(target, [elapsedSeconds]);
    }

    private static void Capture(FrameworkElement element, string path, int width, int height)
    {
        Assert.True(element.IsLoaded);
        element.UpdateLayout();
        element.Dispatcher.Invoke(DispatcherPriority.Render, () => { });
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        bitmap.Freeze();
        Assert.True(Alpha(bitmap).Count(value => value > 0) > width * height / 2);
        Assert.True(CountBrightPixels(bitmap, new Int32Rect(240, 24, 720, 290)) > 900);
        Assert.True(CountBrightPixels(bitmap, new Int32Rect(245, 330, 350, 270)) > 450);
        Assert.True(CountBrightPixels(bitmap, new Int32Rect(605, 330, 350, 270)) > 450);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static int CountBrightPixels(BitmapSource source, Int32Rect bounds)
    {
        var cropped = new CroppedBitmap(source, bounds);
        var pixels = Pixels(cropped);
        var count = 0;
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            if (pixels[offset + 3] >= 32 &&
                Math.Max(pixels[offset], Math.Max(pixels[offset + 1], pixels[offset + 2])) >= 145)
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

    private enum ReviewMode
    {
        Normal,
        HoverFreeze,
        ReducedMotion,
        ReducedNoise,
    }

    private static void DetachFromAutomaticClock(params FrameworkElement[] targets)
    {
        var clockType = typeof(NewTabDecorationControl).Assembly.GetType(
            "OrbitNavigator.Presentation.Wpf.OrbitSharedVisualMotionClock",
            throwOnError: true)!;
        var unregister = clockType.GetMethod(
            "Unregister",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var target in targets)
        {
            unregister.Invoke(null, [target]);
        }
    }

    private static void ApplyCompatibilityFrame(object target, double phase)
    {
        var method = target.GetType().GetMethod(
            "ApplySharedMotionFrame",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(target, [phase]);
    }

    private static BitmapSource Load(string relativePath)
    {
        using var stream = File.OpenRead(Packaged(relativePath));
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    private static string Packaged(string relativePath)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Required v5 visual asset was missing: {path}");
        return path;
    }

    private static BitmapSource Frame(BitmapSource atlas, int index, int columns, int rows)
    {
        var width = atlas.PixelWidth / columns;
        var height = atlas.PixelHeight / rows;
        Assert.Equal(width, height);
        var frame = new CroppedBitmap(
            atlas,
            new Int32Rect((index % columns) * width, (index / columns) * height, width, height));
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
        for (var index = 0; index < alpha.Length; index++)
        {
            alpha[index] = pixels[(index * 4) + 3];
        }
        return alpha;
    }

    private static double IntegratedLuminance(byte[] pixels)
    {
        var total = 0d;
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            total += (0.0722 * pixels[offset] + 0.7152 * pixels[offset + 1] +
                0.2126 * pixels[offset + 2]) * pixels[offset + 3];
        }
        return total;
    }

    private static Point AlphaCentroid(byte[] alpha, int width, int height)
    {
        var total = 0d;
        var xTotal = 0d;
        var yTotal = 0d;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = alpha[(y * width) + x];
                if (value < 32)
                {
                    continue;
                }
                total += value;
                xTotal += x * value;
                yTotal += y * value;
            }
        }
        Assert.True(total > 0);
        return new Point(xTotal / total, yTotal / total);
    }

    private static int CountVisibleComponents(byte[] alpha, int width, int height)
    {
        var visited = new bool[alpha.Length];
        var pending = new Queue<int>();
        var components = 0;
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
}
