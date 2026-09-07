using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Sync;
using OrbitNavigator.Sync.Deletion;
using OrbitNavigator.Sync.State;
using Xunit;

namespace OrbitNavigator.Sync.Tests.Deletion;

public sealed class SyncedDataDeletionOrchestratorTests
{
    [Fact]
    public async Task Preview_maps_selection_and_preserves_downloaded_files()
    {
        var rig = new TestRig();
        var request = rig.LocalRequest(
            LocalDataCategory.DownloadRecords,
            LocalDataCategory.History,
            LocalDataCategory.Cookies);

        var result = await rig.Service.PreviewAsync(rig.Context, request, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(DownloadedFilesDisposition.Preserved, result.Value!.DownloadedFiles);
        Assert.False(rig.Local.LastRequest!.IncludesDownloadedFiles);
        Assert.Equal(
            ["cookies", "history", "download-records"],
            rig.Local.LastRequest.Namespaces.Select(value => value.Value));
        Assert.Equal(0, rig.Authorizer.Calls);
        Assert.Equal(0, rig.Transport.PushCalls);
    }

    [Fact]
    public async Task Concurrent_duplicate_delete_is_idempotent()
    {
        var rig = new TestRig();
        var request = rig.SyncedRequest(SyncDataCategory.History);

        var calls = Enumerable.Range(0, 8)
            .Select(_ => rig.Service.DeleteAsync(rig.Context, request, CancellationToken.None).AsTask())
            .ToArray();
        var results = await Task.WhenAll(calls);

        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.All(results, result => Assert.True(result.Value!.PropagationComplete));
        Assert.Equal(1, rig.Local.ClearCalls);
        Assert.Equal(1, rig.Authorizer.Calls);
        Assert.Equal(1, rig.Catalog.Calls);
        Assert.Equal(1, rig.Codec.TombstoneCalls);
        Assert.Equal(1, rig.Codec.PurgeCalls);
        Assert.Equal(2, rig.Transport.PushCalls);
        Assert.Equal(
            [SyncRecordKind.Tombstone, SyncRecordKind.Purge],
            rig.Transport.Requests.SelectMany(requestValue =>
                requestValue.Push.Tombstones.Select(value => value.Aad.RecordKind)
                    .Concat(requestValue.Push.Purges.Select(value => value.Aad.RecordKind))));
    }

    [Fact]
    public async Task Failed_publication_retains_partial_progress_and_retry_resumes()
    {
        var rig = new TestRig();
        rig.Transport.FailPushesRemaining = 1;
        var request = rig.SyncedRequest(SyncDataCategory.History);

        var failed = await rig.Service.DeleteAsync(rig.Context, request, CancellationToken.None);
        var status = await rig.Service.GetStatusAsync(
            rig.Context,
            new DataDeletionStatusRequest(rig.OperationId, rig.Authorization, rig.Fence),
            CancellationToken.None);

        Assert.False(failed.IsSuccess);
        Assert.Equal(ControllerErrorCode.Unavailable, failed.Error!.Code);
        Assert.True(status.IsSuccess);
        AssertStage(status.Value!, DataDeletionStageKind.DeleteLocal, DataDeletionStageState.Succeeded);
        AssertStage(status.Value!, DataDeletionStageKind.UploadTombstones, DataDeletionStageState.Failed);
        Assert.Equal(1, rig.Local.ClearCalls);

        var retried = await rig.Service.DeleteAsync(rig.Context, request, CancellationToken.None);

        Assert.True(retried.IsSuccess);
        Assert.True(retried.Value!.PropagationComplete);
        Assert.Equal(1, rig.Local.ClearCalls);
        Assert.Equal(1, rig.Codec.TombstoneCalls);
        Assert.Equal(1, rig.Codec.PurgeCalls);
        Assert.Equal(3, rig.Transport.PushCalls);
    }

    [Fact]
    public async Task Private_gate_and_signed_out_local_delete_make_zero_sync_calls()
    {
        var rig = new TestRig();
        var localRequest = rig.LocalRequest(LocalDataCategory.History);
        var signedOut = await rig.Service.DeleteAsync(
            rig.Context,
            localRequest,
            CancellationToken.None);
        var journalCallsBeforePrivateAttempt = rig.Journal.Calls;

        var privateBrowsing = TestRig.CreateBrowsingContext(BrowserProfileMode.Private);
        var privateResult = await SyncOperationGate.ExecuteAsync(
            privateBrowsing,
            rig.OperationId,
            context => rig.Service.DeleteAsync(context, localRequest, CancellationToken.None));

        Assert.True(signedOut.IsSuccess);
        Assert.False(privateResult.IsSuccess);
        Assert.Equal(ControllerErrorCode.PolicyDenied, privateResult.Error!.Code);
        Assert.Equal(1, rig.Local.ClearCalls);
        Assert.Equal(0, rig.Authorizer.Calls);
        Assert.Equal(0, rig.Catalog.Calls);
        Assert.Equal(0, rig.Codec.TombstoneCalls);
        Assert.Equal(0, rig.Codec.PurgeCalls);
        Assert.Equal(0, rig.Transport.PushCalls);
        Assert.Equal(0, rig.Transport.StatusCalls);
        Assert.Equal(journalCallsBeforePrivateAttempt, rig.Journal.Calls);
    }

    [Fact]
    public async Task Stale_fence_is_rejected_before_local_deletion()
    {
        var rig = new TestRig();
        rig.Authorizer.Error = ControllerError.Create(
            ControllerErrorCode.StaleClient,
            "sync.test.stale-fence");

        var result = await rig.Service.DeleteAsync(
            rig.Context,
            rig.SyncedRequest(SyncDataCategory.History),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ControllerErrorCode.StaleClient, result.Error!.Code);
        Assert.Equal(0, rig.Local.PreviewCalls);
        Assert.Equal(0, rig.Local.ClearCalls);
        Assert.Equal(0, rig.Codec.TombstoneCalls);
        Assert.Equal(0, rig.Transport.PushCalls);
    }

    [Fact]
    public async Task Forged_push_acknowledgement_cannot_advance_stage_and_retry_reuses_outbox()
    {
        var rig = new TestRig();
        rig.Verifier.RejectPush = true;
        var request = rig.SyncedRequest(SyncDataCategory.History);

        var forged = await rig.Service.DeleteAsync(rig.Context, request, CancellationToken.None);

        Assert.False(forged.IsSuccess);
        Assert.Equal(ControllerErrorCode.IntegrityFailure, forged.Error!.Code);
        Assert.Equal(1, rig.Transport.PushCalls);
        Assert.Equal(1, rig.Verifier.PushCalls);
        Assert.Equal(1, rig.Codec.TombstoneCalls);
        Assert.Equal(0, rig.Codec.PurgeCalls);

        rig.Verifier.RejectPush = false;
        var retried = await rig.Service.DeleteAsync(rig.Context, request, CancellationToken.None);

        Assert.True(retried.IsSuccess);
        Assert.Equal(3, rig.Transport.PushCalls);
        Assert.Equal(1, rig.Codec.TombstoneCalls);
        Assert.Equal(
            rig.Transport.Requests[0].Binding.IdempotencyKey,
            rig.Transport.Requests[1].Binding.IdempotencyKey);
        Assert.Equal(
            rig.Transport.Requests[0].Binding.EnvelopeIds,
            rig.Transport.Requests[1].Binding.EnvelopeIds);
    }

    [Fact]
    public async Task Forged_propagation_status_cannot_complete_operation_and_status_retry_can_recover()
    {
        var rig = new TestRig();
        rig.Verifier.RejectStatus = true;
        var request = rig.SyncedRequest(SyncDataCategory.Settings);

        var forged = await rig.Service.DeleteAsync(rig.Context, request, CancellationToken.None);
        Assert.False(forged.IsSuccess);
        Assert.Equal(ControllerErrorCode.IntegrityFailure, forged.Error!.Code);
        Assert.Equal(2, rig.Transport.PushCalls);
        Assert.Equal(1, rig.Verifier.StatusCalls);

        rig.Verifier.RejectStatus = false;
        var recovered = await rig.Service.GetStatusAsync(
            rig.Context,
            new DataDeletionStatusRequest(rig.OperationId, rig.Authorization, rig.Fence),
            CancellationToken.None);

        Assert.True(recovered.IsSuccess);
        Assert.True(recovered.Value!.PropagationComplete);
        Assert.Equal(2, rig.Transport.PushCalls);
        Assert.Equal(2, rig.Verifier.StatusCalls);
    }

    [Fact]
    public async Task Tampered_verified_receipt_binding_is_rejected()
    {
        var rig = new TestRig();
        rig.Verifier.ReturnMismatchedPushReceipt = true;

        var result = await rig.Service.DeleteAsync(
            rig.Context,
            rig.SyncedRequest(SyncDataCategory.OpenTabs),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ControllerErrorCode.IntegrityFailure, result.Error!.Code);
        Assert.Equal(1, rig.Transport.PushCalls);
        Assert.Equal(1, rig.Verifier.PushCalls);
        Assert.Equal(0, rig.Codec.PurgeCalls);
    }

    [Fact]
    public async Task Downloaded_file_risk_aborts_before_clear()
    {
        var rig = new TestRig();
        rig.Local.PreviewDeletesDownloadedFiles = true;
        var request = rig.LocalRequest(LocalDataCategory.DownloadRecords);

        var result = await rig.Service.DeleteAsync(rig.Context, request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ControllerErrorCode.IntegrityFailure, result.Error!.Code);
        Assert.Equal(1, rig.Local.PreviewCalls);
        Assert.Equal(0, rig.Local.ClearCalls);
        Assert.False(rig.Local.LastRequest!.IncludesDownloadedFiles);
        Assert.DoesNotContain(
            rig.Local.LastRequest.Namespaces,
            value => value.Value is "download" or "downloads" or "downloaded-files");
    }

    [Fact]
    public async Task Status_poll_completes_pending_propagation()
    {
        var rig = new TestRig();
        rig.Transport.PropagationComplete = false;
        var request = rig.SyncedRequest(SyncDataCategory.OpenTabs);

        var started = await rig.Service.DeleteAsync(rig.Context, request, CancellationToken.None);
        Assert.True(started.IsSuccess);
        Assert.False(started.Value!.PropagationComplete);
        AssertStage(started.Value, DataDeletionStageKind.AwaitPropagation, DataDeletionStageState.Pending);

        rig.Transport.PropagationComplete = true;
        var completed = await rig.Service.GetStatusAsync(
            rig.Context,
            new DataDeletionStatusRequest(rig.OperationId, rig.Authorization, rig.Fence),
            CancellationToken.None);

        Assert.True(completed.IsSuccess);
        Assert.True(completed.Value!.PropagationComplete);
        AssertStage(completed.Value, DataDeletionStageKind.Complete, DataDeletionStageState.Succeeded);
        Assert.Equal(2, rig.Transport.StatusCalls);
    }

    [Fact]
    public async Task Fresh_orchestrator_resumes_durable_outbox_after_local_clear()
    {
        var rig = new TestRig();
        var storage = new FakeProfileStorage();
        var firstJournal = new ProfileStorageDeletionOperationJournal(storage);
        var firstService = CreateService(rig, firstJournal);
        rig.Transport.FailOnPushCall = 2;
        var request = rig.SyncedRequest(SyncDataCategory.History);

        var interrupted = await firstService.DeleteAsync(rig.Context, request, CancellationToken.None);
        Assert.False(interrupted.IsSuccess);
        Assert.Equal(1, rig.Local.ClearCalls);
        Assert.Equal(1, rig.Catalog.Calls);
        Assert.Equal(1, rig.Codec.TombstoneCalls);

        var secondJournal = new ProfileStorageDeletionOperationJournal(storage);
        var restartedService = CreateService(rig, secondJournal);
        var resumed = await restartedService.DeleteAsync(rig.Context, request, CancellationToken.None);

        Assert.True(resumed.IsSuccess);
        Assert.True(resumed.Value!.PropagationComplete);
        Assert.Equal(1, rig.Local.ClearCalls);
        Assert.Equal(1, rig.Catalog.Calls);
        Assert.Equal(1, rig.Codec.TombstoneCalls);
        Assert.Equal(1, rig.Codec.PurgeCalls);
        Assert.Equal([0L, 1L], rig.SequenceAllocator.Reservations.Select(value => value.Sequence));
        Assert.True(storage.ReadCalls >= 2);
        Assert.True(storage.WriteCalls >= 5);

        var statusAfterSecondRestart = await CreateService(
                rig,
                new ProfileStorageDeletionOperationJournal(storage))
            .GetStatusAsync(
                rig.Context,
                new DataDeletionStatusRequest(rig.OperationId, rig.Authorization, rig.Fence),
                CancellationToken.None);
        Assert.True(statusAfterSecondRestart.IsSuccess);
        Assert.True(statusAfterSecondRestart.Value!.PropagationComplete);
        Assert.Equal(1, rig.Transport.StatusCalls);
    }

    [Fact]
    public async Task Profile_storage_journal_rejects_stale_revision_and_corrupt_frame()
    {
        var rig = new TestRig();
        var storage = new FakeProfileStorage();
        var journal = new ProfileStorageDeletionOperationJournal(storage);
        var service = CreateService(rig, journal);
        var completed = await service.DeleteAsync(
            rig.Context,
            rig.LocalRequest(LocalDataCategory.Settings),
            CancellationToken.None);
        Assert.True(completed.IsSuccess);

        var firstLoad = await journal.LoadAsync(rig.Context, rig.OperationId, CancellationToken.None);
        var secondLoad = await journal.LoadAsync(rig.Context, rig.OperationId, CancellationToken.None);
        Assert.True(firstLoad.IsSuccess);
        Assert.True(secondLoad.IsSuccess);

        var advanced = await journal.WriteAsync(
            rig.Context,
            firstLoad.Value!.Checkpoint!,
            CancellationToken.None);
        var stale = await journal.WriteAsync(
            rig.Context,
            secondLoad.Value!.Checkpoint!,
            CancellationToken.None);

        Assert.True(advanced.IsSuccess);
        Assert.False(stale.IsSuccess);
        Assert.Equal(ControllerErrorCode.Conflict, stale.Error!.Code);

        storage.CorruptOnlyValue();
        var corrupt = await new ProfileStorageDeletionOperationJournal(storage)
            .LoadAsync(rig.Context, rig.OperationId, CancellationToken.None);
        Assert.False(corrupt.IsSuccess);
        Assert.Equal(ControllerErrorCode.IntegrityFailure, corrupt.Error!.Code);
    }

    private static SyncedDataDeletionOrchestrator CreateService(
        TestRig rig,
        IDeletionOperationJournal journal) =>
        new(
            rig.Local,
            rig.Authorizer,
            rig.Catalog,
            rig.Codec,
            rig.Transport,
            rig.Verifier,
            journal,
            rig.SequenceAllocator);

    private static void AssertStage(
        DataDeletionProgress progress,
        DataDeletionStageKind stage,
        DataDeletionStageState expected) =>
        Assert.Equal(expected, Assert.Single(progress.Stages, value => value.Stage == stage).State);

    private sealed class TestRig
    {
        public TestRig()
        {
            OperationId = new SyncOperationId(Guid.NewGuid());
            Browsing = CreateBrowsingContext(BrowserProfileMode.Normal);
            Context = Required(SyncOperationContext.Authorize(Browsing, OperationId));
            Authorization = new OpaqueAuthHandle(Guid.NewGuid());
            Fence = new ClientFence(new DeviceId(Guid.NewGuid()), 4, 4);
            Authorizer.Session = new DeletionSyncSession(
                new SyncKeyMaterialHandle(Guid.NewGuid()),
                new SyncKeysetId(Guid.NewGuid()),
                2,
                Fence);
            Service = new SyncedDataDeletionOrchestrator(
                Local,
                Authorizer,
                Catalog,
                Codec,
                Transport,
                Verifier,
                Journal,
                SequenceAllocator);
        }

        public SyncOperationId OperationId { get; }

        public BrowsingContext Browsing { get; }

        public SyncOperationContext Context { get; }

        public OpaqueAuthHandle Authorization { get; }

        public ClientFence Fence { get; }

        public FakeLocalDataClearer Local { get; } = new();

        public FakeDeletionSyncAuthorizer Authorizer { get; } = new();

        public FakeDeletionEntityCatalog Catalog { get; } = new();

        public FakeSyncEnvelopeCodec Codec { get; } = new();

        public FakeSyncTransport Transport { get; } = new();

        public FakeAcknowledgementVerifier Verifier { get; } = new();

        public FakeDeletionOperationJournal Journal { get; } = new();

        public FakeSequenceAllocator SequenceAllocator { get; } = new();

        public SyncedDataDeletionOrchestrator Service { get; }

        public DataDeletionRequest LocalRequest(params LocalDataCategory[] categories) =>
            new(
                OperationId,
                new DataDeletionSelection(categories.ToHashSet(), new HashSet<SyncDataCategory>()),
                null,
                null);

        public DataDeletionRequest SyncedRequest(params SyncDataCategory[] categories)
        {
            var local = categories.Select(category => category switch
            {
                SyncDataCategory.History => LocalDataCategory.History,
                SyncDataCategory.Settings => LocalDataCategory.Settings,
                SyncDataCategory.OpenTabs => LocalDataCategory.OpenTabs,
                _ => throw new ArgumentOutOfRangeException(nameof(categories)),
            });
            return new DataDeletionRequest(
                OperationId,
                new DataDeletionSelection(local.ToHashSet(), categories.ToHashSet()),
                Authorization,
                Fence);
        }

        public static BrowsingContext CreateBrowsingContext(BrowserProfileMode mode) =>
            new(
                new PrivacyContext(
                    new ProfileId(Guid.NewGuid()),
                    new BrowserSessionId(Guid.NewGuid()),
                    mode),
                new BrowserWindowId(Guid.NewGuid()),
                new BrowserTabId(Guid.NewGuid()),
                null);

        private static T Required<T>(ControllerResult<T> result)
            where T : class
        {
            Assert.True(result.IsSuccess);
            return result.Value!;
        }
    }

    private sealed class FakeLocalDataClearer : ILocalDataClearer
    {
        public int PreviewCalls { get; private set; }

        public int ClearCalls { get; private set; }

        public bool PreviewDeletesDownloadedFiles { get; set; }

        public LocalDataClearRequest? LastRequest { get; private set; }

        public ValueTask<ControllerResult<LocalDataClearPreview>> PreviewAsync(
            LocalDataClearRequest request,
            CancellationToken cancellationToken = default)
        {
            PreviewCalls++;
            LastRequest = request;
            return ValueTask.FromResult(ControllerResult<LocalDataClearPreview>.Success(
                new LocalDataClearPreview(request.Namespaces, PreviewDeletesDownloadedFiles)));
        }

        public ValueTask<ControllerResult<LocalDataClearReceipt>> ClearAsync(
            LocalDataClearRequest request,
            CancellationToken cancellationToken = default)
        {
            ClearCalls++;
            LastRequest = request;
            return ValueTask.FromResult(ControllerResult<LocalDataClearReceipt>.Success(
                new LocalDataClearReceipt(request.Namespaces, DateTimeOffset.UtcNow, false)));
        }
    }

    private sealed class FakeDeletionSyncAuthorizer : IDeletionSyncAuthorizer
    {
        public int Calls { get; private set; }

        public DeletionSyncSession? Session { get; set; }

        public ControllerError? Error { get; set; }

        public ValueTask<ControllerResult<DeletionSyncSession>> AuthorizeAsync(
            SyncOperationContext context,
            OpaqueAuthHandle authorization,
            ClientFence requestedFence,
            CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(Error is null
                ? ControllerResult<DeletionSyncSession>.Success(Session!)
                : ControllerResult<DeletionSyncSession>.Failure(Error));
        }
    }

    private sealed class FakeDeletionEntityCatalog : IDeletionEntityCatalog
    {
        private readonly Dictionary<SyncDataCategory, IReadOnlyList<SyncEntityId>> _entities =
            Enum.GetValues<SyncDataCategory>().ToDictionary(
                category => category,
                _ => (IReadOnlyList<SyncEntityId>)[new SyncEntityId(Guid.NewGuid())]);

        public int Calls { get; private set; }

        public ValueTask<ControllerResult<IReadOnlyList<SyncEntityId>>> ListEntityIdsAsync(
            SyncOperationContext context,
            SyncDataCategory category,
            CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(
                ControllerResult<IReadOnlyList<SyncEntityId>>.Success(_entities[category]));
        }
    }

    private sealed class FakeSyncEnvelopeCodec : ISyncEnvelopeCodec
    {
        public int TombstoneCalls { get; private set; }

        public int PurgeCalls { get; private set; }

        public ValueTask<ControllerResult<EncryptedSyncEnvelope>> EncryptAsync(
            SyncOperationContext context,
            SyncKeyMaterialHandle keyMaterial,
            CanonicalSyncAad aad,
            SyncRecordPayload record,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ControllerResult<EncryptedSyncEnvelope>.Failure(NotSupported()));

        public ValueTask<ControllerResult<SyncRecordPayload>> DecryptAsync(
            SyncOperationContext context,
            SyncKeyMaterialHandle keyMaterial,
            EncryptedSyncEnvelope envelope,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ControllerResult<SyncRecordPayload>.Failure(NotSupported()));

        public ValueTask<ControllerResult<EncryptedSyncTombstone>> EncryptTombstoneAsync(
            SyncOperationContext context,
            SyncKeyMaterialHandle keyMaterial,
            CanonicalSyncAad aad,
            CancellationToken cancellationToken)
        {
            TombstoneCalls++;
            return ValueTask.FromResult(ControllerResult<EncryptedSyncTombstone>.Success(
                new EncryptedSyncTombstone(aad, new byte[12], new byte[1], new byte[16])));
        }

        public ValueTask<ControllerResult<AuthenticatedSyncTombstoneReceipt>> DecryptAndValidateTombstoneAsync(
            SyncOperationContext context,
            SyncKeyMaterialHandle keyMaterial,
            EncryptedSyncTombstone tombstone,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ControllerResult<AuthenticatedSyncTombstoneReceipt>.Failure(NotSupported()));

        public ValueTask<ControllerResult<EncryptedPurgeCommand>> EncryptPurgeAsync(
            SyncOperationContext context,
            SyncKeyMaterialHandle keyMaterial,
            CanonicalSyncAad aad,
            DecryptedPurgeMarker marker,
            CancellationToken cancellationToken)
        {
            PurgeCalls++;
            return ValueTask.FromResult(ControllerResult<EncryptedPurgeCommand>.Success(
                new EncryptedPurgeCommand(aad, new byte[12], new byte[1], new byte[16])));
        }

        public ValueTask<ControllerResult<DecryptedPurgeMarker>> DecryptAndValidatePurgeAsync(
            SyncOperationContext context,
            SyncKeyMaterialHandle keyMaterial,
            EncryptedPurgeCommand command,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ControllerResult<DecryptedPurgeMarker>.Failure(NotSupported()));

        private static ControllerError NotSupported() =>
            ControllerError.Create(ControllerErrorCode.NotSupported, "sync.test.not-supported");
    }

    private sealed class FakeSyncTransport : ISyncedDeletionTransport
    {
        public int PushCalls { get; private set; }

        public int StatusCalls { get; private set; }

        public int FailPushesRemaining { get; set; }

        public int? FailOnPushCall { get; set; }

        public bool PropagationComplete { get; set; } = true;

        public List<BoundDeletionPushRequest> Requests { get; } = [];

        public ValueTask<ControllerResult<ServerSignedDeletionPushAcknowledgement>> PushAsync(
            SyncOperationContext context,
            OpaqueAuthHandle authorization,
            BoundDeletionPushRequest request,
            CancellationToken cancellationToken)
        {
            PushCalls++;
            Requests.Add(request);
            if (FailPushesRemaining > 0 || FailOnPushCall == PushCalls)
            {
                if (FailPushesRemaining > 0)
                    FailPushesRemaining--;
                return ValueTask.FromResult(ControllerResult<ServerSignedDeletionPushAcknowledgement>.Failure(
                    ControllerError.Create(
                        ControllerErrorCode.Unavailable,
                        "sync.test.transport-unavailable",
                        isRetryable: true)));
            }

            var aad = CreateAad(request);
            var receipt = new AuthenticatedDeletionPushReceipt(
                aad,
                new SyncCursor($"cursor-{PushCalls}"),
                request.Push.Fence,
                DateTimeOffset.UtcNow);
            return ValueTask.FromResult(
                ControllerResult<ServerSignedDeletionPushAcknowledgement>.Success(
                    new ServerSignedDeletionPushAcknowledgement(
                        receipt,
                        new MyOrbitServerSigningKeyId(Guid.Parse("f19ce60d-872c-4283-9edb-0953339070ec")),
                        new byte[64])));
        }

        public ValueTask<ControllerResult<ServerSignedDeletionPropagationStatus>> GetPropagationStatusAsync(
            SyncOperationContext context,
            OpaqueAuthHandle authorization,
            AuthenticatedDeletionPushReceipt acceptedReceipt,
            CancellationToken cancellationToken)
        {
            StatusCalls++;
            var state = PropagationComplete
                ? DeletionCategoryPropagationState.Propagated
                : DeletionCategoryPropagationState.Pending;
            var status = new AuthenticatedDeletionPropagationStatus(
                acceptedReceipt.Aad,
                acceptedReceipt.Fence,
                acceptedReceipt.Aad.Categories.Select(category => new DeletionCategoryPropagation(
                    category,
                    state,
                    DateTimeOffset.UtcNow,
                    null)).ToArray(),
                PropagationComplete,
                DateTimeOffset.UtcNow);
            return ValueTask.FromResult(
                ControllerResult<ServerSignedDeletionPropagationStatus>.Success(
                    new ServerSignedDeletionPropagationStatus(
                        status,
                        new MyOrbitServerSigningKeyId(Guid.Parse("f19ce60d-872c-4283-9edb-0953339070ec")),
                        new byte[64])));
        }

        private static DeletionAcknowledgementAad CreateAad(BoundDeletionPushRequest request)
        {
            var firstAad = request.Push.Tombstones.Select(value => value.Aad)
                .Concat(request.Push.Purges.Select(value => value.Aad))
                .First();
            return new DeletionAcknowledgementAad(
                SyncProtocol.CurrentProtocolVersion,
                SyncProtocol.CurrentSchemaVersion,
                firstAad.ProfileId,
                request.Push.DeviceId,
                firstAad.KeysetId,
                request.Push.Fence.ClientGeneration,
                request.Binding.OperationId,
                request.Binding.Categories,
                request.Binding.EnvelopeIds,
                request.Binding.RequestDigest,
                request.Binding.IdempotencyKey);
        }
    }

    private sealed class FakeAcknowledgementVerifier : IDeletionAcknowledgementSignatureVerifier
    {
        public int PushCalls { get; private set; }

        public int StatusCalls { get; private set; }

        public bool RejectPush { get; set; }

        public bool RejectStatus { get; set; }

        public bool ReturnMismatchedPushReceipt { get; set; }

        public ValueTask<ControllerResult<AuthenticatedDeletionPushReceipt>> VerifyPushAsync(
            SyncOperationContext context,
            BoundDeletionPushRequest acceptedRequest,
            ServerSignedDeletionPushAcknowledgement acknowledgement,
            CancellationToken cancellationToken)
        {
            PushCalls++;
            if (RejectPush)
                return ValueTask.FromResult(Integrity<AuthenticatedDeletionPushReceipt>());

            var receipt = acknowledgement.Payload;
            if (ReturnMismatchedPushReceipt)
            {
                receipt = receipt with
                {
                    Aad = receipt.Aad with { OperationId = new SyncOperationId(Guid.NewGuid()) },
                };
            }

            return ValueTask.FromResult(
                ControllerResult<AuthenticatedDeletionPushReceipt>.Success(receipt));
        }

        public ValueTask<ControllerResult<AuthenticatedDeletionPropagationStatus>> VerifyStatusAsync(
            SyncOperationContext context,
            AuthenticatedDeletionPushReceipt acceptedReceipt,
            ServerSignedDeletionPropagationStatus status,
            CancellationToken cancellationToken)
        {
            StatusCalls++;
            return ValueTask.FromResult(RejectStatus
                ? Integrity<AuthenticatedDeletionPropagationStatus>()
                : ControllerResult<AuthenticatedDeletionPropagationStatus>.Success(status.Payload));
        }

        private static ControllerResult<T> Integrity<T>()
            where T : class =>
            ControllerResult<T>.Failure(ControllerError.Create(
                ControllerErrorCode.IntegrityFailure,
                "sync.test.signature-invalid"));
    }

    private sealed class FakeDeletionOperationJournal : IDeletionOperationJournal
    {
        private readonly object _gate = new();
        private readonly Dictionary<SyncOperationId, DeletionJournalCheckpoint> _values = [];

        public int Calls { get; private set; }

        public ValueTask<ControllerResult<DeletionJournalLookup>> LoadAsync(
            SyncOperationContext context,
            SyncOperationId operationId,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                Calls++;
                return ValueTask.FromResult(ControllerResult<DeletionJournalLookup>.Success(
                    _values.TryGetValue(operationId, out var value)
                        ? new DeletionJournalLookup(true, Clone(value))
                        : new DeletionJournalLookup(false, null)));
            }
        }

        public ValueTask<ControllerResult<DeletionJournalCheckpoint>> WriteAsync(
            SyncOperationContext context,
            DeletionJournalCheckpoint checkpoint,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                Calls++;
                if (_values.TryGetValue(checkpoint.OperationId, out var current))
                {
                    if (checkpoint.Revision != current.Revision)
                        return ValueTask.FromResult(Conflict<DeletionJournalCheckpoint>());
                }
                else if (checkpoint.Revision is not null)
                {
                    return ValueTask.FromResult(Conflict<DeletionJournalCheckpoint>());
                }

                var stored = checkpoint with
                {
                    SerializedState = checkpoint.SerializedState.ToArray(),
                    Revision = new ProfileStorageRevision(Guid.NewGuid()),
                };
                _values[checkpoint.OperationId] = stored;
                return ValueTask.FromResult(
                    ControllerResult<DeletionJournalCheckpoint>.Success(Clone(stored)));
            }
        }

