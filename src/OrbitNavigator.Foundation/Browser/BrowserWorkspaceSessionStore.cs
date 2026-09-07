using System.Text.Json;
using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;

namespace OrbitNavigator.Foundation.Browser;

public readonly record struct BrowserWorkspaceSessionRevision(Guid Value)
{
    public bool IsEmpty => Value == Guid.Empty;
}

public sealed record BrowserWorkspaceSessionTab(
    BrowserTabId TabId,
    Uri? Address,
    string Title,
    BrowserTabGroupId? GroupId);

public sealed record BrowserWorkspaceSessionGroup(
    BrowserTabGroupId GroupId,
    string Name,
    bool IsCollapsed,
    string ColorToken,
    bool IsTemporary,
    IReadOnlyList<BrowserTabId> TabOrder);

public sealed record BrowserWorkspaceSessionSnapshot(
    ProfileId ProfileId,
    BrowserWorkspaceSessionRevision Revision,
    BrowserWindowId WindowId,
    BrowserTabId SelectedTabId,
    IReadOnlyList<BrowserWorkspaceSessionTab> Tabs,
    IReadOnlyList<BrowserWorkspaceSessionGroup> Groups);

public sealed record SaveBrowserWorkspaceSessionIntent(
    PrivacyContext Context,
    BrowserWorkspaceSessionRevision ExpectedRevision,
    BrowserWindowId WindowId,
    BrowserTabId SelectedTabId,
    IReadOnlyList<BrowserWorkspaceSessionTab> Tabs,
    IReadOnlyList<BrowserWorkspaceSessionGroup> Groups);

public interface IBrowserWorkspaceSessionStore
{
    ValueTask<ControllerResult<BrowserWorkspaceSessionSnapshot>> LoadAsync(
        PrivacyContext context,
        CancellationToken cancellationToken = default);

