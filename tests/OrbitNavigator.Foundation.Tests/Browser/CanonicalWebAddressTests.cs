using OrbitNavigator.Foundation.Browser;
using Xunit;

namespace OrbitNavigator.Foundation.Tests.Browser;

public sealed class CanonicalWebAddressTests
{
    [Theory]
    [InlineData("https://https://example.test/path", "https://example.test/path")]
    [InlineData("https://http://example.test/path", "http://example.test/path")]
    [InlineData("https://https://https://my-orbit.snap-it.cc/login", "https://my-orbit.snap-it.cc/login")]
    [InlineData("https://https://my-orbit.snap-it.cc/login?next=%2Fsettings#account", "https://my-orbit.snap-it.cc/login?next=%2Fsettings#account")]
    [InlineData("https://example.test/path", "https://example.test/path")]
    public void Normalize_NeverLeavesDuplicatedWebScheme(string input, string expected) =>
        Assert.Equal(expected, CanonicalWebAddress.Normalize(new Uri(input)).AbsoluteUri);
}
