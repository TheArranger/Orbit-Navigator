using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Presentation.Resources;
using OrbitNavigator.Presentation.Workspace;

namespace OrbitNavigator.Presentation.Tabs;

public enum TabControllerHostState
{
    Docked = 0,
    Detached = 1,
    Opening = 2,
    Closing = 3,
    Unavailable = 4,
}

public sealed record RevisionedTabControllerProjection(
    BrowserWindowId WindowId,
    bool IsPrivate,
    long Revision,
    TabControllerHostState HostState,
    TabStripPlacement Placement,
    TabStripViewState Tabs)
{
    public WorkspacePreviewRevealMode PreviewRevealMode { get; init; } = WorkspacePreviewRevealMode.Hover;

    public RevisionedTabControllerProjection Validate()
    {
        if (WindowId.IsEmpty || Tabs.WindowId != WindowId || Revision < 0)
        {
            throw new ArgumentException("The tab projection must identify one valid owner window.");
        }

        if (!Enum.IsDefined(HostState) || !Enum.IsDefined(Placement) || !Enum.IsDefined(PreviewRevealMode))
        {
            throw new ArgumentOutOfRangeException(nameof(HostState));
        }

        return this;
    }
}

public abstract record TabControllerAction(Guid StableActionId)
{
    protected static Guid RequireId(Guid value) =>
        value == Guid.Empty ? throw new ArgumentException("A stable action ID is required.") : value;
}

public sealed record SelectTabControllerAction(Guid Id, BrowserTabId TabId) : TabControllerAction(RequireId(Id));
public sealed record CloseTabControllerAction(Guid Id, BrowserTabId TabId) : TabControllerAction(RequireId(Id));
public sealed record DuplicateTabControllerAction(Guid Id, BrowserTabId TabId) : TabControllerAction(RequireId(Id));
public sealed record SetTabMutedControllerAction(Guid Id, BrowserTabId TabId, bool IsMuted) : TabControllerAction(RequireId(Id));
public sealed record SetTabSiteContentControlControllerAction(
    Guid Id,
    BrowserTabId TabId,
    TabSiteContentControlKind Kind,
    bool IsEnabled) : TabControllerAction(RequireId(Id));
public sealed record MoveTabControllerAction(Guid Id, BrowserTabId TabId, int NewIndex, BrowserTabGroupId? GroupId) : TabControllerAction(RequireId(Id));
public sealed record CreateNewTabControllerAction(Guid Id, BrowserTabGroupId? GroupId = null) : TabControllerAction(RequireId(Id));
public sealed record DetachTabControllerAction(Guid Id) : TabControllerAction(RequireId(Id));
public sealed record DockTabControllerAction(Guid Id) : TabControllerAction(RequireId(Id));
public sealed record SetTabPlacementControllerAction(Guid Id, TabStripPlacement Placement) : TabControllerAction(RequireId(Id));
public sealed record CreateTabGroupControllerAction(Guid Id, IReadOnlyList<BrowserTabId> TabIds, string Name) : TabControllerAction(RequireId(Id));
public sealed record RenameTabGroupControllerAction(Guid Id, BrowserTabGroupId GroupId, string RequestedName) : TabControllerAction(RequireId(Id));
public sealed record SetTabGroupColorControllerAction(Guid Id, BrowserTabGroupId GroupId, string ColorToken) : TabControllerAction(RequireId(Id));
public sealed record CloseTabGroupControllerAction(Guid Id, BrowserTabGroupId GroupId, IReadOnlyList<BrowserTabId> TabIds) : TabControllerAction(RequireId(Id));
public sealed record SaveTabGroupAsWorkspaceControllerAction(
    Guid Id,
    BrowserTabGroupId GroupId,
    WorkspacePresetDraftPresentation Draft) : TabControllerAction(RequireId(Id));
public sealed record UngroupTabGroupControllerAction(Guid Id, BrowserTabGroupId GroupId) : TabControllerAction(RequireId(Id));
public sealed record ToggleTabGroupControllerAction(Guid Id, BrowserTabGroupId GroupId, bool IsCollapsed) : TabControllerAction(RequireId(Id));
public sealed record SwitchToResourceTabControllerAction(Guid Id, BrowserTabId TabId) : TabControllerAction(RequireId(Id));
public sealed record BulkCloseTabsControllerAction(Guid Id, IReadOnlyList<BrowserTabId> TabIds) : TabControllerAction(RequireId(Id));

public sealed record TabControllerCommand(
    BrowserWindowId WindowId,
    long ExpectedRevision,
    bool IsPrivate,
    TabControllerAction Action);

public enum TabControllerCommandOutcome
{
    Accepted = 0,
    Stale = 1,
    PolicyDenied = 2,
    Failed = 3,
}

