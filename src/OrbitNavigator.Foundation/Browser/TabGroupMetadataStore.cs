using System.Text.Json;
using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;

namespace OrbitNavigator.Foundation.Browser;

public sealed class TabGroupMetadataStore : ITabGroupMetadataStore, IAsyncDisposable
{
    public const int MaximumGroupNameLength = 60;
    private readonly IProfileStorage _storage;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ProfileStorageNamespace _namespace;
    private PrivateCatalog? _privateCatalog;
    private long _privateGeneration;
    private int _disposed;

    public TabGroupMetadataStore(IProfileStorage storage)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _namespace = ProfileStorageNamespace.Create("browser.tab-groups").Value!;
    }

    public async ValueTask<ControllerResult<TabGroupMetadataSnapshot>> LoadAsync(
        PrivacyContext context,
        BrowserWindowId windowId,
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return Closed();
        }
        if (context is { IsStructurallyValid: true, IsPrivate: true })
        {
            return await LoadPrivateAsync(context, windowId, cancellationToken).ConfigureAwait(false);
        }

        var addressResult = Address(context, windowId);
        if (!addressResult.IsSuccess)
        {
            return ControllerResult<TabGroupMetadataSnapshot>.Failure(addressResult.Error!);
        }

        var read = await _storage.ReadAsync(addressResult.Value!, cancellationToken)
            .ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            return read.Error?.Code == ControllerErrorCode.NotFound
                ? ControllerResult<TabGroupMetadataSnapshot>.Success(new TabGroupMetadataSnapshot(
                    context,
                    windowId,
                    default,
                    []))
                : ControllerResult<TabGroupMetadataSnapshot>.Failure(read.Error!);
        }

        try
        {
            var groups = JsonSerializer.Deserialize<TabGroupMetadata[]>(read.Value!.Payload.Span);
            if (groups is null || !ValidateGroups(groups))
            {
                return Corrupt();
            }

            return ControllerResult<TabGroupMetadataSnapshot>.Success(new TabGroupMetadataSnapshot(
                context,
                windowId,
                new TabGroupCatalogRevision(read.Value.Revision.Value),
                groups));
        }
        catch (JsonException)
        {
            return Corrupt();
        }
    }

    public async ValueTask<ControllerResult<TabGroupMetadataSnapshot>> ReplaceAsync(
        ReplaceTabGroupMetadataIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (Volatile.Read(ref _disposed) != 0)
        {
            return Closed();
        }
        if (intent.Context is not { IsStructurallyValid: true } ||
            intent.WindowId.IsEmpty ||
            intent.Groups is null ||
            !ValidateGroups(intent.Groups))
        {
            return ControllerResult<TabGroupMetadataSnapshot>.Failure(ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "error.tab_groups.replace_invalid"));
        }
        if (intent.Context.IsPrivate)
        {
            return await ReplacePrivateAsync(intent, cancellationToken).ConfigureAwait(false);
        }

        var address = Address(intent.Context, intent.WindowId).Value!;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await _storage.ReadAsync(address, cancellationToken).ConfigureAwait(false);
            if (intent.ExpectedRevision.IsEmpty)
            {
                if (current.IsSuccess)
                {
                    return Conflict();
                }

                if (current.Error?.Code != ControllerErrorCode.NotFound)
                {
                    return ControllerResult<TabGroupMetadataSnapshot>.Failure(current.Error!);
                }
            }
            else if (!current.IsSuccess ||
                current.Value!.Revision.Value != intent.ExpectedRevision.Value)
            {
                return Conflict();
            }

            var bytes = JsonSerializer.SerializeToUtf8Bytes(intent.Groups);
            ProfileStorageRevision? expectedStorageRevision = intent.ExpectedRevision.IsEmpty
                ? null
                : new ProfileStorageRevision(intent.ExpectedRevision.Value);
            var request = ProfileStorageWriteRequest.Create(
                address,
                bytes,
                expectedStorageRevision).Value!;
            var write = await _storage.WriteAsync(request, cancellationToken).ConfigureAwait(false);
            if (!write.IsSuccess)
            {
                return ControllerResult<TabGroupMetadataSnapshot>.Failure(write.Error!);
            }

            return ControllerResult<TabGroupMetadataSnapshot>.Success(new TabGroupMetadataSnapshot(
                intent.Context,
                intent.WindowId,
                new TabGroupCatalogRevision(write.Value!.Revision.Value),
                intent.Groups.ToArray()));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _privateCatalog = null;
            _privateGeneration = 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<ControllerResult<TabGroupMetadataSnapshot>> LoadPrivateAsync(
        PrivacyContext context,
        BrowserWindowId windowId,
        CancellationToken cancellationToken)
    {
        if (windowId.IsEmpty)
        {
            return InvalidContext();
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return Closed();
            }

            var identity = new PrivateCatalogIdentity(
                context.ProfileId,
                context.SessionId,
                windowId);
            if (_privateCatalog is null)
            {
                _privateCatalog = new PrivateCatalog(identity, default, []);
            }
            else if (_privateCatalog.Identity != identity)
            {
                return PrivateIsolationDenied();
            }

            return ControllerResult<TabGroupMetadataSnapshot>.Success(
                Snapshot(context, windowId, _privateCatalog.Revision, _privateCatalog.Groups));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<ControllerResult<TabGroupMetadataSnapshot>> ReplacePrivateAsync(
        ReplaceTabGroupMetadataIntent intent,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return Closed();
            }

            var identity = new PrivateCatalogIdentity(
                intent.Context.ProfileId,
                intent.Context.SessionId,
                intent.WindowId);
            if (_privateCatalog is null)
            {
                _privateCatalog = new PrivateCatalog(identity, default, []);
            }
            else if (_privateCatalog.Identity != identity)
            {
                return PrivateIsolationDenied();
            }

            if (_privateCatalog.Revision != intent.ExpectedRevision)
            {
                return Conflict();
            }

            _privateGeneration = checked(_privateGeneration + 1);
            var revision = new TabGroupCatalogRevision(Guid.NewGuid());
            var groups = CloneGroups(intent.Groups);
            _privateCatalog = new PrivateCatalog(identity, revision, groups);
            return ControllerResult<TabGroupMetadataSnapshot>.Success(
                Snapshot(intent.Context, intent.WindowId, revision, groups));
        }
        finally
        {
            _gate.Release();
        }
    }

    private ControllerResult<ProfileStorageAddress> Address(
        PrivacyContext context,
        BrowserWindowId windowId)
    {
        if (context is not { IsStructurallyValid: true } || windowId.IsEmpty)
        {
            return ControllerResult<ProfileStorageAddress>.Failure(ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "error.tab_groups.context_invalid"));
        }

        var key = ProfileStorageKey.Create($"window:{windowId.Value:N}");
        return key.IsSuccess
            ? ProfileStorageAddress.Create(
                context,
                _namespace,
                key.Value,
                context.IsPrivate
                    ? ProfileStorageDurability.Session
                    : ProfileStorageDurability.Persistent)
            : ControllerResult<ProfileStorageAddress>.Failure(key.Error!);
    }

    private static bool ValidateGroups(IEnumerable<TabGroupMetadata> groups)
    {
        var groupIds = new HashSet<BrowserTabGroupId>();
        var tabIds = new HashSet<BrowserTabId>();
        foreach (var group in groups)
        {
            if (group is null ||
                group.GroupId.IsEmpty ||
                !groupIds.Add(group.GroupId) ||
                string.IsNullOrWhiteSpace(group.Name) ||
                group.Name.Trim().Length > MaximumGroupNameLength ||
                group.TabOrder is null ||
                group.TabOrder.Any(tabId => tabId.IsEmpty || !tabIds.Add(tabId)))
            {
                return false;
            }
        }

        return true;
    }

    private static TabGroupMetadataSnapshot Snapshot(
        PrivacyContext context,
        BrowserWindowId windowId,
        TabGroupCatalogRevision revision,
        IReadOnlyList<TabGroupMetadata> groups) =>
        new(context, windowId, revision, CloneGroups(groups));

    private static TabGroupMetadata[] CloneGroups(IEnumerable<TabGroupMetadata> groups) =>
        groups.Select(group => group with { TabOrder = group.TabOrder.ToArray() }).ToArray();

    private static ControllerResult<TabGroupMetadataSnapshot> Conflict() =>
        ControllerResult<TabGroupMetadataSnapshot>.Failure(ControllerError.Create(
            ControllerErrorCode.Conflict,
            "error.tab_groups.revision_conflict"));

    private static ControllerResult<TabGroupMetadataSnapshot> Corrupt() =>
        ControllerResult<TabGroupMetadataSnapshot>.Failure(ControllerError.Create(
            ControllerErrorCode.IntegrityFailure,
            "error.tab_groups.storage_corrupt"));

    private static ControllerResult<TabGroupMetadataSnapshot> InvalidContext() =>
        ControllerResult<TabGroupMetadataSnapshot>.Failure(ControllerError.Create(
            ControllerErrorCode.InvalidRequest,
            "error.tab_groups.context_invalid"));

    private static ControllerResult<TabGroupMetadataSnapshot> PrivateIsolationDenied() =>
        ControllerResult<TabGroupMetadataSnapshot>.Failure(ControllerError.Create(
            ControllerErrorCode.PolicyDenied,
            "error.tab_groups.private_isolation_denied"));

    private static ControllerResult<TabGroupMetadataSnapshot> Closed() =>
        ControllerResult<TabGroupMetadataSnapshot>.Failure(ControllerError.Create(
            ControllerErrorCode.Unavailable,
            "error.tab_groups.store_closed"));

    private sealed record PrivateCatalogIdentity(
        ProfileId ProfileId,
        BrowserSessionId SessionId,
        BrowserWindowId WindowId);

    private sealed record PrivateCatalog(
        PrivateCatalogIdentity Identity,
        TabGroupCatalogRevision Revision,
        IReadOnlyList<TabGroupMetadata> Groups);
}
