using System.IO;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Privacy;
using OrbitNavigator.Foundation.Diagnostics;
using OrbitNavigator.Foundation.Resources;
using OrbitNavigator.WebViewHost.Navigation;
using OrbitNavigator.WebViewHost.Permissions;

namespace OrbitNavigator.WebViewHost;

/// <summary>
/// Foundation-owned WebView2 surface. Presentation surrounds this control in a
/// separate UX-owned project; this class contains only host initialization and
/// fail-closed event mapping.
/// </summary>
public sealed class WebView2HostControl : UserControl, IAsyncDisposable
{
    private const int MaximumOfflineSnapshotBytes = 25 * 1024 * 1024;
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private readonly WebView2 _webView = new();
    private BrowsingContext _context;
    private readonly HostNavigationGuard _navigation;
    private readonly ILocalDiagnostics _diagnostics;
    private readonly IPermissionBroker? _permissionBroker;
    private readonly PermissionCompletionRegistry _permissionCompletions;
    private IWebViewProfileLease? _profileLease;
    private CoreWebView2Environment? _environment;
    private Uri? _currentAddress;
    private string _documentTitle = string.Empty;
    private byte[] _faviconPng = [];
    private bool _isLoading;
    private long _visualRevision;
    private long _audioRevision;
    private long _faviconRequestGeneration;
    private int _disposed;

    public WebView2HostControl(
        BrowsingContext context,
        HostNavigationGuard? navigation = null,
        IPermissionBroker? permissionBroker = null,
        PermissionCompletionRegistry? permissionCompletions = null,
        ILocalDiagnostics? diagnostics = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _navigation = navigation ?? new HostNavigationGuard();
        _permissionBroker = permissionBroker;
        _permissionCompletions = permissionCompletions ?? new PermissionCompletionRegistry();
        _diagnostics = diagnostics ?? NullLocalDiagnostics.Instance;
        Content = _webView;
    }

    public CoreWebView2? CoreWebView => _webView.CoreWebView2;

    public BrowsingContext Context => _context;

    public Uri? CurrentAddress => _currentAddress is null ? null : new Uri(_currentAddress.AbsoluteUri);

    public string CurrentDocumentTitle => _documentTitle;

    public event EventHandler<WebViewTabVisualStateChangedEventArgs>? TabVisualStateChanged;

    public event EventHandler<WebViewTabAudioStateChangedEventArgs>? TabAudioStateChanged;

    public WebViewTabResourceIdentity CreateResourceIdentity(bool isSelected, bool isLoading) => new(
        _context.TabId,
        _webView.CoreWebView2?.FrameId ?? 0,
        isSelected,
        isLoading,
        _webView.CoreWebView2?.IsDocumentPlayingAudio ?? false,
        _webView.CoreWebView2?.IsSuspended ?? false);

    public ValueTask<ControllerResult> NavigateAsync(Uri target, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (target is null || _webView.CoreWebView2 is null ||
            !_navigation.IsAllowed(_context, target.AbsoluteUri, true, false, true))
        {
            return ValueTask.FromResult(ControllerResult.Failure(ControllerError.Create(
                ControllerErrorCode.PolicyDenied,
                "error.webview_host.navigation_denied")));
        }

        _context = _context with { CurrentSite = SiteIdentity.TryCreate(target, out var site) ? site : null };
        _webView.CoreWebView2.Navigate(target.AbsoluteUri);
        return ValueTask.FromResult(ControllerResult.Success());
    }

