using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.Contracts.Infrastructure;

public enum WebViewProfilePersistence
{
    Persistent = 0,
    EphemeralDeleteOnSessionEnd = 1,
}

public sealed class WebViewProfileDescriptor
{
    private WebViewProfileDescriptor(
        PrivacyContext context,
        string profileName,
        string userDataFolder,
        WebViewProfilePersistence persistence)
    {
        Context = context;
        ProfileName = profileName;
        UserDataFolder = userDataFolder;
        Persistence = persistence;
    }

    public PrivacyContext Context { get; }

    public string ProfileName { get; }

    public string UserDataFolder { get; }

    public WebViewProfilePersistence Persistence { get; }

    public bool MustDeleteAtSessionEnd =>
        Persistence == WebViewProfilePersistence.EphemeralDeleteOnSessionEnd;

    public static ControllerResult<WebViewProfileDescriptor> Create(
        PrivacyContext? context,
        string profileName,
        string userDataFolder)
    {
        if (context is not { IsStructurallyValid: true } ||
            string.IsNullOrWhiteSpace(profileName) ||
            string.IsNullOrWhiteSpace(userDataFolder) ||
            !Path.IsPathFullyQualified(userDataFolder))
        {
            return ControllerResult<WebViewProfileDescriptor>.Failure(ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "error.webview_profile.descriptor_invalid"));
        }

        var persistence = context.IsPrivate
            ? WebViewProfilePersistence.EphemeralDeleteOnSessionEnd
            : WebViewProfilePersistence.Persistent;

        return ControllerResult<WebViewProfileDescriptor>.Success(new WebViewProfileDescriptor(
            context,
            profileName,
            userDataFolder,
            persistence));
    }
}

public interface IWebViewProfileLease : IAsyncDisposable
{
    WebViewProfileDescriptor Descriptor { get; }
}

public sealed record WebViewProfileSessionEndReceipt(
    ProfileId ProfileId,
    BrowserSessionId SessionId,
    bool EphemeralDataDeleted);

public interface IWebViewProfileLifecycle
{
    ValueTask<ControllerResult<IWebViewProfileLease>> AcquireAsync(
        PrivacyContext context,
        CancellationToken cancellationToken = default);

    ValueTask<ControllerResult<WebViewProfileSessionEndReceipt>> EndSessionAsync(
        PrivacyContext context,
        CancellationToken cancellationToken = default);
}
