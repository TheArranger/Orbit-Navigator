using OrbitNavigator.Presentation.Accessibility;
using OrbitNavigator.Presentation.Shell;
using OrbitNavigator.Presentation.Tabs;
using OrbitNavigator.Contracts.Browser;
using Xunit;

namespace OrbitNavigator.Presentation.Tests;

public sealed class AccessibilityAndShellTests
{
    [Fact]
    public void GroupHeaderExposesKeyboardFocusableExpandedStateAndCount()
    {
        var header = new TabGroupHeaderEntry(
            new BrowserTabGroupId(Guid.NewGuid()),
            "Research",
            4,
            true,
            false);

        var automation = TabGroupAutomationState.From(header);

        Assert.Equal("Research", automation.Name);
        Assert.Equal(4, automation.TabCount);
        Assert.True(automation.IsKeyboardFocusable);
        Assert.Equal(AutomationExpandCollapseState.Collapsed, automation.ExpandCollapseState);
        Assert.Equal("ui.tabs.group.expand", automation.ToggleActionMessageKey);
        Assert.Equal("Shift+F10", KeyboardInteractionCatalog.ContextMenu);
    }

    [Fact]
    public void UtilityDrawersAreMutuallyExclusiveAndRestoreNamedFocusTargets()
    {
        var presenter = new ShellSurfacePresenter();

        presenter.ToggleDrawer(UtilityDrawerKind.Bookmarks);
        Assert.Equal(UtilityDrawerKind.Bookmarks, presenter.State.ActiveDrawer);
        Assert.Equal("drawer.bookmarks", presenter.State.RequestedFocusKey);
        presenter.ToggleDrawer(UtilityDrawerKind.History);
        Assert.Equal(UtilityDrawerKind.History, presenter.State.ActiveDrawer);
        presenter.OpenInternalPage(InternalPageKind.Settings);

        Assert.Equal(UtilityDrawerKind.None, presenter.State.ActiveDrawer);
        Assert.Equal(InternalPageKind.Settings, presenter.State.ActiveInternalPage);
        Assert.Equal("page.settings.heading", presenter.State.RequestedFocusKey);
    }

    [Fact]
    public void DonationIsAlwaysInSettingsAndOptionalOnNewTab()
    {
        var settingsOnly = DonationPlacementPolicy.Create(false);
        var withNewTab = DonationPlacementPolicy.Create(true);

        Assert.True(settingsOnly.ShowInSettings);
        Assert.False(settingsOnly.ShowOnNewTab);
        Assert.True(withNewTab.ShowInSettings);
        Assert.True(withNewTab.ShowOnNewTab);
    }

    [Fact]
    public void NewTabArtworkIsOptionalAndNeverRequiredForHighContrast()
    {
        Assert.True(NewTabDecorationPolicy.ShouldShow(true, false, false));
        Assert.False(NewTabDecorationPolicy.ShouldShow(true, true, false));
        Assert.False(NewTabDecorationPolicy.ShouldShow(true, false, true));
        Assert.False(NewTabDecorationPolicy.ShouldShow(false, false, false));
    }
}
