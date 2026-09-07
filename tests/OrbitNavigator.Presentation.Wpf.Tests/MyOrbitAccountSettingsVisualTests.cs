using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Presentation.Accounts;
using OrbitNavigator.Presentation.Wpf;

using Xunit;

namespace OrbitNavigator.Presentation.Wpf.Tests;

[Collection("WPF focus-sensitive")]
public sealed class MyOrbitAccountSettingsVisualTests
{
    [Theory]
    [InlineData(MyOrbitAccountConnectionState.ProviderUnavailable, "Provider unavailable")]
    [InlineData(MyOrbitAccountConnectionState.SignedOut, "Not connected")]
    [InlineData(MyOrbitAccountConnectionState.LinkPending, "Waiting for authorization")]
    [InlineData(MyOrbitAccountConnectionState.Connected, "Connected")]
    [InlineData(MyOrbitAccountConnectionState.ReauthorizationRequired, "Reauthorization required")]
    [InlineData(MyOrbitAccountConnectionState.Revoked, "Connection revoked")]
    [InlineData(MyOrbitAccountConnectionState.Failed, "Connection error")]
    [InlineData(MyOrbitAccountConnectionState.Loading, "Checking connection")]
    public void EveryFrozenStateHasPlainLanguageVisibleAndAutomationStatus(
        MyOrbitAccountConnectionState connectionState,
        string expectedLabel) => StaTest.Run(() =>
    {
        var capabilities = connectionState switch
        {
            MyOrbitAccountConnectionState.ProviderUnavailable or MyOrbitAccountConnectionState.Loading =>
                new MyOrbitAccountSettingsCapabilities(false, false, false, false, false),
            MyOrbitAccountConnectionState.LinkPending => new(false, true, false, false, false),
            MyOrbitAccountConnectionState.Connected => new(false, false, true, true, true),
            MyOrbitAccountConnectionState.ReauthorizationRequired => new(true, false, true, true, true),
            _ => new(true, false, false, false, false),
        };
        var control = new MyOrbitAccountSettingsControl();
        control.Render(State(Context(BrowserProfileMode.Normal), connectionState, capabilities));
        StaTest.Prepare(control, 780, 720);

        Assert.Contains(
            StaTest.Descendants(control).OfType<TextBlock>(),
            text => text.Text == expectedLabel && text.Visibility == Visibility.Visible);
        Assert.Equal(expectedLabel, AutomationProperties.GetItemStatus(control));
    });

