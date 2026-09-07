using OrbitNavigator.Presentation.Motion;
using OrbitNavigator.Presentation.Navigation;
using Xunit;

namespace OrbitNavigator.Presentation.Tests;

public sealed class NavigationAndMotionTests
{
    [Fact]
    public void PlainLanguageInputUsesDuckDuckGo()
    {
        var result = new OmniboxTargetResolver().Resolve("accessible browser tabs");

        Assert.Equal(OmniboxTargetKind.Search, result.Kind);
        Assert.Equal("duckduckgo.com", result.Uri.Host);
        Assert.Equal("?q=accessible%20browser%20tabs", result.Uri.Query);
    }

    [Theory]
    [InlineData("https://example.test/path", "https://example.test/path")]
    [InlineData("example.test/path", "https://example.test/path")]
    public void WebAddressInputNavigatesDirectly(string input, string expected)
    {
        var result = new OmniboxTargetResolver().Resolve(input);

        Assert.Equal(OmniboxTargetKind.Address, result.Kind);
        Assert.Equal(new Uri(expected), result.Uri);
    }

    [Fact]
    public void ReducedMotionEliminatesReorderingTransforms()
    {
        var profile = MotionProfile.Create(true);

        Assert.False(profile.UsesTransformMotion);
        Assert.Equal(0, profile.MaximumTranslationPixels);
        Assert.Equal(TimeSpan.Zero, profile.GroupDisclosure);
        Assert.True(profile.SurfaceDisclosure <= TimeSpan.FromMilliseconds(80));
    }
}
