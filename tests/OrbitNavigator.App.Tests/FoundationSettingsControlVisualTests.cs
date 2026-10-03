using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using OrbitNavigator.Presentation.Wpf;
using Xunit;

namespace OrbitNavigator.App.Tests;

public sealed class FoundationSettingsControlVisualTests
{
    [Fact]
    public void BundledChangelogIsOfflineReadableAndClearlySeparatesUnreleasedChanges() => RunSta(() =>
    {
        var section = FoundationSettingsControl.CreateChangelogSection();
        Assert.False(section.IsExpanded);
        Assert.Equal("Changelog — what's new", AutomationProperties.GetName(section));
        Assert.True(section.MinHeight >= 44);
        var scroll = Assert.IsType<ScrollViewer>(section.Content);
        Assert.Equal(ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);
        Assert.Equal("Orbit Navigator changelog", AutomationProperties.GetName(scroll));
        Assert.IsType<Style>(scroll.Resources[typeof(ScrollBar)]);
        var notes = ReleaseNotesContent.Read();
        Assert.Contains("## Unreleased", notes);
        Assert.Contains("## 0.1.27", notes);
        Assert.DoesNotContain("unavailable in this build", notes);
        Assert.InRange(notes.Length, 1, ReleaseNotesContent.MaximumCharacters);
        var paragraphs = Assert.IsType<StackPanel>(scroll.Content);
        Assert.All(paragraphs.Children.Cast<TextBlock>(), text =>
        {
            Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
            Assert.Equal(FoundationSettingsControl.SettingsTextBrush, text.Foreground);
            Assert.DoesNotContain("##", text.Text);
        });
        Assert.DoesNotContain(ReleaseNotesContent.GetDisplayBlocks(), block => block.Text == "Maintaining this file");
    });

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
        Assert.IsType<Style>(scroll.Resources[typeof(ScrollBar)]);
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
