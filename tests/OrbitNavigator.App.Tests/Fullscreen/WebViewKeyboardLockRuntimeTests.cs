using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Foundation.Profiles;
using OrbitNavigator.WebViewHost;
using OrbitNavigator.WebViewHost.Navigation;
using OrbitNavigator.WebViewHost.Permissions;
using Xunit;
using Xunit.Abstractions;

namespace OrbitNavigator.App.Tests.Fullscreen;

/// <summary>
/// Opt-in capability evidence, not a conformance assertion or physical-input test.
/// Unsupported APIs and rejected/pending promises are reported, not made to pass.
/// </summary>
public sealed class WebViewKeyboardLockRuntimeTests(ITestOutputHelper output)
{
    private const string FixtureOrigin = "https://keyboard-lock.orbit.test";

    [KeyboardLockRuntimeFact]
    [Trait("Category", "WebView2Runtime")]
    public Task ReportKeyboardLockBeforeFullscreenInTopPageAndSameOriginIframe() => RunStaAsync(async () =>
    {
        // Never inherit a security-weakening browser-argument override for this probe.
        Assert.True(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS")),
            "Run this probe without WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS overrides.");
        var root = Path.Combine(Path.GetTempPath(), "OrbitNavigator.KeyboardLockProbe", Guid.NewGuid().ToString("N"));
        var privacy = new PrivacyContext(new ProfileId(Guid.NewGuid()), new BrowserSessionId(Guid.NewGuid()), BrowserProfileMode.Private);
        var context = new BrowsingContext(privacy, new BrowserWindowId(Guid.NewGuid()), new BrowserTabId(Guid.NewGuid()), null);
        var lifecycle = new WebViewProfileLifecycle(root);
        var acquired = await lifecycle.AcquireAsync(privacy);
        Assert.True(acquired.IsSuccess, acquired.Error?.MessageKey);
        var lease = acquired.Value!;
        var profilePath = lease.Descriptor.UserDataFolder;
        var host = new WebView2HostControl(context, new HostNavigationGuard(new FixtureNavigationPolicy()));
        var window = new Window
        {
            Content = host, Width = 800, Height = 600, Left = -32_000, Top = -32_000,
            ShowActivated = false, ShowInTaskbar = false,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        var ownsLease = false;
        var stage = "initialization";
        var permissionEvents = new List<object>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            window.Show();
            var initialized = await host.InitializeAsync(lease, timeout.Token);
            Assert.True(initialized.IsSuccess, initialized.Error?.MessageKey);
            ownsLease = true;
            var core = Assert.IsType<CoreWebView2>(host.CoreWebView);
            output.WriteLine("Runtime: " + core.Environment.BrowserVersionString);
            output.WriteLine("Evidence scope: offscreen non-activated window; fresh private profile; in-memory HTTPS only; " +
                "CDP userGesture and synthetic WebView-only keys are diagnostic, never physical/trusted-input proof.");
            core.PermissionRequested += (_, args) =>
            {
                // Observe AFTER the actual host's fail-closed handler. Do not allow,
                // mark handled, obtain a deferral, or persist any permission here.
                var observation = new
                {
                    stage, kind = args.PermissionKind.ToString(), numericKind = (int)args.PermissionKind,
                    enumDefined = Enum.IsDefined(args.PermissionKind),
                    mappedCapability = WebPermissionCapabilityMapper.Map(args.PermissionKind).ToString(),
                    state = args.State.ToString(), args.IsUserInitiated, args.SavesInProfile, args.Uri,
                };
                permissionEvents.Add(observation);
                output.WriteLine("PermissionRequested: " + JsonSerializer.Serialize(observation));
            };
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, args) =>
            {
                var uri = new Uri(args.Request.Uri);
                var allowed = uri.GetLeftPart(UriPartial.Authority) == FixtureOrigin;
                args.Response = core.Environment.CreateWebResourceResponse(
                    new MemoryStream(Encoding.UTF8.GetBytes(allowed ? BuildHtml(uri.AbsolutePath == "/frame") : string.Empty)),
                    allowed ? 200 : 403, allowed ? "OK" : "Forbidden",
                    "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\n");
            };

            foreach (var target in new[] { "top", "iframe", "top-lock-iframe-focus" })
            {
                stage = target + " fixture navigation";
                Assert.True((await host.NavigateAsync(new Uri(FixtureOrigin + "/?target=" + target), timeout.Token)).IsSuccess);
                await UntilAsync(async () => await ReadBooleanAsync(core,
                    "Boolean(window.fixtureReady && document.querySelector('iframe')?.contentWindow?.fixtureReady)"), timeout.Token);
                var targetWindow = target == "iframe" ? "document.querySelector('iframe').contentWindow" : "window";
                stage = target + " baseline permission query";
                await EvaluateAsync(core, targetWindow + ".readPermission()", userGesture: false, timeout.Token);
                await UntilAsync(async () => await ReadBooleanAsync(core,
                    targetWindow + ".probeState.permission.status !== 'pending'"), timeout.Token);
                output.WriteLine(target + " baseline: " + await ReadStateAsync(core, window, host));

                stage = target + " keyboard.lock before requestFullscreen with injected activation";
                await EvaluateAsync(core, targetWindow + ".beginProbe()", userGesture: true, timeout.Token);
                // Do not require lock fulfillment: rejection or pending is evidence.
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < deadline && !await ReadBooleanAsync(core,
                    targetWindow + ".probeState.lock.status !== 'pending' && " + targetWindow + ".probeState.fullscreen.status !== 'pending'"))
                    await Task.Delay(50, timeout.Token);
                output.WriteLine(target + " after lock/fullscreen (maximum 5-second observation): " + await ReadStateAsync(core, window, host));

                if (target == "top-lock-iframe-focus")
                {
                    // Match a top-level streaming shell owning the lock/fullscreen
                    // while its same-origin iframe is the focused keyboard target.
                    stage = target + " focus child after top-level lock/fullscreen";
                    await EvaluateAsync(core,
                        "document.querySelector('iframe').contentWindow.focus(); " +
                        "document.querySelector('iframe').contentDocument.getElementById('focusTarget').focus();",
                        userGesture: false, timeout.Token);
                    output.WriteLine(target + " after child focus: " + await ReadStateAsync(core, window, host));
                }

                stage = target + " CDP synthetic Escape diagnostic";
                // Input.dispatchKeyEvent is confined to this test-owned WebView.
                // Any DOM isTrusted value here still does not prove physical input.
                foreach (var type in new[] { "keyDown", "keyUp" })
                {
                    var parameters = JsonSerializer.Serialize(new
                    {
                        type, key = "Escape", code = "Escape", windowsVirtualKeyCode = 27, nativeVirtualKeyCode = 27,
                    });
                    await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", parameters).WaitAsync(timeout.Token);
                }
                await Task.Delay(250, timeout.Token);
                output.WriteLine(target + " after CDP Escape (synthetic only): " + await ReadStateAsync(core, window, host));

                stage = target + " explicit fixture cleanup";
                await EvaluateAsync(core,
                    "navigator.keyboard?.unlock?.(); document.querySelector('iframe').contentWindow.navigator.keyboard?.unlock?.(); " +
                    "if (document.fullscreenElement) document.exitFullscreen();", userGesture: false, timeout.Token);
                await host.ExitFullscreenAsync();
                await UntilAsync(async () => !host.ContainsFullscreenElement && !core.ContainsFullScreenElement &&
                    await ReadBooleanAsync(core, "!document.fullscreenElement"), timeout.Token);
                Assert.False(window.IsActive);
                Assert.Equal(-32_000, window.Left);
                Assert.Equal(WindowState.Normal, window.WindowState);
            }
            output.WriteLine("All permission events: " + JsonSerializer.Serialize(permissionEvents));
        }
        catch (Exception exception)
        {
            throw new Xunit.Sdk.XunitException($"Keyboard-lock capability probe failed at {stage}: {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            window.Content = null;
            try
            {
                await host.DisposeAsync();
            }
            finally
            {
                if (!ownsLease) await lease.DisposeAsync();
                window.Close();
                await lifecycle.EndSessionAsync(privacy);
                var deleted = !Directory.Exists(profilePath);
                output.WriteLine("Private profile deleted: " + deleted);
                // Only remove now-empty ancestors inside our exact GUID root.
                // There is no recursive deletion beyond the lifecycle's validated session.
                if (deleted)
                {
                    var profileParent = Path.GetDirectoryName(profilePath)!;
                    Directory.Delete(profileParent);
                    Directory.Delete(Path.Combine(root, "private"));
                    Directory.Delete(root);
                }
                Assert.True(deleted, "Test-owned private profile was not deleted: " + profilePath);
            }
        }
    });

    private static async Task EvaluateAsync(CoreWebView2 core, string expression, bool userGesture, CancellationToken cancellationToken)
    {
        var parameters = JsonSerializer.Serialize(new { expression, userGesture, returnByValue = true });
        using var result = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", parameters).WaitAsync(cancellationToken));
        Assert.False(result.RootElement.TryGetProperty("exceptionDetails", out _), result.RootElement.ToString());
    }

    private static async Task<bool> ReadBooleanAsync(CoreWebView2 core, string expression)
    {
        using var result = JsonDocument.Parse(await core.ExecuteScriptAsync(expression).WaitAsync(TimeSpan.FromSeconds(3)));
        return result.RootElement.ValueKind == JsonValueKind.True;
    }

    private static async Task<string> ReadStateAsync(CoreWebView2 core, Window window, WebView2HostControl host)
    {
        var dom = await core.ExecuteScriptAsync("""
            (() => {
              const child = document.querySelector('iframe')?.contentWindow;
              const read = w => ({ state: w.probeState, secureContext: w.isSecureContext,
                keyboardType: typeof w.navigator.keyboard, lockType: typeof w.navigator.keyboard?.lock,
                focused: w.document.hasFocus(), activeElement: w.document.activeElement?.tagName,
                visibility: w.document.visibilityState, fullscreen: !!w.document.fullscreenElement,
                fullscreenEnabled: w.document.fullscreenEnabled, active: w.navigator.userActivation.isActive });
              return { userAgent: navigator.userAgent, top: read(window), iframe: child ? read(child) : null };
            })()
            """).WaitAsync(TimeSpan.FromSeconds(3));
        return $"windowActive={window.IsActive}; hostFullscreen={host.ContainsFullscreenElement}; coreFullscreen={core.ContainsFullScreenElement}; DOM={dom}";
    }

    private static async Task UntilAsync(Func<Task<bool>> predicate, CancellationToken cancellationToken)
    {
        while (!await predicate()) await Task.Delay(50, cancellationToken);
    }

    private static Task RunStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); completion.SetResult(); }
                catch (Exception exception) { completion.SetException(exception); }
                finally { dispatcher.InvokeShutdown(); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private sealed class FixtureNavigationPolicy : INavigationPolicy
    {
        public NavigationDecision Evaluate(in NavigationPolicyRequest request) =>
            request.Target.GetLeftPart(UriPartial.Authority) == FixtureOrigin
                ? NavigationDecision.Allow : NavigationDecision.Block;
    }

    private static string BuildHtml(bool frame) =>
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>Isolated keyboard-lock probe</title></head><body>" +
        "<input id=\"focusTarget\" aria-label=\"Probe focus target\">" +
        (frame ? string.Empty : "<iframe src=\"/frame\" allow=\"fullscreen\" allowfullscreen></iframe>") +
        "<script>" + FixtureScript + "</script></body></html>";

    private const string FixtureScript = """
        window.probeState = { permission: { status: 'not-started' }, lock: { status: 'not-started' },
          fullscreen: { status: 'not-started' }, calls: [], keys: [], fullscreenEvents: [] };
        const errorResult = error => ({ status: 'rejected', name: error?.name, message: error?.message, text: String(error) });
        window.readPermission = () => {
          probeState.permission = { status: 'pending' };
          try {
            navigator.permissions.query({ name: 'keyboard-lock' }).then(
              result => { probeState.permission = { status: 'fulfilled', state: result.state }; },
              error => { probeState.permission = errorResult(error); });
          } catch (error) { probeState.permission = errorResult(error); }
        };
        window.beginProbe = () => {
          window.focus(); document.getElementById('focusTarget').focus();
          probeState.activationAtStart = navigator.userActivation.isActive;
          probeState.focusedAtStart = document.hasFocus();
          probeState.fullscreenBeforeLock = !!document.fullscreenElement;
          probeState.lock = { status: 'pending' };
          probeState.calls.push('keyboard.lock([Escape])');
          if (typeof navigator.keyboard?.lock !== 'function') {
            probeState.lock = { status: 'unsupported', lockType: typeof navigator.keyboard?.lock };
          } else {
            try {
              navigator.keyboard.lock(['Escape']).then(
                () => { probeState.lock = { status: 'fulfilled' }; },
                error => { probeState.lock = errorResult(error); });
            } catch (error) { probeState.lock = errorResult(error); }
          }
          // Invoke lock first, then request fullscreen synchronously within the
          // same injected activation. Capture each independent Promise result.
          probeState.activationBeforeFullscreen = navigator.userActivation.isActive;
          probeState.calls.push('requestFullscreen');
          probeState.fullscreen = { status: 'pending' };
          try {
            document.documentElement.requestFullscreen().then(
              () => { probeState.fullscreen = { status: 'fulfilled' }; },
              error => { probeState.fullscreen = errorResult(error); });
          } catch (error) { probeState.fullscreen = errorResult(error); }
        };
        for (const type of ['keydown', 'keyup']) document.addEventListener(type, event => {
          probeState.keys.push({ type: event.type, key: event.key, code: event.code,
            isTrusted: event.isTrusted, target: event.target.tagName, fullscreen: !!document.fullscreenElement });
        }, true);
        document.addEventListener('fullscreenchange', () => probeState.fullscreenEvents.push(!!document.fullscreenElement));
        requestAnimationFrame(() => requestAnimationFrame(() => { window.fixtureReady = true; }));
        """;
}

internal sealed class KeyboardLockRuntimeFactAttribute : FactAttribute
{
    public KeyboardLockRuntimeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ORBIT_RUN_WEBVIEW_KEYBOARD_LOCK_PROBE") != "1")
            Skip = "Opt-in isolated WebView2 capability probe. Set ORBIT_RUN_WEBVIEW_KEYBOARD_LOCK_PROBE=1 to run.";
    }
}
