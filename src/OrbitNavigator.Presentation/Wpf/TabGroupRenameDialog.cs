#if ORBIT_WPF
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Presentation.Tabs;

namespace OrbitNavigator.Presentation.Wpf;

public sealed record TabGroupRenameDraft(BrowserTabGroupId GroupId, string RequestedName);

/// <summary>Presentation-owned prompt; the host receives only a validated rename intent.</summary>
public sealed class TabGroupRenameDialog : Window
{
    private readonly BrowserTabGroupId groupId;
    private readonly string currentName;
    private readonly TextBox nameBox = new();
    private readonly TextBlock validation = new()
    {
        Visibility = Visibility.Collapsed,
        TextWrapping = TextWrapping.Wrap,
    };

    public TabGroupRenameDialog(BrowserTabGroupId groupId, string currentName, bool isPrivate)
    {
        if (groupId.IsEmpty)
        {
            throw new ArgumentException("A group ID is required.", nameof(groupId));
        }

        this.groupId = groupId;
        this.currentName = string.IsNullOrWhiteSpace(currentName) ? "Tab group" : currentName.Trim();
        Title = "Rename tab group — Orbit Navigator";
        Width = 470;
        SizeToContent = SizeToContent.Height;
        MinWidth = 380;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = SystemParameters.HighContrast ? SystemColors.WindowBrush : OrbitVisualTheme.Canvas;
        Foreground = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.Ink;
        Content = BuildContent(isPrivate);
        ContentRendered += (_, _) =>
        {
            nameBox.Focus();
            nameBox.SelectAll();
        };
    }

    public TabGroupRenameDraft? Draft { get; private set; }

    private UIElement BuildContent(bool isPrivate)
    {
        var root = new StackPanel { Margin = new Thickness(26, 24, 26, 24) };
        var heading = new TextBlock
        {
            Text = "Rename tab group",
            FontSize = 23,
            FontWeight = FontWeights.SemiBold,
        };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level1);
        root.Children.Add(heading);
        root.Children.Add(new TextBlock
        {
            Text = isPrivate
                ? "This name applies only to this private session."
                : "Choose a short name that describes these tabs.",
            Foreground = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.MutedInk,
            Margin = new Thickness(0, 4, 0, 16),
        });

        nameBox.Text = currentName;
        nameBox.MaxLength = TabGroupPresentationCatalog.MaximumNameLength;
        nameBox.MinHeight = 44;
        AutomationProperties.SetName(nameBox, "Tab group name");
        AutomationProperties.SetHelpText(nameBox, "Enter 1 to 60 characters.");
        OrbitVisualTheme.ApplyTextBox(nameBox);
        root.Children.Add(nameBox);

        validation.Margin = new Thickness(0, 10, 0, 0);
        validation.Foreground = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.Danger;
        AutomationProperties.SetName(validation, "Tab group name validation");
        AutomationProperties.SetLiveSetting(validation, AutomationLiveSetting.Assertive);
        root.Children.Add(validation);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 92, MinHeight = 44 };
        var rename = new Button { Content = "Rename", IsDefault = true, MinWidth = 104, MinHeight = 44 };
        cancel.Margin = new Thickness(0, 0, 8, 0);
        AutomationProperties.SetName(cancel, "Cancel renaming tab group");
        AutomationProperties.SetName(rename, "Rename tab group");
        OrbitVisualTheme.ApplyButton(cancel, OrbitButtonRole.Quiet);
        OrbitVisualTheme.ApplyButton(rename, OrbitButtonRole.Primary);
        rename.Click += (_, _) => Commit();
        actions.Children.Add(cancel);
        actions.Children.Add(rename);
        root.Children.Add(actions);
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape)
            {
                DialogResult = false;
                args.Handled = true;
            }
        };
        return root;
    }

    private void Commit()
    {
        var requested = nameBox.Text.Trim();
        if (requested.Length is < 1 or > TabGroupPresentationCatalog.MaximumNameLength)
        {
            validation.Text = "Enter a tab group name of 1 to 60 characters.";
            validation.Visibility = Visibility.Visible;
            nameBox.Focus();
            nameBox.SelectAll();
            return;
        }

        if (string.Equals(requested, currentName, StringComparison.Ordinal))
        {
            DialogResult = false;
            return;
        }

        Draft = new(groupId, requested);
        DialogResult = true;
    }
}
#endif
