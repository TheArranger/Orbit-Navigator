#if ORBIT_WPF
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

using OrbitNavigator.Presentation.Workspace;

namespace OrbitNavigator.Presentation.Wpf;

/// <summary>Presentation-owned dark editor; persistence remains with App/Foundation.</summary>
public sealed class WorkspacePresetEditorDialog : Window
{
    private readonly TextBox nameBox = new();
    private readonly TextBox groupBox = new();
    private readonly StackPanel rows = new();
    private readonly TextBlock validation = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly Button addButton = new() { Content = "Add another tab" };
    private readonly Button saveButton = new() { Content = "Save workspace", IsDefault = true, MinWidth = 126 };
    private readonly Button cancelButton = new() { Content = "Cancel", IsCancel = true, MinWidth = 92 };
    private readonly bool canModify;

    public WorkspacePresetEditorDialog(WorkspacePresetPresentation? existing, bool canModify = true)
    {
        this.canModify = canModify;
        Title = existing is null ? "Create workspace — Orbit Navigator" : "Edit workspace — Orbit Navigator";
        Width = 760;
        Height = 680;
        MinWidth = 620;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = OrbitVisualTheme.Canvas;
        Foreground = OrbitVisualTheme.Ink;
        nameBox.Text = existing?.Name ?? string.Empty;
        groupBox.Text = existing?.GroupName ?? string.Empty;
        BuildLayout(existing);
        ApplyTheme();
        ContentRendered += (_, _) => nameBox.Focus();
    }

    public WorkspacePresetDraftPresentation? Draft { get; private set; }

