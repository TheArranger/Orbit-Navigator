using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.Contracts.Updates;

public enum UpdatePreference
{
    NotifyOnly = 0,
    Automatic = 1,
}

public enum UpdateLifecycleState
{
    Idle = 0,
    Checking = 1,
    Available = 2,
    Downloading = 3,
    ReadyToInstall = 4,
    Installing = 5,
    Failed = 6,
}

public sealed record UpdatePackageInfo(
    Version Version,
    Uri DownloadUri,
    string Sha256,
    long SizeBytes,
    bool RequiresRestart);

public sealed record UpdateState(
    UpdateLifecycleState Lifecycle,
    UpdatePackageInfo? Package,
    UpdatePreference Preference,
    int? DownloadPercent,
    ControllerError? Error);

public sealed class UpdateStateChangedEventArgs : EventArgs
{
    public UpdateStateChangedEventArgs(UpdateState state)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
    }

    public UpdateState State { get; }
}

public interface IUpdateService
{
    UpdateState CurrentState { get; }

    event EventHandler<UpdateStateChangedEventArgs>? StateChanged;

    ValueTask<ControllerResult<UpdateState>> CheckAsync(
        CancellationToken cancellationToken = default);

    ValueTask<ControllerResult<UpdateState>> DownloadAsync(
        UpdatePackageInfo package,
        CancellationToken cancellationToken = default);

    ValueTask<ControllerResult> ApproveAndInstallAsync(
        UpdatePackageInfo package,
        CancellationToken cancellationToken = default);

    ValueTask<ControllerResult<UpdateState>> SetPreferenceAsync(
        UpdatePreference preference,
        CancellationToken cancellationToken = default);
}

public interface IUpdatePromptService
{
    ValueTask<ControllerResult> ShowAsync(
        UpdateState state,
        CancellationToken cancellationToken = default);
}

