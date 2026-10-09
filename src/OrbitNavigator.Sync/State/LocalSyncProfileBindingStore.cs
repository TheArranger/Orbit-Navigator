using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Sync;

namespace OrbitNavigator.Sync.State;

public enum LocalSyncProfileBindingKind
{
    AccountOrigin = 0,
    JoinedAccount = 1,
}

/// <summary>
/// Immutable local-to-account sync identity mapping. Local browser storage keeps
/// its original ProfileId; only encrypted relay routing uses SyncProfileId.
/// </summary>
public sealed record LocalSyncProfileBinding(
    ProfileId LocalProfileId,
    ProfileId SyncProfileId,
    LocalSyncProfileBindingKind Kind,
    ProfileStorageRevision StorageRevision);

public sealed class LocalSyncProfileBindingStore
{
    private const uint Magic = 0x42534E4F; // ONSB
    private const int Version = 1;
    private const int PayloadSize = 4 + 4 + 16 + 16 + 1;
    private static readonly ProfileStorageNamespace StorageNamespace =
        ProfileStorageNamespace.Create("sync.identity").Value!;
    private static readonly ProfileStorageKey StorageKey =
        ProfileStorageKey.Create("account-profile-binding").Value!;

    private readonly IProfileStorage _storage;
    // The host owns one storage adapter. Serialize first binds across store
    // instances using that adapter, since a missing revision is not create-only
    // in IProfileStorage. This is not a cross-process compare-and-swap primitive.
    private static readonly ConditionalWeakTable<IProfileStorage, SemaphoreSlim> StorageGates = new();
    private readonly SemaphoreSlim _gate;

