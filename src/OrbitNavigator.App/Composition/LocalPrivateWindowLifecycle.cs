using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;

namespace OrbitNavigator.App.Composition;

/// <summary>App composition adapter for the private-window lifecycle contract.</summary>
public sealed class LocalPrivateWindowLifecycle : IPrivateWindowLifecycle
{
    private readonly IWebViewProfileLifecycle _profiles;

    public LocalPrivateWindowLifecycle(IWebViewProfileLifecycle profiles) =>
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));

    public ValueTask<ControllerResult<PrivateWindowSession>> OpenAsync(
        CreatePrivateWindowIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        cancellationToken.ThrowIfCancellationRequested();
        if (intent.InitiatingContext is not { IsStructurallyValid: true } ||
            intent.InitiatingContext.IsPrivate || intent.InitiatingWindowId.IsEmpty)
        {
            return ValueTask.FromResult(ControllerResult<PrivateWindowSession>.Failure(
                ControllerError.Create(ControllerErrorCode.PolicyDenied, "error.private_window.open_denied")));
        }

        var context = new PrivacyContext(
            intent.InitiatingContext.ProfileId,
            new BrowserSessionId(Guid.NewGuid()),
            BrowserProfileMode.Private);
        return ValueTask.FromResult(ControllerResult<PrivateWindowSession>.Success(
            new PrivateWindowSession(context, new BrowserWindowId(Guid.NewGuid()), DateTimeOffset.UtcNow)));
    }

    public async ValueTask<ControllerResult<PrivateWindowCloseReceipt>> CloseAsync(
        ClosePrivateWindowIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (intent.Context is not { IsStructurallyValid: true, IsPrivate: true } || intent.WindowId.IsEmpty)
        {
            return ControllerResult<PrivateWindowCloseReceipt>.Failure(
                ControllerError.Create(ControllerErrorCode.InvalidRequest, "error.private_window.close_invalid"));
        }

        var ended = await _profiles.EndSessionAsync(intent.Context, cancellationToken);
        return ended.IsSuccess
            ? ControllerResult<PrivateWindowCloseReceipt>.Success(new PrivateWindowCloseReceipt(
                intent.Context.ProfileId,
                intent.Context.SessionId,
                intent.WindowId,
                ended.Value!.EphemeralDataDeleted,
                DateTimeOffset.UtcNow))
            : ControllerResult<PrivateWindowCloseReceipt>.Failure(ended.Error!);
    }
}
