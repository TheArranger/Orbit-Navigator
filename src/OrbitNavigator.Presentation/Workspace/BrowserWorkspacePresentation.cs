using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Presentation.Tabs;

namespace OrbitNavigator.Presentation.Workspace;

public enum TabStripPlacement
{
    Top = 0,
    Left = 1,
    Right = 2,
}

public enum NewTabVisualMode
{
    Stellar = 0,
    Basic = 1,
}

public enum WorkspacePreviewRevealMode
{
    Hover = 0,
    Click = 1,
}

public enum AffiliatedRailPlacement
{
    Left = 0,
    Right = 1,
}

public enum WorkspaceArtworkKind
{
    None = 0,
    Site = 1,
    LocalImage = 2,
}

public sealed record WorkspaceArtworkPresentation(
    WorkspaceArtworkKind Kind,
    Uri? SiteAddress,
    string? LocalAssetId,
    string AccessibleDescription)
{
    public static WorkspaceArtworkPresentation None { get; } =
        new(WorkspaceArtworkKind.None, null, null, "No custom artwork");

    public WorkspaceArtworkPresentation Validate()
    {
        if (!Enum.IsDefined(Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(Kind));
        }

        if (Kind == WorkspaceArtworkKind.Site &&
            (SiteAddress is not { IsAbsoluteUri: true } || SiteAddress.Scheme is not ("http" or "https")))
        {
            throw new ArgumentException("Site artwork must reference an HTTP(S) site in the workspace.");
        }
        if (Kind == WorkspaceArtworkKind.LocalImage && string.IsNullOrWhiteSpace(LocalAssetId))
        {
            throw new ArgumentException("Local artwork requires an opaque host asset ID.");
        }

        return this with
        {
            SiteAddress = Kind == WorkspaceArtworkKind.Site ? SiteAddress : null,
            LocalAssetId = Kind == WorkspaceArtworkKind.LocalImage ? LocalAssetId!.Trim() : null,
            AccessibleDescription = string.IsNullOrWhiteSpace(AccessibleDescription)
                ? Kind switch
                {
                    WorkspaceArtworkKind.Site => "Artwork from a site in this workspace",
                    WorkspaceArtworkKind.LocalImage => "Local workspace artwork",
                    _ => "No custom artwork",
                }
                : AccessibleDescription.Trim(),
        };
    }
}

public sealed record BrowserWorkspacePreferences(
    TabStripPlacement TabStripPlacement,
    bool CollapseToActive,
    bool ShowOrbitalGroupPreview)
{
    public static BrowserWorkspacePreferences Default { get; } =
        new(TabStripPlacement.Top, false, true);

    public NewTabVisualMode NewTabMode { get; init; } = NewTabVisualMode.Stellar;

    public WorkspacePreviewRevealMode WorkspacePreviewReveal { get; init; } = WorkspacePreviewRevealMode.Hover;

    public AffiliatedRailPlacement AffiliatedRailPlacement { get; init; } = AffiliatedRailPlacement.Left;

    public bool ShowAffiliatedRail { get; init; } = true;

    public double SideTabPanelWidth { get; init; } = DefaultSideTabPanelWidth;

    public const double MinimumSideTabPanelWidth = 208;

    public const double DefaultSideTabPanelWidth = 224;

    public const double MaximumSideTabPanelWidth = 480;

    public BrowserWorkspacePreferences Validate()
    {
        if (!Enum.IsDefined(TabStripPlacement) || !Enum.IsDefined(NewTabMode) ||
            !Enum.IsDefined(WorkspacePreviewReveal) || !Enum.IsDefined(AffiliatedRailPlacement) ||
            !double.IsFinite(SideTabPanelWidth) ||
            SideTabPanelWidth is < MinimumSideTabPanelWidth or > MaximumSideTabPanelWidth)
        {
            throw new ArgumentOutOfRangeException(nameof(TabStripPlacement));
        }

        return this;
    }
}

public sealed class WorkspacePreferencesChangedEventArgs : EventArgs
{
    public WorkspacePreferencesChangedEventArgs(BrowserWorkspacePreferences preferences)
    {
        Preferences = (preferences ?? throw new ArgumentNullException(nameof(preferences))).Validate();
    }

    public BrowserWorkspacePreferences Preferences { get; }
}

public readonly record struct WorkspacePresetPresentationId(ProfileId ProfileId, Guid Value)
{
    public bool IsEmpty => ProfileId.IsEmpty || Value == Guid.Empty;
}

public sealed record WorkspacePresetTabPresentation(Uri Target, string DisplayTitle)
{
    public string? Note { get; init; }

    public ReadOnlyMemory<byte> FaviconPng { get; init; }

    public WorkspacePresetTabPresentation Validate()
    {
        ArgumentNullException.ThrowIfNull(Target);
        if (Target.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("Workspace tabs must use HTTP or HTTPS.", nameof(Target));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(DisplayTitle);
        return this with
        {
            DisplayTitle = DisplayTitle.Trim(),
            Note = NormalizeOptional(Note, 500),
            FaviconPng = TabVisualMetadataPresentation.IsSafePng(FaviconPng.Span)
                ? FaviconPng.ToArray()
                : ReadOnlyMemory<byte>.Empty,
        };
    }

    private static string? NormalizeOptional(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, maximum)];
}