        private static DeletionJournalCheckpoint Clone(DeletionJournalCheckpoint value) =>
            value with { SerializedState = value.SerializedState.ToArray() };

        private static ControllerResult<T> Conflict<T>()
            where T : class =>
            ControllerResult<T>.Failure(ControllerError.Create(
                ControllerErrorCode.Conflict,
                "sync.test.journal-conflict"));
    }

    private sealed class FakeSequenceAllocator : IDurableSyncSequenceAllocator
    {
        private readonly object _gate = new();
        private readonly Dictionary<SyncStateScope, long> _lastValues = [];

        public List<SyncSequenceReservation> Reservations { get; } = [];

        public ValueTask<ControllerResult<SyncSequenceReservation>> ReserveNextAsync(
            SyncOperationContext context,
            SyncStateScope scope,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _lastValues.TryGetValue(scope, out var last);
                var next = _lastValues.ContainsKey(scope) ? checked(last + 1) : 0;
                _lastValues[scope] = next;
                var reservation = new SyncSequenceReservation(scope, next);
                Reservations.Add(reservation);
                return ValueTask.FromResult(
                    ControllerResult<SyncSequenceReservation>.Success(reservation));
            }
        }
    }

    private sealed class FakeProfileStorage : IProfileStorage
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, StoredValue> _values = new(StringComparer.Ordinal);

        public int ReadCalls { get; private set; }

        public int WriteCalls { get; private set; }

        public ValueTask<ControllerResult<ProfileStorageEntry>> ReadAsync(
            ProfileStorageAddress address,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                ReadCalls++;
                if (!_values.TryGetValue(Key(address), out var stored))
                {
                    return ValueTask.FromResult(ControllerResult<ProfileStorageEntry>.Failure(
                        ControllerError.Create(
                            ControllerErrorCode.NotFound,
                            "sync.test.storage-not-found")));
                }

                return ValueTask.FromResult(ProfileStorageEntry.Create(stored.Revision, stored.Payload));
            }
        }

        public ValueTask<ControllerResult<ProfileStorageWriteReceipt>> WriteAsync(
            ProfileStorageWriteRequest request,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                WriteCalls++;
                var key = Key(request.Address);
                var found = _values.TryGetValue(key, out var current);
                if ((request.ExpectedRevision is null && found) ||
                    (request.ExpectedRevision is { } expected &&
                        (!found || current!.Revision != expected)))
                {
                    return ValueTask.FromResult(ControllerResult<ProfileStorageWriteReceipt>.Failure(
                        ControllerError.Create(
                            ControllerErrorCode.Conflict,
                            "sync.test.storage-conflict")));
                }

                var revision = new ProfileStorageRevision(Guid.NewGuid());
                _values[key] = new StoredValue(revision, request.Payload.ToArray());
                return ValueTask.FromResult(ControllerResult<ProfileStorageWriteReceipt>.Success(
                    new ProfileStorageWriteReceipt(revision)));
            }
        }

        public ValueTask<ControllerResult> DeleteAsync(
            ProfileStorageAddress address,
            ProfileStorageRevision? expectedRevision = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                var key = Key(address);
                if (_values.TryGetValue(key, out var current) &&
                    expectedRevision is { } expected && current.Revision != expected)
                {
                    return ValueTask.FromResult(ControllerResult.Failure(
                        ControllerError.Create(
                            ControllerErrorCode.Conflict,
                            "sync.test.storage-conflict")));
                }

                _values.Remove(key);
                return ValueTask.FromResult(ControllerResult.Success());
            }
        }

        public void CorruptOnlyValue()
        {
            lock (_gate)
            {
                var pair = Assert.Single(_values);
                var payload = pair.Value.Payload.ToArray();
                payload[^1] ^= 0x01;
                _values[pair.Key] = pair.Value with { Payload = payload };
            }
        }

        private static string Key(ProfileStorageAddress address) => string.Join(
            '|',
            address.Context.ProfileId.Value.ToString("N"),
            address.Durability,
            address.Namespace.Value,
            address.Key.Value);

        private sealed record StoredValue(ProfileStorageRevision Revision, byte[] Payload);
    }
}
