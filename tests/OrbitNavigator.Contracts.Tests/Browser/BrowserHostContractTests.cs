using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using Xunit;

namespace OrbitNavigator.Contracts.Tests.Browser;

public sealed class BrowserHostContractTests
{
    [Fact]
    public void InternalPageIsAdditiveAndDefaultsToOrdinaryWebContent()
    {
        var tab = new BrowserTabState(
            new BrowserTabId(Guid.NewGuid()),
            null,
            null,
            "New Tab",
            BrowserLoadState.Idle,
            false,
            false,
            false);

        Assert.Equal(BrowserInternalPageKind.None, tab.InternalPage);
        Assert.Equal(BrowserInternalPageKind.Settings,
            (tab with { InternalPage = BrowserInternalPageKind.Settings }).InternalPage);
    }
}
