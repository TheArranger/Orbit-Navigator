using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Presentation.Resources;
using OrbitNavigator.Presentation.Tabs;
using OrbitNavigator.Presentation.Workspace;
using Xunit;

namespace OrbitNavigator.Presentation.Tests;

public sealed class TabControllerPresentationSessionTests
{
    [Fact]
    public async Task ExecuteAttachesOwnerPrivacyAndExpectedRevisionWithoutOptimisticMutation()
    {
        var window = new BrowserWindowId(Guid.NewGuid());
        var selected = new BrowserTabId(Guid.NewGuid());
        var sink = new RecordingSink();
        var session = new TabControllerPresentationSession(window, true, sink);
        session.AcceptProjection(Projection(window, selected, revision: 14, isPrivate: true));
        var before = session.Current;

        await session.ExecuteAsync(new CloseTabControllerAction(Guid.NewGuid(), selected));

        Assert.NotNull(sink.Last);
        Assert.Equal(window, sink.Last!.WindowId);
        Assert.Equal(14, sink.Last.ExpectedRevision);
        Assert.True(sink.Last.IsPrivate);
        Assert.Same(before!.Projection, session.Current!.Projection);
    }

    [Fact]
    public void StaleResultRefreshesAuthoritativeProjectionAndAnnounces()
    {
        var window = new BrowserWindowId(Guid.NewGuid());
        var tab = new BrowserTabId(Guid.NewGuid());
        var session = new TabControllerPresentationSession(window, false, new RecordingSink());
        session.AcceptProjection(Projection(window, tab, 3));

        session.AcceptCommandResult(new(
            Guid.NewGuid(),
            TabControllerCommandOutcome.Stale,
            string.Empty,
            Projection(window, tab, 4)));

        Assert.Equal(4, session.Current!.Projection.Revision);
        Assert.Contains("refreshed", session.Current.Announcement, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResourceSamplesAreMonotonicAndNeverTreatUnavailableAsZero()
    {
        var window = new BrowserWindowId(Guid.NewGuid());
        var tab = new BrowserTabId(Guid.NewGuid());
        var session = new TabControllerPresentationSession(window, false, new RecordingSink());
        session.AcceptProjection(Projection(window, tab, 1));
        var at = DateTimeOffset.UtcNow;
        session.AcceptResourceSample(Sample(10, at, tab));
        session.AcceptResourceSample(Sample(10, at.AddSeconds(1), tab));
        session.AcceptResourceSample(Sample(11, at.AddMilliseconds(400), tab));

        Assert.Equal(10, session.Current!.Resources.SampleId);
        var row = Assert.Single(session.Current.Resources.Rows);
        Assert.Equal(ResourceAttributionReliability.Unavailable, row.Reliability);
        Assert.Null(row.MeasuredRendererCpuPercent);
        Assert.Null(row.MeasuredRendererPrivateBytes);

        session.AcceptResourceSample(Sample(12, at.AddSeconds(1), tab));
        Assert.Equal(12, session.Current.Resources.SampleId);
    }

    [Fact]
    public void RejectsNumericValuesForSharedAttribution()
    {
        var contribution = new TabResourceContributionProjection(
            new BrowserTabId(Guid.NewGuid()),
            ResourceAttributionReliability.SharedProcesses,
            3,
            null,
            1,
            2);

        Assert.Throws<ArgumentException>(() => contribution.Validate());
    }

    [Fact]
    public async Task PrivateDetachIsDeniedBeforeTheHostSinkAndNeverChangesProjection()
    {
        var window = new BrowserWindowId(Guid.NewGuid());
        var tab = new BrowserTabId(Guid.NewGuid());
        var sink = new RecordingSink();
        var session = new TabControllerPresentationSession(window, true, sink);
        session.AcceptProjection(Projection(window, tab, 2, isPrivate: true));

        await session.ExecuteAsync(new DetachTabControllerAction(Guid.NewGuid()));

        Assert.Null(sink.Last);
        Assert.Equal(TabControllerHostState.Docked, session.Current!.Projection.HostState);
        Assert.Contains("stay docked", session.Current.Announcement, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TabVisualMetadataIsMonotonicLocalAndPrunedWithItsTab()
    {
        var window = new BrowserWindowId(Guid.NewGuid());
        var tab = new BrowserTabId(Guid.NewGuid());
        var session = new TabControllerPresentationSession(window, false, new RecordingSink());
        session.AcceptProjection(Projection(window, tab, 1));
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9ZSmAAAAAASUVORK5CYII=");

        Assert.True(session.AcceptTabVisualMetadata(new(tab, 4, "Orbit research", "example.test", png)));
        Assert.False(session.AcceptTabVisualMetadata(new(tab, 3, "Stale title", "stale.test", png)));
        var accepted = Assert.Single(session.Current!.TabVisuals).Value;
        Assert.Equal("Orbit research", accepted.PageTitle);
        Assert.True(TabVisualMetadataPresentation.IsSafePng(accepted.FaviconPng.Span));

        var replacement = new BrowserTabId(Guid.NewGuid());
        session.AcceptProjection(Projection(window, replacement, 2));
        Assert.Empty(session.Current!.TabVisuals);
    }

    [Fact]
    public void InvalidOrOversizedFaviconFallsBackWithoutRejectingSafeText()
    {
        var tab = new BrowserTabId(Guid.NewGuid());
        var invalid = new TabVisualMetadataPresentation(
            tab,
            1,
            "  A page title  ",
            "  example.test  ",
            new byte[TabVisualMetadataPresentation.MaximumFaviconBytes + 1]).Validate();

        Assert.Equal("A page title", invalid.PageTitle);
        Assert.Equal("example.test", invalid.SiteName);
        Assert.True(invalid.FaviconPng.IsEmpty);
    }

    [Fact]
    public async Task TabInteractionCapabilitiesAreMonotonicPrunedAndCommandsKeepOwnerRevision()
    {
        var window = new BrowserWindowId(Guid.NewGuid());
        var tab = new BrowserTabId(Guid.NewGuid());
        var sink = new RecordingSink();
        var session = new TabControllerPresentationSession(window, false, sink);
        session.AcceptProjection(Projection(window, tab, 7));

        Assert.True(session.AcceptTabInteractionCapabilities(Interaction(tab, 4, playing: true, muted: false)));
        Assert.False(session.AcceptTabInteractionCapabilities(Interaction(tab, 3, playing: false, muted: false)));
        var accepted = Assert.Single(session.Current!.TabInteractions).Value;
        Assert.True(accepted.IsPlayingAudio);
        Assert.True(accepted.Mute.IsAvailable);
        Assert.False(accepted.Volume.IsAvailable);
        Assert.Contains("verified browser audio engine", accepted.Volume.UnavailableReason, StringComparison.Ordinal);

        await session.ExecuteAsync(new SetTabMutedControllerAction(Guid.NewGuid(), tab, true));
        Assert.NotNull(sink.Last);
        Assert.Equal(window, sink.Last!.WindowId);
        Assert.Equal(7, sink.Last.ExpectedRevision);
        Assert.IsType<SetTabMutedControllerAction>(sink.Last.Action);

        var replacement = new BrowserTabId(Guid.NewGuid());
        session.AcceptProjection(Projection(window, replacement, 8));
        Assert.Empty(session.Current!.TabInteractions);
    }

    [Fact]
    public void SiteControlsCannotPretendToBeEnforcedOrDuplicateKinds()
    {
        var tab = new BrowserTabId(Guid.NewGuid());
        var unavailable = Interaction(tab, 1, false, false) with
        {
            SiteContentControls =
            [
                new(TabSiteContentControlKind.AdBlocking, false, true, true, "No enforced blocker is available."),
            ],
        };

        var accepted = unavailable.Validate();
        var control = Assert.Single(accepted.SiteContentControls);
        Assert.False(control.IsEnforced);
        Assert.False(control.CanToggle);
        Assert.Contains("No enforced", control.UnavailableReason, StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() => (unavailable with
        {
            SiteContentControls =
            [
                new(TabSiteContentControlKind.ScriptBlocking, true, true, true, string.Empty),
                new(TabSiteContentControlKind.ScriptBlocking, true, false, true, string.Empty),
            ],
        }).Validate());
    }

    private static RevisionedTabControllerProjection Projection(
        BrowserWindowId window,
        BrowserTabId selected,
        long revision,
        bool isPrivate = false)
    {
        var entry = new BrowserTabEntry(
            selected,
            null,
            "Selected tab",
            new Uri("https://example.test/"),
            BrowserLoadState.Idle,
            true,
            isPrivate,
            false,
            false);
        return new(
            window,
            isPrivate,
            revision,
            TabControllerHostState.Docked,
            TabStripPlacement.Top,
            new(window, selected, [entry]));
    }

    private static ResourceSampleProjection Sample(long id, DateTimeOffset at, BrowserTabId tab) => new(
        id,
        at,
        ResourceSamplingState.Active,
        ResourcePressureLevel.Low,
        2.5,
        64 * 1024 * 1024,
        4,
        false,
        "Sampling is active.",
        [new(tab, ResourceAttributionReliability.Unavailable, null, null, 0, 0)]);

    private static TabInteractionCapabilitiesPresentation Interaction(
        BrowserTabId tab,
        long revision,
        bool playing,
        bool muted) => new(
        tab,
        revision,
        playing,
        muted,
        TabAudioFeatureCapabilityPresentation.Available(TabAudioFeature.Mute),
        TabAudioFeatureCapabilityPresentation.Unavailable(
            TabAudioFeature.Volume,
            "Per-tab volume requires a verified browser audio engine."),
        TabAudioFeatureCapabilityPresentation.Unavailable(
            TabAudioFeature.Equalizer,
            "Equalizer unavailable."),
        TabAudioFeatureCapabilityPresentation.Unavailable(
            TabAudioFeature.Balance,
            "Balance unavailable."),
        TabAudioFeatureCapabilityPresentation.Unavailable(
            TabAudioFeature.OutputDevice,
            "Output routing unavailable."),
        []);

    private sealed class RecordingSink : ITabControllerCommandSink
    {
        public TabControllerCommand? Last { get; private set; }

        public ValueTask<TabControllerCommandResult> ExecuteAsync(
            TabControllerCommand command,
            CancellationToken cancellationToken = default)
        {
            Last = command;
            return ValueTask.FromResult(new TabControllerCommandResult(
                command.Action.StableActionId,
                TabControllerCommandOutcome.Accepted,
                "Completed."));
        }
    }
}
