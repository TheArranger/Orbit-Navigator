using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Foundation.Browser;
using OrbitNavigator.Foundation.Profiles;
using Xunit;

namespace OrbitNavigator.Foundation.Tests.Browser;

public sealed class HistoryFacadeTests
{
    [Fact]
    public async Task NormalHistoryIsDurableAndAggregatesVisits()
    {
        using var temp = new TempDirectory();
        var storage = new FileProfileStorage(temp.Path);
        var facade = new HistoryFacade(storage);
        var context = Browsing(BrowserProfileMode.Normal);
        var target = new Uri("https://example.com/guide");
        var firstTime = new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

        var first = await facade.RecordVisitAsync(new(context, target, "Guide", firstTime));
        var second = await facade.RecordVisitAsync(new(
            context,
            target,
            "Updated guide",
            firstTime.AddMinutes(1)));
        var durable = await new HistoryFacade(storage).QueryAsync(new(
            context.Privacy,
            null,
            null,
            10));

        var entry = Assert.Single(durable.Value!);
        Assert.Equal(first.Value!.Id, second.Value!.Id);
        Assert.Equal(2, entry.VisitCount);
        Assert.Equal("Updated guide", entry.Title);
    }

    [Fact]
    public async Task PrivateHistoryIsEmptyAndNeverMutatesNormalHistory()
    {
        using var temp = new TempDirectory();
        var facade = new HistoryFacade(new FileProfileStorage(temp.Path));
        var profile = new ProfileId(Guid.NewGuid());
        var normal = Browsing(BrowserProfileMode.Normal, profile);
        var privateContext = Browsing(BrowserProfileMode.Private, profile);
        await facade.RecordVisitAsync(new(
            normal,
            new Uri("https://example.com/"),
            "Example",
            DateTimeOffset.UtcNow));

        var privateQuery = await facade.QueryAsync(new(privateContext.Privacy, null, null, 10));
        var privateRecord = await facade.RecordVisitAsync(new(
            privateContext,
            new Uri("https://openai.com/"),
            "OpenAI",
            DateTimeOffset.UtcNow));
        var privateClear = await facade.ClearAsync(new(
            privateContext.Privacy,
            null,
            null,
            true));
        var normalQuery = await facade.QueryAsync(new(normal.Privacy, null, null, 10));

        Assert.Empty(privateQuery.Value!);
        Assert.Equal(ControllerErrorCode.PolicyDenied, privateRecord.Error?.Code);
        Assert.Equal(ControllerErrorCode.PolicyDenied, privateClear.Error?.Code);
        Assert.Single(normalQuery.Value!);
    }

    private static BrowsingContext Browsing(BrowserProfileMode mode, ProfileId? profile = null) =>
        new(
            new PrivacyContext(
                profile ?? new ProfileId(Guid.NewGuid()),
                new BrowserSessionId(Guid.NewGuid()),
                mode),
            new BrowserWindowId(Guid.NewGuid()),
            new BrowserTabId(Guid.NewGuid()),
            null);
}
