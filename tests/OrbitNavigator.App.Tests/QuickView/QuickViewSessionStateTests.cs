using OrbitNavigator.App.QuickView;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Presentation.QuickView;

using Xunit;

namespace OrbitNavigator.App.Tests.QuickView;

public sealed class QuickViewSessionStateTests
{
    [Fact]
    public void NormalLifecycleIsRevisionedAndFreshAfterClose()
    {
        var session = new QuickViewSessionState(Context(BrowserProfileMode.Normal));
        var selected = new Uri("https://selected.test/");
        var target = new Uri("https://preview.test/article");

        session.SetSelectedSite(selected);
        var opened = session.BeginOpen(session.Snapshot.Revision, target);
        session.CompleteOpen(target, "Preview article");
        Assert.Equal(QuickViewHostState.Open, session.Snapshot.HostState);
        var stale = session.BeginClose(opened.Value!.Revision);
        var closing = session.BeginClose(session.Snapshot.Revision);
        session.CompleteClose(selected);

        Assert.Equal(QuickViewHostState.Opening, opened.Value.HostState);
        Assert.Equal(QuickViewHostState.Closing, closing.Value!.HostState);
        Assert.Equal(ControllerErrorCode.Conflict, stale.Error?.Code);
        Assert.Equal(QuickViewHostState.Ready, session.Snapshot.HostState);
        Assert.Null(session.Snapshot.Address);
        Assert.True(session.Snapshot.Revision > closing.Value.Revision);
    }

    [Fact]
    public void PrivateSessionNeverBecomesReadyOrBeginsHostWork()
    {
        var session = new QuickViewSessionState(Context(BrowserProfileMode.Private));
        session.SetSelectedSite(new Uri("https://example.test/"));

        var result = session.BeginOpen(
            session.Snapshot.Revision,
            new Uri("https://preview.test/"));

        Assert.Equal(QuickViewHostState.Unavailable, session.Snapshot.HostState);
        Assert.Equal(ControllerErrorCode.PolicyDenied, result.Error?.Code);
    }

    [Fact]
    public void InternalOrCredentialedSitesRemainUnavailable()
    {
        var session = new QuickViewSessionState(Context(BrowserProfileMode.Normal));

        session.SetSelectedSite(null);
        Assert.Equal(QuickViewHostState.Unavailable, session.Snapshot.HostState);
        session.SetSelectedSite(new Uri("https://user:secret@example.test/"));
        Assert.Equal(QuickViewHostState.Unavailable, session.Snapshot.HostState);
    }

    private static PrivacyContext Context(BrowserProfileMode mode) => new(
        new ProfileId(Guid.NewGuid()),
        new BrowserSessionId(Guid.NewGuid()),
        mode);
}
