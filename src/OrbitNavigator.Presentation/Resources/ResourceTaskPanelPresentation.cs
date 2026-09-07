using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.Presentation.Resources;

public enum ResourceSamplingState
{
    Starting = 0,
    Active = 1,
    Paused = 2,
    Stale = 3,
    Unavailable = 4,
}

public enum ResourcePressureLevel
{
    Unknown = 0,
    Low = 1,
    Medium = 2,
    High = 3,
}

public enum ResourceAttributionReliability
{
    Unavailable = 0,
    SharedProcesses = 1,
    ExclusiveRendererProcesses = 2,
}

public enum ResourceSortKind
{
    CanonicalOrder = 0,
    HighestMeasuredCpu = 1,
    HighestMeasuredMemory = 2,
    Title = 3,
}

public enum ResourceFilterKind
{
    AllTabs = 0,
    BackgroundTabs = 1,
    MeasuredOnly = 2,
}

public sealed record TabResourceContributionProjection(
    BrowserTabId TabId,
    ResourceAttributionReliability Reliability,
    double? MeasuredRendererCpuPercent,
    long? MeasuredRendererPrivateBytes,
    int LinkedProcessCount,
    int SharedTabCount)
{
    public TabResourceContributionProjection Validate()
    {
        if (TabId.IsEmpty)
        {
            throw new ArgumentException("A tab ID is required.", nameof(TabId));
        }

        if (!Enum.IsDefined(Reliability))
        {
            throw new ArgumentOutOfRangeException(nameof(Reliability));
        }

        if (Reliability != ResourceAttributionReliability.ExclusiveRendererProcesses &&
            (MeasuredRendererCpuPercent is not null || MeasuredRendererPrivateBytes is not null))
        {
            throw new ArgumentException("Only exclusive renderer associations may carry numeric per-tab values.");
        }

        if (MeasuredRendererCpuPercent is < 0 or > 100 || MeasuredRendererPrivateBytes < 0 ||
            LinkedProcessCount < 0 || SharedTabCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MeasuredRendererCpuPercent));
        }

        return this;
    }
}

public sealed record ResourceSampleProjection(
    long SampleId,
    DateTimeOffset SampledAtUtc,
    ResourceSamplingState SamplingState,
    ResourcePressureLevel Pressure,
    double? BrowserCpuPercent,
    long? BrowserPrivateBytes,
    int ProcessCount,
    bool ClosingTabsMayHelp,
    string StatusMessage,
    IReadOnlyList<TabResourceContributionProjection> TabContributions)
{
    public ResourceSampleProjection Validate()
    {
        if (SampleId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SampleId));
        }

        if (!Enum.IsDefined(SamplingState) || !Enum.IsDefined(Pressure))
        {
            throw new ArgumentOutOfRangeException(nameof(SamplingState));
        }

        if (BrowserCpuPercent is < 0 or > 100 || BrowserPrivateBytes < 0 || ProcessCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(BrowserCpuPercent));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(StatusMessage);
        ArgumentNullException.ThrowIfNull(TabContributions);
        foreach (var contribution in TabContributions)
        {
            contribution.Validate();
        }

        return this;
    }
}

public sealed record TabResourceRowPresentation(
    BrowserTabId TabId,
    string Title,
    string SiteHost,
    bool IsSelected,
    bool IsPrivate,
    ResourceAttributionReliability Reliability,
    double? MeasuredRendererCpuPercent,
    long? MeasuredRendererPrivateBytes,
    int LinkedProcessCount,
    int SharedTabCount)
{
    public string ReliabilityLabel => Reliability switch
    {
        ResourceAttributionReliability.ExclusiveRendererProcesses => "Measured renderer contribution",
        ResourceAttributionReliability.SharedProcesses => "Shared — exact split unavailable",
        _ => "Resource contribution unavailable",
    };
}

public sealed record ResourceTaskPanelPresentation(
    long SampleId,
    DateTimeOffset SampledAtUtc,
    ResourceSamplingState SamplingState,
    ResourcePressureLevel Pressure,
    double? BrowserCpuPercent,
    long? BrowserPrivateBytes,
    int ProcessCount,
    bool ClosingTabsMayHelp,
    string StatusMessage,
    ResourceSortKind Sort,
    ResourceFilterKind Filter,
    IReadOnlyList<TabResourceRowPresentation> Rows)
{
    public static ResourceTaskPanelPresentation Empty { get; } = new(
        0,
        DateTimeOffset.MinValue,
        ResourceSamplingState.Paused,
        ResourcePressureLevel.Unknown,
        null,
        null,
        0,
        false,
        "Resource sampling is paused.",
        ResourceSortKind.CanonicalOrder,
        ResourceFilterKind.AllTabs,
        []);
}
