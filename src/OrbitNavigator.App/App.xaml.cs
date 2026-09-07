using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using OrbitNavigator.App.Accounts;
using OrbitNavigator.App.Composition;
using OrbitNavigator.App.Diagnostics;
using OrbitNavigator.App.Updates;
using OrbitNavigator.Contracts.Accounts;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Foundation.Diagnostics;
using OrbitNavigator.Foundation.Offline;
using OrbitNavigator.Foundation.Profiles;
using OrbitNavigator.Foundation.Browser;
using OrbitNavigator.Foundation.Runtime;
using OrbitNavigator.Foundation.Security;
using OrbitNavigator.WebViewHost;
using OrbitNavigator.WebViewHost.Navigation;
using OrbitNavigator.WebViewHost.Permissions;
using OrbitNavigator.Privacy.Permissions;
using OrbitNavigator.Privacy.Protection;
using OrbitNavigator.Sync.Accounts;
using OrbitNavigator.Updates;

namespace OrbitNavigator.App;

public partial class App : Application
{
    private JsonLineLocalDiagnostics? _diagnostics;
    private LocalPrivacyComposition? _localPrivacy;
    private IMyOrbitAccountConnectionController? _myOrbitAccount;
    private MyOrbitAccountSettingsAdapter? _myOrbitAccountSettings;
    private readonly CancellationTokenSource _myOrbitAccountLifetime = new();
    private readonly CancellationTokenSource _updateLifetime = new();
    private BetaUpdateClient? _betaUpdates;
    private Task? _updateScheduler;
    private LocalDataRootInstanceLease? _instanceLease;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        WindowsAppIdentity.ApplyCurrentProcessIdentity();
        var acceptance = AcceptanceRunContext.Create(
            e.Args,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.GetTempPath(),
            Environment.GetEnvironmentVariable(LocalDataRootResolver.AcceptanceEnvironmentVariable));
        var paths = acceptance.Paths;
        _instanceLease = LocalDataRootInstanceLease.TryAcquire(paths.Root);
        if (_instanceLease is null)
        {
            MessageBox.Show(
                "Orbit Navigator is already using this browser profile.",
                "Orbit Navigator",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(2);
            return;
        }
        await acceptance.WriteAttestationAsync();
        IBrowserWorkspaceAuditSink? workspaceAudit = acceptance.IsAcceptance
            ? new FileBrowserWorkspaceAuditSink(Path.Combine(
                paths.Root,
                "audit",
                "workspace-state.jsonl"))
            : null;
        _diagnostics = new JsonLineLocalDiagnostics(Path.Combine(paths.LogsRoot, "foundation.jsonl"));
        await _diagnostics.WriteAsync(new LocalDiagnosticEvent(
            DateTimeOffset.UtcNow,
            LocalDiagnosticSeverity.Information,
            LocalDiagnosticCode.ApplicationStarted));

        if (e.Args.Any(argument => string.Equals(
            argument,
            "--webview-smoke",
            StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var resultPath = Environment.GetEnvironmentVariable("ORBIT_WEBVIEW_SMOKE_RESULT");
            var exitCode = await WebView2RuntimeSmoke.RunAsync(paths.Root, resultPath);
            Shutdown(exitCode);
            return;
        }

        try
        {
        var profileId = await new LocalProfileIdentityStore(
            Path.Combine(paths.ProfilesRoot, "default.id")).LoadOrCreateAsync();
        var profileStorage = new FileProfileStorage(paths.ProfileStorageRoot);
        var clock = new SystemClock();
        var bookmarks = new BookmarksFacade(profileStorage, clock);
        var history = new HistoryFacade(profileStorage);
        var settings = new BrowserSettingsFacade(profileStorage);
        var workspacePreferences = new WorkspaceUiPreferencesStore(profileStorage);
        var affiliatedSitesVisibility = new AffiliatedSitesVisibilityStore(profileStorage);
        var workspacePresets = new WorkspacePresetStore(profileStorage, clock);
        var workspaceArtwork = new WorkspaceArtworkStore(profileStorage);
        var workspaceSessions = new BrowserWorkspaceSessionStore(profileStorage);
        var offlineReading = new OfflineReadingStore(
            paths.OfflineReadingRoot,
            clock);
        var localPrivacy = new LocalPrivacyComposition(profileStorage, clock);
        _localPrivacy = localPrivacy;
        var optionalSync = OptionalSyncComposition.SignedOut();
        var myOrbitAuthority = new Uri("https://my-orbit.snap-it.cc/");
        var providerOptions = MyOrbitAccountProviderOptions.Create(myOrbitAuthority);
        _myOrbitAccount = providerOptions.IsSuccess
            ? MyOrbitAccountConnectionController.Create(
                providerOptions.Value!,
                profileStorage,
                new WindowsDataProtection(),
                clock,
                new WindowsMyOrbitSystemBrowserLauncher(myOrbitAuthority))
            : MyOrbitAccountConnectionController.ProviderUnavailable();
        _myOrbitAccountSettings = new MyOrbitAccountSettingsAdapter(
            _myOrbitAccount,
            clock,
            _myOrbitAccountLifetime.Token);
        _betaUpdates = new BetaUpdateClient(
            HttpSignedUpdateManifestSource.CreatePrivacyPreservingClient(TimeSpan.FromSeconds(45)),
            new FileUpdateClientStateStore(Path.Combine(paths.UpdatesRoot, "client-state.json")),
            BetaUpdateTrust.ManifestKey,
            new WindowsAuthenticodeTrustInspector(),
            new VisibleUpdateInstallerLauncher(),
            Path.Combine(paths.UpdatesRoot, "staging"),
            typeof(App).Assembly.GetName().Version ?? new Version(0, 0));
        await _betaUpdates.InitializeAsync(_updateLifetime.Token);
        _updateScheduler = _betaUpdates.RunScheduledChecksAsync(_updateLifetime.Token);
        var lifecycle = new WebViewProfileLifecycle(paths.WebViewRoot);
        var privateWindows = new LocalPrivateWindowLifecycle(lifecycle);

        async Task<PreparedWebViewHost?> PrepareHostAsync(
            BrowsingContext context,
            SiteProtectionController protection,
            PermissionBroker permissions,
            PermissionCompletionRegistry completions)
        {
            var lease = await lifecycle.AcquireAsync(context.Privacy);
            if (!lease.IsSuccess) return null;
            var host = new WebView2HostControl(
                context,
                new HostNavigationGuard(new SiteProtectionNavigationPolicy(protection)),
                permissions,
                completions,
                _diagnostics);
            return new PreparedWebViewHost(host, lease.Value!);
        }

        async Task<FoundationWindow?> CreateWindowAsync(PrivacyContext privacy, BrowserWindowId windowId)
        {
            var persistence = new PrivacyPersistenceComposition(profileId, profileStorage);
            var protection = new SiteProtectionController(clock, persistence.SiteProtectionRules);
            var completions = new PermissionCompletionRegistry();
            var permissions = new PermissionBroker(clock, completions, persistence.PermissionRules);
            if (!(await protection.HydrateProfileAsync(privacy)).IsSuccess ||
                !(await permissions.HydrateProfileAsync(privacy)).IsSuccess)
                return null;

            BrowserWorkspaceSessionSnapshot? restoredSession = null;
            if (!privacy.IsPrivate)
            {
                var loadedSession = await workspaceSessions.LoadAsync(privacy);
                if (loadedSession.IsSuccess)
                {
                    restoredSession = loadedSession.Value;
                    windowId = restoredSession!.WindowId;
                }
            }

            var restoredTabs = restoredSession?.Tabs.Select(tab =>
                new OrbitNavigator.Contracts.Browser.BrowserTabState(
                    tab.TabId,
                    tab.GroupId,
                    tab.Address,
                    tab.Title,
                    OrbitNavigator.Contracts.Browser.BrowserLoadState.Idle,
                    false,
                    false,
                    false)).ToArray();
            var initialTabId = restoredTabs is { Length: > 0 }
                ? restoredTabs[0].TabId
                : new BrowserTabId(Guid.NewGuid());
            SiteIdentity? initialSite = null;
            if (restoredTabs is { Length: > 0 } && restoredTabs[0].Address is { } restoredAddress)
            {
                SiteIdentity.TryCreate(restoredAddress, out initialSite);
            }
            var browsing = new BrowsingContext(privacy, windowId, initialTabId, initialSite);
            var initialHost = await PrepareHostAsync(browsing, protection, permissions, completions);
            if (initialHost is null) return null;

            var tabGroups = new TabGroupMetadataStore(profileStorage);
            var initialState = new OrbitNavigator.Contracts.Browser.BrowserState(
                windowId,
                restoredSession?.SelectedTabId ?? browsing.TabId,
                restoredTabs is { Length: > 0 }
                    ? restoredTabs
                    : [new OrbitNavigator.Contracts.Browser.BrowserTabState(
                        browsing.TabId,
                        null,
                        null,
                        privacy.IsPrivate ? "Private tab" : "New Tab",
                        OrbitNavigator.Contracts.Browser.BrowserLoadState.Idle,
                        false,
                        false,
                        privacy.IsPrivate)]);
            var workspace = await BrowserWorkspaceCoordinator.CreateAsync(
                privacy,
                windowId,
                initialState,
                tabGroups,
                privacy.IsPrivate ? null : workspaceSessions,
                restoredSession);
            if (!workspace.IsSuccess)
            {
                await initialHost.DisposeAsync();
                await tabGroups.DisposeAsync();
                return null;
            }
            if (!privacy.IsPrivate)
            {
                var authoritativeSession = await workspaceSessions.LoadAsync(privacy);
                if (!authoritativeSession.IsSuccess)
                {
                    await workspace.Value!.DisposeAsync();
                    await initialHost.DisposeAsync();
                    return null;
                }
                await new LegacyWorkspaceMetadataRetirement(
                    profileStorage,
                    profileStorage).RetireAsync(privacy, authoritativeSession.Value!);
            }

            var ux = new UxComposition();
            return new FoundationWindow(
                initialHost,
                ux,
                persistence,
                browsing,
                workspace.Value!,
                restoredSession,
                new TabControllerLayoutStore(profileStorage),
                context => PrepareHostAsync(context, protection, permissions, completions),
                privateWindows,
                session => CreateWindowAsync(session.Context, session.WindowId),
                ux.CreatePermissionPrompts(permissions),
                ux.CreateSiteProtection(protection),
                ux.CreateClipboardShelf(localPrivacy.ClipboardShelf),
                bookmarks,
                history,
                settings,
                workspacePreferences,
                affiliatedSitesVisibility,
                workspacePresets,
                workspaceArtwork,
                offlineReading,
                localPrivacy,
                _myOrbitAccountSettings,
                _betaUpdates,
                optionalSync,
                AppContext.BaseDirectory,
                () => RestartApplication(e.Args),
                workspaceAudit);
        }

        var privacy = new PrivacyContext(profileId, new BrowserSessionId(Guid.NewGuid()), BrowserProfileMode.Normal);
        var window = await CreateWindowAsync(
            privacy,
            new BrowserWindowId(Guid.NewGuid())).WaitAsync(TimeSpan.FromSeconds(20));
        if (window is null)
        {
            await WriteNormalLaunchProbeAsync(false, "error.application.startup_unavailable");
            ShowStartupFailure();
            Shutdown(1);
            return;
        }
        MainWindow = window;
        window.Show();
        var ready = await window.InitialHostReady;
        if (ready)
        {
            await window.StartupOverlayCleared.WaitAsync(TimeSpan.FromSeconds(2));
        }
        await WriteNormalLaunchProbeAsync(
            ready,
            ready ? null : "error.webview_host.initialization_failed",
            window);
        }
        catch
        {
            await WriteNormalLaunchProbeAsync(false, "error.application.startup_exception");
            ShowStartupFailure();
            Shutdown(1);
        }
    }

    private static void ShowStartupFailure() =>
        MessageBox.Show(
            "Orbit Navigator could not start. Close the app and try again; reinstall it if the problem continues.",
            "Orbit Navigator",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

    private static void RestartApplication(IReadOnlyList<string> arguments)
    {
        var payloadDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        var installationRoot = Directory.GetParent(payloadDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar))?.FullName;
        var rootLauncher = installationRoot is null
            ? null
            : Path.Combine(installationRoot, "Orbit Navigator.exe");
        var target = rootLauncher is not null && File.Exists(rootLauncher)
            ? rootLauncher
            : Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(target) || !File.Exists(target))
        {
            throw new InvalidOperationException("The Orbit Navigator restart target is unavailable.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = target,
            WorkingDirectory = Path.GetDirectoryName(target)!,
            UseShellExecute = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        Process.Start(startInfo);
    }

    private static async Task WriteNormalLaunchProbeAsync(
        bool success,
        string? errorMessageKey,
        FoundationWindow? window = null)
    {
        var resultPath = Environment.GetEnvironmentVariable("ORBIT_NORMAL_LAUNCH_RESULT");
        if (string.IsNullOrWhiteSpace(resultPath))
        {
            return;
        }

        var resolvedPath = Path.GetFullPath(resultPath);
        Directory.CreateDirectory(Path.GetDirectoryName(resolvedPath)!);
        await File.WriteAllTextAsync(
            resolvedPath,
            JsonSerializer.Serialize(
                new
                {
                    Success = success,
                    WindowVisible = Current?.MainWindow?.IsVisible == true,
                    HostInitialized = success,
                    StartupOverlayCleared = window is not null && window.StartupOverlayCleared.IsCompletedSuccessfully &&
                        window.StartupOverlayCleared.Result,
                    StartupSequenceFrameCount = window?.StartupSequenceFrameCount ?? 0,
                    StartupLoadingPhase = window?.StartupLoadingPhase.ToString(),
                    ReducedMotion = window?.ReducedMotion ?? false,
                    AppUserModelId = WindowsAppIdentity.AppliedAppUserModelId,
                    ErrorMessageKey = errorMessageKey,
                },
                new JsonSerializerOptions { WriteIndented = true }));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _updateLifetime.Cancel();
        if (_updateScheduler is not null)
        {
            try { _updateScheduler.Wait(TimeSpan.FromSeconds(2)); }
            catch (AggregateException exception) when (
                exception.InnerExceptions.All(inner => inner is OperationCanceledException)) { }
            _updateScheduler = null;
        }
        if (_betaUpdates is not null)
        {
            _betaUpdates.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _betaUpdates = null;
        }
        _myOrbitAccountLifetime.Cancel();
        if (_myOrbitAccount is IAsyncDisposable asyncDisposable)
        {
            var disposal = asyncDisposable.DisposeAsync().AsTask();
            try
            {
                // Provider work receives the cancelled application token. Give it a
                // bounded grace period, then still wait for the controller's mandatory
                // in-memory credential clearing before returning from WPF OnExit.
                disposal.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            }
            catch (TimeoutException)
            {
                disposal.GetAwaiter().GetResult();
            }
            _myOrbitAccount = null;
            _myOrbitAccountSettings = null;
        }

        if (_localPrivacy is not null)
        {
            _localPrivacy.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _localPrivacy = null;
        }

        if (_diagnostics is not null)
        {
            _diagnostics.WriteAsync(new LocalDiagnosticEvent(
                DateTimeOffset.UtcNow,
                LocalDiagnosticSeverity.Information,
                LocalDiagnosticCode.ApplicationStopped)).AsTask().GetAwaiter().GetResult();
            _diagnostics.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _myOrbitAccountLifetime.Dispose();
        _updateLifetime.Dispose();
        _instanceLease?.Dispose();
        _instanceLease = null;
        base.OnExit(e);
    }
}
