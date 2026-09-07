using System.Security.Cryptography;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Updates;
using Xunit;

namespace OrbitNavigator.Updates.Tests;

public sealed class UpdateServiceTests
{
    [Fact]
    public async Task FirstRunDefaultsToNotifyOnly()
    {
        using var temp = new TempDirectory();
        var service = Service(temp, out _, out _, out _);

        await service.InitializeAsync();

        Assert.Equal(UpdatePreference.NotifyOnly, service.CurrentState.Preference);
        Assert.Equal(0, (int)service.CurrentState.Preference);
    }

    [Fact]
    public async Task UserCanExplicitlyEnableAutomaticUpdates()
    {
        using var temp = new TempDirectory();
        var store = new FileUpdatePreferenceStore(Path.Combine(temp.Path, "preference.txt"));
        var service = Service(temp, out _, out _, out _, store);

        var result = await service.SetPreferenceAsync(UpdatePreference.Automatic);

        Assert.True(result.IsSuccess);
        Assert.Equal(UpdatePreference.Automatic, await store.LoadAsync());
    }

    [Fact]
    public async Task CheckNotifiesWithoutImplicitDownloadByDefault()
    {
        using var temp = new TempDirectory();
        var service = Service(temp, out var stager, out _, out _);
        await service.InitializeAsync();

        var result = await service.CheckAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(UpdateLifecycleState.Available, result.Value?.Lifecycle);
        Assert.Equal(0, stager.Calls);
    }

    [Fact]
    public async Task StagedPackageMustPassSizeAndSha256BeforeReady()
    {
        using var temp = new TempDirectory();
        var service = Service(temp, out var stager, out _, out var package);

        var result = await service.DownloadAsync(package);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, stager.Calls);
        Assert.Equal(UpdateLifecycleState.ReadyToInstall, result.Value?.Lifecycle);
    }

    [Fact]
    public async Task TamperedPackageFailsClosedBeforeInstallerLaunch()
    {
        using var temp = new TempDirectory();
        var service = Service(temp, out var stager, out var installer, out var package);
        stager.Tamper = true;

        var result = await service.DownloadAsync(package);

        Assert.False(result.IsSuccess);
        Assert.Equal(UpdateLifecycleState.Failed, service.CurrentState.Lifecycle);
        Assert.Equal(ControllerErrorCode.IntegrityFailure, result.Error?.Code);
        Assert.Equal(0, installer.Calls);
    }

    private static UpdateService Service(
        TempDirectory temp,
        out FakeStager stager,
        out FakeInstaller installer,
        out UpdatePackageInfo package,
        IUpdatePreferenceStore? preferenceStore = null)
    {
        byte[] payload = [1, 3, 3, 7];
        package = new UpdatePackageInfo(
            new Version(1, 2, 3),
            new Uri("https://updates.example.test/orbit-1.2.3.exe"),
            Convert.ToHexString(SHA256.HashData(payload)),
            payload.Length,
            true);
        stager = new FakeStager(temp.Path, payload);
        installer = new FakeInstaller();
        return new UpdateService(
            new FakeManifest(package),
            stager,
            installer,
            preferenceStore ?? new FileUpdatePreferenceStore(Path.Combine(temp.Path, "preference.txt")));
    }

    private sealed class FakeManifest(UpdatePackageInfo package) : IUpdateManifestSource
    {
        public ValueTask<ControllerResult<UpdatePackageInfo>> CheckAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ControllerResult<UpdatePackageInfo>.Success(package));
    }

    private sealed class FakeStager(string directory, byte[] payload) : IUpdatePackageStager
    {
        public int Calls { get; private set; }

        public bool Tamper { get; set; }

        public async ValueTask<ControllerResult<StagedUpdatePackage>> StageAsync(
            UpdatePackageInfo package,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            var path = Path.Combine(directory, "update.exe");
            var bytes = Tamper ? payload.Concat(new byte[] { 9 }).ToArray() : payload;
            await File.WriteAllBytesAsync(path, bytes, cancellationToken);
            progress?.Report(100);
            return ControllerResult<StagedUpdatePackage>.Success(new StagedUpdatePackage(package, path));
        }
    }

    private sealed class FakeInstaller : IUpdateInstallerLauncher
    {
        public int Calls { get; private set; }

        public ValueTask<ControllerResult> LaunchAsync(
            StagedUpdatePackage package,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(ControllerResult.Success());
        }
    }
}

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "OrbitNavigator.UpdateTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        var full = System.IO.Path.GetFullPath(Path);
        var allowed = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "OrbitNavigator.UpdateTests")) + System.IO.Path.DirectorySeparatorChar;
        if (full.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full))
        {
            Directory.Delete(full, recursive: true);
        }
    }
}
