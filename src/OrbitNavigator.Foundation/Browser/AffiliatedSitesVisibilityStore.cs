using System.Text.Json;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;

namespace OrbitNavigator.Foundation.Browser;

public sealed record AffiliatedSitesVisibilitySnapshot(
    ProfileId ProfileId,
    long Revision,
    bool IsHidden);

public sealed record SaveAffiliatedSitesVisibilityIntent(
    PrivacyContext Context,
    long ExpectedRevision,
    bool IsHidden);

public interface IAffiliatedSitesVisibilityStore
{
    ValueTask<ControllerResult<AffiliatedSitesVisibilitySnapshot>> LoadAsync(
        PrivacyContext context,
        CancellationToken cancellationToken = default);

    ValueTask<ControllerResult<AffiliatedSitesVisibilitySnapshot>> SaveAsync(
        SaveAffiliatedSitesVisibilityIntent intent,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Stores only the user's local section visibility choice. The approved site
/// catalog remains compiled and owner-reviewed; it is never loaded from this
/// store or discovered from browsing activity.
/// </summary>
public sealed class AffiliatedSitesVisibilityStore : IAffiliatedSitesVisibilityStore
{
    private const string StorageKey = "visibility";
    private const int SchemaVersion = 1;
    private readonly IProfileStorage _storage;
    private readonly ProfileStorageNamespace _namespace;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AffiliatedSitesVisibilityStore(IProfileStorage storage)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _namespace = ProfileStorageNamespace.Create("browser.affiliated-sites").Value!;
    }

    public async ValueTask<ControllerResult<AffiliatedSitesVisibilitySnapshot>> LoadAsync(
        PrivacyContext context,
        CancellationToken cancellationToken = default)
    {
        var address = Address(context);
        if (!address.IsSuccess)
        {
            return ControllerResult<AffiliatedSitesVisibilitySnapshot>.Failure(address.Error!);
        }

        var read = await _storage.ReadAsync(address.Value!, cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            return read.Error?.Code == ControllerErrorCode.NotFound
                ? ControllerResult<AffiliatedSitesVisibilitySnapshot>.Success(Default(context.ProfileId))
                : ControllerResult<AffiliatedSitesVisibilitySnapshot>.Failure(read.Error!);
        }

        try
        {
            var stored = JsonSerializer.Deserialize<StoredVisibility>(read.Value!.Payload.Span);
            return stored is { SchemaVersion: SchemaVersion, Revision: >= 0 }
                ? ControllerResult<AffiliatedSitesVisibilitySnapshot>.Success(new(
                    context.ProfileId,
                    stored.Revision,
                    stored.IsHidden))
                : Corrupt();
        }
        catch (JsonException)
        {
            return Corrupt();
        }
    }

    public async ValueTask<ControllerResult<AffiliatedSitesVisibilitySnapshot>> SaveAsync(
        SaveAffiliatedSitesVisibilityIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (intent.Context is not { IsStructurallyValid: true } || intent.ExpectedRevision < 0)
        {
            return Invalid();
        }
        if (intent.Context.IsPrivate)
        {
            return PolicyDenied();
        }

        var address = Address(intent.Context).Value!;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await _storage.ReadAsync(address, cancellationToken).ConfigureAwait(false);
            long currentRevision;
            ProfileStorageRevision? storageRevision;
            if (current.IsSuccess)
            {
                StoredVisibility? stored;
                try
                {
                    stored = JsonSerializer.Deserialize<StoredVisibility>(current.Value!.Payload.Span);
                }
                catch (JsonException)
                {
                    return Corrupt();
                }
                if (stored is not { SchemaVersion: SchemaVersion, Revision: >= 0 })
                {
                    return Corrupt();
                }
                currentRevision = stored.Revision;
                storageRevision = current.Value.Revision;
            }
            else if (current.Error?.Code == ControllerErrorCode.NotFound)
            {
                currentRevision = 0;
                storageRevision = null;
            }
            else
            {
                return ControllerResult<AffiliatedSitesVisibilitySnapshot>.Failure(current.Error!);
            }

            if (intent.ExpectedRevision != currentRevision || currentRevision == long.MaxValue)
            {
                return Conflict();
            }

            var nextRevision = currentRevision + 1;
            var payload = JsonSerializer.SerializeToUtf8Bytes(new StoredVisibility(
                SchemaVersion,
                nextRevision,
                intent.IsHidden));
            var request = ProfileStorageWriteRequest.Create(
                address,
                payload,
                storageRevision).Value!;
            var write = await _storage.WriteAsync(request, cancellationToken).ConfigureAwait(false);
            return write.IsSuccess
                ? ControllerResult<AffiliatedSitesVisibilitySnapshot>.Success(new(
                    intent.Context.ProfileId,
                    nextRevision,
                    intent.IsHidden))
                : ControllerResult<AffiliatedSitesVisibilitySnapshot>.Failure(write.Error!);
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
                "error.affiliated_sites_visibility.context_invalid"));
        }
        var normalContext = context.IsPrivate
            ? new PrivacyContext(context.ProfileId, context.SessionId, BrowserProfileMode.Normal)
            : context;
        return ProfileStorageAddress.Create(
            normalContext,
            _namespace,
            ProfileStorageKey.Create(StorageKey).Value,
            ProfileStorageDurability.Persistent);
    }

    private static AffiliatedSitesVisibilitySnapshot Default(ProfileId profileId) =>
        new(profileId, 0, false);

    private static ControllerResult<AffiliatedSitesVisibilitySnapshot> Invalid() => Failure(
        ControllerErrorCode.InvalidRequest,
        "error.affiliated_sites_visibility.invalid");

    private static ControllerResult<AffiliatedSitesVisibilitySnapshot> PolicyDenied() => Failure(
        ControllerErrorCode.PolicyDenied,
        "error.affiliated_sites_visibility.private_write_denied");

    private static ControllerResult<AffiliatedSitesVisibilitySnapshot> Conflict() => Failure(
        ControllerErrorCode.Conflict,
        "error.affiliated_sites_visibility.revision_conflict");

    private static ControllerResult<AffiliatedSitesVisibilitySnapshot> Corrupt() => Failure(
        ControllerErrorCode.IntegrityFailure,
        "error.affiliated_sites_visibility.storage_corrupt");

    private static ControllerResult<AffiliatedSitesVisibilitySnapshot> Failure(
        ControllerErrorCode code,
        string messageKey) =>
        ControllerResult<AffiliatedSitesVisibilitySnapshot>.Failure(
            ControllerError.Create(code, messageKey));

    private sealed record StoredVisibility(int SchemaVersion, long Revision, bool IsHidden);
}
