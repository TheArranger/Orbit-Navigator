using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Sync;
using OrbitNavigator.Sync.State;
using Xunit;

namespace OrbitNavigator.Sync.Tests.State;

public sealed class FileSyncSequenceAllocatorTests
{
    [Fact]
    public async Task ReservationsRemainMonotonicAcrossAllocatorRestart()
    {
        using var directory = new TemporaryDirectory();
        var profile = new ProfileId(Guid.NewGuid());
        var device = new DeviceId(Guid.NewGuid());
        var scope = new SyncStateScope(profile, device, 7);
        var context = Context(profile);
        var firstAllocator = new FileSyncSequenceAllocator(directory.Path);

        var first = await firstAllocator.ReserveNextAsync(context, scope, default);
        var second = await firstAllocator.ReserveNextAsync(context, scope, default);
        var restartedAllocator = new FileSyncSequenceAllocator(directory.Path);
        var third = await restartedAllocator.ReserveNextAsync(context, scope, default);

        Assert.Equal(0, first.Value!.Sequence);
        Assert.Equal(1, second.Value!.Sequence);
        Assert.Equal(2, third.Value!.Sequence);
    }

    [Fact]
    public async Task ConcurrentReservationsAreUniqueAndGapFree()
    {
        using var directory = new TemporaryDirectory();
        var profile = new ProfileId(Guid.NewGuid());
        var scope = new SyncStateScope(profile, new DeviceId(Guid.NewGuid()), 3);
        var context = Context(profile);
        var allocator = new FileSyncSequenceAllocator(directory.Path);

        var reservations = await Task.WhenAll(
            Enumerable.Range(0, 32)
                .Select(_ => allocator.ReserveNextAsync(context, scope, default).AsTask()));

        Assert.All(reservations, result => Assert.True(result.IsSuccess));
        Assert.Equal(
            Enumerable.Range(0, 32).Select(value => (long)value),
            reservations.Select(result => result.Value!.Sequence).Order());
    }

    [Fact]
    public async Task SeparateAllocatorInstancesCoordinateSameScope()
    {
        using var directory = new TemporaryDirectory();
        var profile = new ProfileId(Guid.NewGuid());
        var scope = new SyncStateScope(profile, new DeviceId(Guid.NewGuid()), 9);
        var context = Context(profile);
        var first = new FileSyncSequenceAllocator(directory.Path);
        var second = new FileSyncSequenceAllocator(directory.Path);

        var reservations = await Task.WhenAll(
            Enumerable.Range(0, 24).Select(index =>
                (index % 2 == 0 ? first : second)
                    .ReserveNextAsync(context, scope, default)
                    .AsTask()));

        Assert.Equal(
            Enumerable.Range(0, 24).Select(value => (long)value),
            reservations.Select(result => result.Value!.Sequence).Order());
    }

    [Fact]
    public async Task GenerationHasIndependentDurableSequence()
    {
        using var directory = new TemporaryDirectory();
        var profile = new ProfileId(Guid.NewGuid());
        var device = new DeviceId(Guid.NewGuid());
        var context = Context(profile);
        var allocator = new FileSyncSequenceAllocator(directory.Path);

        var oldGeneration = await allocator.ReserveNextAsync(
            context,
            new SyncStateScope(profile, device, 4),
            default);
        var newGeneration = await allocator.ReserveNextAsync(
            context,
            new SyncStateScope(profile, device, 5),
            default);

        Assert.Equal(0, oldGeneration.Value!.Sequence);
        Assert.Equal(0, newGeneration.Value!.Sequence);
    }

    [Fact]
    public async Task MismatchedProfileIsRejectedWithoutCreatingState()
    {
        using var directory = new TemporaryDirectory();
        var scope = new SyncStateScope(
            new ProfileId(Guid.NewGuid()),
            new DeviceId(Guid.NewGuid()),
            1);
        var allocator = new FileSyncSequenceAllocator(directory.Path);

        var result = await allocator.ReserveNextAsync(
            Context(new ProfileId(Guid.NewGuid())),
            scope,
            default);

        Assert.False(result.IsSuccess);
        Assert.Equal(ControllerErrorCode.InvalidRequest, result.Error?.Code);
        Assert.False(Directory.Exists(directory.Path));
    }

    private static SyncOperationContext Context(ProfileId profile)
    {
        var browsing = new BrowsingContext(
            new PrivacyContext(
                profile,
                new BrowserSessionId(Guid.NewGuid()),
                BrowserProfileMode.Normal),
            new BrowserWindowId(Guid.NewGuid()),
            new BrowserTabId(Guid.NewGuid()),
            null);
        return SyncOperationContext.Authorize(
            browsing,
            new SyncOperationId(Guid.NewGuid())).Value!;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"orbit-navigator-sequence-tests-{Guid.NewGuid():N}");
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
