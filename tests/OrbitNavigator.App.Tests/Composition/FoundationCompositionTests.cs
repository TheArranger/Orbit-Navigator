using OrbitNavigator.App.Composition;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Privacy;
using Xunit;

namespace OrbitNavigator.App.Tests.Composition;

public sealed class FoundationCompositionTests
{
    [Fact]
    public void SignedOutSyncKeepsLocalBrowsingAvailableAndRemoteServicesAbsent()
    {
        var composition = OptionalSyncComposition.SignedOut();

        Assert.True(composition.Status.Availability.LocalBrowsingAvailable);
        Assert.False(composition.Status.Availability.SyncAvailable);
        Assert.Equal("sync.account.signed-out", composition.Status.Availability.SyncUnavailableMessageKey);
        Assert.False(composition.Status.RecoveryAvailable);
        Assert.False(composition.Status.SyncedDeletionAvailable);
        Assert.Null(composition.Coordinator);
        Assert.Null(composition.KeyRestoration);
        Assert.Null(composition.SyncedDeletion);
    }

    [Fact]
    public async Task ClipboardLeaseIsOneShotAndBoundToNormalProfileSession()
    {
        var clock = new TestClock();
        using var leases = new ClipboardCaptureLeaseStore(clock);
        var context = Context(BrowserProfileMode.Normal);
        var issued = leases.IssueFromExplicitUserAction(
            context,
            ClipboardCaptureClassification.ExplicitUserInitiatedNonSensitive,
            "clipboard text");

        Assert.True(issued.IsSuccess);
        var redeemed = await leases.RedeemAsync(context, issued.Value!.LeaseId, default);
        Assert.True(redeemed.IsSuccess);
        using var content = redeemed.Value!;
        Assert.Equal("clipboard text", new string(content.Characters.Span));

        var replayed = await leases.RedeemAsync(context, issued.Value.LeaseId, default);
        Assert.False(replayed.IsSuccess);
        Assert.Equal(ControllerErrorCode.PolicyDenied, replayed.Error?.Code);
    }

    [Fact]
    public void ClipboardLeaseRejectsPrivateCaptureBeforeStoringContent()
    {
        using var leases = new ClipboardCaptureLeaseStore(new TestClock());
        var result = leases.IssueFromExplicitUserAction(
            Context(BrowserProfileMode.Private),
            ClipboardCaptureClassification.ExplicitUserInitiatedNonSensitive,
            "private text");

        Assert.False(result.IsSuccess);
        Assert.Equal(ControllerErrorCode.PolicyDenied, result.Error?.Code);
    }

    private static BrowsingContext Context(BrowserProfileMode mode) => new(
        new PrivacyContext(
            new ProfileId(Guid.NewGuid()),
            new BrowserSessionId(Guid.NewGuid()),
            mode),
        new BrowserWindowId(Guid.NewGuid()),
        new BrowserTabId(Guid.NewGuid()),
        null);

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
    }
}
