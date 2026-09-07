using System.Net;
using System.Net.Http.Headers;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Updates;

namespace OrbitNavigator.Updates;

public enum BetaUpdateLifecycle
{
    Disabled = 0,
    Idle = 1,
    Checking = 2,
    UpToDate = 3,
    Available = 4,
    Downloading = 5,
    ReadyToInstall = 6,
    LaunchingInstaller = 7,
    Failed = 8,
}

public sealed record BetaUpdateSnapshot(
    BetaUpdateLifecycle Lifecycle,
    bool IsBetaOptedIn,
    Version CurrentVersion,
    VerifiedUpdateManifest? AvailableManifest,
    StagedUpdatePackage? StagedPackage,
    UpdatePublisherTrust? PublisherTrust,
    string StatusMessage,
    DateTimeOffset? LastCheckedAtUtc,
    DateTimeOffset? NextCheckNotBeforeUtc);

public interface IUpdatePublisherTrustInspector
{
    ValueTask<UpdatePublisherTrust> InspectAsync(
        string absolutePackagePath,
        CancellationToken cancellationToken = default);
}

public interface IVisibleUpdateInstallerLauncher
{
    ValueTask<ControllerResult> LaunchVisibleAsync(
        StagedUpdatePackage package,
        CancellationToken cancellationToken = default);
}

public sealed class BetaUpdateClient : IAsyncDisposable
{
    public static readonly Uri ManifestUri =
        new("https://orbit-nav-updater.snap-it.cc/beta/manifest.json");
    public static readonly Uri PackageOrigin =
        new("https://orbit-nav-updater.snap-it.cc/");

    private readonly HttpClient _httpClient;
    private readonly FileUpdateClientStateStore _stateStore;
    private readonly UpdateManifestPublicKey _trustedKey;
    private readonly IUpdatePublisherTrustInspector _publisherTrust;
    private readonly IVisibleUpdateInstallerLauncher _installer;
    private readonly string _stagingRoot;
    private readonly Version _currentVersion;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UpdateClientState? _clientState;
    private bool _disposed;

    public BetaUpdateClient(
        HttpClient httpClient,
        FileUpdateClientStateStore stateStore,
        UpdateManifestPublicKey trustedKey,
        IUpdatePublisherTrustInspector publisherTrust,
        IVisibleUpdateInstallerLauncher installer,
        string stagingRoot,
        Version currentVersion,
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _trustedKey = trustedKey ?? throw new ArgumentNullException(nameof(trustedKey));
        _publisherTrust = publisherTrust ?? throw new ArgumentNullException(nameof(publisherTrust));
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        if (!Path.IsPathFullyQualified(stagingRoot))
            throw new ArgumentException("The update staging root must be absolute.", nameof(stagingRoot));
        _stagingRoot = Path.GetFullPath(stagingRoot).TrimEnd(Path.DirectorySeparatorChar);
        _currentVersion = currentVersion ?? throw new ArgumentNullException(nameof(currentVersion));
        _timeProvider = timeProvider ?? TimeProvider.System;
        Snapshot = new(
            BetaUpdateLifecycle.Disabled,
            false,
            currentVersion,
            null,
            null,
            null,
            "Beta updates are off.",
            null,
            null);
    }

    public BetaUpdateSnapshot Snapshot { get; private set; }

