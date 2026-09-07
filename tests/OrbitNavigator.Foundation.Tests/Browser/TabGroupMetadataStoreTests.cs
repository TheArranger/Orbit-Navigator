using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Foundation.Browser;
using OrbitNavigator.Foundation.Profiles;
using Xunit;

namespace OrbitNavigator.Foundation.Tests.Browser;

public sealed class TabGroupMetadataStoreTests
{
    [Fact]
    public async Task ReplaceIsDurableRevisionedAndAtomic()
    {
        using var temp = new TempDirectory();
        var store = new TabGroupMetadataStore(new FileProfileStorage(temp.Path));
        var context = Context(BrowserProfileMode.Normal);
        var window = new BrowserWindowId(Guid.NewGuid());
        var group = Group("Research");

        var first = await store.ReplaceAsync(new ReplaceTabGroupMetadataIntent(
            context,
            window,
            default,
            [group]));
        var loaded = await store.LoadAsync(context, window);
        var stale = await store.ReplaceAsync(new ReplaceTabGroupMetadataIntent(
            context,
            window,
            default,
            [Group("Stale")]));

        Assert.True(first.IsSuccess);
        Assert.False(first.Value?.Revision.IsEmpty);
        Assert.True(loaded.IsSuccess);
        Assert.Equal(first.Value?.Revision, loaded.Value?.Revision);
        Assert.Equal("Research", Assert.Single(loaded.Value!.Groups).Name);
        Assert.False(stale.IsSuccess);
        Assert.Equal(ControllerErrorCode.Conflict, stale.Error?.Code);
    }

    [Fact]
    public async Task PrivateCatalogIsMemoryOnlyAndCannotCrossSessionBoundary()
    {
        var storage = new CountingStorage();
        await using var store = new TabGroupMetadataStore(storage);
        var profile = new ProfileId(Guid.NewGuid());
        var first = Context(BrowserProfileMode.Private, profile);
        var second = Context(BrowserProfileMode.Private, profile);
        var window = new BrowserWindowId(Guid.NewGuid());

        var saved = await store.ReplaceAsync(new ReplaceTabGroupMetadataIntent(
            first,
            window,
            default,
            [Group("Private") ]));
        var crossSession = await store.LoadAsync(second, window);

        Assert.True(saved.IsSuccess);
        Assert.False(crossSession.IsSuccess);
        Assert.Equal(ControllerErrorCode.PolicyDenied, crossSession.Error?.Code);
        Assert.Equal(0, storage.TotalCalls);
    }

    [Fact]
    public async Task PrivateCatalogUsesLiveCasAndNeverCallsStorage()
    {
        var storage = new CountingStorage();
        await using var store = new TabGroupMetadataStore(storage);
        var context = Context(BrowserProfileMode.Private);
        var window = new BrowserWindowId(Guid.NewGuid());

        var first = await store.ReplaceAsync(new(
            context, window, default, [Group("One")]));
        var stale = await store.ReplaceAsync(new(
            context, window, default, [Group("Stale")]));
        var second = await store.ReplaceAsync(new(
            context, window, first.Value!.Revision, [Group("Two")]));
        var loaded = await store.LoadAsync(context, window);

        Assert.True(first.IsSuccess);
        Assert.False(stale.IsSuccess);
        Assert.Equal(ControllerErrorCode.Conflict, stale.Error?.Code);
        Assert.True(second.IsSuccess);
        Assert.NotEqual(first.Value.Revision, second.Value!.Revision);
        Assert.Equal("Two", Assert.Single(loaded.Value!.Groups).Name);
        Assert.Equal(0, storage.TotalCalls);
    }

    [Fact]
    public async Task PrivateCatalogDisposeClearsAndFailsClosedAndReopenStartsEmpty()
    {
        var storage = new CountingStorage();
        var context = Context(BrowserProfileMode.Private);
        var window = new BrowserWindowId(Guid.NewGuid());
        var store = new TabGroupMetadataStore(storage);
        await store.ReplaceAsync(new(context, window, default, [Group("Private")]));

        await store.DisposeAsync();
        var afterClose = await store.LoadAsync(context, window);
        await using var reopened = new TabGroupMetadataStore(storage);
        var reopenedState = await reopened.LoadAsync(context, window);

        Assert.False(afterClose.IsSuccess);
        Assert.Equal(ControllerErrorCode.Unavailable, afterClose.Error?.Code);
        Assert.True(reopenedState.IsSuccess);
        Assert.Empty(reopenedState.Value!.Groups);
        Assert.Equal(0, storage.TotalCalls);
    }

    [Fact]
    public async Task DuplicateTabMembershipIsRejectedWithoutMutation()
    {
        using var temp = new TempDirectory();
        var store = new TabGroupMetadataStore(new FileProfileStorage(temp.Path));
        var context = Context(BrowserProfileMode.Normal);
        var window = new BrowserWindowId(Guid.NewGuid());
        var tab = new BrowserTabId(Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;
        var groups = new[]
        {
            new TabGroupMetadata(new BrowserTabGroupId(Guid.NewGuid()), "One", false, [tab], now),
            new TabGroupMetadata(new BrowserTabGroupId(Guid.NewGuid()), "Two", false, [tab], now),
        };

        var result = await store.ReplaceAsync(new ReplaceTabGroupMetadataIntent(
            context,
            window,
            default,
            groups));
        var loaded = await store.LoadAsync(context, window);

        Assert.False(result.IsSuccess);
        Assert.Equal(ControllerErrorCode.InvalidRequest, result.Error?.Code);
        Assert.Empty(loaded.Value!.Groups);
    }

    private static TabGroupMetadata Group(string name) =>
        new(
            new BrowserTabGroupId(Guid.NewGuid()),
            name,
            false,
            [new BrowserTabId(Guid.NewGuid())],
            DateTimeOffset.UtcNow);

    private static PrivacyContext Context(
        BrowserProfileMode mode,
        ProfileId? profile = null) =>
        new(
            profile ?? new ProfileId(Guid.NewGuid()),
            new BrowserSessionId(Guid.NewGuid()),
            mode);

    private sealed class CountingStorage : IProfileStorage
    {
        public int TotalCalls { get; private set; }

        public ValueTask<ControllerResult<ProfileStorageEntry>> ReadAsync(
            ProfileStorageAddress address,
            CancellationToken cancellationToken = default)
        {
            TotalCalls++;
            return ValueTask.FromResult(ControllerResult<ProfileStorageEntry>.Failure(
                ControllerError.Create(ControllerErrorCode.NotFound, "error.test.not_found")));
        }

        public ValueTask<ControllerResult<ProfileStorageWriteReceipt>> WriteAsync(
            ProfileStorageWriteRequest request,
            CancellationToken cancellationToken = default)
        {
            TotalCalls++;
            throw new InvalidOperationException("Private tab groups must not write profile storage.");
        }

        public ValueTask<ControllerResult> DeleteAsync(
            ProfileStorageAddress address,
            ProfileStorageRevision? expectedRevision = null,
            CancellationToken cancellationToken = default)
        {
            TotalCalls++;
            throw new InvalidOperationException("Private tab groups must not delete profile storage.");
        }
    }
}
