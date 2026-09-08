using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Sync;
using OrbitNavigator.Foundation.Browser;

namespace OrbitNavigator.App.Sync;

/// <summary>
/// Projects current normal-profile browser data only when the encrypted sync
/// coordinator asks for an opaque entity already present in its local journal.
/// This adapter performs no network work and retains no projected record.
/// </summary>
internal sealed class BrowserDataSyncRecordProjector : ISyncRecordProjector
{
    private readonly IHistoryFacade _history;
    private readonly Func<BrowserWorkspaceSnapshot> _workspaceSnapshot;

    public BrowserDataSyncRecordProjector(
        IHistoryFacade history,
        Func<BrowserWorkspaceSnapshot> workspaceSnapshot)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _workspaceSnapshot = workspaceSnapshot ??
            throw new ArgumentNullException(nameof(workspaceSnapshot));
    }

    public ValueTask<ControllerResult<SyncRecordPayload>> ProjectAsync(
        SyncOperationContext context,
        SyncDataCategory category,
        SyncEntityId entityId,
        CancellationToken cancellationToken) =>
        category switch
        {
            SyncDataCategory.History => ProjectHistoryAsync(context, entityId, cancellationToken),
            SyncDataCategory.OpenTabs => ProjectOpenTabAsync(context, entityId),
            _ => ValueTask.FromResult(Invalid()),
        };

    private async ValueTask<ControllerResult<SyncRecordPayload>> ProjectHistoryAsync(
        SyncOperationContext context,
        SyncEntityId entityId,
        CancellationToken cancellationToken)
    {
        var validation = ValidateContext(context, entityId);
        if (validation is not null)
            return ControllerResult<SyncRecordPayload>.Failure(validation);

        var query = await _history.QueryAsync(
            new HistoryQuery(
                context.Browsing.Privacy,
                null,
                null,
                HistoryFacade.MaximumEntries),
            cancellationToken).ConfigureAwait(false);
        if (!query.IsSuccess)
            return ControllerResult<SyncRecordPayload>.Failure(query.Error!);
        var entry = query.Value!.SingleOrDefault(candidate => candidate.Id.Value == entityId.Value);
        if (entry is null || entry.Id.ProfileId != context.Browsing.Privacy.ProfileId)
            return NotFound();

        var record = new HistorySyncRecord(
            entityId,
            entry.LastVisitedAtUtc.UtcTicks,
            entry.LastVisitedAtUtc,
            entry.Target.AbsoluteUri,
            entry.Title,
            entry.LastVisitedAtUtc,
            entry.VisitCount);
        return Valid(record);
    }

    private ValueTask<ControllerResult<SyncRecordPayload>> ProjectOpenTabAsync(
        SyncOperationContext context,
        SyncEntityId entityId)
    {
        var validation = ValidateContext(context, entityId);
        if (validation is not null)
            return ValueTask.FromResult(ControllerResult<SyncRecordPayload>.Failure(validation));

        BrowserWorkspaceSnapshot snapshot;
        try
        {
            snapshot = _workspaceSnapshot();
        }
        catch (Exception)
        {
            return ValueTask.FromResult(Unavailable());
        }

        if (snapshot is null || snapshot.Context != context.Browsing.Privacy ||
            snapshot.WindowId != context.Browsing.WindowId || snapshot.Revision.IsEmpty)
        {
            return ValueTask.FromResult(Integrity());
        }

        var position = -1;
        BrowserTabState? tab = null;
        for (var index = 0; index < snapshot.Browser.Tabs.Count; index++)
        {
            if (snapshot.Browser.Tabs[index].TabId.Value != entityId.Value)
                continue;
            position = index;
            tab = snapshot.Browser.Tabs[index];
            break;
        }

        if (tab is null || tab.IsPrivate || tab.Address is null || !ValidTarget(tab.Address))
            return ValueTask.FromResult(NotFound());

        string? groupLabel = null;
        if (tab.GroupId is { } groupId)
        {
            var matches = snapshot.Groups.Where(group => group.GroupId == groupId).ToArray();
            if (matches.Length != 1)
                return ValueTask.FromResult(Integrity());
            groupLabel = matches[0].Name;
        }

        // BrowserWorkspaceRevision is the authoritative concurrency value. A
        // fixed non-default timestamp keeps retries byte-stable without
        // inventing a wall-clock mutation time that the workspace does not own.
        var modified = DateTimeOffset.UnixEpoch;
        var title = string.IsNullOrWhiteSpace(tab.Title) ? "New Tab" : tab.Title.Trim();
        var record = new OpenTabSyncRecord(
            entityId,
            snapshot.Revision.Value,
            modified,
            tab.Address.AbsoluteUri,
            title,
            position,
            groupLabel);
        return ValueTask.FromResult(Valid(record));
    }

    private static ControllerError? ValidateContext(
        SyncOperationContext? context,
        SyncEntityId entityId)
    {
        if (context is null || !entityId.IsDefined)
            return Error(ControllerErrorCode.InvalidRequest, "sync.projection.invalid");
        return context.Browsing.Privacy.IsPrivate
            ? Error(ControllerErrorCode.PolicyDenied, "sync.private-mode.policy-denied")
            : null;
    }

    private static bool ValidTarget(Uri target) =>
        target.IsAbsoluteUri &&
        target.Scheme is "http" or "https" &&
        string.IsNullOrEmpty(target.UserInfo) &&
        !string.IsNullOrWhiteSpace(target.IdnHost) &&
        target.AbsoluteUri.Length <= 16_384;

    private static ControllerResult<SyncRecordPayload> Valid(SyncRecordPayload record) =>
        SyncContractRules.ValidateRecord(record).IsValid
            ? ControllerResult<SyncRecordPayload>.Success(record)
            : Integrity();

    private static ControllerResult<SyncRecordPayload> Invalid() =>
        ControllerResult<SyncRecordPayload>.Failure(
            Error(ControllerErrorCode.InvalidRequest, "sync.projection.invalid"));

    private static ControllerResult<SyncRecordPayload> NotFound() =>
        ControllerResult<SyncRecordPayload>.Failure(
            Error(ControllerErrorCode.NotFound, "sync.projection.entity-not-found"));

    private static ControllerResult<SyncRecordPayload> Integrity() =>
        ControllerResult<SyncRecordPayload>.Failure(
            Error(ControllerErrorCode.IntegrityFailure, "sync.projection.state-invalid"));

    private static ControllerResult<SyncRecordPayload> Unavailable() =>
        ControllerResult<SyncRecordPayload>.Failure(
            Error(ControllerErrorCode.Unavailable, "sync.projection.unavailable", true));

    private static ControllerError Error(
        ControllerErrorCode code,
        string messageKey,
        bool retryable = false) =>
        ControllerError.Create(code, messageKey, isRetryable: retryable);
}
