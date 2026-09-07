using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Foundation.Resources;

namespace OrbitNavigator.WebViewHost;

/// <summary>
/// Exposes only process IDs, process kinds, and opaque frame IDs. Frame source,
/// page content, and URLs are deliberately never projected.
/// </summary>
public sealed class WebViewResourceProcessSource : IWebViewResourceProcessSource
{
    private readonly PrivacyContext _context;
    private readonly Func<IReadOnlyCollection<WebView2HostControl>> _hosts;

    public WebViewResourceProcessSource(
        PrivacyContext context,
        Func<IReadOnlyCollection<WebView2HostControl>> hosts)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _hosts = hosts ?? throw new ArgumentNullException(nameof(hosts));
    }

    public async ValueTask<ControllerResult<IReadOnlyList<WebViewProcessAssociation>>> GetProcessAssociationsAsync(
        PrivacyContext context,
        CancellationToken cancellationToken = default)
    {
        if (context != _context)
        {
            return ControllerResult<IReadOnlyList<WebViewProcessAssociation>>.Failure(
                ControllerError.Create(
                    ControllerErrorCode.PolicyDenied,
                    "error.resource.context_mismatch"));
        }

        try
        {
            var hosts = _hosts();
            if (hosts.Any(host => host.Context.Privacy != _context))
            {
                return ControllerResult<IReadOnlyList<WebViewProcessAssociation>>.Failure(
                    ControllerError.Create(
                        ControllerErrorCode.PolicyDenied,
                        "error.resource.host_context_mismatch"));
            }

            var merged = new Dictionary<(int ProcessId, WebViewProcessKind Kind), HashSet<ulong>>();
            foreach (var host in hosts)
            {
                foreach (var process in await host.GetProcessAssociationsAsync(cancellationToken))
                {
                    var key = (process.ProcessId, process.Kind);
                    if (!merged.TryGetValue(key, out var frames))
                    {
                        frames = [];
                        merged.Add(key, frames);
                    }
                    frames.UnionWith(process.FrameIds);
                }
            }

            return ControllerResult<IReadOnlyList<WebViewProcessAssociation>>.Success(
                merged.Select(pair => new WebViewProcessAssociation(
                    pair.Key.ProcessId,
                    pair.Key.Kind,
                    pair.Value.ToArray())).ToArray());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ControllerResult<IReadOnlyList<WebViewProcessAssociation>>.Failure(
                ControllerError.Create(
                    ControllerErrorCode.Unavailable,
                    "error.resource.webview_processes_unavailable"));
        }
    }
}