    public LocalSyncProfileBindingStore(IProfileStorage storage)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _gate = StorageGates.GetValue(storage, _ => new(1, 1));
    }

    public ValueTask<ControllerResult<LocalSyncProfileBinding>> LoadAsync(
        BrowsingContext browsing,
        SyncOperationId operationId,
        CancellationToken cancellationToken = default) =>
        SyncOperationGate.ExecuteAsync(
            browsing,
            operationId,
            context => LoadAuthorizedAsync(context, cancellationToken));

    public ValueTask<ControllerResult<LocalSyncProfileBinding>> BindAsync(
        BrowsingContext browsing,
        SyncOperationId operationId,
        ProfileId syncProfileId,
        LocalSyncProfileBindingKind kind,
        CancellationToken cancellationToken = default) =>
        SyncOperationGate.ExecuteAsync(
            browsing,
            operationId,
            context => BindAuthorizedAsync(context, syncProfileId, kind, cancellationToken));

    private async ValueTask<ControllerResult<LocalSyncProfileBinding>> LoadAuthorizedAsync(
        SyncOperationContext context,
        CancellationToken cancellationToken)
    {
        var address = Address(context);
        if (!address.IsSuccess)
            return ControllerResult<LocalSyncProfileBinding>.Failure(address.Error!);
        var read = await _storage.ReadAsync(address.Value!, cancellationToken).ConfigureAwait(false);
        return read.IsSuccess
            ? Decode(read.Value!, context.Browsing.Privacy.ProfileId)
            : ControllerResult<LocalSyncProfileBinding>.Failure(read.Error!);
    }

    private async ValueTask<ControllerResult<LocalSyncProfileBinding>> BindAuthorizedAsync(
        SyncOperationContext context,
        ProfileId syncProfileId,
        LocalSyncProfileBindingKind kind,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await BindUnderGateAsync(context, syncProfileId, kind, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<ControllerResult<LocalSyncProfileBinding>> BindUnderGateAsync(
        SyncOperationContext context,
        ProfileId syncProfileId,
        LocalSyncProfileBindingKind kind,
        CancellationToken cancellationToken)
    {
        var localProfileId = context.Browsing.Privacy.ProfileId;
        if (syncProfileId.IsEmpty || !Enum.IsDefined(kind) ||
            kind == LocalSyncProfileBindingKind.AccountOrigin && syncProfileId != localProfileId)
        {
            return Invalid();
        }

        var address = Address(context);
        if (!address.IsSuccess)
            return ControllerResult<LocalSyncProfileBinding>.Failure(address.Error!);
        var read = await _storage.ReadAsync(address.Value!, cancellationToken).ConfigureAwait(false);
        if (read.IsSuccess)
        {
            var existing = Decode(read.Value!, localProfileId);
            if (!existing.IsSuccess)
                return existing;
            return existing.Value!.SyncProfileId == syncProfileId && existing.Value.Kind == kind
                ? existing
                : Conflict();
        }
        if (read.Error!.Code != ControllerErrorCode.NotFound)
            return ControllerResult<LocalSyncProfileBinding>.Failure(read.Error);

        var payload = Encode(localProfileId, syncProfileId, kind);
        try
        {
            var request = ProfileStorageWriteRequest.Create(address.Value!, payload);
            if (!request.IsSuccess)
                return ControllerResult<LocalSyncProfileBinding>.Failure(request.Error!);
            var written = await _storage.WriteAsync(request.Value!, cancellationToken).ConfigureAwait(false);
            if (!written.IsSuccess)
                return ControllerResult<LocalSyncProfileBinding>.Failure(written.Error!);
            return written.Value!.Revision.IsEmpty
                ? Integrity()
                : ControllerResult<LocalSyncProfileBinding>.Success(new(
                    localProfileId,
                    syncProfileId,
                    kind,
                    written.Value.Revision));
        }
        finally
        {
            Array.Clear(payload);
        }
    }

    private static byte[] Encode(
        ProfileId localProfileId,
        ProfileId syncProfileId,
        LocalSyncProfileBindingKind kind)
    {
        var payload = new byte[PayloadSize];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), Magic);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4, 4), Version);
        localProfileId.Value.TryWriteBytes(payload.AsSpan(8, 16));
        syncProfileId.Value.TryWriteBytes(payload.AsSpan(24, 16));
        payload[40] = (byte)kind;
        return payload;
    }

    private static ControllerResult<LocalSyncProfileBinding> Decode(
        ProfileStorageEntry entry,
        ProfileId expectedLocalProfileId)
    {
        var payload = entry.Payload.Span;
        if (payload.Length != PayloadSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(payload[..4]) != Magic ||
            BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4, 4)) != Version)
            return Integrity();
        var local = new ProfileId(new Guid(payload.Slice(8, 16)));
        var sync = new ProfileId(new Guid(payload.Slice(24, 16)));
        var kind = (LocalSyncProfileBindingKind)payload[40];
        if (local != expectedLocalProfileId || sync.IsEmpty || !Enum.IsDefined(kind) ||
            kind == LocalSyncProfileBindingKind.AccountOrigin && sync != local ||
            entry.Revision.IsEmpty)
            return Integrity();
        return ControllerResult<LocalSyncProfileBinding>.Success(new(local, sync, kind, entry.Revision));
    }

    private static ControllerResult<ProfileStorageAddress> Address(SyncOperationContext context) =>
        ProfileStorageAddress.Create(
            context.Browsing.Privacy,
            StorageNamespace,
            StorageKey,
            ProfileStorageDurability.Persistent);

    private static ControllerResult<LocalSyncProfileBinding> Invalid() =>
        ControllerResult<LocalSyncProfileBinding>.Failure(ControllerError.Create(
            ControllerErrorCode.InvalidRequest, "sync.identity.binding-invalid"));

    private static ControllerResult<LocalSyncProfileBinding> Conflict() =>
        ControllerResult<LocalSyncProfileBinding>.Failure(ControllerError.Create(
            ControllerErrorCode.Conflict, "sync.identity.binding-conflict"));

    private static ControllerResult<LocalSyncProfileBinding> Integrity() =>
        ControllerResult<LocalSyncProfileBinding>.Failure(ControllerError.Create(
            ControllerErrorCode.IntegrityFailure, "sync.identity.binding-corrupt"));
}
