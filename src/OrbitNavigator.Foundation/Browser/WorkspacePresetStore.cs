using System.Text.Json;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;

namespace OrbitNavigator.Foundation.Browser;

public readonly record struct WorkspacePresetId(ProfileId ProfileId, Guid Value)
{
    public bool IsEmpty => ProfileId.IsEmpty || Value == Guid.Empty;
}

public readonly record struct WorkspacePresetCatalogRevision(Guid Value)
{
    public bool IsEmpty => Value == Guid.Empty;
}

public enum StoredWorkspaceArtworkKind
{
    None = 0,
    Site = 1,
    LocalImage = 2,
}

public sealed record StoredWorkspaceArtwork(
    StoredWorkspaceArtworkKind Kind,
    Uri? SiteAddress,
    string? LocalAssetId,
    string AccessibleDescription)
{
    public static StoredWorkspaceArtwork None { get; } =
        new(StoredWorkspaceArtworkKind.None, null, null, "No custom artwork");
}

public sealed record WorkspacePresetTab(Uri Target, string DisplayTitle)
{
    public string? Note { get; init; }
    public byte[] FaviconPng { get; init; } = [];
}

public sealed record WorkspacePreset(
    WorkspacePresetId Id,
    string Name,
    string? GroupName,
    IReadOnlyList<WorkspacePresetTab> Tabs,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    public string ColorToken { get; init; } = "SeaGlass";
    public string? Note { get; init; }
    public StoredWorkspaceArtwork Artwork { get; init; } = StoredWorkspaceArtwork.None;
    public int FirstTabIndex { get; init; }
}

public sealed record WorkspacePresetCatalogSnapshot(
    ProfileId ProfileId,
    WorkspacePresetCatalogRevision Revision,
    IReadOnlyList<WorkspacePreset> Presets);

public sealed record UpsertWorkspacePresetIntent(
    PrivacyContext Context,
    WorkspacePresetCatalogRevision ExpectedRevision,
    WorkspacePresetId? PresetId,
    string Name,
    string? GroupName,
    IReadOnlyList<WorkspacePresetTab> Tabs)
{
    public string ColorToken { get; init; } = "SeaGlass";
    public string? Note { get; init; }
    public StoredWorkspaceArtwork Artwork { get; init; } = StoredWorkspaceArtwork.None;
    public int FirstTabIndex { get; init; }
}

public sealed record RemoveWorkspacePresetIntent(
    PrivacyContext Context,
    WorkspacePresetCatalogRevision ExpectedRevision,
    WorkspacePresetId PresetId);

public interface IWorkspacePresetFacade
{
    ValueTask<ControllerResult<WorkspacePresetCatalogSnapshot>> QueryAsync(
        PrivacyContext context,
        CancellationToken cancellationToken = default);

    ValueTask<ControllerResult<WorkspacePresetCatalogSnapshot>> UpsertAsync(
        UpsertWorkspacePresetIntent intent,
        CancellationToken cancellationToken = default);

    ValueTask<ControllerResult<WorkspacePresetCatalogSnapshot>> RemoveAsync(
        RemoveWorkspacePresetIntent intent,
        CancellationToken cancellationToken = default);
}

public sealed class WorkspacePresetStore : IWorkspacePresetFacade
{
    public const int CurrentSchemaVersion = 2;
    public const int MaximumPresets = 24;
    public const int MaximumTabsPerPreset = 32;
    public const int MaximumPresetNameLength = 80;
    public const int MaximumGroupNameLength = 60;
    public const int MaximumDisplayTitleLength = 200;
    public const int MaximumNoteLength = 1000;
    public const int MaximumTabNoteLength = 500;
    public const int MaximumFaviconBytes = 262_144;

