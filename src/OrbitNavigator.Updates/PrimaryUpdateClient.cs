using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Updates;

namespace OrbitNavigator.Updates;

public enum PrimaryUpdateLifecycle
{
    Idle = 0,
    Checking = 1,
    UpToDate = 2,
    Available = 3,
    Downloading = 4,
    ReadyToInstall = 5,
    LaunchingInstaller = 6,
    Failed = 7,
}

public sealed record PrimaryUpdateSnapshot(
    PrimaryUpdateLifecycle Lifecycle,
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

public sealed class PrimaryUpdateClient : IAsyncDisposable
{
    public static readonly Uri ManifestUri =
        new("https://orbit-nav-updater.snap-it.cc/primary/manifest.json");
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
    private byte[]? _cachedManifest;
    private string? _cachedEntityTag;
    private bool _disposed;

    public PrimaryUpdateClient(
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
            PrimaryUpdateLifecycle.Idle,
            currentVersion,
            null,
            null,
            null,
            "Orbit checks for signed updates automatically.",
            null,
            null);
    }

    public PrimaryUpdateSnapshot Snapshot { get; private set; }

    public event EventHandler<PrimaryUpdateSnapshot>? SnapshotChanged;

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _clientState = await _stateStore.LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
            if (_clientState.Channel != UpdateReleaseChannel.Primary ||
                _clientState.BetaChannelOptIn)
            {
                _clientState = _clientState.SelectChannel(
                    UpdateReleaseChannel.Primary,
                    explicitUserOptIn: false);
            }
            if (_clientState.NextCheckNotBeforeUtc is null)
            {
                _clientState = _clientState with
                {
                    NextCheckNotBeforeUtc = _timeProvider.GetUtcNow() +
                        UpdateCheckSchedule.GetInitialDelay(_clientState),
                };
            }
            await _stateStore.SaveAsync(_clientState, cancellationToken).ConfigureAwait(false);
            Publish(Snapshot with
            {
                Lifecycle = PrimaryUpdateLifecycle.Idle,
                StatusMessage = "Orbit checks for signed updates automatically. Unsigned installers always require confirmation.",
                NextCheckNotBeforeUtc = _clientState.NextCheckNotBeforeUtc,
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask<ControllerResult<PrimaryUpdateSnapshot>> CheckAsync(
        CancellationToken cancellationToken = default) =>
        CheckCoreAsync(ignoreSchedule: true, cancellationToken);

    public async Task RunScheduledChecksAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var state = _clientState;
                if (state?.NextCheckNotBeforeUtc is { } due &&
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

    public async ValueTask<ControllerResult<PrimaryUpdateSnapshot>> DownloadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var state = await EnsureStateAsync(cancellationToken).ConfigureAwait(false);
            if (Snapshot.Lifecycle != PrimaryUpdateLifecycle.Available ||
                Snapshot.AvailableManifest is not { } manifest)
                return Failure(ControllerErrorCode.PolicyDenied, "error.update.not_available");

            var verified = VerifyManifest(_cachedManifest ?? [], state);
            if (!verified.IsSuccess)
                return await RecordFailureAsync(
                    verified.Error!.Code, verified.Error.MessageKey, cancellationToken).ConfigureAwait(false);

            Publish(Snapshot with
            {
                Lifecycle = PrimaryUpdateLifecycle.Downloading,
                StatusMessage = "Downloading the verified Orbit Navigator update...",
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
                    UpdateReleaseChannel.Primary,
                    manifest.ReleaseSequence) with
                {
                    PrimaryAcceptedManifestSha256 = Convert.ToHexString(SHA256.HashData(_cachedManifest!)),
                };
                await _stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
                _clientState = state;
                Publish(Snapshot with
                {
                    Lifecycle = PrimaryUpdateLifecycle.ReadyToInstall,
                    StagedPackage = staged,
                    PublisherTrust = publisherTrust,
                    StatusMessage = publisherTrust == UpdatePublisherTrust.Unsigned
                        ? "Update downloaded and verified. Its Windows publisher is unknown; installation requires a separate confirmation."
                        : "Update downloaded and verified. Installation requires your confirmation.",
                });
                return ControllerResult<PrimaryUpdateSnapshot>.Success(Snapshot);
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
            if (Snapshot.Lifecycle != PrimaryUpdateLifecycle.ReadyToInstall ||
                Snapshot.StagedPackage is not { } staged ||
                Snapshot.PublisherTrust is not { } priorTrust)
                return ControllerResult.Failure(ControllerError.Create(
                    ControllerErrorCode.Conflict,
                    "error.update.package_not_staged"));
            var state = await EnsureStateAsync(cancellationToken).ConfigureAwait(false);
            var verified = VerifyManifest(_cachedManifest ?? [], state);
            if (!verified.IsSuccess)
                return ControllerResult.Failure(verified.Error!);
            if (verified.Value!.Package != staged.Package)
                return ControllerResult.Failure(ControllerError.Create(
                    ControllerErrorCode.IntegrityFailure, "error.update.package_invalid"));
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
                UpdateReleaseChannel.Primary,
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
                Lifecycle = PrimaryUpdateLifecycle.LaunchingInstaller,
                StatusMessage = "Opening the visible Orbit Navigator installer...",
            });
            var launched = await _installer.LaunchVisibleAsync(staged, cancellationToken)
                .ConfigureAwait(false);
            if (!launched.IsSuccess)
            {
                Publish(Snapshot with
                {
                    Lifecycle = PrimaryUpdateLifecycle.ReadyToInstall,
                    StatusMessage = "The Orbit Navigator installer could not be opened.",
                });
            }
            return launched;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<ControllerResult<PrimaryUpdateSnapshot>> CheckCoreAsync(
        bool ignoreSchedule,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var state = await EnsureStateAsync(cancellationToken).ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow();
            if (!ignoreSchedule && state.NextCheckNotBeforeUtc is { } due && due > now)
                return ControllerResult<PrimaryUpdateSnapshot>.Success(Snapshot);

            Publish(Snapshot with
            {
                Lifecycle = PrimaryUpdateLifecycle.Checking,
                StatusMessage = "Checking the signed Orbit Navigator update feed...",
            });
            byte[]? manifestBytes = null;
            string? entityTag = null;
            // A persisted validator alone is not a cache. After restart fetch the
            // body unconditionally; within a session 304 revalidates that body.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, ManifestUri);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
                var conditionalTag = attempt == 0 && _cachedManifest is not null
                    ? _cachedEntityTag
                    : null;
                UpdateConditionalRequest.ApplyEntityTag(request, conditionalTag);
                using var response = await _httpClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    if (conditionalTag is not null && _cachedManifest is not null)
                    {
                        manifestBytes = _cachedManifest;
                        entityTag = _cachedEntityTag;
                        break;
                    }
                    // A bodyless response without a usable cache gets one full retry.
                    continue;
                }
                if (response.StatusCode != HttpStatusCode.OK ||
                    response.Content.Headers.ContentLength is < 1 or > UpdateFeedTrustPolicy.DefaultMaximumManifestBytes)
                    return await RecordFailureAsync(
                        ControllerErrorCode.Unavailable, "error.update.feed_unavailable", cancellationToken,
                        preserveVerifiedReady: true).ConfigureAwait(false);

                await using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                var block = new byte[8192];
                while (true)
                {
                    var read = await content.ReadAsync(block, cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    if (buffer.Length + read > UpdateFeedTrustPolicy.DefaultMaximumManifestBytes)
                        return await RecordFailureAsync(
                            ControllerErrorCode.IntegrityFailure, "error.update.manifest_size_invalid", cancellationToken).ConfigureAwait(false);
                    buffer.Write(block, 0, read);
                }
                manifestBytes = buffer.ToArray();
                entityTag = UpdateConditionalRequest.ReadEntityTag(response);
                break;
            }
            if (manifestBytes is null)
                return await RecordFailureAsync(
                    ControllerErrorCode.Unavailable, "error.update.feed_unavailable", cancellationToken,
                    preserveVerifiedReady: true).ConfigureAwait(false);
            var verified = VerifyManifest(manifestBytes, state);
            if (!verified.IsSuccess)
                return await RecordFailureAsync(
                    verified.Error!.Code,
                    verified.Error.MessageKey,
                    cancellationToken).ConfigureAwait(false);
            var rollout = UpdateRolloutGate.Evaluate(verified.Value!, state, now);
            var manifest = verified.Value!;
            var installed = SignedUpdateManifestVerifier.IsSameVersion(manifest.Package.Version, _currentVersion);
            StagedUpdatePackage? staged = null;
            UpdatePublisherTrust? publisherTrust = null;
            if (!installed && rollout.IsEligible)
            {
                var stagedPath = Path.Combine(_stagingRoot, Path.GetFileName(manifest.Package.DownloadUri.AbsolutePath));
                if (File.Exists(stagedPath))
                {
                    staged = new StagedUpdatePackage(manifest.Package, stagedPath);
                    var integrity = await UpdatePackageIntegrity.VerifyAsync(staged, cancellationToken).ConfigureAwait(false);
                    if (!integrity.IsSuccess)
                    {
                        // Never reuse damaged/stale bytes. The verified offer remains
                        // downloadable and DownloadAsync replaces this candidate.
                        staged = null;
                    }
                    else
                    {
                        publisherTrust = await _publisherTrust.InspectAsync(stagedPath, cancellationToken).ConfigureAwait(false);
                        if (publisherTrust is UpdatePublisherTrust.InvalidSignature or
                            UpdatePublisherTrust.UnexpectedPublisher or UpdatePublisherTrust.VerificationUnavailable ||
                            Snapshot.StagedPackage?.Package == staged.Package &&
                            Snapshot.PublisherTrust is { } priorTrust && priorTrust != publisherTrust)
                            return await RecordFailureAsync(
                                ControllerErrorCode.IntegrityFailure, "error.update.publisher_invalid", cancellationToken).ConfigureAwait(false);
                        state = state.WithAcceptedReleaseSequence(UpdateReleaseChannel.Primary, manifest.ReleaseSequence) with
                        {
                            PrimaryAcceptedManifestSha256 = Convert.ToHexString(SHA256.HashData(manifestBytes)),
                        };
                    }
                }
            }
            state = state.WithEntityTag(
                UpdateReleaseChannel.Primary,
                null);
            state = SuccessfulCheckState(state, now);
            if (!installed && rollout.ReevaluateAtUtc is { } reevaluate &&
                reevaluate < state.NextCheckNotBeforeUtc)
                state = state with { NextCheckNotBeforeUtc = reevaluate };
            await _stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            _clientState = state;
            _cachedManifest = manifestBytes;
            _cachedEntityTag = entityTag;
            Publish(Snapshot with
            {
                Lifecycle = installed || !rollout.IsEligible
                    ? PrimaryUpdateLifecycle.UpToDate
                    : staged is not null ? PrimaryUpdateLifecycle.ReadyToInstall : PrimaryUpdateLifecycle.Available,
                AvailableManifest = !installed && rollout.IsEligible ? manifest : null,
                StagedPackage = staged,
                PublisherTrust = publisherTrust,
                StatusMessage = installed ? "Orbit Navigator is up to date."
                    : !rollout.IsEligible ? "This update has not reached this installation's privacy-preserving rollout bucket yet."
                    : staged is not null ? publisherTrust == UpdatePublisherTrust.Unsigned
                        ? "Update downloaded and verified. Its Windows publisher is unknown; installation requires a separate confirmation."
                        : "Update downloaded and verified. Installation requires your confirmation."
                    : $"Orbit Navigator {manifest.Package.Version} is available.",
                LastCheckedAtUtc = now,
                NextCheckNotBeforeUtc = state.NextCheckNotBeforeUtc,
            });
            return ControllerResult<PrimaryUpdateSnapshot>.Success(Snapshot);
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
                cancellationToken,
                preserveVerifiedReady: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // HttpClient timeouts cancel its internal token, not the caller's.
            return await RecordFailureAsync(
                ControllerErrorCode.Unavailable,
                "error.update.feed_unavailable",
                cancellationToken,
                preserveVerifiedReady: true).ConfigureAwait(false);
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

    private ControllerResult<VerifiedUpdateManifest> VerifyManifest(byte[] manifest, UpdateClientState state) =>
        new SignedUpdateManifestVerifier(new UpdateFeedTrustPolicy(
            ManifestUri, PackageOrigin, "primary", _currentVersion,
            state.GetAcceptedReleaseSequence(UpdateReleaseChannel.Primary), [_trustedKey]), _timeProvider)
            .VerifyCurrentFeed(manifest, state.PrimaryAcceptedManifestSha256);

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
                Math.Max(1, state.PrimaryAcceptedReleaseSequence + 1)),
        };

    private async ValueTask<ControllerResult<PrimaryUpdateSnapshot>> RecordFailureAsync(
        ControllerErrorCode code,
        string messageKey,
        CancellationToken cancellationToken,
        bool preserveVerifiedReady = false)
    {
        var state = await EnsureStateAsync(cancellationToken).ConfigureAwait(false);
        var keepReady = false;
        if (preserveVerifiedReady && _cachedManifest is not null &&
            Snapshot.StagedPackage is { } staged && Snapshot.PublisherTrust is { } priorTrust)
        {
            var verified = VerifyManifest(_cachedManifest, state);
            if (verified.IsSuccess && verified.Value!.Package == staged.Package &&
                (await UpdatePackageIntegrity.VerifyAsync(staged, cancellationToken).ConfigureAwait(false)).IsSuccess)
            {
                var trust = await _publisherTrust.InspectAsync(staged.AbsolutePath, cancellationToken).ConfigureAwait(false);
                keepReady = trust == priorTrust && trust is UpdatePublisherTrust.Unsigned or UpdatePublisherTrust.TrustedPublisher;
            }
        }
        if (!keepReady)
        {
            _cachedManifest = null;
            _cachedEntityTag = null;
        }
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
            Lifecycle = keepReady ? PrimaryUpdateLifecycle.ReadyToInstall : PrimaryUpdateLifecycle.Failed,
            AvailableManifest = keepReady ? Snapshot.AvailableManifest : null,
            StagedPackage = keepReady ? Snapshot.StagedPackage : null,
            PublisherTrust = keepReady ? Snapshot.PublisherTrust : null,
            StatusMessage = keepReady
                ? "The update feed is unavailable. The downloaded update remains verified and requires your confirmation."
                : "The update service is unavailable or returned data that could not be verified. Local browsing is unaffected.",
            LastCheckedAtUtc = _timeProvider.GetUtcNow(),
            NextCheckNotBeforeUtc = state.NextCheckNotBeforeUtc,
        });
        return Failure(code, messageKey);
    }

    private ControllerResult<PrimaryUpdateSnapshot> Failure(
        ControllerErrorCode code,
        string messageKey) =>
        ControllerResult<PrimaryUpdateSnapshot>.Failure(ControllerError.Create(code, messageKey));

    private void Publish(PrimaryUpdateSnapshot snapshot)
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
