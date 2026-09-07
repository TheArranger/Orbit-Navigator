#if ORBIT_WPF
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Presentation.Workspace;

namespace OrbitNavigator.Presentation.Wpf;

public sealed record WorkspaceLocalArtworkImportRequest(
    BrowserTabGroupId GroupId,
    string ProposedWorkspaceName);

public sealed class TabGroupCloseConfirmationDialog : Window
{
    public TabGroupCloseConfirmationDialog(string groupName, int tabCount)
    {
        if (tabCount < 2) throw new ArgumentOutOfRangeException(nameof(tabCount));
        Title = "Close tab group — Orbit Navigator";
        Width = 460;
        Height = 250;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = SystemParameters.HighContrast ? SystemColors.WindowBrush : OrbitVisualTheme.Canvas;
        Foreground = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.Ink;
        var root = new StackPanel { Margin = new Thickness(26) };
        var heading = new TextBlock
        {
            Text = $"Close {groupName}?",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
        };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level1);
        root.Children.Add(heading);
        root.Children.Add(new TextBlock
        {
            Text = $"This closes all {tabCount} tabs in the temporary group and discards their in-memory page state.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.MutedInk,
            Margin = new Thickness(0, 10, 0, 22),
        });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 92, MinHeight = 44 };
        var close = new Button { Content = "Close group", IsDefault = true, MinWidth = 116, MinHeight = 44 };
        OrbitVisualTheme.ApplyButton(cancel, OrbitButtonRole.Quiet);
        OrbitVisualTheme.ApplyButton(close, OrbitButtonRole.Primary);
        close.Foreground = SystemParameters.HighContrast ? SystemColors.ControlTextBrush : OrbitVisualTheme.Danger;
        AutomationProperties.SetName(cancel, "Cancel closing tab group");
        AutomationProperties.SetName(close, $"Close all {tabCount} tabs in {groupName}");
        close.Click += (_, _) => DialogResult = true;
        actions.Children.Add(cancel);
        actions.Children.Add(close);
        root.Children.Add(actions);
        Content = root;
    }
}