    public event EventHandler<BetaUpdateSnapshot>? SnapshotChanged;

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _clientState = await _stateStore.LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
            Publish(Snapshot with
            {
                Lifecycle = _clientState.BetaChannelOptIn
                    ? BetaUpdateLifecycle.Idle
                    : BetaUpdateLifecycle.Disabled,
                IsBetaOptedIn = _clientState.BetaChannelOptIn,
                StatusMessage = _clientState.BetaChannelOptIn
                    ? "Beta updates are enabled. Unsigned packages always require confirmation."
                    : "Beta updates are off.",
                NextCheckNotBeforeUtc = _clientState.NextCheckNotBeforeUtc,
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ControllerResult<BetaUpdateSnapshot>> SetBetaOptInAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var state = await EnsureStateAsync(cancellationToken).ConfigureAwait(false);
            state = state.SelectChannel(
                enabled ? UpdateReleaseChannel.Beta : UpdateReleaseChannel.Primary,
                explicitUserOptIn: enabled);
            if (enabled && state.NextCheckNotBeforeUtc is null)
                state = state with
                {
                    NextCheckNotBeforeUtc = _timeProvider.GetUtcNow() +
                        UpdateCheckSchedule.GetInitialDelay(state),
                };
            await _stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            _clientState = state;
            Publish(new(
                enabled ? BetaUpdateLifecycle.Idle : BetaUpdateLifecycle.Disabled,
                enabled,
                _currentVersion,
                null,
                null,
                null,
                enabled
                    ? "Beta updates are enabled. Unsigned packages always require confirmation."
                    : "Beta updates are off. Primary automatic updates remain disabled.",
                Snapshot.LastCheckedAtUtc,
                state.NextCheckNotBeforeUtc));
            return ControllerResult<BetaUpdateSnapshot>.Success(Snapshot);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask<ControllerResult<BetaUpdateSnapshot>> CheckAsync(
        CancellationToken cancellationToken = default) =>
        CheckCoreAsync(ignoreSchedule: true, cancellationToken);

    public async Task RunScheduledChecksAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var state = _clientState;
                if (state is { BetaChannelOptIn: true } &&
                    state.NextCheckNotBeforeUtc is { } due &&
                    due <= _timeProvider.GetUtcNow())
                {
                    await CheckCoreAsync(ignoreSchedule: false, cancellationToken).ConfigureAwait(false);
                }
                await Task.Delay(TimeSpan.FromMinutes(1), _timeProvider, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // Check failures are represented in local state. The scheduler never
                // escalates an offline feed into an application failure.
            }
        }
    }

    public async ValueTask<ControllerResult<BetaUpdateSnapshot>> DownloadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var state = await EnsureStateAsync(cancellationToken).ConfigureAwait(false);
            if (!state.BetaChannelOptIn || Snapshot.AvailableManifest is not { } manifest)
                return Failure(ControllerErrorCode.PolicyDenied, "error.update.beta_not_available");

