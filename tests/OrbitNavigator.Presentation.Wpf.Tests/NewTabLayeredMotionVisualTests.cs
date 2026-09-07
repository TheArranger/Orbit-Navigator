using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

using OrbitNavigator.Presentation.Wpf;

using Xunit;

namespace OrbitNavigator.Presentation.Wpf.Tests;

[Collection("WPF focus-sensitive")]
public sealed class NewTabLayeredMotionVisualTests
{
    [Fact]
    public void SceneContractUsesIndependentCalmPeriodsOnOneBoundedClock()
    {
        Assert.Equal(6, NewTabDecorationControl.SceneLayerCount);
        Assert.Equal(5, NewTabDecorationControl.AnimatedSceneLayerCount);
        Assert.InRange(NewTabDecorationControl.SharedAnimationFramesPerSecond, 1, 24);

        var periods = new[]
        {
            NewTabDecorationControl.FarStarfieldHorizontalPeriod,
            NewTabDecorationControl.FarStarfieldVerticalPeriod,
            NewTabDecorationControl.NearStarfieldHorizontalPeriod,
            NewTabDecorationControl.NearStarfieldVerticalPeriod,
            NewTabDecorationControl.NebulaHorizontalPeriod,
            NewTabDecorationControl.NebulaVerticalPeriod,
            NewTabDecorationControl.OrbitalRoutePeriod,
        };
        Assert.Equal(periods.Length, periods.Distinct().Count());
        Assert.All(periods[..^1], period => Assert.True(period >= TimeSpan.FromSeconds(29)));
        Assert.InRange(
            NewTabDecorationControl.OrbitalRoutePeriod,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(15));

        var instanceFields = typeof(NewTabDecorationControl).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.DoesNotContain(instanceFields, field => typeof(DispatcherTimer).IsAssignableFrom(field.FieldType));
        Assert.DoesNotContain(instanceFields, field => typeof(AnimationClock).IsAssignableFrom(field.FieldType));

        var clockType = typeof(NewTabDecorationControl).Assembly.GetType(
            "OrbitNavigator.Presentation.Wpf.OrbitSharedVisualMotionClock",
            throwOnError: true)!;
        var staticFields = clockType.GetFields(
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.DoesNotContain(staticFields, field => typeof(DispatcherTimer).IsAssignableFrom(field.FieldType));
        Assert.DoesNotContain(staticFields, field => typeof(AnimationClock).IsAssignableFrom(field.FieldType));
    }

    [Fact]
    public void CompatibilityTimelineAdvancesLayersIndependentlyAndReducedMotionIsStatic() => StaTest.Run(() =>
    {
        if (SystemParameters.HighContrast)
        {
            return;
        }

        var decoration = new NewTabDecorationControl();
        decoration.SetCoordinatorAsset(PackagedFieldPath(), reduceVisualNoise: false);
        Assert.True(decoration.HasLayeredScene);

        decoration.RenderTimelineFrame(100, 1130);
        Assert.Equal(10, decoration.LastMotionElapsedSeconds, precision: 10);
        Assert.Equal(
            10d / NewTabDecorationControl.FarStarfieldHorizontalPeriod.TotalSeconds,
            decoration.CurrentFarStarfieldPhase,
            precision: 10);
        Assert.Equal(
            10d / NewTabDecorationControl.NearStarfieldHorizontalPeriod.TotalSeconds,
            decoration.CurrentNearStarfieldPhase,
            precision: 10);
        Assert.Equal(
            10d / NewTabDecorationControl.NebulaHorizontalPeriod.TotalSeconds,
            decoration.CurrentNebulaPhase,
            precision: 10);
        Assert.Equal(
            10d / NewTabDecorationControl.OrbitalRoutePeriod.TotalSeconds,
            decoration.CurrentOrbitalRoutePhase,
            precision: 10);
        Assert.Equal(4, new[]
        {
            decoration.CurrentFarStarfieldPhase,
            decoration.CurrentNearStarfieldPhase,
            decoration.CurrentNebulaPhase,
            decoration.CurrentOrbitalRoutePhase,
        }.Distinct().Count());

        decoration.ReducedMotion = true;
        var staticFarPhase = NewTabDecorationControl.StaticSceneTimeSeconds /
            NewTabDecorationControl.FarStarfieldHorizontalPeriod.TotalSeconds;
        Assert.Equal(NewTabDecorationControl.StaticSceneTimeSeconds, decoration.LastMotionElapsedSeconds, precision: 10);
        Assert.Equal(staticFarPhase, decoration.CurrentFarStarfieldPhase, precision: 10);

        decoration.RenderTimelineFrame(977, 1130);
        Assert.Equal(NewTabDecorationControl.StaticSceneTimeSeconds, decoration.LastMotionElapsedSeconds, precision: 10);
        Assert.Equal(staticFarPhase, decoration.CurrentFarStarfieldPhase, precision: 10);
    });

    [Fact]
    public void LoadedSceneRegistersOnceAndAccessibilityStatesStopMotion() => StaTest.Run(() =>
    {
        if (SystemParameters.HighContrast)
        {
            return;
        }

        var baselineSubscribers = NewTabDecorationControl.SharedClockSubscriberCount;
        var decoration = new NewTabDecorationControl();
        decoration.SetCoordinatorAsset(PackagedFieldPath(), reduceVisualNoise: false);
        var window = new Window
        {
            Content = decoration,
            Width = 960,
            Height = 540,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
        };

        window.Show();
        try
        {
            window.UpdateLayout();
            Assert.True(decoration.HasLayeredScene);
            Assert.True(decoration.IsLayeredMotionActive);
            Assert.True(decoration.IsRegisteredWithSharedClock);
            Assert.Equal(baselineSubscribers + 2, NewTabDecorationControl.SharedClockSubscriberCount);
            Assert.Equal(1, NewTabDecorationControl.SharedClockRenderingHandlerCount);

            window.WindowState = WindowState.Minimized;
            window.Dispatcher.Invoke(DispatcherPriority.Background, () => { });
            Assert.False(decoration.IsLayeredMotionActive);
            Assert.False(decoration.IsRegisteredWithSharedClock);
            Assert.Equal(baselineSubscribers, NewTabDecorationControl.SharedClockSubscriberCount);

            window.WindowState = WindowState.Normal;
            window.Dispatcher.Invoke(DispatcherPriority.Background, () => { });
            Assert.True(decoration.IsLayeredMotionActive);
            Assert.True(decoration.IsRegisteredWithSharedClock);
            Assert.Equal(baselineSubscribers + 2, NewTabDecorationControl.SharedClockSubscriberCount);

            decoration.ReducedMotion = true;
            Assert.False(decoration.IsLayeredMotionActive);
            Assert.False(decoration.IsRegisteredWithSharedClock);
            Assert.Equal(baselineSubscribers, NewTabDecorationControl.SharedClockSubscriberCount);

            decoration.ReducedMotion = false;
            decoration.SetCoordinatorAsset(PackagedFieldPath(), reduceVisualNoise: true);
            Assert.False(decoration.IsLayeredMotionActive);
            Assert.Equal(Visibility.Collapsed, decoration.Visibility);
            Assert.Equal(baselineSubscribers, NewTabDecorationControl.SharedClockSubscriberCount);
        }
        finally
        {
            window.Close();
        }

        Assert.Equal(baselineSubscribers, NewTabDecorationControl.SharedClockSubscriberCount);
    });

    private static string PackagedFieldPath()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            NewTabDecorationControl.DefaultV3AssetRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Required packaged field was missing: {path}");
        return path;
    }
}
