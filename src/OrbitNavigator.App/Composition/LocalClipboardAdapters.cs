using System.Windows;
using System.Windows.Threading;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Privacy;
using OrbitNavigator.Privacy.Clipboard;

namespace OrbitNavigator.App.Composition;

public sealed record ClipboardCaptureLeaseReceipt(ClipboardCaptureLeaseId LeaseId);

public sealed class ClipboardCaptureLeaseStore : IClipboardCaptureLeaseProvider, IDisposable
{
    private const int MaximumLeases = 32;
    private readonly IClock _clock;
    private readonly object _gate = new();
    private readonly Dictionary<ClipboardCaptureLeaseId, Lease> _leases = [];
    private bool _disposed;

    public ClipboardCaptureLeaseStore(IClock clock) =>
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public ControllerResult<ClipboardCaptureLeaseReceipt> IssueFromExplicitUserAction(
        BrowsingContext context,
        ClipboardCaptureClassification classification,
        ReadOnlySpan<char> content)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.IsStructurallyValid ||
            context.Privacy.IsPrivate ||
            classification != ClipboardCaptureClassification.ExplicitUserInitiatedNonSensitive)
        {
            return ControllerResult<ClipboardCaptureLeaseReceipt>.Failure(ControllerError.Create(
                ControllerErrorCode.PolicyDenied,
                "error.clipboard-shelf.capture-lease-denied"));
        }

        var sensitive = SensitiveClipboardContent.Create(content);
        if (!sensitive.IsSuccess)
        {
            return ControllerResult<ClipboardCaptureLeaseReceipt>.Failure(sensitive.Error!);
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            PruneExpired();
            if (_leases.Count >= MaximumLeases)
            {
                sensitive.Value!.Dispose();
                return ControllerResult<ClipboardCaptureLeaseReceipt>.Failure(ControllerError.Create(
                    ControllerErrorCode.Unavailable,
                    "error.clipboard-shelf.capture-lease-capacity"));
            }

            var id = new ClipboardCaptureLeaseId(context.Privacy.ProfileId, Guid.NewGuid());
            _leases.Add(id, new Lease(
                context.Privacy.SessionId,
                sensitive.Value!,
                _clock.UtcNow.AddSeconds(30)));
            return ControllerResult<ClipboardCaptureLeaseReceipt>.Success(
                new ClipboardCaptureLeaseReceipt(id));
        }
    }

    public ValueTask<ControllerResult<SensitiveClipboardContent>> RedeemAsync(
        BrowsingContext context,
        ClipboardCaptureLeaseId leaseId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);
        lock (_gate)
        {
            ThrowIfDisposed();
            PruneExpired();
            if (context.Privacy.IsPrivate ||
                leaseId.IsEmpty ||
                leaseId.ProfileId != context.Privacy.ProfileId ||
                !_leases.Remove(leaseId, out var lease))
            {
                return ValueTask.FromResult(ControllerResult<SensitiveClipboardContent>.Failure(
                    ControllerError.Create(
                        ControllerErrorCode.PolicyDenied,
                        "error.clipboard-shelf.capture-lease-rejected")));
            }

            if (lease.SessionId != context.Privacy.SessionId)
            {
                lease.Content.Dispose();
                return ValueTask.FromResult(ControllerResult<SensitiveClipboardContent>.Failure(
                    ControllerError.Create(
                        ControllerErrorCode.PolicyDenied,
                        "error.clipboard-shelf.capture-lease-session-mismatch")));
            }

            return ValueTask.FromResult(
                ControllerResult<SensitiveClipboardContent>.Success(lease.Content));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var lease in _leases.Values) lease.Content.Dispose();
            _leases.Clear();
        }
    }

    private void PruneExpired()
    {
        var expired = _leases.Where(pair => pair.Value.ExpiresAtUtc <= _clock.UtcNow)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var id in expired)
        {
            _leases.Remove(id, out var lease);
            lease?.Content.Dispose();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record Lease(
        BrowserSessionId SessionId,
        SensitiveClipboardContent Content,
        DateTimeOffset ExpiresAtUtc);
}

public sealed class WindowsClipboardShelfUseTarget : IClipboardShelfUseTarget
{
    public async ValueTask<ControllerResult> WriteAsync(
        BrowsingContext destination,
        ClipboardShelfContentKind kind,
        SensitiveClipboardContent content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(content);
        if (!destination.IsStructurallyValid || destination.Privacy.IsPrivate || !Enum.IsDefined(kind))
        {
            return ControllerResult.Failure(ControllerError.Create(
                ControllerErrorCode.PolicyDenied,
                "error.clipboard-shelf.use-target-denied"));
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            return ControllerResult.Failure(ControllerError.Create(
                ControllerErrorCode.Unavailable,
                "error.clipboard-shelf.platform-unavailable"));
        }

        try
        {
            var text = new string(content.Characters.Span);
            await dispatcher.InvokeAsync(
                () => Clipboard.SetText(text),
                DispatcherPriority.Send,
                cancellationToken);
            return ControllerResult.Success();
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return ControllerResult.Failure(ControllerError.Create(
                ControllerErrorCode.Unavailable,
                "error.clipboard-shelf.platform-write-failed",
                isRetryable: true));
        }
    }
}
