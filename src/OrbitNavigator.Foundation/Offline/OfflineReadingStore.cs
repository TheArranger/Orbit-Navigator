using System.Text.Json;

using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;

namespace OrbitNavigator.Foundation.Offline;

public readonly record struct OfflineReadingItemId(Guid Value)
{
    public bool IsEmpty => Value == Guid.Empty;
}

public sealed record OfflineReadingItem(
    OfflineReadingItemId ItemId,
    string Title,
    Uri SourceAddress,
    DateTimeOffset SavedAtUtc,
    long SizeBytes);

public sealed record OfflineReadingCatalogSnapshot(
    ProfileId ProfileId,
    long Revision,
    IReadOnlyList<OfflineReadingItem> Items);

public sealed record OfflineReadingContent(
    OfflineReadingItem Item,
    ReadOnlyMemory<byte> PngBytes);

public sealed record SaveOfflineReadingSnapshotRequest(
    PrivacyContext Context,
    long ExpectedRevision,
    string Title,
    Uri SourceAddress,
    ReadOnlyMemory<byte> PngBytes);

public sealed record DeleteOfflineReadingSnapshotRequest(
    PrivacyContext Context,
    long ExpectedRevision,
    OfflineReadingItemId ItemId);

public interface IOfflineReadingStore
{
    ValueTask<ControllerResult<OfflineReadingCatalogSnapshot>> LoadCatalogAsync(
        PrivacyContext context,
        CancellationToken cancellationToken = default);

