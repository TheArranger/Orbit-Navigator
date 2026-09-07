using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Foundation.Profiles;
using Xunit;

namespace OrbitNavigator.Foundation.Tests.Profiles;

public sealed class WebViewProfileLifecycleTests
{
    [Fact]
    public async Task PrivateLeaseDeletesItsSessionDirectoryOnLastDispose()
    {
        using var temp = new TempDirectory();
        var lifecycle = new WebViewProfileLifecycle(temp.Path);
        var context = Context(BrowserProfileMode.Private);
        var leaseResult = await lifecycle.AcquireAsync(context);
        var lease = leaseResult.Value!;
        var path = lease.Descriptor.UserDataFolder;

        Assert.True(Directory.Exists(path));
        Assert.True(lease.Descriptor.MustDeleteAtSessionEnd);

        await lease.DisposeAsync();

        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task NormalProfileDirectorySurvivesLeaseDispose()
    {
        using var temp = new TempDirectory();
        var lifecycle = new WebViewProfileLifecycle(temp.Path);
        var lease = (await lifecycle.AcquireAsync(Context(BrowserProfileMode.Normal))).Value!;
        var path = lease.Descriptor.UserDataFolder;

        await lease.DisposeAsync();

        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public async Task PrivateLeaseRetriesUntilTransientLockIsReleased()
    {
        using var temp = new TempDirectory();
        var lifecycle = new WebViewProfileLifecycle(temp.Path);
        var lease = (await lifecycle.AcquireAsync(Context(BrowserProfileMode.Private))).Value!;
        var path = lease.Descriptor.UserDataFolder;
        var lockPath = Path.Combine(path, "lockfile");
        await File.WriteAllTextAsync(lockPath, "locked");
        await using var heldLock = new FileStream(
            lockPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        var cleanup = lease.DisposeAsync().AsTask();
        await Task.Delay(150);
        Assert.False(cleanup.IsCompleted);

        await heldLock.DisposeAsync();
        await cleanup;

        Assert.False(Directory.Exists(path));
    }

    private static PrivacyContext Context(BrowserProfileMode mode) =>
        new(
            new ProfileId(Guid.NewGuid()),
            new BrowserSessionId(Guid.NewGuid()),
            mode);
}
