using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;

namespace OrbitNavigator.Foundation.Profiles;

/// <summary>
/// Narrows a profile-storage capability to persistent records for one normal
/// profile. Privacy owns rule serialization, hydration, and write-through;
/// this adapter only enforces the composition boundary.
/// </summary>
public sealed class NormalProfilePersistentStorage : IProfileStorage
{
    private readonly IProfileStorage _inner;
    private readonly ProfileId _profileId;

    public NormalProfilePersistentStorage(IProfileStorage inner, ProfileId profileId)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (profileId.IsEmpty)
        {
            throw new ArgumentException("A non-empty profile identity is required.", nameof(profileId));
        }

        _profileId = profileId;
    }

    public ValueTask<ControllerResult<ProfileStorageEntry>> ReadAsync(
        ProfileStorageAddress address,
        CancellationToken cancellationToken = default) =>
        IsAllowed(address)
            ? _inner.ReadAsync(address, cancellationToken)
            : ValueTask.FromResult(ControllerResult<ProfileStorageEntry>.Failure(Denied()));

    public ValueTask<ControllerResult<ProfileStorageWriteReceipt>> WriteAsync(
        ProfileStorageWriteRequest request,
        CancellationToken cancellationToken = default) =>
        request is not null && IsAllowed(request.Address)
            ? _inner.WriteAsync(request, cancellationToken)
            : ValueTask.FromResult(ControllerResult<ProfileStorageWriteReceipt>.Failure(Denied()));

    public ValueTask<ControllerResult> DeleteAsync(
        ProfileStorageAddress address,
        ProfileStorageRevision? expectedRevision = null,
        CancellationToken cancellationToken = default) =>
        IsAllowed(address)
            ? _inner.DeleteAsync(address, expectedRevision, cancellationToken)
            : ValueTask.FromResult(ControllerResult.Failure(Denied()));

    private bool IsAllowed(ProfileStorageAddress? address) =>
        address is not null &&
        address.Context.ProfileId == _profileId &&
        address.Context.Mode == BrowserProfileMode.Normal &&
        address.Durability == ProfileStorageDurability.Persistent;

    private static ControllerError Denied() => ControllerError.Create(
        ControllerErrorCode.PolicyDenied,
        "error.profile_storage.persistent_normal_profile_required");
}
