using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.Contracts.Sync;

/// <summary>
/// Capability proving that private-mode rejection and identity validation occurred
/// before a sync dependency was invoked. No public constructor is exposed.
/// </summary>
public sealed class SyncOperationContext
{
    private SyncOperationContext(BrowsingContext browsing, SyncOperationId operationId)
    {
        Browsing = browsing;
        OperationId = operationId;
    }

    public BrowsingContext Browsing { get; }

    public SyncOperationId OperationId { get; }

    public static ControllerResult<SyncOperationContext> Authorize(
        BrowsingContext browsing,
        SyncOperationId operationId)
    {
        ArgumentNullException.ThrowIfNull(browsing);

        if (browsing.Privacy is { Mode: BrowserProfileMode.Private })
        {
            return ControllerResult<SyncOperationContext>.Failure(
                ControllerError.Create(
                    ControllerErrorCode.PolicyDenied,
                    "sync.private-mode.policy-denied"));
        }

        if (!browsing.IsStructurallyValid || !operationId.IsDefined)
        {
            return ControllerResult<SyncOperationContext>.Failure(
                ControllerError.Create(
                    ControllerErrorCode.InvalidRequest,
                    "sync.context.invalid"));
        }

        return ControllerResult<SyncOperationContext>.Success(
            new SyncOperationContext(browsing, operationId));
    }
}

public static class SyncOperationGate
{
    public static async ValueTask<ControllerResult<T>> ExecuteAsync<T>(
        BrowsingContext browsing,
        SyncOperationId operationId,
        Func<SyncOperationContext, ValueTask<ControllerResult<T>>> operation)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(operation);
        var authorized = SyncOperationContext.Authorize(browsing, operationId);
        if (!authorized.IsSuccess)
            return ControllerResult<T>.Failure(authorized.Error!);

        return await operation(authorized.Value!).ConfigureAwait(false);
    }
}
