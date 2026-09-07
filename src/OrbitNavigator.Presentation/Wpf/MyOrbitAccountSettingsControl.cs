#if ORBIT_WPF
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Presentation.Accounts;

namespace OrbitNavigator.Presentation.Wpf;

public sealed class MyOrbitAccountSettingsIntentRequestedEventArgs : EventArgs
{
    public MyOrbitAccountSettingsIntentRequestedEventArgs(MyOrbitAccountSettingsIntent intent) =>
        Intent = intent ?? throw new ArgumentNullException(nameof(intent));

    public MyOrbitAccountSettingsIntent Intent { get; }
}

/// <summary>
/// Accessible account-link Settings presentation. The host owns all protocol,
/// system-browser, credential, provider, persistence, and network behavior.
/// </summary>
public sealed class MyOrbitAccountSettingsControl : Grid
{
    private static readonly MyOrbitAccountSettingsCapabilities NoCapabilities = new(false, false, false, false, false);

    private readonly StackPanel page = new()
    {
        MaxWidth = 760,
        HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(28, 24, 28, 32),
    };
    private readonly Border accountSurface = OrbitVisualTheme.CreateSurface(16);
    private readonly Border deviceSurface = OrbitVisualTheme.CreateSurface(16);
    private readonly TextBlock stateLabel = new() { FontSize = 13, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock providerLabel = new() { FontSize = 16, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock accountLabel = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Focusable = true };
    private readonly TextBlock localBrowsing = new()
    {
        Text = "Local browsing remains available whether or not you link an account.",
        TextWrapping = TextWrapping.Wrap,
    };
    private readonly Button link = new() { Content = "Link My Orbit account", MinHeight = 44 };
    private readonly Button cancel = new() { Content = "Cancel linking", MinHeight = 44 };
    private readonly Button disconnect = new() { Content = "Disconnect this device", MinHeight = 44 };
    private readonly Button refresh = new() { Content = "Refresh account status", MinHeight = 44 };
    private readonly Button refreshDevices = new() { Content = "Refresh devices", MinHeight = 44 };
    private readonly ListBox devices = new() { MinHeight = 96, MaxHeight = 300 };
    private readonly TextBlock deviceEmpty = new()
    {
        Text = "No linked devices are available.",
        TextWrapping = TextWrapping.Wrap,
    };
    private MyOrbitAccountSettingsPresentationState? state;
    private MyOrbitAccountSettingsIntentKind? pendingIntentKind;
    private bool confirmingDisconnect;
    private DeviceId? confirmingRevokeDeviceId;
    private bool systemParameterEventsAttached;
    private bool hasAcceptedHostState;

    public MyOrbitAccountSettingsControl()
    {
        AutomationProperties.SetName(this, "My Orbit account settings");
        BuildLayout();
        ApplyTheme();
        state = UnavailableInitialState();
        UpdatePresentation();
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public event EventHandler<MyOrbitAccountSettingsIntentRequestedEventArgs>? IntentRequested;

    public MyOrbitAccountSettingsPresentationState? CurrentState => state;

    public bool ReducedMotion { get; set; }

    public bool HasActiveAnimation => false;

    public ListBox DeviceList => devices;

    public void Render(MyOrbitAccountSettingsPresentationState nextState)
    {
        ArgumentNullException.ThrowIfNull(nextState);
        nextState.Validate();
        if (hasAcceptedHostState && state is not null &&
            (nextState.Privacy.ProfileId != state.Privacy.ProfileId ||
             nextState.Privacy.SessionId != state.Privacy.SessionId))
        {
            throw new ArgumentException("An account Settings control cannot switch profile sessions.", nameof(nextState));
        }

        if (hasAcceptedHostState && state is not null && nextState.Revision < state.Revision)
        {
            return;
        }

        state = nextState;
        hasAcceptedHostState = true;
        confirmingDisconnect = false;
        confirmingRevokeDeviceId = null;
        UpdatePresentation();
    }

    public void ApplyOperationResult(MyOrbitAccountOperationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        result.Validate();
        if (result.RefreshedState is not null)
        {
            Render(result.RefreshedState);
        }

        status.Text = result.SafeMessage;
        AutomationProperties.SetLiveSetting(
            status,
            result.Outcome is MyOrbitAccountOperationOutcome.Accepted or MyOrbitAccountOperationOutcome.Stale
                ? AutomationLiveSetting.Polite
                : AutomationLiveSetting.Assertive);
        var target = pendingIntentKind switch
        {
            MyOrbitAccountSettingsIntentKind.BeginExternalLink => MyOrbitAccountSettingsFocusTarget.Status,
            MyOrbitAccountSettingsIntentKind.CancelPendingLink => MyOrbitAccountSettingsFocusTarget.Link,
            MyOrbitAccountSettingsIntentKind.DisconnectCurrentDevice => MyOrbitAccountSettingsFocusTarget.Link,
            MyOrbitAccountSettingsIntentKind.QueryDevices => MyOrbitAccountSettingsFocusTarget.DeviceList,
            MyOrbitAccountSettingsIntentKind.RevokeDevice => MyOrbitAccountSettingsFocusTarget.DeviceList,
            _ => MyOrbitAccountSettingsFocusTarget.Status,
        };
        pendingIntentKind = null;
        FocusAfterOperation(target);
    }

    public bool RequestQuery() => Emit(MyOrbitAccountSettingsIntentKind.Query);

    public void FocusAfterOperation(MyOrbitAccountSettingsFocusTarget target)
    {
        FrameworkElement element = target switch
        {
            MyOrbitAccountSettingsFocusTarget.Link => link,
            MyOrbitAccountSettingsFocusTarget.Cancel => cancel,
            MyOrbitAccountSettingsFocusTarget.Disconnect => disconnect,
            MyOrbitAccountSettingsFocusTarget.RefreshDevices => refreshDevices,
            MyOrbitAccountSettingsFocusTarget.DeviceList => devices,
            _ => status,
        };
        if (element.Visibility != Visibility.Visible || !element.IsEnabled)
        {
            element = status;
        }

        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => element.Focus()));
    }

