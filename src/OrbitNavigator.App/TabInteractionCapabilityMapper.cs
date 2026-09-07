using OrbitNavigator.Presentation.Tabs;
using OrbitNavigator.WebViewHost;

namespace OrbitNavigator.App;

internal static class TabInteractionCapabilityMapper
{
    internal static TabInteractionCapabilitiesPresentation FromHost(WebViewTabAudioState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.Validate();
        return new TabInteractionCapabilitiesPresentation(
            state.TabId,
            state.Revision,
            state.IsPlayingAudio,
            state.IsMuted,
            TabAudioFeatureCapabilityPresentation.Available(TabAudioFeature.Mute),
            TabAudioFeatureCapabilityPresentation.Unavailable(
                TabAudioFeature.Volume,
                "Per-tab volume is not available; Orbit can currently mute or unmute this tab."),
            TabAudioFeatureCapabilityPresentation.Unavailable(
                TabAudioFeature.Equalizer,
                "Per-tab equalizer controls require a verified browser audio engine."),
            TabAudioFeatureCapabilityPresentation.Unavailable(
                TabAudioFeature.Balance,
                "Per-tab left and right balance requires a verified browser audio engine."),
            TabAudioFeatureCapabilityPresentation.Unavailable(
                TabAudioFeature.OutputDevice,
                "Per-tab output device routing requires verified Windows audio-session routing."),
            []).Validate();
    }
}
