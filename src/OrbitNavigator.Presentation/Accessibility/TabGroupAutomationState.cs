using OrbitNavigator.Presentation.Tabs;

namespace OrbitNavigator.Presentation.Accessibility;

public enum AutomationExpandCollapseState
{
    Expanded = 0,
    Collapsed = 1,
}

public sealed record TabGroupAutomationState(
    string Name,
    int TabCount,
    AutomationExpandCollapseState ExpandCollapseState,
    bool IsKeyboardFocusable,
    string CountMessageKey,
    string ToggleActionMessageKey)
{
    public static TabGroupAutomationState From(TabGroupHeaderEntry header)
    {
        ArgumentNullException.ThrowIfNull(header);
        return new TabGroupAutomationState(
            header.Name,
            header.TabCount,
            header.IsCollapsed
                ? AutomationExpandCollapseState.Collapsed
                : AutomationExpandCollapseState.Expanded,
            true,
            "ui.tabs.group.tab_count",
            header.IsCollapsed
                ? "ui.tabs.group.expand"
                : "ui.tabs.group.collapse");
    }
}

public static class KeyboardInteractionCatalog
{
    public const string FocusOmnibox = "Ctrl+L";
    public const string NewTab = "Ctrl+T";
    public const string CloseTab = "Ctrl+W";
    public const string ReopenTab = "Ctrl+Shift+T";
    public const string NewPrivateWindow = "Ctrl+Shift+N";
    public const string ContextMenu = "Shift+F10";
}
