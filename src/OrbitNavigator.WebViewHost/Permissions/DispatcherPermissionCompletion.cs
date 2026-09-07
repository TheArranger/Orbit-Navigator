using System.Windows.Threading;
using OrbitNavigator.Contracts.Privacy;

namespace OrbitNavigator.WebViewHost.Permissions;

/// <summary>
/// Applies a WebView permission completion on the Dispatcher that owns the
/// CoreWebView2 event arguments. Persistent Orbit rules complete after an
/// asynchronous storage write and therefore commonly arrive on a worker.
/// </summary>
public sealed class DispatcherPermissionCompletion
{
    private readonly Dispatcher _dispatcher;
    private readonly Action<PermissionHostCompletion> _complete;

    public DispatcherPermissionCompletion(
        Dispatcher dispatcher,
        Action<PermissionHostCompletion> complete)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _complete = complete ?? throw new ArgumentNullException(nameof(complete));
    }

    public void Complete(PermissionHostCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        if (_dispatcher.CheckAccess())
        {
            _complete(completion);
            return;
        }

        _dispatcher.Invoke(() => _complete(completion), DispatcherPriority.Send);
    }
}
