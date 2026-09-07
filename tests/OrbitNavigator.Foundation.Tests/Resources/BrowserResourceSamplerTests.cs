using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Foundation.Resources;
using Xunit;

namespace OrbitNavigator.Foundation.Tests.Resources;

public sealed class BrowserResourceSamplerTests
{
    [Fact]
    public async Task ExclusiveRendererGetsMeasuredContributionButSharedRendererDoesNot()
    {
        var context = Context();
        var window = new BrowserWindowId(Guid.NewGuid());
        var exclusive = new BrowserTabId(Guid.NewGuid());
        var sharedOne = new BrowserTabId(Guid.NewGuid());
        var sharedTwo = new BrowserTabId(Guid.NewGuid());
        var clock = new FakeTimestamps();
        var measurements = new FakeMeasurements();
        var processes = new FakeWebViewProcesses([
            new(101, WebViewProcessKind.Renderer, [11]),
            new(102, WebViewProcessKind.Renderer, [21, 22]),
        ]);
        var sampler = new BrowserResourceSampler(
            context, window, processes, measurements, clock, processorCount: 4);
        var tabs = new[]
        {
            Tab(exclusive, 11, true),
            Tab(sharedOne, 21),
            Tab(sharedTwo, 22),
        };

        await sampler.SampleAsync(tabs);
        clock.Advance(TimeSpan.FromSeconds(1));
        measurements.Advance();
        var sample = await sampler.SampleAsync(tabs);

        Assert.True(sample.IsSuccess);
        Assert.True(sample.Value!.SampleId > 1);
        var exclusiveRow = sample.Value.Tabs.Single(row => row.TabId == exclusive);
        var sharedRows = sample.Value.Tabs.Where(row => row.TabId != exclusive).ToArray();
        Assert.Equal(ResourceAttributionQuality.ExclusiveRendererMeasured, exclusiveRow.Quality);
        Assert.NotNull(exclusiveRow.CpuPercent);
        Assert.Equal(300_000_000, exclusiveRow.PrivateBytes);
        Assert.All(sharedRows, row =>
        {
            Assert.Equal(ResourceAttributionQuality.SharedRenderer, row.Quality);
            Assert.Null(row.CpuPercent);
            Assert.Null(row.PrivateBytes);
        });
    }

    [Fact]
    public async Task ConcurrentSampleIsRejectedAndProcessIdsAreDeduplicated()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new BlockingWebViewProcesses(gate.Task);
        var measurements = new FakeMeasurements();
        var sampler = new BrowserResourceSampler(
            Context(),
            new BrowserWindowId(Guid.NewGuid()),
            source,
            measurements,
            new FakeTimestamps());

        var first = sampler.SampleAsync([]).AsTask();
        await source.Entered.Task;
        var overlapping = await sampler.SampleAsync([]);
        gate.SetResult();
        await first;

        Assert.False(overlapping.IsSuccess);
        Assert.Equal(ControllerErrorCode.Conflict, overlapping.Error?.Code);
    }

    [Fact]
    public async Task UnavailableSourceProducesTruthfulUnavailableRows()
    {
        var tab = new BrowserTabId(Guid.NewGuid());
        var sampler = new BrowserResourceSampler(
            Context(),
            new BrowserWindowId(Guid.NewGuid()),
            new FailingWebViewProcesses(),
            new FakeMeasurements(),
            new FakeTimestamps());

        var result = await sampler.SampleAsync([Tab(tab, 44, true)]);

        Assert.True(result.IsSuccess);
        Assert.Equal(ResourceSamplingState.Unavailable, result.Value!.State);
        Assert.Equal(ClosingTabsHelpfulness.Unknown, result.Value.ClosingTabsMayHelp);
        var row = Assert.Single(result.Value.Tabs);
        Assert.Equal(ResourceAttributionQuality.Unavailable, row.Quality);
        Assert.Null(row.CpuPercent);
        Assert.Null(row.PrivateBytes);
    }

    private static WebViewTabResourceIdentity Tab(
        BrowserTabId id,
        ulong frame,
        bool selected = false) =>
        new(id, frame, selected, false, false, false);

    private static PrivacyContext Context() =>
        new(
            new ProfileId(Guid.NewGuid()),
            new BrowserSessionId(Guid.NewGuid()),
            BrowserProfileMode.Normal);

    private sealed class FakeWebViewProcesses(
        IReadOnlyList<WebViewProcessAssociation> associations) : IWebViewResourceProcessSource
    {
        public ValueTask<ControllerResult<IReadOnlyList<WebViewProcessAssociation>>> GetProcessAssociationsAsync(
            PrivacyContext context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ControllerResult<IReadOnlyList<WebViewProcessAssociation>>.Success(associations));
    }

    private sealed class FailingWebViewProcesses : IWebViewResourceProcessSource
    {
        public ValueTask<ControllerResult<IReadOnlyList<WebViewProcessAssociation>>> GetProcessAssociationsAsync(
            PrivacyContext context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ControllerResult<IReadOnlyList<WebViewProcessAssociation>>.Failure(
                ControllerError.Create(ControllerErrorCode.Unavailable, "error.test.unavailable")));
    }

    private sealed class BlockingWebViewProcesses(Task release) : IWebViewResourceProcessSource
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ControllerResult<IReadOnlyList<WebViewProcessAssociation>>> GetProcessAssociationsAsync(
            PrivacyContext context,
            CancellationToken cancellationToken = default)
        {
            Entered.SetResult();
            await release.WaitAsync(cancellationToken);
            return ControllerResult<IReadOnlyList<WebViewProcessAssociation>>.Success([]);
        }
    }

    private sealed class FakeTimestamps : IMonotonicTimestampSource
    {
        private long value = 1_000;
        public long GetTimestamp() => value;
        public long Frequency => 1_000;
        public void Advance(TimeSpan amount) => value += (long)(amount.TotalSeconds * Frequency);
    }

    private sealed class FakeMeasurements : IWindowsResourceMeasurementSource
    {
        private int generation;

        public ControllerResult<SystemResourceMeasurement> ReadSystem() =>
            ControllerResult<SystemResourceMeasurement>.Success(generation == 0
                ? new(1_000, 400, 100, 500, 500)
                : new(1_000, 150, 120, 700, 700));

        public ControllerResult<ProcessResourceMeasurement> ReadProcess(int processId) =>
            ControllerResult<ProcessResourceMeasurement>.Success(new(
                processId,
                processId == 101 ? 300_000_000 : 200_000_000,
                TimeSpan.FromMilliseconds(generation == 0 ? 100 : processId == 101 ? 900 : 300)));

        public void Advance() => generation++;
    }
}
