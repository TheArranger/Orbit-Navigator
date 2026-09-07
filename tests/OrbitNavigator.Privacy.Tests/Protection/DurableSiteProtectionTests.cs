using System.Text;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Privacy;
using OrbitNavigator.Privacy.Protection;
using OrbitNavigator.Privacy.Tests.Persistence;
using Xunit;

namespace OrbitNavigator.Privacy.Tests.Protection;

public sealed class DurableSiteProtectionTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PersistentExceptionSurvivesRestartAfterExplicitHydration()
    {
        var storage = new TestProfileStorage();
        var clock = new FakeClock(Start);
        var context = Browsing();
        var site = Site("https://durable.test/path");
        var first = new SiteProtectionController(clock, storage);

        Assert.Equal(
            ProtectionEvaluationSource.StrictBaseline,
            first.Evaluate(context, site).Value?.Source);
        Assert.True((await first.HydrateProfileAsync(
            context.Privacy)).IsSuccess);
        var created = await first.RelaxAsync(
            new ProtectionRelaxationIntent(
                context,
                site,
                ProtectionRelaxationDuration.Persistent),
            default);

        var laterContext = Browsing(
            BrowserProfileMode.Normal,
            context.Privacy.ProfileId);
        var restarted = new SiteProtectionController(clock, storage);
        Assert.Equal(
            ProtectionEvaluationSource.StrictBaseline,
            restarted.Evaluate(laterContext, site).Value?.Source);

        var hydrated = await restarted.HydrateProfileAsync(
            laterContext.Privacy);
        var restored = restarted.Evaluate(
            laterContext,
            Site("https://durable.test/other"));

        Assert.True(created.IsSuccess);
        Assert.True(hydrated.IsSuccess);
        Assert.Equal(
            ProtectionEvaluationSource.PersistentException,
            restored.Value?.Source);
        Assert.Equal(created.Value?.Id, restored.Value?.ExceptionId);
        Assert.Equal(1, storage.WriteCount);
    }

    [Fact]
    public async Task SessionTemporaryAndPrivateRulesNeverWritePersistentStorage()
    {
        var storage = new TestProfileStorage();
        var controller = new SiteProtectionController(
            new FakeClock(Start),
            storage);
        var normal = Browsing();
        var privateContext = Browsing(BrowserProfileMode.Private);
        await controller.HydrateProfileAsync(normal.Privacy);
        await controller.HydrateProfileAsync(privateContext.Privacy);

        var session = await controller.RelaxAsync(
            new ProtectionRelaxationIntent(
                normal,
                Site("https://session.test"),
                ProtectionRelaxationDuration.Session),
            default);
        var temporary = await controller.RelaxAsync(
            new ProtectionRelaxationIntent(
                normal,
                Site("https://temporary.test"),
                ProtectionRelaxationDuration.Temporary),
            default);
        var privateSession = await controller.RelaxAsync(
            new ProtectionRelaxationIntent(
                privateContext,
                Site("https://private.test"),
                ProtectionRelaxationDuration.Session),
            default);

        Assert.True(session.IsSuccess);
        Assert.True(temporary.IsSuccess);
        Assert.True(privateSession.IsSuccess);
        Assert.Equal(0, storage.WriteCount);
        Assert.Equal(1, storage.ReadCount);
    }

    [Fact]
    public async Task CorruptPayloadFailsClosedAndCannotBeOverwritten()
    {
        var storage = new TestProfileStorage();
        var context = Browsing();
        var probe = new SiteProtectionController(
            new FakeClock(Start),
            storage);
        await probe.HydrateProfileAsync(context.Privacy);
        storage.ReplaceLastReadPayload(Encoding.UTF8.GetBytes(
            "{\"version\":1,\"profileId\":\"broken\",\"rules\":[]}"));
        var controller = new SiteProtectionController(
            new FakeClock(Start),
            storage);
        var site = Site("https://corrupt.test");

        var hydration = await controller.HydrateProfileAsync(context.Privacy);
        var mutation = await controller.RelaxAsync(
            new ProtectionRelaxationIntent(
                context,
                site,
                ProtectionRelaxationDuration.Persistent),
            default);
        var evaluation = controller.Evaluate(context, site);

        Assert.Equal(ControllerErrorCode.IntegrityFailure, hydration.Error?.Code);
        Assert.Equal(ControllerErrorCode.IntegrityFailure, mutation.Error?.Code);
        Assert.Equal(
            ProtectionEvaluationSource.StrictBaseline,
            evaluation.Value?.Source);
        Assert.Equal(0, storage.WriteCount);
    }

    [Fact]
    public async Task ConflictAndWriteFailureRetainPreviousException()
    {
        var storage = new TestProfileStorage();
        var context = Browsing();
        var site = Site("https://rollback.test");
        var controller = new SiteProtectionController(
            new FakeClock(Start),
            storage);
        await controller.HydrateProfileAsync(context.Privacy);
        var original = await controller.RelaxAsync(
            new ProtectionRelaxationIntent(
                context,
                site,
                ProtectionRelaxationDuration.Persistent),
            default);
        storage.BumpLastReadRevision();

        var conflict = await controller.RelaxAsync(
            new ProtectionRelaxationIntent(
                context,
                site,
                ProtectionRelaxationDuration.Persistent),
            default);
        storage.NextWriteError = ControllerError.Create(
            ControllerErrorCode.Unavailable,
            "error.test.write_failed");
        var removal = await controller.RemoveExceptionAsync(
            new RemoveProtectionExceptionIntent(
                context.Privacy,
                original.Value!.Id),
            default);
        var retained = controller.Evaluate(context, site);

        Assert.Equal(ControllerErrorCode.Conflict, conflict.Error?.Code);
        Assert.Equal(ControllerErrorCode.Unavailable, removal.Error?.Code);
        Assert.Equal(original.Value.Id, retained.Value?.ExceptionId);
    }

    [Fact]
    public async Task SuccessfulRemovalIsDurableAcrossRestart()
    {
        var storage = new TestProfileStorage();
        var context = Browsing();
        var site = Site("https://remove-durable.test");
        var first = new SiteProtectionController(
            new FakeClock(Start),
            storage);
        await first.HydrateProfileAsync(context.Privacy);
        var created = await first.RelaxAsync(
            new ProtectionRelaxationIntent(
                context,
                site,
                ProtectionRelaxationDuration.Persistent),
            default);
        var removed = await first.RemoveExceptionAsync(
            new RemoveProtectionExceptionIntent(
                context.Privacy,
                created.Value!.Id),
            default);
        var restarted = new SiteProtectionController(
            new FakeClock(Start),
            storage);
        await restarted.HydrateProfileAsync(context.Privacy);

        Assert.True(removed.IsSuccess);
        Assert.Equal(
            ProtectionEvaluationSource.StrictBaseline,
            restarted.Evaluate(context, site).Value?.Source);
        Assert.Equal(2, storage.WriteCount);
    }

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
}
