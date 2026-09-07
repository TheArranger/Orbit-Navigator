using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Foundation.Browser;
using OrbitNavigator.Foundation.Profiles;
using Xunit;

namespace OrbitNavigator.Foundation.Tests.Browser;

public sealed class TabControllerLayoutStoreTests
{
    [Fact]
    public async Task NormalLayoutIsDurableAndRevisioned()
    {
        using var temp = new TempDirectory();
        var storage = new FileProfileStorage(temp.Path);
        var store = new TabControllerLayoutStore(storage);
        var context = Context(BrowserProfileMode.Normal);
        var bounds = new TabControllerBoundsDip(-900, 40, 520, 720);

        var first = await store.SaveAsync(new(
            context, default, TabControllerDockState.Detached, bounds));
        var stale = await store.SaveAsync(new(
            context, default, TabControllerDockState.Docked, bounds));
        var loaded = await new TabControllerLayoutStore(storage).LoadAsync(context);

        Assert.True(first.IsSuccess);
        Assert.False(first.Value!.Revision.IsEmpty);
        Assert.False(stale.IsSuccess);
        Assert.Equal(ControllerErrorCode.Conflict, stale.Error?.Code);
        Assert.Equal(TabControllerDockState.Detached, loaded.Value!.DockState);
        Assert.Equal(bounds, loaded.Value.DetachedBounds);
        Assert.Equal(first.Value.Revision, loaded.Value.Revision);
    }

    [Fact]
    public async Task PrivateLayoutIsDockedAndNeverTouchesStorage()
    {
        var storage = new CountingStorage();
        var store = new TabControllerLayoutStore(storage);
        var context = Context(BrowserProfileMode.Private);

        var loaded = await store.LoadAsync(context);
        var save = await store.SaveAsync(new(
            context,
            default,
            TabControllerDockState.Detached,
            new TabControllerBoundsDip(20, 20, 500, 600)));

        Assert.True(loaded.IsSuccess);
        Assert.Equal(TabControllerDockState.Docked, loaded.Value!.DockState);
        Assert.False(save.IsSuccess);
        Assert.Equal(ControllerErrorCode.PolicyDenied, save.Error?.Code);
        Assert.Equal(0, storage.TotalCalls);
    }

    [Theory]
    [InlineData(double.NaN, 0, 500, 600)]
    [InlineData(0, double.PositiveInfinity, 500, 600)]
    [InlineData(0, 0, 200, 600)]
    [InlineData(0, 0, 500, 100)]
    public async Task InvalidBoundsAreRejected(
        double left,
        double top,
        double width,
        double height)
    {
        var storage = new CountingStorage();
        var store = new TabControllerLayoutStore(storage);

        var result = await store.SaveAsync(new(
            Context(BrowserProfileMode.Normal),
            default,
            TabControllerDockState.Detached,
            new TabControllerBoundsDip(left, top, width, height)));

        Assert.False(result.IsSuccess);
        Assert.Equal(ControllerErrorCode.InvalidRequest, result.Error?.Code);
        Assert.Equal(0, storage.TotalCalls);
    }

    private static PrivacyContext Context(BrowserProfileMode mode) =>
        new(
            new ProfileId(Guid.NewGuid()),
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
            throw new InvalidOperationException("Private layout must not be written.");
        }

        public ValueTask<ControllerResult> DeleteAsync(
            ProfileStorageAddress address,
            ProfileStorageRevision? expectedRevision = null,
            CancellationToken cancellationToken = default)
        {
            TotalCalls++;
            throw new InvalidOperationException("Private layout must not be deleted.");
        }
    }
}
