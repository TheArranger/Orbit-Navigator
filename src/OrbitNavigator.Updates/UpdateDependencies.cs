using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Updates;

namespace OrbitNavigator.Updates;

public interface IUpdateManifestSource
{
    ValueTask<ControllerResult<UpdatePackageInfo>> CheckAsync(
        CancellationToken cancellationToken = default);
}

public sealed record StagedUpdatePackage(
    UpdatePackageInfo Package,
    string AbsolutePath);

public interface IUpdatePackageStager
{
    ValueTask<ControllerResult<StagedUpdatePackage>> StageAsync(
        UpdatePackageInfo package,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IUpdateInstallerLauncher
{
    ValueTask<ControllerResult> LaunchAsync(
        StagedUpdatePackage package,
        CancellationToken cancellationToken = default);
}

public interface IUpdatePreferenceStore
{
    ValueTask<UpdatePreference> LoadAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(
        UpdatePreference preference,
        CancellationToken cancellationToken = default);
}
