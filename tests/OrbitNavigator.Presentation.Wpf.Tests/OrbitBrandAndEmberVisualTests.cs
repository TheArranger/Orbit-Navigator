using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using OrbitNavigator.Presentation.Wpf;

using Xunit;

namespace OrbitNavigator.Presentation.Wpf.Tests;

[Collection("WPF focus-sensitive")]
public sealed class OrbitBrandAndEmberVisualTests
{
    [Fact]
    public void NewTabV3FieldIsAFullBleedAlphaAtmosphereWithAQuietCenter() => StaTest.Run(() =>
    {
        var artwork = LoadAsset(NewTabDecorationControl.DefaultV3AssetRelativePath);
        Assert.Equal(3, NewTabDecorationControl.ArtworkVersion);
        Assert.Equal(1672, artwork.PixelWidth);
        Assert.Equal(941, artwork.PixelHeight);
        Assert.True(PartialAlphaPixelCount(artwork) > artwork.PixelWidth * artwork.PixelHeight * 0.95);
        Assert.Equal(0, OpaquePixelCount(artwork));

        var (pixels, width, height) = Pixels(artwork);
        var centerLuminance = AverageLuminance(pixels, width, height, 0.38, 0.32, 0.62, 0.68);
        var edgeLuminance = AverageLuminance(pixels, width, height, 0.00, 0.00, 0.18, 1.00);
        Assert.True(centerLuminance < edgeLuminance * 0.62, "The content center was not sufficiently calm.");
        Assert.True(NewTabDecorationControl.ShouldShowVisualField(false, false));
        Assert.False(NewTabDecorationControl.ShouldShowVisualField(true, false));
        Assert.False(NewTabDecorationControl.ShouldShowVisualField(false, true));
    });