    ValueTask<ControllerResult<BrowserWorkspaceSessionSnapshot>> SaveAsync(
        SaveBrowserWorkspaceSessionIntent intent,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Normal-profile local restore data only. Private windows are rejected before
/// any profile-storage call and continue to use their window-scoped memory store.
/// </summary>
public sealed class BrowserWorkspaceSessionStore : IBrowserWorkspaceSessionStore
{
    public const int MaximumTabs = 100;
    public const int MaximumGroups = 50;
    private const int MaximumTitleLength = 512;
    private const string StorageKey = "primary";
    private readonly IProfileStorage _storage;
    private readonly ProfileStorageNamespace _namespace;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public BrowserWorkspaceSessionStore(IProfileStorage storage)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _namespace = ProfileStorageNamespace.Create("browser.workspace-session").Value!;
    }

    public async ValueTask<ControllerResult<BrowserWorkspaceSessionSnapshot>> LoadAsync(
        PrivacyContext context,
        CancellationToken cancellationToken = default)
    {
        var address = Address(context);
        if (!address.IsSuccess)
        {
            return ControllerResult<BrowserWorkspaceSessionSnapshot>.Failure(address.Error!);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var read = await _storage.ReadAsync(address.Value!, cancellationToken).ConfigureAwait(false);
            if (!read.IsSuccess)
            {
                return read.Error?.Code == ControllerErrorCode.NotFound
                    ? ControllerResult<BrowserWorkspaceSessionSnapshot>.Failure(read.Error)
                    : ControllerResult<BrowserWorkspaceSessionSnapshot>.Failure(read.Error!);
            }

            try
            {
                var stored = JsonSerializer.Deserialize<StoredSession>(read.Value!.Payload.Span);
                if (stored is null)
                {
                    return Corrupt();
                }
                var candidate = new BrowserWorkspaceSessionSnapshot(
                    context.ProfileId,
                    new BrowserWorkspaceSessionRevision(read.Value.Revision.Value),
                    stored.WindowId,
                    stored.SelectedTabId,
                    stored.Tabs,
                    stored.Groups);
                var reconciled = BrowserWorkspaceProfileReconciler.Reconcile(candidate);
                if (!reconciled.IsSuccess)
                {
                    return Corrupt();
                }
                if (!reconciled.Value!.WasRepaired)
                {
                    return ControllerResult<BrowserWorkspaceSessionSnapshot>.Success(
                        reconciled.Value.Snapshot);
                }

                var repaired = reconciled.Value.Snapshot;
                var payload = Serialize(repaired);
                var request = ProfileStorageWriteRequest.Create(
                    address.Value!,
                    payload,
                    read.Value.Revision).Value!;
                var write = await _storage.WriteAsync(request, cancellationToken).ConfigureAwait(false);
                return write.IsSuccess
                    ? ControllerResult<BrowserWorkspaceSessionSnapshot>.Success(repaired with
                    {
                        Revision = new BrowserWorkspaceSessionRevision(write.Value!.Revision.Value),
                    })
                    : ControllerResult<BrowserWorkspaceSessionSnapshot>.Failure(write.Error!);
            }
            catch (JsonException)
            {
                return Corrupt();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ControllerResult<BrowserWorkspaceSessionSnapshot>> SaveAsync(
        SaveBrowserWorkspaceSessionIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var address = Address(intent.Context);
        if (!address.IsSuccess)
        {
            return ControllerResult<BrowserWorkspaceSessionSnapshot>.Failure(address.Error!);
        }
        if (!Validate(intent.WindowId, intent.SelectedTabId, intent.Tabs, intent.Groups))
        {
            return Invalid();
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await _storage.ReadAsync(address.Value!, cancellationToken).ConfigureAwait(false);
            ProfileStorageRevision? expected;
            if (current.IsSuccess)
            {
                if (intent.ExpectedRevision.IsEmpty)
                {
                    if (IsValidStoredPayload(current.Value!.Payload.Span))
                    {
                        return Conflict();
                    }

                    // A structurally invalid restore snapshot is not usable
                    // authority. Repair only this isolated session record with
                    // an exact CAS against the corrupt entry's revision.
                    expected = current.Value.Revision;
                }
                else if (current.Value!.Revision.Value != intent.ExpectedRevision.Value)
                {
                    return Conflict();
                }
                else
                {
                    expected = current.Value.Revision;
                }
            }
            else if (current.Error?.Code == ControllerErrorCode.NotFound)
            {
                if (!intent.ExpectedRevision.IsEmpty)
                {
                    return Conflict();
                }
                expected = null;
            }
            else
            {
                return ControllerResult<BrowserWorkspaceSessionSnapshot>.Failure(current.Error!);
            }

            var payload = Serialize(new(
                intent.Context.ProfileId,
                intent.ExpectedRevision,
                intent.WindowId,
                intent.SelectedTabId,
                intent.Tabs,
                intent.Groups));
            var request = ProfileStorageWriteRequest.Create(address.Value!, payload, expected);
            if (!request.IsSuccess)
            {
                return ControllerResult<BrowserWorkspaceSessionSnapshot>.Failure(request.Error!);
            }
            var write = await _storage.WriteAsync(request.Value!, cancellationToken).ConfigureAwait(false);
            return write.IsSuccess
                ? ControllerResult<BrowserWorkspaceSessionSnapshot>.Success(new(
                    intent.Context.ProfileId,
                    new BrowserWorkspaceSessionRevision(write.Value!.Revision.Value),
                    intent.WindowId,
                    intent.SelectedTabId,
                    intent.Tabs.ToArray(),
                    intent.Groups.ToArray()))
                : ControllerResult<BrowserWorkspaceSessionSnapshot>.Failure(write.Error!);
        }
        finally
        {
            _gate.Release();
        }
    }

    private ControllerResult<ProfileStorageAddress> Address(PrivacyContext context)
    {
        if (context is not { IsStructurallyValid: true })
        {
            return ControllerResult<ProfileStorageAddress>.Failure(ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "error.workspace_session.context_invalid"));
        }
        if (context.IsPrivate)
        {
            return ControllerResult<ProfileStorageAddress>.Failure(ControllerError.Create(
                ControllerErrorCode.PolicyDenied,
                "error.workspace_session.private_denied"));
        }
        return ProfileStorageAddress.Create(
            context,
            _namespace,
            ProfileStorageKey.Create(StorageKey).Value,
            ProfileStorageDurability.Persistent);
    }

    private static bool Validate(
        BrowserWindowId windowId,
        BrowserTabId selectedTabId,
        IReadOnlyList<BrowserWorkspaceSessionTab>? tabs,
        IReadOnlyList<BrowserWorkspaceSessionGroup>? groups)
    {
        if (windowId.IsEmpty || selectedTabId.IsEmpty ||
            tabs is not { Count: > 0 and <= MaximumTabs } ||
            groups is not { Count: <= MaximumGroups })
        {
            return false;
        }

        var tabIds = new HashSet<BrowserTabId>();
        foreach (var tab in tabs)
        {
            if (tab is null || tab.TabId.IsEmpty || !tabIds.Add(tab.TabId) ||
                string.IsNullOrWhiteSpace(tab.Title) || tab.Title.Length > MaximumTitleLength ||
                (tab.Address is not null &&
                    (!tab.Address.IsAbsoluteUri || tab.Address.Scheme is not ("http" or "https") ||
                     !string.IsNullOrEmpty(tab.Address.UserInfo) || string.IsNullOrWhiteSpace(tab.Address.IdnHost))))
            {
                return false;
            }
        }
        if (!tabIds.Contains(selectedTabId))
        {
            return false;
        }

        var groupIds = new HashSet<BrowserTabGroupId>();
        var groupedTabs = new HashSet<BrowserTabId>();
        foreach (var group in groups)
        {
            if (group is null || group.GroupId.IsEmpty || !groupIds.Add(group.GroupId) ||
                string.IsNullOrWhiteSpace(group.Name) || group.Name.Trim().Length > 60 ||
                !ValidColor(group.ColorToken) || group.TabOrder is not { Count: > 0 } ||
                group.TabOrder.Any(tabId => !tabIds.Contains(tabId) || !groupedTabs.Add(tabId)))
            {
                return false;
            }
        }

        return tabs.All(tab => tab.GroupId is null || groupIds.Contains(tab.GroupId.Value)) &&
            groups.All(group => group.TabOrder.SequenceEqual(
                tabs.Where(tab => tab.GroupId == group.GroupId).Select(tab => tab.TabId)));
    }

    private static bool ValidColor(string value) => value is
        "SeaGlass" or "Gold" or "Violet" or "Scarlet" or "Azure" or "Slate";

    private static bool IsValidStoredPayload(ReadOnlySpan<byte> payload)
    {
        try
        {
            var stored = JsonSerializer.Deserialize<StoredSession>(payload);
            return stored is not null &&
                Validate(stored.WindowId, stored.SelectedTabId, stored.Tabs, stored.Groups);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static byte[] Serialize(BrowserWorkspaceSessionSnapshot snapshot) =>
        JsonSerializer.SerializeToUtf8Bytes(new StoredSession(
            snapshot.WindowId,
            snapshot.SelectedTabId,
            snapshot.Tabs.ToArray(),
            snapshot.Groups.Select(group => group with { TabOrder = group.TabOrder.ToArray() }).ToArray()));

    private static ControllerResult<BrowserWorkspaceSessionSnapshot> Invalid() =>
        ControllerResult<BrowserWorkspaceSessionSnapshot>.Failure(ControllerError.Create(
            ControllerErrorCode.InvalidRequest,
            "error.workspace_session.invalid"));

    private static ControllerResult<BrowserWorkspaceSessionSnapshot> Conflict() =>
        ControllerResult<BrowserWorkspaceSessionSnapshot>.Failure(ControllerError.Create(
            ControllerErrorCode.Conflict,
            "error.workspace_session.revision_conflict"));

    private static ControllerResult<BrowserWorkspaceSessionSnapshot> Corrupt() =>
        ControllerResult<BrowserWorkspaceSessionSnapshot>.Failure(ControllerError.Create(
            ControllerErrorCode.IntegrityFailure,
            "error.workspace_session.corrupt"));

    private sealed record StoredSession(
        BrowserWindowId WindowId,
        BrowserTabId SelectedTabId,
        IReadOnlyList<BrowserWorkspaceSessionTab> Tabs,
        IReadOnlyList<BrowserWorkspaceSessionGroup> Groups);
}
