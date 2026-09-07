using System.Windows;
using System.Windows.Controls;
using OrbitNavigator.ClipboardShelf.Presentation;
using OrbitNavigator.App.Accounts;
using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Privacy;
using OrbitNavigator.Contracts.Updates;
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
    private readonly IBrowserSettingsFacade _settings;
    private readonly ClipboardShelfPresenter _clipboard;
    private readonly MyOrbitAccountSettingsAdapter _myOrbitAccountSettings;
    private readonly BetaUpdateClient _betaUpdates;
    private readonly Func<BrowsingContext?> _accountContext;
    private readonly Func<Uri, Task> _openTarget;
    private readonly ListBox _items = new() { MinHeight = 280 };
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal };
    private BrowserSettingsSnapshot? _settingsSnapshot;

    public FoundationUtilityWindow(
        PrivacyContext context,
        IHistoryFacade history,
        IBrowserSettingsFacade settings,
        ClipboardShelfPresenter clipboard,
        MyOrbitAccountSettingsAdapter myOrbitAccountSettings,
        BetaUpdateClient betaUpdates,
        Func<BrowsingContext?> accountContext,
        Func<Uri, Task> openTarget)
    {
        _context = context;
        _history = history;
        _settings = settings;
        _clipboard = clipboard;
        _myOrbitAccountSettings = myOrbitAccountSettings;
        _betaUpdates = betaUpdates ?? throw new ArgumentNullException(nameof(betaUpdates));
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
            Content = "Automatic Primary updates (requires future trusted Windows signing)",
            IsChecked = false,
            Margin = new Thickness(0, 6, 0, 12),
            IsEnabled = false,
        };
        var allowBeta = new CheckBox
        {
            Content = "Allow Beta Updates",
            IsChecked = _betaUpdates.Snapshot.IsBetaOptedIn,
            Margin = new Thickness(0, 8, 0, 6),
            IsEnabled = !_context.IsPrivate,
        };
        var betaDisclosure = new TextBlock
        {
            Text = "Beta packages are authenticated by Orbit's signed manifest and SHA-256 hash, but may not be signed by a Windows-trusted publisher. Windows may show an unknown-publisher warning. Beta never installs silently and every package requires a separate confirmation.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
        var betaStatus = new TextBlock
        {
            Text = _betaUpdates.Snapshot.StatusMessage,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 8),
        };
        var checkBeta = new Button { Content = "Check for Beta update", MinWidth = 160, Margin = new Thickness(0, 0, 8, 0) };
        var downloadBeta = new Button { Content = "Download verified Beta", MinWidth = 170, Margin = new Thickness(0, 0, 8, 0) };
        var installBeta = new Button { Content = "Install verified Beta...", MinWidth = 170 };
        var betaActions = new StackPanel { Orientation = Orientation.Horizontal };
        betaActions.Children.Add(checkBeta);
        betaActions.Children.Add(downloadBeta);
        betaActions.Children.Add(installBeta);

        void RenderBeta(BetaUpdateSnapshot snapshot)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(() => RenderBeta(snapshot));
                return;
            }
            allowBeta.IsChecked = snapshot.IsBetaOptedIn;
            betaStatus.Text = snapshot.StatusMessage;
            checkBeta.IsEnabled = !_context.IsPrivate && snapshot.IsBetaOptedIn &&
                snapshot.Lifecycle is not BetaUpdateLifecycle.Checking and not BetaUpdateLifecycle.Downloading;
            downloadBeta.IsEnabled = !_context.IsPrivate &&
                snapshot.Lifecycle == BetaUpdateLifecycle.Available;
            installBeta.IsEnabled = !_context.IsPrivate &&
                snapshot.Lifecycle == BetaUpdateLifecycle.ReadyToInstall;
        }
        void OnBetaSnapshotChanged(object? _, BetaUpdateSnapshot snapshot) => RenderBeta(snapshot);
        _betaUpdates.SnapshotChanged += OnBetaSnapshotChanged;
        Closed += (_, _) => _betaUpdates.SnapshotChanged -= OnBetaSnapshotChanged;
        RenderBeta(_betaUpdates.Snapshot);

        checkBeta.Click += async (_, _) =>
        {
            var checkedResult = await _betaUpdates.CheckAsync();
            if (!checkedResult.IsSuccess) betaStatus.Text = "The Beta feed is offline or could not be verified. Local browsing is unaffected.";
        };
        downloadBeta.Click += async (_, _) =>
        {
            var downloaded = await _betaUpdates.DownloadAsync();
            if (!downloaded.IsSuccess) betaStatus.Text = "The Beta package could not be downloaded and verified.";
        };
        installBeta.Click += async (_, _) =>
        {
            var snapshot = _betaUpdates.Snapshot;
            if (snapshot.Lifecycle != BetaUpdateLifecycle.ReadyToInstall) return;
            var version = snapshot.StagedPackage?.Package.Version?.ToString() ?? "available";
            var approved = MessageBox.Show(
                this,
                $"Install Orbit Navigator Beta {version}?\n\nThe manifest signature, version, size, and SHA-256 hash have been verified. This installer is not signed by a Windows-trusted publisher, so Windows may show an unknown-publisher warning. Setup will remain visible and may ask you to close Orbit Navigator. Continue only if you deliberately want this Beta package.",
                "Confirm unsigned Orbit Navigator Beta",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes;
            if (!approved) return;
            var launched = await _betaUpdates.ApproveAndLaunchAsync(
                deliberatePerPackageConfirmation: true);
            if (!launched.IsSuccess) betaStatus.Text = "The verified Beta installer could not be opened.";
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
            var wantsBeta = allowBeta.IsChecked == true;
            if (wantsBeta && !_betaUpdates.Snapshot.IsBetaOptedIn)
            {
                var confirmed = MessageBox.Show(
                    this,
                    "Allow Beta Updates?\n\nBeta builds may be unsigned by a Windows-trusted publisher. Orbit still requires a valid pinned manifest signature and package hash, never installs a Beta silently, and asks again before launching each installer.",
                    "Allow Beta Updates",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) == MessageBoxResult.Yes;
                if (!confirmed)
                {
                    allowBeta.IsChecked = false;
                    wantsBeta = false;
                }
            }
            var channelSaved = await _betaUpdates.SetBetaOptInAsync(wantsBeta);
            var updated = await _settings.UpdateAsync(new(
                _context,
                _settingsSnapshot.Revision,
                new BrowserCoreSettings(
                    "duckduckgo",
                    restore.IsChecked == true,
                    ask.IsChecked == true,
                    UpdatePreference.NotifyOnly)));
            if (updated.IsSuccess) _settingsSnapshot = updated.Value;
            status.Text = updated.IsSuccess && channelSaved.IsSuccess
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
        panel.Children.Add(allowBeta);
        panel.Children.Add(betaDisclosure);
        panel.Children.Add(betaActions);
        panel.Children.Add(betaStatus);
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
