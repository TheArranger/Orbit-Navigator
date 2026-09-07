using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.Foundation.Resources;

public sealed class BrowserResourceSampler
{
    private readonly PrivacyContext _context;
    private readonly BrowserWindowId _windowId;
    private readonly IWebViewResourceProcessSource _webViewProcesses;
    private readonly IWindowsResourceMeasurementSource _windows;
    private readonly IMonotonicTimestampSource _timestamps;
    private readonly int _processorCount;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<int, ProcessResourceMeasurement> _previousProcesses = [];
    private SystemResourceMeasurement? _previousSystem;
    private long _previousTimestamp;
    private long _sampleId;

    public BrowserResourceSampler(
        PrivacyContext context,
        BrowserWindowId windowId,
        IWebViewResourceProcessSource webViewProcesses,
        IWindowsResourceMeasurementSource windows,
        IMonotonicTimestampSource timestamps,
        int? processorCount = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _windowId = windowId;
        _webViewProcesses = webViewProcesses ?? throw new ArgumentNullException(nameof(webViewProcesses));
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _timestamps = timestamps ?? throw new ArgumentNullException(nameof(timestamps));
        _processorCount = Math.Max(1, processorCount ?? Environment.ProcessorCount);
        if (!context.IsStructurallyValid || windowId.IsEmpty)
        {
            throw new ArgumentException("A valid browser resource context is required.", nameof(context));
        }
    }

