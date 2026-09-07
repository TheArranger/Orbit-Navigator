#if ORBIT_WPF
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;

using OrbitNavigator.Presentation.Tabs;

namespace OrbitNavigator.Presentation.Wpf;

/// <summary>
/// Owner-linked native resource monitor. It contains no WebView and consumes
/// only the owner window's Presentation session.
/// </summary>
public sealed class ResourceTaskWindow : Window
{
    public const double RecommendedWidth = 680;
    public const double RecommendedHeight = 560;
    public const double MinimumWidth = 480;
    public const double MinimumHeight = 400;

    private readonly ResourceTaskPanelControl panel;
    private readonly Window ownerWindow;
    private bool disposeRequested;

    public ResourceTaskWindow(
        Window owner,
        TabControllerPresentationSession session,
        bool reducedMotion = false)
    {
        ownerWindow = owner ?? throw new ArgumentNullException(nameof(owner));
        Owner = ownerWindow;
        ArgumentNullException.ThrowIfNull(session);
        Title = "Orbit Navigator — Browser resources";
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = RecommendedWidth;
        Height = RecommendedHeight;
        MinWidth = MinimumWidth;
        MinHeight = MinimumHeight;
        Background = SystemParameters.HighContrast ? SystemColors.WindowBrush : OrbitVisualTheme.Canvas;
        AutomationProperties.SetName(this, "Browser resources window");
        AutomationProperties.SetHelpText(this,
            "Shows local CPU, memory, and tab resource information from accepted samples.");

        panel = new ResourceTaskPanelControl { ReducedMotion = reducedMotion };
        panel.Bind(session);
        var surface = OrbitVisualTheme.CreateSurface(12);
        surface.Child = panel;
        surface.Margin = new Thickness(10);
        Content = surface;
        Loaded += OnLoaded;
        IsVisibleChanged += OnIsVisibleChanged;
        Closing += OnClosing;
        Closed += OnClosed;
        PreviewKeyDown += OnPreviewKeyDown;
        ownerWindow.Closing += OnOwnerClosing;
    }

    public event EventHandler<bool>? MonitorVisibilityChanged;

    public ResourceTaskPanelControl Panel => panel;

    public bool IsDisposed { get; private set; }

    public void ShowOrActivate()
    {
        if (IsDisposed)
        {
            throw new ObjectDisposedException(nameof(ResourceTaskWindow));
        }

        if (!IsVisible)
        {
            Show();
        }
        Activate();
        _ = Dispatcher.BeginInvoke(() => panel.FocusFirstAction());
    }

    public void HideForReuse()
    {
        if (IsDisposed || !IsVisible)
        {
            return;
        }

        Hide();
        if (Owner is { IsVisible: true } owner)
        {
            owner.Activate();
        }
    }

    public void DisposeForOwner()
    {
        if (IsDisposed)
        {
            return;
        }

        disposeRequested = true;
        Close();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        _ = Dispatcher.BeginInvoke(() => panel.FocusFirstAction());
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs args) =>
        MonitorVisibilityChanged?.Invoke(this, IsVisible);

    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (disposeRequested)
        {
            return;
        }

        args.Cancel = true;
        HideForReuse();
    }

    private void OnOwnerClosing(object? sender, CancelEventArgs args) => disposeRequested = true;

    private void OnClosed(object? sender, EventArgs args)
    {
        IsDisposed = true;
        ownerWindow.Closing -= OnOwnerClosing;
        panel.Unbind();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key == Key.Escape)
        {
            HideForReuse();
            args.Handled = true;
        }
    }
}
#endif