    [Fact]
    public void SignedOutExplainsSystemBrowserAndEmitsRevisionBoundLinkIntent() => StaTest.Run(() =>
    {
        var context = Context(BrowserProfileMode.Normal);
        var control = new MyOrbitAccountSettingsControl { ReducedMotion = true };
        MyOrbitAccountSettingsIntent? requested = null;
        control.IntentRequested += (_, args) => requested = args.Intent;
        control.Render(State(
            context,
            MyOrbitAccountConnectionState.SignedOut,
            new(true, false, false, false, false),
            revision: 9));
        StaTest.Prepare(control, 780, 720);

        var allText = string.Join(" ", StaTest.Descendants(control).OfType<TextBlock>().Select(text => text.Text));
        Assert.Contains("default system browser", allText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not enable tab, history, settings", allText, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(StaTest.Descendants(control).OfType<PasswordBox>());
        var link = StaTest.FindByAutomationName<Button>(control, "Link My Orbit account");
        Assert.True(link.IsEnabled);
        link.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.NotNull(requested);
        Assert.Equal(MyOrbitAccountSettingsIntentKind.BeginExternalLink, requested!.Kind);
        Assert.Equal(9, requested.ExpectedRevision);
        Assert.Equal(context, requested.Privacy);
        Assert.Null(requested.DeviceId);
    });

    [Fact]
    public void PrivateSettingsRemainReadableAndEmitNoRemoteIntent() => StaTest.Run(() =>
    {
        var control = new MyOrbitAccountSettingsControl();
        var count = 0;
        control.IntentRequested += (_, _) => count++;
        control.Render(State(
            Context(BrowserProfileMode.Private),
            MyOrbitAccountConnectionState.SignedOut,
            new(false, false, false, false, false)));
        StaTest.Prepare(control, 780, 720);

        Assert.False(StaTest.FindByAutomationName<Button>(control, "Link My Orbit account").IsEnabled);
        Assert.False(StaTest.FindByAutomationName<Button>(control, "Refresh My Orbit account status").IsEnabled);
        Assert.Contains(
            StaTest.Descendants(control).OfType<TextBlock>(),
            text => text.Text.Contains("unavailable in private windows", StringComparison.OrdinalIgnoreCase));
        Assert.False(control.RequestQuery());
        Assert.Equal(0, count);
    });

    [Fact]
    public void PendingAndReauthorizationStatesExposeTruthfulActions() => StaTest.Run(() =>
    {
        var context = Context(BrowserProfileMode.Normal);
        var control = new MyOrbitAccountSettingsControl();
        var intents = new List<MyOrbitAccountSettingsIntent>();
        control.IntentRequested += (_, args) => intents.Add(args.Intent);
        control.Render(State(
            context,
            MyOrbitAccountConnectionState.LinkPending,
            new(false, true, false, false, false),
            startedAt: DateTimeOffset.UtcNow));
        StaTest.Prepare(control, 780, 720);

        StaTest.FindByAutomationName<Button>(control, "Cancel account linking")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(MyOrbitAccountSettingsIntentKind.CancelPendingLink, Assert.Single(intents).Kind);

        control.Render(State(
            context,
            MyOrbitAccountConnectionState.ReauthorizationRequired,
            new(true, false, true, true, false),
            revision: 3));
        Assert.Equal(
            "Reauthorize My Orbit account",
            StaTest.FindByAutomationName<Button>(control, "Reauthorize My Orbit account").Content);
        Assert.Contains(
            StaTest.Descendants(control).OfType<TextBlock>(),
            text => text.Text == "Reauthorization required");
    });

    [Fact]
    public void ConnectedDeviceActionsRequireConfirmationAndCarryOnlyDeviceId() => StaTest.Run(() =>
    {
        var context = Context(BrowserProfileMode.Normal);
        var otherId = new DeviceId(Guid.NewGuid());
        var devices = new[]
        {
            Device(new DeviceId(Guid.NewGuid()), "This PC", current: true, canRevoke: false),
            Device(otherId, "Travel laptop", current: false, canRevoke: true),
        };
        var control = new MyOrbitAccountSettingsControl { ReducedMotion = true };
        var intents = new List<MyOrbitAccountSettingsIntent>();
        control.IntentRequested += (_, args) => intents.Add(args.Intent);
        control.Render(State(
            context,
            MyOrbitAccountConnectionState.Connected,
            new(false, false, true, true, true),
            devices: devices,
            revision: 22));
        StaTest.Prepare(control, 780, 760);

        var revoke = StaTest.FindByAutomationName<Button>(control, "Revoke device Travel laptop");
        revoke.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Empty(intents);
        StaTest.FindByAutomationName<Button>(control, "Confirm revoke device Travel laptop")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var revokeIntent = Assert.Single(intents);
        Assert.Equal(MyOrbitAccountSettingsIntentKind.RevokeDevice, revokeIntent.Kind);
        Assert.Equal(otherId, revokeIntent.DeviceId);
        Assert.Equal(22, revokeIntent.ExpectedRevision);

        var disconnect = StaTest.FindByAutomationName<Button>(control, "Disconnect this device from My Orbit");
        disconnect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Single(intents);
        StaTest.FindByAutomationName<Button>(control, "Confirm disconnect this device from My Orbit")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(MyOrbitAccountSettingsIntentKind.DisconnectCurrentDevice, intents[^1].Kind);
        Assert.Null(intents[^1].DeviceId);
    });

    [Fact]
    public void RenderingAndSamplesNeverStealFocusAndReducedMotionHasNoAnimation() => StaTest.Run(() =>
    {
        var context = Context(BrowserProfileMode.Normal);
        var control = new MyOrbitAccountSettingsControl { ReducedMotion = true };
        control.Render(State(context, MyOrbitAccountConnectionState.SignedOut, new(true, false, false, false, false)));
        var outside = new Button { Content = "Outside" };
        var root = new StackPanel { Children = { outside, control } };
        var window = new Window { Content = root, Width = 800, Height = 760, ShowInTaskbar = false };
        window.Show();
        try
        {
            outside.Focus();
            Assert.True(outside.IsKeyboardFocused);
            control.Render(State(context, MyOrbitAccountConnectionState.Failed, new(true, false, false, false, false), revision: 2));
            Assert.True(outside.IsKeyboardFocused);
            Assert.False(control.HasActiveAnimation);
            if (SystemParameters.HighContrast)
            {
                Assert.Equal(SystemColors.WindowBrush, control.Background);
            }
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void EscapeCancelsDestructiveConfirmationWithoutEmitting() => StaTest.Run(() =>
    {
        var context = Context(BrowserProfileMode.Normal);
        var control = new MyOrbitAccountSettingsControl();
        var count = 0;
        control.IntentRequested += (_, _) => count++;
        control.Render(State(context, MyOrbitAccountConnectionState.Connected, new(false, false, true, true, false)));
        var window = new Window { Content = control, Width = 780, Height = 720, ShowInTaskbar = false };
        window.Show();
        try
        {
            window.UpdateLayout();
            StaTest.FindByAutomationName<Button>(control, "Disconnect this device from My Orbit")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(control)!, 0, Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
                Source = control,
            };
            control.RaiseEvent(escape);

            Assert.True(escape.Handled);
            Assert.Equal(0, count);
            Assert.NotNull(StaTest.FindByAutomationName<Button>(control, "Disconnect this device from My Orbit"));
        }
        finally
        {
            window.Close();
        }
    });

    private static MyOrbitAccountSettingsPresentationState State(
        PrivacyContext context,
        MyOrbitAccountConnectionState connectionState,
        MyOrbitAccountSettingsCapabilities capabilities,
        long revision = 1,
        DateTimeOffset? startedAt = null,
        IReadOnlyList<MyOrbitDevicePresentation>? devices = null) => new(
            context,
            revision,
            connectionState,
            "My Orbit",
            connectionState == MyOrbitAccountConnectionState.Connected ? "Connected account" : null,
            connectionState switch
            {
                MyOrbitAccountConnectionState.ProviderUnavailable => "My Orbit linking is unavailable.",
                MyOrbitAccountConnectionState.SignedOut => "Not linked.",
                MyOrbitAccountConnectionState.LinkPending => "Finish authorization in your system browser.",
                MyOrbitAccountConnectionState.Connected => "This browser is linked.",
                MyOrbitAccountConnectionState.ReauthorizationRequired => "Authorization needs renewal.",
                MyOrbitAccountConnectionState.Revoked => "The link was revoked.",
                MyOrbitAccountConnectionState.Failed => "The last account operation failed safely.",
                _ => "Checking account status.",
            },
            connectionState == MyOrbitAccountConnectionState.LinkPending ? startedAt ?? DateTimeOffset.UtcNow : null,
            null,
            devices ?? [],
            capabilities);

    private static MyOrbitDevicePresentation Device(
        DeviceId id,
        string name,
        bool current,
        bool canRevoke) => new(
            id,
            name,
            current,
            DateTimeOffset.UtcNow.AddDays(-2),
            DateTimeOffset.UtcNow.AddMinutes(-5),
            false,
            null,
            canRevoke,
            canRevoke || current ? null : "Revocation unavailable.");

    private static PrivacyContext Context(BrowserProfileMode mode) => new(
        new ProfileId(Guid.NewGuid()),
        new BrowserSessionId(Guid.NewGuid()),
        mode);
}
