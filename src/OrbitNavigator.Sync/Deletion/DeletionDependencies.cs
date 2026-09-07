using System.Collections.Frozen;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Sync;

namespace OrbitNavigator.Sync.Deletion;

/// <summary>
/// The validated account, key, and client-fence material needed to publish a
/// deletion. Implementations must not expose raw key bytes through this boundary.
/// </summary>
public sealed record DeletionSyncSession(
    SyncKeyMaterialHandle KeyMaterial,
    SyncKeysetId KeysetId,
    long KeyEpoch,
    ClientFence Fence)
{
    public bool IsDefined =>
        KeyMaterial.Value != Guid.Empty &&
        KeysetId.IsDefined &&
        KeyEpoch >= 0 &&
        Fence.IsDefined;
}

/// <summary>
/// Resolves opaque account authorization and rejects stale client fences before
/// any local data is cleared or any sync dependency is invoked.
/// </summary>
public interface IDeletionSyncAuthorizer
{
    ValueTask<ControllerResult<DeletionSyncSession>> AuthorizeAsync(
        SyncOperationContext context,
        OpaqueAuthHandle authorization,
        ClientFence requestedFence,
        CancellationToken cancellationToken);
}

/// <summary>
/// Enumerates sync entity identifiers known locally before their corresponding
/// local records are cleared. It must not perform a network request.
/// </summary>
public interface IDeletionEntityCatalog
{
    ValueTask<ControllerResult<IReadOnlyList<SyncEntityId>>> ListEntityIdsAsync(
        SyncOperationContext context,
        SyncDataCategory category,
        CancellationToken cancellationToken);
}

public static class DataDeletionNamespaceMap
{
    private static readonly FrozenDictionary<LocalDataCategory, ProfileStorageNamespace> Namespaces =
        new Dictionary<LocalDataCategory, string>
        {
            [LocalDataCategory.Cache] = "cache",
            [LocalDataCategory.Cookies] = "cookies",
            [LocalDataCategory.SiteData] = "site-data",
            [LocalDataCategory.History] = "history",
            [LocalDataCategory.Settings] = "settings",
            [LocalDataCategory.OpenTabs] = "open-tabs",
            [LocalDataCategory.DownloadRecords] = "download-records",
            [LocalDataCategory.ClipboardShelf] = "clipboard-shelf",
            [LocalDataCategory.FormEntriesAutofill] = "form-entries-autofill",
            [LocalDataCategory.SiteSessionsAuthTokens] = "site-sessions-auth-tokens",
            [LocalDataCategory.SavedPasswords] = "saved-passwords",
            [LocalDataCategory.PermissionRules] = "permission-rules",
            [LocalDataCategory.SiteProtectionExceptions] = "site-protection-exceptions",
            [LocalDataCategory.ReadingPreferences] = "reading-preferences",
        }
        .ToFrozenDictionary(
            pair => pair.Key,
            pair => CreateNamespace(pair.Value));

    public static ControllerResult<IReadOnlyList<ProfileStorageNamespace>> Map(
        IEnumerable<LocalDataCategory>? categories)
    {
        if (categories is null)
        {
            return ControllerResult<IReadOnlyList<ProfileStorageNamespace>>.Failure(
                ControllerError.Create(
                    ControllerErrorCode.InvalidRequest,
                    "sync.deletion.local-categories-required"));
        }

        var selected = categories.Distinct().Order().ToArray();
        if (selected.Any(category => !Enum.IsDefined(category) || !Namespaces.ContainsKey(category)))
        {
            return ControllerResult<IReadOnlyList<ProfileStorageNamespace>>.Failure(
                ControllerError.Create(
                    ControllerErrorCode.InvalidRequest,
                    "sync.deletion.local-category-invalid"));
        }

        return ControllerResult<IReadOnlyList<ProfileStorageNamespace>>.Success(
            selected.Select(category => Namespaces[category]).ToArray());
    }

    private static ProfileStorageNamespace CreateNamespace(string value)
    {
        var result = ProfileStorageNamespace.Create(value);
        return result.IsSuccess
            ? result.Value!
            : throw new InvalidOperationException($"Invalid built-in deletion namespace: {value}");
    }
}
