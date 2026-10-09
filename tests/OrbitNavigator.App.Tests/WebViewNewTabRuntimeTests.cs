using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Foundation.Profiles;
using OrbitNavigator.WebViewHost;
using OrbitNavigator.WebViewHost.Navigation;
using Xunit;

namespace OrbitNavigator.App.Tests;

[Collection("WebView new-tab focus isolation")]
public sealed class WebViewNewTabRuntimeTests
{
    private const string FixtureOrigin = "https://new-tabs.orbit.test";

    [NewTabRuntimeFact]
    [Trait("Category", "WebView2Runtime")]
    public Task NativeMiddleClicksInPageAndIframeAndBlankLinksRequestOneBackgroundTab() => RunStaAsync(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "OrbitNavigator.NewTabSmoke", Guid.NewGuid().ToString("N"));
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
        // ShowActivated=false affects only the initial show. Native no-activate
        // style also prevents fallback activation when another test HWND closes.
        window.SourceInitialized += (_, _) => MakeNonActivatingToolWindow(window);
        var wasActivated = false;
        window.Activated += (_, _) => wasActivated = true;
        var ownsLease = false;
        CoreWebView2? core = null;
        var stage = "initialization";
        var requests = new List<WebViewNewTabRequestedEventArgs>();
        var nativeRequests = new List<(string Uri, bool Handled)>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try
        {
            // CDP sends input only to this isolated off-screen WebView. The test
            // never moves the system pointer, activates a window, or opens a
            // user's profile. Links still use Chromium's native default action.
            window.Show();
            var initialized = await host.InitializeAsync(lease, timeout.Token);
            Assert.True(initialized.IsSuccess, initialized.Error?.MessageKey);
            ownsLease = true;
            core = Assert.IsType<CoreWebView2>(host.CoreWebView);
            host.NewTabRequested += (_, args) => requests.Add(args);
            core.NewWindowRequested += (_, args) => nativeRequests.Add((args.Uri, args.Handled));
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, args) =>
            {
                var uri = new Uri(args.Request.Uri);
                var allowed = uri.GetLeftPart(UriPartial.Authority) == FixtureOrigin;
                var html = uri.AbsolutePath == "/frame" ? FrameHtml : MainHtml;
                args.Response = core.Environment.CreateWebResourceResponse(
                    new MemoryStream(Encoding.UTF8.GetBytes(allowed ? html : string.Empty)),
                    allowed ? 200 : 403, allowed ? "OK" : "Forbidden",
                    "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\n");
            };
            Assert.True((await host.NavigateAsync(new Uri(FixtureOrigin + "/"), timeout.Token)).IsSuccess);
            await UntilAsync(async () => await ReadBooleanAsync(core,
                "document.readyState === 'complete' && document.querySelector('iframe')?.contentDocument.readyState === 'complete'"), timeout.Token);
            // Renderer-only focus emulation enables off-screen CDP hit-testing;
            // it does not focus the HWND or claim to test a physical mouse.
            await core.CallDevToolsProtocolMethodAsync("Emulation.setFocusEmulationEnabled", "{\"enabled\":true}");
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", "{\"type\":\"mouseMoved\",\"x\":40,\"y\":35}");

            stage = "native top-level middle click";
            await ClickAsync(core, 40, 35, "middle");
            await UntilAsync(() => Task.FromResult(requests.Count == 1), timeout.Token);
            AssertRequest(requests[0], "/top-target");

            stage = "native iframe middle click";
            await ClickAsync(core, 40, 175, "middle");
            await UntilAsync(() => Task.FromResult(requests.Count == 2), timeout.Token);
            AssertRequest(requests[1], "/frame-target");

            stage = "native target=_blank middle click";
            await ClickAsync(core, 40, 95, "middle");
            await UntilAsync(() => Task.FromResult(requests.Count == 3), timeout.Token);
            AssertRequest(requests[2], "/blank-target");

            stage = "native target=_blank left click policy";
            await ClickAsync(core, 40, 95, "left");
            await UntilAsync(() => Task.FromResult(requests.Count == 4), timeout.Token);
            AssertRequest(requests[3], "/blank-target");

            stage = "policy-denied native middle click";
            await ClickAsync(core, 320, 35, "middle");
            await UntilAsync(() => Task.FromResult(nativeRequests.Count == 5), timeout.Token);

