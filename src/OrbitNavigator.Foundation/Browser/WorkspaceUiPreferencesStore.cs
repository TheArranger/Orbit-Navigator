using System.Text.Json;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;

namespace OrbitNavigator.Foundation.Browser;

public enum TabStripPlacement
{
    Top = 0,
    Left = 1,
    Right = 2,
}

public enum WorkspaceNewTabMode
{
    Stellar = 0,
    Basic = 1,
}

public enum WorkspacePreviewMode
{
    Hover = 0,
    Click = 1,
}

public enum WorkspaceAffiliatedRailPlacement
{
    Left = 0,
    Right = 1,
}

public static class WorkspaceUiPreferenceLimits
{
    public const double MinimumSideTabPanelWidth = 208;
    public const double DefaultSideTabPanelWidth = 224;
    public const double MaximumSideTabPanelWidth = 480;

    public static bool IsValidSideTabPanelWidth(double value) =>
        double.IsFinite(value) &&
        value is >= MinimumSideTabPanelWidth and <= MaximumSideTabPanelWidth;
}

public readonly record struct WorkspaceUiPreferencesRevision(Guid Value)
{
    public bool IsEmpty => Value == Guid.Empty;
}

public sealed record WorkspaceUiPreferencesSnapshot(
    ProfileId ProfileId,
    WorkspaceUiPreferencesRevision Revision,
    TabStripPlacement TabStripPlacement,
    bool CollapseToActive,
    bool ShowOrbitalGroupPreview,
    bool CompactTabs,
    WorkspaceNewTabMode NewTabMode = WorkspaceNewTabMode.Stellar,
    WorkspacePreviewMode PreviewMode = WorkspacePreviewMode.Hover,
    WorkspaceAffiliatedRailPlacement AffiliatedRailPlacement = WorkspaceAffiliatedRailPlacement.Left,
    bool ShowAffiliatedRail = true,
    double SideTabPanelWidth = WorkspaceUiPreferenceLimits.DefaultSideTabPanelWidth);

public sealed record SaveWorkspaceUiPreferencesIntent(
    PrivacyContext Context,
    WorkspaceUiPreferencesRevision ExpectedRevision,
    TabStripPlacement TabStripPlacement,
    bool CollapseToActive,
    bool ShowOrbitalGroupPreview,
    bool CompactTabs,
    WorkspaceNewTabMode NewTabMode = WorkspaceNewTabMode.Stellar,
    WorkspacePreviewMode PreviewMode = WorkspacePreviewMode.Hover,
    WorkspaceAffiliatedRailPlacement AffiliatedRailPlacement = WorkspaceAffiliatedRailPlacement.Left,
    bool ShowAffiliatedRail = true,
    double SideTabPanelWidth = WorkspaceUiPreferenceLimits.DefaultSideTabPanelWidth);

public interface IWorkspaceUiPreferencesStore
{
    ValueTask<ControllerResult<WorkspaceUiPreferencesSnapshot>> LoadAsync(
        PrivacyContext context,
        CancellationToken cancellationToken = default);

    ValueTask<ControllerResult<WorkspaceUiPreferencesSnapshot>> SaveAsync(
        SaveWorkspaceUiPreferencesIntent intent,
        CancellationToken cancellationToken = default);
}