    public async ValueTask<ControllerResult<BrowserResourceSnapshot>> SampleAsync(
        IReadOnlyList<WebViewTabResourceIdentity> tabs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        if (tabs.Any(tab => tab.TabId.IsEmpty) ||
            tabs.Select(tab => tab.TabId).Distinct().Count() != tabs.Count)
        {
            return ControllerResult<BrowserResourceSnapshot>.Failure(ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "error.resource.tabs_invalid"));
        }

        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return ControllerResult<BrowserResourceSnapshot>.Failure(ControllerError.Create(
                ControllerErrorCode.Conflict,
                "error.resource.sample_in_progress"));
        }

        try
        {
            var timestamp = _timestamps.GetTimestamp();
            var system = _windows.ReadSystem();
            var associations = await _webViewProcesses.GetProcessAssociationsAsync(_context, cancellationToken)
                .ConfigureAwait(false);
            if (!system.IsSuccess || !associations.IsSuccess)
            {
                return ControllerResult<BrowserResourceSnapshot>.Success(Unavailable(tabs, timestamp));
            }

            var processes = new Dictionary<int, ProcessResourceMeasurement>();
            foreach (var processId in associations.Value!.Select(item => item.ProcessId)
                .Append(Environment.ProcessId)
                .Where(id => id > 0)
                .Distinct())
            {
                var measurement = _windows.ReadProcess(processId);
                if (measurement.IsSuccess)
                {
                    processes[processId] = measurement.Value!;
                }
            }

            var elapsedSeconds = _previousTimestamp > 0 && timestamp > _previousTimestamp
                ? (double)(timestamp - _previousTimestamp) / _timestamps.Frequency
                : 0;
            var systemCpu = SystemCpu(_previousSystem, system.Value!);
            var processCpu = processes.ToDictionary(
                pair => pair.Key,
                pair => ProcessCpu(_previousProcesses.GetValueOrDefault(pair.Key), pair.Value, elapsedSeconds));
            var orbitCpu = processCpu.Values.Where(value => value is not null).Sum(value => value!.Value);
            var frameOwners = associations.Value!
                .Where(process => process.Kind == WebViewProcessKind.Renderer)
                .SelectMany(process => process.FrameIds.Select(frame => (frame, process.ProcessId)))
                .GroupBy(pair => pair.frame)
                .ToDictionary(group => group.Key, group => group.Select(pair => pair.ProcessId).Distinct().ToArray());
            var processTabs = tabs
                .Where(tab => tab.MainFrameId != 0 && frameOwners.ContainsKey(tab.MainFrameId))
                .SelectMany(tab => frameOwners[tab.MainFrameId].Select(pid => (tab.TabId, pid)))
                .GroupBy(pair => pair.pid)
                .ToDictionary(group => group.Key, group => group.Select(pair => pair.TabId).Distinct().ToArray());
            var tabRows = tabs.Select(tab => TabContribution(
                tab,
                frameOwners,
                processTabs,
                processes,
                processCpu)).ToArray();

            var totalPhysical = system.Value!.TotalPhysicalBytes;
            var usedPhysical = totalPhysical - Math.Min(totalPhysical, system.Value.AvailablePhysicalBytes);
            var memoryRatio = totalPhysical == 0 ? 0 : (double)usedPhysical / totalPhysical;
            var cpuPressure = Pressure(systemCpu);
            var memoryPressure = Pressure(memoryRatio * 100);
            var orbitPrivate = processes.Values.Sum(process => process.PrivateBytes);
            var background = tabRows.Where(tab => !tab.IsSelected).ToArray();
            var exclusivePressure = background.Any(tab =>
                tab.Quality == ResourceAttributionQuality.ExclusiveRendererMeasured &&
                ((tab.CpuPercent ?? 0) >= 20 || (tab.PrivateBytes ?? 0) >= 512L * 1024 * 1024));
            var helpfulness = cpuPressure == ResourcePressureLevel.High || memoryPressure == ResourcePressureLevel.High
                ? exclusivePressure ? ClosingTabsHelpfulness.Likely :
                    background.Length > 0 ? ClosingTabsHelpfulness.MayHelp : ClosingTabsHelpfulness.Unlikely
                : ClosingTabsHelpfulness.Unlikely;

            _previousSystem = system.Value;
            _previousTimestamp = timestamp;
            _previousProcesses.Clear();
            foreach (var pair in processes) _previousProcesses.Add(pair.Key, pair.Value);
            return ControllerResult<BrowserResourceSnapshot>.Success(new(
                _context,
                _windowId,
                checked(++_sampleId),
                timestamp,
                DateTimeOffset.UtcNow,
                ResourceSamplingState.Fresh,
                cpuPressure,
                memoryPressure,
                systemCpu,
                elapsedSeconds > 0 ? orbitCpu : null,
                usedPhysical,
                totalPhysical,
                orbitPrivate,
                processes.Count,
                helpfulness,
                tabRows));
        }
        finally
        {
            _gate.Release();
        }
    }

    private BrowserResourceSnapshot Unavailable(
        IReadOnlyList<WebViewTabResourceIdentity> tabs,
        long timestamp) =>
        new(
            _context,
            _windowId,
            checked(++_sampleId),
            timestamp,
            DateTimeOffset.UtcNow,
            ResourceSamplingState.Unavailable,
            ResourcePressureLevel.Unknown,
            ResourcePressureLevel.Unknown,
            null,
            null,
            null,
            null,
            null,
            0,
            ClosingTabsHelpfulness.Unknown,
            tabs.Select(tab => new TabResourceContribution(
                tab.TabId,
                ResourceAttributionQuality.Unavailable,
                null,
                null,
                0,
                0,
                tab.IsSelected,
                tab.IsLoading,
                tab.IsPlayingAudio,
                tab.IsSuspended)).ToArray());

    private static TabResourceContribution TabContribution(
        WebViewTabResourceIdentity tab,
        IReadOnlyDictionary<ulong, int[]> frameOwners,
        IReadOnlyDictionary<int, BrowserTabId[]> processTabs,
        IReadOnlyDictionary<int, ProcessResourceMeasurement> processes,
        IReadOnlyDictionary<int, double?> processCpu)
    {
        if (tab.MainFrameId == 0 || !frameOwners.TryGetValue(tab.MainFrameId, out var pids) || pids.Length == 0)
        {
            return Row(tab, ResourceAttributionQuality.AggregateOnly, null, null, 0, 0);
        }

        if (pids.Length == 1 &&
            processTabs.TryGetValue(pids[0], out var owners) && owners.Length == 1 &&
            processes.TryGetValue(pids[0], out var measurement))
        {
            return Row(
                tab,
                ResourceAttributionQuality.ExclusiveRendererMeasured,
                processCpu.GetValueOrDefault(pids[0]),
                measurement.PrivateBytes,
                1,
                1);
        }

        var sharedTabs = pids
            .Where(processTabs.ContainsKey)
            .SelectMany(pid => processTabs[pid])
            .Distinct()
            .Count();
        return Row(
            tab,
            ResourceAttributionQuality.SharedRenderer,
            null,
            null,
            pids.Length,
            sharedTabs);
    }

    private static TabResourceContribution Row(
        WebViewTabResourceIdentity tab,
        ResourceAttributionQuality quality,
        double? cpu,
        long? bytes,
        int linkedProcesses,
        int sharedTabs) =>
        new(
            tab.TabId,
            quality,
            cpu,
            bytes,
            linkedProcesses,
            sharedTabs,
            tab.IsSelected,
            tab.IsLoading,
            tab.IsPlayingAudio,
            tab.IsSuspended);

    private double? ProcessCpu(
        ProcessResourceMeasurement? previous,
        ProcessResourceMeasurement current,
        double elapsedSeconds)
    {
        if (previous is null || elapsedSeconds <= 0 || current.TotalProcessorTime < previous.TotalProcessorTime)
        {
            return null;
        }
        var percent = (current.TotalProcessorTime - previous.TotalProcessorTime).TotalSeconds /
            (elapsedSeconds * _processorCount) * 100;
        return Math.Clamp(percent, 0, 100);
    }

    private static double? SystemCpu(
        SystemResourceMeasurement? previous,
        SystemResourceMeasurement current)
    {
        if (previous is null) return null;
        var previousTotal = previous.KernelTime + previous.UserTime;
        var currentTotal = current.KernelTime + current.UserTime;
        if (currentTotal <= previousTotal || current.IdleTime < previous.IdleTime) return null;
        var total = currentTotal - previousTotal;
        var idle = current.IdleTime - previous.IdleTime;
        return Math.Clamp((double)(total - Math.Min(total, idle)) / total * 100, 0, 100);
    }

    private static ResourcePressureLevel Pressure(double? percent) => percent switch
    {
        null => ResourcePressureLevel.Unknown,
        < 60 => ResourcePressureLevel.Low,
        < 85 => ResourcePressureLevel.Medium,
        _ => ResourcePressureLevel.High,
    };
}
