#if ORBIT_WPF
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;

namespace OrbitNavigator.Presentation.Wpf;

/// <summary>
/// A tab-group disclosure that exposes the UI Automation ExpandCollapse
/// pattern. Mouse, keyboard, Narrator, and context-menu paths all invoke the
/// same click command supplied by the browser chrome.
/// </summary>
public sealed class TabGroupHeaderButton : Button
{
    public static readonly DependencyProperty GroupNameProperty = DependencyProperty.Register(
        nameof(GroupName),
        typeof(string),
        typeof(TabGroupHeaderButton),
        new FrameworkPropertyMetadata("Tab group", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TabCountProperty = DependencyProperty.Register(
        nameof(TabCount),
        typeof(int),
        typeof(TabGroupHeaderButton),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsCollapsedProperty = DependencyProperty.Register(
        nameof(IsCollapsed),
        typeof(bool),
        typeof(TabGroupHeaderButton),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public string GroupName
    {
        get => (string)GetValue(GroupNameProperty);
        set => SetValue(GroupNameProperty, value);
    }

    public int TabCount
    {
        get => (int)GetValue(TabCountProperty);
        set => SetValue(TabCountProperty, value);
    }

    public bool IsCollapsed
    {
        get => (bool)GetValue(IsCollapsedProperty);
        set => SetValue(IsCollapsedProperty, value);
    }

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new TabGroupHeaderAutomationPeer(this);

    internal void InvokeFromAutomation()
    {
        if (IsEnabled)
        {
            RaiseEvent(new RoutedEventArgs(ClickEvent, this));
        }
    }

    private sealed class TabGroupHeaderAutomationPeer : ButtonAutomationPeer, IExpandCollapseProvider
    {
        private readonly TabGroupHeaderButton owner;

        public TabGroupHeaderAutomationPeer(TabGroupHeaderButton owner)
            : base(owner)
        {
            this.owner = owner;
        }

        public System.Windows.Automation.ExpandCollapseState ExpandCollapseState => owner.IsCollapsed
            ? System.Windows.Automation.ExpandCollapseState.Collapsed
            : System.Windows.Automation.ExpandCollapseState.Expanded;

        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.ExpandCollapse
                ? this
                : base.GetPattern(patternInterface);

        public void Collapse()
        {
            if (!owner.IsCollapsed)
            {
                owner.Dispatcher.Invoke(owner.InvokeFromAutomation);
            }
        }

        public void Expand()
        {
            if (owner.IsCollapsed)
            {
                owner.Dispatcher.Invoke(owner.InvokeFromAutomation);
            }
        }

        protected override string GetNameCore() =>
            $"{owner.GroupName}, {owner.TabCount} tabs";

        protected override string GetHelpTextCore() => owner.IsCollapsed
            ? "Collapsed tab group. Activate to expand."
            : "Expanded tab group. Activate to collapse.";

        protected override string GetItemStatusCore() => owner.IsCollapsed
            ? "Collapsed"
            : "Expanded";
    }
}
#endif
