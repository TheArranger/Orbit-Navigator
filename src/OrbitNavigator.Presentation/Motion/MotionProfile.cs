namespace OrbitNavigator.Presentation.Motion;

public sealed record MotionProfile(
    TimeSpan HoverAndFocus,
    TimeSpan TabActivation,
    TimeSpan GroupDisclosure,
    TimeSpan SurfaceDisclosure,
    double MaximumTranslationPixels,
    bool UsesTransformMotion)
{
    public static MotionProfile Create(bool reducedMotion) =>
        reducedMotion
            ? new MotionProfile(
                TimeSpan.FromMilliseconds(80),
                TimeSpan.Zero,
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(80),
                0,
                false)
            : new MotionProfile(
                TimeSpan.FromMilliseconds(120),
                TimeSpan.FromMilliseconds(140),
                TimeSpan.FromMilliseconds(180),
                TimeSpan.FromMilliseconds(160),
                4,
                true);
}

public static class InterfaceMetrics
{
    public const double MinimumPointerTarget = 44;
    public const double VisibleFocusThickness = 3;
    public const double TabRowHeight = 40;
    public const double NavigationRowHeight = 48;
    public const double OmniboxHeight = 40;
}