    private void BuildLayout()
    {
        var heading = new TextBlock
        {
            Text = "My Orbit account",
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
        };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level1);
        page.Children.Add(heading);
        page.Children.Add(new TextBlock
        {
            Text = "Optionally link this browser to My Orbit through a secure external authorization flow.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 18),
        });

        var account = new StackPanel { Margin = new Thickness(20) };
        AutomationProperties.SetName(accountSurface, "My Orbit connection status");
        account.Children.Add(stateLabel);
        providerLabel.Margin = new Thickness(0, 9, 0, 2);
        account.Children.Add(providerLabel);
        account.Children.Add(accountLabel);
        status.Margin = new Thickness(0, 12, 0, 0);
        AutomationProperties.SetName(status, "My Orbit account status");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        account.Children.Add(status);
        localBrowsing.Margin = new Thickness(0, 10, 0, 0);
        account.Children.Add(localBrowsing);

        var disclosure = OrbitVisualTheme.CreateSurface(10);
        disclosure.Margin = new Thickness(0, 16, 0, 0);
        disclosure.Padding = new Thickness(14);
        disclosure.Child = new StackPanel
        {
            Children =
            {
                Heading("Account link only", AutomationHeadingLevel.Level2),
                Copy(MyOrbitAccountSettingsPresentationState.LinkScopeDescription),
                Copy(MyOrbitAccountSettingsPresentationState.SystemBrowserDisclosure, new Thickness(0, 8, 0, 0)),
            },
        };
        AutomationProperties.SetName(disclosure, "Account link scope and security disclosure");
        account.Children.Add(disclosure);

        var actions = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        foreach (var button in new[] { link, cancel, disconnect, refresh })
        {
            button.Margin = new Thickness(0, 0, 8, 8);
            actions.Children.Add(button);
        }
        account.Children.Add(actions);
        accountSurface.Child = account;
        page.Children.Add(accountSurface);

        var devicePanel = new StackPanel { Margin = new Thickness(20) };
        var deviceHeader = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(refreshDevices, Dock.Right);
        refreshDevices.Margin = new Thickness(12, 0, 0, 8);
        deviceHeader.Children.Add(refreshDevices);
        deviceHeader.Children.Add(Heading("Linked devices", AutomationHeadingLevel.Level2));
        devicePanel.Children.Add(deviceHeader);
        devicePanel.Children.Add(new TextBlock
        {
            Text = "Review devices linked to this account. Device rows never include browsing activity, addresses, or page content.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 12),
        });
        devices.ItemContainerStyle = OrbitVisualTheme.CreateResourceListBoxItemStyle();
        devices.Style = OrbitVisualTheme.CreateResourceListBoxStyle();
        VirtualizingPanel.SetIsVirtualizing(devices, true);
        VirtualizingPanel.SetVirtualizationMode(devices, VirtualizationMode.Recycling);
        AutomationProperties.SetName(devices, "Linked My Orbit devices");
        devicePanel.Children.Add(devices);
        deviceEmpty.Margin = new Thickness(0, 8, 0, 4);
        devicePanel.Children.Add(deviceEmpty);
        deviceSurface.Margin = new Thickness(0, 16, 0, 0);
        deviceSurface.Child = devicePanel;
        page.Children.Add(deviceSurface);

