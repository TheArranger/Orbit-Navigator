using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using OrbitNavigator.App.Composition;
using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Foundation.Profiles;
using OrbitNavigator.Presentation.Wpf;
using OrbitNavigator.WebViewHost;
using OrbitNavigator.WebViewHost.Navigation;
using Xunit;
using Xunit.Abstractions;

namespace OrbitNavigator.App.Tests.Fullscreen;

/// <summary>
/// Manual OS-input acceptance surface. Never runs by default and never injects
/// keys, clicks, script evaluation, user activation, or permission grants.
/// A successful test means the operator completed the harness, not that every
/// keyboard behavior passed. Consult the separately recorded operator verdicts.
/// </summary>
public sealed class WebViewKeyboardManualAcceptanceTests(ITestOutputHelper output)
{
    private const string FixtureOrigin = "https://keyboard-acceptance.orbit.test";

    [KeyboardManualAcceptanceFact]
    [Trait("Category", "WebView2ManualAcceptance")]
    public Task ManualInputThroughRealBrowserShellReportsEscapeBehavior() => RunStaAsync(async () =>
    {
        Assert.True(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS")),
            "This acceptance fixture must not inherit additional browser arguments.");
        var runId = Guid.NewGuid();
        var acceptanceParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "OrbitNavigatorAcceptance"));
        var runRoot = Path.GetFullPath(Path.Combine(acceptanceParent, runId.ToString("N")));
        Assert.Equal(Path.Combine(acceptanceParent, runId.ToString("N")), runRoot);
        Assert.False(Directory.Exists(runRoot), "Manual acceptance requires a fresh unique run root.");
        var acceptance = AcceptanceRunContext.Create(
            ["--acceptance-profile-root", Path.Combine(runRoot, "profile"), "--acceptance-run-id", runId.ToString("D")],
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Path.GetTempPath(), null);
        using var rootLease = LocalDataRootInstanceLease.TryAcquire(acceptance.Paths.Root);
        Assert.NotNull(rootLease);
        var privacy = new PrivacyContext(new ProfileId(Guid.NewGuid()), new BrowserSessionId(Guid.NewGuid()), BrowserProfileMode.Private);
        var context = new BrowsingContext(privacy, new BrowserWindowId(Guid.NewGuid()), new BrowserTabId(Guid.NewGuid()), null);
        var lifecycle = new WebViewProfileLifecycle(acceptance.Paths.WebViewRoot);
        var acquired = await lifecycle.AcquireAsync(privacy);
        Assert.True(acquired.IsSuccess, acquired.Error?.MessageKey);
        var lease = acquired.Value!;
        var profilePath = lease.Descriptor.UserDataFolder;
        var host = new WebView2HostControl(context, new HostNavigationGuard(new FixtureNavigationPolicy()));
        var chrome = new BrowserChromeControl { WebContent = host, ReducedMotion = true };
        chrome.SetBrowsingContext(context);
        var window = new Window
        {
            Title = "Orbit keyboard acceptance — disposable fixture",
            Content = chrome, Width = 1180, Height = 900, MinWidth = 800, MinHeight = 650,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowActivated = false, ShowInTaskbar = true,
        };
        using var nativeChrome = new BrowserWindowChromeController(window, chrome.ApplyWindowState);
        var fullscreen = new WindowFullscreenController(window, enabled =>
        {
            nativeChrome.ApplyFullscreen(enabled);
            chrome.ApplyFullscreen(enabled);
        });
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = false;
        var ownsLease = false;
        var messageCount = 0;
        var permissionCount = 0;
        var latestSnapshot = "No fixture input received.";
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        void RecordHost(string kind) => output.WriteLine(JsonSerializer.Serialize(new
        {
            fixture = "orbit-keyboard-acceptance-v1", kind,
            hostFullscreen = host.ContainsFullscreenElement, nativeFullscreen = fullscreen.IsFullscreen,
            chromeFullscreen = chrome.IsFullscreen, windowActive = window.IsActive,
        }));
        host.FullscreenChanged += (_, args) =>
        {
            fullscreen.Update(host, isSelected: true, args.IsFullscreen);
            RecordHost("host-fullscreen-change");
        };
        chrome.FullscreenExitRequested += (_, _) =>
        {
            RecordHost("chrome-exit-request");
            fullscreen.Exit();
            _ = host.ExitFullscreenAsync();
        };
        chrome.WindowCloseRequested += (_, _) => completion.TrySetResult("aborted-by-window-close");
        chrome.WindowMinimizeRequested += (_, _) => nativeChrome.Minimize();
        chrome.WindowMaximizeRestoreRequested += (_, _) => nativeChrome.ToggleMaximizeRestore();
        chrome.WindowDragRequested += (_, args) => nativeChrome.BeginCaptionDrag(args);
        window.Closed += (_, _) => { closed = true; completion.TrySetResult("aborted-by-window-close"); };
        try
        {
            await acceptance.WriteAttestationAsync(deadline.Token);
            output.WriteLine("Attested disposable root: " + acceptance.Paths.Root);
            output.WriteLine("Manual OS-routed input only. No CDP/input synthesis/user-activation injection by this fixture.");
            output.WriteLine("Chrome-origin Escape is covered separately by BrowserChromeVisualTests.FullscreenEscapeRequestsOwnerExit; this page does not claim that regression ran.");
            window.Show();
            var initialized = await host.InitializeAsync(lease, deadline.Token);
            Assert.True(initialized.IsSuccess, initialized.Error?.MessageKey);
            ownsLease = true;
            var core = Assert.IsType<CoreWebView2>(host.CoreWebView);
            output.WriteLine(JsonSerializer.Serialize(new
            {
                runtime = core.Environment.BrowserVersionString,
                appProduct = FileVersionInfo.GetVersionInfo(typeof(WindowFullscreenController).Assembly.Location).ProductVersion,
                presentationProduct = FileVersionInfo.GetVersionInfo(typeof(BrowserChromeControl).Assembly.Location).ProductVersion,
                runId,
            }));
            core.PermissionRequested += (_, args) =>
            {
                // The real host has already applied its normal fail-closed policy.
                // Never override State, SavesInProfile, Handled, or take a deferral.
                permissionCount++;
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    kind = "permission-observation", permission = args.PermissionKind.ToString(),
                    numericKind = (int)args.PermissionKind, state = args.State.ToString(), args.SavesInProfile,
                }));
            };
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
            core.WebMessageReceived += (_, args) =>
            {
                if (!Uri.TryCreate(args.Source, UriKind.Absolute, out var source) ||
                    source.GetLeftPart(UriPartial.Authority) != FixtureOrigin) return;
                var json = args.WebMessageAsJson;
                if (json.Length > 32_768) return;
                using var message = JsonDocument.Parse(json);
                if (!message.RootElement.TryGetProperty("fixture", out var marker) ||
                    marker.GetString() != "orbit-keyboard-acceptance-v1") return;
                var kind = message.RootElement.GetProperty("kind").GetString();
                // Preserve completion even if many manual taps exhausted the
                // bounded diagnostic-message budget.
                if (++messageCount > 200 && kind is not ("complete" or "abort")) return;
                latestSnapshot = json;
                output.WriteLine(json);
                if (kind is "complete" or "abort") completion.TrySetResult(kind);
            };
            Assert.True((await host.NavigateAsync(new Uri(FixtureOrigin + "/"), deadline.Token)).IsSuccess);
            var result = await completion.Task.WaitAsync(deadline.Token);
            output.WriteLine("Manual harness completion: " + result + "; permission events: " + permissionCount);
            Assert.Equal("complete", result);
        }
        catch (OperationCanceledException)
        {
            output.WriteLine("Four-minute acceptance deadline reached. Last safe fixture snapshot: " + latestSnapshot);
            throw new Xunit.Sdk.XunitException("Manual acceptance timed out; it is not a behavior pass.");
        }
        finally
        {
            // Restore the native window immediately even if the renderer is slow.
            fullscreen.Exit();
            try { await host.ExitFullscreenAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception error) { output.WriteLine("Bounded fullscreen cleanup: " + error.GetType().Name); }
            chrome.WebContent = null;
            chrome.DisconnectPresentation();
            window.Content = null;
            try { await host.DisposeAsync(); }
            finally
            {
                if (!ownsLease) await lease.DisposeAsync();
                if (!closed) window.Close();
                await lifecycle.EndSessionAsync(privacy);
                var deleted = !Directory.Exists(profilePath);
                output.WriteLine("Private WebView profile deleted: " + deleted);
                // Only known, empty, test-owned ancestors are removed. No broad
                // recursive deletion or process termination is used for cleanup.
                if (deleted)
                {
                    File.Delete(Path.Combine(acceptance.Paths.Root, "acceptance-root-attestation.json"));
                    foreach (var directory in new[]
                    {
                        Path.GetDirectoryName(profilePath)!, Path.Combine(acceptance.Paths.WebViewRoot, "private"),
                        acceptance.Paths.WebViewRoot, acceptance.Paths.Root, runRoot,
                    })
                    {
                        Assert.StartsWith(runRoot, Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase);
                        if (Directory.Exists(directory)) Directory.Delete(directory);
                    }
                }
                Assert.True(deleted, "The manual fixture's private WebView profile was not deleted: " + profilePath);
            }
        }
    });

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
        <!doctype html><html><head><meta charset="utf-8"><title>Orbit keyboard acceptance</title>
        <style>
        *{box-sizing:border-box}body{font:16px system-ui;margin:0;padding:20px;background:#101827;color:#eef5ff}
        h1{font-size:25px;margin:0 0 8px}p{margin:7px 0}button,select{font:inherit;padding:10px;margin:6px 8px 6px 0}
        button{cursor:pointer}#status{white-space:pre-wrap;background:#203249;padding:12px;border-radius:6px;min-height:90px}
        iframe{display:block;width:100%;height:190px;border:3px solid #73c8ff;margin:10px 0}small{color:#bfd3eb}
        label{display:inline-block;margin-right:15px}.verdicts{border-top:1px solid #54708f;margin-top:10px;padding-top:6px}
        </style></head><body>
        <h1>Orbit keyboard acceptance — disposable fixture</h1>
        <p>Real browser shell. Fake HTTPS page. No accounts/network content. Auto-closes after 4 minutes.</p>
        <button id="locked" disabled>Enter locked fullscreen</button><button id="ordinary">Enter ordinary fullscreen</button>
        <button id="exit">Exit fullscreen now</button>
        <div id="status">Checking Keyboard Lock support without requesting permission…</div>
        <iframe id="child" src="/frame" allow="fullscreen" allowfullscreen title="Same-origin keyboard target"></iframe>
        <p>1. Locked: click the locked button, then tap Escape in the child input. Both child counters should rise; fullscreen should remain.</p>
        <p>2. Ordinary: exit, click ordinary fullscreen, then tap Escape. The browser should exit fullscreen.</p>
        <p>3. Locked again: a person must hold Escape continuously for at least 3 seconds. A short-key automation helper or repeated taps cannot test this.</p>
        <small>Only Escape metadata is recorded, never input text. Chrome-origin Escape has a separate WPF regression; it is not asserted by this page.</small>
        <div class="verdicts">
          <label>Locked short Escape <select id="short"><option>Not tested</option><option>Pass</option><option>Fail</option></select></label>
          <label>Ordinary Escape exit <select id="ordinaryVerdict"><option>Not tested</option><option>Pass</option><option>Fail</option></select></label>
          <label>Long-hold exit <select id="hold"><option>Not tested</option><option>Pass</option><option>Fail</option></select></label>
        </div>
        <p><small>Partial runs are valid evidence: leave unperformed checks as Not tested, then complete. Harness completion is not a behavior pass.</small></p>
        <button id="complete">Complete manual run</button><button id="abort">Abort and close</button>
        <script>
        const state = { permission:'checking', lockType:typeof navigator.keyboard?.lock, attempts:[], events:[], down:0, up:0, repeat:0 };
        let current = null, escapeDownAt = null, sequence = 0;
        const time = () => Math.round(performance.now());
        const errorValue = e => ({ name:e?.name, message:e?.message });
        const verdicts = () => ({ lockedShort:document.getElementById('short').value,
          ordinaryExit:document.getElementById('ordinaryVerdict').value, longHoldExit:document.getElementById('hold').value });
        const snapshot = () => ({ permission:state.permission, lockType:state.lockType, fullscreen:!!document.fullscreenElement,
          iframeFocused:!!document.getElementById('child').contentDocument?.hasFocus(),
          down:state.down, up:state.up, repeat:state.repeat, attempts:state.attempts, events:state.events,
          operatorVerdicts:verdicts(), inputEvidence:'operator-input; fixture injects none' });
        function report(kind) { render(); chrome.webview.postMessage({fixture:'orbit-keyboard-acceptance-v1',kind,at:time(),state:snapshot()}); }
        function event(kind, data={}) { state.events.push({kind,at:time(),attempt:current?.id,...data}); if(state.events.length>50)state.events.shift(); }
        function render() {
          document.getElementById('status').textContent = 'Fullscreen: '+(document.fullscreenElement?'YES':'NO')+
            ' | Keyboard Lock permission: '+state.permission+' | lock API: '+state.lockType+
            '\nChild Escape keydown: '+state.down+' | keyup: '+state.up+' | repeats: '+state.repeat+
            '\nAttempt: '+(current ? current.id+' '+current.mode+' | lock '+current.lock+' | fullscreen '+current.fullscreen : 'none')+
            '\nOperator verdicts are separate from raw event evidence.';
        }
        async function enter(mode, click) {
          if(document.fullscreenElement) { event('entry-refused-already-fullscreen'); report('entry-refused'); return; }
          if(state.attempts.length>=8) { event('attempt-limit'); report('attempt-limit'); return; }
          current = {id:++sequence,mode,clickTrusted:click.isTrusted,activeAtClick:navigator.userActivation.isActive,
            lock:mode==='locked'?'pending':'not-requested',fullscreen:'pending',down:0,up:0,repeat:0,keys:[],exitAt:null};
          const attempt=current; state.attempts.push(attempt); escapeDownAt=null;
          navigator.keyboard?.unlock?.();
          if(mode==='locked') {
            if(state.permission!=='granted'||state.lockType!=='function') {
              current.lock='not-requested-to-avoid-prompt'; current.fullscreen='not-requested'; report('lock-unavailable'); return;
            }
            try {
              navigator.keyboard.lock(['Escape']).then(()=>{attempt.lock='fulfilled';report('lock-result');},
                e=>{attempt.lock='rejected';attempt.lockError=errorValue(e);report('lock-result');});
            } catch(e) { attempt.lock='threw'; attempt.lockError=errorValue(e); }
          }
          event('fullscreen-request',{mode});
          try {
            await document.documentElement.requestFullscreen(); attempt.fullscreen='fulfilled';
            document.getElementById('child').contentWindow.focus();
            document.getElementById('child').contentDocument.getElementById('target').focus();
          } catch(e) { attempt.fullscreen='rejected';attempt.fullscreenError=errorValue(e); }
          report('fullscreen-result');
        }
        window.childEscape = e => {
          if(e.key!=='Escape')return;
          const at=time(), down=e.type==='keydown'; state[down?'down':'up']++; if(e.repeat)state.repeat++;
          if(down&&!e.repeat)escapeDownAt=at;
          const key={type:e.type,at,isTrusted:e.isTrusted,repeat:e.repeat,fullscreen:!!document.fullscreenElement,
            heldMs:escapeDownAt===null?null:at-escapeDownAt};
          if(current){current[down?'down':'up']++;if(e.repeat)current.repeat++;
            current.keys.push(key);if(current.keys.length>20)current.keys.shift();
            if(!down&&current.mode==='locked'&&current.lock==='fulfilled'&&key.fullscreen&&key.heldMs!==null&&key.heldMs<1000)
              current.observedShortEscapeDeliveredWhileFullscreen=true;
          }
          if(!e.repeat){event('child-escape',key);report('child-escape');}else render();
          if(!down)escapeDownAt=null;
        };
        document.addEventListener('fullscreenchange',()=>{
          const active=!!document.fullscreenElement;
          if(!active&&current){current.exitAt=time();current.escapeHeldMsAtExit=escapeDownAt===null?null:time()-escapeDownAt;}
          event('fullscreenchange',{active});report('fullscreen-change');
        });
        document.getElementById('locked').onclick=e=>enter('locked',e);
        document.getElementById('ordinary').onclick=e=>enter('ordinary',e);
        document.getElementById('exit').onclick=()=>{event('explicit-exit-button');navigator.keyboard?.unlock?.();if(document.fullscreenElement)document.exitFullscreen();report('explicit-exit');};
        document.getElementById('complete').onclick=()=>{event('operator-complete');report('complete');};
        document.getElementById('abort').onclick=()=>report('abort');
        for(const id of ['short','ordinaryVerdict','hold'])document.getElementById(id).onchange=()=>report('operator-verdict');
        function permissionError(e){state.permission='query-rejected';event('permission-query-error',errorValue(e));report('ready');}
        try { navigator.permissions.query({name:'keyboard-lock'}).then(result=>{
          state.permission=result.state;document.getElementById('locked').disabled=result.state!=='granted'||state.lockType!=='function';report('ready');
        },permissionError); } catch(e) { permissionError(e); }
        </script></body></html>
        """;

    private const string FrameHtml = """
        <!doctype html><html><head><meta charset="utf-8"><style>
        body{font:18px system-ui;background:#193a51;color:white;margin:15px}input{font:20px system-ui;padding:12px;width:95%;border:3px solid #92dcff}
        p{margin:8px 0}
        </style></head><body><p>Same-origin child iframe — Escape target</p>
        <input id="target" aria-label="Child iframe keyboard target" placeholder="Click here if focus is lost; then use Escape" autocomplete="off">
        <p id="counts">Escape keydown: 0 | keyup: 0</p><script>
        let down=0,up=0;for(const type of ['keydown','keyup'])document.addEventListener(type,e=>{
          if(e.key!=='Escape')return;if(type==='keydown')down++;else up++;
          document.getElementById('counts').textContent='Escape keydown: '+down+' | keyup: '+up;
          parent.childEscape(e);
        },true);
        </script></body></html>
        """;
}

internal sealed class KeyboardManualAcceptanceFactAttribute : FactAttribute
{
    public KeyboardManualAcceptanceFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ORBIT_RUN_WEBVIEW_KEYBOARD_MANUAL_ACCEPTANCE") != "1")
            Skip = "Visible manual-input fixture; launch only after idle authorization with ORBIT_RUN_WEBVIEW_KEYBOARD_MANUAL_ACCEPTANCE=1.";
    }
}
