using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Foundation.Browser;

namespace OrbitNavigator.App;

/// <summary>One real, window-local Settings tab for every menu/duplicate request.</summary>
internal sealed class SettingsTabRoute
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async ValueTask<ControllerResult<BrowserWorkspaceCommandReceipt>> OpenAsync(
        BrowserWorkspaceCoordinator workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Read the coordinator's current snapshot, not a stale UI projection:
            // repeated menu clicks must select the tab the prior request created.
            var snapshot = workspace.Current;
            var existing = InternalPageTabRoute.FindOpenTab(snapshot.Browser, BrowserInternalPageKind.Settings);
            BrowserWorkspaceAction action = existing is { } tabId
                ? new SelectWorkspaceTabAction(snapshot.WindowId, snapshot.Revision, tabId)
                : new AddWorkspaceTabAction(snapshot.WindowId, snapshot.Revision,
                    new BrowserTabState(new BrowserTabId(Guid.NewGuid()), null, null, "Settings",
                        BrowserLoadState.Idle, false, false, snapshot.Context.IsPrivate)
                    {
                        InternalPage = BrowserInternalPageKind.Settings,
                    }, Select: true);
            return await workspace.ExecuteAsync(action, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
