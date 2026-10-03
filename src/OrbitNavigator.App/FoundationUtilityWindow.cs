using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using OrbitNavigator.ClipboardShelf.Presentation;
using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Privacy;
using OrbitNavigator.Foundation.Browser;

namespace OrbitNavigator.App;

/// <summary>
/// App-owned functional fallback surfaces for data facades. Presentation owns
/// the browser chrome; these windows keep advertised menu commands actionable.
/// </summary>
public sealed class FoundationUtilityWindow : Window
{
    private readonly PrivacyContext _context;
    private readonly IHistoryFacade _history;
    private readonly IDownloadsFacade _downloads;
    private readonly ClipboardShelfPresenter _clipboard;
    private readonly Func<Uri, Task> _openTarget;
    private readonly ListBox _items = new() { MinHeight = 280 };
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _downloadStatus = new()
    {
        Margin = new Thickness(0, 8, 0, 0),
        TextWrapping = TextWrapping.Wrap,
    };
    private bool _downloadsSubscribed;

    public FoundationUtilityWindow(
        PrivacyContext context,
        IHistoryFacade history,
        IDownloadsFacade downloads,
        ClipboardShelfPresenter clipboard,
        Func<Uri, Task> openTarget)
    {
        _context = context;
        _history = history;
        _downloads = downloads ?? throw new ArgumentNullException(nameof(downloads));
        _clipboard = clipboard;
        _openTarget = openTarget;
        Width = 620;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = OrbitProgramIdentity.CreateWindowIcon();
    }

