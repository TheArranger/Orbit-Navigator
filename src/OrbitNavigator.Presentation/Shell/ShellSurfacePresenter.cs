using OrbitNavigator.Presentation.Common;

namespace OrbitNavigator.Presentation.Shell;

public enum UtilityDrawerKind
{
    None = 0,
    Bookmarks = 1,
    History = 2,
    Downloads = 3,
    ClipboardShelf = 4,
}

public enum InternalPageKind
{
    None = 0,
    NewTab = 1,
    Settings = 2,
}

public sealed record ShellSurfaceState(
    UtilityDrawerKind ActiveDrawer,
    InternalPageKind ActiveInternalPage,
    bool IsPrivate,
    string? RequestedFocusKey,
    PresentationAnnouncement? Announcement)
{
    public static ShellSurfaceState Default { get; } = new(
        UtilityDrawerKind.None,
        InternalPageKind.None,
        false,
        null,
        null);
}

public sealed class ShellSurfacePresenter : PresentationStateSource<ShellSurfaceState>
{
    public ShellSurfacePresenter()
        : base(ShellSurfaceState.Default)
    {
    }

    public void ToggleDrawer(UtilityDrawerKind drawer)
    {
        if (!Enum.IsDefined(drawer) || drawer == UtilityDrawerKind.None)
        {
            throw new ArgumentOutOfRangeException(nameof(drawer));
        }

        var current = State;
        var nextDrawer = current.ActiveDrawer == drawer
            ? UtilityDrawerKind.None
            : drawer;
        Publish(current with
        {
            ActiveDrawer = nextDrawer,
            ActiveInternalPage = InternalPageKind.None,
            RequestedFocusKey = nextDrawer == UtilityDrawerKind.None
                ? "shell.toolbar"
                : $"drawer.{nextDrawer.ToString().ToLowerInvariant()}",
            Announcement = new PresentationAnnouncement(
                nextDrawer == UtilityDrawerKind.None
                    ? "ui.drawer.closed"
                    : "ui.drawer.opened"),
        });
    }

    public void OpenInternalPage(InternalPageKind page)
    {
        if (!Enum.IsDefined(page) || page == InternalPageKind.None)
        {
            throw new ArgumentOutOfRangeException(nameof(page));
        }

        Publish(State with
        {
            ActiveDrawer = UtilityDrawerKind.None,
            ActiveInternalPage = page,
            RequestedFocusKey = $"page.{page.ToString().ToLowerInvariant()}.heading",
            Announcement = new PresentationAnnouncement("ui.internal_page.opened"),
        });
    }

    public void ReturnToWebContent()
    {
        Publish(State with
        {
            ActiveDrawer = UtilityDrawerKind.None,
            ActiveInternalPage = InternalPageKind.None,
            RequestedFocusKey = "webview",
            Announcement = new PresentationAnnouncement("ui.web_content.focused"),
        });
    }

    public void SetPrivateMode(bool isPrivate)
    {
        if (State.IsPrivate == isPrivate)
        {
            return;
        }

        Publish(State with
        {
            IsPrivate = isPrivate,
            Announcement = new PresentationAnnouncement(
                isPrivate ? "ui.private.entered" : "ui.private.exited",
                AnnouncementPriority.Assertive),
        });
    }
}

public sealed record DonationPlacementPolicy(
    bool ShowInSettings,
    bool ShowOnNewTab,
    string LabelKey)
{
    public static DonationPlacementPolicy Create(bool showOnNewTab) =>
        new(true, showOnNewTab, "ui.donation.support_orbit");
}