    [Fact]
    public void NewTabDecorationPromotesLegacyHostPathAndSharesTheVisibleOnlyClock() => StaTest.Run(() =>
    {
        if (SystemParameters.HighContrast)
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var legacyPath = Path.Combine(
            repositoryRoot.FullName,
            "assets",
            "new-tab",
            NewTabDecorationControl.LegacyV2AssetFileName);
        var baselineSubscribers = NewTabDecorationControl.SharedClockSubscriberCount;
        var decoration = new NewTabDecorationControl();
        decoration.SetCoordinatorAsset(legacyPath, reduceVisualNoise: false);
        var star = new OrbitEmberStar(OrbitEmberStarKind.Favorite)
        {
            IsActive = true,
            ArtworkAtlasSource = LoadAsset(OrbitEmberStar.FavoriteAtlasRelativePath),
        };
        var panel = new Grid();
        panel.Children.Add(decoration);
        panel.Children.Add(star);
        var window = new Window
        {
            Content = panel,
            Width = 720,
            Height = 460,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
        };

        window.Show();
        try
        {
            window.UpdateLayout();
            Assert.True(decoration.HasCoordinatorAsset);
            Assert.True(decoration.HasV3Asset);
            Assert.EndsWith("orbit-deep-space-field-v3.png", decoration.ResolvedAssetPath, StringComparison.OrdinalIgnoreCase);
            Assert.True(decoration.IsRegisteredWithSharedClock);
            Assert.True(star.IsRegisteredWithSharedClock);
            Assert.True(decoration.CelestialOverlay.IsRegisteredWithSharedClock);
            Assert.Equal(baselineSubscribers + 3, NewTabDecorationControl.SharedClockSubscriberCount);
            Assert.Equal(1, NewTabDecorationControl.SharedClockRenderingHandlerCount);

            decoration.ReducedMotion = true;
            star.ReducedMotion = true;
            Assert.False(decoration.IsRegisteredWithSharedClock);
            Assert.False(star.IsRegisteredWithSharedClock);
            Assert.Equal(baselineSubscribers, NewTabDecorationControl.SharedClockSubscriberCount);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void ApprovedProgramLogoHasRealAlphaAndNewTabPrimitivePreservesItsAccessibilityRole() => StaTest.Run(() =>
    {
        var artwork = LoadAsset(OrbitNewTabLogo.ProgramLogoRelativePath);
        Assert.Equal("New Tab page", OrbitNewTabLogo.IntendedSurface);
        Assert.Equal("Approved Orbit Navigator program-wide identity", OrbitNewTabLogo.BrandingApprovalStatus);
        Assert.Equal(OrbitNewTabLogo.ProgramLogoRelativePath, OrbitNewTabLogo.ProgramLogoCandidateRelativePath);
        Assert.NotEqual(OrbitNavigatorIdentity.RoutePathData, OrbitNewTabLogo.RoutePathData);
        AssertTransparentCorners(artwork, "approved program logo");
        Assert.True(TransparentPixelRatio(artwork) > 0.25);

        foreach (var dpi in new[] { 96d, 120d, 144d, 192d })
        {
            var logo = new OrbitNewTabLogo
            {
                Width = 96,
                Height = 96,
                ArtworkSource = artwork,
            };
            var (pixels, pixelWidth, pixelHeight) = Render(logo, 96, 96, dpi);

            Assert.True(logo.UsesTransparentBackground);
            Assert.True(logo.HasArtwork);
            Assert.Equal(0, AlphaAt(pixels, pixelWidth, 0, 0));
            Assert.Equal(0, AlphaAt(pixels, pixelWidth, pixelWidth - 1, 0));
            Assert.Equal(0, AlphaAt(pixels, pixelWidth, 0, pixelHeight - 1));
            Assert.Equal(0, AlphaAt(pixels, pixelWidth, pixelWidth - 1, pixelHeight - 1));
            Assert.True(VisiblePixelCount(pixels) > 900, $"Approved program logo was too faint at {dpi} DPI.");

            var peer = UIElementAutomationPeer.CreatePeerForElement(logo);
            Assert.NotNull(peer);
            Assert.Equal(AutomationControlType.Image, peer.GetAutomationControlType());
            Assert.Equal("Orbit Navigator New Tab logo", peer.GetName());
            Assert.Contains("orbital path", AutomationProperties.GetHelpText(logo));
            Assert.Contains("program logo", AutomationProperties.GetHelpText(logo));
            Assert.Contains("New Tab page", AutomationProperties.GetHelpText(logo));
        }
    });

    [Fact]
    public void ApprovedWindowsIdentityOutputsContainExactTransparentFramesAndGeneratorUsesApprovedSource() => StaTest.Run(() =>
    {
        var expectedSizes = new[] { 16, 20, 24, 32, 40, 48, 64, 256 };
        var repositoryRoot = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(repositoryRoot.FullName, "scripts", "Build-IdentityIcon.ps1"));
        Assert.Contains("orbit-navigator-program-logo-v2.png", script);
        Assert.DoesNotContain("New-RoundedRectanglePath", script);

        var pngs = expectedSizes.ToDictionary(
            size => size,
            size => LoadAsset($"assets/branding/windows/orbit-navigator-{size}.png"));
        foreach (var (size, png) in pngs)
        {
            Assert.Equal(size, png.PixelWidth);
            Assert.Equal(size, png.PixelHeight);
            AssertTransparentCorners(png, $"Windows {size}px identity");
            Assert.True(TransparentPixelRatio(png) > 0.20, $"Windows {size}px identity had an opaque matte.");
            Assert.True(PartialAlphaPixelCount(png) > 0, $"Windows {size}px identity lost its antialiased alpha edge.");
        }

        var icoPath = Path.Combine(repositoryRoot.FullName, "assets", "branding", "orbit-navigator.ico");
        using var icoStream = File.OpenRead(icoPath);
        var decoder = new IconBitmapDecoder(
            icoStream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        Assert.Equal(expectedSizes, decoder.Frames.Select(frame => frame.PixelWidth));
        Assert.All(decoder.Frames, frame => Assert.Equal(frame.PixelWidth, frame.PixelHeight));
        foreach (var frame in decoder.Frames)
        {
            AssertTransparentCorners(frame, $"ICO {frame.PixelWidth}px frame");
            Assert.True(TransparentPixelRatio(frame) > 0.20, $"ICO {frame.PixelWidth}px frame had an opaque matte.");
            Assert.Equal(Pixels(pngs[frame.PixelWidth]).Pixels, Pixels(frame).Pixels);
        }
    });

    [Theory]
    [InlineData(OrbitEmberStarKind.Favorite, OrbitEmberStar.FavoriteAtlasRelativePath)]
    [InlineData(OrbitEmberStarKind.TabGroup, OrbitEmberStar.TabGroupAtlasRelativePath)]
    public void WholeStarAtlasesHaveTransparentCellsAndNoDetachedPlasmaComponents(
        OrbitEmberStarKind kind,
        string relativePath) => StaTest.Run(() =>
    {
        var atlas = LoadAsset(relativePath);
        Assert.Equal(OrbitEmberStar.AtlasColumns, atlas.PixelWidth / (atlas.PixelHeight / OrbitEmberStar.AtlasRows));
        Assert.Equal(0, atlas.PixelWidth % OrbitEmberStar.AtlasColumns);
        Assert.Equal(0, atlas.PixelHeight % OrbitEmberStar.AtlasRows);
        AssertTransparentCorners(atlas, $"{kind} atlas");
        Assert.True(TransparentPixelRatio(atlas) > 0.35);

        var frameWidth = atlas.PixelWidth / OrbitEmberStar.AtlasColumns;
        var frameHeight = atlas.PixelHeight / OrbitEmberStar.AtlasRows;
        Assert.Equal(frameWidth, frameHeight);
        for (var index = 0; index < OrbitEmberStar.AtlasFrameCount; index++)
        {
            var frame = new CroppedBitmap(
                atlas,
                new Int32Rect(
                    (index % OrbitEmberStar.AtlasColumns) * frameWidth,
                    (index / OrbitEmberStar.AtlasColumns) * frameHeight,
                    frameWidth,
                    frameHeight));
            AssertTransparentCorners(frame, $"{kind} frame {index}");
            AssertTransparentOuterRing(frame, $"{kind} frame {index}");
            AssertSingleVisibleComponent(frame, $"{kind} frame {index}");
        }
    });

    [Theory]
    [InlineData(OrbitEmberStarKind.Favorite, OrbitEmberStar.FavoriteAtlasRelativePath)]
    [InlineData(OrbitEmberStarKind.TabGroup, OrbitEmberStar.TabGroupAtlasRelativePath)]
    public void V3AtlasEvolvesRealSurfaceDetailWithoutChangingTheValidatedSilhouette(
        OrbitEmberStarKind kind,
        string relativePath) => StaTest.Run(() =>
    {
        var atlas = LoadAsset(relativePath);
        var frameWidth = atlas.PixelWidth / OrbitEmberStar.AtlasColumns;
        var frameHeight = atlas.PixelHeight / OrbitEmberStar.AtlasRows;
        var first = new CroppedBitmap(atlas, new Int32Rect(0, 0, frameWidth, frameHeight));
        var evolved = new CroppedBitmap(
            atlas,
            new Int32Rect(2 * frameWidth, frameHeight, frameWidth, frameHeight));
        var firstPixels = Pixels(first).Pixels;
        var evolvedPixels = Pixels(evolved).Pixels;
        var visiblePixels = 0;
        var changedSurfacePixels = 0;
        for (var offset = 0; offset < firstPixels.Length; offset += 4)
        {
            Assert.Equal(firstPixels[offset + 3], evolvedPixels[offset + 3]);
            if (firstPixels[offset + 3] <= 20)
            {
                continue;
            }

            visiblePixels++;
            var colorDistance = Math.Abs(firstPixels[offset] - evolvedPixels[offset]) +
                Math.Abs(firstPixels[offset + 1] - evolvedPixels[offset + 1]) +
                Math.Abs(firstPixels[offset + 2] - evolvedPixels[offset + 2]);
            if (colorDistance >= 18)
            {
                changedSurfacePixels++;
            }
        }

        Assert.True(
            changedSurfacePixels > visiblePixels / 7,
            $"{kind} did not evolve enough connected plasma detail between frames.");
    });

    [Fact]
    public void RepeatedStarsUseStableSharedClockPhaseStaggerWithoutExtraTimers() => StaTest.Run(() =>
    {
        var first = new OrbitEmberStar(OrbitEmberStarKind.Favorite);
        var second = new OrbitEmberStar(OrbitEmberStarKind.Favorite);
        var group = new OrbitEmberStar(OrbitEmberStarKind.TabGroup);

        Assert.InRange(first.MotionPhaseOffset, 0, 1 - double.Epsilon);
        Assert.InRange(second.MotionPhaseOffset, 0, 1 - double.Epsilon);
        Assert.InRange(group.MotionPhaseOffset, 0, 1 - double.Epsilon);
        Assert.NotEqual(first.MotionPhaseOffset, second.MotionPhaseOffset);
        Assert.Equal(17, OrbitEmberStar.PhaseStaggerSlotCount);

        first.MotionPhaseOffset = 0.25;
        Assert.Equal(0.25, first.MotionPhaseOffset);
        Assert.Throws<ArgumentException>(() => first.MotionPhaseOffset = 1);
    });

    [Theory]
    [InlineData(OrbitEmberStarKind.Favorite, OrbitEmberStar.FavoriteAtlasRelativePath)]
    [InlineData(OrbitEmberStarKind.TabGroup, OrbitEmberStar.TabGroupAtlasRelativePath)]
    public void ActiveStarUsesStaticWholeStarFrameAndNonColorCueWhenMotionIsReduced(
        OrbitEmberStarKind kind,
        string relativePath) => StaTest.Run(() =>
    {
        var atlas = LoadAsset(relativePath);
        var inactive = new OrbitEmberStar(kind)
        {
            Width = 56,
            Height = 56,
            ReducedMotion = true,
            IsActive = false,
            ArtworkAtlasSource = atlas,
        };
        var active = new OrbitEmberStar(kind)
        {
            Width = 56,
            Height = 56,
            ReducedMotion = true,
            IsActive = true,
            ArtworkAtlasSource = atlas,
        };

        var (inactivePixels, _, _) = Render(inactive, 56, 56, 192);
        var (activePixels, pixelWidth, pixelHeight) = Render(active, 56, 56, 192);
        Assert.Equal("Hollow stellar disc without corona", inactive.ShapeStateCue);
        Assert.Equal("Filled stellar disc with attached corona", active.ShapeStateCue);
        Assert.True(active.UsesTransparentBackground);
        Assert.True(active.HasArtworkAtlas);
        Assert.True(
            VisiblePixelCount(activePixels) > VisiblePixelCount(inactivePixels) * 2,
            $"{kind} active state did not retain its filled non-color shape cue.");
        Assert.Equal(0, AlphaAt(activePixels, pixelWidth, 0, 0));
        Assert.Equal(0, AlphaAt(activePixels, pixelWidth, pixelWidth - 1, pixelHeight - 1));
        Assert.False(active.IsRegisteredWithSharedClock);
        Assert.Equal(0, active.CurrentEmberOffset);
        Assert.Equal(0, OrbitEmberStar.MaximumDetachedFragmentOffset);
        Assert.Equal(0, active.CurrentFrameBlend);
        Assert.Equal(0, active.ReceivedMotionFrameCount);

        var second = Render(active, 56, 56, 192).Pixels;
        Assert.Equal(activePixels, second);
    });

    [Theory]
    [InlineData(OrbitEmberStarKind.Favorite, "Favorite star, active")]
    [InlineData(OrbitEmberStarKind.TabGroup, "Tab group star, active")]
    public void EmberStarsSelfDescribeSemanticStateWithoutDependingOnMotion(
        OrbitEmberStarKind kind,
        string expectedName) => StaTest.Run(() =>
    {
        var star = new OrbitEmberStar(kind) { IsActive = true, ReducedMotion = true };
        StaTest.Prepare(star, 28, 28);

        var peer = UIElementAutomationPeer.CreatePeerForElement(star);
        Assert.NotNull(peer);
        Assert.Equal(AutomationControlType.Image, peer.GetAutomationControlType());
        Assert.Equal(expectedName, peer.GetName());
        Assert.Equal("Active", AutomationProperties.GetItemStatus(star));
        Assert.Contains("filled", AutomationProperties.GetHelpText(star), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("attached corona", AutomationProperties.GetHelpText(star));
        Assert.Contains("decorative", AutomationProperties.GetHelpText(star));

        star.IsActive = false;
        Assert.EndsWith("inactive", AutomationProperties.GetName(star));
        Assert.Equal("Inactive", AutomationProperties.GetItemStatus(star));
        Assert.Contains("hollow", AutomationProperties.GetHelpText(star), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("without plasma motion", AutomationProperties.GetHelpText(star));
    });

    [Theory]
    [InlineData(OrbitEmberStarKind.Favorite, OrbitEmberStar.FavoriteAtlasRelativePath)]
    [InlineData(OrbitEmberStarKind.TabGroup, OrbitEmberStar.TabGroupAtlasRelativePath)]
    public void MotionCrossfadesCompleteConnectedStarsAndReducedMotionFreezes(
        OrbitEmberStarKind kind,
        string relativePath) => StaTest.Run(() =>
    {
        if (SystemParameters.HighContrast)
        {
            return;
        }

        var star = new OrbitEmberStar(kind)
        {
            Width = 72,
            Height = 72,
            IsActive = true,
            ArtworkAtlasSource = LoadAsset(relativePath),
        };
        var window = new Window
        {
            Content = star,
            Width = 100,
            Height = 100,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
        };
        var applyFrame = typeof(OrbitEmberStar).GetMethod(
            "ApplySharedMotionFrame",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        window.Show();
        try
        {
            window.UpdateLayout();
            Assert.True(star.IsRegisteredWithSharedClock);
            applyFrame.Invoke(star, [0.03]);
            var first = Render(star, 72, 72, 192);
            applyFrame.Invoke(star, [0.57]);
            var second = Render(star, 72, 72, 192);
            Assert.NotEqual(first.Pixels, second.Pixels);
            // Bilinear scaling can create a few isolated antialias-fringe
            // pixels. At least 99.8% must remain one coherent stellar body.
            AssertSingleVisibleComponent(first.Pixels, first.PixelWidth, first.PixelHeight, $"{kind} rendered phase one", minimumConnectedRatio: 0.998);
            AssertSingleVisibleComponent(second.Pixels, second.PixelWidth, second.PixelHeight, $"{kind} rendered phase two", minimumConnectedRatio: 0.998);
            Assert.Equal(0, star.CurrentEmberOffset);

            star.ReducedMotion = true;
            var frozen = Render(star, 72, 72, 192).Pixels;
            applyFrame.Invoke(star, [0.81]);
            Assert.Equal(frozen, Render(star, 72, 72, 192).Pixels);
            Assert.False(star.IsRegisteredWithSharedClock);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void VisibleActiveStarsShareOneClockAndStopWhenHiddenOrReducedMotion() => StaTest.Run(() =>
    {
        const int starCount = 64;
        var baselineSubscribers = OrbitEmberStar.SharedClockSubscriberCount;
        var panel = new UniformGrid { Columns = 8 };
        var stars = Enumerable.Range(0, starCount)
            .Select(index => new OrbitEmberStar(index % 2 == 0
                ? OrbitEmberStarKind.Favorite
                : OrbitEmberStarKind.TabGroup)
            {
                Width = 28,
                Height = 28,
                IsActive = true,
            })
            .ToArray();
        foreach (var star in stars)
        {
            panel.Children.Add(star);
        }

        var window = new Window
        {
            Content = panel,
            Width = 320,
            Height = 260,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
        };
        window.Show();
        try
        {
            window.UpdateLayout();
            Assert.All(stars, star => Assert.True(star.IsVisible));
            Assert.All(stars, star => Assert.True(star.IsRegisteredWithSharedClock));
            Assert.Equal(baselineSubscribers + starCount, OrbitEmberStar.SharedClockSubscriberCount);
            Assert.Equal(1, OrbitEmberStar.SharedClockRenderingHandlerCount);

            stars[0].Visibility = Visibility.Collapsed;
            window.UpdateLayout();
            Assert.False(stars[0].IsRegisteredWithSharedClock);
            Assert.Equal(baselineSubscribers + starCount - 1, OrbitEmberStar.SharedClockSubscriberCount);

            foreach (var star in stars)
            {
                star.ReducedMotion = true;
            }
            Assert.All(stars, star => Assert.False(star.IsRegisteredWithSharedClock));
            Assert.Equal(baselineSubscribers, OrbitEmberStar.SharedClockSubscriberCount);
            if (baselineSubscribers == 0)
            {
                Assert.Equal(0, OrbitEmberStar.SharedClockRenderingHandlerCount);
            }
        }
        finally
        {
            window.Close();
        }

        Assert.Equal(baselineSubscribers, OrbitEmberStar.SharedClockSubscriberCount);
    });

    [Fact]
    public void MotionBudgetUsesOneVisibleOnlyClockAndNoPerItemTimerOrDetachedMotion() => StaTest.Run(() =>
    {
        var instanceFields = typeof(OrbitEmberStar).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.DoesNotContain(instanceFields, field => typeof(DispatcherTimer).IsAssignableFrom(field.FieldType));
        Assert.DoesNotContain(instanceFields, field => typeof(AnimationClock).IsAssignableFrom(field.FieldType));
        var decorationFields = typeof(NewTabDecorationControl).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.DoesNotContain(decorationFields, field => typeof(DispatcherTimer).IsAssignableFrom(field.FieldType));
        Assert.DoesNotContain(decorationFields, field => typeof(AnimationClock).IsAssignableFrom(field.FieldType));

        var clockType = typeof(OrbitEmberStar).Assembly.GetType(
            "OrbitNavigator.Presentation.Wpf.OrbitSharedVisualMotionClock",
            throwOnError: true)!;
        var clockFields = clockType.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.DoesNotContain(clockFields, field => typeof(DispatcherTimer).IsAssignableFrom(field.FieldType));
        Assert.InRange(OrbitEmberStar.SharedAnimationFramesPerSecond, 1, 24);
        Assert.Equal(TimeSpan.FromSeconds(8), OrbitEmberStar.SharedAnimationCycleDuration);
        Assert.Equal(0, OrbitEmberStar.MaximumAnimatedOffset);
        Assert.Equal(0, OrbitEmberStar.MaximumDetachedFragmentOffset);
        Assert.Equal(12, OrbitEmberStar.AtlasFrameCount);
        Assert.Equal(17, OrbitEmberStar.PhaseStaggerSlotCount);
        Assert.Equal(3, OrbitEmberStar.ArtworkVersion);
        Assert.Equal(OrbitEmberStar.SharedAnimationFramesPerSecond, NewTabDecorationControl.SharedAnimationFramesPerSecond);
        Assert.InRange(NewTabDecorationControl.MaximumParallaxOffset, 0, 3.2);
        Assert.InRange(NewTabDecorationControl.MaximumScaleDelta, 0, 0.003);
    });

    private static BitmapSource LoadAsset(string relativePath)
    {
        var directory = FindRepositoryRoot();
        var path = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Required visual asset was missing: {path}");
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

    private static DirectoryInfo FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "assets")))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        return directory;
    }

    private static void AssertTransparentCorners(BitmapSource source, string label)
    {
        var (pixels, width, height) = Pixels(source);
        Assert.True(AlphaAt(pixels, width, 0, 0) <= 1, $"{label} top-left corner was opaque.");
        Assert.True(AlphaAt(pixels, width, width - 1, 0) <= 1, $"{label} top-right corner was opaque.");
        Assert.True(AlphaAt(pixels, width, 0, height - 1) <= 1, $"{label} bottom-left corner was opaque.");
        Assert.True(AlphaAt(pixels, width, width - 1, height - 1) <= 1, $"{label} bottom-right corner was opaque.");
    }

    private static void AssertTransparentOuterRing(BitmapSource source, string label)
    {
        var (pixels, width, height) = Pixels(source);
        for (var x = 0; x < width; x++)
        {
            Assert.True(AlphaAt(pixels, width, x, 0) <= 8, $"{label} had content touching its top edge.");
            Assert.True(AlphaAt(pixels, width, x, height - 1) <= 8, $"{label} had content touching its bottom edge.");
        }
        for (var y = 0; y < height; y++)
        {
            Assert.True(AlphaAt(pixels, width, 0, y) <= 8, $"{label} had content touching its left edge.");
            Assert.True(AlphaAt(pixels, width, width - 1, y) <= 8, $"{label} had content touching its right edge.");
        }
    }

    private static void AssertSingleVisibleComponent(BitmapSource source, string label)
    {
        var (pixels, width, height) = Pixels(source);
        AssertSingleVisibleComponent(pixels, width, height, label, visibilityThreshold: 8);
    }

    private static void AssertSingleVisibleComponent(
        byte[] pixels,
        int width,
        int height,
        string label,
        byte visibilityThreshold = 20,
        double minimumConnectedRatio = 1)
    {
        var visible = new bool[width * height];
        var visibleCount = 0;
        for (var index = 0; index < visible.Length; index++)
        {
            if (pixels[(index * 4) + 3] <= visibilityThreshold)
            {
                continue;
            }
            visible[index] = true;
            visibleCount++;
        }

        Assert.True(visibleCount > width * height / 5, $"{label} contained too little stellar artwork.");
        var visited = new bool[visible.Length];
        var queue = new Queue<int>();
        var largestConnectedCount = 0;
        for (var start = 0; start < visible.Length; start++)
        {
            if (!visible[start] || visited[start])
            {
                continue;
            }

            queue.Enqueue(start);
            visited[start] = true;
            var connectedCount = 0;
            while (queue.Count > 0)
            {
                var index = queue.Dequeue();
                connectedCount++;
                var x = index % width;
                var y = index / width;
                Visit(x - 1, y);
                Visit(x + 1, y);
                Visit(x, y - 1);
                Visit(x, y + 1);
            }
            largestConnectedCount = Math.Max(largestConnectedCount, connectedCount);
        }

        Assert.True(
            largestConnectedCount >= Math.Ceiling(visibleCount * minimumConnectedRatio),
            $"{label} had {visibleCount - largestConnectedCount} visible pixels outside its cohesive whole-star component.");

        void Visit(int x, int y)
        {
            if (x < 0 || y < 0 || x >= width || y >= height)
            {
                return;
            }
            var index = (y * width) + x;
            if (!visible[index] || visited[index])
            {
                return;
            }
            visited[index] = true;
            queue.Enqueue(index);
        }
    }

    private static double TransparentPixelRatio(BitmapSource source)
    {
        var (pixels, _, _) = Pixels(source);
        var transparent = pixels.Where((_, index) => index % 4 == 3).Count(alpha => alpha <= 8);
        return transparent / (pixels.Length / 4d);
    }

    private static int PartialAlphaPixelCount(BitmapSource source)
    {
        var (pixels, _, _) = Pixels(source);
        return pixels.Where((_, index) => index % 4 == 3).Count(alpha => alpha is > 0 and < 255);
    }

    private static int OpaquePixelCount(BitmapSource source)
    {
        var (pixels, _, _) = Pixels(source);
        return pixels.Where((_, index) => index % 4 == 3).Count(alpha => alpha == 255);
    }

    private static double AverageLuminance(
        byte[] pixels,
        int width,
        int height,
        double left,
        double top,
        double right,
        double bottom)
    {
        var startX = (int)Math.Floor(width * left);
        var endX = (int)Math.Ceiling(width * right);
        var startY = (int)Math.Floor(height * top);
        var endY = (int)Math.Ceiling(height * bottom);
        double total = 0;
        var count = 0;
        for (var y = startY; y < endY; y++)
        {
            for (var x = startX; x < endX; x++)
            {
                var offset = ((y * width) + x) * 4;
                total += (pixels[offset + 2] * 0.2126) + (pixels[offset + 1] * 0.7152) + (pixels[offset] * 0.0722);
                count++;
            }
        }
        return total / Math.Max(1, count);
    }

    private static (byte[] Pixels, int Width, int Height) Pixels(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return (pixels, converted.PixelWidth, converted.PixelHeight);
    }

    private static (byte[] Pixels, int PixelWidth, int PixelHeight) Render(
        FrameworkElement element,
        double width,
        double height,
        double dpi)
    {
        StaTest.Prepare(element, width, height);
        var pixelWidth = (int)Math.Ceiling(width * dpi / 96d);
        var pixelHeight = (int)Math.Ceiling(height * dpi / 96d);
        var bitmap = new RenderTargetBitmap(
            pixelWidth,
            pixelHeight,
            dpi,
            dpi,
            PixelFormats.Pbgra32);
        bitmap.Render(element);
        var pixels = new byte[pixelWidth * pixelHeight * 4];
        bitmap.CopyPixels(pixels, pixelWidth * 4, 0);
        return (pixels, pixelWidth, pixelHeight);
    }

    private static byte AlphaAt(byte[] pixels, int pixelWidth, int x, int y) =>
        pixels[((y * pixelWidth + x) * 4) + 3];

    private static int VisiblePixelCount(byte[] pixels) =>
        pixels.Where((_, index) => index % 4 == 3).Count(alpha => alpha > 20);
}