        var scroll = new ScrollViewer
        {
            Content = page,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        KeyboardNavigation.SetTabNavigation(scroll, KeyboardNavigationMode.Continue);
        Children.Add(scroll);

        AutomationProperties.SetName(link, "Link My Orbit account");
        AutomationProperties.SetHelpText(link, "Opens the default system browser for secure My Orbit authorization.");
        AutomationProperties.SetName(cancel, "Cancel account linking");
        AutomationProperties.SetName(disconnect, "Disconnect this device from My Orbit");
        AutomationProperties.SetHelpText(disconnect, "Disconnects this browser after confirmation. Local browsing data remains available.");
        AutomationProperties.SetName(refresh, "Refresh My Orbit account status");
        AutomationProperties.SetName(refreshDevices, "Refresh linked devices");
        link.Click += (_, _) => Emit(MyOrbitAccountSettingsIntentKind.BeginExternalLink);
        cancel.Click += (_, _) => Emit(MyOrbitAccountSettingsIntentKind.CancelPendingLink);
        disconnect.Click += (_, _) => RequestDisconnect();
        refresh.Click += (_, _) => Emit(MyOrbitAccountSettingsIntentKind.Query);
        refreshDevices.Click += (_, _) => Emit(MyOrbitAccountSettingsIntentKind.QueryDevices);
    }

    private void UpdatePresentation()
    {
        if (state is null)
        {
            return;
        }

        var privateMode = state.Privacy.IsPrivate;
        stateLabel.Text = StateLabel(state.ConnectionState);
        providerLabel.Text = string.IsNullOrWhiteSpace(state.SafeProviderLabel) ? "My Orbit" : state.SafeProviderLabel.Trim();
        accountLabel.Text = string.IsNullOrWhiteSpace(state.SafeAccountLabel)
            ? "No account identifier is displayed."
            : state.SafeAccountLabel.Trim();
        status.Text = privateMode
            ? "Account linking is unavailable in private windows. Open Settings from a normal window to manage the account."
            : state.SafeStatusMessage;
        localBrowsing.Text = state.LocalBrowsingRemainsAvailable
            ? "Local browsing remains available whether or not you link an account."
            : throw new InvalidOperationException("Account state cannot disable local browsing.");

        var pending = state.ConnectionState == MyOrbitAccountConnectionState.LinkPending;
        var connected = state.ConnectionState is MyOrbitAccountConnectionState.Connected or
            MyOrbitAccountConnectionState.ReauthorizationRequired;
        link.Content = state.ConnectionState switch
        {
            MyOrbitAccountConnectionState.ReauthorizationRequired => "Reauthorize My Orbit account",
            MyOrbitAccountConnectionState.Revoked => "Link My Orbit account again",
            _ => "Link My Orbit account",
        };
        AutomationProperties.SetName(link, (string)link.Content);
        link.Visibility = pending || connected || state.ConnectionState == MyOrbitAccountConnectionState.Loading
            ? Visibility.Collapsed
            : Visibility.Visible;
        link.IsEnabled = !privateMode && state.Capabilities.CanBeginExternalLink;
        cancel.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        cancel.IsEnabled = !privateMode && state.Capabilities.CanCancelPendingLink;
        disconnect.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        disconnect.IsEnabled = !privateMode && state.Capabilities.CanDisconnectCurrentDevice;
        disconnect.Content = "Disconnect this device";
        AutomationProperties.SetName(disconnect, "Disconnect this device from My Orbit");
        refresh.IsEnabled = !privateMode;
        deviceSurface.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        refreshDevices.IsEnabled = !privateMode && state.Capabilities.CanQueryDevices;
        RenderDevices();
        AutomationProperties.SetItemStatus(this, stateLabel.Text);
    }

