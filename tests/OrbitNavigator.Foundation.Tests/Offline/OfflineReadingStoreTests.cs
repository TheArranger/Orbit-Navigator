using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Foundation.Offline;

using Xunit;

namespace OrbitNavigator.Foundation.Tests.Offline;

public sealed class OfflineReadingStoreTests
{
    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3, 4];

    [Fact]
    public async Task ExplicitSnapshotIsDurableRevisionedAndDeletesAtomically()
    {
        using var temp = new TempDirectory();
        var clock = new TestClock(new DateTimeOffset(2026, 8, 17, 20, 0, 0, TimeSpan.Zero));
        var context = Context(BrowserProfileMode.Normal);
        var store = new OfflineReadingStore(temp.Path, clock);

        var initial = await store.LoadCatalogAsync(context);
        var saved = await store.SaveAsync(new(
            context,
            initial.Value!.Revision,
            " Example page ",
            new Uri("https://example.test/article"),
            Png));
        var stale = await store.SaveAsync(new(
            context,
            0,
            "Stale",
            new Uri("https://example.test/stale"),
            Png));
        var reopened = await new OfflineReadingStore(temp.Path, clock).LoadCatalogAsync(context);
        var content = await store.OpenAsync(context, saved.Value!.Items.Single().ItemId);
        var deleted = await store.DeleteAsync(new(
            context,
            saved.Value.Revision,
            saved.Value.Items.Single().ItemId));

        Assert.Equal(0, initial.Value.Revision);
        Assert.Equal(1, saved.Value.Revision);
        Assert.Equal("Example page", saved.Value.Items.Single().Title);
        Assert.Equal(clock.UtcNow, saved.Value.Items.Single().SavedAtUtc);
        Assert.Equal(ControllerErrorCode.Conflict, stale.Error?.Code);
        Assert.Equal(1, reopened.Value!.Revision);
        Assert.Equal(Png, content.Value!.PngBytes.ToArray());
        Assert.Equal(2, deleted.Value!.Revision);
        Assert.Empty(deleted.Value.Items);
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*.png", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task PrivateOperationsFailBeforeAnyStoragePathIsCreated()
    {
        using var temp = new TempDirectory();
        var emptyRoot = Path.Combine(temp.Path, "offline");
        var context = Context(BrowserProfileMode.Private);
        var store = new OfflineReadingStore(emptyRoot, new TestClock(DateTimeOffset.UtcNow));

        var load = await store.LoadCatalogAsync(context);
        var save = await store.SaveAsync(new(
            context,
            0,
            "Private",
            new Uri("https://private.test/"),
            Png));
        var open = await store.OpenAsync(context, new OfflineReadingItemId(Guid.NewGuid()));
        var delete = await store.DeleteAsync(new(
            context,
            0,
            new OfflineReadingItemId(Guid.NewGuid())));

        Assert.All(
            new[] { load.Error, save.Error, open.Error, delete.Error },
            error => Assert.Equal(ControllerErrorCode.PolicyDenied, error?.Code));
        Assert.False(Directory.Exists(emptyRoot));
    }

    [Fact]
    public async Task InvalidOrNonPngContentNeverCreatesCatalogOrContent()
    {
        using var temp = new TempDirectory();
        var root = Path.Combine(temp.Path, "offline");
        var context = Context(BrowserProfileMode.Normal);
        var store = new OfflineReadingStore(root, new TestClock(DateTimeOffset.UtcNow));

        var result = await store.SaveAsync(new(
            context,
            0,
            "Unsafe",
            new Uri("https://example.test/"),
            "<html>network-capable content</html>"u8.ToArray()));

        Assert.Equal(ControllerErrorCode.InvalidRequest, result.Error?.Code);
        Assert.False(Directory.Exists(root));
    }

    private static PrivacyContext Context(BrowserProfileMode mode) => new(
        new ProfileId(Guid.NewGuid()),
        new BrowserSessionId(Guid.NewGuid()),
        mode);

    private sealed class TestClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