            // Allow any duplicate/default request to arrive before asserting.
            await Task.Delay(250, timeout.Token);
            Assert.Equal(4, requests.Count);
            Assert.Equal(5, nativeRequests.Count);
            Assert.All(nativeRequests, request => Assert.True(request.Handled));
            Assert.Equal(FixtureOrigin + "/", core.Source);
            Assert.Equal(privacy, host.Context.Privacy);
            Assert.False(window.IsActive);
            Assert.False(wasActivated, "The isolated fixture must never activate its native window.");
            Assert.Equal(-32_000, window.Left);
            Assert.True(await ReadBooleanAsync(core,
                "window.events.filter(e => e.type === 'auxclick' && e.trusted && e.button === 1).length === 3"));
            Assert.True(await ReadBooleanAsync(core,
                "document.querySelector('iframe').contentWindow.events.some(e => e.type === 'auxclick' && e.trusted && e.button === 1)"));
        }
        catch (Exception exception)
        {
            var dom = core is null ? "core unavailable" : await core.ExecuteScriptAsync("JSON.stringify({ events:window.events, frame:document.querySelector('iframe')?.contentWindow.events, width:innerWidth, height:innerHeight, visibility:document.visibilityState })").WaitAsync(TimeSpan.FromSeconds(3));
            throw new Xunit.Sdk.XunitException($"New-tab runtime smoke failed at {stage}: {exception.GetType().Name}: {exception.Message}; native={nativeRequests.Count}, host={requests.Count}, DOM={dom}");
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

    private static void AssertRequest(WebViewNewTabRequestedEventArgs request, string path)
    {
        Assert.Equal(FixtureOrigin + path, request.Target.AbsoluteUri);
        Assert.True(request.IsUserInitiated);
        Assert.False(request.Activate);
    }

    private static void MakeNonActivatingToolWindow(Window window)
    {
        const int extendedStyleIndex = -20;
        const long noActivate = 0x08000000, toolWindow = 0x00000080, appWindow = 0x00040000;
        var handle = new WindowInteropHelper(window).Handle;
        Assert.NotEqual(nint.Zero, handle);
        var existing = ReadWindowLong(handle, extendedStyleIndex);
        var style = (nint)((existing.ToInt64() | noActivate | toolWindow) & ~appWindow);
        if (Environment.Is64BitProcess) SetWindowLongPtr(handle, extendedStyleIndex, style);
        else SetWindowLong(handle, extendedStyleIndex, style.ToInt32());
        Assert.Equal(noActivate | toolWindow,
            ReadWindowLong(handle, extendedStyleIndex).ToInt64() & (noActivate | toolWindow));
        // Apply cached style changes without moving, sizing, activating, or
        // changing the z-order of this test-owned window.
        Assert.True(SetWindowPos(handle, nint.Zero, 0, 0, 0, 0, 0x0037));
    }

    private static nint ReadWindowLong(nint window, int index) => Environment.Is64BitProcess
        ? GetWindowLongPtr(window, index) : GetWindowLong(window, index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(nint window, int index, int value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    private static async Task ClickAsync(CoreWebView2 core, int x, int y, string button)
    {
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new
        {
            type = "mousePressed", x, y, button, buttons = button == "middle" ? 4 : 1, clickCount = 1,
        }));
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new
        {
            type = "mouseReleased", x, y, button, buttons = 0, clickCount = 1,
        }));
    }

    private static async Task<bool> ReadBooleanAsync(CoreWebView2 core, string expression)
    {
        using var value = JsonDocument.Parse(await core.ExecuteScriptAsync(expression));
        return value.RootElement.ValueKind == JsonValueKind.True;
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
        <!doctype html><html><head><style>html,body{margin:0;width:100%;height:100%}a{position:absolute;left:20px;width:240px;height:50px;display:block}iframe{position:absolute;left:0;top:140px;width:600px;height:240px;border:0}</style></head>
        <body><a style="top:10px" href="/top-target">Plain link</a><a style="top:70px" href="/blank-target" target="_blank">New window link</a><a style="left:300px;top:10px" href="https://blocked.orbit.test/">Blocked link</a><iframe src="/frame"></iframe><script>
        window.events=[]; for(const type of ['mousedown','mouseup','click','auxclick']) document.addEventListener(type,event=>events.push({type,trusted:event.isTrusted,button:event.button}),true);
        </script></body></html>
        """;

    private const string FrameHtml = """
        <!doctype html><html><head><style>html,body{margin:0}a{position:absolute;left:20px;top:10px;width:240px;height:50px;display:block}</style></head>
        <body><a href="/frame-target">Iframe link</a><script>
        window.events=[]; for(const type of ['mousedown','mouseup','click','auxclick']) document.addEventListener(type,event=>events.push({type,trusted:event.isTrusted,button:event.button}),true);
        </script></body></html>
        """;
}

[CollectionDefinition("WebView new-tab focus isolation", DisableParallelization = true)]
public sealed class WebViewNewTabFocusIsolationCollection { }

internal sealed class NewTabRuntimeFactAttribute : FactAttribute
{
    public NewTabRuntimeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ORBIT_RUN_WEBVIEW_NEW_TAB_SMOKE") != "1")
            Skip = "Opt-in isolated WebView2 runtime test. Set ORBIT_RUN_WEBVIEW_NEW_TAB_SMOKE=1 to run.";
    }
}
