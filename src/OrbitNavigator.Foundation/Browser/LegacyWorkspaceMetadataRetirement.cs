using System.Text.Json;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Foundation.Profiles;

namespace OrbitNavigator.Foundation.Browser;

public sealed record LegacyWorkspaceMetadataRetirementReceipt(
    bool AlreadyRetired,
    int DeletedLegacyFiles);

public sealed class LegacyWorkspaceMetadataRetirement
{
    private const string MarkerKey = "workspace-session-v2";
    private readonly IProfileStorage _storage;
    private readonly IProfileStorageNamespaceMaintenance _maintenance;
    private readonly ProfileStorageNamespace _migrationNamespace;
    private readonly ProfileStorageNamespace _legacyGroupNamespace;

    public LegacyWorkspaceMetadataRetirement(
        IProfileStorage storage,
        IProfileStorageNamespaceMaintenance maintenance)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _maintenance = maintenance ?? throw new ArgumentNullException(nameof(maintenance));
        _migrationNamespace = ProfileStorageNamespace.Create("browser.migrations").Value!;
        _legacyGroupNamespace = ProfileStorageNamespace.Create("browser.tab-groups").Value!;
    }

    public async ValueTask<ControllerResult<LegacyWorkspaceMetadataRetirementReceipt>> RetireAsync(
        PrivacyContext context,
        BrowserWorkspaceSessionSnapshot authoritativeSession,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authoritativeSession);
        if (!context.IsStructurallyValid || context.IsPrivate ||
            authoritativeSession.ProfileId != context.ProfileId ||
            authoritativeSession.Tabs.Count == 0)
        {
            return ControllerResult<LegacyWorkspaceMetadataRetirementReceipt>.Failure(
                ControllerError.Create(
                    ControllerErrorCode.PolicyDenied,
                    "error.workspace_migration.retirement_denied"));
        }

        var address = ProfileStorageAddress.Create(
            context,
            _migrationNamespace,
            ProfileStorageKey.Create(MarkerKey).Value,
            ProfileStorageDurability.Persistent).Value!;
        var marker = await _storage.ReadAsync(address, cancellationToken).ConfigureAwait(false);
        if (marker.IsSuccess)
        {
            return ControllerResult<LegacyWorkspaceMetadataRetirementReceipt>.Success(new(true, 0));
        }
        if (marker.Error?.Code != ControllerErrorCode.NotFound)
        {
            return ControllerResult<LegacyWorkspaceMetadataRetirementReceipt>.Failure(marker.Error!);
        }

        var cleanup = await _maintenance.DeleteNamespaceAsync(
            context,
            _legacyGroupNamespace,
            ProfileStorageDurability.Persistent,
            cancellationToken).ConfigureAwait(false);
        if (!cleanup.IsSuccess)
        {
            return ControllerResult<LegacyWorkspaceMetadataRetirementReceipt>.Failure(cleanup.Error!);
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            SchemaVersion = 2,
            authoritativeSession.WindowId,
            authoritativeSession.Revision,
            RetiredAtUtc = DateTimeOffset.UtcNow,
        });
        var write = await _storage.WriteAsync(
            ProfileStorageWriteRequest.Create(address, payload).Value!,
            cancellationToken).ConfigureAwait(false);
        return write.IsSuccess
            ? ControllerResult<LegacyWorkspaceMetadataRetirementReceipt>.Success(new(
                false,
                cleanup.Value!.DeletedFileCount))
            : ControllerResult<LegacyWorkspaceMetadataRetirementReceipt>.Failure(write.Error!);
    }
}
