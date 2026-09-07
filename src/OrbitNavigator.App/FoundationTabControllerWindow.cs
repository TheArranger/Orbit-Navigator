using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using OrbitNavigator.Foundation.Browser;
using OrbitNavigator.Presentation.Wpf;

namespace OrbitNavigator.App;

/// <summary>Native owner tool window; it contains no WebView or tab state.</summary>
public sealed class FoundationTabControllerWindow : Window
{
    private readonly TabControllerControl _tabs;
    private bool _closeForOwner;

    public FoundationTabControllerWindow(
        Window owner,
        TabControllerControl tabs,
        TabControllerBoundsDip? bounds)
    {
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _tabs = tabs ?? throw new ArgumentNullException(nameof(tabs));
        Title = "Orbit Navigator — Tabs";
        Icon = OrbitProgramIdentity.CreateWindowIcon();
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = bounds is null
            ? WindowStartupLocation.CenterOwner
            : WindowStartupLocation.Manual;
        Width = bounds?.Width ?? 480;
        Height = bounds?.Height ?? 560;
        MinWidth = 360;
        MinHeight = 400;
        Background = SystemParameters.HighContrast ? SystemColors.WindowBrush : OrbitVisualTheme.Chrome;
        if (bounds is not null)
        {
            Left = bounds.Left;
            Top = bounds.Top;
        }

        Content = _tabs;
        Closing += OnClosing;
        Closed += OnClosed;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public event EventHandler? DockRequested;

    public TabControllerBoundsDip CurrentBounds =>
        new(Left, Top, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height);

    public bool FocusSelectedTab() => _tabs.FocusSelectedTab();

    public void CloseForOwner()
    {
        _closeForOwner = true;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (_closeForOwner)
        {
            return;
        }

        args.Cancel = true;
        DockRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnClosed(object? sender, EventArgs args)
    {
        _tabs.Unbind();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape)
        {
            return;
        }

        _tabs.CloseActiveFlyout();
        args.Handled = true;
    }
}