    ValueTask<ControllerResult<OfflineReadingCatalogSnapshot>> SaveAsync(
        SaveOfflineReadingSnapshotRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<ControllerResult<OfflineReadingContent>> OpenAsync(
        PrivacyContext context,
        OfflineReadingItemId itemId,
        CancellationToken cancellationToken = default);

    ValueTask<ControllerResult<OfflineReadingCatalogSnapshot>> DeleteAsync(
        DeleteOfflineReadingSnapshotRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Stores explicit user-requested viewport snapshots. Content is PNG only and
/// is opened by a native image viewer, so saved copies cannot execute script,
/// navigate, issue network requests, or join browser sync.
/// </summary>
public sealed class OfflineReadingStore : IOfflineReadingStore
{
    public const int MaximumItems = 100;
    public const int MaximumTitleLength = 300;
    public const int MaximumSnapshotBytes = 25 * 1024 * 1024;

    private const int SchemaVersion = 1;
    private const string CatalogFileName = "catalog.json";
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private readonly string _root;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public OfflineReadingStore(string root, IClock clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Path.IsPathFullyQualified(root))
        {
            throw new ArgumentException("Offline-reading storage root must be absolute.", nameof(root));
        }

        _root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask<ControllerResult<OfflineReadingCatalogSnapshot>> LoadCatalogAsync(
        PrivacyContext context,
        CancellationToken cancellationToken = default)
    {
        var policy = ValidateNormalContext(context);
        if (policy is not null)
        {
            return ControllerResult<OfflineReadingCatalogSnapshot>.Failure(policy);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadCatalogWithoutLockAsync(context, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ControllerResult<OfflineReadingCatalogSnapshot>> SaveAsync(
        SaveOfflineReadingSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var policy = ValidateNormalContext(request.Context);
        if (policy is not null)
        {
            return ControllerResult<OfflineReadingCatalogSnapshot>.Failure(policy);
        }
        if (request.ExpectedRevision < 0 ||
            !IsWebAddress(request.SourceAddress) ||
            !IsPng(request.PngBytes.Span) ||
            request.PngBytes.Length > MaximumSnapshotBytes)
        {
            return Invalid<OfflineReadingCatalogSnapshot>();
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await LoadCatalogWithoutLockAsync(request.Context, cancellationToken)
                .ConfigureAwait(false);
            if (!loaded.IsSuccess)
            {
                return loaded;
            }
            var current = loaded.Value!;
            if (current.Revision != request.ExpectedRevision || current.Revision == long.MaxValue)
            {
                return Conflict<OfflineReadingCatalogSnapshot>();
            }
            if (current.Items.Count >= MaximumItems)
            {
                return ControllerResult<OfflineReadingCatalogSnapshot>.Failure(ControllerError.Create(
                    ControllerErrorCode.InvalidRequest,
                    "error.offline_reading.capacity_reached"));
            }

            var item = new OfflineReadingItem(
                new OfflineReadingItemId(Guid.NewGuid()),
                NormalizeTitle(request.Title, request.SourceAddress),
                new Uri(request.SourceAddress.AbsoluteUri),
                _clock.UtcNow,
                request.PngBytes.Length);
            var next = new OfflineReadingCatalogSnapshot(
                request.Context.ProfileId,
                current.Revision + 1,
                current.Items.Append(item).ToArray());
            var profileRoot = ProfileRoot(request.Context.ProfileId);
            var itemsRoot = Path.Combine(profileRoot, "items");
            Directory.CreateDirectory(itemsRoot);
            var contentPath = ItemPath(request.Context.ProfileId, item.ItemId);
            var contentTemp = contentPath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllBytesAsync(contentTemp, request.PngBytes.ToArray(), cancellationToken)
                    .ConfigureAwait(false);
                File.Move(contentTemp, contentPath);
                var catalogWrite = await WriteCatalogWithoutLockAsync(next, cancellationToken)
                    .ConfigureAwait(false);
                if (!catalogWrite.IsSuccess)
                {
                    File.Delete(contentPath);
                    return ControllerResult<OfflineReadingCatalogSnapshot>.Failure(catalogWrite.Error!);
                }
                return ControllerResult<OfflineReadingCatalogSnapshot>.Success(next);
            }
            catch (IOException)
            {
                if (File.Exists(contentPath)) File.Delete(contentPath);
                return Unavailable<OfflineReadingCatalogSnapshot>();
            }
            catch (UnauthorizedAccessException)
            {
                if (File.Exists(contentPath)) File.Delete(contentPath);
                return Unavailable<OfflineReadingCatalogSnapshot>();
            }
            finally
            {
                if (File.Exists(contentTemp)) File.Delete(contentTemp);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ControllerResult<OfflineReadingContent>> OpenAsync(
        PrivacyContext context,
        OfflineReadingItemId itemId,
        CancellationToken cancellationToken = default)
    {
        var policy = ValidateNormalContext(context);
        if (policy is not null)
        {
            return ControllerResult<OfflineReadingContent>.Failure(policy);
        }
        if (itemId.IsEmpty)
        {
            return Invalid<OfflineReadingContent>();
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await LoadCatalogWithoutLockAsync(context, cancellationToken).ConfigureAwait(false);
            if (!loaded.IsSuccess)
            {
                return ControllerResult<OfflineReadingContent>.Failure(loaded.Error!);
            }
            var item = loaded.Value!.Items.SingleOrDefault(candidate => candidate.ItemId == itemId);
            if (item is null)
            {
                return NotFound<OfflineReadingContent>();
            }
            try
            {
                var bytes = await File.ReadAllBytesAsync(ItemPath(context.ProfileId, itemId), cancellationToken)
                    .ConfigureAwait(false);
                return bytes.LongLength == item.SizeBytes && IsPng(bytes)
                    ? ControllerResult<OfflineReadingContent>.Success(new(item, bytes))
                    : Corrupt<OfflineReadingContent>();
            }
            catch (FileNotFoundException)
            {
                return Corrupt<OfflineReadingContent>();
            }
            catch (IOException)
            {
                return Unavailable<OfflineReadingContent>();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ControllerResult<OfflineReadingCatalogSnapshot>> DeleteAsync(
        DeleteOfflineReadingSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var policy = ValidateNormalContext(request.Context);
        if (policy is not null)
        {
            return ControllerResult<OfflineReadingCatalogSnapshot>.Failure(policy);
        }
        if (request.ExpectedRevision < 0 || request.ItemId.IsEmpty)
        {
            return Invalid<OfflineReadingCatalogSnapshot>();
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await LoadCatalogWithoutLockAsync(request.Context, cancellationToken)
                .ConfigureAwait(false);
            if (!loaded.IsSuccess)
            {
                return loaded;
            }
            var current = loaded.Value!;
            if (current.Revision != request.ExpectedRevision || current.Revision == long.MaxValue)
            {
                return Conflict<OfflineReadingCatalogSnapshot>();
            }
            var item = current.Items.SingleOrDefault(candidate => candidate.ItemId == request.ItemId);
            if (item is null)
            {
                return NotFound<OfflineReadingCatalogSnapshot>();
            }

            var contentPath = ItemPath(request.Context.ProfileId, request.ItemId);
            var retiredPath = contentPath + $".{Guid.NewGuid():N}.delete";
            try
            {
                if (!File.Exists(contentPath))
                {
                    return Corrupt<OfflineReadingCatalogSnapshot>();
                }
                File.Move(contentPath, retiredPath);
                var next = new OfflineReadingCatalogSnapshot(
                    request.Context.ProfileId,
                    current.Revision + 1,
                    current.Items.Where(candidate => candidate.ItemId != request.ItemId).ToArray());
                var catalogWrite = await WriteCatalogWithoutLockAsync(next, cancellationToken)
                    .ConfigureAwait(false);
                if (!catalogWrite.IsSuccess)
                {
                    File.Move(retiredPath, contentPath);
                    return ControllerResult<OfflineReadingCatalogSnapshot>.Failure(catalogWrite.Error!);
                }
                File.Delete(retiredPath);
                return ControllerResult<OfflineReadingCatalogSnapshot>.Success(next);
            }
            catch (IOException)
            {
                if (File.Exists(retiredPath) && !File.Exists(contentPath))
                {
                    File.Move(retiredPath, contentPath);
                }
                return Unavailable<OfflineReadingCatalogSnapshot>();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<ControllerResult<OfflineReadingCatalogSnapshot>> LoadCatalogWithoutLockAsync(
        PrivacyContext context,
        CancellationToken cancellationToken)
    {
        var path = CatalogPath(context.ProfileId);
        if (!File.Exists(path))
        {
            return ControllerResult<OfflineReadingCatalogSnapshot>.Success(new(
                context.ProfileId,
                0,
                []));
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var stored = await JsonSerializer.DeserializeAsync<StoredCatalog>(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (stored is not { SchemaVersion: SchemaVersion, Revision: >= 0 } ||
                stored.Items is null || stored.Items.Count > MaximumItems)
            {
                return Corrupt<OfflineReadingCatalogSnapshot>();
            }
            var items = new List<OfflineReadingItem>(stored.Items.Count);
            var ids = new HashSet<Guid>();
            foreach (var candidate in stored.Items)
            {
                if (candidate.ItemId == Guid.Empty || !ids.Add(candidate.ItemId) ||
                    !Uri.TryCreate(candidate.SourceAddress, UriKind.Absolute, out var address) ||
                    !IsWebAddress(address) || candidate.SavedAtUtc == default ||
                    candidate.SizeBytes is < 8 or > MaximumSnapshotBytes ||
                    string.IsNullOrWhiteSpace(candidate.Title) || candidate.Title.Length > MaximumTitleLength)
                {
                    return Corrupt<OfflineReadingCatalogSnapshot>();
                }
                items.Add(new(
                    new OfflineReadingItemId(candidate.ItemId),
                    candidate.Title,
                    address,
                    candidate.SavedAtUtc,
                    candidate.SizeBytes));
            }
            return ControllerResult<OfflineReadingCatalogSnapshot>.Success(new(
                context.ProfileId,
                stored.Revision,
                items));
        }
        catch (JsonException)
        {
            return Corrupt<OfflineReadingCatalogSnapshot>();
        }
        catch (IOException)
        {
            return Unavailable<OfflineReadingCatalogSnapshot>();
        }
    }

    private async ValueTask<ControllerResult> WriteCatalogWithoutLockAsync(
        OfflineReadingCatalogSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var path = CatalogPath(snapshot.ProfileId);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var stored = new StoredCatalog(
                SchemaVersion,
                snapshot.Revision,
                snapshot.Items.Select(item => new StoredItem(
                    item.ItemId.Value,
                    item.Title,
                    item.SourceAddress.AbsoluteUri,
                    item.SavedAtUtc,
                    item.SizeBytes)).ToArray());
            await using (var stream = new FileStream(
                temp,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, stored, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temp, path, overwrite: true);
            return ControllerResult.Success();
        }
        catch (IOException)
        {
            return ControllerResult.Failure(ControllerError.Create(
                ControllerErrorCode.Unavailable,
                "error.offline_reading.storage_unavailable"));
        }
        catch (UnauthorizedAccessException)
        {
            return ControllerResult.Failure(ControllerError.Create(
                ControllerErrorCode.Unavailable,
                "error.offline_reading.storage_unavailable"));
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private ControllerError? ValidateNormalContext(PrivacyContext context)
    {
        if (context is not { IsStructurallyValid: true })
        {
            return ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "error.offline_reading.context_invalid");
        }
        return context.IsPrivate
            ? ControllerError.Create(
                ControllerErrorCode.PolicyDenied,
                "error.offline_reading.private_unavailable")
            : null;
    }

    private string ProfileRoot(ProfileId profileId) => ResolveWithinRoot(
        Path.Combine(_root, profileId.Value.ToString("N")));

    private string CatalogPath(ProfileId profileId) => ResolveWithinRoot(
        Path.Combine(ProfileRoot(profileId), CatalogFileName));

    private string ItemPath(ProfileId profileId, OfflineReadingItemId itemId) => ResolveWithinRoot(
        Path.Combine(ProfileRoot(profileId), "items", itemId.Value.ToString("N") + ".png"));

    private string ResolveWithinRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Resolved offline-reading path escaped its configured root.");
        }
        return fullPath;
    }

    private static string NormalizeTitle(string title, Uri sourceAddress)
    {
        var normalized = string.IsNullOrWhiteSpace(title) ? sourceAddress.Host : title.Trim();
        return normalized.Length <= MaximumTitleLength ? normalized : normalized[..MaximumTitleLength];
    }

    private static bool IsWebAddress(Uri? address) =>
        address is { IsAbsoluteUri: true } &&
        address.Scheme is "http" or "https" &&
        string.IsNullOrEmpty(address.UserInfo) &&
        !string.IsNullOrWhiteSpace(address.IdnHost);

    private static bool IsPng(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= PngSignature.Length &&
        bytes.Length <= MaximumSnapshotBytes &&
        bytes[..PngSignature.Length].SequenceEqual(PngSignature);

    private static ControllerResult<T> Invalid<T>() where T : class => Failure<T>(
        ControllerErrorCode.InvalidRequest,
        "error.offline_reading.invalid");

    private static ControllerResult<T> Conflict<T>() where T : class => Failure<T>(
        ControllerErrorCode.Conflict,
        "error.offline_reading.revision_conflict");

    private static ControllerResult<T> NotFound<T>() where T : class => Failure<T>(
        ControllerErrorCode.NotFound,
        "error.offline_reading.not_found");

    private static ControllerResult<T> Corrupt<T>() where T : class => Failure<T>(
        ControllerErrorCode.IntegrityFailure,
        "error.offline_reading.storage_corrupt");

    private static ControllerResult<T> Unavailable<T>() where T : class => Failure<T>(
        ControllerErrorCode.Unavailable,
        "error.offline_reading.storage_unavailable");

    private static ControllerResult<T> Failure<T>(ControllerErrorCode code, string messageKey)
        where T : class => ControllerResult<T>.Failure(ControllerError.Create(code, messageKey));

    private sealed record StoredCatalog(int SchemaVersion, long Revision, IReadOnlyList<StoredItem> Items);

    private sealed record StoredItem(
        Guid ItemId,
        string Title,
        string SourceAddress,
        DateTimeOffset SavedAtUtc,
        long SizeBytes);
}
