using OrbitNavigator.App.Resources;
using OrbitNavigator.Presentation.Wpf;

using Xunit;

namespace OrbitNavigator.App.Tests.Resources;

public sealed class ResourceSamplingLifecycleTests
{
    [Fact]
    public void CadenceMatchesPresentationFiveSecondContract()
    {
        using var owner = new CancellationTokenSource();
        using var lifecycle = new ResourceSamplingLifecycle(
            ResourceTaskPanelControl.RecommendedSamplingInterval,
            owner.Token,
            _ => ValueTask.CompletedTask);

        Assert.Equal(TimeSpan.FromSeconds(5), lifecycle.Cadence);
    }

    [Fact]
    public async Task VisibleSamplesOnceThenWaitsWithoutFabricatedIntermediateSamples()
    {
        using var owner = new CancellationTokenSource();
        var delay = new ControlledDelay();
        var samples = 0;
        using var lifecycle = new ResourceSamplingLifecycle(
            TimeSpan.FromSeconds(5),
            owner.Token,
            _ =>
            {
                Interlocked.Increment(ref samples);
                return ValueTask.CompletedTask;
            },
            delay.WaitAsync);

        lifecycle.SetVisible(true);
        await WaitUntilAsync(() => Volatile.Read(ref samples) == 1 && delay.CallCount == 1);

        Assert.Equal(1, Volatile.Read(ref samples));
        Assert.Equal([TimeSpan.FromSeconds(5)], delay.RequestedCadences);

        delay.CompleteCurrent();
        await WaitUntilAsync(() => Volatile.Read(ref samples) == 2 && delay.CallCount == 2);
        Assert.Equal(2, Volatile.Read(ref samples));
    }

    [Fact]
    public async Task HiddenCancelsPendingDelayPromptlyAndPreventsFurtherSamples()
    {
        using var owner = new CancellationTokenSource();
        var delay = new ControlledDelay();
        var samples = 0;
        using var lifecycle = new ResourceSamplingLifecycle(
            TimeSpan.FromSeconds(5),
            owner.Token,
            _ =>
            {
                Interlocked.Increment(ref samples);
                return ValueTask.CompletedTask;
            },
            delay.WaitAsync);

        lifecycle.SetVisible(true);
        await WaitUntilAsync(() => delay.CallCount == 1);
        lifecycle.SetVisible(false);
        await WaitUntilAsync(() => delay.CancellationCount == 1);

        Assert.False(lifecycle.IsSampling);
        Assert.Equal(1, Volatile.Read(ref samples));
    }

    [Fact]
    public async Task OwnerShutdownStopsSamplingAndCannotRestart()
    {
        using var owner = new CancellationTokenSource();
        var delay = new ControlledDelay();
        var samples = 0;
        using var lifecycle = new ResourceSamplingLifecycle(
            TimeSpan.FromSeconds(5),
            owner.Token,
            _ =>
            {
                Interlocked.Increment(ref samples);
                return ValueTask.CompletedTask;
            },
            delay.WaitAsync);

        lifecycle.SetVisible(true);
        await WaitUntilAsync(() => delay.CallCount == 1);
        owner.Cancel();
        await WaitUntilAsync(() => delay.CancellationCount == 1);
        lifecycle.SetVisible(true);

        Assert.Equal(1, Volatile.Read(ref samples));
    }

    [Fact]
    public async Task DisposeWhileVisibleStopsSampling()
    {
        using var owner = new CancellationTokenSource();
        var delay = new ControlledDelay();
        var samples = 0;
        var lifecycle = new ResourceSamplingLifecycle(
            TimeSpan.FromSeconds(5),
            owner.Token,
            _ =>
            {
                Interlocked.Increment(ref samples);
                return ValueTask.CompletedTask;
            },
            delay.WaitAsync);

        lifecycle.SetVisible(true);
        await WaitUntilAsync(() => delay.CallCount == 1);
        lifecycle.Dispose();
        await WaitUntilAsync(() => delay.CancellationCount == 1);

        Assert.False(lifecycle.IsSampling);
        Assert.Equal(1, Volatile.Read(ref samples));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeout)
            {
                throw new TimeoutException("The sampling lifecycle did not reach the expected state.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class ControlledDelay
    {
        private readonly object _gate = new();
        private TaskCompletionSource _current = NewCompletion();
        private readonly List<TimeSpan> _requestedCadences = [];
        private int _cancellationCount;

        public int CallCount
        {
            get
            {
                lock (_gate)
                {
                    return _requestedCadences.Count;
                }
            }
        }

        public int CancellationCount => Volatile.Read(ref _cancellationCount);

        public IReadOnlyList<TimeSpan> RequestedCadences
        {
            get
            {
                lock (_gate)
                {
                    return _requestedCadences.ToArray();
                }
            }
        }

        public Task WaitAsync(TimeSpan cadence, CancellationToken cancellationToken)
        {
            TaskCompletionSource current;
            lock (_gate)
            {
                _requestedCadences.Add(cadence);
                current = _current;
            }

            cancellationToken.Register(() =>
            {
                Interlocked.Increment(ref _cancellationCount);
                current.TrySetCanceled(cancellationToken);
            });
            return current.Task;
        }

        public void CompleteCurrent()
        {
            TaskCompletionSource current;
            lock (_gate)
            {
                current = _current;
                _current = NewCompletion();
            }

            current.TrySetResult();
        }

        private static TaskCompletionSource NewCompletion() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
