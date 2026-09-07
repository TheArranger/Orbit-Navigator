using OrbitNavigator.Contracts.Infrastructure;
using Xunit;

namespace OrbitNavigator.Contracts.Tests.Infrastructure;

public sealed class LocalBrowsingReadinessContractTests
{
    [Fact]
    public void SignedOutAndOfflineRemainsReadyForLocalBrowsing()
    {
        var prerequisites = new LocalBrowsingPrerequisites(
            WebViewRuntimeAvailable: true,
            ProfileStorageAvailable: true,
            WindowsKeyProtectionAvailable: true,
            NetworkAvailability.Unavailable,
            LocalAccountState.SignedOut);

        var readiness = LocalBrowsingReadiness.Evaluate(prerequisites);

        Assert.True(readiness.IsReady);
        Assert.False(readiness.RequiresAccount);
        Assert.False(readiness.RequiresNetwork);
    }

    [Theory]
    [InlineData(LocalAccountState.SignedOut)]
    [InlineData(LocalAccountState.SignedIn)]
    public void AccountStateDoesNotChangeFoundationReadiness(LocalAccountState accountState)
    {
        var prerequisites = new LocalBrowsingPrerequisites(
            WebViewRuntimeAvailable: true,
            ProfileStorageAvailable: true,
            WindowsKeyProtectionAvailable: true,
            NetworkAvailability.Available,
            accountState);

        var readiness = LocalBrowsingReadiness.Evaluate(prerequisites);

        Assert.Equal(LocalBrowsingReadinessState.Ready, readiness.State);
        Assert.False(readiness.RequiresAccount);
    }

    [Fact]
    public void LocalFoundationFailureBlocksReadinessWithoutInventingAccountDependency()
    {
        var prerequisites = new LocalBrowsingPrerequisites(
            WebViewRuntimeAvailable: false,
            ProfileStorageAvailable: true,
            WindowsKeyProtectionAvailable: true,
            NetworkAvailability.Available,
            LocalAccountState.SignedOut);

        var readiness = LocalBrowsingReadiness.Evaluate(prerequisites);

        Assert.Equal(LocalBrowsingReadinessState.WebViewRuntimeUnavailable, readiness.State);
        Assert.False(readiness.RequiresAccount);
    }
}
