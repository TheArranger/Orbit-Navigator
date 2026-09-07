using System.Collections.ObjectModel;
using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.Contracts.Infrastructure;

public sealed class LocalDataClearRequest
{
    private LocalDataClearRequest(
        PrivacyContext context,
        IReadOnlyList<ProfileStorageNamespace> namespaces,
        ProfileStorageDurability durability)
    {
        Context = context;
        Namespaces = namespaces;
        Durability = durability;
    }

    public PrivacyContext Context { get; }

    public IReadOnlyList<ProfileStorageNamespace> Namespaces { get; }

    public ProfileStorageDurability Durability { get; }

    public bool IncludesDownloadedFiles => false;

    public static ControllerResult<LocalDataClearRequest> Create(
        PrivacyContext? context,
        IEnumerable<ProfileStorageNamespace>? namespaces,
        ProfileStorageDurability durability)
    {
        if (context is not { IsStructurallyValid: true } || !Enum.IsDefined(durability))
        {
            return ControllerResult<LocalDataClearRequest>.Failure(ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "error.local_data_clear.request_invalid"));
        }

        if (context.IsPrivate && durability == ProfileStorageDurability.Persistent)
        {
            return ControllerResult<LocalDataClearRequest>.Failure(ControllerError.Create(
                ControllerErrorCode.PolicyDenied,
                "error.private.persistent_storage_denied"));
        }

        var selected = namespaces?.Distinct().ToArray() ?? [];
        if (selected.Length == 0 || selected.Any(item => item is null))
        {
            return ControllerResult<LocalDataClearRequest>.Failure(ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "error.local_data_clear.namespaces_required"));
        }

        return ControllerResult<LocalDataClearRequest>.Success(new LocalDataClearRequest(
            context,
            new ReadOnlyCollection<ProfileStorageNamespace>(selected),
            durability));
    }
}

public sealed record LocalDataClearPreview(
    IReadOnlyList<ProfileStorageNamespace> Namespaces,
    bool DownloadedFilesWillBeDeleted);

public sealed record LocalDataClearReceipt(
    IReadOnlyList<ProfileStorageNamespace> ClearedNamespaces,
    DateTimeOffset CompletedAtUtc,
    bool DownloadedFilesWereDeleted);

public interface ILocalDataClearer
{
    ValueTask<ControllerResult<LocalDataClearPreview>> PreviewAsync(
        LocalDataClearRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<ControllerResult<LocalDataClearReceipt>> ClearAsync(
        LocalDataClearRequest request,
        CancellationToken cancellationToken = default);
}
