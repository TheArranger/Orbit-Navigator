using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Privacy;

namespace OrbitNavigator.Contracts.Sync;

public enum SyncKeyWrapMethod
{
    RecoveryCode = 0,
    TrustedDevice = 1,
}

public enum SyncKdfAlgorithm
{
    Pbkdf2Sha256 = 0,
    Argon2id = 1,
}

public sealed record SyncKdfParameters(
    SyncKdfAlgorithm Algorithm,
    ReadOnlyMemory<byte> Salt,
    int Iterations,
    int MemoryKiB,
    int Parallelism,
    int DerivedKeySizeBytes);

public sealed record WrappedSyncKeyset(
    SyncKeysetId KeysetId,
    long Generation,
    SyncKeyWrapMethod WrapMethod,
    SyncKdfParameters Kdf,
    ReadOnlyMemory<byte> Nonce,
    ReadOnlyMemory<byte> WrappedKeyCiphertext,
    ReadOnlyMemory<byte> AuthenticationTag);

public readonly record struct EmailVerificationProofHandle(Guid Value)
{
    public bool IsDefined => Value != Guid.Empty;
}

/// <summary>
/// Privacy-owned, process-local recovery-code storage. The shared boundary exposes
/// only an opaque lease identifier; raw recovery-code characters are never a DTO.
/// Implementations must zero their backing memory when disposed.
/// </summary>
public abstract class SensitiveRecoveryCodeBuffer : IDisposable
{
    protected SensitiveRecoveryCodeBuffer(RecoveryCodeLeaseId leaseId)
    {
        if (!leaseId.IsDefined)
        {
            throw new ArgumentException("A recovery-code lease identifier is required.", nameof(leaseId));
        }

        LeaseId = leaseId;
    }

    public RecoveryCodeLeaseId LeaseId { get; }

    public abstract bool IsDisposed { get; }

    public abstract void Dispose();
}

public sealed record EmailAccountRestorationRequest(EmailVerificationProofHandle VerificationProof);

/// <summary>Email restoration restores account access only and never returns key material.</summary>
public sealed record EmailAccountRestorationReceipt(
    string MaskedEmailAddress,
    DateTimeOffset RestoredAtUtc,
    OpaqueAuthHandle Authorization);

public interface IEmailAccountRestoration
{
    ValueTask<ControllerResult<EmailAccountRestorationReceipt>> RestoreAccountAsync(
        SyncOperationContext context,
        EmailAccountRestorationRequest request,
        CancellationToken cancellationToken);
}

public sealed record RecoveryCodeKeyRestorationRequest(
    SyncRecoveryAttemptId AttemptId,
    SyncKeysetId KeysetId,
    RecoveryCodeLeaseId SensitiveCodeLease,
    WrappedSyncKeyset WrappedKeyset,
    SensitiveActionAuthorizationToken SensitiveAuthorization);

public sealed record TrustedDeviceKeyRestorationRequest(
    SyncRecoveryAttemptId AttemptId,
    SyncKeysetId KeysetId,
    DeviceId TrustedDeviceId,
    WrappedSyncKeyset WrappedKeyset,
    ReadOnlyMemory<byte> DeviceAttestation,
    SensitiveActionAuthorizationToken SensitiveAuthorization);

public sealed record SyncKeyRestorationReceipt(
    SyncRecoveryAttemptId AttemptId,
    SyncKeysetId KeysetId,
    long Generation,
    SyncKeyMaterialHandle KeyMaterial);

public interface ISyncKeyRestoration
{
    ValueTask<ControllerResult<SyncKeyRestorationReceipt>> RestoreWithRecoveryCodeAsync(
        SyncOperationContext context,
        OpaqueAuthHandle authorization,
        RecoveryCodeKeyRestorationRequest request,
        CancellationToken cancellationToken);

    ValueTask<ControllerResult<SyncKeyRestorationReceipt>> RestoreFromTrustedDeviceAsync(
        SyncOperationContext context,
        OpaqueAuthHandle authorization,
        TrustedDeviceKeyRestorationRequest request,
        CancellationToken cancellationToken);
}

public sealed record EncryptedSyncResetRequest(
    SyncKeysetId PreviousKeysetId,
    long PreviousGeneration,
    SyncOperationId OperationId,
    SensitiveActionAuthorizationToken SensitiveAuthorization);

public sealed record EncryptedSyncResetReceipt(
    SyncOperationId OperationId,
    SyncKeysetId PreviousKeysetId,
    SyncKeysetId NewKeysetId,
    ClientFence Fence,
    DateTimeOffset ResetAtUtc);

public interface IEncryptedSyncReset
{
    ValueTask<ControllerResult<EncryptedSyncResetReceipt>> ResetAsync(
        SyncOperationContext context,
        OpaqueAuthHandle authorization,
        EncryptedSyncResetRequest request,
        CancellationToken cancellationToken);
}

public enum LocalDataCategory
{
    Cache = 0,
    Cookies = 1,
    SiteData = 2,
    History = 3,
    Settings = 4,
    OpenTabs = 5,
    DownloadRecords = 6,
    ClipboardShelf = 7,
    FormEntriesAutofill = 8,
    SiteSessionsAuthTokens = 9,
    SavedPasswords = 10,
    PermissionRules = 11,
    SiteProtectionExceptions = 12,
    ReadingPreferences = 13,
}

