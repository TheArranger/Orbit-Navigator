using System.Text.Json;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;

namespace OrbitNavigator.Foundation.Browser;

public enum TabControllerDockState
{
    Docked = 0,
    Detached = 1,
}

public readonly record struct TabControllerLayoutRevision(Guid Value)
{
    public bool IsEmpty => Value == Guid.Empty;
}

public sealed record TabControllerBoundsDip(
    double Left,
    double Top,
    double Width,
    double Height)
{
    public bool IsValid =>
        double.IsFinite(Left) &&
        double.IsFinite(Top) &&
        double.IsFinite(Width) &&
        double.IsFinite(Height) &&
        Math.Abs(Left) <= 262_144 &&
        Math.Abs(Top) <= 262_144 &&
        Width is >= 320 and <= 8_192 &&
        Height is >= 240 and <= 8_192;
}

public sealed record TabControllerLayoutSnapshot(
    ProfileId ProfileId,
    TabControllerLayoutRevision Revision,
    TabControllerDockState DockState,
    TabControllerBoundsDip? DetachedBounds);

public sealed record SaveTabControllerLayoutIntent(
    PrivacyContext Context,
    TabControllerLayoutRevision ExpectedRevision,
    TabControllerDockState DockState,
    TabControllerBoundsDip? DetachedBounds);

public interface ITabControllerLayoutStore
{
    ValueTask<ControllerResult<TabControllerLayoutSnapshot>> LoadAsync(
        PrivacyContext context,
        CancellationToken cancellationToken = default);

    ValueTask<ControllerResult<TabControllerLayoutSnapshot>> SaveAsync(
        SaveTabControllerLayoutIntent intent,
        CancellationToken cancellationToken = default);
}

public sealed class TabControllerLayoutStore : ITabControllerLayoutStore
{
    private const string StorageKey = "layout";
    private readonly IProfileStorage _storage;
    private readonly ProfileStorageNamespace _namespace;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TabControllerLayoutStore(IProfileStorage storage)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _namespace = ProfileStorageNamespace.Create("browser.tab-controller-layout").Value!;
    }

    public async ValueTask<ControllerResult<TabControllerLayoutSnapshot>> LoadAsync(
        PrivacyContext context,
        CancellationToken cancellationToken = default)
    {
        if (context is not { IsStructurallyValid: true })
        {
            return Invalid();
        }
        if (context.IsPrivate)
        {
            return ControllerResult<TabControllerLayoutSnapshot>.Success(Default(context.ProfileId));
        }

        var address = Address(context).Value!;
        var read = await _storage.ReadAsync(address, cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            return read.Error?.Code == ControllerErrorCode.NotFound
                ? ControllerResult<TabControllerLayoutSnapshot>.Success(Default(context.ProfileId))
                : ControllerResult<TabControllerLayoutSnapshot>.Failure(read.Error!);
        }

        try
        {
            var stored = JsonSerializer.Deserialize<StoredLayout>(read.Value!.Payload.Span);
            if (stored is null || !Validate(stored.DockState, stored.DetachedBounds))
            {
                return Corrupt();
            }

            return ControllerResult<TabControllerLayoutSnapshot>.Success(new(
                context.ProfileId,
                new TabControllerLayoutRevision(read.Value.Revision.Value),
                stored.DockState,
                stored.DetachedBounds));
        }
        catch (JsonException)
        {
            return Corrupt();
        }
    }

    public async ValueTask<ControllerResult<TabControllerLayoutSnapshot>> SaveAsync(
        SaveTabControllerLayoutIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (intent.Context is not { IsStructurallyValid: true } ||
            !Validate(intent.DockState, intent.DetachedBounds))
        {
            return Invalid();
        }
        if (intent.Context.IsPrivate)
        {
            return ControllerResult<TabControllerLayoutSnapshot>.Failure(ControllerError.Create(
                ControllerErrorCode.PolicyDenied,
                "error.tab_controller_layout.private_write_denied"));
        }

        var address = Address(intent.Context).Value!;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await _storage.ReadAsync(address, cancellationToken).ConfigureAwait(false);
            ProfileStorageRevision? expected;
            if (current.IsSuccess)
            {
                if (intent.ExpectedRevision.IsEmpty ||
                    current.Value!.Revision.Value != intent.ExpectedRevision.Value)
                {
                    return Conflict();
                }
                expected = current.Value.Revision;
            }
            else if (current.Error?.Code == ControllerErrorCode.NotFound)
            {
                if (!intent.ExpectedRevision.IsEmpty)
                {
                    return Conflict();
                }
                expected = null;
            }
            else
            {
                return ControllerResult<TabControllerLayoutSnapshot>.Failure(current.Error!);
            }

            var payload = JsonSerializer.SerializeToUtf8Bytes(new StoredLayout(
                intent.DockState,
                intent.DetachedBounds));
            var request = ProfileStorageWriteRequest.Create(address, payload, expected).Value!;
            var write = await _storage.WriteAsync(request, cancellationToken).ConfigureAwait(false);
            return write.IsSuccess
                ? ControllerResult<TabControllerLayoutSnapshot>.Success(new(
                    intent.Context.ProfileId,
                    new TabControllerLayoutRevision(write.Value!.Revision.Value),
                    intent.DockState,
                    intent.DetachedBounds))
                : ControllerResult<TabControllerLayoutSnapshot>.Failure(write.Error!);
        }
        finally
        {
            _gate.Release();
        }
    }

    private ControllerResult<ProfileStorageAddress> Address(PrivacyContext context) =>
        ProfileStorageAddress.Create(
            context,
            _namespace,
            ProfileStorageKey.Create(StorageKey).Value,
            ProfileStorageDurability.Persistent);

    private static bool Validate(
        TabControllerDockState dockState,
        TabControllerBoundsDip? bounds) =>
        Enum.IsDefined(dockState) && (bounds is null || bounds.IsValid);

    private static TabControllerLayoutSnapshot Default(ProfileId profileId) =>
        new(profileId, default, TabControllerDockState.Docked, null);

    private static ControllerResult<TabControllerLayoutSnapshot> Invalid() =>
        ControllerResult<TabControllerLayoutSnapshot>.Failure(ControllerError.Create(
            ControllerErrorCode.InvalidRequest,
            "error.tab_controller_layout.invalid"));

    private static ControllerResult<TabControllerLayoutSnapshot> Conflict() =>
        ControllerResult<TabControllerLayoutSnapshot>.Failure(ControllerError.Create(
            ControllerErrorCode.Conflict,
            "error.tab_controller_layout.revision_conflict"));

    private static ControllerResult<TabControllerLayoutSnapshot> Corrupt() =>
        ControllerResult<TabControllerLayoutSnapshot>.Failure(ControllerError.Create(
            ControllerErrorCode.IntegrityFailure,
            "error.tab_controller_layout.storage_corrupt"));

    private sealed record StoredLayout(
        TabControllerDockState DockState,
        TabControllerBoundsDip? DetachedBounds);
}