public sealed class WorkspaceUiPreferencesStore : IWorkspaceUiPreferencesStore
{
    private const string StorageKey = "preferences";
    private readonly IProfileStorage _storage;
    private readonly ProfileStorageNamespace _namespace;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WorkspaceUiPreferencesStore(IProfileStorage storage)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _namespace = ProfileStorageNamespace.Create("browser.workspace-ui").Value!;
    }

    public async ValueTask<ControllerResult<WorkspaceUiPreferencesSnapshot>> LoadAsync(
        PrivacyContext context,
        CancellationToken cancellationToken = default)
    {
        var address = Address(context);
        if (!address.IsSuccess)
        {
            return ControllerResult<WorkspaceUiPreferencesSnapshot>.Failure(address.Error!);
        }

        var read = await _storage.ReadAsync(address.Value!, cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            return read.Error?.Code == ControllerErrorCode.NotFound
                ? ControllerResult<WorkspaceUiPreferencesSnapshot>.Success(Default(context.ProfileId))
                : ControllerResult<WorkspaceUiPreferencesSnapshot>.Failure(read.Error!);
        }

        try
        {
            var stored = JsonSerializer.Deserialize<StoredPreferences>(read.Value!.Payload.Span);
            if (stored is null || !Enum.IsDefined(stored.TabStripPlacement) ||
                !Enum.IsDefined(stored.NewTabMode) || !Enum.IsDefined(stored.PreviewMode) ||
                !Enum.IsDefined(stored.AffiliatedRailPlacement) ||
                !WorkspaceUiPreferenceLimits.IsValidSideTabPanelWidth(stored.SideTabPanelWidth))
            {
                return Corrupt();
            }

            return ControllerResult<WorkspaceUiPreferencesSnapshot>.Success(new(
                context.ProfileId,
                new WorkspaceUiPreferencesRevision(read.Value.Revision.Value),
                stored.TabStripPlacement,
                stored.CollapseToActive,
                stored.ShowOrbitalGroupPreview,
                stored.CompactTabs,
                stored.NewTabMode,
                stored.PreviewMode,
                stored.AffiliatedRailPlacement,
                stored.ShowAffiliatedRail,
                stored.SideTabPanelWidth));
        }
        catch (JsonException)
        {
            return Corrupt();
        }
    }

    public async ValueTask<ControllerResult<WorkspaceUiPreferencesSnapshot>> SaveAsync(
        SaveWorkspaceUiPreferencesIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (intent.Context is not { IsStructurallyValid: true } ||
            !Enum.IsDefined(intent.TabStripPlacement) ||
            !Enum.IsDefined(intent.NewTabMode) || !Enum.IsDefined(intent.PreviewMode) ||
            !Enum.IsDefined(intent.AffiliatedRailPlacement) ||
            !WorkspaceUiPreferenceLimits.IsValidSideTabPanelWidth(intent.SideTabPanelWidth))
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
            var expected = ValidateExpectedRevision(current, intent.ExpectedRevision);
            if (!expected.IsSuccess)
            {
                return ControllerResult<WorkspaceUiPreferencesSnapshot>.Failure(expected.Error!);
            }

            var payload = JsonSerializer.SerializeToUtf8Bytes(new StoredPreferences(
                intent.TabStripPlacement,
                intent.CollapseToActive,
                intent.ShowOrbitalGroupPreview,
                intent.CompactTabs,
                intent.NewTabMode,
                intent.PreviewMode,
                intent.AffiliatedRailPlacement,
                intent.ShowAffiliatedRail,
                intent.SideTabPanelWidth));
            var writeRequest = ProfileStorageWriteRequest.Create(
                address,
                payload,
                expected.Value!.Revision).Value!;
            var write = await _storage.WriteAsync(writeRequest, cancellationToken).ConfigureAwait(false);
            if (!write.IsSuccess)
            {
                return ControllerResult<WorkspaceUiPreferencesSnapshot>.Failure(write.Error!);
            }

            return ControllerResult<WorkspaceUiPreferencesSnapshot>.Success(new(
                intent.Context.ProfileId,
                new WorkspaceUiPreferencesRevision(write.Value!.Revision.Value),
                intent.TabStripPlacement,
                intent.CollapseToActive,
                intent.ShowOrbitalGroupPreview,
                intent.CompactTabs,
                intent.NewTabMode,
                intent.PreviewMode,
                intent.AffiliatedRailPlacement,
                intent.ShowAffiliatedRail,
                intent.SideTabPanelWidth));
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
                "error.workspace_preferences.context_invalid"));
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

    private static ControllerResult<ExpectedRevisionValidation> ValidateExpectedRevision(
        ControllerResult<ProfileStorageEntry> current,
        WorkspaceUiPreferencesRevision expected)
    {
        if (current.IsSuccess)
        {
            return !expected.IsEmpty && current.Value!.Revision.Value == expected.Value
                ? ControllerResult<ExpectedRevisionValidation>.Success(new(current.Value.Revision))
                : RevisionConflict<ExpectedRevisionValidation>();
        }

        if (current.Error?.Code != ControllerErrorCode.NotFound)
        {
            return ControllerResult<ExpectedRevisionValidation>.Failure(current.Error!);
        }

        return expected.IsEmpty
            ? ControllerResult<ExpectedRevisionValidation>.Success(new(null))
            : RevisionConflict<ExpectedRevisionValidation>();
    }

    private static WorkspaceUiPreferencesSnapshot Default(ProfileId profileId) =>
        new(profileId, default, TabStripPlacement.Top, false, true, false,
            WorkspaceNewTabMode.Stellar, WorkspacePreviewMode.Hover,
            WorkspaceAffiliatedRailPlacement.Left, true,
            WorkspaceUiPreferenceLimits.DefaultSideTabPanelWidth);

    private static ControllerResult<WorkspaceUiPreferencesSnapshot> Invalid() =>
        ControllerResult<WorkspaceUiPreferencesSnapshot>.Failure(ControllerError.Create(
            ControllerErrorCode.InvalidRequest,
            "error.workspace_preferences.invalid"));

    private static ControllerResult<WorkspaceUiPreferencesSnapshot> PolicyDenied() =>
        ControllerResult<WorkspaceUiPreferencesSnapshot>.Failure(ControllerError.Create(
            ControllerErrorCode.PolicyDenied,
            "error.workspace_preferences.private_write_denied"));

    private static ControllerResult<WorkspaceUiPreferencesSnapshot> Corrupt() =>
        ControllerResult<WorkspaceUiPreferencesSnapshot>.Failure(ControllerError.Create(
            ControllerErrorCode.IntegrityFailure,
            "error.workspace_preferences.storage_corrupt"));

    private static ControllerResult<T> RevisionConflict<T>() where T : class =>
        ControllerResult<T>.Failure(ControllerError.Create(
            ControllerErrorCode.Conflict,
            "error.workspace_preferences.revision_conflict"));

    private sealed record StoredPreferences(
        TabStripPlacement TabStripPlacement,
        bool CollapseToActive,
        bool ShowOrbitalGroupPreview,
        bool CompactTabs = false,
        WorkspaceNewTabMode NewTabMode = WorkspaceNewTabMode.Stellar,
        WorkspacePreviewMode PreviewMode = WorkspacePreviewMode.Hover,
        WorkspaceAffiliatedRailPlacement AffiliatedRailPlacement = WorkspaceAffiliatedRailPlacement.Left,
        bool ShowAffiliatedRail = true,
        double SideTabPanelWidth = WorkspaceUiPreferenceLimits.DefaultSideTabPanelWidth);

    private sealed record ExpectedRevisionValidation(ProfileStorageRevision? Revision);
}
