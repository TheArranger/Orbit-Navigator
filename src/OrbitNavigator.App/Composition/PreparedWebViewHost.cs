using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.WebViewHost;

namespace OrbitNavigator.App.Composition;

/// <summary>
/// Holds a host and its profile lease until the host has been attached to a
/// loaded WPF visual. WebView2 controller creation is not started by prepare.
/// </summary>
public sealed class PreparedWebViewHost : IAsyncDisposable
{
    private IWebViewProfileLease? _profileLease;
    private int _initializationStarted;
    private int _disposed;

    public PreparedWebViewHost(
        WebView2HostControl host,
        IWebViewProfileLease profileLease)
    {
        Host = host ?? throw new ArgumentNullException(nameof(host));
        _profileLease = profileLease ?? throw new ArgumentNullException(nameof(profileLease));
    }

    public WebView2HostControl Host { get; }

    public async ValueTask<ControllerResult> InitializeAttachedAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1) ||
            Interlocked.Exchange(ref _initializationStarted, 1) != 0 ||
            Volatile.Read(ref _disposed) != 0)
        {
            return ControllerResult.Failure(ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "error.webview_host.initialization_state_invalid"));
        }

        var lease = Volatile.Read(ref _profileLease);
        if (lease is null)
        {
            return ControllerResult.Failure(ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "error.webview_host.profile_lease_missing"));
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            var result = await Host.InitializeAsync(lease, timeoutSource.Token);
            if (result.IsSuccess)
            {
                // The initialized host now owns and releases this lease.
                Interlocked.Exchange(ref _profileLease, null);
                return result;
            }

            await DisposeAsync();
            return result;
        }
        catch (OperationCanceledException)
        {
            await DisposeAsync();
            return ControllerResult.Failure(ControllerError.Create(
                ControllerErrorCode.Unavailable,
                "error.webview_host.initialization_timeout"));
        }
        catch
        {
            await DisposeAsync();
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

        await Host.DisposeAsync();
        var lease = Interlocked.Exchange(ref _profileLease, null);
        if (lease is not null)
        {
            await lease.DisposeAsync();
        }
    }
}