    private const string StorageKey = "catalog";
    private readonly IProfileStorage _storage;
    private readonly IClock _clock;
    private readonly ProfileStorageNamespace _namespace;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WorkspacePresetStore(IProfileStorage storage, IClock clock)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _namespace = ProfileStorageNamespace.Create("browser.workspace-presets").Value!;
    }

    public async ValueTask<ControllerResult<WorkspacePresetCatalogSnapshot>> QueryAsync(
        PrivacyContext context,
        CancellationToken cancellationToken = default)
    {
        var address = Address(context);
        if (!address.IsSuccess)
        {
            return ControllerResult<WorkspacePresetCatalogSnapshot>.Failure(address.Error!);
        }

        var loaded = await LoadStoredAsync(context.ProfileId, address.Value!, cancellationToken)
            .ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return ControllerResult<WorkspacePresetCatalogSnapshot>.Failure(loaded.Error!);
        }
        if (!loaded.Value!.RequiresMigration || context.IsPrivate)
        {
            return ControllerResult<WorkspacePresetCatalogSnapshot>.Success(new(
                context.ProfileId,
                loaded.Value.Revision,
                loaded.Value.Presets));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-read under the write gate so migration is an exact CAS and never
            // overwrites a concurrent user edit.
            loaded = await LoadStoredAsync(context.ProfileId, address.Value!, cancellationToken)
                .ConfigureAwait(false);
            if (!loaded.IsSuccess)
            {
                return ControllerResult<WorkspacePresetCatalogSnapshot>.Failure(loaded.Error!);
            }
            return loaded.Value!.RequiresMigration
                ? await WriteAsync(
                    context.ProfileId,
                    address.Value!,
                    loaded.Value.Revision,
                    loaded.Value.Presets,
                    cancellationToken).ConfigureAwait(false)
                : ControllerResult<WorkspacePresetCatalogSnapshot>.Success(new(
                    context.ProfileId,
                    loaded.Value.Revision,
                    loaded.Value.Presets));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ControllerResult<WorkspacePresetCatalogSnapshot>> UpsertAsync(
        UpsertWorkspacePresetIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (!ValidateIntent(intent))
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
            var loaded = await LoadStoredAsync(intent.Context.ProfileId, address, cancellationToken)
                .ConfigureAwait(false);
            if (!loaded.IsSuccess)
            {
                return ControllerResult<WorkspacePresetCatalogSnapshot>.Failure(loaded.Error!);
            }
            if (loaded.Value!.Revision != intent.ExpectedRevision)
            {
                return RevisionConflict();
            }

            var presets = loaded.Value.Presets.ToList();
            var presetId = intent.PresetId is { IsEmpty: false } supplied
                ? supplied
                : new WorkspacePresetId(intent.Context.ProfileId, Guid.NewGuid());
            var existingIndex = presets.FindIndex(preset => preset.Id == presetId);
            if (existingIndex < 0 && presets.Count >= MaximumPresets)
            {
                return ControllerResult<WorkspacePresetCatalogSnapshot>.Failure(ControllerError.Create(
                    ControllerErrorCode.InvalidRequest,
                    "error.workspace_presets.capacity_reached"));
            }

            var now = _clock.UtcNow;
            var created = existingIndex >= 0 ? presets[existingIndex].CreatedAtUtc : now;
            var updated = new WorkspacePreset(
                presetId,
                intent.Name.Trim(),
                NormalizeOptional(intent.GroupName),
                intent.Tabs.Select(tab => new WorkspacePresetTab(
                    tab.Target,
                    tab.DisplayTitle.Trim())
                {
                    Note = NormalizeOptional(tab.Note),
                    FaviconPng = tab.FaviconPng.ToArray(),
                }).ToArray(),
                created,
                now)
            {
                ColorToken = NormalizeColor(intent.ColorToken),
                Note = NormalizeOptional(intent.Note),
                Artwork = NormalizeArtwork(intent.Artwork),
                FirstTabIndex = intent.FirstTabIndex,
            };
            if (existingIndex >= 0)
            {
                presets[existingIndex] = updated;
            }
            else
            {
                presets.Add(updated);
            }

            return await WriteAsync(
                intent.Context.ProfileId,
                address,
                loaded.Value.Revision,
                presets,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ControllerResult<WorkspacePresetCatalogSnapshot>> RemoveAsync(
        RemoveWorkspacePresetIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (intent.Context is not { IsStructurallyValid: true } ||
            intent.PresetId.IsEmpty ||
            intent.PresetId.ProfileId != intent.Context.ProfileId)
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
            var loaded = await LoadStoredAsync(intent.Context.ProfileId, address, cancellationToken)
                .ConfigureAwait(false);
            if (!loaded.IsSuccess)
            {
                return ControllerResult<WorkspacePresetCatalogSnapshot>.Failure(loaded.Error!);
            }
            if (loaded.Value!.Revision != intent.ExpectedRevision)
            {
                return RevisionConflict();
            }

            var presets = loaded.Value.Presets.ToList();
            if (presets.RemoveAll(preset => preset.Id == intent.PresetId) != 1)
            {
                return ControllerResult<WorkspacePresetCatalogSnapshot>.Failure(ControllerError.Create(
                    ControllerErrorCode.NotFound,
                    "error.workspace_presets.not_found"));
            }

            return await WriteAsync(
                intent.Context.ProfileId,
                address,
                loaded.Value.Revision,
                presets,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<ControllerResult<StoredCatalog>> LoadStoredAsync(
        ProfileId profileId,
        ProfileStorageAddress address,
        CancellationToken cancellationToken)
    {
        var read = await _storage.ReadAsync(address, cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            return read.Error?.Code == ControllerErrorCode.NotFound
                ? ControllerResult<StoredCatalog>.Success(new(default, [], false))
                : ControllerResult<StoredCatalog>.Failure(read.Error!);
        }

        try
        {
            using var document = JsonDocument.Parse(read.Value!.Payload);
            WorkspacePreset[]? presets;
            var requiresMigration = false;
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                presets = document.RootElement.Deserialize<WorkspacePreset[]>();
                requiresMigration = true;
                if (presets is not null)
                {
                    presets = presets.Select(MigratePreset).ToArray();
                }
            }
            else
            {
                var envelope = document.RootElement.Deserialize<StoredCatalogEnvelope>();
                if (envelope is null || envelope.SchemaVersion != CurrentSchemaVersion)
                {
                    return Corrupt<StoredCatalog>();
                }
                presets = envelope.Presets.ToArray();
            }
            if (presets is null)
            {
                return Corrupt<StoredCatalog>();
            }

            if (!ValidateCatalog(profileId, presets))
            {
                return Corrupt<StoredCatalog>();
            }

            return ControllerResult<StoredCatalog>.Success(new(
                new WorkspacePresetCatalogRevision(read.Value.Revision.Value),
                presets,
                requiresMigration));
        }
        catch (JsonException)
        {
            return Corrupt<StoredCatalog>();
        }
    }

    private async ValueTask<ControllerResult<WorkspacePresetCatalogSnapshot>> WriteAsync(
        ProfileId profileId,
        ProfileStorageAddress address,
        WorkspacePresetCatalogRevision currentRevision,
        IReadOnlyList<WorkspacePreset> presets,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new StoredCatalogEnvelope(
            CurrentSchemaVersion,
            presets.ToArray()));
        ProfileStorageRevision? expected = currentRevision.IsEmpty
            ? null
            : new ProfileStorageRevision(currentRevision.Value);
        var request = ProfileStorageWriteRequest.Create(address, payload, expected).Value!;
        var write = await _storage.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        if (!write.IsSuccess)
        {
            return ControllerResult<WorkspacePresetCatalogSnapshot>.Failure(write.Error!);
        }

        return ControllerResult<WorkspacePresetCatalogSnapshot>.Success(new(
            profileId,
            new WorkspacePresetCatalogRevision(write.Value!.Revision.Value),
            presets.ToArray()));
    }

    private ControllerResult<ProfileStorageAddress> Address(PrivacyContext context)
    {
        if (context is not { IsStructurallyValid: true })
        {
            return ControllerResult<ProfileStorageAddress>.Failure(ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "error.workspace_presets.context_invalid"));
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

    private static bool ValidateIntent(UpsertWorkspacePresetIntent intent) =>
        intent.Context is { IsStructurallyValid: true } &&
        (intent.PresetId is null ||
            (!intent.PresetId.Value.IsEmpty &&
             intent.PresetId.Value.ProfileId == intent.Context.ProfileId)) &&
        ValidName(intent.Name, MaximumPresetNameLength) &&
        (intent.GroupName is null || ValidName(intent.GroupName, MaximumGroupNameLength)) &&
        intent.Tabs is { Count: > 0 and <= MaximumTabsPerPreset } &&
        intent.Tabs.All(ValidTab) &&
        ValidOptional(intent.Note, MaximumNoteLength) &&
        intent.FirstTabIndex >= 0 && intent.FirstTabIndex < intent.Tabs.Count &&
        ValidArtwork(intent.Artwork, intent.Tabs);

    private static bool ValidateCatalog(ProfileId profileId, IReadOnlyList<WorkspacePreset> presets)
    {
        if (presets.Count > MaximumPresets)
        {
            return false;
        }

        var ids = new HashSet<WorkspacePresetId>();
        return presets.All(preset =>
            preset is not null &&
            !preset.Id.IsEmpty &&
            preset.Id.ProfileId == profileId &&
            ids.Add(preset.Id) &&
            ValidName(preset.Name, MaximumPresetNameLength) &&
            (preset.GroupName is null || ValidName(preset.GroupName, MaximumGroupNameLength)) &&
            preset.Tabs is { Count: > 0 and <= MaximumTabsPerPreset } &&
            preset.Tabs.All(ValidTab) &&
            ValidOptional(preset.Note, MaximumNoteLength) &&
            preset.FirstTabIndex >= 0 && preset.FirstTabIndex < preset.Tabs.Count &&
            ValidArtwork(preset.Artwork, preset.Tabs) &&
            preset.CreatedAtUtc <= preset.UpdatedAtUtc);
    }

    private static bool ValidTab(WorkspacePresetTab tab) =>
        tab is not null &&
        tab.Target is { IsAbsoluteUri: true } &&
        tab.Target.Scheme is "http" or "https" &&
        string.IsNullOrEmpty(tab.Target.UserInfo) &&
        !string.IsNullOrWhiteSpace(tab.Target.IdnHost) &&
        ValidName(tab.DisplayTitle, MaximumDisplayTitleLength) &&
        ValidOptional(tab.Note, MaximumTabNoteLength) &&
        tab.FaviconPng is { Length: <= MaximumFaviconBytes } &&
        (tab.FaviconPng.Length == 0 || IsPng(tab.FaviconPng));

    private static WorkspacePreset MigratePreset(WorkspacePreset preset)
    {
        var tabs = preset.Tabs.Select(tab => tab with
        {
            Target = MigrateTarget(tab.Target),
            Note = NormalizeOptional(tab.Note),
            FaviconPng = tab.FaviconPng?.ToArray() ?? [],
        }).ToArray();
        var artwork = preset.Artwork ?? StoredWorkspaceArtwork.None;
        if (artwork.Kind == StoredWorkspaceArtworkKind.Site && artwork.SiteAddress is not null)
        {
            artwork = artwork with { SiteAddress = MigrateTarget(artwork.SiteAddress) };
        }
        return preset with
        {
            Tabs = tabs,
            ColorToken = NormalizeColor(preset.ColorToken),
            Note = NormalizeOptional(preset.Note),
            Artwork = NormalizeArtwork(artwork),
            FirstTabIndex = Math.Clamp(preset.FirstTabIndex, 0, Math.Max(0, tabs.Length - 1)),
        };
    }

    private static Uri MigrateTarget(Uri target)
    {
        var canonical = CanonicalWebAddress.Normalize(target);
        if (!canonical.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
            !canonical.IdnHost.Equals("my-orbit.ping-it.cc", StringComparison.OrdinalIgnoreCase) ||
            !canonical.IsDefaultPort || !string.IsNullOrEmpty(canonical.UserInfo))
        {
            return canonical;
        }

        var builder = new UriBuilder(canonical)
        {
            Host = "my-orbit.snap-it.cc",
            Port = -1,
        };
        return builder.Uri;
    }

    private static bool ValidArtwork(
        StoredWorkspaceArtwork? artwork,
        IReadOnlyList<WorkspacePresetTab> tabs) => artwork is not null &&
        Enum.IsDefined(artwork.Kind) &&
        artwork.Kind switch
        {
            StoredWorkspaceArtworkKind.None => true,
            StoredWorkspaceArtworkKind.Site => artwork.SiteAddress is { IsAbsoluteUri: true } site &&
                site.Scheme is "http" or "https" && string.IsNullOrEmpty(site.UserInfo) &&
                tabs.Any(tab => tab.Target == site),
            StoredWorkspaceArtworkKind.LocalImage => IsOpaqueAssetId(artwork.LocalAssetId),
            _ => false,
        };

    private static StoredWorkspaceArtwork NormalizeArtwork(StoredWorkspaceArtwork artwork) =>
        artwork.Kind switch
        {
            StoredWorkspaceArtworkKind.Site => artwork with
            {
                LocalAssetId = null,
                AccessibleDescription = NormalizeDescription(artwork.AccessibleDescription),
            },
            StoredWorkspaceArtworkKind.LocalImage => artwork with
            {
                SiteAddress = null,
                LocalAssetId = artwork.LocalAssetId!.Trim(),
                AccessibleDescription = NormalizeDescription(artwork.AccessibleDescription),
            },
            _ => StoredWorkspaceArtwork.None,
        };

    private static bool IsOpaqueAssetId(string? value) =>
        value is { Length: 35 } && value.StartsWith("wa_", StringComparison.Ordinal) &&
        value.AsSpan(3).IndexOfAnyExcept("0123456789abcdef".AsSpan()) < 0;

    private static bool IsPng(ReadOnlySpan<byte> value) => value.Length >= 8 &&
        value[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

    private static bool ValidOptional(string? value, int maximumLength) =>
        value is null || (!string.IsNullOrWhiteSpace(value) && value.Trim().Length <= maximumLength);

    private static string NormalizeColor(string? value) => value is
        "SeaGlass" or "Gold" or "Violet" or "Scarlet" or "Azure" or "Slate"
        ? value : "SeaGlass";

    private static string NormalizeDescription(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "Local workspace artwork" : value.Trim()[..Math.Min(200, value.Trim().Length)];

    private static bool ValidName(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= maximumLength;

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ControllerResult<WorkspacePresetCatalogSnapshot> Invalid() =>
        ControllerResult<WorkspacePresetCatalogSnapshot>.Failure(ControllerError.Create(
            ControllerErrorCode.InvalidRequest,
            "error.workspace_presets.invalid"));

    private static ControllerResult<WorkspacePresetCatalogSnapshot> PolicyDenied() =>
        ControllerResult<WorkspacePresetCatalogSnapshot>.Failure(ControllerError.Create(
            ControllerErrorCode.PolicyDenied,
            "error.workspace_presets.private_write_denied"));

    private static ControllerResult<WorkspacePresetCatalogSnapshot> RevisionConflict() =>
        ControllerResult<WorkspacePresetCatalogSnapshot>.Failure(ControllerError.Create(
            ControllerErrorCode.Conflict,
            "error.workspace_presets.revision_conflict"));

    private static ControllerResult<T> Corrupt<T>() where T : class =>
        ControllerResult<T>.Failure(ControllerError.Create(
            ControllerErrorCode.IntegrityFailure,
            "error.workspace_presets.storage_corrupt"));

    private sealed record StoredCatalog(
        WorkspacePresetCatalogRevision Revision,
        IReadOnlyList<WorkspacePreset> Presets,
        bool RequiresMigration);

    private sealed record StoredCatalogEnvelope(
        int SchemaVersion,
        IReadOnlyList<WorkspacePreset> Presets);
}
