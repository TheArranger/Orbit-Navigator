using System.Collections.Concurrent;
using System.Text;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Privacy;
using OrbitNavigator.Privacy.Permissions;
using OrbitNavigator.Privacy.Tests.Persistence;
using Xunit;

namespace OrbitNavigator.Privacy.Tests.Permissions;

public sealed class DurablePermissionBrokerTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PersistentRuleSurvivesRestartAfterExplicitHydration()
    {
        var storage = new TestProfileStorage();
        var clock = new FakeClock(Start);
        var sink = new RecordingSink();
        var context = Browsing();
        var site = Site("https://camera.test/path");
        var first = new PermissionBroker(clock, sink, storage);
        await first.HydrateProfileAsync(context.Privacy);
        var request = Request(
            context,
            site,
            [PermissionAllowScope.Persistent]);
        await first.IngestAsync(request, default);

        var accepted = await first.RespondAsync(
            Response(request, PermissionAllowScope.Persistent),
            default);
        var later = Browsing(
            BrowserProfileMode.Normal,
            context.Privacy.ProfileId);
        var restarted = new PermissionBroker(
            clock,
            new RecordingSink(),
            storage);
        var beforeHydration = await restarted.GetCurrentSiteStateAsync(
            later,
            site,
            default);
        var hydrated = await restarted.HydrateProfileAsync(later.Privacy);
        var restored = await restarted.GetCurrentSiteStateAsync(
            later,
            Site("https://camera.test/other"),
            default);

        Assert.True(accepted.IsSuccess);
        Assert.Equal(ControllerErrorCode.Unavailable, beforeHydration.Error?.Code);
        Assert.True(hydrated.IsSuccess);
        var rule = Assert.Single(restored.Value!.Rules);
        Assert.Equal(accepted.Value?.AppliedRule?.Id, rule.Id);
        Assert.Equal(PermissionAllowScope.Persistent, rule.Scope);
        Assert.Equal(1, storage.WriteCount);
        Assert.Equal(PermissionHostDisposition.Allow, Assert.Single(
            sink.Completions).Disposition);
    }

    [Fact]
    public async Task SessionAndPrivateRulesNeverWritePersistentStorage()
    {
        var storage = new TestProfileStorage();
        var sink = new RecordingSink();
        var broker = new PermissionBroker(
            new FakeClock(Start),
            sink,
            storage);
        var normal = Browsing();
        var privateContext = Browsing(BrowserProfileMode.Private);
        await broker.HydrateProfileAsync(normal.Privacy);
        await broker.HydrateProfileAsync(privateContext.Privacy);
        var normalRequest = Request(
            normal,
            Site("https://session.test"),
            [PermissionAllowScope.Session]);
        var privateRequest = Request(
            privateContext,
            Site("https://private.test"),
            [PermissionAllowScope.Session]);
        await broker.IngestAsync(normalRequest, default);
        await broker.RespondAsync(
            Response(normalRequest, PermissionAllowScope.Session),
            default);
        await broker.IngestAsync(privateRequest, default);
        await broker.RespondAsync(
            Response(privateRequest, PermissionAllowScope.Session),
            default);

        Assert.Equal(0, storage.WriteCount);
        Assert.Equal(1, storage.ReadCount);
        Assert.Equal(2, sink.Completions.Count);
    }

    [Fact]
    public async Task CorruptPayloadFailsClosedWithTypedIntegrityFailure()
    {
        var storage = new TestProfileStorage();
        var context = Browsing();
        var probe = new PermissionBroker(
            new FakeClock(Start),
            new RecordingSink(),
            storage);
        await probe.HydrateProfileAsync(context.Privacy);
        storage.ReplaceLastReadPayload(Encoding.UTF8.GetBytes(
            "{\"version\":999,\"profileId\":\"bad\",\"rules\":[]}"));
        var sink = new RecordingSink();
        var broker = new PermissionBroker(
            new FakeClock(Start),
            sink,
            storage);
        var hydration = await broker.HydrateProfileAsync(context.Privacy);
        var request = Request(
            context,
            Site("https://corrupt.test"),
            [PermissionAllowScope.Persistent]);

        var ingest = await broker.IngestAsync(request, default);

        Assert.Equal(ControllerErrorCode.IntegrityFailure, hydration.Error?.Code);
        Assert.Equal(ControllerErrorCode.IntegrityFailure, ingest.Error?.Code);
        var completion = Assert.Single(sink.Completions);
        Assert.True(completion.IsFailClosed);
        Assert.Equal(PermissionHostDisposition.Deny, completion.Disposition);
        Assert.Equal(0, storage.WriteCount);
    }

    [Fact]
    public async Task InvalidPersistedCapabilityIsRejectedDuringHydration()
    {
        var storage = new TestProfileStorage();
        var context = Browsing();
        var broker = new PermissionBroker(
            new FakeClock(Start),
            new RecordingSink(),
            storage);
        await broker.HydrateProfileAsync(context.Privacy);
        var request = Request(
            context,
            Site("https://enum.test"),
            [PermissionAllowScope.Persistent]);
        await broker.IngestAsync(request, default);
        await broker.RespondAsync(
            Response(request, PermissionAllowScope.Persistent),
            default);
        var json = Encoding.UTF8.GetString(storage.GetLastReadPayload())
            .Replace("Camera", "Unknown", StringComparison.Ordinal);
        storage.ReplaceLastReadPayload(Encoding.UTF8.GetBytes(json));
        var restarted = new PermissionBroker(
            new FakeClock(Start),
            new RecordingSink(),
            storage);

        var hydration = await restarted.HydrateProfileAsync(context.Privacy);

        Assert.Equal(ControllerErrorCode.IntegrityFailure, hydration.Error?.Code);
    }

    [Fact]
    public async Task ConflictAndWriteFailureRetainPreviousRule()
    {
        var storage = new TestProfileStorage();
        var sink = new RecordingSink();
        var context = Browsing();
        var site = Site("https://rollback.test");
        var broker = new PermissionBroker(
            new FakeClock(Start),
            sink,
            storage);
        await broker.HydrateProfileAsync(context.Privacy);
        var first = Request(
            context,
            site,
            [PermissionAllowScope.Persistent]);
        await broker.IngestAsync(first, default);
        var accepted = await broker.RespondAsync(
            Response(first, PermissionAllowScope.Persistent),
            default);
        var originalId = accepted.Value!.AppliedRule!.Id;
        storage.BumpLastReadRevision();
        var replacement = Request(
            context,
            site,
            [PermissionAllowScope.Persistent]);
        await broker.IngestAsync(replacement, default);

        var conflict = await broker.RespondAsync(
            Response(replacement, PermissionAllowScope.Persistent),
            default);
        storage.NextWriteError = ControllerError.Create(
            ControllerErrorCode.Unavailable,
            "error.test.write_failed");
        var reset = await broker.ResetAsync(
            new ResetPermissionRuleIntent(context, originalId),
            default);
        var retained = await broker.GetCurrentSiteStateAsync(
            context,
            site,
            default);

        Assert.Equal(ControllerErrorCode.Conflict, conflict.Error?.Code);
        Assert.Equal(ControllerErrorCode.Unavailable, reset.Error?.Code);
        Assert.Equal(originalId, Assert.Single(retained.Value!.Rules).Id);
        Assert.True(sink.Completions.Last().IsFailClosed);
    }

    [Fact]
    public async Task SuccessfulResetIsDurableAcrossRestart()
    {
        var storage = new TestProfileStorage();
        var context = Browsing();
        var site = Site("https://reset-durable.test");
        var broker = new PermissionBroker(
            new FakeClock(Start),
            new RecordingSink(),
            storage);
        await broker.HydrateProfileAsync(context.Privacy);
        var request = Request(
            context,
            site,
            [PermissionAllowScope.Persistent]);
        await broker.IngestAsync(request, default);
        var accepted = await broker.RespondAsync(
            Response(request, PermissionAllowScope.Persistent),
            default);
        var reset = await broker.ResetAsync(
            new ResetPermissionRuleIntent(
                context,
                accepted.Value!.AppliedRule!.Id),
            default);
        var restarted = new PermissionBroker(
            new FakeClock(Start),
            new RecordingSink(),
            storage);
        await restarted.HydrateProfileAsync(context.Privacy);
        var restored = await restarted.GetCurrentSiteStateAsync(
            context,
            site,
            default);

        Assert.True(reset.IsSuccess);
        Assert.Empty(restored.Value!.Rules);
        Assert.Equal(2, storage.WriteCount);
    }

    private static PermissionBrokerRequest Request(
        BrowsingContext context,
        SiteIdentity site,
        IReadOnlyList<PermissionAllowScope> scopes) =>
        new(
            new RequestId(Guid.NewGuid()),
            new ResponseToken(Guid.NewGuid()),
            context,
            site,
            WebPermissionCapability.Camera,
            scopes,
            PermissionDecision.Ask,
            PermissionDecision.Deny,
            true,
            Start,
            Start.AddMinutes(1));

    private static PermissionResponse Response(
        PermissionBrokerRequest request,
        PermissionAllowScope scope) =>
        new(
            request.RequestId,
            request.ResponseToken,
            request.Context,
            PermissionDecision.Allow,
            scope,
            Start.AddSeconds(1));

    private static SiteIdentity Site(string uri) =>
        SiteIdentity.Create(new Uri(uri)).Value!;

    private static BrowsingContext Browsing(
        BrowserProfileMode mode = BrowserProfileMode.Normal,
        ProfileId? profileId = null) =>
        new(
            new PrivacyContext(
                profileId ?? new ProfileId(Guid.NewGuid()),
                new BrowserSessionId(Guid.NewGuid()),
                mode),
            new BrowserWindowId(Guid.NewGuid()),
            new BrowserTabId(Guid.NewGuid()),
            null);

    private sealed class FakeClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class RecordingSink : IPermissionHostCompletionSink
    {
        private readonly ConcurrentQueue<PermissionHostCompletion> _completions = [];

        public IReadOnlyList<PermissionHostCompletion> Completions =>
            _completions.ToArray();

        public ValueTask<ControllerResult> CompleteAsync(
            PermissionHostCompletion completion,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.False(completion.SaveInProfile);
            _completions.Enqueue(completion);
            return ValueTask.FromResult(ControllerResult.Success());
        }
    }
}
