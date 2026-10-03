using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Foundation.Browser;
using Xunit;

namespace OrbitNavigator.App.Tests;

public sealed class WorkspacePreferencesUpgradeTests
{
    [Theory]
    [InlineData(TabStripPlacement.Top)]
    [InlineData(TabStripPlacement.Left)]
    [InlineData(TabStripPlacement.Right)]
    public void RetiredCondensePreferenceCannotHideInactiveTabsInUpgradedProfiles(TabStripPlacement placement)
    {
        var saved = new WorkspaceUiPreferencesSnapshot(new ProfileId(Guid.NewGuid()),
            new WorkspaceUiPreferencesRevision(Guid.NewGuid()), placement,
            CollapseToActive: true, ShowOrbitalGroupPreview: true, CompactTabs: true,
            SideTabPanelWidth: 320, ShowAddressBar: true);

        var mapped = FoundationWindow.ToPresentationPreferences(saved);

        Assert.False(mapped.CollapseToActive);
        Assert.True(mapped.ShowOrbitalGroupPreview);
        Assert.Equal(320, mapped.SideTabPanelWidth);
        Assert.True(mapped.ShowAddressBar);
        Assert.Equal((int)placement, (int)mapped.TabStripPlacement);
        Assert.True(saved.CollapseToActive); // In-memory compatibility, no destructive profile rewrite.
    }
}
