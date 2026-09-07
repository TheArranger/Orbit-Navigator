using OrbitNavigator.Contracts.Browser;
using Xunit;

namespace OrbitNavigator.Contracts.Tests.Browser;

public sealed class NavigationContractsTests
{
    [Fact]
    public void NavigationDecision_DefaultIsFailClosed()
    {
        Assert.Equal(NavigationDecision.Block, default);
    }
}

