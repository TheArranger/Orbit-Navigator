using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;

namespace OrbitNavigator.Foundation.Browser;

public sealed record WorkspaceArtworkAsset(string AssetId, ReadOnlyMemory<byte> PngBytes);

public interface IWorkspaceArtworkStore
{
    ValueTask<ControllerResult<WorkspaceArtworkAsset>> ImportAsync(
        PrivacyContext context,
        ReadOnlyMemory<byte> pngBytes,
        CancellationToken cancellationToken = default);

    ValueTask<ControllerResult<WorkspaceArtworkAsset>> LoadAsync(
        PrivacyContext context,
        string assetId,
        CancellationToken cancellationToken = default);
}

public sealed class WorkspaceArtworkStore : IWorkspaceArtworkStore
{
    public const int MaximumPngBytes = 4 * 1024 * 1024;
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private readonly IProfileStorage _storage;
    private readonly ProfileStorageNamespace _namespace;

    public WorkspaceArtworkStore(IProfileStorage storage)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _namespace = ProfileStorageNamespace.Create("browser.workspace-artwork").Value!;
    }

    public async ValueTask<ControllerResult<WorkspaceArtworkAsset>> ImportAsync(
        PrivacyContext context,
        ReadOnlyMemory<byte> pngBytes,
        CancellationToken cancellationToken = default)
    {
        if (context is not { IsStructurallyValid: true } || context.IsPrivate ||
            !IsSafePng(pngBytes.Span))
        {
            return Failure(context?.IsPrivate == true
                ? ControllerErrorCode.PolicyDenied
                : ControllerErrorCode.InvalidRequest,
                context?.IsPrivate == true
                    ? "error.workspace_artwork.private_write_denied"
                    : "error.workspace_artwork.invalid");
        }

        var assetId = $"wa_{Guid.NewGuid():N}";
        var address = Address(context, assetId);
        if (!address.IsSuccess)
        {
            return ControllerResult<WorkspaceArtworkAsset>.Failure(address.Error!);
        }

        var request = ProfileStorageWriteRequest.Create(address.Value!, pngBytes.Span, null);
        if (!request.IsSuccess)
        {
            return ControllerResult<WorkspaceArtworkAsset>.Failure(request.Error!);
        }
        var write = await _storage.WriteAsync(request.Value!, cancellationToken).ConfigureAwait(false);
        return write.IsSuccess
            ? ControllerResult<WorkspaceArtworkAsset>.Success(new(assetId, pngBytes.ToArray()))
            : ControllerResult<WorkspaceArtworkAsset>.Failure(write.Error!);
    }

    public async ValueTask<ControllerResult<WorkspaceArtworkAsset>> LoadAsync(
        PrivacyContext context,
        string assetId,
        CancellationToken cancellationToken = default)
    {
        if (context is not { IsStructurallyValid: true } || !IsAssetId(assetId))
        {
            return Failure(ControllerErrorCode.InvalidRequest, "error.workspace_artwork.invalid");
        }

        var address = Address(context, assetId);
        if (!address.IsSuccess)
        {
            return ControllerResult<WorkspaceArtworkAsset>.Failure(address.Error!);
        }
        var read = await _storage.ReadAsync(address.Value!, cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            return ControllerResult<WorkspaceArtworkAsset>.Failure(read.Error!);
        }
        return IsSafePng(read.Value!.Payload.Span)
            ? ControllerResult<WorkspaceArtworkAsset>.Success(new(assetId, read.Value.Payload.ToArray()))
            : Failure(ControllerErrorCode.IntegrityFailure, "error.workspace_artwork.corrupt");
    }

    private ControllerResult<ProfileStorageAddress> Address(PrivacyContext context, string assetId)
    {
        var normal = context.IsPrivate
            ? new PrivacyContext(context.ProfileId, context.SessionId, BrowserProfileMode.Normal)
            : context;
        return ProfileStorageAddress.Create(
            normal,
            _namespace,
            ProfileStorageKey.Create(assetId).Value,
            ProfileStorageDurability.Persistent);
    }

    private static bool IsSafePng(ReadOnlySpan<byte> bytes) =>
        bytes.Length is >= 8 and <= MaximumPngBytes && bytes[..8].SequenceEqual(PngSignature);

    private static bool IsAssetId(string? value) =>
        value is { Length: 35 } && value.StartsWith("wa_", StringComparison.Ordinal) &&
        value.AsSpan(3).IndexOfAnyExcept("0123456789abcdef".AsSpan()) < 0;

    private static ControllerResult<WorkspaceArtworkAsset> Failure(
        ControllerErrorCode code,
        string messageKey) =>
        ControllerResult<WorkspaceArtworkAsset>.Failure(ControllerError.Create(code, messageKey));
}