public sealed record TabControllerCommandResult(
    Guid StableActionId,
    TabControllerCommandOutcome Outcome,
    string SafeMessage,
    RevisionedTabControllerProjection? RefreshedProjection = null);

public interface ITabControllerCommandSink
{
    ValueTask<TabControllerCommandResult> ExecuteAsync(
        TabControllerCommand command,
        CancellationToken cancellationToken = default);
}

public enum TabControllerPresentationChangeKind
{
    Projection = 0,
    ResourceSample = 1,
    CommandResult = 2,
    EphemeralState = 3,
}

public sealed record TabControllerPresentationState(
    RevisionedTabControllerProjection Projection,
    ResourceTaskPanelPresentation Resources,
    IReadOnlyDictionary<BrowserTabId, TabVisualMetadataPresentation> TabVisuals,
    string? Announcement)
{
    public IReadOnlyDictionary<BrowserTabId, TabInteractionCapabilitiesPresentation> TabInteractions { get; init; } =
        new Dictionary<BrowserTabId, TabInteractionCapabilitiesPresentation>();
}

public sealed class TabControllerPresentationChangedEventArgs : EventArgs
{
    public TabControllerPresentationChangedEventArgs(
        TabControllerPresentationState state,
        TabControllerPresentationChangeKind kind)
    {
        State = state;
        Kind = kind;
    }

    public TabControllerPresentationState State { get; }
    public TabControllerPresentationChangeKind Kind { get; }
}

public sealed class TabControllerPresentationSession
{
    public static readonly TimeSpan MinimumVisibleSampleInterval = TimeSpan.FromSeconds(1);

    private readonly ITabControllerCommandSink commandSink;
    private readonly BrowserWindowId windowId;
    private readonly bool isPrivate;
    private TabControllerPresentationState? state;
    private long lastSampleId;
    private DateTimeOffset lastSampleAtUtc = DateTimeOffset.MinValue;

    public TabControllerPresentationSession(
        BrowserWindowId windowId,
        bool isPrivate,
        ITabControllerCommandSink commandSink)
    {
        if (windowId.IsEmpty)
        {
            throw new ArgumentException("An owner window ID is required.", nameof(windowId));
        }

        this.windowId = windowId;
        this.isPrivate = isPrivate;
        this.commandSink = commandSink ?? throw new ArgumentNullException(nameof(commandSink));
    }

    public event EventHandler<TabControllerPresentationChangedEventArgs>? PresentationChanged;

    public TabControllerPresentationState? Current => state;

    public void AcceptProjection(RevisionedTabControllerProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        projection.Validate();
        if (projection.WindowId != windowId || projection.IsPrivate != isPrivate)
        {
            throw new ArgumentException("A controller session cannot accept another window or privacy context.", nameof(projection));
        }

        if (isPrivate && projection.HostState is TabControllerHostState.Detached or TabControllerHostState.Opening)
        {
            throw new ArgumentException("Private tab controllers must remain docked and session-only.", nameof(projection));
        }

        if (state is not null && projection.Revision < state.Projection.Revision)
        {
            return;
        }

        state = new(
            projection,
            state?.Resources ?? ResourceTaskPanelPresentation.Empty,
            RetainCurrentTabVisuals(projection),
            null)
        {
            TabInteractions = RetainCurrentTabInteractions(projection),
        };
        Raise(TabControllerPresentationChangeKind.Projection);
    }

    public bool AcceptTabVisualMetadata(TabVisualMetadataPresentation visual)
    {
        ArgumentNullException.ThrowIfNull(visual);
        var accepted = visual.Validate();
        if (state is null ||
            !state.Projection.Tabs.Entries.OfType<BrowserTabEntry>()
                .Any(tab => tab.TabId == accepted.TabId))
        {
            return false;
        }

        if (state.TabVisuals.TryGetValue(accepted.TabId, out var current) &&
            accepted.Revision <= current.Revision)
        {
            return false;
        }

        var visuals = new Dictionary<BrowserTabId, TabVisualMetadataPresentation>(state.TabVisuals)
        {
            [accepted.TabId] = accepted,
        };
        state = state with { TabVisuals = visuals, Announcement = null };
        Raise(TabControllerPresentationChangeKind.EphemeralState);
        return true;
    }

    public bool AcceptTabInteractionCapabilities(TabInteractionCapabilitiesPresentation capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        var accepted = capabilities.Validate();
        if (state is null ||
            !state.Projection.Tabs.Entries.OfType<BrowserTabEntry>()
                .Any(tab => tab.TabId == accepted.TabId))
        {
            return false;
        }

        if (state.TabInteractions.TryGetValue(accepted.TabId, out var current) &&
            accepted.Revision <= current.Revision)
        {
            return false;
        }

        var interactions = new Dictionary<BrowserTabId, TabInteractionCapabilitiesPresentation>(state.TabInteractions)
        {
            [accepted.TabId] = accepted,
        };
        state = state with { TabInteractions = interactions, Announcement = null };
        Raise(TabControllerPresentationChangeKind.EphemeralState);
        return true;
    }

