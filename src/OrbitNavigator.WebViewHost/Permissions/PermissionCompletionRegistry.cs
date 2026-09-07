using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Privacy;

namespace OrbitNavigator.WebViewHost.Permissions;

public sealed record PendingPermissionRegistration(
    RequestId RequestId,
    BrowserTabId TabId,
    WebPermissionCapability Capability,
    DateTimeOffset ExpiresAtUtc,
    Action<PermissionHostCompletion> Complete);

public sealed class PermissionCompletionRegistry : IPermissionHostCompletionSink
{
    private readonly Dictionary<RequestId, PendingPermissionRegistration> _pending = [];
    private readonly object _gate = new();

    public ControllerResult Register(PendingPermissionRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(registration.Complete);
        if (registration.RequestId.IsEmpty ||
            registration.TabId.IsEmpty ||
            registration.Capability == WebPermissionCapability.Unknown)
        {
            return ControllerResult.Failure(ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "error.permission_host.registration_invalid"));
        }

        lock (_gate)
        {
            if (!_pending.TryAdd(registration.RequestId, registration))
            {
                return ControllerResult.Failure(ControllerError.Create(
                    ControllerErrorCode.Conflict,
                    "error.permission_host.request_conflict"));
            }
        }

        return ControllerResult.Success();
    }

    public ValueTask<ControllerResult> CompleteAsync(
        PermissionHostCompletion completion,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(completion);
        PendingPermissionRegistration? registration;
        lock (_gate)
        {
            if (!_pending.Remove(completion.RequestId, out registration))
            {
                return ValueTask.FromResult(ControllerResult.Failure(ControllerError.Create(
                    ControllerErrorCode.AlreadyHandled,
                    "error.permission_host.request_already_handled")));
            }
        }

        if (registration.TabId != completion.RequestingTabId ||
            registration.Capability != completion.Capability ||
            completion.SaveInProfile)
        {
            registration.Complete(PermissionHostCompletion.FailClosed(
                registration.RequestId,
                registration.TabId,
                registration.Capability));
            return ValueTask.FromResult(ControllerResult.Failure(ControllerError.Create(
                ControllerErrorCode.IntegrityFailure,
                "error.permission_host.completion_mismatch")));
        }

        registration.Complete(completion);
        return ValueTask.FromResult(ControllerResult.Success());
    }

    public int Expire(DateTimeOffset nowUtc)
    {
        List<PendingPermissionRegistration> expired;
        lock (_gate)
        {
            expired = _pending.Values
                .Where(item => item.ExpiresAtUtc <= nowUtc)
                .ToList();
            foreach (var item in expired)
            {
                _pending.Remove(item.RequestId);
            }
        }

        foreach (var item in expired)
        {
            item.Complete(PermissionHostCompletion.FailClosed(
                item.RequestId,
                item.TabId,
                item.Capability,
                PermissionDecisionSource.RequestExpired));
        }

        return expired.Count;
    }
}
