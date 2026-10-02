using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using OrbitNavigator.Presentation.Wpf;
using Xunit;

namespace OrbitNavigator.App.Tests;

public sealed class FoundationSettingsControlVisualTests
{
    [Fact]
    public void SettingsChromeUsesReadableThemeAndAccessibleActionBounds() => RunSta(() =>
    {
        var surface = new UserControl();
        FoundationSettingsControl.ApplySettingsSurfaceTheme(surface);

        Assert.Equal(FoundationSettingsControl.SettingsBackgroundBrush, surface.Background);
        Assert.Equal(FoundationSettingsControl.SettingsTextBrush, surface.Foreground);

        var checkBox = new CheckBox { Content = "Setting" };
        FoundationSettingsControl.ApplySettingsCheckBoxTheme(checkBox);

        Assert.Equal(FoundationSettingsControl.SettingsTextBrush, checkBox.Foreground);
        Assert.Equal(32, checkBox.MinHeight);
        Assert.Equal(VerticalAlignment.Center, checkBox.VerticalContentAlignment);

        var button = new Button { Content = "Action" };
        FoundationSettingsControl.ApplySettingsButtonTheme(button, OrbitButtonRole.Primary);

        Assert.Equal(44, button.MinHeight);
        Assert.Equal(new Thickness(14, 8, 14, 8), button.Padding);
        Assert.NotNull(button.Style);
        Assert.Null(button.FocusVisualStyle);
        Assert.Equal(
            SystemParameters.HighContrast ? SystemColors.ControlTextBrush : OrbitVisualTheme.Ink,
            button.Foreground);
        Assert.Equal(
            SystemParameters.HighContrast ? SystemColors.ControlBrush : OrbitVisualTheme.SeaGlassStrong,
            button.Background);

        var scroll = FoundationSettingsControl.CreateSettingsScrollSurface(new StackPanel());
        Assert.Equal(FoundationSettingsControl.SettingsBackgroundBrush, scroll.Background);
        Assert.Equal(FoundationSettingsControl.SettingsTextBrush, scroll.Foreground);
        Assert.Equal(ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);
    });

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Settings theme test exceeded its STA timeout.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
