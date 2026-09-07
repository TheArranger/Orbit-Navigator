using OrbitNavigator.App.QuickView;
using Xunit;

namespace OrbitNavigator.App.Tests.QuickView;

public sealed class QuickViewTargetResolverTests
{
    [Fact]
    public void EmptyAnchorClickUsesSelectedNormalPage()
    {
        var selected = new Uri("https://my-orbit.snap-it.cc/settings");

        Assert.Equal(selected, QuickViewTargetResolver.Resolve("", selected));
    }

    [Fact]
    public void EmptyNavigateKeepsCurrentQuickViewPage()
    {
        var selected = new Uri("https://example.test/");
        var current = new Uri("https://my-orbit.snap-it.cc/");

        Assert.Equal(current, QuickViewTargetResolver.Resolve("  ", selected, current));
    }

    [Theory]
    [InlineData("my-orbit.snap-it.cc", "https://my-orbit.snap-it.cc/")]
    [InlineData("https://my-orbit.snap-it.cc/", "https://my-orbit.snap-it.cc/")]
    [InlineData("orbit browser", "https://duckduckgo.com/?q=orbit%20browser")]
    public void TypedInputUsesCanonicalOmniboxResolution(string query, string expected)
    {
        Assert.Equal(expected, QuickViewTargetResolver.Resolve(query, null).AbsoluteUri);
    }

    [Theory]
    [InlineData("about:blank")]
    [InlineData("file:///C:/private.txt")]
    public void EmptyClickRejectsNonWebFallback(string fallback)
    {
        Assert.Throws<ArgumentException>(() =>
            QuickViewTargetResolver.Resolve("", new Uri(fallback)));
    }
}
