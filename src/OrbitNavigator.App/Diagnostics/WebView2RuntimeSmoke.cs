using System.IO;
using System.Text.Json;
using System.Windows;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Foundation.Profiles;
using OrbitNavigator.WebViewHost;

namespace OrbitNavigator.App.Diagnostics;

internal sealed record WebView2RuntimeSmokeResult(
    bool Success,
    string? RuntimeVersion,
    bool CoreCreated,
    bool DevToolsDisabled,
    bool PasswordAutosaveDisabled,
    bool AutofillDisabled,
    string? ErrorMessageKey);

internal static class WebView2RuntimeSmoke
{
    public static async Task<int> RunAsync(string localRoot, string? resultPath)
    {
        var outputPath = string.IsNullOrWhiteSpace(resultPath)
            ? Path.Combine(localRoot, "logs", "webview2-runtime-smoke.json")
            : Path.GetFullPath(resultPath);
        var profile = new PrivacyContext(
            new ProfileId(Guid.NewGuid()),
            new BrowserSessionId(Guid.NewGuid()),
            BrowserProfileMode.Private);
        var context = new BrowsingContext(
            profile,
            new BrowserWindowId(Guid.NewGuid()),
            new BrowserTabId(Guid.NewGuid()),
            null);
        var lifecycle = new WebViewProfileLifecycle(Path.Combine(localRoot, "smoke", "webview"));
        var lease = await lifecycle.AcquireAsync(profile);
        if (!lease.IsSuccess)
        {
            return await WriteResultAsync(outputPath, new(
                false, null, false, false, false, false,
                lease.Error?.MessageKey ?? "error.webview_smoke.profile_failed"));
        }

        var host = new WebView2HostControl(context);
        var hostWindow = new Window
        {
            Content = host,
            Width = 1,
            Height = 1,
            Left = -32_000,
            Top = -32_000,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        var hostOwnsLease = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            // WebView2 requires an attached, loaded WPF visual to complete
            // controller creation. Keep the smoke surface off-screen while still
            // exercising the same hosted control used by the real application.
            hostWindow.Show();
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
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            return await WriteResultAsync(outputPath, new(
                false, null, false, false, false, false,
                exception is OperationCanceledException
                    ? "error.webview_smoke.timeout"
                    : "error.webview_smoke.exception"));
        }
        finally
        {
            hostWindow.Content = null;
            if (hostOwnsLease) await host.DisposeAsync();
            else await lease.Value!.DisposeAsync();
            hostWindow.Close();
        }
    }

    private static async Task<int> WriteResultAsync(
        string outputPath,
        WebView2RuntimeSmokeResult result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(
            outputPath,
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return result.Success && result.CoreCreated &&
            result.DevToolsDisabled && result.PasswordAutosaveDisabled &&
            result.AutofillDisabled && !string.IsNullOrWhiteSpace(result.RuntimeVersion)
                ? 0
                : 1;
    }
}
