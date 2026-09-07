using System.IO;
using System.Text.Json;
using System.Windows;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Foundation.Profiles;

namespace OrbitNavigator.WebViewHost.Smoke;

internal sealed record RuntimeSmokeResult(
    bool Success,
    string? RuntimeVersion,
    bool CoreCreated,
    bool DevToolsDisabled,
    bool PasswordAutosaveDisabled,
    bool AutofillDisabled,
    string? ErrorMessageKey);

internal static class Program
{
    private static int _exitCode = 1;

    [STAThread]
    private static int Main()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Startup += async (_, _) =>
        {
            _exitCode = await RunAsync();
            application.Shutdown(_exitCode);
        };
        application.Run();
        return _exitCode;
    }

    private static async Task<int> RunAsync()
    {
        var outputPath = Environment.GetEnvironmentVariable("ORBIT_WEBVIEW_SMOKE_RESULT");
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return 2;
        }

        outputPath = Path.GetFullPath(outputPath);
        var localRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Orbit Navigator",
            "smoke");
        var profile = new PrivacyContext(
            new ProfileId(Guid.NewGuid()),
            new BrowserSessionId(Guid.NewGuid()),
            BrowserProfileMode.Private);
        var context = new BrowsingContext(
            profile,
            new BrowserWindowId(Guid.NewGuid()),
            new BrowserTabId(Guid.NewGuid()),
            null);
        var lifecycle = new WebViewProfileLifecycle(Path.Combine(localRoot, "webview"));
        var lease = await lifecycle.AcquireAsync(profile);
        if (!lease.IsSuccess)
        {
            return await WriteResultAsync(outputPath, new(
                false, null, false, false, false, false,
                lease.Error?.MessageKey ?? "error.webview_smoke.profile_failed"));
        }

        var host = new WebView2HostControl(context);
        var window = new Window
        {
            Width = 2,
            Height = 2,
            Left = -32_000,
            Top = -32_000,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            Content = host,
        };
        var hostOwnsLease = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            window.Show();
            var initialized = await host.InitializeAsync(lease.Value!, timeout.Token);
            if (!initialized.IsSuccess || host.CoreWebView is null)
            {
                return await WriteResultAsync(outputPath, new(
                    false, null, false, false, false, false,
                    initialized.Error?.MessageKey ?? "error.webview_smoke.core_missing"));
            }

            hostOwnsLease = true;
            var core = host.CoreWebView;
            return await WriteResultAsync(outputPath, new(
                true,
                core.Environment.BrowserVersionString,
                true,
                !core.Settings.AreDevToolsEnabled,
                !core.Settings.IsPasswordAutosaveEnabled,
                !core.Settings.IsGeneralAutofillEnabled,
                null));
        }
        catch (Exception exception)
        {
            return await WriteResultAsync(outputPath, new(
                false, null, false, false, false, false,
                exception is OperationCanceledException
                    ? "error.webview_smoke.timeout"
                    : "error.webview_smoke.exception"));
        }
        finally
        {
            if (hostOwnsLease)
            {
                await host.DisposeAsync();
            }
            else
            {
                await lease.Value!.DisposeAsync();
            }

            window.Content = null;
            window.Close();
        }
    }

    private static async Task<int> WriteResultAsync(string outputPath, RuntimeSmokeResult result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(
            outputPath,
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return result.Success &&
            result.CoreCreated &&
            result.DevToolsDisabled &&
            result.PasswordAutosaveDisabled &&
            result.AutofillDisabled &&
            !string.IsNullOrWhiteSpace(result.RuntimeVersion)
                ? 0
                : 1;
    }
}
