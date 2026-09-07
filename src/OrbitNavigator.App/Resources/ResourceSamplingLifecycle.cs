namespace OrbitNavigator.App.Resources;

/// <summary>
/// Owns the visible-only lifetime of the Browser Resources sampling loop.
/// One accepted host sample is requested per cadence; this type never creates
/// intermediate presentation samples.
/// </summary>
internal sealed class ResourceSamplingLifecycle : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationToken _ownerLifetime;
    private readonly Func<CancellationToken, ValueTask> _sampleOnceAsync;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private CancellationTokenSource? _visibleLifetime;
    private Task? _activeLoop;
    private bool _disposed;

    public ResourceSamplingLifecycle(
        TimeSpan cadence,
        CancellationToken ownerLifetime,
        Func<CancellationToken, ValueTask> sampleOnceAsync,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        if (cadence <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cadence));
        }

        Cadence = cadence;
        _ownerLifetime = ownerLifetime;
        _sampleOnceAsync = sampleOnceAsync ?? throw new ArgumentNullException(nameof(sampleOnceAsync));
        _delayAsync = delayAsync ?? Task.Delay;
    }

    public TimeSpan Cadence { get; }

    public bool IsSampling
    {
        get
        {
            lock (_gate)
            {
                return _visibleLifetime is not null;
            }
        }
    }

    public void SetVisible(bool visible)
    {
        if (visible)
        {
            Start();
        }
        else
        {
            Stop();
        }
    }

    private void Start()
    {
        lock (_gate)
        {
            if (_disposed || _ownerLifetime.IsCancellationRequested || _visibleLifetime is not null)
            {
                return;
            }

            _visibleLifetime = CancellationTokenSource.CreateLinkedTokenSource(_ownerLifetime);
            _activeLoop = RunAsync(_visibleLifetime.Token);
        }
    }

    private void Stop()
    {
        CancellationTokenSource? visibleLifetime;
        Task? activeLoop;
        lock (_gate)
        {
            visibleLifetime = _visibleLifetime;
            activeLoop = _activeLoop;
            _visibleLifetime = null;
            _activeLoop = null;
        }

        if (visibleLifetime is null)
        {
            return;
        }

        visibleLifetime.Cancel();
        _ = DisposeWhenCompleteAsync(visibleLifetime, activeLoop);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _sampleOnceAsync(cancellationToken);
                await _delayAsync(Cadence, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task DisposeWhenCompleteAsync(
        CancellationTokenSource visibleLifetime,
        Task? activeLoop)
    {
        try
        {
            if (activeLoop is not null)
            {
                await activeLoop;
            }
        }
        catch (OperationCanceledException) when (visibleLifetime.IsCancellationRequested)
        {
        }
        finally
        {
            visibleLifetime.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Stop();
    }
}
