using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;

namespace OrbitNavigator.Foundation.Profiles;

public sealed record ProfileStorageNamespaceCleanupReceipt(
    ProfileStorageNamespace Namespace,
    int DeletedFileCount);

public interface IProfileStorageNamespaceMaintenance
{
    ValueTask<ControllerResult<ProfileStorageNamespaceCleanupReceipt>> DeleteNamespaceAsync(
        PrivacyContext context,
        ProfileStorageNamespace storageNamespace,
        ProfileStorageDurability durability,
        CancellationToken cancellationToken = default);
}
