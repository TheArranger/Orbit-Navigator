using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using OrbitNavigator.ClipboardShelf.Presentation;
using OrbitNavigator.App.Accounts;
using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Privacy;
using OrbitNavigator.Contracts.Updates;
using OrbitNavigator.Foundation.Browser;
using OrbitNavigator.Presentation.Wpf;
using OrbitNavigator.Updates;

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
    private readonly IBrowserSettingsFacade _settings;
    private readonly ClipboardShelfPresenter _clipboard;
    private readonly MyOrbitAccountSettingsAdapter _myOrbitAccountSettings;
    private readonly PrimaryUpdateClient _updates;
    private readonly Func<BrowsingContext?> _accountContext;
    private readonly Func<Uri, Task> _openTarget;
    private readonly ListBox _items = new() { MinHeight = 280 };
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal };
    private BrowserSettingsSnapshot? _settingsSnapshot;
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
        IBrowserSettingsFacade settings,
        ClipboardShelfPresenter clipboard,
        MyOrbitAccountSettingsAdapter myOrbitAccountSettings,
        PrimaryUpdateClient updates,
        Func<BrowsingContext?> accountContext,
        Func<Uri, Task> openTarget)
    {
        _context = context;
        _history = history;
        _downloads = downloads ?? throw new ArgumentNullException(nameof(downloads));
        _settings = settings;
        _clipboard = clipboard;
        _myOrbitAccountSettings = myOrbitAccountSettings;
        _updates = updates ?? throw new ArgumentNullException(nameof(updates));
        _accountContext = accountContext;
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

    public async Task ShowSettingsAsync()
    {
        Title = "Settings — Orbit Navigator";
        var result = await _settings.GetAsync(_context);
        if (!result.IsSuccess)
        {
            Content = BuildMessage("Settings", "Settings could not be loaded.");
            return;
        }
        _settingsSnapshot = result.Value!;
        var values = _settingsSnapshot.Values;
        var restore = new CheckBox
        {
            Content = "Restore open tabs when Orbit Navigator starts",
            IsChecked = values.RestoreOpenTabs,
            Margin = new Thickness(0, 12, 0, 6),
            IsEnabled = !_context.IsPrivate,
        };
        var ask = new CheckBox
        {
            Content = "Ask where to save downloads",
            IsChecked = values.AskWhereToSaveDownloads,
            Margin = new Thickness(0, 6, 0, 6),
            IsEnabled = !_context.IsPrivate,
        };
        var automatic = new CheckBox
        {
            Content = "Check for Orbit Navigator updates automatically",
            IsChecked = true,
            Margin = new Thickness(0, 6, 0, 6),
            IsEnabled = false,
            ToolTip = "Orbit uses a randomized local schedule and sends no identifying update telemetry.",
        };
        var updateDisclosure = new TextBlock
        {
            Text = "Orbit verifies a pinned manifest signature, version sequence, package size, and SHA-256 hash before offering an update. Updates never install silently. Until Windows-trusted signing is available, each installer requires a separate confirmation and Windows may show an unknown-publisher warning.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
        var updateStatus = new TextBlock
        {
            Text = _updates.Snapshot.StatusMessage,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 8),
        };
        var checkUpdate = new Button { Content = "Check for updates", MinWidth = 150, Margin = new Thickness(0, 0, 8, 0) };
        var downloadUpdate = new Button { Content = "Download verified update", MinWidth = 185, Margin = new Thickness(0, 0, 8, 0) };
        var installUpdate = new Button { Content = "Install verified update...", MinWidth = 180 };
        var updateActions = new WrapPanel();
        updateActions.Children.Add(checkUpdate);
        updateActions.Children.Add(downloadUpdate);
        updateActions.Children.Add(installUpdate);

        void RenderUpdate(PrimaryUpdateSnapshot snapshot)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(() => RenderUpdate(snapshot));
                return;
            }
            updateStatus.Text = snapshot.StatusMessage;
            checkUpdate.IsEnabled = !_context.IsPrivate &&
                snapshot.Lifecycle is not PrimaryUpdateLifecycle.Checking and not PrimaryUpdateLifecycle.Downloading;
            downloadUpdate.IsEnabled = !_context.IsPrivate &&
                snapshot.Lifecycle == PrimaryUpdateLifecycle.Available;
            installUpdate.IsEnabled = !_context.IsPrivate &&
                snapshot.Lifecycle == PrimaryUpdateLifecycle.ReadyToInstall;
        }
        void OnUpdateSnapshotChanged(object? _, PrimaryUpdateSnapshot snapshot) => RenderUpdate(snapshot);
        _updates.SnapshotChanged += OnUpdateSnapshotChanged;
        Closed += (_, _) => _updates.SnapshotChanged -= OnUpdateSnapshotChanged;
        RenderUpdate(_updates.Snapshot);

        checkUpdate.Click += async (_, _) =>
        {
            var checkedResult = await _updates.CheckAsync();
            if (!checkedResult.IsSuccess) updateStatus.Text = "The update feed is offline or could not be verified. Local browsing is unaffected.";
        };
        downloadUpdate.Click += async (_, _) =>
        {
            var downloaded = await _updates.DownloadAsync();
            if (!downloaded.IsSuccess) updateStatus.Text = "The update could not be downloaded and verified.";
        };
        installUpdate.Click += async (_, _) =>
        {
            var snapshot = _updates.Snapshot;
            if (snapshot.Lifecycle != PrimaryUpdateLifecycle.ReadyToInstall) return;
            var version = snapshot.StagedPackage?.Package.Version?.ToString() ?? "available";
            var approved = MessageBox.Show(
                this,
                $"Install Orbit Navigator {version}?\n\nThe manifest signature, version sequence, size, and SHA-256 hash have been verified. This installer is not yet signed by a Windows-trusted publisher, so Windows may show an unknown-publisher warning. Setup remains visible and may ask you to close Orbit Navigator. Continue only if you deliberately want this update.",
                "Confirm Orbit Navigator update",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes;
            if (!approved) return;
            var launched = await _updates.ApproveAndLaunchAsync(
                deliberatePerPackageConfirmation: true);
            if (!launched.IsSuccess) updateStatus.Text = "The verified Orbit Navigator installer could not be opened.";
        };
        var save = new Button
        {
            Content = "Save settings",
            MinWidth = 120,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsEnabled = !_context.IsPrivate,
        };
        var status = new TextBlock { Margin = new Thickness(0, 10, 0, 0) };
        save.Click += async (_, _) =>
        {
            if (_settingsSnapshot is null) return;
            var updated = await _settings.UpdateAsync(new(
                _context,
                _settingsSnapshot.Revision,
                new BrowserCoreSettings(
                    "duckduckgo",
                    restore.IsChecked == true,
                    ask.IsChecked == true,
                    UpdatePreference.NotifyOnly)));
            if (updated.IsSuccess) _settingsSnapshot = updated.Value;
            status.Text = updated.IsSuccess
                ? "Settings saved."
                : "Settings could not be saved. Refresh and try again.";
        };

        var panel = BasePanel("Settings", _context.IsPrivate
            ? "Private windows use normal-profile settings read-only."
            : "Privacy-preserving local browser settings.");
        panel.Children.Add(new TextBlock { Text = "Search provider: DuckDuckGo" });
        panel.Children.Add(restore);
        panel.Children.Add(ask);
        panel.Children.Add(automatic);
        panel.Children.Add(updateDisclosure);
        panel.Children.Add(updateActions);
        panel.Children.Add(updateStatus);
        panel.Children.Add(save);
        panel.Children.Add(status);
        var account = new MyOrbitAccountSettingsControl
        {
            ReducedMotion = !SystemParameters.ClientAreaAnimation,
            Margin = new Thickness(-20, 22, -20, 0),
        };
        panel.Children.Add(account);
        Closed += (_, _) => _myOrbitAccountSettings.Detach(account);
        Content = new ScrollViewer { Content = panel };
        await _myOrbitAccountSettings.AttachAsync(account, _accountContext);
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

    private static FrameworkElement BuildMessage(string heading, string message) =>
        BasePanel(heading, message);

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
