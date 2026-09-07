using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Foundation.Browser;
using OrbitNavigator.Foundation.Profiles;
using Xunit;

namespace OrbitNavigator.Foundation.Tests.Browser;

public sealed class BookmarksFacadeTests
{
    [Fact]
    public async Task BookmarkCatalogIsDurableAndDuplicateTargetKeepsStableId()
    {
        using var temp = new TempDirectory();
        var clock = new MutableClock(new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero));
        var storage = new FileProfileStorage(temp.Path);
        var facade = new BookmarksFacade(storage, clock);
        var context = Browsing(BrowserProfileMode.Normal);
        var target = new Uri("https://example.com/path");

        var added = await facade.AddAsync(new(context, target, "First title")
        {
            Note = "  Initial note  ",
        });
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        var updated = await facade.AddAsync(new(context, target, "Updated title")
        {
            Note = "Durable note after relaunch",
        });
        var durable = await new BookmarksFacade(storage, clock).QueryAsync(new(
            context.Privacy,
            null,
            10));

        var bookmark = Assert.Single(durable.Value!);
        Assert.Equal(added.Value!.Id, updated.Value!.Id);
        Assert.Equal(added.Value.Id, bookmark.Id);
        Assert.Equal("Updated title", bookmark.Title);
        Assert.Equal("Durable note after relaunch", updated.Value.Note);
        Assert.Equal("Durable note after relaunch", bookmark.Note);
        Assert.Equal(added.Value.CreatedAtUtc, bookmark.CreatedAtUtc);
        Assert.Equal(clock.UtcNow, bookmark.UpdatedAtUtc);
    }

    [Fact]
    public async Task PrivateWindowReadsNormalBookmarksButCannotMutateThem()
    {
        using var temp = new TempDirectory();
        var facade = new BookmarksFacade(
            new FileProfileStorage(temp.Path),
            new MutableClock(DateTimeOffset.UtcNow));
        var profile = new ProfileId(Guid.NewGuid());
        var normal = Browsing(BrowserProfileMode.Normal, profile);
        var privateContext = Browsing(BrowserProfileMode.Private, profile);
        var bookmark = await facade.AddAsync(new(
            normal,
            new Uri("https://example.com/"),
            "Example")
        {
            Note = "Normal profile note",
        });

        var privateQuery = await facade.QueryAsync(new(privateContext.Privacy, null, 10));
        var privateAdd = await facade.AddAsync(new(
            privateContext,
            new Uri("https://openai.com/"),
            "OpenAI")
        {
            Note = "Private write must not persist",
        });
        var privateRemove = await facade.RemoveAsync(new(
            privateContext.Privacy,
            bookmark.Value!.Id));
        var normalQuery = await facade.QueryAsync(new(normal.Privacy, null, 10));

        Assert.Single(privateQuery.Value!);
        Assert.Equal("Normal profile note", privateQuery.Value![0].Note);
        Assert.Equal(ControllerErrorCode.PolicyDenied, privateAdd.Error?.Code);
        Assert.Equal(ControllerErrorCode.PolicyDenied, privateRemove.Error?.Code);
        Assert.Single(normalQuery.Value!);
        Assert.Equal("Normal profile note", normalQuery.Value![0].Note);
    }

    [Fact]
    public async Task NonWebBookmarkIsRejected()
    {
        using var temp = new TempDirectory();
        var facade = new BookmarksFacade(
            new FileProfileStorage(temp.Path),
            new MutableClock(DateTimeOffset.UtcNow));
        var context = Browsing(BrowserProfileMode.Normal);

        var result = await facade.AddAsync(new(
            context,
            new Uri("file:///C:/secret.txt"),
            "Local file"));

        Assert.Equal(ControllerErrorCode.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task OversizedNoteIsRejectedWithoutWritingBookmark()
    {
        using var temp = new TempDirectory();
        var facade = new BookmarksFacade(
            new FileProfileStorage(temp.Path),
            new MutableClock(DateTimeOffset.UtcNow));
        var context = Browsing(BrowserProfileMode.Normal);

        var result = await facade.AddAsync(new(
            context,
            new Uri("https://example.com/"),
            "Example")
        {
            Note = new string('n', BookmarksFacade.MaximumNoteLength + 1),
        });
        var query = await facade.QueryAsync(new(context.Privacy, null, 10));

        Assert.Equal(ControllerErrorCode.InvalidRequest, result.Error?.Code);
        Assert.Empty(query.Value!);
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

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }
}
