using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.Presentation.Tabs;

public enum TabAudioFeature
{
    Mute = 0,
    Volume = 1,
    Equalizer = 2,
    Balance = 3,
    OutputDevice = 4,
}

public enum TabCapabilityAvailability
{
    Unavailable = 0,
    Available = 1,
}

public sealed record TabAudioFeatureCapabilityPresentation(
    TabAudioFeature Feature,
    TabCapabilityAvailability Availability,
    string UnavailableReason)
{
    public bool IsAvailable => Availability == TabCapabilityAvailability.Available;

    public TabAudioFeatureCapabilityPresentation Validate()
    {
        if (!Enum.IsDefined(Feature) || !Enum.IsDefined(Availability))
        {
            throw new ArgumentOutOfRangeException(nameof(Feature));
        }

        var reason = string.IsNullOrWhiteSpace(UnavailableReason)
            ? "This audio control is unavailable."
            : UnavailableReason.Trim();
        return this with { UnavailableReason = IsAvailable ? string.Empty : reason };
    }

    public static TabAudioFeatureCapabilityPresentation Available(TabAudioFeature feature) =>
        new(feature, TabCapabilityAvailability.Available, string.Empty);

    public static TabAudioFeatureCapabilityPresentation Unavailable(
        TabAudioFeature feature,
        string reason) =>
        new TabAudioFeatureCapabilityPresentation(
            feature,
            TabCapabilityAvailability.Unavailable,
            reason).Validate();
}

public enum TabSiteContentControlKind
{
    AdBlocking = 0,
    ScriptBlocking = 1,
}

public sealed record TabSiteContentControlPresentation(
    TabSiteContentControlKind Kind,
    bool IsEnforced,
    bool IsEnabled,
    bool CanToggle,
    string UnavailableReason)
{
    public TabSiteContentControlPresentation Validate()
    {
        if (!Enum.IsDefined(Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(Kind));
        }

        var reason = string.IsNullOrWhiteSpace(UnavailableReason)
            ? "This site control is unavailable."
            : UnavailableReason.Trim();
        return this with
        {
            CanToggle = IsEnforced && CanToggle,
            UnavailableReason = IsEnforced && CanToggle ? string.Empty : reason,
        };
    }
}

/// <summary>
/// A memory-only, host-supplied capability projection for one tab. Presentation
/// never infers audio activity or claims that a site control is enforced.
/// </summary>
public sealed record TabInteractionCapabilitiesPresentation(
    BrowserTabId TabId,
    long Revision,
    bool IsPlayingAudio,
    bool IsMuted,
    TabAudioFeatureCapabilityPresentation Mute,
    TabAudioFeatureCapabilityPresentation Volume,
    TabAudioFeatureCapabilityPresentation Equalizer,
    TabAudioFeatureCapabilityPresentation Balance,
    TabAudioFeatureCapabilityPresentation OutputDevice,
    IReadOnlyList<TabSiteContentControlPresentation> SiteContentControls)
{
    public static TabInteractionCapabilitiesPresentation Unavailable(BrowserTabId tabId, long revision) => new(
        tabId,
        revision,
        false,
        false,
        TabAudioFeatureCapabilityPresentation.Unavailable(
            TabAudioFeature.Mute,
            "Mute becomes available when the browser host can control this tab's audio."),
        TabAudioFeatureCapabilityPresentation.Unavailable(
            TabAudioFeature.Volume,
            "Per-tab volume requires a verified browser audio engine."),
        TabAudioFeatureCapabilityPresentation.Unavailable(
            TabAudioFeature.Equalizer,
            "Per-tab equalizer controls require a verified browser audio engine."),
        TabAudioFeatureCapabilityPresentation.Unavailable(
            TabAudioFeature.Balance,
            "Per-tab left and right balance requires a verified browser audio engine."),
        TabAudioFeatureCapabilityPresentation.Unavailable(
            TabAudioFeature.OutputDevice,
            "Per-tab output device routing requires verified Windows audio-session routing."),
        []);

    public TabInteractionCapabilitiesPresentation Validate()
    {
        if (TabId.IsEmpty)
        {
            throw new ArgumentException("A tab ID is required.", nameof(TabId));
        }
        if (Revision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Revision));
        }

        ArgumentNullException.ThrowIfNull(Mute);
        ArgumentNullException.ThrowIfNull(Volume);
        ArgumentNullException.ThrowIfNull(Equalizer);
        ArgumentNullException.ThrowIfNull(Balance);
        ArgumentNullException.ThrowIfNull(OutputDevice);
        ArgumentNullException.ThrowIfNull(SiteContentControls);

        var mute = RequireFeature(Mute.Validate(), TabAudioFeature.Mute);
        var volume = RequireFeature(Volume.Validate(), TabAudioFeature.Volume);
        var equalizer = RequireFeature(Equalizer.Validate(), TabAudioFeature.Equalizer);
        var balance = RequireFeature(Balance.Validate(), TabAudioFeature.Balance);
        var output = RequireFeature(OutputDevice.Validate(), TabAudioFeature.OutputDevice);
        var controls = SiteContentControls
            .Select(control => (control ?? throw new ArgumentException("Site controls cannot contain null values.")).Validate())
            .GroupBy(control => control.Kind)
            .Select(group => group.Single())
            .ToArray();
        return this with
        {
            IsMuted = mute.IsAvailable && IsMuted,
            Mute = mute,
            Volume = volume,
            Equalizer = equalizer,
            Balance = balance,
            OutputDevice = output,
            SiteContentControls = controls,
        };
    }

    private static TabAudioFeatureCapabilityPresentation RequireFeature(
        TabAudioFeatureCapabilityPresentation capability,
        TabAudioFeature expected) =>
        capability.Feature == expected
            ? capability
            : throw new ArgumentException($"The {expected} capability is missing or misidentified.");
}
