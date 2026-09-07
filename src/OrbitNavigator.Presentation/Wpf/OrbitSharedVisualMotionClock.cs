#if ORBIT_WPF
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Threading;

namespace OrbitNavigator.Presentation.Wpf;

/// <summary>
/// One throttled composition clock shared by all visible Orbit ember visuals.
/// It is inactive when there are no eligible subscribers and owns no per-item
/// timers or animation clocks.
/// </summary>
internal interface IOrbitSharedVisualMotionTarget
{
    Dispatcher Dispatcher { get; }

    void ApplySharedMotionFrame(double normalizedPhase);
}

/// <summary>
/// Optional richer timeline for scene-scale visuals. Small atlas-based targets
/// continue to consume the compatibility cycle phase, while layered scenes can
/// derive independent, long-running periods from the same allocation-free clock.
/// </summary>
internal interface IOrbitSharedVisualMotionTimelineTarget : IOrbitSharedVisualMotionTarget
{
    void ApplySharedMotionFrame(OrbitSharedMotionFrame frame);
}

internal readonly record struct OrbitSharedMotionFrame(
    double ElapsedSeconds,
    double NormalizedCyclePhase,
    long Sequence);

internal static class OrbitSharedVisualMotionClock
{
    internal const int FramesPerSecond = 24;
    internal static readonly TimeSpan CycleDuration = TimeSpan.FromSeconds(8);

    private static readonly HashSet<IOrbitSharedVisualMotionTarget> Subscribers = [];
    private static readonly long MinimumFrameTicks = Math.Max(1, Stopwatch.Frequency / FramesPerSecond);
    private static long lastFrameTimestamp;
    private static long deliveredFrameCount;
    private static bool renderingAttached;

    internal static int SubscriberCount => Subscribers.Count;

    internal static int RenderingHandlerCount => renderingAttached ? 1 : 0;

    internal static long DeliveredFrameCount => deliveredFrameCount;

    internal static void Register(IOrbitSharedVisualMotionTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.Dispatcher.VerifyAccess();
        if (!Subscribers.Add(target) || renderingAttached)
        {
            return;
        }

        lastFrameTimestamp = 0;
        CompositionTarget.Rendering += OnRendering;
        renderingAttached = true;
    }

    internal static void Unregister(IOrbitSharedVisualMotionTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.Dispatcher.VerifyAccess();
        _ = Subscribers.Remove(target);
        if (Subscribers.Count == 0)
        {
            DetachRenderingHandler();
        }
    }

    private static void OnRendering(object? sender, EventArgs args)
    {
        if (Subscribers.Count == 0)
        {
            DetachRenderingHandler();
            return;
        }

        var timestamp = Stopwatch.GetTimestamp();
        if (lastFrameTimestamp != 0 && timestamp - lastFrameTimestamp < MinimumFrameTicks)
        {
            return;
        }

        lastFrameTimestamp = timestamp;
        var seconds = timestamp / (double)Stopwatch.Frequency;
        var phase = seconds % CycleDuration.TotalSeconds / CycleDuration.TotalSeconds;
        deliveredFrameCount++;
        var frame = new OrbitSharedMotionFrame(seconds, phase, deliveredFrameCount);
        foreach (var target in Subscribers)
        {
            if (target is IOrbitSharedVisualMotionTimelineTarget timelineTarget)
            {
                timelineTarget.ApplySharedMotionFrame(frame);
            }
            else
            {
                target.ApplySharedMotionFrame(phase);
            }
        }
    }

    private static void DetachRenderingHandler()
    {
        if (!renderingAttached)
        {
            return;
        }

        CompositionTarget.Rendering -= OnRendering;
        renderingAttached = false;
        lastFrameTimestamp = 0;
    }
}
#endif
