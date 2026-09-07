using OrbitNavigator.ClipboardShelf.Presentation;
using OrbitNavigator.Contracts.Privacy;
using OrbitNavigator.Contracts.UX;
using OrbitNavigator.Presentation.Permissions;
using OrbitNavigator.Presentation.Protection;
using OrbitNavigator.Presentation.Reading;
using OrbitNavigator.Presentation.Shell;
using OrbitNavigator.Presentation.Tabs;

namespace OrbitNavigator.App.Composition;

/// <summary>
/// Foundation-owned composition point for UX-owned, framework-neutral
/// presenters. It creates no styles or controls and implements no policy.
/// </summary>
public sealed class UxComposition
{
    public ShellSurfacePresenter Shell { get; } = new();

    public TabGroupPresentationCatalog TabGroups { get; } = new();

    public PermissionPromptPresenter CreatePermissionPrompts(IPermissionBroker broker) =>
        new(broker);

    public SiteProtectionPresenter CreateSiteProtection(ISiteProtectionController controller) =>
        new(controller);

    public ReadingPreferencesPresenter CreateReadingPreferences(
        IReadingPreferencesController controller) =>
        new(controller);

    public ClipboardShelfPresenter CreateClipboardShelf(IClipboardShelfController controller) =>
        new(controller);
}
