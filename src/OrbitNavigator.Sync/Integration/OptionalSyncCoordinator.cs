using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Sync;
using OrbitNavigator.Sync.State;

namespace OrbitNavigator.Sync.Integration;

/// <summary>
/// Runs one bounded optional-sync pass. The only public entry point accepts an
/// ordinary browsing context and enters every dependency through SyncOperationGate.
/// </summary>
public sealed class OptionalSyncCoordinator
{
    public const int MaximumItemsPerPage = 250;
    public const int MaximumPushPagesPerRun = 32;
    public const int MaximumPullPagesPerRun = 32;

    private readonly IOptionalSyncSessionProvider _sessionProvider;
    private readonly ILocalSyncChangeCatalog _changeCatalog;
    private readonly ISyncRecordProjector _projector;
    private readonly ISyncEnvelopeCodec _codec;
    private readonly ISyncTransport _transport;
    private readonly IAuthenticatedSyncApplyTarget _applyTarget;
    private readonly IDurableSyncCheckpointStore _checkpointStore;
    private readonly IDurableSyncSequenceAllocator _sequenceAllocator;

    public OptionalSyncCoordinator(
        IOptionalSyncSessionProvider sessionProvider,
        ILocalSyncChangeCatalog changeCatalog,
        ISyncRecordProjector projector,
        ISyncEnvelopeCodec codec,
        ISyncTransport transport,
        IAuthenticatedSyncApplyTarget applyTarget,
        IDurableSyncCheckpointStore checkpointStore,
        IDurableSyncSequenceAllocator sequenceAllocator)
    {
        _sessionProvider = sessionProvider ?? throw new ArgumentNullException(nameof(sessionProvider));
        _changeCatalog = changeCatalog ?? throw new ArgumentNullException(nameof(changeCatalog));
        _projector = projector ?? throw new ArgumentNullException(nameof(projector));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _applyTarget = applyTarget ?? throw new ArgumentNullException(nameof(applyTarget));
        _checkpointStore = checkpointStore ?? throw new ArgumentNullException(nameof(checkpointStore));
        _sequenceAllocator = sequenceAllocator ?? throw new ArgumentNullException(nameof(sequenceAllocator));
    }

    public ValueTask<ControllerResult<OptionalSyncRunReceipt>> SynchronizeAsync(
        BrowsingContext browsing,
        SyncOperationId operationId,
        CancellationToken cancellationToken = default)
    {
        if (browsing is null)
            return Invalid<OptionalSyncRunReceipt>("sync.integration.context-required");

        return SyncOperationGate.ExecuteAsync(
            browsing,
            operationId,
            context => SynchronizeAuthorizedAsync(context, cancellationToken));
    }

