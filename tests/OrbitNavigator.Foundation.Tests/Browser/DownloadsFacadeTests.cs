using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Sync;
using OrbitNavigator.Foundation.Browser;
using OrbitNavigator.Foundation.Profiles;
using Xunit;

namespace OrbitNavigator.Foundation.Tests.Browser;

public sealed class DownloadsFacadeTests
{
    [Fact]
    public async Task NormalDownloadIsDurableOpenableAndMetadataClearPreservesFile()
    {
        using var temp = new TempDirectory();
        var storage = new FileProfileStorage(temp.Path);
        var clock = new MutableClock(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        var launcher = new RecordingLauncher();
        var facade = new DownloadsFacade(storage, clock, launcher);
        var context = Privacy(BrowserProfileMode.Normal);
        var downloadedFile = Path.Combine(temp.Path, "Orbit-Guide.pdf");
        await File.WriteAllTextAsync(downloadedFile, "downloaded bytes");

        var registration = await facade.BeginAsync(new(
            context,
            new Uri("https://downloads.example.test/Orbit-Guide.pdf"),
            downloadedFile,
            16), new RecordingRuntime());
        Assert.True(registration.IsSuccess);

        clock.UtcNow = clock.UtcNow.AddSeconds(2);
        await facade.UpdateAsync(
            context,
            registration.Value!.DownloadId,
            16,
            16,
            DownloadLifecycleState.Completed);

        var reopened = new DownloadsFacade(storage, clock, launcher);
        var query = await reopened.QueryAsync(new(context, 10));
        var record = Assert.Single(query.Value!);
        Assert.Equal(DownloadLifecycleState.Completed, record.State);
        Assert.Equal("Orbit-Guide.pdf", record.FileName);
        Assert.Equal(16, record.ReceivedBytes);

        var opened = await reopened.OpenFileAsync(new(context, record.Id));
        Assert.True(opened.IsSuccess);
        Assert.Equal(Path.GetFullPath(downloadedFile), launcher.OpenedPath);

        var cleared = await reopened.ClearRecordsAsync(new(
            context,
            new HashSet<DownloadRecordId> { record.Id },
            true));
        Assert.True(cleared.IsSuccess);
        Assert.Equal(1, cleared.Value!.RemovedRecordCount);
        Assert.Equal(DownloadedFilesDisposition.Preserved, cleared.Value.DownloadedFiles);
        Assert.True(File.Exists(downloadedFile));
        Assert.Empty((await reopened.QueryAsync(new(context, 10))).Value!);
    }

    [Fact]
    public async Task PrivateDownloadsAreMemoryOnlyAndIsolatedBySession()
    {
        using var temp = new TempDirectory();
        var storage = new FileProfileStorage(temp.Path);
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var facade = new DownloadsFacade(storage, clock, new RecordingLauncher());
        var profile = new ProfileId(Guid.NewGuid());
        var firstSession = Privacy(BrowserProfileMode.Private, profile);
        var otherSession = Privacy(BrowserProfileMode.Private, profile);
        var resultPath = Path.Combine(temp.Path, "private.zip");

        var registration = await facade.BeginAsync(new(
            firstSession,
            new Uri("https://example.test/private.zip"),
            resultPath,
            null), new RecordingRuntime());
        await facade.UpdateAsync(
            firstSession,
            registration.Value!.DownloadId,
            512,
            null,
            DownloadLifecycleState.InProgress);

        var sameSession = await facade.QueryAsync(new(firstSession, 10));
        var isolated = await facade.QueryAsync(new(otherSession, 10));
        var normal = await facade.QueryAsync(new(
            new PrivacyContext(profile, new BrowserSessionId(Guid.NewGuid()), BrowserProfileMode.Normal),
            10));

        Assert.Single(sameSession.Value!);
        Assert.Empty(isolated.Value!);
        Assert.Empty(normal.Value!);
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task PrivateProfilesWithTheSameSessionIdCannotReadEachOthersCatalog()
    {
        using var temp = new TempDirectory();
        var storage = new ForbiddenStorage();
        var facade = new DownloadsFacade(storage, new MutableClock(DateTimeOffset.UtcNow));
        var first = Privacy(BrowserProfileMode.Private);
        var other = first with { ProfileId = new ProfileId(Guid.NewGuid()) };
        var registration = await facade.BeginAsync(new(first, new Uri("https://example.test/private.zip"),
            Path.Combine(temp.Path, "private.zip"), 12), new RecordingRuntime());
        await facade.UpdateAsync(first, registration.Value!.DownloadId, 12, 12, DownloadLifecycleState.Completed);

        Assert.Single((await facade.QueryAsync(new(first, 10))).Value!);
        Assert.Empty((await facade.QueryAsync(new(other, 10))).Value!);
        Assert.True((await facade.EndPrivateSessionAsync(other)).IsSuccess);
        Assert.Single((await facade.QueryAsync(new(first, 10))).Value!);
        Assert.Equal(0, storage.CallCount);
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task EndingPrivateSessionErasesRowsCancelsOnceAndRejectsLateAccessWithoutStorage()
    {
        using var temp = new TempDirectory();
        var storage = new ForbiddenStorage();
        var facade = new DownloadsFacade(storage, new MutableClock(DateTimeOffset.UtcNow));
        var context = Privacy(BrowserProfileMode.Private);
        var other = Privacy(BrowserProfileMode.Private, context.ProfileId);
        var pending = new RecordingRuntime();
        var completed = new RecordingRuntime();
        var unrelated = new RecordingRuntime();
        var pendingId = (await facade.BeginAsync(new(context, new Uri("https://example.test/pending.zip"),
            Path.Combine(temp.Path, "pending.zip"), 10), pending)).Value!.DownloadId;
        var completedId = (await facade.BeginAsync(new(context, new Uri("https://example.test/complete.zip"),
            Path.Combine(temp.Path, "complete.zip"), 10), completed)).Value!.DownloadId;
        await facade.UpdateAsync(context, completedId, 10, 10, DownloadLifecycleState.Completed);
        await facade.BeginAsync(new(other, new Uri("https://example.test/unrelated.zip"),
            Path.Combine(temp.Path, "unrelated.zip"), 10), unrelated);
        // A real runtime may publish its terminal event synchronously while cancelling.
        pending.OnCancel = () => facade.UpdateAsync(context, pendingId, 1, 10, DownloadLifecycleState.Cancelled);

        Assert.True((await facade.EndPrivateSessionAsync(context)).IsSuccess);
        Assert.True((await facade.EndPrivateSessionAsync(context)).IsSuccess);
        await facade.UpdateAsync(context, pendingId, 10, 10, DownloadLifecycleState.Completed);

        Assert.Equal(1, pending.CancelCount);
        Assert.Equal(0, completed.CancelCount);
        Assert.Equal(0, unrelated.CancelCount);
        Assert.Equal(ControllerErrorCode.PolicyDenied, (await facade.QueryAsync(new(context, 10))).Error?.Code);
        Assert.Equal(ControllerErrorCode.PolicyDenied, (await facade.CancelAsync(new(context, pendingId))).Error?.Code);
        Assert.Equal(ControllerErrorCode.PolicyDenied, (await facade.OpenFileAsync(new(context, completedId))).Error?.Code);
        Assert.Equal(ControllerErrorCode.PolicyDenied, (await facade.ClearRecordsAsync(new(context,
            new HashSet<DownloadRecordId> { completedId }, true))).Error?.Code);
        Assert.Equal(ControllerErrorCode.PolicyDenied, (await facade.BeginAsync(new(context,
            new Uri("https://example.test/late.zip"), Path.Combine(temp.Path, "late.zip"), 10),
            new RecordingRuntime())).Error?.Code);
        Assert.Single((await facade.QueryAsync(new(other, 10))).Value!);
        var reopened = Privacy(BrowserProfileMode.Private, context.ProfileId);
        Assert.Empty((await facade.QueryAsync(new(reopened, 10))).Value!);
        Assert.True((await facade.BeginAsync(new(reopened, new Uri("https://example.test/fresh.zip"),
            Path.Combine(temp.Path, "fresh.zip"), 10), new RecordingRuntime())).IsSuccess);
        Assert.Equal(0, storage.CallCount);
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task PrivateCloseRevokesRowsEvenWhenRuntimeCancellationFailsAndContinuesOtherTransfers()
    {
        using var temp = new TempDirectory();
        var storage = new ForbiddenStorage();
        var facade = new DownloadsFacade(storage, new MutableClock(DateTimeOffset.UtcNow));
        var context = Privacy(BrowserProfileMode.Private);
        var failed = new RecordingRuntime { OnCancel = () => throw new InvalidOperationException() };
        var next = new RecordingRuntime();
        await facade.BeginAsync(new(context, new Uri("https://example.test/first.zip"),
            Path.Combine(temp.Path, "first.zip"), 10), failed);
        await facade.BeginAsync(new(context, new Uri("https://example.test/next.zip"),
            Path.Combine(temp.Path, "next.zip"), 10), next);

        Assert.Equal(ControllerErrorCode.Unavailable, (await facade.EndPrivateSessionAsync(context)).Error?.Code);
        Assert.Equal(1, failed.CancelCount);
        Assert.Equal(1, next.CancelCount);
        Assert.Equal(ControllerErrorCode.PolicyDenied, (await facade.QueryAsync(new(context, 10))).Error?.Code);
        Assert.True((await facade.EndPrivateSessionAsync(context)).IsSuccess);
        Assert.Equal(1, failed.CancelCount);
        Assert.Equal(0, storage.CallCount);
    }

    [Fact]
    public async Task CancelTargetsOnlyTheMatchingActiveOperation()
    {
        using var temp = new TempDirectory();
        var runtime = new RecordingRuntime();
        var facade = new DownloadsFacade(
            new FileProfileStorage(temp.Path),
            new MutableClock(DateTimeOffset.UtcNow),
            new RecordingLauncher());
        var context = Privacy(BrowserProfileMode.Normal);
        var registration = await facade.BeginAsync(new(
            context,
            new Uri("https://example.test/archive.zip"),
            Path.Combine(temp.Path, "archive.zip"),
            100), runtime);

        var cancelled = await facade.CancelAsync(new(context, registration.Value!.DownloadId));
        var missing = await facade.CancelAsync(new(
            context,
            new DownloadRecordId(context.ProfileId, Guid.NewGuid())));

        Assert.True(cancelled.IsSuccess);
        Assert.Equal(1, runtime.CancelCount);
        Assert.Equal(ControllerErrorCode.NotFound, missing.Error?.Code);
    }

    [Fact]
    public async Task InProgressRecordCannotBeClearedAndInterruptedRestartIsNotShownAsActive()
    {
        using var temp = new TempDirectory();
        var storage = new FileProfileStorage(temp.Path);
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var context = Privacy(BrowserProfileMode.Normal);
        var facade = new DownloadsFacade(storage, clock, new RecordingLauncher());
        var registration = await facade.BeginAsync(new(
            context,
            new Uri("https://example.test/archive.zip"),
            Path.Combine(temp.Path, "archive.zip"),
            100), new RecordingRuntime());

        var clear = await facade.ClearRecordsAsync(new(
            context,
            new HashSet<DownloadRecordId> { registration.Value!.DownloadId },
            true));
        var afterRestart = await new DownloadsFacade(storage, clock, new RecordingLauncher())
            .QueryAsync(new(context, 10));

        Assert.Equal(ControllerErrorCode.Conflict, clear.Error?.Code);
        Assert.Equal(DownloadLifecycleState.Failed, Assert.Single(afterRestart.Value!).State);
    }

    private static PrivacyContext Privacy(BrowserProfileMode mode, ProfileId? profile = null) => new(
        profile ?? new ProfileId(Guid.NewGuid()),
        new BrowserSessionId(Guid.NewGuid()),
        mode);

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    private sealed class RecordingRuntime : IDownloadRuntimeControl
    {
        public int CancelCount { get; private set; }
        public Func<ValueTask>? OnCancel { get; set; }

        public async ValueTask<ControllerResult> CancelAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CancelCount++;
            if (OnCancel is not null) await OnCancel();
            return ControllerResult.Success();
        }
    }

    private sealed class ForbiddenStorage : IProfileStorage
    {
        public int CallCount { get; private set; }
        public ValueTask<ControllerResult<ProfileStorageEntry>> ReadAsync(ProfileStorageAddress address,
            CancellationToken cancellationToken = default) { CallCount++; throw new InvalidOperationException(); }
        public ValueTask<ControllerResult<ProfileStorageWriteReceipt>> WriteAsync(ProfileStorageWriteRequest request,
            CancellationToken cancellationToken = default) { CallCount++; throw new InvalidOperationException(); }
        public ValueTask<ControllerResult> DeleteAsync(ProfileStorageAddress address,
            ProfileStorageRevision? expectedRevision = null, CancellationToken cancellationToken = default)
        { CallCount++; throw new InvalidOperationException(); }
    }

    private sealed class RecordingLauncher : IDownloadFileLauncher
    {
        public string? OpenedPath { get; private set; }

        public ControllerResult Open(string fullPath)
        {
            OpenedPath = fullPath;
            return ControllerResult.Success();
        }
    }
}