    public void AcceptResourceSample(ResourceSampleProjection sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        sample.Validate();
        if (state is null || sample.SampleId <= lastSampleId ||
            (lastSampleAtUtc != DateTimeOffset.MinValue &&
             sample.SampledAtUtc - lastSampleAtUtc < MinimumVisibleSampleInterval))
        {
            return;
        }

        lastSampleId = sample.SampleId;
        lastSampleAtUtc = sample.SampledAtUtc;
        var tabs = state.Projection.Tabs.Entries.OfType<BrowserTabEntry>().ToArray();
        var contributions = sample.TabContributions.ToDictionary(value => value.TabId);
        var rows = tabs.Select(tab =>
        {
            contributions.TryGetValue(tab.TabId, out var contribution);
            return new TabResourceRowPresentation(
                tab.TabId,
                tab.Title,
                tab.Address?.Host ?? "New tab",
                tab.IsSelected,
                tab.IsPrivate,
                contribution?.Reliability ?? ResourceAttributionReliability.Unavailable,
                contribution?.MeasuredRendererCpuPercent,
                contribution?.MeasuredRendererPrivateBytes,
                contribution?.LinkedProcessCount ?? 0,
                contribution?.SharedTabCount ?? 0);
        }).ToArray();

        state = state with
        {
            Resources = new(
                sample.SampleId,
                sample.SampledAtUtc,
                sample.SamplingState,
                sample.Pressure,
                sample.BrowserCpuPercent,
                sample.BrowserPrivateBytes,
                sample.ProcessCount,
                sample.ClosingTabsMayHelp,
                sample.StatusMessage.Trim(),
                state.Resources.Sort,
                state.Resources.Filter,
                rows),
            Announcement = null,
        };
        Raise(TabControllerPresentationChangeKind.ResourceSample);
    }

    public void AcceptCommandResult(TabControllerCommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.RefreshedProjection is not null)
        {
            AcceptProjection(result.RefreshedProjection);
        }

        if (state is null)
        {
            return;
        }

        var message = string.IsNullOrWhiteSpace(result.SafeMessage)
            ? result.Outcome switch
            {
                TabControllerCommandOutcome.Stale => "Tabs changed elsewhere. The current list has been refreshed.",
                TabControllerCommandOutcome.PolicyDenied => "That tab action is unavailable in this window.",
                TabControllerCommandOutcome.Failed => "The tab action could not be completed.",
                _ => "Tab action completed.",
            }
            : result.SafeMessage.Trim();
        state = state with { Announcement = message };
        Raise(TabControllerPresentationChangeKind.CommandResult);
    }

    public async ValueTask ExecuteAsync(
        TabControllerAction action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (state is null)
        {
            throw new InvalidOperationException("The controller requires an authoritative projection before commands can run.");
        }

        if (isPrivate && action is DetachTabControllerAction)
        {
            AcceptCommandResult(new(
                action.StableActionId,
                TabControllerCommandOutcome.PolicyDenied,
                "Private tab controllers stay docked and are not saved."));
            return;
        }

        var command = new TabControllerCommand(windowId, state.Projection.Revision, isPrivate, action);
        var result = await commandSink.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        AcceptCommandResult(result);
    }

    private void Raise(TabControllerPresentationChangeKind kind) =>
        PresentationChanged?.Invoke(this, new(state!, kind));

    private IReadOnlyDictionary<BrowserTabId, TabVisualMetadataPresentation> RetainCurrentTabVisuals(
        RevisionedTabControllerProjection projection)
    {
        if (state is null || state.TabVisuals.Count == 0)
        {
            return new Dictionary<BrowserTabId, TabVisualMetadataPresentation>();
        }

        var currentIds = projection.Tabs.Entries.OfType<BrowserTabEntry>()
            .Select(tab => tab.TabId)
            .ToHashSet();
        return state.TabVisuals
            .Where(pair => currentIds.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    private IReadOnlyDictionary<BrowserTabId, TabInteractionCapabilitiesPresentation> RetainCurrentTabInteractions(
        RevisionedTabControllerProjection projection)
    {
        if (state is null || state.TabInteractions.Count == 0)
        {
            return new Dictionary<BrowserTabId, TabInteractionCapabilitiesPresentation>();
        }

        var currentIds = projection.Tabs.Entries.OfType<BrowserTabEntry>()
            .Select(tab => tab.TabId)
            .ToHashSet();
        return state.TabInteractions
            .Where(pair => currentIds.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
    }
}
