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
using Xunit;

namespace OrbitNavigator.App.Tests.Fullscreen;

public sealed class WebViewFullscreenRuntimeTests
{
    private const string FixtureOrigin = "https://fullscreen.orbit.test";

    [FullscreenRuntimeFact]
    [Trait("Category", "WebView2Runtime")]
    public Task InjectedUserActivationExercisesIframeFullscreenAndHostExitPaths() => RunStaAsync(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "OrbitNavigator.FullscreenSmoke", Guid.NewGuid().ToString("N"));
        var privacy = new PrivacyContext(new ProfileId(Guid.NewGuid()), new BrowserSessionId(Guid.NewGuid()), BrowserProfileMode.Private);
        var context = new BrowsingContext(privacy, new BrowserWindowId(Guid.NewGuid()), new BrowserTabId(Guid.NewGuid()), null);
        var lifecycle = new WebViewProfileLifecycle(root);
        var acquired = await lifecycle.AcquireAsync(privacy);
        Assert.True(acquired.IsSuccess, acquired.Error?.MessageKey);
        var lease = acquired.Value!;
        var host = new WebView2HostControl(context, new HostNavigationGuard(new FixtureNavigationPolicy()));
        var window = new Window
        {
            Content = host, Width = 800, Height = 600, Left = -32_000, Top = -32_000,
            ShowActivated = false, ShowInTaskbar = false,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        var ownsLease = false;
        CoreWebView2? runtimeCore = null;
        var stage = "initialization";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        try
        {
            // Only this off-screen WebView receives protocol evaluation. There
            // is no global input, real page, account, or stream here. The test
            // injects activation and does not claim to test a physical click.
            window.Show();
            var initialized = await host.InitializeAsync(lease, timeout.Token);
            Assert.True(initialized.IsSuccess, initialized.Error?.MessageKey);
            ownsLease = true;
            var core = Assert.IsType<CoreWebView2>(host.CoreWebView);
            runtimeCore = core;
            var changes = new List<bool>();
            host.FullscreenChanged += (_, args) => changes.Add(args.IsFullscreen);
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, args) =>
            {
                var uri = new Uri(args.Request.Uri);
                var allowed = uri.GetLeftPart(UriPartial.Authority) == FixtureOrigin;
                var html = uri.AbsolutePath == "/frame" ? FrameHtml : uri.AbsolutePath == "/after" ? "<p>Navigation complete</p>" : MainHtml;
                // Intercept the fake HTTPS origin in memory; unexpected requests
                // fail locally instead of reaching DNS or another machine.
                args.Response = core.Environment.CreateWebResourceResponse(
                    new MemoryStream(Encoding.UTF8.GetBytes(allowed ? html : string.Empty)),
                    allowed ? 200 : 403, allowed ? "OK" : "Forbidden",
                    "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\n");
            };

            stage = "fixture navigation";
            var navigated = await host.NavigateAsync(new Uri(FixtureOrigin + "/"), timeout.Token);
            Assert.True(navigated.IsSuccess);
            await UntilAsync(async () => await ReadBooleanAsync(core,
                "Boolean(document.querySelector('iframe')?.contentWindow?.fixtureReady)"), timeout.Token);

            stage = "first iframe click with injected user activation";
            await ActivateFixtureButtonAsync(core);
            await UntilAsync(() => Task.FromResult(host.ContainsFullscreenElement), timeout.Token);
            Assert.Equal(new[] { true }, changes);
            Assert.False(await ReadBooleanAsync(core, "document.querySelector('iframe').contentWindow.testState.trusted"));
            Assert.True(await ReadBooleanAsync(core, "document.querySelector('iframe').contentWindow.testState.active"));
            Assert.True(await ReadBooleanAsync(core, "Boolean(document.querySelector('iframe').contentDocument.fullscreenElement)"));

            // The same button exits using the browser API, without any native
            // synthetic Escape key or a shell-level shortcut.
            stage = "iframe button exit";
            await ActivateFixtureButtonAsync(core);
            await UntilAsync(() => FullscreenExitedAsync(host, core), timeout.Token);
            Assert.Equal(new[] { true, false }, changes);

            stage = "host API exit";
            await ActivateFixtureButtonAsync(core);
            await UntilAsync(() => Task.FromResult(host.ContainsFullscreenElement), timeout.Token);
            await host.ExitFullscreenAsync();
            await UntilAsync(() => FullscreenExitedAsync(host, core), timeout.Token);
            Assert.Equal(new[] { true, false, true, false }, changes);

            stage = "navigation exit";
            await ActivateFixtureButtonAsync(core);
            await UntilAsync(() => Task.FromResult(host.ContainsFullscreenElement), timeout.Token);
            Assert.True((await host.NavigateAsync(new Uri(FixtureOrigin + "/after"), timeout.Token)).IsSuccess);
            await UntilAsync(async () => await FullscreenExitedAsync(host, core) &&
                await ReadBooleanAsync(core, "location.pathname === '/after' && document.readyState === 'complete'"), timeout.Token);
            Assert.Equal(new[] { true, false, true, false, true, false }, changes);
            Assert.False(window.IsActive);
            Assert.Equal(WindowState.Normal, window.WindowState);
            Assert.Equal(-32_000, window.Left);
        }
        catch (Exception exception)
        {
            var diagnostics = await ReadDiagnosticsAsync(host, runtimeCore, window);
            throw new Xunit.Sdk.XunitException($"Fullscreen runtime smoke failed at {stage}: {exception.GetType().Name}: {exception.Message}\n{diagnostics}");
        }
        finally
        {
            window.Content = null;
            await host.DisposeAsync();
            if (!ownsLease) await lease.DisposeAsync();
            window.Close();
            await lifecycle.EndSessionAsync(privacy);
        }
    });

    private static async Task ActivateFixtureButtonAsync(CoreWebView2 core)
    {
        // Off-screen WebView2 does not reliably route CDP mouse hit-testing.
        // Inject activation explicitly, then exercise the actual page listener,
        // Fullscreen API and native host events. This is not trusted-input proof.
        var parameters = JsonSerializer.Serialize(new
        {
            expression = "document.querySelector('iframe').contentDocument.getElementById('fullscreen').click()",
            userGesture = true,
            returnByValue = true,
        });
        using var result = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", parameters));
        Assert.False(result.RootElement.TryGetProperty("exceptionDetails", out _), result.RootElement.ToString());
    }

    private static async Task<bool> ReadBooleanAsync(CoreWebView2 core, string expression)
    {
        using var value = JsonDocument.Parse(await core.ExecuteScriptAsync(expression));
        return value.RootElement.ValueKind == JsonValueKind.True;
    }

    private static async Task<bool> FullscreenExitedAsync(WebView2HostControl host, CoreWebView2 core) =>
        !host.ContainsFullscreenElement && !core.ContainsFullScreenElement &&
        await ReadBooleanAsync(core, "!document.fullscreenElement");

    private static async Task<string> ReadDiagnosticsAsync(WebView2HostControl host, CoreWebView2? core, Window window)
    {
        var managed = $"Window active={window.IsActive}, visible={window.IsVisible}, bounds={window.Left},{window.Top},{window.ActualWidth},{window.ActualHeight}; hostFullscreen={host.ContainsFullscreenElement}";
        if (core is null) return managed + "; core unavailable";
        try
        {
            var dom = await core.ExecuteScriptAsync("""
                (() => {
                  const frame = document.querySelector('iframe'), child = frame?.contentWindow;
                  const button = child?.document.getElementById('fullscreen'), rect = button?.getBoundingClientRect();
                  return { top: { href: location.href, visibility: document.visibilityState, focused: document.hasFocus(),
                    enabled: document.fullscreenEnabled, fullscreen: !!document.fullscreenElement,
                    active: navigator.userActivation.isActive, width: innerWidth, height: innerHeight, events: window.fixtureEvents },
                    child: child ? { ready: child.fixtureReady, state: child.testState, focused: child.document.hasFocus(),
                      visibility: child.document.visibilityState, enabled: child.document.fullscreenEnabled,
                      fullscreen: !!child.document.fullscreenElement, active: child.navigator.userActivation.isActive,
                      width: child.innerWidth, height: child.innerHeight, events: child.fixtureEvents,
                      button: rect ? { x: rect.x, y: rect.y, width: rect.width, height: rect.height } : null } : null };
                })()
                """).WaitAsync(TimeSpan.FromSeconds(3));
            return $"{managed}; coreFullscreen={core.ContainsFullScreenElement}; DOM={dom}";
        }
        catch (Exception diagnosticError)
        {
            return managed + $"; diagnostics failed: {diagnosticError.GetType().Name}: {diagnosticError.Message}";
        }
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

    private const string MainHtml = """
        <!doctype html><html><head><style>html,body{margin:0;width:100%;height:100%;overflow:hidden}iframe{border:0;width:100%;height:100%;display:block}</style></head>
        <body><iframe src="/frame" allow="fullscreen" allowfullscreen></iframe><script>
        window.fixtureEvents=[]; for(const type of ['pointerdown','mousedown','mouseup','click']) document.addEventListener(type,event=>fixtureEvents.push({type,target:event.target.tagName,x:event.clientX,y:event.clientY}),true);
        </script></body></html>
        """;

    private const string FrameHtml = """
        <!doctype html><html><head><style>html,body{margin:0;background:#182034}button{position:absolute;left:30px;top:30px;width:240px;height:90px}</style></head>
        <body><button id="fullscreen">Toggle fullscreen</button><script>
        window.testState = {};
        window.fixtureEvents=[]; for(const type of ['pointerdown','mousedown','mouseup','click']) document.addEventListener(type,event=>fixtureEvents.push({type,target:event.target.tagName,x:event.clientX,y:event.clientY}),true);
        document.getElementById('fullscreen').addEventListener('click', async event => {
          window.testState = { trusted: event.isTrusted, active: navigator.userActivation.isActive };
          try {
            if (document.fullscreenElement) await document.exitFullscreen();
            else await document.documentElement.requestFullscreen();
          } catch (error) { window.testState.error = String(error); }
        });
        requestAnimationFrame(() => requestAnimationFrame(() => { window.fixtureReady = true; }));
        </script></body></html>
        """;
}

internal sealed class FullscreenRuntimeFactAttribute : FactAttribute
{
    public FullscreenRuntimeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ORBIT_RUN_WEBVIEW_FULLSCREEN_SMOKE") != "1")
            Skip = "Opt-in isolated WebView2 runtime test. Set ORBIT_RUN_WEBVIEW_FULLSCREEN_SMOKE=1 to run.";
    }
}
