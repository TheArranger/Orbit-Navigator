#if ORBIT_WPF
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

using OrbitNavigator.Presentation.Offline;

namespace OrbitNavigator.Presentation.Wpf;

public sealed class OfflineLibraryControl : Grid
{
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock empty = new()
    {
        Text = "No pages saved for offline reading.",
        TextWrapping = TextWrapping.Wrap,
    };
    private readonly StackPanel rows = new();
    private readonly Button save;
    private OfflineReadingCatalogPresentation catalog =
        OfflineReadingCatalogPresentation.Unavailable(false, "Offline reading is not connected.");

    public OfflineLibraryControl()
    {
        Background = SystemParameters.HighContrast ? SystemColors.WindowBrush : OrbitVisualTheme.Canvas;
        Margin = new Thickness(0);
        AutomationProperties.SetName(this, "Offline library");
        AutomationProperties.SetHelpText(
            this,
            "User-selected local page copies. Saved pages are not live websites and are not synced.");

        save = ActionButton("Save current page for offline", () => Raise(new SavePageForOfflineAction(
            Guid.NewGuid(), catalog.Revision)));
        BuildLayout();
        Apply(catalog);
    }

    public event EventHandler<OfflineReadingActionRequestedEventArgs>? ActionRequested;

    public OfflineReadingCatalogPresentation Catalog => catalog;
    public int VisibleItemCount => rows.Children.Count;

    public void Apply(OfflineReadingCatalogPresentation presentation)
    {
        catalog = (presentation ?? throw new ArgumentNullException(nameof(presentation))).Validate();
        save.IsEnabled = catalog.CanSaveCurrentPage && ActionRequested is not null;
        AutomationProperties.SetHelpText(save, catalog.IsPrivate
            ? "Offline reading is unavailable in private browsing."
            : catalog.CanSaveCurrentPage
                ? "Save a local copy of the current page on this device."
                : catalog.SafeStatusMessage);
        status.Text = catalog.SafeStatusMessage;
        rows.Children.Clear();
        foreach (var item in catalog.Items.OrderByDescending(item => item.SavedAtUtc))
        {
            rows.Children.Add(CreateItem(item));
        }
        empty.Visibility = catalog.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetItemStatus(this, catalog.IsPrivate
            ? "Unavailable in private browsing"
            : $"{catalog.Items.Count} saved page{(catalog.Items.Count == 1 ? string.Empty : "s")}");
    }

    private void BuildLayout()
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid { Margin = new Thickness(20, 18, 20, 12) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var copy = new StackPanel();
        copy.Children.Add(new TextBlock
        {
            Text = "Offline library",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = ForegroundBrush(),
        });
        copy.Children.Add(new TextBlock
        {
            Text = "Saved locally on this device. These copies are not live websites and do not sync.",
            Margin = new Thickness(0, 4, 16, 4),
            TextWrapping = TextWrapping.Wrap,
            Foreground = MutedBrush(),
        });
        status.Foreground = MutedBrush();
        copy.Children.Add(status);
        header.Children.Add(copy);
        Grid.SetColumn(save, 1);
        header.Children.Add(save);
        Children.Add(header);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(20, 0, 20, 20),
            Content = new StackPanel
            {
                Children = { empty, rows },
            },
        };
        AutomationProperties.SetName(scroll, "Saved offline pages");
        Grid.SetRow(scroll, 1);
        Children.Add(scroll);
        empty.Foreground = MutedBrush();
        empty.Margin = new Thickness(4, 18, 4, 18);
    }

    private FrameworkElement CreateItem(OfflineReadingItemPresentation item)
    {
        var card = new Border
        {
            Background = SystemParameters.HighContrast ? SystemColors.ControlBrush : OrbitVisualTheme.Surface,
            BorderBrush = SystemParameters.HighContrast ? SystemColors.ControlTextBrush : OrbitVisualTheme.Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 11, 10, 11),
            Margin = new Thickness(0, 0, 0, 8),
        };
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var copy = new StackPanel();
        copy.Children.Add(new TextBlock
        {
            Text = item.Title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = ForegroundBrush(),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        copy.Children.Add(new TextBlock
        {
            Text = item.SourceAddress.Host,
            Foreground = MutedBrush(),
            Margin = new Thickness(0, 3, 0, 0),
        });
        copy.Children.Add(new TextBlock
        {
            Text = $"Saved {item.SavedAtUtc.ToLocalTime():g}  •  {FormatSize(item.SizeBytes)}  •  Offline copy",
            Foreground = MutedBrush(),
            Margin = new Thickness(0, 3, 0, 0),
        });
        layout.Children.Add(copy);

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var open = ActionButton("Open offline copy", () => Raise(new OpenOfflineReadingItemAction(
            Guid.NewGuid(), catalog.Revision, item.ItemId)));
        var delete = ActionButton("Delete", () => Raise(new DeleteOfflineReadingItemAction(
            Guid.NewGuid(), catalog.Revision, item.ItemId)), destructive: true);
        AutomationProperties.SetName(open, $"Open offline copy — {item.Title}");
        AutomationProperties.SetHelpText(open, "Open the saved local copy. This is not a live website.");
        AutomationProperties.SetName(delete, $"Delete offline copy — {item.Title}");
        AutomationProperties.SetHelpText(delete, "Delete this local saved copy from the device.");
        actions.Children.Add(open);
        actions.Children.Add(delete);
        Grid.SetColumn(actions, 1);
        layout.Children.Add(actions);
        card.Child = layout;
        AutomationProperties.SetName(card,
            $"{item.Title}, saved {item.SavedAtUtc.ToLocalTime():g}, {FormatSize(item.SizeBytes)}, offline copy");
        return card;
    }

    private Button ActionButton(string label, Action action, bool destructive = false)
    {
        var button = new Button
        {
            Content = label,
            MinWidth = 44,
            MinHeight = 44,
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(4, 0, 0, 0),
        };
        OrbitVisualTheme.ApplyButton(button, destructive ? OrbitButtonRole.Quiet : OrbitButtonRole.Toolbar);
        if (destructive && !SystemParameters.HighContrast)
        {
            button.Foreground = OrbitVisualTheme.Danger;
        }
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => action();
        return button;
    }

    private void Raise(OfflineReadingAction action) =>
        ActionRequested?.Invoke(this, new(action));

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes.ToString(CultureInfo.CurrentCulture)} B";
        if (bytes < 1024 * 1024) return $"{(bytes / 1024d).ToString("0.#", CultureInfo.CurrentCulture)} KB";
        return $"{(bytes / (1024d * 1024d)).ToString("0.#", CultureInfo.CurrentCulture)} MB";
    }

    private static Brush ForegroundBrush() =>
        SystemParameters.HighContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.Ink;

    private static Brush MutedBrush() =>
        SystemParameters.HighContrast ? SystemColors.GrayTextBrush : OrbitVisualTheme.MutedInk;
}
#endif
