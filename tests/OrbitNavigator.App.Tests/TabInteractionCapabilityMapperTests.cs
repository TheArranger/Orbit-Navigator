using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Presentation.Tabs;
using OrbitNavigator.WebViewHost;
using Xunit;

namespace OrbitNavigator.App.Tests;

public sealed class TabInteractionCapabilityMapperTests
{
    [Fact]
    public void AdvertisesOnlyRealMuteAndNoUnenforcedSiteControls()
    {
        var tabId = new BrowserTabId(Guid.NewGuid());
        var result = TabInteractionCapabilityMapper.FromHost(new(
            tabId,
            7,
            IsPlayingAudio: true,
            IsMuted: true));

        Assert.Equal(tabId, result.TabId);
        Assert.Equal(7, result.Revision);
        Assert.True(result.IsPlayingAudio);
        Assert.True(result.IsMuted);
        Assert.True(result.Mute.IsAvailable);
        Assert.False(result.Volume.IsAvailable);
        Assert.False(result.Equalizer.IsAvailable);
        Assert.False(result.Balance.IsAvailable);
        Assert.False(result.OutputDevice.IsAvailable);
        Assert.All(
            new[] { result.Volume, result.Equalizer, result.Balance, result.OutputDevice },
            feature => Assert.False(string.IsNullOrWhiteSpace(feature.UnavailableReason)));
        Assert.Empty(result.SiteContentControls);
    }
}
