using System.Collections.Concurrent;
using System.Collections.Frozen;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Sync;
using OrbitNavigator.Sync.State;

namespace OrbitNavigator.Sync.Deletion;

/// <summary>
/// Coordinates local clearing and E2E-encrypted remote deletion. Operation state
/// remains available for the lifetime of this instance and is safe for concurrent
/// preview, delete, retry, and status calls.
/// </summary>
public sealed class SyncedDataDeletionOrchestrator : ISyncedDataDeletion
{
    private static readonly IReadOnlyList<DataDeletionStageKind> OrderedStages =
        Enum.GetValues<DataDeletionStageKind>();

    private readonly ILocalDataClearer _localDataClearer;
    private readonly IDeletionSyncAuthorizer _syncAuthorizer;
    private readonly IDeletionEntityCatalog _entityCatalog;
    private readonly ISyncEnvelopeCodec _codec;
    private readonly ISyncedDeletionTransport _transport;
    private readonly IDeletionAcknowledgementSignatureVerifier _acknowledgementVerifier;
    private readonly IDeletionOperationJournal _journal;
    private readonly IDurableSyncSequenceAllocator _sequenceAllocator;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<SyncOperationId, OperationState> _operations = new();

    public SyncedDataDeletionOrchestrator(
        ILocalDataClearer localDataClearer,
        IDeletionSyncAuthorizer syncAuthorizer,
        IDeletionEntityCatalog entityCatalog,
        ISyncEnvelopeCodec codec,
        ISyncedDeletionTransport transport,
        IDeletionAcknowledgementSignatureVerifier acknowledgementVerifier,
        IDeletionOperationJournal journal,
        IDurableSyncSequenceAllocator sequenceAllocator,
        TimeProvider? timeProvider = null)
    {
        _localDataClearer = localDataClearer ?? throw new ArgumentNullException(nameof(localDataClearer));
        _syncAuthorizer = syncAuthorizer ?? throw new ArgumentNullException(nameof(syncAuthorizer));
        _entityCatalog = entityCatalog ?? throw new ArgumentNullException(nameof(entityCatalog));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _acknowledgementVerifier = acknowledgementVerifier ??
            throw new ArgumentNullException(nameof(acknowledgementVerifier));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _sequenceAllocator = sequenceAllocator ?? throw new ArgumentNullException(nameof(sequenceAllocator));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<ControllerResult<DataDeletionPreview>> PreviewAsync(
        SyncOperationContext context,
        DataDeletionRequest request,
        CancellationToken cancellationToken)
    {
        var validationError = Validate(context, request);
        if (validationError is not null)
            return ControllerResult<DataDeletionPreview>.Failure(validationError);

        var localRequest = CreateLocalRequest(context, request.Selection.LocalCategories);
        if (!localRequest.IsSuccess)
            return ControllerResult<DataDeletionPreview>.Failure(localRequest.Error!);

        try
        {
            var localPreview = await _localDataClearer
                .PreviewAsync(localRequest.Value!, cancellationToken)
                .ConfigureAwait(false);
            if (!localPreview.IsSuccess)
                return ControllerResult<DataDeletionPreview>.Failure(localPreview.Error!);

            if (localPreview.Value!.DownloadedFilesWillBeDeleted)
            {
                return ControllerResult<DataDeletionPreview>.Failure(DownloadedFilesIntegrityError());
            }

            var stages = OrderedStages
                .Select(stage => new DataDeletionStageStatus(
                    stage,
                    stage == DataDeletionStageKind.Validate
                        ? DataDeletionStageState.Succeeded
                        : DataDeletionStageState.Pending,
                    stage == DataDeletionStageKind.Validate ? _timeProvider.GetUtcNow() : null,
                    null))
                .ToArray();

            return ControllerResult<DataDeletionPreview>.Success(new DataDeletionPreview(
                request.OperationId,
                request.Selection.LocalCategories.ToFrozenSet(),
                request.Selection.SyncedCategories.ToFrozenSet(),
                stages,
                request.Selection.SyncedCategories.Count == 0 ? null : request.Fence,
                DownloadedFilesDisposition.Preserved));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ControllerResult<DataDeletionPreview>.Failure(CancelledError());
        }
        catch (Exception)
        {
            return ControllerResult<DataDeletionPreview>.Failure(InternalError());
        }
    }

    public async ValueTask<ControllerResult<DataDeletionProgress>> DeleteAsync(
        SyncOperationContext context,
        DataDeletionRequest request,
        CancellationToken cancellationToken)
    {
        var validationError = Validate(context, request);
        if (validationError is not null)
            return ControllerResult<DataDeletionProgress>.Failure(validationError);

        var localRequest = CreateLocalRequest(context, request.Selection.LocalCategories);
        if (!localRequest.IsSuccess)
            return ControllerResult<DataDeletionProgress>.Failure(localRequest.Error!);

        var stateResult = await GetOrCreateStateAsync(
                context,
                request,
                localRequest.Value!,
                cancellationToken)
            .ConfigureAwait(false);
        if (!stateResult.IsSuccess)
            return ControllerResult<DataDeletionProgress>.Failure(stateResult.Error!);

        var state = stateResult.Value!;
        try
        {
            await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ControllerResult<DataDeletionProgress>.Failure(CancelledError());
        }

        try
        {
            if (state.IsComplete)
                return ControllerResult<DataDeletionProgress>.Success(state.SnapshotProgress());

            return await ExecuteDeletionAsync(context, request, state, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public async ValueTask<ControllerResult<DataDeletionProgress>> GetStatusAsync(
        SyncOperationContext context,
        DataDeletionStatusRequest request,
        CancellationToken cancellationToken)
    {
        if (context is null ||
            request is null ||
            !request.OperationId.IsDefined ||
            request.OperationId != context.OperationId)
        {
            return ControllerResult<DataDeletionProgress>.Failure(InvalidRequestError());
        }

        if (!_operations.TryGetValue(request.OperationId, out var state))
        {
            var loaded = await LoadStateForStatusAsync(context, request, cancellationToken)
                .ConfigureAwait(false);
            if (!loaded.IsSuccess)
                return ControllerResult<DataDeletionProgress>.Failure(loaded.Error!);
            state = loaded.Value!;
        }

        if (state.ProfileId != context.Browsing.Privacy.ProfileId)
            return ControllerResult<DataDeletionProgress>.Failure(OperationNotFoundError());

        if (state.HasSyncedCategories)
        {
            if (request.Authorization.IsEmpty)
            {
                return ControllerResult<DataDeletionProgress>.Failure(
                    ControllerError.Create(
                        ControllerErrorCode.PolicyDenied,
                        "sync.deletion.authorization-required"));
            }

            if (!request.Fence.IsDefined || request.Fence != state.RequestedFence)
            {
                return ControllerResult<DataDeletionProgress>.Failure(StaleFenceError());
            }

            state.RefreshAuthorization(request.Authorization);
        }

        try
        {
            await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ControllerResult<DataDeletionProgress>.Failure(CancelledError());
        }

        try
        {
            if (state.HasSyncedCategories && state.PurgesPublished && !state.IsComplete)
            {
                var propagation = await ObservePropagationAsync(
                        context,
                        state,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!propagation.IsSuccess)
                    return ControllerResult<DataDeletionProgress>.Failure(propagation.Error!);

                var checkpointed = await CheckpointAsync(context, state, cancellationToken)
                    .ConfigureAwait(false);
                if (!checkpointed.IsSuccess)
                    return ControllerResult<DataDeletionProgress>.Failure(checkpointed.Error!);
            }

            return ControllerResult<DataDeletionProgress>.Success(state.SnapshotProgress());
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private async ValueTask<ControllerResult<DataDeletionProgress>> ExecuteDeletionAsync(
        SyncOperationContext context,
        DataDeletionRequest request,
        OperationState state,
        CancellationToken cancellationToken)
    {
        var activeStage = DataDeletionStageKind.Validate;
        try
        {
            state.Succeed(activeStage, _timeProvider.GetUtcNow());

            activeStage = DataDeletionStageKind.Authorize;
            if (!state.IsSucceeded(activeStage) ||
                (state.HasSyncedCategories && state.SyncSession is null))
            {
                state.Run(activeStage, _timeProvider.GetUtcNow());
                if (state.HasSyncedCategories)
                {
                    var authorization = await AuthorizeAndCaptureEntitiesAsync(
                            context,
                            request,
                            state,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!authorization.IsSuccess)
                        return Fail<DataDeletionProgress>(state, activeStage, authorization.Error!);
                }

                state.Succeed(activeStage, _timeProvider.GetUtcNow());
            }

            var checkpointed = await CheckpointAsync(context, state, cancellationToken)
                .ConfigureAwait(false);
            if (!checkpointed.IsSuccess)
                return Fail<DataDeletionProgress>(state, activeStage, checkpointed.Error!);

            activeStage = DataDeletionStageKind.DeleteLocal;
            if (!state.IsSucceeded(activeStage))
            {
                state.Run(activeStage, _timeProvider.GetUtcNow());
                var localPreview = await _localDataClearer
                    .PreviewAsync(state.LocalRequest, cancellationToken)
                    .ConfigureAwait(false);
                if (!localPreview.IsSuccess)
                    return Fail<DataDeletionProgress>(state, activeStage, localPreview.Error!);
                if (localPreview.Value!.DownloadedFilesWillBeDeleted)
                    return Fail<DataDeletionProgress>(state, activeStage, DownloadedFilesIntegrityError());

                var cleared = await _localDataClearer
                    .ClearAsync(state.LocalRequest, cancellationToken)
                    .ConfigureAwait(false);
                if (!cleared.IsSuccess)
                    return Fail<DataDeletionProgress>(state, activeStage, cleared.Error!);
                if (!ValidLocalReceipt(state.LocalRequest, cleared.Value!))
                    return Fail<DataDeletionProgress>(state, activeStage, DownloadedFilesIntegrityError());

                state.Succeed(activeStage, _timeProvider.GetUtcNow());
                checkpointed = await CheckpointAsync(context, state, cancellationToken)
                    .ConfigureAwait(false);
                if (!checkpointed.IsSuccess)
                    return Fail<DataDeletionProgress>(state, activeStage, checkpointed.Error!);
            }

            activeStage = DataDeletionStageKind.UploadTombstones;
            if (!state.IsSucceeded(activeStage))
            {
                state.Run(activeStage, _timeProvider.GetUtcNow());
                if (state.HasSyncedCategories)
                {
                    var tombstones = await BuildTombstonesAsync(context, state, cancellationToken)
                        .ConfigureAwait(false);
                    if (!tombstones.IsSuccess)
                        return Fail<DataDeletionProgress>(state, activeStage, tombstones.Error!);

                    checkpointed = await CheckpointAsync(context, state, cancellationToken)
                        .ConfigureAwait(false);
                    if (!checkpointed.IsSuccess)
                        return Fail<DataDeletionProgress>(state, activeStage, checkpointed.Error!);

                    if (tombstones.Value!.Count > 0)
                    {
                        var pushed = await PushAsync(
                                context,
                                state,
                                tombstones.Value,
                                Array.Empty<EncryptedPurgeCommand>(),
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (!pushed.IsSuccess)
                            return Fail<DataDeletionProgress>(state, activeStage, pushed.Error!);

                        state.TombstoneReceipt = pushed.Value;
                    }
                }

                state.Succeed(activeStage, _timeProvider.GetUtcNow());
                checkpointed = await CheckpointAsync(context, state, cancellationToken)
                    .ConfigureAwait(false);
                if (!checkpointed.IsSuccess)
                    return Fail<DataDeletionProgress>(state, activeStage, checkpointed.Error!);
            }

            activeStage = DataDeletionStageKind.UploadPurgeCommands;
            if (!state.IsSucceeded(activeStage))
            {
                state.Run(activeStage, _timeProvider.GetUtcNow());
                if (state.HasSyncedCategories)
                {
                    var purges = await BuildPurgesAsync(context, state, cancellationToken)
                        .ConfigureAwait(false);
                    if (!purges.IsSuccess)
                        return Fail<DataDeletionProgress>(state, activeStage, purges.Error!);

                    checkpointed = await CheckpointAsync(context, state, cancellationToken)
                        .ConfigureAwait(false);
                    if (!checkpointed.IsSuccess)
                        return Fail<DataDeletionProgress>(state, activeStage, checkpointed.Error!);

                    var pushed = await PushAsync(
                            context,
                            state,
                            Array.Empty<EncryptedSyncTombstone>(),
                            purges.Value!,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!pushed.IsSuccess)
                        return Fail<DataDeletionProgress>(state, activeStage, pushed.Error!);

                    state.PurgeReceipt = pushed.Value;
                    state.PurgesPublished = true;
                }

                state.Succeed(activeStage, _timeProvider.GetUtcNow());
                checkpointed = await CheckpointAsync(context, state, cancellationToken)
                    .ConfigureAwait(false);
                if (!checkpointed.IsSuccess)
                    return Fail<DataDeletionProgress>(state, activeStage, checkpointed.Error!);
            }

            activeStage = DataDeletionStageKind.AwaitPropagation;
            if (state.HasSyncedCategories)
            {
                var propagation = await ObservePropagationAsync(context, state, cancellationToken)
                    .ConfigureAwait(false);
                if (!propagation.IsSuccess)
                    return ControllerResult<DataDeletionProgress>.Failure(propagation.Error!);
            }
            else
            {
                state.Succeed(activeStage, _timeProvider.GetUtcNow());
                state.Succeed(DataDeletionStageKind.Complete, _timeProvider.GetUtcNow());
                state.PropagationComplete = true;
            }

            checkpointed = await CheckpointAsync(context, state, cancellationToken)
                .ConfigureAwait(false);
            if (!checkpointed.IsSuccess)
                return Fail<DataDeletionProgress>(state, activeStage, checkpointed.Error!);

            return ControllerResult<DataDeletionProgress>.Success(state.SnapshotProgress());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Fail<DataDeletionProgress>(state, activeStage, CancelledError());
        }
        catch (Exception)
        {
            return Fail<DataDeletionProgress>(state, activeStage, InternalError());
        }
    }

    private async ValueTask<ControllerResult> AuthorizeAndCaptureEntitiesAsync(
        SyncOperationContext context,
        DataDeletionRequest request,
        OperationState state,
        CancellationToken cancellationToken)
    {
        var authorization = request.Authorization!.Value;
        var requestedFence = request.Fence!;
        var session = await _syncAuthorizer
            .AuthorizeAsync(context, authorization, requestedFence, cancellationToken)
            .ConfigureAwait(false);
        if (!session.IsSuccess)
            return ControllerResult.Failure(session.Error!);

        if (!session.Value!.IsDefined || session.Value.Fence != requestedFence)
            return ControllerResult.Failure(StaleFenceError());
        if (!OutboxMatchesSession(state, session.Value))
        {
            return ControllerResult.Failure(
                ControllerError.Create(
                    ControllerErrorCode.IntegrityFailure,
                    "sync.deletion.outbox-session-mismatch"));
        }

        if (state.Entities is null)
        {
            var entities = new Dictionary<SyncDataCategory, IReadOnlyList<SyncEntityId>>();
            foreach (var category in state.SyncedCategories)
            {
                var listed = await _entityCatalog
                    .ListEntityIdsAsync(context, category, cancellationToken)
                    .ConfigureAwait(false);
                if (!listed.IsSuccess)
                    return ControllerResult.Failure(listed.Error!);

                var values = listed.Value?.Distinct().ToArray();
                if (values is null || values.Any(entity => !entity.IsDefined))
                {
                    return ControllerResult.Failure(
                        ControllerError.Create(
                            ControllerErrorCode.IntegrityFailure,
                            "sync.deletion.entity-catalog-invalid"));
                }

                entities[category] = values;
            }

            state.Entities = entities;
        }

        state.SyncSession = session.Value;
        return ControllerResult.Success();
    }

    private static bool OutboxMatchesSession(
        OperationState state,
        DeletionSyncSession session)
    {
        static bool Matches(CanonicalSyncAad aad, DeletionSyncSession value) =>
            aad.DeviceId == value.Fence.DeviceId &&
            aad.ClientGeneration == value.Fence.ClientGeneration &&
            aad.KeysetId == value.KeysetId &&
            aad.KeyEpoch == value.KeyEpoch;

        return (state.Tombstones is null || state.Tombstones.All(value => Matches(value.Aad, session))) &&
            (state.Purges is null || state.Purges.All(value => Matches(value.Aad, session)));
    }

    private async ValueTask<ControllerResult<IReadOnlyList<EncryptedSyncTombstone>>> BuildTombstonesAsync(
        SyncOperationContext context,
        OperationState state,
        CancellationToken cancellationToken)
    {
        if (state.Tombstones is not null)
            return ControllerResult<IReadOnlyList<EncryptedSyncTombstone>>.Success(state.Tombstones);

        var session = state.SyncSession!;
        var values = new List<EncryptedSyncTombstone>();
        foreach (var category in state.SyncedCategories)
        {
            foreach (var entityId in state.Entities![category])
            {
                var aad = await CreateAadAsync(
                        context,
                        state,
                        session,
                        SyncRecordKind.Tombstone,
                        category,
                        entityId,
                        null,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!aad.IsSuccess)
                    return ControllerResult<IReadOnlyList<EncryptedSyncTombstone>>.Failure(aad.Error!);
                var encrypted = await _codec
                    .EncryptTombstoneAsync(context, session.KeyMaterial, aad.Value!, cancellationToken)
                    .ConfigureAwait(false);
                if (!encrypted.IsSuccess)
                    return ControllerResult<IReadOnlyList<EncryptedSyncTombstone>>.Failure(encrypted.Error!);
                if (!SyncContractRules.ValidateTombstone(encrypted.Value).IsValid)
                {
                    return ControllerResult<IReadOnlyList<EncryptedSyncTombstone>>.Failure(
                        ControllerError.Create(
                            ControllerErrorCode.IntegrityFailure,
                            "sync.deletion.tombstone-invalid"));
                }

                values.Add(encrypted.Value!);
            }
        }

        state.Tombstones = values.ToArray();
        return ControllerResult<IReadOnlyList<EncryptedSyncTombstone>>.Success(state.Tombstones);
    }

    private async ValueTask<ControllerResult<IReadOnlyList<EncryptedPurgeCommand>>> BuildPurgesAsync(
        SyncOperationContext context,
        OperationState state,
        CancellationToken cancellationToken)
    {
        if (state.Purges is not null)
            return ControllerResult<IReadOnlyList<EncryptedPurgeCommand>>.Success(state.Purges);

        var session = state.SyncSession!;
        var values = new List<EncryptedPurgeCommand>();
        foreach (var category in state.SyncedCategories)
        {
            var aad = await CreateAadAsync(
                    context,
                    state,
                    session,
                    SyncRecordKind.Purge,
                    category,
                    new SyncEntityId(Guid.NewGuid()),
                    state.OperationId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!aad.IsSuccess)
                return ControllerResult<IReadOnlyList<EncryptedPurgeCommand>>.Failure(aad.Error!);
            var marker = new DecryptedPurgeMarker(
                state.OperationId,
                state.ProfileId,
                category,
                session.Fence.ClientGeneration,
                session.KeysetId);
            var encrypted = await _codec
                .EncryptPurgeAsync(context, session.KeyMaterial, aad.Value!, marker, cancellationToken)
                .ConfigureAwait(false);
            if (!encrypted.IsSuccess)
                return ControllerResult<IReadOnlyList<EncryptedPurgeCommand>>.Failure(encrypted.Error!);
            if (!SyncContractRules.ValidatePurgeCommand(encrypted.Value).IsValid)
            {
                return ControllerResult<IReadOnlyList<EncryptedPurgeCommand>>.Failure(
                    ControllerError.Create(
                        ControllerErrorCode.IntegrityFailure,
                        "sync.deletion.purge-invalid"));
            }

            values.Add(encrypted.Value!);
        }

        state.Purges = values.ToArray();
        return ControllerResult<IReadOnlyList<EncryptedPurgeCommand>>.Success(state.Purges);
    }

    private async ValueTask<ControllerResult<AuthenticatedDeletionPushReceipt>> PushAsync(
        SyncOperationContext context,
        OperationState state,
        IReadOnlyList<EncryptedSyncTombstone> tombstones,
        IReadOnlyList<EncryptedPurgeCommand> purges,
        CancellationToken cancellationToken)
    {
        var session = state.SyncSession!;
        var request = new SyncPushRequest(
            session.Fence.DeviceId,
            session.Fence,
            Array.Empty<EncryptedSyncEnvelope>(),
            tombstones,
            purges);
        var validation = SyncTransferRules.ValidatePush(request);
        if (!validation.IsValid)
        {
            return ControllerResult<AuthenticatedDeletionPushReceipt>.Failure(
                ControllerError.Create(
                    ControllerErrorCode.IntegrityFailure,
                    "sync.deletion.push-invalid"));
        }

        var digest = DeletionAcknowledgementRules.ComputeRequestDigest(request);
        if (!digest.IsSuccess)
            return ControllerResult<AuthenticatedDeletionPushReceipt>.Failure(digest.Error!);
        var categories = tombstones.Select(value => value.Aad.Category)
            .Concat(purges.Select(value => value.Aad.Category))
            .ToFrozenSet();
        var envelopes = tombstones.Select(value => value.Aad.EnvelopeId)
            .Concat(purges.Select(value => value.Aad.EnvelopeId))
            .ToArray();
        var phase = purges.Count > 0 ? "purges" : "tombstones";
        var binding = new DeletionPushBinding(
            state.OperationId,
            categories,
            envelopes,
            digest.Value!,
            CreateIdempotencyKey(state.OperationId, phase));
        var boundRequest = new BoundDeletionPushRequest(request, binding);
        if (!DeletionAcknowledgementRules.ValidateRequest(boundRequest).IsValid)
        {
            return ControllerResult<AuthenticatedDeletionPushReceipt>.Failure(
                ControllerError.Create(
                    ControllerErrorCode.IntegrityFailure,
                    "sync.deletion.bound-push-invalid"));
        }

        var pushed = await _transport
            .PushAsync(context, state.Authorization!.Value, boundRequest, cancellationToken)
            .ConfigureAwait(false);
        if (!pushed.IsSuccess)
            return ControllerResult<AuthenticatedDeletionPushReceipt>.Failure(pushed.Error!);
        if (!DeletionAcknowledgementRules.ValidateSignedAcknowledgement(pushed.Value).IsValid)
        {
            return ControllerResult<AuthenticatedDeletionPushReceipt>.Failure(
                ControllerError.Create(
                    ControllerErrorCode.IntegrityFailure,
                    "sync.deletion.signed-receipt-invalid"));
        }

        var verified = await _acknowledgementVerifier
            .VerifyPushAsync(context, boundRequest, pushed.Value!, cancellationToken)
            .ConfigureAwait(false);
        if (!verified.IsSuccess)
            return ControllerResult<AuthenticatedDeletionPushReceipt>.Failure(verified.Error!);
        if (!DeletionAcknowledgementRules.ValidateReceipt(boundRequest, verified.Value).IsValid)
        {
            return ControllerResult<AuthenticatedDeletionPushReceipt>.Failure(
                ControllerError.Create(
                    ControllerErrorCode.IntegrityFailure,
                    "sync.deletion.verified-receipt-invalid"));
        }

        return ControllerResult<AuthenticatedDeletionPushReceipt>.Success(verified.Value!);
    }

    private async ValueTask<ControllerResult> ObservePropagationAsync(
        SyncOperationContext context,
        OperationState state,
        CancellationToken cancellationToken)
    {
        if (state.PurgeReceipt is null)
        {
            return Fail(
                state,
                DataDeletionStageKind.AwaitPropagation,
                ControllerError.Create(
                    ControllerErrorCode.IntegrityFailure,
                    "sync.deletion.purge-receipt-required"));
        }

        state.Run(DataDeletionStageKind.AwaitPropagation, _timeProvider.GetUtcNow());
        var observed = await _transport
            .GetPropagationStatusAsync(
                context,
                state.Authorization!.Value,
                state.PurgeReceipt,
                cancellationToken)
            .ConfigureAwait(false);
        if (!observed.IsSuccess)
            return Fail(state, DataDeletionStageKind.AwaitPropagation, observed.Error!);
        if (!DeletionAcknowledgementRules.ValidateSignedPropagationStatus(observed.Value).IsValid)
        {
            return Fail(
                state,
                DataDeletionStageKind.AwaitPropagation,
                ControllerError.Create(
                    ControllerErrorCode.IntegrityFailure,
                    "sync.deletion.signed-status-invalid"));
        }

        var verified = await _acknowledgementVerifier
            .VerifyStatusAsync(context, state.PurgeReceipt, observed.Value!, cancellationToken)
            .ConfigureAwait(false);
        if (!verified.IsSuccess)
            return Fail(state, DataDeletionStageKind.AwaitPropagation, verified.Error!);
        if (!DeletionAcknowledgementRules
            .ValidatePropagationStatus(state.PurgeReceipt, verified.Value)
            .IsValid)
        {
            return Fail(
                state,
                DataDeletionStageKind.AwaitPropagation,
                ControllerError.Create(
                    ControllerErrorCode.IntegrityFailure,
                    "sync.deletion.verified-status-invalid"));
        }

        state.VerifiedPropagationStatus = verified.Value;
        var categoryFailure = verified.Value!.Categories.FirstOrDefault(value =>
            value.State == DeletionCategoryPropagationState.Failed);
        if (categoryFailure is not null)
        {
            return Fail(
                state,
                DataDeletionStageKind.AwaitPropagation,
                categoryFailure.Error ?? ControllerError.Create(
                    ControllerErrorCode.Unavailable,
                    "sync.deletion.propagation-failed",
                    isRetryable: true));
        }

        if (verified.Value.IsComplete)
        {
            state.Succeed(DataDeletionStageKind.AwaitPropagation, _timeProvider.GetUtcNow());
            state.Succeed(DataDeletionStageKind.Complete, _timeProvider.GetUtcNow());
            state.PropagationComplete = true;
        }
        else
        {
            state.Pending(DataDeletionStageKind.AwaitPropagation);
            state.Pending(DataDeletionStageKind.Complete);
            state.PropagationComplete = false;
        }

        return ControllerResult.Success();
    }

    private static SyncIdempotencyKey CreateIdempotencyKey(
        SyncOperationId operationId,
        string phase)
    {
        var source = System.Text.Encoding.UTF8.GetBytes(
            $"orbit-navigator|deletion|{operationId.Value:N}|{phase}");
        Span<byte> digest = stackalloc byte[32];
        System.Security.Cryptography.SHA256.HashData(source, digest);
        return new SyncIdempotencyKey(new Guid(digest[..16]));
    }

    private async ValueTask<ControllerResult<CanonicalSyncAad>> CreateAadAsync(
        SyncOperationContext context,
        OperationState state,
        DeletionSyncSession session,
        SyncRecordKind kind,
        SyncDataCategory category,
        SyncEntityId entityId,
        SyncOperationId? operationId,
        CancellationToken cancellationToken)
    {
        var scope = new SyncStateScope(
            state.ProfileId,
            session.Fence.DeviceId,
            session.Fence.ClientGeneration);
        var reserved = await _sequenceAllocator
            .ReserveNextAsync(context, scope, cancellationToken)
            .ConfigureAwait(false);
        if (!reserved.IsSuccess)
            return ControllerResult<CanonicalSyncAad>.Failure(reserved.Error!);
        if (reserved.Value!.Scope != scope || reserved.Value.Sequence < 0)
        {
            return ControllerResult<CanonicalSyncAad>.Failure(
                ControllerError.Create(
                    ControllerErrorCode.IntegrityFailure,
                    "sync.deletion.sequence-reservation-invalid"));
        }

        return ControllerResult<CanonicalSyncAad>.Success(new CanonicalSyncAad(
            SyncProtocol.CurrentProtocolVersion,
            SyncProtocol.CurrentSchemaVersion,
            state.ProfileId,
            session.Fence.DeviceId,
            session.KeysetId,
            session.KeyEpoch,
            kind,
            new SyncEnvelopeId(Guid.NewGuid()),
            category,
            entityId,
            operationId,
            session.Fence.ClientGeneration,
            reserved.Value.Sequence));
    }

    private async ValueTask<ControllerResult<OperationState>> GetOrCreateStateAsync(
        SyncOperationContext context,
        DataDeletionRequest request,
        LocalDataClearRequest localRequest,
        CancellationToken cancellationToken)
    {
        if (_operations.TryGetValue(request.OperationId, out var existing))
        {
            if (!existing.Matches(context, request))
                return ControllerResult<OperationState>.Failure(OperationConflictError());

            existing.RefreshAuthorization(request.Authorization);
            return ControllerResult<OperationState>.Success(existing);
        }

        var loaded = await _journal.LoadAsync(context, request.OperationId, cancellationToken)
            .ConfigureAwait(false);
        if (!loaded.IsSuccess)
            return ControllerResult<OperationState>.Failure(loaded.Error!);

        OperationState candidate;
        if (loaded.Value!.Found)
        {
            var checkpoint = loaded.Value.Checkpoint!;
            var parsed = DeletionOperationCheckpointCodec.Deserialize(checkpoint.SerializedState.Span);
            if (!parsed.IsSuccess)
                return ControllerResult<OperationState>.Failure(parsed.Error!);
            candidate = OperationState.Restore(
                context,
                request.Authorization,
                localRequest,
                parsed.Value!,
                checkpoint.Revision);
        }
        else
        {
            candidate = new OperationState(context, request, localRequest, _timeProvider.GetUtcNow());
        }

        var state = _operations.GetOrAdd(request.OperationId, candidate);
        if (!state.Matches(context, request))
            return ControllerResult<OperationState>.Failure(OperationConflictError());

        state.RefreshAuthorization(request.Authorization);
        return ControllerResult<OperationState>.Success(state);
    }

    private async ValueTask<ControllerResult<OperationState>> LoadStateForStatusAsync(
        SyncOperationContext context,
        DataDeletionStatusRequest request,
        CancellationToken cancellationToken)
    {
        var loaded = await _journal.LoadAsync(context, request.OperationId, cancellationToken)
            .ConfigureAwait(false);
        if (!loaded.IsSuccess)
            return ControllerResult<OperationState>.Failure(loaded.Error!);
        if (!loaded.Value!.Found)
            return ControllerResult<OperationState>.Failure(OperationNotFoundError());

        var checkpoint = loaded.Value.Checkpoint!;
        var parsed = DeletionOperationCheckpointCodec.Deserialize(checkpoint.SerializedState.Span);
        if (!parsed.IsSuccess)
            return ControllerResult<OperationState>.Failure(parsed.Error!);
        var localRequest = CreateLocalRequest(
            context,
            parsed.Value!.LocalCategories.ToHashSet());
        if (!localRequest.IsSuccess)
            return ControllerResult<OperationState>.Failure(localRequest.Error!);

        var candidate = OperationState.Restore(
            context,
            request.Authorization,
            localRequest.Value!,
            parsed.Value,
            checkpoint.Revision);
        var state = _operations.GetOrAdd(request.OperationId, candidate);
        return ControllerResult<OperationState>.Success(state);
    }

    private async ValueTask<ControllerResult> CheckpointAsync(
        SyncOperationContext context,
        OperationState state,
        CancellationToken cancellationToken)
    {
        var serialized = DeletionOperationCheckpointCodec.Serialize(state.SnapshotCheckpoint());
        if (!serialized.IsSuccess)
            return ControllerResult.Failure(serialized.Error!);

        var checkpoint = new DeletionJournalCheckpoint(
            state.ProfileId,
            state.OperationId,
            DeletionOperationCheckpointCodec.CurrentFormatVersion,
            serialized.Value!,
            state.JournalRevision);
        var written = await _journal.WriteAsync(context, checkpoint, cancellationToken)
            .ConfigureAwait(false);
        if (!written.IsSuccess)
            return ControllerResult.Failure(written.Error!);

        state.JournalRevision = written.Value!.Revision;
        return ControllerResult.Success();
    }

    private static ControllerResult<LocalDataClearRequest> CreateLocalRequest(
        SyncOperationContext context,
        IReadOnlySet<LocalDataCategory> categories)
    {
        var mapped = DataDeletionNamespaceMap.Map(categories);
        if (!mapped.IsSuccess)
            return ControllerResult<LocalDataClearRequest>.Failure(mapped.Error!);

        return LocalDataClearRequest.Create(
            context.Browsing.Privacy,
            mapped.Value!,
            ProfileStorageDurability.Persistent);
    }

    private static ControllerError? Validate(
        SyncOperationContext? context,
        DataDeletionRequest? request)
    {
        if (context is null || request is null || request.Selection is null)
            return InvalidRequestError();

        if (request.Selection.LocalCategories is null || request.Selection.SyncedCategories is null)
            return InvalidRequestError();

        if (request.Selection.LocalCategories.Count == 0 && request.Selection.SyncedCategories.Count == 0)
            return InvalidRequestError();

        if (request.Selection.LocalCategories.Any(category => !Enum.IsDefined(category)) ||
            request.Selection.SyncedCategories.Any(category => !Enum.IsDefined(category)))
        {
            return InvalidRequestError();
        }

        var contractValidation = DataDeletionRules.ValidateRequest(context, request);
        if (!contractValidation.IsValid)
        {
            if (request.Fence is { ClientGeneration: var generation, MinimumAcceptedGeneration: var minimum } &&
                generation < minimum)
            {
                return StaleFenceError();
            }

            return InvalidRequestError();
        }

        return null;
    }

    private static bool ValidLocalReceipt(
        LocalDataClearRequest request,
        LocalDataClearReceipt receipt)
    {
        if (receipt.DownloadedFilesWereDeleted || receipt.ClearedNamespaces is null)
            return false;

        var cleared = receipt.ClearedNamespaces.ToHashSet();
        return request.Namespaces.All(cleared.Contains);
    }

    private ControllerResult<T> Fail<T>(
        OperationState state,
        DataDeletionStageKind stage,
        ControllerError error)
        where T : class
    {
        state.Fail(stage, error, _timeProvider.GetUtcNow());
        return ControllerResult<T>.Failure(error);
    }

    private ControllerResult Fail(
        OperationState state,
        DataDeletionStageKind stage,
        ControllerError error)
    {
        state.Fail(stage, error, _timeProvider.GetUtcNow());
        return ControllerResult.Failure(error);
    }

    private static ControllerError InvalidRequestError() =>
        ControllerError.Create(
            ControllerErrorCode.InvalidRequest,
            "sync.deletion.request-invalid");

    private static ControllerError StaleFenceError() =>
        ControllerError.Create(
            ControllerErrorCode.StaleClient,
            "sync.deletion.stale-fence");

    private static ControllerError OperationConflictError() =>
        ControllerError.Create(
            ControllerErrorCode.Conflict,
            "sync.deletion.operation-conflict");

    private static ControllerError OperationNotFoundError() =>
        ControllerError.Create(
            ControllerErrorCode.NotFound,
            "sync.deletion.operation-not-found");

    private static ControllerError DownloadedFilesIntegrityError() =>
        ControllerError.Create(
            ControllerErrorCode.IntegrityFailure,
            "sync.deletion.downloaded-files-not-preserved");

    private static ControllerError CancelledError() =>
        ControllerError.Create(
            ControllerErrorCode.Cancelled,
            "sync.deletion.cancelled",
            isRetryable: true);

    private static ControllerError InternalError() =>
        ControllerError.Create(
            ControllerErrorCode.InternalFailure,
            "sync.deletion.internal-failure",
            isRetryable: true);

    private sealed class OperationState
    {
        private readonly object _statusLock = new();
        private readonly Dictionary<DataDeletionStageKind, DataDeletionStageStatus> _stages;
        private readonly LocalDataCategory[] _localCategories;
        private readonly SyncDataCategory[] _syncedCategories;
        private OpaqueAuthHandle? _authorization;

        public OperationState(
            SyncOperationContext context,
            DataDeletionRequest request,
            LocalDataClearRequest localRequest,
            DateTimeOffset createdAtUtc)
        {
            OperationId = request.OperationId;
            ProfileId = context.Browsing.Privacy.ProfileId;
            _authorization = request.Authorization;
            RequestedFence = request.Fence;
            LocalRequest = localRequest;
            _localCategories = request.Selection.LocalCategories.Distinct().Order().ToArray();
            _syncedCategories = request.Selection.SyncedCategories.Distinct().Order().ToArray();
            LocalCategories = _localCategories.ToFrozenSet();
            SyncedCategories = _syncedCategories.ToFrozenSet();
            _stages = OrderedStages.ToDictionary(
                stage => stage,
                stage => new DataDeletionStageStatus(
                    stage,
                    DataDeletionStageState.Pending,
                    stage == DataDeletionStageKind.Validate ? createdAtUtc : null,
                    null));
        }

        private OperationState(
            ProfileId profileId,
            SyncOperationId operationId,
            OpaqueAuthHandle? authorization,
            ClientFence? requestedFence,
            LocalDataClearRequest localRequest,
            IReadOnlyList<LocalDataCategory> localCategories,
            IReadOnlyList<SyncDataCategory> syncedCategories,
            IReadOnlyList<DataDeletionStageStatus> stages,
            ProfileStorageRevision? journalRevision)
        {
            ProfileId = profileId;
            OperationId = operationId;
            _authorization = authorization;
            RequestedFence = requestedFence;
            LocalRequest = localRequest;
            _localCategories = localCategories.Distinct().Order().ToArray();
            _syncedCategories = syncedCategories.Distinct().Order().ToArray();
            LocalCategories = _localCategories.ToFrozenSet();
            SyncedCategories = _syncedCategories.ToFrozenSet();
            _stages = stages.ToDictionary(
                value => value.Stage,
                value => value.State == DataDeletionStageState.Running
                    ? value with { State = DataDeletionStageState.Pending, UpdatedAtUtc = null, Error = null }
                    : value);
            JournalRevision = journalRevision;
        }

        public static OperationState Restore(
            SyncOperationContext context,
            OpaqueAuthHandle? authorization,
            LocalDataClearRequest localRequest,
            DeletionOperationCheckpointModel checkpoint,
            ProfileStorageRevision? journalRevision)
        {
            if (checkpoint.ProfileId != context.Browsing.Privacy.ProfileId ||
                checkpoint.OperationId != context.OperationId)
            {
                throw new InvalidOperationException("A deletion checkpoint was restored into a different context.");
            }

            var state = new OperationState(
                checkpoint.ProfileId,
                checkpoint.OperationId,
                authorization,
                checkpoint.RequestedFence,
                localRequest,
                checkpoint.LocalCategories,
                checkpoint.SyncedCategories,
                checkpoint.Stages,
                journalRevision)
            {
                Entities = checkpoint.Entities.Count == checkpoint.SyncedCategories.Count
                    ? checkpoint.Entities
                    : null,
                Tombstones = checkpoint.Tombstones,
                Purges = checkpoint.Purges,
                TombstoneReceipt = checkpoint.TombstoneReceipt,
                PurgeReceipt = checkpoint.PurgeReceipt,
                VerifiedPropagationStatus = checkpoint.VerifiedPropagationStatus,
                PurgesPublished = checkpoint.PurgesPublished,
                PropagationComplete = checkpoint.PropagationComplete,
            };
            return state;
        }

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public SyncOperationId OperationId { get; }

        public ProfileId ProfileId { get; }

        public OpaqueAuthHandle? Authorization
        {
            get
            {
                lock (_statusLock)
                    return _authorization;
            }
        }

        public ClientFence? RequestedFence { get; }

        public LocalDataClearRequest LocalRequest { get; }

        public IReadOnlySet<LocalDataCategory> LocalCategories { get; }

        public IReadOnlySet<SyncDataCategory> SyncedCategories { get; }

        public bool HasSyncedCategories => _syncedCategories.Length > 0;

        public DeletionSyncSession? SyncSession { get; set; }

        public IReadOnlyDictionary<SyncDataCategory, IReadOnlyList<SyncEntityId>>? Entities { get; set; }

        public IReadOnlyList<EncryptedSyncTombstone>? Tombstones { get; set; }

        public IReadOnlyList<EncryptedPurgeCommand>? Purges { get; set; }

        public AuthenticatedDeletionPushReceipt? TombstoneReceipt { get; set; }

        public AuthenticatedDeletionPushReceipt? PurgeReceipt { get; set; }

        public AuthenticatedDeletionPropagationStatus? VerifiedPropagationStatus { get; set; }

        public bool PurgesPublished { get; set; }

        public bool PropagationComplete { get; set; }

        public ProfileStorageRevision? JournalRevision { get; set; }

        public bool IsComplete => IsSucceeded(DataDeletionStageKind.Complete);

        public bool Matches(SyncOperationContext context, DataDeletionRequest request) =>
            ProfileId == context.Browsing.Privacy.ProfileId &&
            RequestedFence == request.Fence &&
            _localCategories.SequenceEqual(request.Selection.LocalCategories.Distinct().Order()) &&
            _syncedCategories.SequenceEqual(request.Selection.SyncedCategories.Distinct().Order());

        public void RefreshAuthorization(OpaqueAuthHandle? authorization)
        {
            lock (_statusLock)
                _authorization = authorization;
        }

        public bool IsSucceeded(DataDeletionStageKind stage)
        {
            lock (_statusLock)
                return _stages[stage].State == DataDeletionStageState.Succeeded;
        }

        public void Run(DataDeletionStageKind stage, DateTimeOffset updatedAtUtc) =>
            Set(stage, DataDeletionStageState.Running, updatedAtUtc, null);

        public void Pending(DataDeletionStageKind stage) =>
            Set(stage, DataDeletionStageState.Pending, null, null);

        public void Succeed(DataDeletionStageKind stage, DateTimeOffset updatedAtUtc) =>
            Set(stage, DataDeletionStageState.Succeeded, updatedAtUtc, null);

        public void Fail(
            DataDeletionStageKind stage,
            ControllerError error,
            DateTimeOffset updatedAtUtc) =>
            Set(stage, DataDeletionStageState.Failed, updatedAtUtc, error);

        public DataDeletionProgress SnapshotProgress()
        {
            lock (_statusLock)
            {
                return new DataDeletionProgress(
                    OperationId,
                    HasSyncedCategories
                        ? PurgeReceipt?.Fence ?? TombstoneReceipt?.Fence ?? SyncSession?.Fence ?? RequestedFence
                        : null,
                    OrderedStages.Select(stage => _stages[stage]).ToArray(),
                    PropagationComplete,
                    DownloadedFilesDisposition.Preserved);
            }
        }

        public DeletionOperationCheckpointModel SnapshotCheckpoint()
        {
            lock (_statusLock)
            {
                return new DeletionOperationCheckpointModel(
                    ProfileId,
                    OperationId,
                    _localCategories,
                    _syncedCategories,
                    RequestedFence,
                    OrderedStages.Select(stage => _stages[stage]).ToArray(),
                    Entities ?? new Dictionary<SyncDataCategory, IReadOnlyList<SyncEntityId>>(),
                    Tombstones,
                    Purges,
                    TombstoneReceipt,
                    PurgeReceipt,
                    VerifiedPropagationStatus,
                    PurgesPublished,
                    PropagationComplete);
            }
        }

        private void Set(
            DataDeletionStageKind stage,
            DataDeletionStageState status,
            DateTimeOffset? updatedAtUtc,
            ControllerError? error)
        {
            lock (_statusLock)
                _stages[stage] = new DataDeletionStageStatus(stage, status, updatedAtUtc, error);
        }
    }
}
