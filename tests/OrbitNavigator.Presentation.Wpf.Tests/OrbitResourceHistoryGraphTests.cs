using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Presentation.Resources;
using OrbitNavigator.Presentation.Wpf;

using Xunit;

namespace OrbitNavigator.Presentation.Wpf.Tests;

public sealed class OrbitResourceHistoryGraphTests
{
    [Fact]
    public void AppendsOnlyActiveNumericMonotonicSamplesAndFreezesOtherStates() => StaTest.Run(() =>
    {
        var graph = new OrbitResourceHistoryGraph { Metric = OrbitResourceHistoryMetric.Cpu };

        Assert.False(graph.AcceptSample(Sample(1, ResourceSamplingState.Starting, 4, 64)));
        Assert.True(graph.AcceptSample(Sample(2, ResourceSamplingState.Active, 12.5, 70)));
        Assert.False(graph.AcceptSample(Sample(2, ResourceSamplingState.Active, 99, 999)));
        Assert.False(graph.AcceptSample(Sample(3, ResourceSamplingState.Paused, 80, 800)));
        Assert.False(graph.AcceptSample(Sample(4, ResourceSamplingState.Stale, 60, 600)));
        Assert.False(graph.AcceptSample(Sample(5, ResourceSamplingState.Active, null, 500)));

        graph.IsSurfaceActive = false;
        Assert.False(graph.AcceptSample(Sample(6, ResourceSamplingState.Active, 50, 500)));
        graph.IsSurfaceActive = true;
        Assert.True(graph.AcceptSample(Sample(7, ResourceSamplingState.Active, 20, 200)));

        Assert.Equal(2, graph.HistoryPointCount);
        Assert.Equal(7, graph.LastObservedSampleId);
        Assert.Equal(7, graph.LastPlottedSampleId);
        Assert.Equal(20, graph.LatestValue);
        Assert.Equal(new long[] { 2, 7 }, graph.GetHistorySnapshot().Select(point => point.SampleId));
        Assert.False(graph.HasActiveAnimation);
    });

    [Fact]
    public void FixedRingBufferKeepsOnlyTheNewestRealSamplesInCanonicalOrder() => StaTest.Run(() =>
    {
        var graph = new OrbitResourceHistoryGraph { Metric = OrbitResourceHistoryMetric.Memory };
        for (var sampleId = 1; sampleId <= OrbitResourceHistoryGraph.MaximumHistoryPoints + 5; sampleId++)
        {
            Assert.True(graph.AcceptSample(Sample(
                sampleId,
                ResourceSamplingState.Active,
                sampleId,
                100 + sampleId)));
        }

        var history = graph.GetHistorySnapshot();
        Assert.Equal(OrbitResourceHistoryGraph.MaximumHistoryPoints, history.Count);
        Assert.Equal(6, history[0].SampleId);
        Assert.Equal(OrbitResourceHistoryGraph.MaximumHistoryPoints + 5, history[^1].SampleId);
        Assert.Equal((106d) * 1024 * 1024, history[0].Value);
        Assert.Equal((135d) * 1024 * 1024, history[^1].Value);
    });

    [Fact]
    public void BiggestConsumerUsesOnlyExclusiveMeasuredRendererContribution() => StaTest.Run(() =>
    {
        var measuredSmall = Row(
            "Mail",
            ResourceAttributionReliability.ExclusiveRendererProcesses,
            3,
            90);
        var shared = Row(
            "Shared media",
            ResourceAttributionReliability.SharedProcesses,
            null,
            null);
        var measuredBig = Row(
            "Map",
            ResourceAttributionReliability.ExclusiveRendererProcesses,
            7,
            256);
        var sample = Sample(
            1,
            ResourceSamplingState.Active,
            18,
            700,
            [measuredSmall, shared, measuredBig]);

        var cpu = new OrbitResourceHistoryGraph { Metric = OrbitResourceHistoryMetric.Cpu };
        var memory = new OrbitResourceHistoryGraph { Metric = OrbitResourceHistoryMetric.Memory };
        Assert.True(cpu.AcceptSample(sample));
        Assert.True(memory.AcceptSample(sample));

        Assert.Contains("Map", cpu.BiggestMeasuredConsumerText);
        Assert.Contains("7%", cpu.BiggestMeasuredConsumerText);
        Assert.Contains("Map", memory.BiggestMeasuredConsumerText);
        Assert.Contains("256 MB", memory.BiggestMeasuredConsumerText);
        Assert.Contains("measured renderer contribution", memory.BiggestMeasuredConsumerText);
        Assert.DoesNotContain("Shared media", cpu.BiggestMeasuredConsumerText);
        Assert.DoesNotContain("Shared media", memory.BiggestMeasuredConsumerText);
    });