    public ValueTask<ControllerResult> SetMutedAsync(
        bool isMuted,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _disposed) != 0 || _webView.CoreWebView2 is not { } core)
        {
            return ValueTask.FromResult(ControllerResult.Failure(ControllerError.Create(
                ControllerErrorCode.Unavailable,
                "error.webview_host.audio_unavailable")));
        }

        try
        {
            core.IsMuted = isMuted;
            PublishTabAudioState(core);
            return ValueTask.FromResult(ControllerResult.Success());
        }
        catch
        {
            return ValueTask.FromResult(ControllerResult.Failure(ControllerError.Create(
                ControllerErrorCode.Unavailable,
                "error.webview_host.audio_unavailable")));
        }
    }

    public async ValueTask<ControllerResult<WebViewOfflineSnapshot>> CaptureOfflineSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_context.Privacy.IsPrivate)
        {
            return ControllerResult<WebViewOfflineSnapshot>.Failure(ControllerError.Create(
                ControllerErrorCode.PolicyDenied,
                "error.offline_reading.private_unavailable"));
        }
        if (Volatile.Read(ref _disposed) != 0 ||
            _webView.CoreWebView2 is not { } core ||
            _currentAddress is not { } address)
        {
            return ControllerResult<WebViewOfflineSnapshot>.Failure(ControllerError.Create(
                ControllerErrorCode.Unavailable,
                "error.offline_reading.capture_unavailable"));
        }

        try
        {
            using var stream = new MemoryStream();
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream)
                .WaitAsync(cancellationToken);
            if (stream.Length is < 8 or > MaximumOfflineSnapshotBytes)
            {
                return ControllerResult<WebViewOfflineSnapshot>.Failure(ControllerError.Create(
                    ControllerErrorCode.IntegrityFailure,
                    "error.offline_reading.capture_invalid"));
            }
            var bytes = stream.ToArray();
            if (!bytes.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
            {
                return ControllerResult<WebViewOfflineSnapshot>.Failure(ControllerError.Create(
                    ControllerErrorCode.IntegrityFailure,
                    "error.offline_reading.capture_invalid"));
            }
            var title = string.IsNullOrWhiteSpace(_documentTitle)
                ? address.Host
                : _documentTitle.Trim();
            return ControllerResult<WebViewOfflineSnapshot>.Success(new(
                _context.TabId,
                title,
                new Uri(address.AbsoluteUri),
                bytes));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return ControllerResult<WebViewOfflineSnapshot>.Failure(ControllerError.Create(
                ControllerErrorCode.Unavailable,
                "error.offline_reading.capture_failed"));
        }
    }

    public ValueTask<ControllerResult> GoBackAsync(CancellationToken cancellationToken = default) =>
        ExecuteNavigationActionAsync(core =>
        {
            if (!core.CanGoBack) return false;
            core.GoBack();
            return true;
        }, cancellationToken);

    public ValueTask<ControllerResult> GoForwardAsync(CancellationToken cancellationToken = default) =>
        ExecuteNavigationActionAsync(core =>
        {
            if (!core.CanGoForward) return false;
            core.GoForward();
            return true;
        }, cancellationToken);

    public ValueTask<ControllerResult> ReloadAsync(CancellationToken cancellationToken = default) =>
        ExecuteNavigationActionAsync(core => { core.Reload(); return true; }, cancellationToken);

    public async ValueTask<ControllerResult> InitializeAsync(
        IWebViewProfileLease profileLease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profileLease);
        if (!_context.IsStructurallyValid || profileLease.Descriptor.Context != _context.Privacy)
        {
            return ControllerResult.Failure(ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "error.webview_host.context_mismatch"));
        }

        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: profileLease.Descriptor.UserDataFolder).WaitAsync(cancellationToken);
            await _webView.EnsureCoreWebView2Async(environment).WaitAsync(cancellationToken);
            _environment = environment;
            _profileLease = profileLease;
            ConfigureFailClosedDefaults(_webView.CoreWebView2);
            return ControllerResult.Success();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await _diagnostics.WriteAsync(new LocalDiagnosticEvent(
                DateTimeOffset.UtcNow,
                LocalDiagnosticSeverity.Error,
                LocalDiagnosticCode.WebViewInitializationFailed,
                "webview-init"), cancellationToken);
            return ControllerResult.Failure(ControllerError.Create(
                ControllerErrorCode.Unavailable,
                "error.webview_host.initialization_failed"));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_webView.CoreWebView2 is not null)
        {
            _webView.CoreWebView2.NavigationStarting -= OnNavigationStarting;
            _webView.CoreWebView2.SourceChanged -= OnSourceChanged;
            _webView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
            _webView.CoreWebView2.DocumentTitleChanged -= OnDocumentTitleChanged;
            _webView.CoreWebView2.HistoryChanged -= OnHistoryChanged;
            _webView.CoreWebView2.FaviconChanged -= OnFaviconChanged;
            _webView.CoreWebView2.IsDocumentPlayingAudioChanged -= OnAudioStateChanged;
            _webView.CoreWebView2.IsMutedChanged -= OnAudioStateChanged;
            _webView.CoreWebView2.PermissionRequested -= OnPermissionRequested;
            _webView.CoreWebView2.NewWindowRequested -= OnNewWindowRequested;
        }

        Interlocked.Increment(ref _faviconRequestGeneration);
        TabVisualStateChanged = null;
        TabAudioStateChanged = null;
        _faviconPng = [];

        Content = null;
        _webView.Dispose();
        _environment = null;
        var profileLease = Interlocked.Exchange(ref _profileLease, null);
        if (profileLease is not null)
        {
            await profileLease.DisposeAsync();
        }
    }

    internal async ValueTask<IReadOnlyList<WebViewProcessAssociation>> GetProcessAssociationsAsync(
        CancellationToken cancellationToken)
    {
        var environment = _environment;
        if (environment is null)
        {
            return [];
        }

        var extended = await environment.GetProcessExtendedInfosAsync().WaitAsync(cancellationToken);
        return extended.Select(process => new WebViewProcessAssociation(
            process.ProcessInfo.ProcessId,
            MapProcessKind(process.ProcessInfo.Kind),
            process.AssociatedFrameInfos.Select(frame => (ulong)frame.FrameId).Distinct().ToArray())).ToArray();
    }

    private static WebViewProcessKind MapProcessKind(CoreWebView2ProcessKind kind) => kind switch
    {
        CoreWebView2ProcessKind.Browser => WebViewProcessKind.Browser,
        CoreWebView2ProcessKind.Renderer => WebViewProcessKind.Renderer,
        CoreWebView2ProcessKind.Utility => WebViewProcessKind.Utility,
        CoreWebView2ProcessKind.Gpu => WebViewProcessKind.Gpu,
        CoreWebView2ProcessKind.PpapiPlugin => WebViewProcessKind.PpapiPlugin,
        CoreWebView2ProcessKind.PpapiBroker => WebViewProcessKind.PpapiBroker,
        CoreWebView2ProcessKind.SandboxHelper => WebViewProcessKind.SandboxHelper,
        _ => WebViewProcessKind.Unknown,
    };

    private void ConfigureFailClosedDefaults(CoreWebView2 core)
    {
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.NavigationStarting += OnNavigationStarting;
        core.SourceChanged += OnSourceChanged;
        core.NavigationCompleted += OnNavigationCompleted;
        core.DocumentTitleChanged += OnDocumentTitleChanged;
        core.HistoryChanged += OnHistoryChanged;
        core.FaviconChanged += OnFaviconChanged;
        core.IsDocumentPlayingAudioChanged += OnAudioStateChanged;
        core.IsMutedChanged += OnAudioStateChanged;
        core.PermissionRequested += OnPermissionRequested;
        core.NewWindowRequested += OnNewWindowRequested;
        UpdateFromCore(core);
        PublishTabVisualState(core);
        _ = RefreshFaviconAsync(core);
        PublishTabAudioState(core);
    }

    private void OnAudioStateChanged(object? sender, object args)
    {
        if (sender is CoreWebView2 core)
        {
            PublishTabAudioState(core);
        }
    }

    private void PublishTabAudioState(CoreWebView2? core)
    {
        if (core is null || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var state = new WebViewTabAudioState(
            _context.TabId,
            checked(++_audioRevision),
            core.IsDocumentPlayingAudio,
            core.IsMuted);
        TabAudioStateChanged?.Invoke(this, new(state));
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        var allowed = _navigation.IsAllowed(
            _context,
            args.Uri,
            isMainFrame: true,
            args.IsRedirected,
            args.IsUserInitiated);
        args.Cancel = !allowed;
        if (!allowed)
        {
            _ = _diagnostics.WriteAsync(new LocalDiagnosticEvent(
                DateTimeOffset.UtcNow,
                LocalDiagnosticSeverity.Warning,
                LocalDiagnosticCode.NavigationBlocked,
                "navigation-policy"));
            return;
        }

        _isLoading = true;
        _documentTitle = string.Empty;
        _faviconPng = [];
        Interlocked.Increment(ref _faviconRequestGeneration);
        _currentAddress = TryGetWebAddress(args.Uri);
        UpdateCurrentSite(_currentAddress);
        PublishTabVisualState(sender as CoreWebView2);
    }

    private void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs args)
    {
        if (sender is not CoreWebView2 core)
        {
            return;
        }
        _currentAddress = TryGetWebAddress(core.Source);
        UpdateCurrentSite(_currentAddress);
        PublishTabVisualState(core);
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (sender is not CoreWebView2 core)
        {
            return;
        }
        _isLoading = false;
        UpdateFromCore(core);
        PublishTabVisualState(core);
        _ = RefreshFaviconAsync(core);
    }

    private void OnDocumentTitleChanged(object? sender, object args)
    {
        if (sender is not CoreWebView2 core)
        {
            return;
        }
        _documentTitle = core.DocumentTitle ?? string.Empty;
        PublishTabVisualState(core);
    }

    private void OnHistoryChanged(object? sender, object args)
    {
        if (sender is CoreWebView2 core)
        {
            PublishTabVisualState(core);
        }
    }

    private async void OnFaviconChanged(object? sender, object args)
    {
        if (sender is not CoreWebView2 core || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        await RefreshFaviconAsync(core);
    }

    private async Task RefreshFaviconAsync(CoreWebView2 core)
    {
        if (Volatile.Read(ref _disposed) != 0 || !ReferenceEquals(core, _webView.CoreWebView2))
        {
            return;
        }

        var generation = Interlocked.Increment(ref _faviconRequestGeneration);
        var sourceAtRequest = core.Source;
        var faviconUriAtRequest = core.FaviconUri;
        if (string.IsNullOrWhiteSpace(faviconUriAtRequest))
        {
            _faviconPng = [];
            PublishTabVisualState(core);
            return;
        }

        try
        {
            await using var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            var favicon = await ReadBoundedFaviconAsync(stream);
            if (Volatile.Read(ref _disposed) != 0 ||
                generation != Volatile.Read(ref _faviconRequestGeneration) ||
                !ReferenceEquals(core, _webView.CoreWebView2) ||
                !string.Equals(core.Source, sourceAtRequest, StringComparison.Ordinal) ||
                !string.Equals(core.FaviconUri, faviconUriAtRequest, StringComparison.Ordinal))
            {
                return;
            }

            _faviconPng = favicon;
            PublishTabVisualState(core);
        }
        catch
        {
            if (Volatile.Read(ref _disposed) == 0 &&
                generation == Volatile.Read(ref _faviconRequestGeneration))
            {
                _faviconPng = [];
                PublishTabVisualState(core);
            }
        }
    }

    private void UpdateFromCore(CoreWebView2 core)
    {
        _currentAddress = TryGetWebAddress(core.Source);
        _documentTitle = core.DocumentTitle ?? string.Empty;
        UpdateCurrentSite(_currentAddress);
    }

    private void UpdateCurrentSite(Uri? address)
    {
        _context = _context with
        {
            CurrentSite = address is not null && SiteIdentity.TryCreate(address, out var site)
                ? site
                : null,
        };
    }

    private void PublishTabVisualState(CoreWebView2? core)
    {
        if (core is null || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var state = WebViewTabVisualState.Create(
            _context.TabId,
            checked(++_visualRevision),
            _currentAddress,
            _documentTitle,
            _context.Privacy.IsPrivate ? "Private tab" : "New Tab",
            _isLoading,
            core.CanGoBack,
            core.CanGoForward,
            _faviconPng);
        TabVisualStateChanged?.Invoke(this, new(state));
    }

    private static Uri? TryGetWebAddress(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var address) &&
        address.Scheme is "http" or "https"
            ? address
            : null;

    private static async Task<byte[]> ReadBoundedFaviconAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk);
            if (read == 0)
            {
                break;
            }
            if (buffer.Length + read > WebViewTabVisualState.MaximumFaviconBytes)
            {
                return [];
            }
            await buffer.WriteAsync(chunk.AsMemory(0, read));
        }

        var bytes = buffer.ToArray();
        return WebViewTabVisualState.IsSafePng(bytes) ? bytes : [];
    }

    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs args)
    {
        args.State = CoreWebView2PermissionState.Deny;
        args.SavesInProfile = false;
        if (_permissionBroker is null ||
            !Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) ||
            !SiteIdentity.TryCreate(uri, out var site))
        {
            LogPermissionDenied(args);
            return;
        }

        var capability = WebPermissionCapabilityMapper.Map(args.PermissionKind);
        if (capability == WebPermissionCapability.Unknown)
        {
            LogPermissionDenied(args);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var requestId = new RequestId(Guid.NewGuid());
        var requestContext = _context with { CurrentSite = site };
        var deferral = args.GetDeferral();
        var uiCompletion = new DispatcherPermissionCompletion(
            Dispatcher,
            completion =>
            {
                args.State = completion.Disposition == PermissionHostDisposition.Allow
                    ? CoreWebView2PermissionState.Allow
                    : CoreWebView2PermissionState.Deny;
                args.SavesInProfile = false;
                deferral.Complete();
            });
        var registration = _permissionCompletions.Register(new PendingPermissionRegistration(
            requestId,
            requestContext.TabId,
            capability,
            now.AddSeconds(30),
            uiCompletion.Complete));
        if (!registration.IsSuccess)
        {
            deferral.Complete();
            LogPermissionDenied(args);
            return;
        }

        _ = IngestPermissionAsync(new PermissionBrokerRequest(
            requestId,
            new ResponseToken(Guid.NewGuid()),
            requestContext,
            site!,
            capability,
            SupportedScopes(requestContext.Privacy),
            PermissionDecision.Ask,
            PermissionDecision.Deny,
            args.IsUserInitiated,
            now,
            now.AddSeconds(30)));
    }

    private async Task IngestPermissionAsync(PermissionBrokerRequest request)
    {
        try
        {
            var state = await _permissionBroker!.GetCurrentSiteStateAsync(
                request.Context,
                request.RequestingSite,
                CancellationToken.None);
            if (!state.IsSuccess)
            {
                await _permissionCompletions.CompleteAsync(
                    PermissionHostCompletion.FailClosed(
                        request.RequestId,
                        request.Context.TabId,
                        request.Capability,
                        PermissionDecisionSource.HostFailure),
                    CancellationToken.None);
                return;
            }

            var existing = ExistingPermissionRuleResolver.Resolve(request, state.Value!);
            if (existing is not null)
            {
                await _permissionCompletions.CompleteAsync(
                    existing,
                    CancellationToken.None);
                return;
            }

            await _permissionBroker!.IngestAsync(request, CancellationToken.None);
        }
        catch
        {
            _permissionCompletions.CompleteAsync(PermissionHostCompletion.FailClosed(
                request.RequestId, request.Context.TabId, request.Capability),
                CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }

        _permissionCompletions.Expire(DateTimeOffset.UtcNow);
    }

    private static IReadOnlyList<PermissionAllowScope> SupportedScopes(PrivacyContext context) =>
        context.IsPrivate
            ? [PermissionAllowScope.Once, PermissionAllowScope.Session]
            : [PermissionAllowScope.Once, PermissionAllowScope.Session, PermissionAllowScope.Persistent];

    private void LogPermissionDenied(CoreWebView2PermissionRequestedEventArgs args)
    {
        _ = _diagnostics.WriteAsync(new LocalDiagnosticEvent(
            DateTimeOffset.UtcNow,
            LocalDiagnosticSeverity.Information,
            LocalDiagnosticCode.PermissionDenied,
            WebPermissionCapabilityMapper.Map(args.PermissionKind).ToString()));
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        _ = _diagnostics.WriteAsync(new LocalDiagnosticEvent(
            DateTimeOffset.UtcNow,
            LocalDiagnosticSeverity.Information,
            LocalDiagnosticCode.PermissionDenied,
            WebPermissionCapabilityMapper.Popup.ToString()));
    }

    private sealed class NullLocalDiagnostics : ILocalDiagnostics
    {
        public static NullLocalDiagnostics Instance { get; } = new();

        public ValueTask WriteAsync(
            LocalDiagnosticEvent diagnosticEvent,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private ValueTask<ControllerResult> ExecuteNavigationActionAsync(
        Func<CoreWebView2, bool> action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_webView.CoreWebView2 is not { } core || !action(core))
        {
            return ValueTask.FromResult(ControllerResult.Failure(ControllerError.Create(
                ControllerErrorCode.Unavailable,
                "error.webview_host.navigation_unavailable")));
        }

        return ValueTask.FromResult(ControllerResult.Success());
    }
}
