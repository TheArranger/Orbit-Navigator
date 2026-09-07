using System.Security.Cryptography;
using System.Text;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Privacy;

namespace OrbitNavigator.Privacy.Clipboard;

/// <summary>
/// Process-local clipboard shelf. Content never crosses the UI contract; the UI sees
/// only capped summaries and refers to content by profile-bound identifiers.
/// </summary>
public sealed class ClipboardShelfController :
    IClipboardShelfController,
    IClipboardShelfCaptureSink,
    IAsyncDisposable
{
    public const int MaximumContentCharacters = 65_536;
    private const int MaximumPreviewCharacters = 80;

    private readonly IClipboardCaptureLeaseProvider _leases;
    private readonly IClipboardShelfUseTarget _useTarget;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly Dictionary<ProfileId, LinkedList<StoredItem>> _items = [];
    private int _disposeState;

    public ClipboardShelfController(
        IClipboardCaptureLeaseProvider leases,
        IClipboardShelfUseTarget useTarget,
        IClock clock)
    {
        _leases = leases ?? throw new ArgumentNullException(nameof(leases));
        _useTarget = useTarget ?? throw new ArgumentNullException(nameof(useTarget));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask<ControllerResult<ClipboardShelfReadModel>> GetAsync(
        BrowsingContext context,
        CancellationToken cancellationToken)
    {
        var policy = ClipboardShelfPolicy.Read(context);
        if (policy.IsSuccess)
        {
            return policy;
        }

        if (policy.Error?.Code != ControllerErrorCode.Unavailable)
        {
            return policy;
        }

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!_items.TryGetValue(context.Privacy.ProfileId, out var profileItems))
            {
                return ControllerResult<ClipboardShelfReadModel>.Success(Available([]));
            }

            var summaries = profileItems
                .Reverse()
                .Select(ToSummary)
                .ToArray();
            return ControllerResult<ClipboardShelfReadModel>.Success(Available(summaries));
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<ControllerResult> CaptureAsync(
        ClipboardShelfCaptureRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var authorization = ClipboardShelfPolicy.AuthorizeMutation(request.Context);
        if (!authorization.IsSuccess)
        {
            return authorization;
        }

        if (request.ContentLeaseId.IsEmpty ||
            request.ContentLeaseId.ProfileId != request.Context.Privacy.ProfileId ||
            !Enum.IsDefined(request.Kind) ||
            request.Classification != ClipboardCaptureClassification.ExplicitUserInitiatedNonSensitive ||
            request.CapturedAtUtc > _clock.UtcNow.AddMinutes(1))
        {
            return Invalid("error.clipboard-shelf.capture-invalid");
        }

        var redeemed = await _leases.RedeemAsync(
            request.Context,
            request.ContentLeaseId,
            cancellationToken).ConfigureAwait(false);
        if (!redeemed.IsSuccess)
        {
            return ControllerResult.Failure(redeemed.Error!);
        }

        using var sensitive = redeemed.Value!;
        if (sensitive.Characters.IsEmpty || sensitive.Characters.Length > MaximumContentCharacters)
        {
            return Invalid("error.clipboard-shelf.content-invalid");
        }

        var bytes = new byte[Encoding.UTF8.GetByteCount(sensitive.Characters.Span)];
        Encoding.UTF8.GetBytes(sensitive.Characters.Span, bytes);
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var profileId = request.Context.Privacy.ProfileId;
            if (!_items.TryGetValue(profileId, out var profileItems))
            {
                profileItems = new LinkedList<StoredItem>();
                _items.Add(profileId, profileItems);
            }

            profileItems.AddLast(new StoredItem(
                new ClipboardShelfItemId(profileId, Guid.NewGuid()),
                request.Kind,
                bytes,
                request.SourceSite,
                request.CapturedAtUtc));
            bytes = [];

            while (profileItems.Count > ClipboardShelfReadModel.MaximumCapacity)
            {
                var evicted = profileItems.First!;
                profileItems.RemoveFirst();
                evicted.Value.Dispose();
            }

            return ControllerResult.Success();
        }
        finally
        {
            if (bytes.Length > 0)
            {
                CryptographicOperations.ZeroMemory(bytes);
            }

            _mutex.Release();
        }
    }

    public async ValueTask<ControllerResult<ClipboardShelfUseReceipt>> UseAsync(
        UseClipboardShelfItemIntent intent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var authorization = ClipboardShelfPolicy.AuthorizeMutation(intent.Context);
        if (!authorization.IsSuccess)
        {
            return ControllerResult<ClipboardShelfUseReceipt>.Failure(authorization.Error!);
        }

        if (intent.ItemId.IsEmpty || intent.ItemId.ProfileId != intent.Context.Privacy.ProfileId)
        {
            return ControllerResult<ClipboardShelfUseReceipt>.Failure(
                Error(ControllerErrorCode.InvalidRequest, "error.clipboard-shelf.item-invalid"));
        }

        StoredItem? item;
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            item = Find(intent.Context.Privacy.ProfileId, intent.ItemId);
            if (item is null)
            {
                return ControllerResult<ClipboardShelfUseReceipt>.Failure(
                    Error(ControllerErrorCode.NotFound, "error.clipboard-shelf.item-not-found"));
            }

            item = item.Clone();
        }
        finally
        {
            _mutex.Release();
        }

        using (item)
        {
            var decoded = new char[Encoding.UTF8.GetCharCount(item.Content)];
            Encoding.UTF8.GetChars(item.Content, decoded);
            var created = SensitiveClipboardContent.Create(decoded);
            CryptographicOperations.ZeroMemory(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(decoded.AsSpan()));
            if (!created.IsSuccess)
            {
                return ControllerResult<ClipboardShelfUseReceipt>.Failure(created.Error!);
            }

            using var sensitive = created.Value!;
            var write = await _useTarget.WriteAsync(
                intent.Context,
                item.Kind,
                sensitive,
                cancellationToken).ConfigureAwait(false);
            if (!write.IsSuccess)
            {
                return ControllerResult<ClipboardShelfUseReceipt>.Failure(write.Error!);
            }
        }

        return ControllerResult<ClipboardShelfUseReceipt>.Success(new ClipboardShelfUseReceipt(
            intent.ItemId,
            intent.Context.TabId,
            _clock.UtcNow));
    }

    public async ValueTask<ControllerResult> ClearAsync(
        ClearClipboardShelfIntent intent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var authorization = ClipboardShelfPolicy.AuthorizeMutation(intent.Context);
        if (!authorization.IsSuccess)
        {
            return authorization;
        }

        if (!intent.Confirmed)
        {
            return Invalid("error.clipboard-shelf.clear-confirmation-required");
        }

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_items.Remove(intent.Context.Privacy.ProfileId, out var profileItems))
            {
                Dispose(profileItems);
            }

            return ControllerResult.Success();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var profileItems in _items.Values)
            {
                Dispose(profileItems);
            }

            _items.Clear();
        }
        finally
        {
            _mutex.Release();
        }
    }

    private static ClipboardShelfReadModel Available(IReadOnlyList<ClipboardShelfItemSummary> items) =>
        new(
            ClipboardShelfAvailability.Available,
            ClipboardShelfUnavailableReason.None,
            ClipboardShelfReadModel.MaximumCapacity,
            items);

    private static ClipboardShelfItemSummary ToSummary(StoredItem item)
    {
        var value = Encoding.UTF8.GetString(item.Content);
        var preview = string.Join(' ', value.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (preview.Length > MaximumPreviewCharacters)
        {
            preview = string.Concat(preview.AsSpan(0, MaximumPreviewCharacters - 1), "…");
        }

        return new ClipboardShelfItemSummary(
            item.Id,
            item.Kind,
            preview,
            item.SourceSite,
            item.CapturedAtUtc);
    }

    private StoredItem? Find(ProfileId profileId, ClipboardShelfItemId id)
    {
        if (!_items.TryGetValue(profileId, out var profileItems))
        {
            return null;
        }

        return profileItems.FirstOrDefault(item => item.Id == id);
    }

    private static void Dispose(IEnumerable<StoredItem> items)
    {
        foreach (var item in items)
        {
            item.Dispose();
        }
    }

    private static ControllerResult Invalid(string messageKey) =>
        ControllerResult.Failure(Error(ControllerErrorCode.InvalidRequest, messageKey));

    private static ControllerError Error(ControllerErrorCode code, string messageKey) =>
        ControllerError.Create(code, messageKey);

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);

    private sealed class StoredItem : IDisposable
    {
        private byte[]? _content;

        public StoredItem(
            ClipboardShelfItemId id,
            ClipboardShelfContentKind kind,
            byte[] content,
            SiteIdentity? sourceSite,
            DateTimeOffset capturedAtUtc)
        {
            Id = id;
            Kind = kind;
            _content = content;
            SourceSite = sourceSite;
            CapturedAtUtc = capturedAtUtc;
        }

        public ClipboardShelfItemId Id { get; }
        public ClipboardShelfContentKind Kind { get; }
        public ReadOnlySpan<byte> Content =>
            _content is { } content ? content : throw new ObjectDisposedException(nameof(StoredItem));
        public SiteIdentity? SourceSite { get; }
        public DateTimeOffset CapturedAtUtc { get; }

        public StoredItem Clone() =>
            new(Id, Kind, Content.ToArray(), SourceSite, CapturedAtUtc);

        public void Dispose()
        {
            var content = Interlocked.Exchange(ref _content, null);
            if (content is not null)
            {
                CryptographicOperations.ZeroMemory(content);
            }
        }
    }
}
