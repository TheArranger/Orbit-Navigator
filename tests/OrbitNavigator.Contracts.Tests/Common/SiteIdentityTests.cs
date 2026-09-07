using OrbitNavigator.Contracts.Common;
using Xunit;

namespace OrbitNavigator.Contracts.Tests.Common;

public sealed class SiteIdentityTests
{
    [Fact]
    public void Factory_CanonicalizesExactOriginAndDropsPath()
    {
        var result = SiteIdentity.Create(new Uri("HTTPS://Example.COM:443/private?q=1"));

        Assert.True(result.IsSuccess);
        Assert.Equal("https://example.com", result.Value!.CanonicalOrigin);
    }

    [Fact]
    public void Equality_UsesCanonicalOrigin()
    {
        var first = SiteIdentity.Create(new Uri("https://example.com/one")).Value;
        var second = SiteIdentity.Create(new Uri("https://EXAMPLE.com/two")).Value;

        Assert.Equal(first, second);
        Assert.Equal(first!.GetHashCode(), second!.GetHashCode());
    }

    [Theory]
    [InlineData("file:///C:/private.txt")]
    [InlineData("ftp://example.com/file")]
    [InlineData("https://user:password@example.com/")]
    public void Factory_RejectsUntrustedOrigins(string value)
    {
        Assert.False(SiteIdentity.TryCreate(new Uri(value), out _));
    }
}

