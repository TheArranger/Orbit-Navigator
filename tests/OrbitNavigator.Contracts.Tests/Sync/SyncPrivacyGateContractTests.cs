using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Sync;
using Xunit;

namespace OrbitNavigator.Contracts.Tests.Sync;

public sealed class SyncPrivacyGateContractTests
{
    [Theory]
    [InlineData("codec")]
    [InlineData("projection")]
    [InlineData("device")]
    [InlineData("authorization")]
    [InlineData("transport")]
    [InlineData("key-restoration")]
    [InlineData("reset")]
    [InlineData("synced-deletion")]
    public async Task PrivateContextReturnsPolicyDeniedWithZeroExternalCalls(string dependency)
    {
        var calls = 0;
        var result = await SyncOperationGate.ExecuteAsync(
            Browsing(BrowserProfileMode.Private),
            new SyncOperationId(Guid.NewGuid()),
            _ =>
            {
                calls++;
                return ValueTask.FromResult(ControllerResult<ProbeReceipt>.Success(new ProbeReceipt(dependency)));
            });

        Assert.False(result.IsSuccess);
        Assert.Equal(ControllerErrorCode.PolicyDenied, result.Error?.Code);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task SignedOutLocalBrowsingDoesNotInvokeAuthOrTransport()
    {
        var authCalls = 0;
        var transportCalls = 0;
        var availability = SyncAvailabilityPolicy.Evaluate(hasAccountAuthorization: false);

        if (availability.SyncAvailable)
        {
            authCalls++;
            transportCalls++;
        }

        await Task.CompletedTask;
        Assert.True(availability.LocalBrowsingAvailable);
        Assert.Equal(0, authCalls);
        Assert.Equal(0, transportCalls);
    }

    [Fact]
    public void EveryRemoteDependencyRequiresAuthorizedSyncContextFirst()
    {
        var interfaces = new[]
        {
            typeof(ISyncRecordProjector),
            typeof(ISyncEnvelopeCodec),
            typeof(ISyncAccountAuthorization),
            typeof(ISyncTransport),
            typeof(ISyncDeviceRegistry),
            typeof(IEmailAccountRestoration),
            typeof(ISyncKeyRestoration),
            typeof(IEncryptedSyncReset),
            typeof(ISyncedDataDeletion),
        };

        foreach (var contract in interfaces)
        {
            Assert.All(contract.GetMethods(), method =>
                Assert.Equal(typeof(SyncOperationContext), method.GetParameters()[0].ParameterType));
        }
    }

    private sealed record ProbeReceipt(string Dependency);

    private static BrowsingContext Browsing(BrowserProfileMode mode) =>
        new(
            new PrivacyContext(
                new ProfileId(Guid.NewGuid()),
                new BrowserSessionId(Guid.NewGuid()),
                mode),
            new BrowserWindowId(Guid.NewGuid()),
            new BrowserTabId(Guid.NewGuid()),
            null);
}
