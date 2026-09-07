namespace OrbitNavigator.Contracts.Common;

public enum BrowserProfileMode
{
    Normal = 0,
    Private = 1,
}

public sealed record PrivacyContext(
    ProfileId ProfileId,
    BrowserSessionId SessionId,
    BrowserProfileMode Mode)
{
    public bool IsStructurallyValid =>
        !ProfileId.IsEmpty &&
        !SessionId.IsEmpty &&
        Enum.IsDefined(Mode);

    public bool IsPrivate => Mode == BrowserProfileMode.Private;
}

public sealed record BrowsingContext(
    PrivacyContext Privacy,
    BrowserWindowId WindowId,
    BrowserTabId TabId,
    SiteIdentity? CurrentSite)
{
    public bool IsStructurallyValid =>
        Privacy is { IsStructurallyValid: true } &&
        !WindowId.IsEmpty &&
        !TabId.IsEmpty;
}

public static class NormalProfileOperationGuard
{
    public static ControllerResult RequireNormal(PrivacyContext? context)
    {
        if (context is not { IsStructurallyValid: true })
        {
            return ControllerResult.Failure(ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "error.context.invalid"));
        }

        return context.Mode == BrowserProfileMode.Private
            ? ControllerResult.Failure(ControllerError.Create(
                ControllerErrorCode.PolicyDenied,
                "error.private.remote_operation_denied"))
            : ControllerResult.Success();
    }

    public static ControllerResult RequireNormal(BrowsingContext? context)
    {
        if (context is not { IsStructurallyValid: true })
        {
            return ControllerResult.Failure(ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "error.context.invalid"));
        }

        return RequireNormal(context.Privacy);
    }
}

