using OrbitNavigator.Contracts.Common;
using Xunit;

namespace OrbitNavigator.WebViewHost.Tests;

public sealed class WebViewTabAudioStateTests
{
    [Fact]
    public void ValidStateCarriesOnlyMemoryLocalAudioFacts()
    {
        var state = new WebViewTabAudioState(
            new BrowserTabId(Guid.NewGuid()),
            2,
            IsPlayingAudio: true,
            IsMuted: false).Validate();

        Assert.True(state.IsPlayingAudio);
        Assert.False(state.IsMuted);
        Assert.Equal(2, state.Revision);
    }

    [Fact]
    public void EmptyTabOrNonMonotonicRevisionFailsClosed()
    {
        Assert.Throws<ArgumentException>(() => new WebViewTabAudioState(
            default,
            1,
            false,
            false).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebViewTabAudioState(
            new BrowserTabId(Guid.NewGuid()),
            0,
            false,
            false).Validate());
    }
}