    private void BuildLayout(WorkspacePresetPresentation? existing)
    {
        var content = new StackPanel { Margin = new Thickness(30, 26, 30, 30) };
        var markRow = new StackPanel { Orientation = Orientation.Horizontal };
        markRow.Children.Add(new OrbitNavigatorMark { Width = 48, Height = 48, Margin = new Thickness(0, 0, 14, 0) });
        var headingStack = new StackPanel();
        var heading = new TextBlock
        {
            Text = existing is null ? "Create a saved workspace" : "Edit saved workspace",
            FontSize = 25,
            FontWeight = FontWeights.SemiBold,
        };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level1);
        headingStack.Children.Add(heading);
        headingStack.Children.Add(new TextBlock
        {
            Text = "Name the workspace, then add each web address and a useful tab nickname.",
            Foreground = OrbitVisualTheme.MutedInk,
            Margin = new Thickness(0, 4, 0, 0),
        });
        markRow.Children.Add(headingStack);
        content.Children.Add(markRow);
        content.Children.Add(Label("Workspace name", "Required, 1 to 80 characters"));
        SetupTextBox(nameBox, "Workspace name");
        content.Children.Add(nameBox);
        content.Children.Add(Label("Tab group name", "Optional, up to 60 characters"));
        SetupTextBox(groupBox, "Optional tab group name");
        content.Children.Add(groupBox);
        var tabsHeading = new TextBlock
        {
            Text = "Tabs",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 22, 0, 8),
        };
        AutomationProperties.SetHeadingLevel(tabsHeading, AutomationHeadingLevel.Level2);
        content.Children.Add(tabsHeading);
        content.Children.Add(rows);
        addButton.Margin = new Thickness(0, 10, 0, 0);
        addButton.HorizontalAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetName(addButton, "Add another workspace tab");
        addButton.Click += (_, _) => AddRow(null);
        content.Children.Add(addButton);
        validation.Margin = new Thickness(0, 14, 0, 0);
        AutomationProperties.SetName(validation, "Workspace validation message");
        AutomationProperties.SetLiveSetting(validation, AutomationLiveSetting.Assertive);
        content.Children.Add(validation);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 22, 0, 0),
        };
        cancelButton.Margin = new Thickness(0, 0, 8, 0);
        AutomationProperties.SetName(cancelButton, "Cancel workspace editing");
        AutomationProperties.SetName(saveButton, "Save workspace");
        saveButton.Click += (_, _) => Save();
        actions.Children.Add(cancelButton);
        actions.Children.Add(saveButton);
        content.Children.Add(actions);
        var scroll = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Content = scroll;

        if (existing?.Tabs.Count > 0)
            foreach (var tab in existing.Tabs) AddRow(tab);
        else AddRow(null);
        SetCanModify();
    }

    private void AddRow(WorkspacePresetTabPresentation? existing)
    {
        if (rows.Children.Count >= 32) return;
        WorkspaceTabRow? row = null;
        row = new WorkspaceTabRow(existing, () =>
        {
            if (rows.Children.Count <= 1) return;
            var currentRow = row!;
            var focusIndex = rows.Children.IndexOf(currentRow);
            rows.Children.Remove(currentRow);
            UpdateRemoveButtons();
            if (rows.Children.Count > 0)
                ((WorkspaceTabRow)rows.Children[Math.Min(focusIndex, rows.Children.Count - 1)]).FocusAddress();
        });
        rows.Children.Add(row);
        UpdateRemoveButtons();
        if (existing is null && rows.Children.Count > 1) row.FocusAddress();
    }

    private void UpdateRemoveButtons()
    {
        foreach (WorkspaceTabRow row in rows.Children)
            row.SetRemoveEnabled(canModify && rows.Children.Count > 1);
    }

    private void Save()
    {
        if (!canModify) return;
        var name = nameBox.Text.Trim();
        if (name.Length is < 1 or > 80) { Fail("Enter a workspace name of 1 to 80 characters.", nameBox); return; }
        var group = string.IsNullOrWhiteSpace(groupBox.Text) ? null : groupBox.Text.Trim();
        if (group?.Length > 60) { Fail("The group name must be 60 characters or fewer.", groupBox); return; }
        var tabs = new List<WorkspacePresetTabPresentation>();
        foreach (WorkspaceTabRow row in rows.Children)
        {
            if (!row.TryBuild(out var tab, out var message, out var field))
            {
                Fail(message!, field!);
                return;
            }
            tabs.Add(tab!);
        }
        Draft = new WorkspacePresetDraftPresentation(name, group, tabs);
        DialogResult = true;
    }

    private void Fail(string message, Control field)
    {
        validation.Text = message;
        validation.Foreground = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.Danger;
        validation.Visibility = Visibility.Visible;
        field.Focus();
    }

    private void SetCanModify()
    {
        nameBox.IsReadOnly = !canModify;
        groupBox.IsReadOnly = !canModify;
        addButton.IsEnabled = canModify;
        saveButton.IsEnabled = canModify;
        foreach (WorkspaceTabRow row in rows.Children) row.SetCanModify(canModify);
        if (!canModify)
        {
            validation.Text = "Workspace changes are unavailable in private browsing.";
            validation.Visibility = Visibility.Visible;
        }
    }

    private void ApplyTheme()
    {
        OrbitVisualTheme.ApplyTextBox(nameBox);
        OrbitVisualTheme.ApplyTextBox(groupBox);
        OrbitVisualTheme.ApplyButton(addButton, OrbitButtonRole.Quiet);
        OrbitVisualTheme.ApplyButton(cancelButton, OrbitButtonRole.Quiet);
        OrbitVisualTheme.ApplyButton(saveButton, OrbitButtonRole.Primary);
    }

    private static TextBlock Label(string label, string hint) => new()
    {
        Text = $"{label}  •  {hint}",
        FontWeight = FontWeights.SemiBold,
        Foreground = OrbitVisualTheme.MutedInk,
        Margin = new Thickness(0, 16, 0, 5),
    };

    private static void SetupTextBox(TextBox box, string name)
    {
        box.MinHeight = 42;
        AutomationProperties.SetName(box, name);
    }

    private sealed class WorkspaceTabRow : Border
    {
        private readonly TextBox address = new();
        private readonly TextBox nickname = new();
        private readonly Button remove = new() { Content = "Remove", MinWidth = 78 };

        public WorkspaceTabRow(WorkspacePresetTabPresentation? existing, Action removeAction)
        {
            Background = OrbitVisualTheme.Surface;
            BorderBrush = OrbitVisualTheme.Divider;
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(10);
            Padding = new Thickness(12);
            Margin = new Thickness(0, 0, 0, 8);
            address.Text = existing?.Target.AbsoluteUri ?? string.Empty;
            nickname.Text = existing?.DisplayTitle ?? string.Empty;
            AutomationProperties.SetName(address, "Tab address or URL");
            AutomationProperties.SetName(nickname, "Tab nickname");
            AutomationProperties.SetName(remove, "Remove this workspace tab");
            OrbitVisualTheme.ApplyTextBox(address);
            OrbitVisualTheme.ApplyTextBox(nickname);
            OrbitVisualTheme.ApplyButton(remove, OrbitButtonRole.Quiet);
            remove.Click += (_, _) => removeAction();
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            address.Margin = new Thickness(0, 0, 8, 0);
            nickname.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(nickname, 1);
            Grid.SetColumn(remove, 2);
            grid.Children.Add(address);
            grid.Children.Add(nickname);
            grid.Children.Add(remove);
            Child = grid;
        }

        public void SetRemoveEnabled(bool enabled) => remove.IsEnabled = enabled;
        public void SetCanModify(bool enabled) { address.IsReadOnly = !enabled; nickname.IsReadOnly = !enabled; remove.IsEnabled &= enabled; }
        public void FocusAddress() => address.Focus();

        public bool TryBuild(out WorkspacePresetTabPresentation? tab, out string? message, out Control? field)
        {
            var raw = address.Text.Trim();
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
            { tab = null; message = "Enter a valid HTTP(S) address for every tab."; field = address; return false; }
            var title = nickname.Text.Trim();
            if (title.Length > 100) { tab = null; message = "Tab nicknames must be 100 characters or fewer."; field = nickname; return false; }
            tab = new WorkspacePresetTabPresentation(uri, string.IsNullOrWhiteSpace(title) ? uri.Host : title);
            message = null; field = null; return true;
        }
    }
}
#endif