    [Fact]
    public void AutomationDescribesValueStateScaleWindowAndConsumer() => StaTest.Run(() =>
    {
        var graph = new OrbitResourceHistoryGraph
        {
            Metric = OrbitResourceHistoryMetric.Cpu,
            ReducedMotion = true,
        };
        Assert.True(graph.AcceptSample(Sample(
            1,
            ResourceSamplingState.Active,
            12,
            300,
            [Row("Mail", ResourceAttributionReliability.ExclusiveRendererProcesses, 4, 64)])));
        StaTest.Prepare(graph, 420, 210);

        var peer = UIElementAutomationPeer.CreatePeerForElement(graph);
        Assert.NotNull(peer);
        Assert.Equal(AutomationControlType.Group, peer.GetAutomationControlType());
        Assert.Equal("CPU trend graph", peer.GetName());
        Assert.Contains("12%", AutomationProperties.GetItemStatus(graph));
        Assert.Contains("Sampling active", AutomationProperties.GetItemStatus(graph));
        Assert.Contains("Last updated", AutomationProperties.GetItemStatus(graph));
        Assert.Contains("Rolling window", AutomationProperties.GetHelpText(graph));
        Assert.Contains("Scale 0 to 100 percent", AutomationProperties.GetHelpText(graph));
        Assert.Contains("1 accepted real sample", AutomationProperties.GetHelpText(graph));
        Assert.Contains("No estimated or shared per-tab values", AutomationProperties.GetHelpText(graph));
        Assert.False(graph.HasActiveAnimation);
    });

    [Fact]
    public void RollingWindowUsesActualAcceptedTimestampsAndFiveSecondHostGuidance() => StaTest.Run(() =>
    {
        var graph = new OrbitResourceHistoryGraph { Metric = OrbitResourceHistoryMetric.Memory };
        Assert.Equal(TimeSpan.FromSeconds(5), OrbitResourceHistoryGraph.RecommendedSampleInterval);
        Assert.Equal(TimeSpan.FromSeconds(145), OrbitResourceHistoryGraph.NominalRollingWindow);

        Assert.True(graph.AcceptSample(Sample(1, ResourceSamplingState.Active, 2, 256)));
        Assert.True(graph.AcceptSample(Sample(6, ResourceSamplingState.Active, 4, 512)));

        Assert.Contains("5 sec", graph.TimeWindowText);
        Assert.Contains("2 accepted samples", graph.TimeWindowText);
        Assert.Equal("Scale 0 to 1.0 GB", graph.ScaleText);
    });

    [Fact]
    public void GraphRendersAtCommonWindowsScaleFactorsWithoutOwningATimer() => StaTest.Run(() =>
    {
        var timerFields = typeof(OrbitResourceHistoryGraph).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(field => typeof(DispatcherTimer).IsAssignableFrom(field.FieldType));
        Assert.Empty(timerFields);

        foreach (var metric in Enum.GetValues<OrbitResourceHistoryMetric>())
        foreach (var dpi in new[] { 96d, 120d, 144d, 192d })
        {
            var graph = new OrbitResourceHistoryGraph
            {
                Metric = metric,
                Width = 420,
                Height = 210,
            };
            for (var sampleId = 1; sampleId <= 8; sampleId++)
            {
                Assert.True(graph.AcceptSample(Sample(
                    sampleId,
                    ResourceSamplingState.Active,
                    sampleId * 4,
                    200 + (sampleId * 20))));
            }

            StaTest.Prepare(graph, 420, 210);
            var pixelsWide = (int)Math.Ceiling(420 * dpi / 96d);
            var pixelsHigh = (int)Math.Ceiling(210 * dpi / 96d);
            var bitmap = new RenderTargetBitmap(
                pixelsWide,
                pixelsHigh,
                dpi,
                dpi,
                PixelFormats.Pbgra32);
            bitmap.Render(graph);
            var pixels = new byte[pixelsWide * pixelsHigh * 4];
            bitmap.CopyPixels(pixels, pixelsWide * 4, 0);

            Assert.True(
                pixels.Where((_, index) => index % 4 == 3).Any(alpha => alpha > 0),
                $"{metric} graph did not render at {dpi} DPI.");
            Assert.False(graph.HasActiveAnimation);
        }
    });

    private static ResourceTaskPanelPresentation Sample(
        long sampleId,
        ResourceSamplingState state,
        double? cpuPercent,
        int memoryMegabytes,
        IReadOnlyList<TabResourceRowPresentation>? rows = null) => new(
            sampleId,
            new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero).AddSeconds(sampleId),
            state,
            ResourcePressureLevel.Low,
            cpuPercent,
            memoryMegabytes * 1024L * 1024L,
            3,
            false,
            "Accepted sample.",
            ResourceSortKind.CanonicalOrder,
            ResourceFilterKind.AllTabs,
            rows ?? []);

    private static TabResourceRowPresentation Row(
        string title,
        ResourceAttributionReliability reliability,
        double? cpuPercent,
        int? memoryMegabytes) => new(
            new BrowserTabId(Guid.NewGuid()),
            title,
            "example.test",
            false,
            false,
            reliability,
            cpuPercent,
            memoryMegabytes * 1024L * 1024L,
            reliability == ResourceAttributionReliability.Unavailable ? 0 : 1,
            reliability == ResourceAttributionReliability.SharedProcesses ? 2 : 0);
}