public sealed class SaveTabGroupWorkspaceDialog : Window
{
    private readonly BrowserTabGroupId groupId;
    private readonly IReadOnlyList<WorkspacePresetTabPresentation> tabs;
    private readonly Func<WorkspaceLocalArtworkImportRequest, WorkspaceArtworkPresentation?>? localArtworkImporter;
    private readonly TextBox name = new();
    private readonly TextBox note = new() { AcceptsReturn = true, Height = 76, TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox color = new() { MinHeight = 42 };
    private readonly ComboBox firstSite = new() { MinHeight = 42 };
    private readonly ComboBox artwork = new() { MinHeight = 42 };
    private readonly TextBlock validation = new() { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
    private WorkspaceArtworkPresentation localArtwork = WorkspaceArtworkPresentation.None;

    public SaveTabGroupWorkspaceDialog(
        BrowserTabGroupId groupId,
        string groupName,
        string initialColor,
        IReadOnlyList<WorkspacePresetTabPresentation> tabs,
        Func<WorkspaceLocalArtworkImportRequest, WorkspaceArtworkPresentation?>? localArtworkImporter = null)
    {
        if (groupId.IsEmpty) throw new ArgumentException("A group ID is required.", nameof(groupId));
        if (tabs is null || tabs.Count == 0) throw new ArgumentException("A workspace requires tabs.", nameof(tabs));
        this.groupId = groupId;
        this.tabs = tabs.Select(tab => tab.Validate()).ToArray();
        this.localArtworkImporter = localArtworkImporter;
        Title = "Save tab group as workspace — Orbit Navigator";
        Width = 620;
        Height = 690;
        MinWidth = 540;
        MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = SystemParameters.HighContrast ? SystemColors.WindowBrush : OrbitVisualTheme.Canvas;
        Foreground = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.Ink;
        name.Text = string.IsNullOrWhiteSpace(groupName) ? "Workspace" : groupName.Trim();
        foreach (var token in WorkspaceColorCatalog.Tokens) color.Items.Add(token);
        color.SelectedItem = WorkspaceColorCatalog.Normalize(initialColor);
        for (var index = 0; index < this.tabs.Count; index++)
        {
            firstSite.Items.Add(new ComboBoxItem { Content = this.tabs[index].DisplayTitle, Tag = index });
        }
        firstSite.SelectedIndex = 0;
        artwork.Items.Add(new ComboBoxItem { Content = "No custom artwork", Tag = WorkspaceArtworkPresentation.None });
        foreach (var tab in this.tabs)
        {
            artwork.Items.Add(new ComboBoxItem
            {
                Content = $"Use {tab.DisplayTitle}",
                Tag = new WorkspaceArtworkPresentation(
                    WorkspaceArtworkKind.Site,
                    tab.Target,
                    null,
                    $"Artwork from {tab.DisplayTitle}"),
            });
        }
        artwork.SelectedIndex = 0;
        BuildLayout();
        ContentRendered += (_, _) => { name.Focus(); name.SelectAll(); };
    }

    public WorkspacePresetDraftPresentation? Draft { get; private set; }

    private void BuildLayout()
    {
        var root = new StackPanel { Margin = new Thickness(28, 24, 28, 28) };
        var heading = new TextBlock { Text = "Save group as a workspace", FontSize = 25, FontWeight = FontWeights.SemiBold };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level1);
        root.Children.Add(heading);
        root.Children.Add(new TextBlock
        {
            Text = "A workspace is a saved tab group. Opening it later adds a new collapsed live group and selects the chosen first site.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.MutedInk,
            Margin = new Thickness(0, 6, 0, 12),
        });
        AddField(root, "Workspace name", name, "Workspace name");
        AddField(root, "Color", color, "Workspace color");
        AddField(root, "Optional note", note, "Workspace note");
        AddField(root, "Site to open first", firstSite, "First workspace site");
        AddField(root, "Artwork", artwork, "Workspace artwork");
        var chooseLocal = new Button
        {
            Content = "Choose local image…",
            MinHeight = 44,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsEnabled = localArtworkImporter is not null,
            Margin = new Thickness(0, 8, 0, 0),
        };
        OrbitVisualTheme.ApplyButton(chooseLocal, OrbitButtonRole.Quiet);
        AutomationProperties.SetName(chooseLocal, "Choose local workspace artwork");
        AutomationProperties.SetHelpText(chooseLocal, localArtworkImporter is null
            ? "Local artwork import is unavailable until the secure host importer is connected."
            : "Choose a local image. The host imports a safe local copy; the original path is not stored in the workspace.");
        chooseLocal.Click += (_, _) =>
        {
            var imported = localArtworkImporter?.Invoke(new(groupId, name.Text.Trim()));
            if (imported is null) return;
            localArtwork = imported.Validate();
            artwork.Items.Add(new ComboBoxItem { Content = "Selected local image", Tag = localArtwork });
            artwork.SelectedIndex = artwork.Items.Count - 1;
        };
        root.Children.Add(chooseLocal);
        validation.Foreground = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.Danger;
        validation.Margin = new Thickness(0, 12, 0, 0);
        AutomationProperties.SetLiveSetting(validation, AutomationLiveSetting.Assertive);
        root.Children.Add(validation);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 92, MinHeight = 44 };
        var save = new Button { Content = "Save workspace", IsDefault = true, MinWidth = 126, MinHeight = 44 };
        OrbitVisualTheme.ApplyButton(cancel, OrbitButtonRole.Quiet);
        OrbitVisualTheme.ApplyButton(save, OrbitButtonRole.Primary);
        save.Click += (_, _) => Save();
        actions.Children.Add(cancel);
        actions.Children.Add(save);
        root.Children.Add(actions);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private void Save()
    {
        var workspaceName = name.Text.Trim();
        if (workspaceName.Length is < 1 or > 80)
        {
            validation.Text = "Enter a workspace name of 1 to 80 characters.";
            validation.Visibility = Visibility.Visible;
            name.Focus();
            return;
        }
        var first = firstSite.SelectedItem is ComboBoxItem { Tag: int index } ? index : 0;
        var art = artwork.SelectedItem is ComboBoxItem { Tag: WorkspaceArtworkPresentation selected }
            ? selected.Validate()
            : WorkspaceArtworkPresentation.None;
        Draft = new WorkspacePresetDraftPresentation(workspaceName, workspaceName, tabs)
        {
            ColorToken = WorkspaceColorCatalog.Normalize(color.SelectedItem?.ToString()),
            Note = string.IsNullOrWhiteSpace(note.Text) ? null : note.Text.Trim(),
            Artwork = art,
            FirstTabIndex = first,
        }.Validate();
        DialogResult = true;
    }

    private static void AddField(Panel root, string label, Control control, string automationName)
    {
        root.Children.Add(new TextBlock
        {
            Text = label,
            FontWeight = FontWeights.SemiBold,
            Foreground = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.MutedInk,
            Margin = new Thickness(0, 12, 0, 5),
        });
        AutomationProperties.SetName(control, automationName);
        switch (control)
        {
            case TextBox box: OrbitVisualTheme.ApplyTextBox(box); break;
            case ComboBox combo:
                combo.Background = SystemParameters.HighContrast ? SystemColors.WindowBrush : OrbitVisualTheme.Surface;
                combo.Foreground = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.Ink;
                break;
        }
        root.Children.Add(control);
    }
}
#endif