    private void RenderDevices()
    {
        devices.Items.Clear();
        if (state is null)
        {
            return;
        }

        foreach (var device in state.Devices)
        {
            devices.Items.Add(CreateDeviceRow(device));
        }

        deviceEmpty.Visibility = state.Devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        devices.Visibility = state.Devices.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private ListBoxItem CreateDeviceRow(MyOrbitDevicePresentation device)
    {
        var grid = new Grid { Margin = new Thickness(10, 8, 10, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var copy = new StackPanel();
        copy.Children.Add(new TextBlock
        {
            Text = device.DisplayName,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        copy.Children.Add(new TextBlock
        {
            Text = DeviceDetail(device),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Foreground = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.MutedInk,
        });
        grid.Children.Add(copy);
        if (!device.IsCurrentDevice && !device.IsRevoked)
        {
            var revoke = new Button
            {
                Content = confirmingRevokeDeviceId == device.DeviceId ? "Confirm revoke" : "Revoke",
                MinWidth = 92,
                MinHeight = 44,
                Margin = new Thickness(12, 0, 0, 0),
                IsEnabled = state is { Privacy.IsPrivate: false } && state.Capabilities.CanRevokeDevices && device.CanRevoke,
                Tag = device.DeviceId,
            };
            OrbitVisualTheme.ApplyButton(revoke, OrbitButtonRole.Quiet);
            AutomationProperties.SetName(revoke,
                confirmingRevokeDeviceId == device.DeviceId
                    ? $"Confirm revoke device {device.DisplayName}"
                    : $"Revoke device {device.DisplayName}");
            AutomationProperties.SetHelpText(revoke, revoke.IsEnabled
                ? "Requires confirmation. Revoking signs this device out of the My Orbit link."
                : device.RevokeUnavailableReason ?? "Device revocation is unavailable.");
            revoke.Click += (_, _) => RequestRevoke(device);
            Grid.SetColumn(revoke, 1);
            grid.Children.Add(revoke);
        }

        var item = new ListBoxItem { Content = grid, Tag = device.DeviceId, MinHeight = 58 };
        AutomationProperties.SetName(item,
            $"{device.DisplayName}, {(device.IsCurrentDevice ? "this device" : device.IsRevoked ? "revoked" : "linked device")}");
        return item;
    }

    private void RequestDisconnect()
    {
        if (!confirmingDisconnect)
        {
            confirmingDisconnect = true;
            disconnect.Content = "Confirm disconnect";
            AutomationProperties.SetName(disconnect, "Confirm disconnect this device from My Orbit");
            status.Text = "Disconnect this browser from My Orbit? Local browsing data stays on this device.";
            AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Assertive);
            disconnect.Focus();
            return;
        }

        confirmingDisconnect = false;
        disconnect.Content = "Disconnect this device";
        AutomationProperties.SetName(disconnect, "Disconnect this device from My Orbit");
        Emit(MyOrbitAccountSettingsIntentKind.DisconnectCurrentDevice);
    }

    private void RequestRevoke(MyOrbitDevicePresentation device)
    {
        if (confirmingRevokeDeviceId != device.DeviceId)
        {
            confirmingRevokeDeviceId = device.DeviceId;
            status.Text = $"Revoke {device.DisplayName}? That device will lose this My Orbit account link.";
            AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Assertive);
            RenderDevices();
            var confirm = FindButtonByTag(device.DeviceId);
            confirm?.Focus();
            return;
        }

        confirmingRevokeDeviceId = null;
        Emit(MyOrbitAccountSettingsIntentKind.RevokeDevice, device.DeviceId);
    }

    private bool Emit(MyOrbitAccountSettingsIntentKind kind, DeviceId? deviceId = null)
    {
        if (!hasAcceptedHostState || state is null || state.Privacy.IsPrivate || !CanEmit(kind, deviceId) || IntentRequested is null)
        {
            return false;
        }

        var intent = new MyOrbitAccountSettingsIntent(
            Guid.NewGuid(),
            state.Privacy,
            state.Revision,
            kind,
            deviceId).Validate();
        pendingIntentKind = kind;
        IntentRequested.Invoke(this, new(intent));
        return true;
    }

    private bool CanEmit(MyOrbitAccountSettingsIntentKind kind, DeviceId? deviceId) =>
        state is not null && kind switch
        {
            MyOrbitAccountSettingsIntentKind.Query => true,
            MyOrbitAccountSettingsIntentKind.BeginExternalLink => state.Capabilities.CanBeginExternalLink,
            MyOrbitAccountSettingsIntentKind.CancelPendingLink => state.Capabilities.CanCancelPendingLink,
            MyOrbitAccountSettingsIntentKind.DisconnectCurrentDevice => state.Capabilities.CanDisconnectCurrentDevice,
            MyOrbitAccountSettingsIntentKind.QueryDevices => state.Capabilities.CanQueryDevices,
            MyOrbitAccountSettingsIntentKind.RevokeDevice => state.Capabilities.CanRevokeDevices &&
                deviceId is not null && state.Devices.Any(device =>
                    device.DeviceId == deviceId && device.CanRevoke && !device.IsCurrentDevice && !device.IsRevoked),
            _ => false,
        };

    private Button? FindButtonByTag(DeviceId deviceId) =>
        VisualDescendants(devices).OfType<Button>().FirstOrDefault(button => button.Tag is DeviceId id && id == deviceId);

    private void OnPreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape || (!confirmingDisconnect && confirmingRevokeDeviceId is null))
        {
            return;
        }

        confirmingDisconnect = false;
        confirmingRevokeDeviceId = null;
        status.Text = state?.SafeStatusMessage ?? string.Empty;
        UpdatePresentation();
        status.Focus();
        args.Handled = true;
    }

    private void ApplyTheme()
    {
        var highContrast = SystemParameters.HighContrast;
        Background = highContrast ? SystemColors.WindowBrush : OrbitVisualTheme.Canvas;
        var foreground = highContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.Ink;
        TextElement.SetForeground(page, foreground);
        accountSurface.Background = highContrast ? SystemColors.WindowBrush : OrbitVisualTheme.Surface;
        accountSurface.BorderBrush = highContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.Divider;
        deviceSurface.Background = highContrast ? SystemColors.WindowBrush : OrbitVisualTheme.Surface;
        deviceSurface.BorderBrush = highContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.Divider;
        stateLabel.Foreground = highContrast ? SystemColors.HighlightBrush : OrbitVisualTheme.SeaGlass;
        status.Foreground = highContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.Ink;
        localBrowsing.Foreground = highContrast ? SystemColors.WindowTextBrush : OrbitVisualTheme.MutedInk;
        devices.Style = OrbitVisualTheme.CreateResourceListBoxStyle();
        devices.ItemContainerStyle = OrbitVisualTheme.CreateResourceListBoxItemStyle();
        foreach (var button in new[] { link, cancel, disconnect, refresh, refreshDevices })
        {
            OrbitVisualTheme.ApplyButton(button, button == link ? OrbitButtonRole.Primary : OrbitButtonRole.Quiet);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (!systemParameterEventsAttached)
        {
            SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
            systemParameterEventsAttached = true;
        }
        ApplyTheme();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (systemParameterEventsAttached)
        {
            SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
            systemParameterEventsAttached = false;
        }
    }

    private void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(SystemParameters.HighContrast) or null)
        {
            ApplyTheme();
            RenderDevices();
        }
    }