public sealed record WorkspacePresetPresentation(
    WorkspacePresetPresentationId Id,
    string Name,
    string? GroupName,
    IReadOnlyList<WorkspacePresetTabPresentation> Tabs)
{
    public string ColorToken { get; init; } = "SeaGlass";

    public string? Note { get; init; }

    public WorkspaceArtworkPresentation Artwork { get; init; } = WorkspaceArtworkPresentation.None;

    public int FirstTabIndex { get; init; }

    public WorkspacePresetPresentation Validate()
    {
        if (Id.IsEmpty)
        {
            throw new ArgumentException("A workspace preset ID is required.", nameof(Id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        ArgumentNullException.ThrowIfNull(Tabs);
        if (Tabs.Count == 0 || Tabs.Count > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(Tabs), "A workspace must contain 1 to 64 tabs.");
        }

        foreach (var tab in Tabs)
        {
            tab.Validate();
        }
        if (FirstTabIndex < 0 || FirstTabIndex >= Tabs.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(FirstTabIndex));
        }
        if (Artwork?.Kind == WorkspaceArtworkKind.Site &&
            !Tabs.Any(tab => tab.Target == Artwork.SiteAddress))
        {
            throw new ArgumentException("Workspace site artwork must come from a site inside the workspace.");
        }

        _ = (Artwork ?? WorkspaceArtworkPresentation.None).Validate();
        _ = WorkspaceColorCatalog.Normalize(ColorToken);
        return this;
    }
}

public sealed record WorkspacePresetDraftPresentation(
    string Name,
    string? GroupName,
    IReadOnlyList<WorkspacePresetTabPresentation> Tabs)
{
    public string ColorToken { get; init; } = "SeaGlass";
    public string? Note { get; init; }
    public WorkspaceArtworkPresentation Artwork { get; init; } = WorkspaceArtworkPresentation.None;
    public int FirstTabIndex { get; init; }

    public WorkspacePresetDraftPresentation Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        ArgumentNullException.ThrowIfNull(Tabs);
        if (Name.Trim().Length > 80 || Tabs.Count is < 1 or > 64 ||
            FirstTabIndex < 0 || FirstTabIndex >= Tabs.Count)
        {
            throw new ArgumentException("A workspace draft requires a name, 1 to 64 tabs, and a valid first site.");
        }
        foreach (var tab in Tabs) _ = tab.Validate();
        if (!string.IsNullOrWhiteSpace(Note) && Note.Trim().Length > 1000)
        {
            throw new ArgumentException("Workspace notes are limited to 1000 characters.", nameof(Note));
        }
        var artwork = (Artwork ?? WorkspaceArtworkPresentation.None).Validate();
        if (artwork.Kind == WorkspaceArtworkKind.Site &&
            !Tabs.Any(tab => tab.Target == artwork.SiteAddress))
        {
            throw new ArgumentException("Workspace site artwork must come from a site in the workspace.");
        }
        _ = WorkspaceColorCatalog.Normalize(ColorToken);
        return this;
    }
}

public static class WorkspaceColorCatalog
{
    public static IReadOnlyList<string> Tokens { get; } =
        ["SeaGlass", "Gold", "Violet", "Scarlet", "Azure", "Slate"];

    public static string Normalize(string? value) =>
        Tokens.FirstOrDefault(token => token.Equals(value, StringComparison.OrdinalIgnoreCase)) ?? "SeaGlass";
}

public sealed record NewTabWorkspaceData(
    IReadOnlyList<BookmarkEntry> Bookmarks,
    IReadOnlyList<WorkspacePresetPresentation> Presets,
    bool CanConfigure)
{
    public static NewTabWorkspaceData Empty { get; } = new([], [], true);

    /// <summary>Optional profile-local notes. Bookmark remains one saved site.</summary>
    public IReadOnlyDictionary<BookmarkId, string> BookmarkNotes { get; init; } =
        new Dictionary<BookmarkId, string>();

    /// <summary>Optional host-supplied safe PNG favicon bytes; no remote fetch occurs in Presentation.</summary>
    public IReadOnlyDictionary<BookmarkId, ReadOnlyMemory<byte>> BookmarkFavicons { get; init; } =
        new Dictionary<BookmarkId, ReadOnlyMemory<byte>>();

    public NewTabWorkspaceData Validate()
    {
        ArgumentNullException.ThrowIfNull(Bookmarks);
        ArgumentNullException.ThrowIfNull(Presets);
        foreach (var bookmark in Bookmarks)
        {
            ArgumentNullException.ThrowIfNull(bookmark);
            if (bookmark.Id.IsEmpty || bookmark.Target is null)
            {
                throw new ArgumentException("Every bookmark must be valid.", nameof(Bookmarks));
            }
        }

        foreach (var preset in Presets)
        {
            preset.Validate();
        }

        foreach (var (bookmarkId, note) in BookmarkNotes)
        {
            if (bookmarkId.IsEmpty || string.IsNullOrWhiteSpace(note) || note.Trim().Length > 500)
            {
                throw new ArgumentException("Bookmark notes require a valid bookmark and 1 to 500 characters.", nameof(BookmarkNotes));
            }
        }
        foreach (var (bookmarkId, favicon) in BookmarkFavicons)
        {
            if (bookmarkId.IsEmpty || !TabVisualMetadataPresentation.IsSafePng(favicon.Span))
            {
                throw new ArgumentException("Bookmark favicons must be safe PNG data for a valid bookmark.", nameof(BookmarkFavicons));
            }
        }

        return this;
    }
}
