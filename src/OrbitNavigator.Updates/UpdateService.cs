using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Updates;

namespace OrbitNavigator.Updates;

public sealed class UpdateService : IUpdateService
{
    private readonly IUpdateManifestSource _manifestSource;
    private readonly IUpdatePackageStager _stager;
    private readonly IUpdateInstallerLauncher _installer;
    private readonly IUpdatePreferenceStore _preferences;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private StagedUpdatePackage? _staged;

    public UpdateService(
        IUpdateManifestSource manifestSource,
        IUpdatePackageStager stager,
        IUpdateInstallerLauncher installer,
        IUpdatePreferenceStore preferences)
    {
        _manifestSource = manifestSource ?? throw new ArgumentNullException(nameof(manifestSource));
        _stager = stager ?? throw new ArgumentNullException(nameof(stager));
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        CurrentState = new UpdateState(
            UpdateLifecycleState.Idle,
            null,
            UpdatePreference.NotifyOnly,
            null,
            null);
    }

    public UpdateState CurrentState { get; private set; }

    public event EventHandler<UpdateStateChangedEventArgs>? StateChanged;

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        var preference = await _preferences.LoadAsync(cancellationToken).ConfigureAwait(false);
        Publish(CurrentState with { Preference = preference });
    }

    public async ValueTask<ControllerResult<UpdateState>> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        if (!await _operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return Busy();
        }

        try
        {
            Publish(CurrentState with
            {
                Lifecycle = UpdateLifecycleState.Checking,
                Package = null,
                DownloadPercent = null,
                Error = null,
            });
            var result = await _manifestSource.CheckAsync(cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                return Fail(result.Error!);
            }

            Publish(CurrentState with
            {
                Lifecycle = UpdateLifecycleState.Available,
                Package = result.Value,
            });
            return ControllerResult<UpdateState>.Success(CurrentState);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask<ControllerResult<UpdateState>> DownloadAsync(
        UpdatePackageInfo package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!await _operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return Busy();
        }

        try
        {
            Publish(CurrentState with
            {
                Lifecycle = UpdateLifecycleState.Downloading,
                Package = package,
                DownloadPercent = 0,
                Error = null,
            });
            var progress = new Progress<int>(value => Publish(CurrentState with
            {
                DownloadPercent = Math.Clamp(value, 0, 100),
            }));
            var staged = await _stager.StageAsync(package, progress, cancellationToken).ConfigureAwait(false);
            if (!staged.IsSuccess)
            {
                return Fail(staged.Error!);
            }

            var integrity = await UpdatePackageIntegrity.VerifyAsync(
                staged.Value!,
                cancellationToken).ConfigureAwait(false);
            if (!integrity.IsSuccess)
            {
                return Fail(integrity.Error!);
            }

            _staged = staged.Value;
            Publish(CurrentState with
            {
                Lifecycle = UpdateLifecycleState.ReadyToInstall,
                DownloadPercent = 100,
            });
            return ControllerResult<UpdateState>.Success(CurrentState);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask<ControllerResult> ApproveAndInstallAsync(
        UpdatePackageInfo package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (_staged is null || _staged.Package != package)
        {
            return ControllerResult.Failure(ControllerError.Create(
                ControllerErrorCode.Conflict,
                "error.update.package_not_staged"));
        }

        var integrity = await UpdatePackageIntegrity.VerifyAsync(_staged, cancellationToken)
            .ConfigureAwait(false);
        if (!integrity.IsSuccess)
        {
            return integrity;
        }

        Publish(CurrentState with { Lifecycle = UpdateLifecycleState.Installing });
        var result = await _installer.LaunchAsync(_staged, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            Fail(result.Error!);
        }

        return result;
    }

    public async ValueTask<ControllerResult<UpdateState>> SetPreferenceAsync(
        UpdatePreference preference,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(preference))
        {
            return ControllerResult<UpdateState>.Failure(ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "error.update.preference_invalid"));
        }

        await _preferences.SaveAsync(preference, cancellationToken).ConfigureAwait(false);
        Publish(CurrentState with { Preference = preference });
        return ControllerResult<UpdateState>.Success(CurrentState);
    }

    private ControllerResult<UpdateState> Busy() =>
        ControllerResult<UpdateState>.Failure(ControllerError.Create(
            ControllerErrorCode.Conflict,
            "error.update.operation_in_progress"));

    private ControllerResult<UpdateState> Fail(ControllerError error)
    {
        Publish(CurrentState with
        {
            Lifecycle = UpdateLifecycleState.Failed,
            Error = error,
        });
        return ControllerResult<UpdateState>.Failure(error);
    }

    private void Publish(UpdateState state)
    {
        CurrentState = state;
        StateChanged?.Invoke(this, new UpdateStateChangedEventArgs(state));
    }
}