    private static TextBlock Heading(string text, AutomationHeadingLevel level)
    {
        var heading = new TextBlock { Text = text, FontSize = 17, FontWeight = FontWeights.SemiBold };
        AutomationProperties.SetHeadingLevel(heading, level);
        return heading;
    }

    private static TextBlock Copy(string text, Thickness? margin = null) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Margin = margin ?? new Thickness(0, 5, 0, 0),
    };

    private static string StateLabel(MyOrbitAccountConnectionState connectionState) => connectionState switch
    {
        MyOrbitAccountConnectionState.ProviderUnavailable => "Provider unavailable",
        MyOrbitAccountConnectionState.SignedOut => "Not connected",
        MyOrbitAccountConnectionState.LinkPending => "Waiting for authorization",
        MyOrbitAccountConnectionState.Connected => "Connected",
        MyOrbitAccountConnectionState.ReauthorizationRequired => "Reauthorization required",
        MyOrbitAccountConnectionState.Revoked => "Connection revoked",
        MyOrbitAccountConnectionState.Failed => "Connection error",
        MyOrbitAccountConnectionState.Loading => "Checking connection",
        _ => throw new ArgumentOutOfRangeException(nameof(connectionState)),
    };

    private static string DeviceDetail(MyOrbitDevicePresentation device)
    {
        var parts = new List<string>();
        if (device.IsCurrentDevice) parts.Add("This device");
        if (device.IsRevoked) parts.Add("Revoked");
        if (device.RegisteredAtUtc is { } registered) parts.Add($"Linked {registered.ToLocalTime():g}");
        if (device.LastSeenAtUtc is { } lastSeen) parts.Add($"Last active {lastSeen.ToLocalTime():g}");
        return parts.Count == 0 ? "Linked device" : string.Join(" - ", parts);
    }

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in VisualDescendants(child)) yield return descendant;
        }
    }

    private static MyOrbitAccountSettingsPresentationState UnavailableInitialState()
    {
        var profile = new ProfileId(Guid.NewGuid());
        return new(
            new PrivacyContext(profile, new BrowserSessionId(Guid.NewGuid()), BrowserProfileMode.Normal),
            0,
            MyOrbitAccountConnectionState.ProviderUnavailable,
            "My Orbit",
            null,
            "Account status has not been supplied by the browser host.",
            null,
            null,
            [],
            NoCapabilities);
    }
}
#endif
