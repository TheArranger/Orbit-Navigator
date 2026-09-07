using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Sync.Accounts;

namespace OrbitNavigator.App.Accounts;

/// <summary>
/// Opens a short-lived pushed-authorization URI in the Windows default browser.
/// The URI is never logged, cached, or returned to Presentation.
/// </summary>
public sealed class WindowsMyOrbitSystemBrowserLauncher : IMyOrbitSystemBrowserLauncher
{
    private const string AuthorizationPath = "/oauth2/authorize";
    private const string ClientId = "orbit-navigator";
    private const string RequestUriPrefix = "urn:ietf:params:oauth:request_uri:mopr_";
    private static readonly Regex ParHandlePattern = new(
        "^[A-Za-z0-9_-]{43}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly Uri _authority;
    private readonly Func<ProcessStartInfo, Process?> _start;

    public WindowsMyOrbitSystemBrowserLauncher(Uri authority)
        : this(authority, Process.Start)
    {
    }

    internal WindowsMyOrbitSystemBrowserLauncher(
        Uri authority,
        Func<ProcessStartInfo, Process?> start)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (!authority.IsAbsoluteUri || authority.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(authority.UserInfo) || authority.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(authority.Query) || !string.IsNullOrEmpty(authority.Fragment))
        {
            throw new ArgumentException("A canonical HTTPS provider authority is required.", nameof(authority));
        }

        _authority = authority;
        _start = start ?? throw new ArgumentNullException(nameof(start));
    }

    public ValueTask<ControllerResult> LaunchAsync(
        Uri authorizationRequest,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsApprovedAuthorizationRequest(authorizationRequest))
        {
            return ValueTask.FromResult(ControllerResult.Failure(ControllerError.Create(
                ControllerErrorCode.PolicyDenied,
                "account.link.authorization-origin-denied")));
        }

        try
        {
            _ = _start(new ProcessStartInfo
            {
                FileName = authorizationRequest.AbsoluteUri,
                UseShellExecute = true,
            });
            return ValueTask.FromResult(ControllerResult.Success());
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return ValueTask.FromResult(ControllerResult.Failure(ControllerError.Create(
                ControllerErrorCode.Unavailable,
                "account.link.browser-launch-failed",
                isRetryable: true)));
        }
    }

    internal bool IsApprovedAuthorizationRequest(Uri? request)
    {
        if (request is not { IsAbsoluteUri: true } ||
            request.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(request.UserInfo) ||
            !string.IsNullOrEmpty(request.Fragment) ||
            request.Scheme != _authority.Scheme ||
            !request.Host.Equals(_authority.Host, StringComparison.OrdinalIgnoreCase) ||
            request.Port != _authority.Port ||
            !request.AbsolutePath.Equals(AuthorizationPath, StringComparison.Ordinal) ||
            string.IsNullOrEmpty(request.Query))
        {
            return false;
        }

        var parameters = request.Query[1..].Split('&', StringSplitOptions.None);
        if (parameters.Length != 2)
        {
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in parameters)
        {
            var separator = parameter.IndexOf('=');
            if (separator <= 0 || separator == parameter.Length - 1)
            {
                return false;
            }

            var name = DecodeQueryComponent(parameter[..separator]);
            var value = DecodeQueryComponent(parameter[(separator + 1)..]);
            if (name is null || value is null || name.Any(char.IsControl) || value.Any(char.IsControl) ||
                !values.TryAdd(name, value))
            {
                return false;
            }
        }

        if (values.Count != 2 ||
            !values.TryGetValue("client_id", out var clientId) || clientId != ClientId ||
            !values.TryGetValue("request_uri", out var requestUri) ||
            !requestUri.StartsWith(RequestUriPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        return ParHandlePattern.IsMatch(requestUri[RequestUriPrefix.Length..]);
    }

    private static string? DecodeQueryComponent(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        catch (UriFormatException)
        {
            return null;
        }
    }
}
