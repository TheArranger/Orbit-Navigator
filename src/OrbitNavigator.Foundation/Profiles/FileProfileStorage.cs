using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;

namespace OrbitNavigator.Foundation.Profiles;

public sealed class FileProfileStorage : IProfileStorage, IProfileStorageNamespaceMaintenance
{
    private const int RevisionSize = 16;
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileProfileStorage(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Path.IsPathFullyQualified(root))
        {
            throw new ArgumentException("Profile storage root must be absolute.", nameof(root));
        }

        _root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
    }

    public async ValueTask<ControllerResult<ProfileStorageEntry>> ReadAsync(
        ProfileStorageAddress address,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        var path = Resolve(address);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
            {
                return ControllerResult<ProfileStorageEntry>.Failure(ControllerError.Create(
                    ControllerErrorCode.NotFound,
                    "error.profile_storage.not_found"));
            }

            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (bytes.Length < RevisionSize)
            {
                return ControllerResult<ProfileStorageEntry>.Failure(ControllerError.Create(
                    ControllerErrorCode.IntegrityFailure,
                    "error.profile_storage.corrupt"));
            }

            var revision = new ProfileStorageRevision(new Guid(bytes.AsSpan(0, RevisionSize)));
            return ProfileStorageEntry.Create(revision, bytes.AsSpan(RevisionSize));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ControllerResult<ProfileStorageWriteReceipt>> WriteAsync(
        ProfileStorageWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var path = Resolve(request.Address);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadRevisionWithoutLockAsync(path, cancellationToken).ConfigureAwait(false);
            if (request.ExpectedRevision is { } expected && current != expected)
            {
                return ControllerResult<ProfileStorageWriteReceipt>.Failure(ControllerError.Create(
                    ControllerErrorCode.Conflict,
                    "error.profile_storage.revision_conflict"));
            }

            var revision = new ProfileStorageRevision(Guid.NewGuid());
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            var bytes = new byte[RevisionSize + request.Payload.Length];
            revision.Value.TryWriteBytes(bytes.AsSpan(0, RevisionSize));
            request.Payload.Span.CopyTo(bytes.AsSpan(RevisionSize));
            try
            {
                await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }

            return ControllerResult<ProfileStorageWriteReceipt>.Success(
                new ProfileStorageWriteReceipt(revision));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ControllerResult> DeleteAsync(
        ProfileStorageAddress address,
        ProfileStorageRevision? expectedRevision = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        var path = Resolve(address);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
            {
                return ControllerResult.Success();
            }

            var current = await ReadRevisionWithoutLockAsync(path, cancellationToken).ConfigureAwait(false);
            if (expectedRevision is { } expected && current != expected)
            {
                return ControllerResult.Failure(ControllerError.Create(
                    ControllerErrorCode.Conflict,
                    "error.profile_storage.revision_conflict"));
            }

            File.Delete(path);
            return ControllerResult.Success();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ControllerResult<ProfileStorageNamespaceCleanupReceipt>> DeleteNamespaceAsync(
        PrivacyContext context,
        ProfileStorageNamespace storageNamespace,
        ProfileStorageDurability durability,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(storageNamespace);
        if (!context.IsStructurallyValid || !Enum.IsDefined(durability) ||
            (context.IsPrivate && durability == ProfileStorageDurability.Persistent))
        {
            return ControllerResult<ProfileStorageNamespaceCleanupReceipt>.Failure(
                ControllerError.Create(
                    ControllerErrorCode.PolicyDenied,
                    "error.profile_storage.namespace_cleanup_denied"));
        }

        var mode = context.IsPrivate ? "private" : "normal";
        var session = context.IsPrivate || durability == ProfileStorageDurability.Session
            ? context.SessionId.Value.ToString("N")
            : "persistent";
        var directory = Path.GetFullPath(Path.Combine(
            _root,
            context.ProfileId.Value.ToString("N"),
            mode,
            session,
            storageNamespace.Value));
        EnsureWithinRoot(directory);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(directory))
            {
                return ControllerResult<ProfileStorageNamespaceCleanupReceipt>.Success(
                    new(storageNamespace, 0));
            }
            var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToArray();
            Directory.Delete(directory, recursive: true);
            return ControllerResult<ProfileStorageNamespaceCleanupReceipt>.Success(
                new(storageNamespace, files.Length));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ControllerResult<ProfileStorageNamespaceCleanupReceipt>.Failure(
                ControllerError.Create(
                    ControllerErrorCode.Unavailable,
                    "error.profile_storage.namespace_cleanup_failed"));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<ProfileStorageRevision?> ReadRevisionWithoutLockAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var buffer = new byte[RevisionSize];
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            RevisionSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        return read == RevisionSize
            ? new ProfileStorageRevision(new Guid(buffer))
            : null;
    }

    private string Resolve(ProfileStorageAddress address)
    {
        var privacy = address.Context;
        var mode = privacy.IsPrivate ? "private" : "normal";
        var session = privacy.IsPrivate || address.Durability == ProfileStorageDurability.Session
            ? privacy.SessionId.Value.ToString("N")
            : "persistent";
        var keyHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(address.Key.Value)));
        var path = Path.GetFullPath(Path.Combine(
            _root,
            privacy.ProfileId.Value.ToString("N"),
            mode,
            session,
            address.Namespace.Value,
            keyHash + ".bin"));
        EnsureWithinRoot(path);
        return path;
    }

    private void EnsureWithinRoot(string path)
    {
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Resolved profile path escaped its configured root.");
        }
    }
}
