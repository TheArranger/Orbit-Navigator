using OrbitNavigator.Contracts.Common;
using Xunit;

namespace OrbitNavigator.Contracts.Tests.Common;

public sealed class PrivacyContextTests
{
    [Fact]
    public void NormalGuard_RejectsPrivateBeforeWork()
    {
        var context = new PrivacyContext(
            new ProfileId(Guid.NewGuid()),
            new BrowserSessionId(Guid.NewGuid()),
            BrowserProfileMode.Private);

        var result = NormalProfileOperationGuard.RequireNormal(context);

        Assert.False(result.IsSuccess);
        Assert.Equal(ControllerErrorCode.PolicyDenied, result.Error!.Code);
    }

    [Fact]
    public void BrowsingContext_RequiresStableWindowAndTabIdentity()
    {
        var context = new BrowsingContext(
            new PrivacyContext(
                new ProfileId(Guid.NewGuid()),
                new BrowserSessionId(Guid.NewGuid()),
                BrowserProfileMode.Normal),
            default,
            new BrowserTabId(Guid.NewGuid()),
            null);

        Assert.False(context.IsStructurallyValid);
    }
}