public sealed record DataDeletionSelection(
    IReadOnlySet<LocalDataCategory> LocalCategories,
    IReadOnlySet<SyncDataCategory> SyncedCategories);

public enum DownloadedFilesDisposition
{
    Preserved = 0,
}

public sealed record DataDeletionRequest(
    SyncOperationId OperationId,
    DataDeletionSelection Selection,
    OpaqueAuthHandle? Authorization,
    ClientFence? Fence);

public sealed record DataDeletionStatusRequest(
    SyncOperationId OperationId,
    OpaqueAuthHandle Authorization,
    ClientFence Fence);

public enum DataDeletionStageKind
{
    Validate = 0,
    Authorize = 1,
    DeleteLocal = 2,
    UploadTombstones = 3,
    UploadPurgeCommands = 4,
    AwaitPropagation = 5,
    Complete = 6,
}

public enum DataDeletionStageState
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
}

public sealed record DataDeletionStageStatus(
    DataDeletionStageKind Stage,
    DataDeletionStageState State,
    DateTimeOffset? UpdatedAtUtc,
    ControllerError? Error);

public sealed record DataDeletionPreview(
    SyncOperationId OperationId,
    IReadOnlySet<LocalDataCategory> LocalCategories,
    IReadOnlySet<SyncDataCategory> SyncedCategories,
    IReadOnlyList<DataDeletionStageStatus> Stages,
    ClientFence? Fence,
    DownloadedFilesDisposition DownloadedFiles);

public sealed record DataDeletionProgress(
    SyncOperationId OperationId,
    ClientFence? Fence,
    IReadOnlyList<DataDeletionStageStatus> Stages,
    bool PropagationComplete,
    DownloadedFilesDisposition DownloadedFiles);

public interface ISyncedDataDeletion
{
    ValueTask<ControllerResult<DataDeletionPreview>> PreviewAsync(
        SyncOperationContext context,
        DataDeletionRequest request,
        CancellationToken cancellationToken);

    ValueTask<ControllerResult<DataDeletionProgress>> DeleteAsync(
        SyncOperationContext context,
        DataDeletionRequest request,
        CancellationToken cancellationToken);

    ValueTask<ControllerResult<DataDeletionProgress>> GetStatusAsync(
        SyncOperationContext context,
        DataDeletionStatusRequest request,
        CancellationToken cancellationToken);
}

public static class DataDeletionRules
{
    public static bool PreservesDownloadedFiles => true;

    public static SyncValidationResult ValidateSelection(DataDeletionSelection? selection)
    {
        var issues = new List<SyncValidationIssue>();
        if (selection is null)
        {
            issues.Add(new("deletion.selection", "sync.deletion.selection_required"));
            return new(issues);
        }

        foreach (var category in selection.SyncedCategories)
        {
            if (!SyncAllowlist.IsAllowed(category))
                issues.Add(new("deletion.synced_category", "sync.deletion.synced_category_not_allowed"));

            var local = category switch
            {
                SyncDataCategory.History => LocalDataCategory.History,
                SyncDataCategory.Settings => LocalDataCategory.Settings,
                SyncDataCategory.OpenTabs => LocalDataCategory.OpenTabs,
                _ => (LocalDataCategory)(-1),
            };

            if (!selection.LocalCategories.Contains(local))
                issues.Add(new("deletion.local_counterpart", "sync.deletion.local_counterpart_required"));
        }

        return new(issues);
    }

    public static SyncValidationResult ValidateRequest(
        SyncOperationContext context,
        DataDeletionRequest request)
    {
        var issues = new List<SyncValidationIssue>(ValidateSelection(request.Selection).Issues);
        if (request.OperationId != context.OperationId || !request.OperationId.IsDefined)
            issues.Add(new("deletion.operation", "sync.deletion.operation-mismatch"));

        if (request.Selection.SyncedCategories.Count > 0)
        {
            if (request.Authorization is null || request.Authorization.Value.IsEmpty)
                issues.Add(new("deletion.authorization", "sync.deletion.authorization-required"));
            if (request.Fence is not { IsDefined: true })
                issues.Add(new("deletion.fence", "sync.deletion.fence-required"));
        }

        return new(issues);
    }

    public static SyncValidationResult ValidateReset(
        EncryptedSyncResetRequest request,
        EncryptedSyncResetReceipt receipt)
    {
        var issues = new List<SyncValidationIssue>();
        if (request.OperationId != receipt.OperationId)
            issues.Add(new("reset.operation", "sync.reset.operation_mismatch"));
        if (request.PreviousKeysetId != receipt.PreviousKeysetId)
            issues.Add(new("reset.previous_keyset", "sync.reset.previous_keyset_mismatch"));
        if (receipt.NewKeysetId == receipt.PreviousKeysetId || !receipt.NewKeysetId.IsDefined)
            issues.Add(new("reset.new_keyset", "sync.reset.new_keyset_required"));
        if (receipt.Fence.ClientGeneration <= request.PreviousGeneration || !receipt.Fence.IsDefined)
            issues.Add(new("reset.fence", "sync.reset.stale_fence"));
        if (request.SensitiveAuthorization.Purpose != SensitiveActionKind.ResetEncryptedSync)
            issues.Add(new("reset.authorization", "sync.reset.authorization-purpose"));
        return new(issues);
    }
}
