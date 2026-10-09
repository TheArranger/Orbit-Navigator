using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Sync.Accounts;

namespace OrbitNavigator.App.Accounts;

/// <summary>
/// Opens a short-lived pushed-authorization URI in a resolved external browser.
/// Never routes OAuth back through Navigator when it is the Windows default.
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
    private readonly Func<ExternalBrowserExecutable?> _resolveBrowser;

    public WindowsMyOrbitSystemBrowserLauncher(Uri authority)
        : this(authority, Process.Start)
    {
    }

    internal WindowsMyOrbitSystemBrowserLauncher(
        Uri authority,
        Func<ProcessStartInfo, Process?> start)
        : this(authority, ResolveAssociatedBrowser, start)
    {
    }

    internal WindowsMyOrbitSystemBrowserLauncher(
        Uri authority,
        Func<ExternalBrowserExecutable?> resolveBrowser,
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
        _resolveBrowser = resolveBrowser ?? throw new ArgumentNullException(nameof(resolveBrowser));
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
            var browser = _resolveBrowser();
            if (!IsExternalBrowser(browser, Environment.ProcessPath))
                return ExternalBrowserUnavailable();
            cancellationToken.ThrowIfCancellationRequested();
            var startInfo = new ProcessStartInfo
            {
                FileName = browser!.ExecutablePath,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(browser.ExecutablePath)!,
            };
            // The resolved executable receives one URI argument. Do not execute
            // a registry command template or re-resolve a possibly changed default.
            startInfo.ArgumentList.Add(authorizationRequest.AbsoluteUri);
            using var process = _start(startInfo);
            if (process is null)
                return ExternalBrowserUnavailable();
            return ValueTask.FromResult(ControllerResult.Success());
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or
            IOException or UnauthorizedAccessException or SecurityException or ArgumentException or
            NotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        {
            return ExternalBrowserUnavailable();
        }
    }

    private static ValueTask<ControllerResult> ExternalBrowserUnavailable() =>
        ValueTask.FromResult(ControllerResult.Failure(ControllerError.Create(
            ControllerErrorCode.Unavailable,
            "account.link.external-browser-unavailable",
            isRetryable: true)));

    private static ExternalBrowserExecutable? ResolveAssociatedBrowser()
    {
        if (!OperatingSystem.IsWindows()) return null;
        // ASSOCF_IS_PROTOCOL honors the current user's HTTPS default; NOFIXUPS
        // keeps this lookup read-only. Ask Windows for an executable, never parse
        // a shell command or guess browser installation paths.
        const uint flags = 0x00001000 | 0x00000100 | 0x00000020;
        const uint executable = 2; // ASSOCSTR_EXECUTABLE
        uint capacity = 0;
        var result = AssocQueryString(flags, executable, "https", "open", null, ref capacity);
        if (result != 1 || capacity is < 2 or > 32768) return null;
        var buffer = new StringBuilder((int)capacity);
        if (AssocQueryString(flags, executable, "https", "open", buffer, ref capacity) != 0)
            return null;
        return InspectExecutable(buffer.ToString());
    }

    internal static ExternalBrowserExecutable? InspectExecutable(string? executablePath)
    {
        if (!ValidExecutablePath(executablePath) || !File.Exists(executablePath)) return null;
        var path = Path.GetFullPath(executablePath!);
        var information = FileVersionInfo.GetVersionInfo(path);
        return new(path, information.ProductName, information.FileDescription,
            information.OriginalFilename, information.InternalName);
    }

    internal static bool IsExternalBrowser(ExternalBrowserExecutable? browser, string? currentProcessPath)
    {
        if (browser is null || !ValidExecutablePath(browser.ExecutablePath)) return false;
        var path = Path.GetFullPath(browser.ExecutablePath);
        if (!string.IsNullOrEmpty(currentProcessPath) &&
            path.Equals(Path.GetFullPath(currentProcessPath), StringComparison.OrdinalIgnoreCase)) return false;
        var fileName = Path.GetFileName(path);
        if (IsNavigatorIdentity(fileName)) return false;
        // These are shell/protocol brokers, not direct browser executables. A
        // packaged browser that cannot resolve directly remains unavailable.
        if (fileName.ToLowerInvariant() is "rundll32.exe" or "dllhost.exe" or "explorer.exe" or
            "cmd.exe" or "powershell.exe" or "pwsh.exe" or "conhost.exe") return false;
        var identities = new[] { browser.ProductName, browser.FileDescription, browser.OriginalFilename, browser.InternalName };
        if (identities.All(string.IsNullOrWhiteSpace)) return false;
        // Version-resource identity catches renamed copies of Navigator's apphost
        // and stable launcher as well as the familiar executable filenames.
        return !identities.Any(value => IsNavigatorIdentity(value) || value?.Any(char.IsControl) == true);
    }

    private static bool ValidExecutablePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && !path.Any(char.IsControl) && !path.Contains('"') &&
        Path.IsPathFullyQualified(path) && Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsNavigatorIdentity(string? value) =>
        value is not null && new string(value.Where(char.IsLetterOrDigit).ToArray())
            .StartsWith("OrbitNavigator", StringComparison.OrdinalIgnoreCase);

    [DllImport("shlwapi.dll", EntryPoint = "AssocQueryStringW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int AssocQueryString(uint flags, uint associationString, string association,
        string extra, StringBuilder? output, ref uint outputCharacters);

    internal sealed record ExternalBrowserExecutable(string ExecutablePath, string? ProductName,
        string? FileDescription, string? OriginalFilename, string? InternalName);

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