    public async Task ShowHistoryAsync()
    {
        Title = "History — Orbit Navigator";
        var result = await _history.QueryAsync(new(_context, null, null, 500));
        var entries = result.IsSuccess ? result.Value! : [];
        _items.ItemsSource = entries;
        _items.DisplayMemberPath = nameof(HistoryEntry.Title);
        BuildListSurface(
            "History",
            _context.IsPrivate ? "History is unavailable in private windows." :
                entries.Count == 0 ? "No history yet." : "Recent local history",
            ("Open", async () =>
            {
                if (_items.SelectedItem is HistoryEntry item) await _openTarget(item.Target);
            }),
            ("Clear", async () =>
            {
                if (_context.IsPrivate) return;
                if (MessageBox.Show(this, "Clear all local history?", "Orbit Navigator",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                await _history.ClearAsync(new(_context, null, null, true));
                await ShowHistoryAsync();
            }));
    }

    public void ShowClipboardShelf()
    {
        Title = "Clipboard Shelf — Orbit Navigator";
        var state = _clipboard.State;
        _items.ItemsSource = state.Items;
        _items.DisplayMemberPath = nameof(ClipboardShelfItemSummary.DisplayPreview);
        BuildListSurface(
            "Clipboard Shelf",
            state.IsAvailable ? "Local items available to use in the current tab." :
                "Clipboard Shelf is unavailable in this window.",
            ("Use", async () =>
            {
                if (_items.SelectedItem is ClipboardShelfItemSummary item) await _clipboard.UseAsync(item.Id);
            }),
            ("Clear", async () =>
            {
                _clipboard.RequestClear();
                await _clipboard.ConfirmClearAsync();
                ShowClipboardShelf();
            }));
    }

    public async Task ShowDownloadsAsync()
    {
        Title = "Downloads — Orbit Navigator";
        if (!_downloadsSubscribed)
        {
            _downloads.Changed += OnDownloadsChanged;
            Closed += (_, _) => _downloads.Changed -= OnDownloadsChanged;
            _downloadsSubscribed = true;
        }

        var result = await _downloads.QueryAsync(new(_context, DownloadsFacade.MaximumRecords));
        var selectedId = (_items.SelectedItem as DownloadListItem)?.Record.Id;
        var entries = result.IsSuccess
            ? result.Value!.Select(record => new DownloadListItem(record)).ToArray()
            : [];
        _items.ItemsSource = entries;
        _items.DisplayMemberPath = nameof(DownloadListItem.DisplayText);
        AutomationProperties.SetName(_items, "Downloads list");
        if (selectedId is { } id)
        {
            _items.SelectedItem = entries.SingleOrDefault(item => item.Record.Id == id);
        }
        BuildListSurface(
            "Downloads",
            result.IsSuccess
                ? _context.IsPrivate
                    ? "Downloads from this private session stay isolated and the list is discarded when the session ends."
                    : entries.Length == 0
                        ? "No downloads yet."
                        : "Downloaded files stay on disk when you clear an item from this list."
                : "Downloads could not be loaded.",
            ("Open file", OpenSelectedDownloadAsync),
            ("Cancel", CancelSelectedDownloadAsync),
            ("Clear record", ClearSelectedDownloadAsync),
            ("Refresh", ShowDownloadsAsync));
        var panel = Content as StackPanel;
        panel?.Children.Add(_downloadStatus);
    }

    private async Task OpenSelectedDownloadAsync()
    {
        if (_items.SelectedItem is not DownloadListItem selected) return;
        var opened = await _downloads.OpenFileAsync(new(_context, selected.Record.Id));
        _downloadStatus.Text = opened.IsSuccess
            ? "Opened the downloaded file."
            : "That downloaded file is unavailable or no longer exists.";
    }

    private async Task CancelSelectedDownloadAsync()
    {
        if (_items.SelectedItem is not DownloadListItem selected) return;
        var cancelled = await _downloads.CancelAsync(new(_context, selected.Record.Id));
        _downloadStatus.Text = cancelled.IsSuccess
            ? "Cancellation requested."
            : "That download is no longer active.";
    }

    private async Task ClearSelectedDownloadAsync()
    {
        if (_items.SelectedItem is not DownloadListItem selected) return;
        if (MessageBox.Show(
                this,
                "Remove this item from the downloads list? The downloaded file will be preserved.",
                "Orbit Navigator",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        var cleared = await _downloads.ClearRecordsAsync(new(
            _context,
            new HashSet<DownloadRecordId> { selected.Record.Id },
            true));
        _downloadStatus.Text = cleared.IsSuccess
            ? "Download record cleared. The downloaded file was preserved."
            : "That download record could not be cleared.";
        await ShowDownloadsAsync();
    }

    private void OnDownloadsChanged(object? sender, EventArgs args)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnDownloadsChanged(sender, args));
            return;
        }
        _ = ShowDownloadsAsync();
    }

    private void BuildListSurface(
        string heading,
        string description,
        params (string Label, Func<Task> Execute)[] actions)
    {
        var panel = BasePanel(heading, description);
        panel.Children.Add(_items);
        _actions.Children.Clear();
        foreach (var action in actions)
        {
            var button = new Button
            {
                Content = action.Label,
                MinWidth = 88,
                Margin = new Thickness(0, 12, 8, 0),
            };
            button.Click += async (_, _) => await action.Execute();
            _actions.Children.Add(button);
        }
        panel.Children.Add(_actions);
        Content = panel;
    }

    private sealed record DownloadListItem(DownloadRecord Record)
    {
        public string DisplayText
        {
            get
            {
                var progress = Record.TotalBytes is > 0
                    ? $" — {Math.Clamp((int)(Record.ReceivedBytes * 100d / Record.TotalBytes.Value), 0, 100)}%"
                    : Record.ReceivedBytes > 0
                        ? $" — {FormatBytes(Record.ReceivedBytes)} received"
                        : string.Empty;
                var state = Record.State switch
                {
                    DownloadLifecycleState.InProgress => "Downloading",
                    DownloadLifecycleState.Completed => "Complete",
                    DownloadLifecycleState.Cancelled => "Cancelled",
                    DownloadLifecycleState.Failed => "Interrupted",
                    _ => "Unknown",
                };
                return $"{Record.FileName} — {state}{progress} — from {Record.Source.IdnHost}";
            }
        }

        private static string FormatBytes(long bytes) => bytes switch
        {
            >= 1024L * 1024L * 1024L => $"{bytes / (1024d * 1024d * 1024d):0.##} GB",
            >= 1024L * 1024L => $"{bytes / (1024d * 1024d):0.##} MB",
            >= 1024L => $"{bytes / 1024d:0.##} KB",
            _ => $"{bytes} bytes",
        };
    }

    private static StackPanel BasePanel(string heading, string description)
    {
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = heading,
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = description,
            Margin = new Thickness(0, 6, 0, 14),
            TextWrapping = TextWrapping.Wrap,
        });
        return panel;
    }
}