            Publish(Snapshot with
            {
                Lifecycle = BetaUpdateLifecycle.Downloading,
                StatusMessage = "Downloading the verified Beta package...",
            });
            Directory.CreateDirectory(_stagingRoot);
            var finalName = Path.GetFileName(manifest.Package.DownloadUri.AbsolutePath);
            var finalPath = Path.Combine(_stagingRoot, finalName);
            var temporaryPath = Path.Combine(_stagingRoot, $".{Guid.NewGuid():N}.download");
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, manifest.Package.DownloadUri);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
                    "application/vnd.microsoft.portable-executable"));
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.OK ||
                    response.Content.Headers.ContentLength is { } declaredLength &&
                    declaredLength != manifest.Package.SizeBytes)
                {
                    return await RecordFailureAsync(
                        ControllerErrorCode.Unavailable,
                        "error.update.package_unavailable",
                        cancellationToken).ConfigureAwait(false);
                }

                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                await using (var output = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    var buffer = new byte[64 * 1024];
                    long total = 0;
                    while (true)
                    {
                        var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                        if (read == 0) break;
                        total = checked(total + read);
                        if (total > manifest.Package.SizeBytes)
                            return await RecordFailureAsync(
                                ControllerErrorCode.IntegrityFailure,
                                "error.update.size_mismatch",
                                cancellationToken).ConfigureAwait(false);
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                            .ConfigureAwait(false);
                    }
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                File.Move(temporaryPath, finalPath, overwrite: true);
                var staged = new StagedUpdatePackage(manifest.Package, finalPath);
                var integrity = await UpdatePackageIntegrity.VerifyAsync(staged, cancellationToken)
                    .ConfigureAwait(false);
                if (!integrity.IsSuccess)
                    return await RecordFailureAsync(
                        integrity.Error!.Code,
                        integrity.Error.MessageKey,
                        cancellationToken).ConfigureAwait(false);
                var publisherTrust = await _publisherTrust.InspectAsync(finalPath, cancellationToken)
                    .ConfigureAwait(false);
                if (publisherTrust is UpdatePublisherTrust.InvalidSignature or
                    UpdatePublisherTrust.UnexpectedPublisher or
                    UpdatePublisherTrust.VerificationUnavailable)
                    return await RecordFailureAsync(
                        ControllerErrorCode.IntegrityFailure,
                        "error.update.publisher_invalid",
                        cancellationToken).ConfigureAwait(false);

                state = state.WithAcceptedReleaseSequence(
                    UpdateReleaseChannel.Beta,
                    manifest.ReleaseSequence);
                await _stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
                _clientState = state;
                Publish(Snapshot with
                {
                    Lifecycle = BetaUpdateLifecycle.ReadyToInstall,
                    StagedPackage = staged,
                    PublisherTrust = publisherTrust,
                    StatusMessage = publisherTrust == UpdatePublisherTrust.Unsigned
                        ? "Beta downloaded and verified. Its Windows publisher is unknown; installation requires a separate confirmation."
                        : "Beta downloaded and verified. Installation requires your confirmation.",
                });
                return ControllerResult<BetaUpdateSnapshot>.Success(Snapshot);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(ControllerErrorCode.Cancelled, "error.update.cancelled");
        }
        catch (HttpRequestException)
        {
            return await RecordFailureAsync(
                ControllerErrorCode.Unavailable,
                "error.update.package_unavailable",
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ControllerResult> ApproveAndLaunchAsync(
        bool deliberatePerPackageConfirmation,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!Snapshot.IsBetaOptedIn ||
                Snapshot.StagedPackage is not { } staged ||
                Snapshot.PublisherTrust is not { } priorTrust)
                return ControllerResult.Failure(ControllerError.Create(
                    ControllerErrorCode.Conflict,
                    "error.update.package_not_staged"));
            var integrity = await UpdatePackageIntegrity.VerifyAsync(staged, cancellationToken)
                .ConfigureAwait(false);
            if (!integrity.IsSuccess) return integrity;
            var currentTrust = await _publisherTrust.InspectAsync(staged.AbsolutePath, cancellationToken)
                .ConfigureAwait(false);
            if (currentTrust != priorTrust)
                return ControllerResult.Failure(ControllerError.Create(
                    ControllerErrorCode.IntegrityFailure,
                    "error.update.publisher_changed"));
            var decision = UpdateApplyPolicy.Evaluate(
                UpdateReleaseChannel.Beta,
                currentTrust,
                UpdatePreference.NotifyOnly,
                deliberatePerPackageConfirmation);
            if (decision.Disposition is UpdateApplyDisposition.Blocked or
                UpdateApplyDisposition.RequiresDeliberateConfirmation)
                return ControllerResult.Failure(ControllerError.Create(
                    ControllerErrorCode.PolicyDenied,
                    decision.MessageKey));

            Publish(Snapshot with
            {
                Lifecycle = BetaUpdateLifecycle.LaunchingInstaller,
                StatusMessage = "Opening the visible Beta installer...",
            });
            var launched = await _installer.LaunchVisibleAsync(staged, cancellationToken)
                .ConfigureAwait(false);
            if (!launched.IsSuccess)
            {
                Publish(Snapshot with
                {
                    Lifecycle = BetaUpdateLifecycle.ReadyToInstall,
                    StatusMessage = "The Beta installer could not be opened.",
                });
            }
            return launched;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<ControllerResult<BetaUpdateSnapshot>> CheckCoreAsync(
        bool ignoreSchedule,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var state = await EnsureStateAsync(cancellationToken).ConfigureAwait(false);
            if (!state.BetaChannelOptIn)
                return Failure(ControllerErrorCode.PolicyDenied, "error.update.beta_not_enabled");
            var now = _timeProvider.GetUtcNow();
            if (!ignoreSchedule && state.NextCheckNotBeforeUtc is { } due && due > now)
                return ControllerResult<BetaUpdateSnapshot>.Success(Snapshot);

            Publish(Snapshot with
            {
                Lifecycle = BetaUpdateLifecycle.Checking,
                StatusMessage = "Checking the signed Beta update feed...",
            });
            var policy = new UpdateFeedTrustPolicy(
                ManifestUri,
                PackageOrigin,
                "beta",
                _currentVersion,
                state.GetAcceptedReleaseSequence(UpdateReleaseChannel.Beta),
                [_trustedKey]);
            var verifier = new SignedUpdateManifestVerifier(policy, _timeProvider);
            using var request = new HttpRequestMessage(HttpMethod.Get, ManifestUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            UpdateConditionalRequest.ApplyEntityTag(
                request,
                state.GetEntityTag(UpdateReleaseChannel.Beta));
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                state = SuccessfulCheckState(state, now);
                await _stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
                _clientState = state;
                Publish(Snapshot with
                {
                    Lifecycle = BetaUpdateLifecycle.UpToDate,
                    StatusMessage = "No newer eligible Beta update is available.",
                    LastCheckedAtUtc = now,
                    NextCheckNotBeforeUtc = state.NextCheckNotBeforeUtc,
                });
                return ControllerResult<BetaUpdateSnapshot>.Success(Snapshot);
            }
            if (response.StatusCode != HttpStatusCode.OK ||
                response.Content.Headers.ContentLength is < 1 or > UpdateFeedTrustPolicy.DefaultMaximumManifestBytes)
                return await RecordFailureAsync(
                    ControllerErrorCode.Unavailable,
                    "error.update.feed_unavailable",
                    cancellationToken).ConfigureAwait(false);

            await using var content = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var block = new byte[8192];
            while (true)
            {
                var read = await content.ReadAsync(block, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (buffer.Length + read > UpdateFeedTrustPolicy.DefaultMaximumManifestBytes)
                    return await RecordFailureAsync(
                        ControllerErrorCode.IntegrityFailure,
                        "error.update.manifest_size_invalid",
                        cancellationToken).ConfigureAwait(false);
                buffer.Write(block, 0, read);
            }
            var verified = verifier.Verify(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)));
            if (!verified.IsSuccess)
                return await RecordFailureAsync(
                    verified.Error!.Code,
                    verified.Error.MessageKey,
                    cancellationToken).ConfigureAwait(false);
            var rollout = UpdateRolloutGate.Evaluate(verified.Value!, state, now);
            state = state.WithEntityTag(
                UpdateReleaseChannel.Beta,
                UpdateConditionalRequest.ReadEntityTag(response));
            state = SuccessfulCheckState(state, now);
            await _stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            _clientState = state;
            Publish(Snapshot with
            {
                Lifecycle = rollout.IsEligible
                    ? BetaUpdateLifecycle.Available
                    : BetaUpdateLifecycle.UpToDate,
                AvailableManifest = rollout.IsEligible ? verified.Value : null,
                StagedPackage = null,
                PublisherTrust = null,
                StatusMessage = rollout.IsEligible
                    ? $"Orbit Navigator Beta {verified.Value!.Package.Version} is available."
                    : "This Beta release has not reached this installation's privacy-preserving rollout bucket yet.",
                LastCheckedAtUtc = now,
                NextCheckNotBeforeUtc = rollout.ReevaluateAtUtc ?? state.NextCheckNotBeforeUtc,
            });
            return ControllerResult<BetaUpdateSnapshot>.Success(Snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(ControllerErrorCode.Cancelled, "error.update.cancelled");
        }
        catch (HttpRequestException)
        {
            return await RecordFailureAsync(
                ControllerErrorCode.Unavailable,
                "error.update.feed_unavailable",
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            return await RecordFailureAsync(
                ControllerErrorCode.IntegrityFailure,
                "error.update.local_state_invalid",
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<UpdateClientState> EnsureStateAsync(CancellationToken cancellationToken)
    {
        if (_clientState is not null) return _clientState;
        _clientState = await _stateStore.LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
        return _clientState;
    }

    private UpdateClientState SuccessfulCheckState(UpdateClientState state, DateTimeOffset now) =>
        state with
        {
            ConsecutiveFailures = 0,
            NextCheckNotBeforeUtc = now + UpdateCheckSchedule.GetRegularDelay(
                state,
                Math.Max(1, state.BetaAcceptedReleaseSequence + 1)),
        };

    private async ValueTask<ControllerResult<BetaUpdateSnapshot>> RecordFailureAsync(
        ControllerErrorCode code,
        string messageKey,
        CancellationToken cancellationToken)
    {
        var state = await EnsureStateAsync(cancellationToken).ConfigureAwait(false);
        var failures = checked(state.ConsecutiveFailures + 1);
        state = state with
        {
            ConsecutiveFailures = failures,
            NextCheckNotBeforeUtc = _timeProvider.GetUtcNow() +
                UpdateCheckSchedule.GetFailureDelay(state, failures),
        };
        await _stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        _clientState = state;
        Publish(Snapshot with
        {
            Lifecycle = BetaUpdateLifecycle.Failed,
            StatusMessage = "The Beta update service is unavailable or returned data that could not be verified. Local browsing is unaffected.",
            LastCheckedAtUtc = _timeProvider.GetUtcNow(),
            NextCheckNotBeforeUtc = state.NextCheckNotBeforeUtc,
        });
        return Failure(code, messageKey);
    }

    private ControllerResult<BetaUpdateSnapshot> Failure(
        ControllerErrorCode code,
        string messageKey) =>
        ControllerResult<BetaUpdateSnapshot>.Failure(ControllerError.Create(code, messageKey));

    private void Publish(BetaUpdateSnapshot snapshot)
    {
        Snapshot = snapshot;
        SnapshotChanged?.Invoke(this, snapshot);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _httpClient.Dispose();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