    private async ValueTask<ControllerResult<OptionalSyncRunReceipt>> SynchronizeAuthorizedAsync(
        SyncOperationContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sessionResult = await _sessionProvider
                .GetAuthorizedSessionAsync(context, cancellationToken)
                .ConfigureAwait(false);
            if (!sessionResult.IsSuccess)
                return ControllerResult<OptionalSyncRunReceipt>.Failure(sessionResult.Error!);

            var session = sessionResult.Value!;
            if (!session.IsDefined)
                return Integrity<OptionalSyncRunReceipt>("sync.integration.session-invalid");

            var scope = new SyncStateScope(
                context.Browsing.Privacy.ProfileId,
                session.DeviceId,
                session.Fence.ClientGeneration);
            var checkpointResult = await _checkpointStore
                .LoadAsync(context, scope, cancellationToken)
                .ConfigureAwait(false);
            if (!checkpointResult.IsSuccess)
                return ControllerResult<OptionalSyncRunReceipt>.Failure(checkpointResult.Error!);
            if (!ValidCheckpoint(checkpointResult.Value, scope))
                return Integrity<OptionalSyncRunReceipt>("sync.integration.checkpoint-invalid");

            var pushed = await PushAsync(
                    context,
                    session,
                    checkpointResult.Value!,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!pushed.IsSuccess)
                return ControllerResult<OptionalSyncRunReceipt>.Failure(pushed.Error!);

            var pulled = await PullAsync(
                    context,
                    session,
                    pushed.Value!.Checkpoint,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!pulled.IsSuccess)
                return ControllerResult<OptionalSyncRunReceipt>.Failure(pulled.Error!);

            return ControllerResult<OptionalSyncRunReceipt>.Success(
                new OptionalSyncRunReceipt(
                    SyncAvailabilityPolicy.Evaluate(hasAccountAuthorization: true),
                    pushed.Value.UpsertCount,
                    pushed.Value.TombstoneCount,
                    pulled.Value!.UpsertCount,
                    pulled.Value.TombstoneCount,
                    pulled.Value.PurgeCount,
                    pulled.Value.PageCount,
                    pulled.Value.Checkpoint.PulledThrough));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled<OptionalSyncRunReceipt>();
        }
        catch (Exception)
        {
            return ControllerResult<OptionalSyncRunReceipt>.Failure(
                ControllerError.Create(
                    ControllerErrorCode.InternalFailure,
                    "sync.integration.internal-failure",
                    isRetryable: true));
        }
    }

    private async ValueTask<ControllerResult<PushProgress>> PushAsync(
        SyncOperationContext context,
        OptionalSyncSession session,
        SyncCheckpoint initialCheckpoint,
        CancellationToken cancellationToken)
    {
        var checkpoint = initialCheckpoint;
        var upsertCount = 0;
        var tombstoneCount = 0;

        for (var pageNumber = 0; pageNumber < MaximumPushPagesPerRun; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pending = await _changeCatalog
                .ListPendingAsync(
                    context,
                    checkpoint.PushedThrough,
                    MaximumItemsPerPage,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!pending.IsSuccess)
                return ControllerResult<PushProgress>.Failure(pending.Error!);

            var page = pending.Value!;
            var pageError = ValidateLocalPage(page, checkpoint.PushedThrough);
            if (pageError is not null)
                return ControllerResult<PushProgress>.Failure(pageError);
            if (page.Changes.Count == 0)
                return ControllerResult<PushProgress>.Success(
                    new PushProgress(checkpoint, upsertCount, tombstoneCount));

            var envelopes = new List<EncryptedSyncEnvelope>();
            var tombstones = new List<EncryptedSyncTombstone>();
            foreach (var change in page.Changes)
            {
                var reservation = await _sequenceAllocator
                    .ReserveNextAsync(context, checkpoint.Scope, cancellationToken)
                    .ConfigureAwait(false);
                if (!reservation.IsSuccess)
                    return ControllerResult<PushProgress>.Failure(reservation.Error!);
                if (reservation.Value!.Scope != checkpoint.Scope || reservation.Value.Sequence < 0)
                    return Integrity<PushProgress>("sync.integration.sequence-invalid");

                var aad = CreateLocalAad(context, session, change, reservation.Value.Sequence);
                if (change.RecordKind == SyncRecordKind.Upsert)
                {
                    var projected = await _projector
                        .ProjectAsync(context, change.Category, change.EntityId, cancellationToken)
                        .ConfigureAwait(false);
                    if (!projected.IsSuccess)
                        return ControllerResult<PushProgress>.Failure(projected.Error!);
                    if (!ValidProjectedRecord(projected.Value, change))
                        return Integrity<PushProgress>("sync.integration.projection-invalid");

                    var encrypted = await _codec
                        .EncryptAsync(context, session.KeyMaterial, aad, projected.Value!, cancellationToken)
                        .ConfigureAwait(false);
                    if (!encrypted.IsSuccess)
                        return ControllerResult<PushProgress>.Failure(encrypted.Error!);
                    if (!SyncContractRules.ValidateEnvelope(encrypted.Value).IsValid || encrypted.Value!.Aad != aad)
                        return Integrity<PushProgress>("sync.integration.envelope-invalid");
                    envelopes.Add(encrypted.Value);
                }
                else
                {
                    var encrypted = await _codec
                        .EncryptTombstoneAsync(context, session.KeyMaterial, aad, cancellationToken)
                        .ConfigureAwait(false);
                    if (!encrypted.IsSuccess)
                        return ControllerResult<PushProgress>.Failure(encrypted.Error!);
                    if (!SyncContractRules.ValidateTombstone(encrypted.Value).IsValid || encrypted.Value!.Aad != aad)
                        return Integrity<PushProgress>("sync.integration.tombstone-invalid");
                    tombstones.Add(encrypted.Value);
                }
            }

            var request = new SyncPushRequest(
                session.DeviceId,
                session.Fence,
                envelopes,
                tombstones,
                Array.Empty<EncryptedPurgeCommand>());
            if (!SyncTransferRules.ValidatePush(request).IsValid ||
                !request.Envelopes.All(value => ValidLocalAad(value.Aad, context, session)) ||
                !request.Tombstones.All(value => ValidLocalAad(value.Aad, context, session)))
            {
                return Integrity<PushProgress>("sync.integration.push-invalid");
            }

            var pushed = await _transport
                .PushAsync(context, session.Authorization, request, cancellationToken)
                .ConfigureAwait(false);
            if (!pushed.IsSuccess)
                return ControllerResult<PushProgress>.Failure(pushed.Error!);
            if (!ValidPushReceipt(pushed.Value, session.Fence, envelopes.Count, tombstones.Count))
                return Integrity<PushProgress>("sync.integration.push-receipt-invalid");

            var committed = await _checkpointStore
                .CommitPushAsync(context, checkpoint, page.NextCursor!, cancellationToken)
                .ConfigureAwait(false);
            if (!committed.IsSuccess)
                return ControllerResult<PushProgress>.Failure(committed.Error!);
            if (!ValidPushAdvance(checkpoint, committed.Value, page.NextCursor!))
                return Integrity<PushProgress>("sync.integration.push-checkpoint-invalid");

            checkpoint = committed.Value!;
            upsertCount += envelopes.Count;
            tombstoneCount += tombstones.Count;
            if (!page.HasMore)
                return ControllerResult<PushProgress>.Success(
                    new PushProgress(checkpoint, upsertCount, tombstoneCount));
        }

        return Unavailable<PushProgress>("sync.integration.push-page-limit");
    }

    private async ValueTask<ControllerResult<PullProgress>> PullAsync(
        SyncOperationContext context,
        OptionalSyncSession session,
        SyncCheckpoint initialCheckpoint,
        CancellationToken cancellationToken)
    {
        var checkpoint = initialCheckpoint;
        var upsertCount = 0;
        var tombstoneCount = 0;
        var purgeCount = 0;
        var pageCount = 0;

        for (var pageNumber = 0; pageNumber < MaximumPullPagesPerRun; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = new SyncPullRequest(
                session.DeviceId,
                session.Fence,
                checkpoint.PulledThrough,
                MaximumItemsPerPage);
            if (!SyncTransferRules.ValidatePull(request).IsValid)
                return Integrity<PullProgress>("sync.integration.pull-request-invalid");

            var pulled = await _transport
                .PullAsync(context, session.Authorization, request, cancellationToken)
                .ConfigureAwait(false);
            if (!pulled.IsSuccess)
                return ControllerResult<PullProgress>.Failure(pulled.Error!);

            var page = pulled.Value!;
            var pageError = ValidatePullPage(page, checkpoint.PulledThrough, session, context);
            if (pageError is not null)
                return ControllerResult<PullProgress>.Failure(pageError);

            var authenticated = await AuthenticatePageAsync(
                    context,
                    session,
                    page,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!authenticated.IsSuccess)
                return ControllerResult<PullProgress>.Failure(authenticated.Error!);

            var applied = await _applyTarget
                .ApplyPageAsync(context, authenticated.Value!, cancellationToken)
                .ConfigureAwait(false);
            if (!applied.IsSuccess)
                return ControllerResult<PullProgress>.Failure(applied.Error!);
            if (!ValidApplyReceipt(applied.Value, authenticated.Value!))
                return Integrity<PullProgress>("sync.integration.apply-receipt-invalid");

            var committed = await _checkpointStore
                .CommitPullAsync(context, checkpoint, page.Cursor, cancellationToken)
                .ConfigureAwait(false);
            if (!committed.IsSuccess)
                return ControllerResult<PullProgress>.Failure(committed.Error!);
            if (!ValidPullAdvance(checkpoint, committed.Value, page.Cursor))
                return Integrity<PullProgress>("sync.integration.pull-checkpoint-invalid");

            checkpoint = committed.Value!;
            pageCount++;
            upsertCount += authenticated.Value!.Upserts.Count;
            tombstoneCount += authenticated.Value.Tombstones.Count;
            purgeCount += authenticated.Value.Purges.Count;
            if (!page.HasMore)
            {
                return ControllerResult<PullProgress>.Success(
                    new PullProgress(
                        checkpoint,
                        upsertCount,
                        tombstoneCount,
                        purgeCount,
                        pageCount));
            }
        }

        return Unavailable<PullProgress>("sync.integration.pull-page-limit");
    }

    private async ValueTask<ControllerResult<AuthenticatedSyncPage>> AuthenticatePageAsync(
        SyncOperationContext context,
        OptionalSyncSession session,
        SyncPullPage page,
        CancellationToken cancellationToken)
    {
        var upserts = new List<AuthenticatedRemoteUpsert>(page.Envelopes.Count);
        foreach (var envelope in page.Envelopes)
        {
            var decrypted = await _codec
                .DecryptAsync(context, session.KeyMaterial, envelope, cancellationToken)
                .ConfigureAwait(false);
            if (!decrypted.IsSuccess)
                return ControllerResult<AuthenticatedSyncPage>.Failure(decrypted.Error!);
            if (!ValidDecryptedRecord(envelope.Aad, decrypted.Value))
                return Integrity<AuthenticatedSyncPage>("sync.integration.remote-record-invalid");
            upserts.Add(new AuthenticatedRemoteUpsert(envelope.Aad, decrypted.Value!));
        }

        var tombstones = new List<AuthenticatedRemoteTombstone>(page.Tombstones.Count);
        foreach (var tombstone in page.Tombstones)
        {
            var authenticated = await _codec
                .DecryptAndValidateTombstoneAsync(
                    context,
                    session.KeyMaterial,
                    tombstone,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!authenticated.IsSuccess)
                return ControllerResult<AuthenticatedSyncPage>.Failure(authenticated.Error!);
            if (!SyncContractRules.ValidateAuthenticatedTombstone(tombstone, authenticated.Value).IsValid)
                return Integrity<AuthenticatedSyncPage>("sync.integration.remote-tombstone-invalid");
            tombstones.Add(new AuthenticatedRemoteTombstone(tombstone.Aad, authenticated.Value!));
        }

        var purges = new List<AuthenticatedRemotePurge>(page.Purges.Count);
        foreach (var purge in page.Purges)
        {
            var decrypted = await _codec
                .DecryptAndValidatePurgeAsync(
                    context,
                    session.KeyMaterial,
                    purge,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!decrypted.IsSuccess)
                return ControllerResult<AuthenticatedSyncPage>.Failure(decrypted.Error!);
            if (!SyncContractRules.ValidatePurgeMarker(purge.Aad, decrypted.Value).IsValid)
                return Integrity<AuthenticatedSyncPage>("sync.integration.remote-purge-invalid");
            purges.Add(new AuthenticatedRemotePurge(purge.Aad, decrypted.Value!));
        }

        return ControllerResult<AuthenticatedSyncPage>.Success(
            new AuthenticatedSyncPage(
                page.Cursor,
                page.Fence,
                upserts,
                tombstones,
                purges));
    }

    private static ControllerError? ValidateLocalPage(
        LocalSyncChangePage? page,
        LocalChangeCursor? previous)
    {
        if (page?.Changes is null || page.Changes.Count > MaximumItemsPerPage)
            return IntegrityError("sync.integration.local-page-invalid");
        if (page.Changes.Count == 0)
            return page.HasMore
                ? IntegrityError("sync.integration.local-page-empty-loop")
                : null;
        if (page.NextCursor is not { IsDefined: true } || page.NextCursor == previous)
            return IntegrityError("sync.integration.local-cursor-invalid");
        if (page.Changes.Any(change =>
                change is null ||
                change.RecordKind is not (SyncRecordKind.Upsert or SyncRecordKind.Tombstone) ||
                !SyncAllowlist.IsAllowed(change.Category) ||
                !change.EntityId.IsDefined))
        {
            return IntegrityError("sync.integration.local-change-invalid");
        }

        if (page.Changes
            .Select(change => (change.RecordKind, change.Category, change.EntityId))
            .Distinct()
            .Count() != page.Changes.Count)
        {
            return IntegrityError("sync.integration.local-change-duplicate");
        }

        return null;
    }

    private static ControllerError? ValidatePullPage(
        SyncPullPage? page,
        SyncCursor? previous,
        OptionalSyncSession session,
        SyncOperationContext context)
    {
        if (page is null)
            return IntegrityError("sync.integration.pull-page-invalid");
        if (page.Fence != session.Fence)
            return StaleFenceError();
        if (!SyncStateValidation.IsDefined(page.Cursor) ||
            page.Envelopes is null ||
            page.Tombstones is null ||
            page.Purges is null ||
            (long)page.Envelopes.Count + page.Tombstones.Count + page.Purges.Count > MaximumItemsPerPage ||
            (previous is not null && page.Cursor == previous))
        {
            return IntegrityError("sync.integration.pull-page-invalid");
        }

        if (page.Envelopes.Any(value => !SyncContractRules.ValidateEnvelope(value).IsValid) ||
            page.Tombstones.Any(value => !SyncContractRules.ValidateTombstone(value).IsValid) ||
            page.Purges.Any(value => !SyncContractRules.ValidatePurgeCommand(value).IsValid))
        {
            return IntegrityError("sync.integration.pull-shape-invalid");
        }

        var allAad = page.Envelopes.Select(value => value.Aad)
            .Concat(page.Tombstones.Select(value => value.Aad))
            .Concat(page.Purges.Select(value => value.Aad))
            .ToArray();
        if (allAad.Any(aad => !ValidRemoteAad(aad, context, session)) ||
            allAad.Select(aad => aad.EnvelopeId).Distinct().Count() != allAad.Length)
        {
            return IntegrityError("sync.integration.pull-identity-invalid");
        }

        return null;
    }

    private static bool ValidRemoteAad(
        CanonicalSyncAad aad,
        SyncOperationContext context,
        OptionalSyncSession session) =>
        aad.ProfileId == context.Browsing.Privacy.ProfileId &&
        aad.KeysetId == session.KeysetId &&
        aad.KeyEpoch == session.KeyEpoch &&
        aad.ClientGeneration >= session.Fence.MinimumAcceptedGeneration;

    private static bool ValidLocalAad(
        CanonicalSyncAad aad,
        SyncOperationContext context,
        OptionalSyncSession session) =>
        aad.ProfileId == context.Browsing.Privacy.ProfileId &&
        aad.DeviceId == session.DeviceId &&
        aad.KeysetId == session.KeysetId &&
        aad.KeyEpoch == session.KeyEpoch &&
        aad.ClientGeneration == session.Fence.ClientGeneration &&
        aad.OperationId is null;

    private static bool ValidProjectedRecord(SyncRecordPayload? record, LocalSyncChange change) =>
        record is not null &&
        SyncContractRules.ValidateRecord(record).IsValid &&
        record.Category == change.Category &&
        record.EntityId == change.EntityId;

    private static bool ValidDecryptedRecord(CanonicalSyncAad aad, SyncRecordPayload? record) =>
        record is not null &&
        SyncContractRules.ValidateRecord(record).IsValid &&
        record.Category == aad.Category &&
        record.EntityId == aad.EntityId;

    private static CanonicalSyncAad CreateLocalAad(
        SyncOperationContext context,
        OptionalSyncSession session,
        LocalSyncChange change,
        long sequence) =>
        new(
            SyncProtocol.CurrentProtocolVersion,
            SyncProtocol.CurrentSchemaVersion,
            context.Browsing.Privacy.ProfileId,
            session.DeviceId,
            session.KeysetId,
            session.KeyEpoch,
            change.RecordKind,
            new SyncEnvelopeId(Guid.NewGuid()),
            change.Category,
            change.EntityId,
            null,
            session.Fence.ClientGeneration,
            sequence);

    private static bool ValidPushReceipt(
        SyncPushReceipt? receipt,
        ClientFence fence,
        int envelopeCount,
        int tombstoneCount) =>
        receipt is not null &&
        SyncStateValidation.IsDefined(receipt.Cursor) &&
        receipt.Fence == fence &&
        receipt.AcceptedEnvelopeCount == envelopeCount &&
        receipt.AcceptedTombstoneCount == tombstoneCount &&
        receipt.AcceptedPurgeCount == 0;

    private static bool ValidApplyReceipt(
        AuthenticatedPageApplyReceipt? receipt,
        AuthenticatedSyncPage page) =>
        receipt is not null &&
        receipt.Cursor == page.Cursor &&
        receipt.AppliedUpsertCount == page.Upserts.Count &&
        receipt.AppliedTombstoneCount == page.Tombstones.Count &&
        receipt.AppliedPurgeCount == page.Purges.Count;

    private static bool ValidCheckpoint(SyncCheckpoint? checkpoint, SyncStateScope scope) =>
        checkpoint is { IsDefined: true } && checkpoint.Scope == scope;

    private static bool ValidPushAdvance(
        SyncCheckpoint previous,
        SyncCheckpoint? current,
        LocalChangeCursor expectedCursor) =>
        current is { IsDefined: true } &&
        current.Scope == previous.Scope &&
        current.Version > previous.Version &&
        current.PushedThrough == expectedCursor &&
        current.PulledThrough == previous.PulledThrough;

    private static bool ValidPullAdvance(
        SyncCheckpoint previous,
        SyncCheckpoint? current,
        SyncCursor expectedCursor) =>
        current is { IsDefined: true } &&
        current.Scope == previous.Scope &&
        current.Version > previous.Version &&
        current.PushedThrough == previous.PushedThrough &&
        current.PulledThrough == expectedCursor;

    private static ValueTask<ControllerResult<T>> Invalid<T>(string messageKey)
        where T : class =>
        ValueTask.FromResult(ControllerResult<T>.Failure(
            ControllerError.Create(ControllerErrorCode.InvalidRequest, messageKey)));

    private static ControllerResult<T> Integrity<T>(string messageKey)
        where T : class =>
        ControllerResult<T>.Failure(IntegrityError(messageKey));

    private static ControllerResult<T> Unavailable<T>(string messageKey)
        where T : class =>
        ControllerResult<T>.Failure(
            ControllerError.Create(
                ControllerErrorCode.Unavailable,
                messageKey,
                isRetryable: true));

    private static ControllerResult<T> Cancelled<T>()
        where T : class =>
        ControllerResult<T>.Failure(
            ControllerError.Create(
                ControllerErrorCode.Cancelled,
                "sync.integration.cancelled",
                isRetryable: true));

    private static ControllerError IntegrityError(string messageKey) =>
        ControllerError.Create(ControllerErrorCode.IntegrityFailure, messageKey);

    private static ControllerError StaleFenceError() =>
        ControllerError.Create(
            ControllerErrorCode.StaleClient,
            "sync.integration.stale-fence");

    private sealed record PushProgress(
        SyncCheckpoint Checkpoint,
        int UpsertCount,
        int TombstoneCount);

    private sealed record PullProgress(
        SyncCheckpoint Checkpoint,
        int UpsertCount,
        int TombstoneCount,
        int PurgeCount,
        int PageCount);
}
