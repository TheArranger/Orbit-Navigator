using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using OrbitNavigator.App.Accounts;
using OrbitNavigator.App.Support;
using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Updates;
using OrbitNavigator.Presentation.Wpf;
using OrbitNavigator.Updates;

namespace OrbitNavigator.App;

/// <summary>
/// Browser-owned Settings content. It is hosted by an ordinary browser tab and
/// intentionally contains no WebView, origin, cookies, or site permissions.
/// </summary>
internal sealed class FoundationSettingsControl : UserControl, IDisposable
{
    private readonly PrivacyContext _context;
    private readonly IBrowserSettingsFacade _settings;
    private readonly MyOrbitAccountSettingsAdapter _myOrbitAccountSettings;
    private readonly PrimaryUpdateClient _updates;
    private readonly Func<BrowsingContext?> _accountContext;
    private readonly Func<Uri, Task<bool>>? _openSupportPage;
    private readonly MyOrbitAccountSettingsControl _account = new()
    {
        ReducedMotion = !SystemParameters.ClientAreaAnimation,
        Margin = new Thickness(-20, 22, -20, 0),
    };
    private BrowserSettingsSnapshot? _settingsSnapshot;
    private bool _updatesAttached;
    private bool _accountAttached;
    private int _disposed;

    public FoundationSettingsControl(
        PrivacyContext context,
        IBrowserSettingsFacade settings,
        MyOrbitAccountSettingsAdapter myOrbitAccountSettings,
        PrimaryUpdateClient updates,
        Func<BrowsingContext?> accountContext,
        Func<Uri, Task<bool>>? openSupportPage = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _myOrbitAccountSettings = myOrbitAccountSettings ?? throw new ArgumentNullException(nameof(myOrbitAccountSettings));
        _updates = updates ?? throw new ArgumentNullException(nameof(updates));
        _accountContext = accountContext ?? throw new ArgumentNullException(nameof(accountContext));
        _openSupportPage = openSupportPage;
        ApplySettingsSurfaceTheme(this);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var result = await _settings.GetAsync(_context, cancellationToken);
        if (!result.IsSuccess)
        {
            Content = CreateSettingsScrollSurface(BasePanel("Settings", "Settings could not be loaded."));
            return;
        }

        _settingsSnapshot = result.Value!;
        Content = BuildSettingsSurface(_settingsSnapshot.Values);
        if (!_updatesAttached)
        {
            _updates.SnapshotChanged += OnUpdateSnapshotChanged;
            _updatesAttached = true;
        }
        if (!_accountAttached)
        {
            _accountAttached = true;
            await _myOrbitAccountSettings.AttachAsync(_account, _accountContext, cancellationToken);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_updatesAttached)
        {
            _updates.SnapshotChanged -= OnUpdateSnapshotChanged;
            _updatesAttached = false;
        }
        if (_accountAttached)
        {
            _myOrbitAccountSettings.Detach(_account);
            _accountAttached = false;
        }
        Content = null;
    }

