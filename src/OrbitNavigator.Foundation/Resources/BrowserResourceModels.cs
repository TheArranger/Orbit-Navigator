using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.Foundation.Resources;

public enum ResourceSamplingState
{
    Starting = 0,
    Fresh = 1,
    Paused = 2,
    Stale = 3,
    Unavailable = 4,
}

public enum ResourceAttributionQuality
{
    Unavailable = 0,
    AggregateOnly = 1,
    SharedRenderer = 2,
    ExclusiveRendererMeasured = 3,
}

public enum ResourcePressureLevel
{
    Unknown = 0,
    Low = 1,
    Medium = 2,
    High = 3,
}

public enum ClosingTabsHelpfulness
{
    Unknown = 0,
    Unlikely = 1,
    MayHelp = 2,
    Likely = 3,
}

public enum WebViewProcessKind
{
    Unknown = 0,
    Browser = 1,
    Renderer = 2,
    Utility = 3,
    Gpu = 4,
    PpapiPlugin = 5,
    PpapiBroker = 6,
    SandboxHelper = 7,
}

public sealed record WebViewProcessAssociation(
    int ProcessId,
    WebViewProcessKind Kind,
    IReadOnlyList<ulong> FrameIds);

public sealed record WebViewTabResourceIdentity(
    BrowserTabId TabId,
    ulong MainFrameId,
    bool IsSelected,
    bool IsLoading,
    bool IsPlayingAudio,
    bool IsSuspended);

public sealed record ProcessResourceMeasurement(
    int ProcessId,
    long PrivateBytes,
    TimeSpan TotalProcessorTime);

public sealed record SystemResourceMeasurement(
    ulong TotalPhysicalBytes,
    ulong AvailablePhysicalBytes,
    ulong IdleTime,
    ulong KernelTime,
    ulong UserTime);

public sealed record TabResourceContribution(
    BrowserTabId TabId,
    ResourceAttributionQuality Quality,
    double? CpuPercent,
    long? PrivateBytes,
    int LinkedProcessCount,
    int SharedTabCount,
    bool IsSelected,
    bool IsLoading,
    bool IsPlayingAudio,
    bool IsSuspended);

public sealed record BrowserResourceSnapshot(
    PrivacyContext Context,
    BrowserWindowId WindowId,
    long SampleId,
    long MonotonicTimestamp,
    DateTimeOffset SampledAtUtc,
    ResourceSamplingState State,
    ResourcePressureLevel CpuPressure,
    ResourcePressureLevel MemoryPressure,
    double? SystemCpuPercent,
    double? OrbitCpuPercent,
    ulong? SystemUsedPhysicalBytes,
    ulong? SystemTotalPhysicalBytes,
    long? OrbitPrivateBytes,
    int OrbitProcessCount,
    ClosingTabsHelpfulness ClosingTabsMayHelp,
    IReadOnlyList<TabResourceContribution> Tabs);

public interface IWebViewResourceProcessSource
{
    ValueTask<ControllerResult<IReadOnlyList<WebViewProcessAssociation>>> GetProcessAssociationsAsync(
        PrivacyContext context,
        CancellationToken cancellationToken = default);
}

public interface IWindowsResourceMeasurementSource
{
    ControllerResult<SystemResourceMeasurement> ReadSystem();

    ControllerResult<ProcessResourceMeasurement> ReadProcess(int processId);
}

public interface IMonotonicTimestampSource
{
    long GetTimestamp();
    long Frequency { get; }
}
