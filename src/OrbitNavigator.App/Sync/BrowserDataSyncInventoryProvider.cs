using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Sync;
using OrbitNavigator.Foundation.Browser;
using OrbitNavigator.Sync.State;

namespace OrbitNavigator.App.Sync;

/// <summary>
/// Builds a content-free revision inventory from authoritative local state. The
/// resulting identifiers can be reconciled into the local sync journal without
/// copying URLs, titles, or page content into the comparison index.
/// </summary>
internal sealed class BrowserDataSyncInventoryProvider
{
    private readonly IHistoryFacade _history;
    private readonly Func<BrowserWorkspaceSnapshot> _workspaceSnapshot;

    public BrowserDataSyncInventoryProvider(
        IHistoryFacade history,
        Func<BrowserWorkspaceSnapshot> workspaceSnapshot)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _workspaceSnapshot = workspaceSnapshot ??
            throw new ArgumentNullException(nameof(workspaceSnapshot));
    }

    public async ValueTask<ControllerResult<IReadOnlyList<LocalSyncEntityVersion>>> BuildAsync(
        SyncOperationContext context,
        CancellationToken cancellationToken)
    {
        if (context is null)
            return Invalid();
        if (context.Browsing.Privacy.IsPrivate)
            return PolicyDenied();

        var history = await _history.QueryAsync(
            new HistoryQuery(
                context.Browsing.Privacy,
                null,
                null,
                HistoryFacade.MaximumEntries),
            cancellationToken).ConfigureAwait(false);
        if (!history.IsSuccess)
            return ControllerResult<IReadOnlyList<LocalSyncEntityVersion>>.Failure(history.Error!);

        BrowserWorkspaceSnapshot workspace;
        try
        {
            workspace = _workspaceSnapshot();
        }
        catch (Exception)
        {
            return Unavailable();
        }
        if (workspace is null || workspace.Context != context.Browsing.Privacy ||
            workspace.WindowId != context.Browsing.WindowId || workspace.Revision.IsEmpty)
        {
            return Integrity();
        }

        var result = new List<LocalSyncEntityVersion>(
            history.Value!.Count + workspace.Browser.Tabs.Count);
        foreach (var entry in history.Value)
        {
            if (entry is null || entry.Id.IsEmpty ||
                entry.Id.ProfileId != context.Browsing.Privacy.ProfileId ||
                entry.LastVisitedAtUtc == default)
            {
                return Integrity();
            }
            result.Add(new(
                SyncDataCategory.History,
                new SyncEntityId(entry.Id.Value),
                entry.LastVisitedAtUtc.UtcTicks));
        }

        foreach (var tab in workspace.Browser.Tabs)
        {
            if (tab is null || tab.TabId.IsEmpty)
                return Integrity();
            if (tab.IsPrivate || tab.Address is null || !ValidTarget(tab.Address))
                continue;
            result.Add(new(
                SyncDataCategory.OpenTabs,
                new SyncEntityId(tab.TabId.Value),
                workspace.Revision.Value));
        }

        return result.Count <= ProfileStorageSyncEntityIndexReconciler.MaximumTrackedEntities
            ? ControllerResult<IReadOnlyList<LocalSyncEntityVersion>>.Success(result)
            : Invalid();
    }

    private static bool ValidTarget(Uri target) =>
        target.IsAbsoluteUri &&
        target.Scheme is "http" or "https" &&
        string.IsNullOrEmpty(target.UserInfo) &&
        !string.IsNullOrWhiteSpace(target.IdnHost) &&
        target.AbsoluteUri.Length <= 16_384;

    private static ControllerResult<IReadOnlyList<LocalSyncEntityVersion>> Invalid() =>
        Failure(ControllerErrorCode.InvalidRequest, "sync.inventory.invalid");

    private static ControllerResult<IReadOnlyList<LocalSyncEntityVersion>> PolicyDenied() =>
        Failure(ControllerErrorCode.PolicyDenied, "sync.private-mode.policy-denied");

    private static ControllerResult<IReadOnlyList<LocalSyncEntityVersion>> Integrity() =>
        Failure(ControllerErrorCode.IntegrityFailure, "sync.inventory.state-invalid");

    private static ControllerResult<IReadOnlyList<LocalSyncEntityVersion>> Unavailable() =>
        ControllerResult<IReadOnlyList<LocalSyncEntityVersion>>.Failure(
            ControllerError.Create(
                ControllerErrorCode.Unavailable,
                "sync.inventory.unavailable",
                isRetryable: true));

    private static ControllerResult<IReadOnlyList<LocalSyncEntityVersion>> Failure(
        ControllerErrorCode code,
        string key) =>
        ControllerResult<IReadOnlyList<LocalSyncEntityVersion>>.Failure(
            ControllerError.Create(code, key));
}
