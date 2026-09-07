namespace OrbitNavigator.Presentation.Shell;

/// <summary>
/// Keeps decorative new-tab artwork subordinate to content and removable for
/// high-contrast or reduced-visual-noise preferences.
/// </summary>
public static class NewTabDecorationPolicy
{
    public static bool ShouldShow(bool assetIsAvailable, bool highContrast, bool reducedVisualNoise) =>
        assetIsAvailable && !highContrast && !reducedVisualNoise;
}
