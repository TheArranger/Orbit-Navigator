using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Privacy;
using Xunit;

namespace OrbitNavigator.Contracts.Tests.Privacy;

public sealed class ClipboardShelfContractTests
{
    [Fact]
    public void PrivateReadIsSuccessfulEmptyAndUnavailable()
    {
        var context = Browsing(BrowserProfileMode.Private);

        var result = ClipboardShelfPolicy.Read(context);

        Assert.True(result.IsSuccess);
        Assert.Equal(ClipboardShelfAvailability.Unavailable, result.Value?.Availability);
        Assert.Equal(ClipboardShelfUnavailableReason.PrivateMode, result.Value?.UnavailableReason);
        Assert.Empty(result.Value?.Items ?? []);
        Assert.Equal(10, result.Value?.Capacity);
    }

    [Fact]
    public void EveryPrivateMutationIsPolicyDeniedBeforeProviderMutation()
    {
        var context = Browsing(BrowserProfileMode.Private);

        var capture = ClipboardShelfPolicy.AuthorizeMutation(context);
        var use = ClipboardShelfPolicy.AuthorizeMutation(context);
        var clear = ClipboardShelfPolicy.AuthorizeMutation(context);

        Assert.All([capture, use, clear], result =>
        {
            Assert.False(result.IsSuccess);
            Assert.Equal(ControllerErrorCode.PolicyDenied, result.Error?.Code);
        });
    }

    [Fact]
    public void ItemIdsAreProfileBoundAndUseReceiptContainsNoPayload()
    {
        var profile = new ProfileId(Guid.NewGuid());
        var id = new ClipboardShelfItemId(profile, Guid.NewGuid());
        var receipt = new ClipboardShelfUseReceipt(
            id,
            new BrowserTabId(Guid.NewGuid()),
            DateTimeOffset.UtcNow);

        Assert.Equal(profile, receipt.ItemId.ProfileId);
        var properties = typeof(ClipboardShelfUseReceipt).GetProperties();
        Assert.DoesNotContain(properties, property =>
            property.PropertyType == typeof(string) ||
            property.Name.Contains("Content", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Payload", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CaptureContractOnlyAcceptsExplicitNonSensitiveClassification()
    {
        Assert.Single(Enum.GetValues<ClipboardCaptureClassification>());
        Assert.Equal(
            ClipboardCaptureClassification.ExplicitUserInitiatedNonSensitive,
            Enum.GetValues<ClipboardCaptureClassification>()[0]);
        Assert.Equal(10, ClipboardShelfReadModel.MaximumCapacity);
    }

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