    private FrameworkElement BuildSettingsSurface(BrowserCoreSettings values)
    {
        var restore = new CheckBox
        {
            Content = "Restore open tabs when Orbit Navigator starts",
            IsChecked = values.RestoreOpenTabs,
            Margin = new Thickness(0, 12, 0, 6),
            IsEnabled = !_context.IsPrivate,
        };
        ApplySettingsCheckBoxTheme(restore);
        var ask = new CheckBox
        {
            Content = "Ask where to save downloads",
            IsChecked = values.AskWhereToSaveDownloads,
            Margin = new Thickness(0, 6, 0, 6),
            IsEnabled = !_context.IsPrivate,
        };
        ApplySettingsCheckBoxTheme(ask);
        var automatic = new CheckBox
        {
            Content = "Check for Orbit Navigator updates automatically",
            IsChecked = true,
            Margin = new Thickness(0, 6, 0, 6),
            IsEnabled = false,
            ToolTip = "Orbit uses a randomized local schedule and sends no identifying update telemetry.",
        };
        ApplySettingsCheckBoxTheme(automatic);
        var updateDisclosure = new TextBlock
        {
            Text = "Orbit verifies a pinned manifest signature, version sequence, package size, and SHA-256 hash before offering an update. Updates never install silently. Until Windows-trusted signing is available, each installer requires a separate confirmation and Windows may show an unknown-publisher warning.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = SettingsSecondaryTextBrush,
        };
        var updateStatus = new TextBlock
        {
            Text = _updates.Snapshot.StatusMessage,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 8),
            Foreground = SettingsTextBrush,
        };
        var checkUpdate = new Button { Content = "Check for updates", MinWidth = 150, Margin = new Thickness(0, 0, 8, 0) };
        var downloadUpdate = new Button { Content = "Download verified update", MinWidth = 185, Margin = new Thickness(0, 0, 8, 0) };
        var installUpdate = new Button { Content = "Install verified update...", MinWidth = 180 };
        ApplySettingsButtonTheme(checkUpdate, OrbitButtonRole.Quiet);
        ApplySettingsButtonTheme(downloadUpdate, OrbitButtonRole.Quiet);
        ApplySettingsButtonTheme(installUpdate, OrbitButtonRole.Quiet);
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
            if (Volatile.Read(ref _disposed) != 0) return;
            updateStatus.Text = snapshot.StatusMessage;
            checkUpdate.IsEnabled = !_context.IsPrivate &&
                snapshot.Lifecycle is not PrimaryUpdateLifecycle.Checking and not PrimaryUpdateLifecycle.Downloading;
            downloadUpdate.IsEnabled = !_context.IsPrivate && snapshot.Lifecycle == PrimaryUpdateLifecycle.Available;
            installUpdate.IsEnabled = !_context.IsPrivate && snapshot.Lifecycle == PrimaryUpdateLifecycle.ReadyToInstall;
        }

        _renderUpdate = RenderUpdate;
        RenderUpdate(_updates.Snapshot);
        checkUpdate.Click += async (_, _) =>
        {
            var checkedResult = await _updates.CheckAsync();
            if (!checkedResult.IsSuccess)
                updateStatus.Text = "The update feed is offline or could not be verified. Local browsing is unaffected.";
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
            var message = $"Install Orbit Navigator {version}?\n\nThe manifest signature, version sequence, size, and SHA-256 hash have been verified. This installer is not yet signed by a Windows-trusted publisher, so Windows may show an unknown-publisher warning. Setup remains visible and may ask you to close Orbit Navigator. Continue only if you deliberately want this update.";
            var owner = Window.GetWindow(this);
            var approved = (owner is null
                ? MessageBox.Show(message, "Confirm Orbit Navigator update", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                : MessageBox.Show(owner, message, "Confirm Orbit Navigator update", MessageBoxButton.YesNo, MessageBoxImage.Warning)) == MessageBoxResult.Yes;
            if (!approved) return;
            var launched = await _updates.ApproveAndLaunchAsync(deliberatePerPackageConfirmation: true);
            if (!launched.IsSuccess) updateStatus.Text = "The verified Orbit Navigator installer could not be opened.";
        };

        var save = new Button
        {
            Content = "Save settings",
            MinWidth = 120,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsEnabled = !_context.IsPrivate,
        };
        ApplySettingsButtonTheme(save, OrbitButtonRole.Primary);
        var status = new TextBlock
        {
            Margin = new Thickness(0, 10, 0, 0),
            Foreground = SettingsTextBrush,
        };
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
        panel.Children.Add(new TextBlock
        {
            Text = "Search provider: DuckDuckGo",
            Foreground = SettingsTextBrush,
        });
        panel.Children.Add(restore);
        panel.Children.Add(ask);
        panel.Children.Add(automatic);
        panel.Children.Add(updateDisclosure);
        panel.Children.Add(updateActions);
        panel.Children.Add(updateStatus);
        panel.Children.Add(save);
        panel.Children.Add(status);
        panel.Children.Add(CreateChangelogSection());
        panel.Children.Add(new ProblemReportControl(typeof(App).Assembly.GetName().Version, _openSupportPage));
        panel.Children.Add(_account);
        return CreateSettingsScrollSurface(panel);
    }

    private Action<PrimaryUpdateSnapshot>? _renderUpdate;

    internal static Expander CreateChangelogSection()
    {
        var paragraphs = new StackPanel { Margin = new Thickness(14) };
        foreach (var block in ReleaseNotesContent.GetDisplayBlocks())
        {
            paragraphs.Children.Add(new TextBlock
            {
                Text = block.IsBullet ? "\u2022 " + block.Text : block.Text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = SettingsTextBrush,
                FontSize = block.IsHeading ? 18 : 14,
                FontWeight = block.IsHeading ? FontWeights.SemiBold : FontWeights.Normal,
                Margin = new Thickness(0, block.IsHeading ? 12 : 0, 0, 8),
            });
        }
        var notes = new ScrollViewer
        {
            Content = paragraphs,
            Focusable = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MinHeight = 160,
            MaxHeight = 360,
            Background = SettingsBackgroundBrush,
            Foreground = SettingsTextBrush,
            BorderBrush = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.SeaGlassStrong,
            BorderThickness = new Thickness(1),
        };
        AutomationProperties.SetName(notes, "Orbit Navigator changelog");
        OrbitVisualTheme.ApplyScrollBarTheme(notes);
        AutomationProperties.SetHelpText(notes,
            "Read-only release notes included with this build. Available offline. Unreleased changes are not in the public installer yet.");
        var section = new Expander
        {
            Header = "Changelog — what's new",
            Content = notes,
            Foreground = SettingsTextBrush,
            Background = SettingsBackgroundBrush,
            Margin = new Thickness(0, 20, 0, 0),
            MinHeight = 44,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(section, "Changelog — what's new");
        AutomationProperties.SetHelpText(section, "Expand to read the bundled release notes without opening another window.");
        return section;
    }

    private void OnUpdateSnapshotChanged(object? sender, PrimaryUpdateSnapshot snapshot) =>
        _renderUpdate?.Invoke(snapshot);

    private static StackPanel BasePanel(string heading, string description)
    {
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = heading,
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            Foreground = SettingsTextBrush,
        });
        panel.Children.Add(new TextBlock
        {
            Text = description,
            Margin = new Thickness(0, 6, 0, 14),
            TextWrapping = TextWrapping.Wrap,
            Foreground = SettingsSecondaryTextBrush,
        });
        return panel;
    }

    internal static Brush SettingsBackgroundBrush =>
        SystemParameters.HighContrast ? SystemColors.WindowBrush : OrbitVisualTheme.Canvas;

    internal static Brush SettingsTextBrush =>
        SystemParameters.HighContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.Ink;

    internal static Brush SettingsSecondaryTextBrush =>
        SystemParameters.HighContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.MutedInk;

    internal static void ApplySettingsSurfaceTheme(Control surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        surface.Background = SettingsBackgroundBrush;
        surface.Foreground = SettingsTextBrush;
        surface.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        OrbitVisualTheme.ApplyScrollBarTheme(surface);
    }

    internal static void ApplySettingsCheckBoxTheme(CheckBox checkBox)
    {
        ArgumentNullException.ThrowIfNull(checkBox);
        checkBox.Foreground = SettingsTextBrush;
        checkBox.Background = Brushes.Transparent;
        checkBox.MinHeight = 32;
        checkBox.VerticalContentAlignment = VerticalAlignment.Center;
    }

    internal static void ApplySettingsButtonTheme(Button button, OrbitButtonRole role)
    {
        ArgumentNullException.ThrowIfNull(button);
        OrbitVisualTheme.ApplyButton(button, role);
        button.MinHeight = 44;
        button.Padding = new Thickness(14, 8, 14, 8);
        button.UseLayoutRounding = true;
        button.SnapsToDevicePixels = true;
    }

    internal static ScrollViewer CreateSettingsScrollSurface(FrameworkElement content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var scroll = new ScrollViewer
        {
            Content = content,
            Background = SettingsBackgroundBrush,
            Foreground = SettingsTextBrush,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        OrbitVisualTheme.ApplyScrollBarTheme(scroll);
        return scroll;
    }
}
