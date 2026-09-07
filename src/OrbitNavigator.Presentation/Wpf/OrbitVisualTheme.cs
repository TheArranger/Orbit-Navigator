#if ORBIT_WPF
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;

namespace OrbitNavigator.Presentation.Wpf;

public enum OrbitButtonRole
{
    Toolbar = 0,
    Tab = 1,
    Group = 2,
    Primary = 3,
    Private = 4,
    Quiet = 5,
}

/// <summary>Original “quiet orbital instrument panel” visual tokens.</summary>
public static class OrbitVisualTheme
{
    public static Brush Canvas { get; } = Brush("#0B1117");
    public static Brush Chrome { get; } = Brush("#111A22");
    public static Brush Surface { get; } = Brush("#18242D");
    public static Brush SurfaceHover { get; } = Brush("#20323D");
    public static Brush SurfacePressed { get; } = Brush("#263B47");
    public static Brush Ink { get; } = Brush("#F2F7F6");
    public static Brush MutedInk { get; } = Brush("#A5B6B8");
    public static Brush Divider { get; } = Brush("#2B3A42");
    public static Brush SeaGlass { get; } = Brush("#42BDA7");
    public static Brush SeaGlassStrong { get; } = Brush("#278779");
    public static Brush Focus { get; } = Brush("#79DEC9");
    public static Brush WaypointGold { get; } = Brush("#E2A653");
    public static Brush PrivateViolet { get; } = Brush("#A48CEB");
    public static Brush Danger { get; } = Brush("#DD6F74");

    public static void ApplyButton(Button button, OrbitButtonRole role)
    {
        ArgumentNullException.ThrowIfNull(button);
        var highContrast = SystemParameters.HighContrast;
        button.Style = CreateButtonStyle(role, highContrast);
    }

    /// <summary>
    /// Applies an inset visual silhouette while retaining the full rectangular
    /// control as the keyboard, touch, UIA, and pointer hit target.
    /// </summary>
    public static void ApplyCompactRailButton(Button button, OrbitButtonRole role)
    {
        ArgumentNullException.ThrowIfNull(button);
        button.Style = CreateButtonStyle(role, SystemParameters.HighContrast);
        button.Template = CreateButtonTemplate(role == OrbitButtonRole.Tab ? 8 : 9, new Thickness(0, 6, 0, 6));
    }

    public static void ApplyTextBox(TextBox textBox)
    {
        ArgumentNullException.ThrowIfNull(textBox);
        var highContrast = SystemParameters.HighContrast;
        textBox.Style = CreateTextBoxStyle(highContrast);
    }

    public static void ApplyProgressBar(ProgressBar progressBar)
    {
        ArgumentNullException.ThrowIfNull(progressBar);
        progressBar.Style = CreateProgressBarStyle(SystemParameters.HighContrast);
    }

    public static void ApplyContextMenu(ContextMenu menu)
    {
        ArgumentNullException.ThrowIfNull(menu);
        menu.Background = SystemParameters.HighContrast ? SystemColors.MenuBrush : Surface;
        menu.Foreground = SystemParameters.HighContrast ? SystemColors.MenuTextBrush : Ink;
        menu.BorderBrush = SystemParameters.HighContrast ? SystemColors.MenuTextBrush : Divider;
        menu.BorderThickness = new Thickness(1);
        menu.Padding = new Thickness(4);
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            item.Foreground = menu.Foreground;
            item.Background = Brushes.Transparent;
            item.Padding = new Thickness(10, 7, 16, 7);
        }
    }

    public static Border CreateSurface(double cornerRadius = 10) =>
        new()
        {
            Background = SystemParameters.HighContrast ? SystemColors.WindowBrush : Surface,
            BorderBrush = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(cornerRadius),
        };

    public static Style CreateResourceScrollBarStyle()
    {
        var highContrast = SystemParameters.HighContrast;
        var style = new Style(typeof(ScrollBar));
        style.Setters.Add(new Setter(Control.BackgroundProperty,
            highContrast ? SystemColors.ScrollBarBrush : Canvas));
        style.Setters.Add(new Setter(Control.ForegroundProperty,
            highContrast ? SystemColors.ControlTextBrush : MutedInk));
        style.Setters.Add(new Setter(FrameworkElement.WidthProperty, 13d));
        if (!highContrast)
        {
            style.Setters.Add(new Setter(Control.TemplateProperty, CreateDarkVerticalScrollBarTemplate()));
        }
        return style;
    }

    public static Style CreateTabViewportScrollBarStyle(Orientation orientation)
    {
        var highContrast = SystemParameters.HighContrast;
        var style = new Style(typeof(ScrollBar));
        style.Setters.Add(new Setter(Control.BackgroundProperty,
            highContrast ? SystemColors.ScrollBarBrush : Canvas));
        style.Setters.Add(new Setter(Control.ForegroundProperty,
            highContrast ? SystemColors.ControlTextBrush : MutedInk));
        if (orientation == Orientation.Vertical)
        {
            style.Setters.Add(new Setter(FrameworkElement.WidthProperty, 13d));
            style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 13d));
            if (!highContrast)
            {
                style.Setters.Add(new Setter(Control.TemplateProperty, CreateDarkVerticalScrollBarTemplate()));
            }
        }
        else
        {
            style.Setters.Add(new Setter(FrameworkElement.HeightProperty, 13d));
            style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 13d));
            if (!highContrast)
            {
                style.Setters.Add(new Setter(Control.TemplateProperty, CreateDarkHorizontalScrollBarTemplate()));
            }
        }
        return style;
    }

    public static Style CreateResourceListBoxStyle()
    {
        var highContrast = SystemParameters.HighContrast;
        var style = new Style(typeof(ListBox));
        style.Setters.Add(new Setter(Control.BackgroundProperty,
            highContrast ? SystemColors.WindowBrush : Chrome));
        style.Setters.Add(new Setter(Control.ForegroundProperty,
            highContrast ? SystemColors.WindowTextBrush : Ink));
        style.Setters.Add(new Setter(Control.BorderBrushProperty,
            highContrast ? SystemColors.WindowTextBrush : Divider));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(highContrast ? 2 : 1)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(2)));
        style.Setters.Add(new Setter(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled));
        style.Setters.Add(new Setter(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto));
        style.Setters.Add(new Setter(Control.TemplateProperty, CreateResourceListBoxTemplate()));
        return style;
    }

    public static Style CreateResourceListBoxItemStyle()
    {
        var highContrast = SystemParameters.HighContrast;
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.BackgroundProperty,
            highContrast ? SystemColors.WindowBrush : Surface));
        style.Setters.Add(new Setter(Control.ForegroundProperty,
            highContrast ? SystemColors.WindowTextBrush : Ink));
        style.Setters.Add(new Setter(Control.BorderBrushProperty,
            highContrast ? SystemColors.WindowTextBrush : Divider));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(2)));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 0, 6)));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
        style.Setters.Add(new Setter(Control.TemplateProperty, CreateResourceListBoxItemTemplate()));
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty,
            highContrast ? SystemColors.HighlightBrush : SeaGlassStrong));
        selected.Setters.Add(new Setter(Control.ForegroundProperty,
            highContrast ? SystemColors.HighlightTextBrush : Ink));
        selected.Setters.Add(new Setter(Control.BorderBrushProperty,
            highContrast ? SystemColors.HighlightTextBrush : SeaGlass));
        style.Triggers.Add(selected);
        var focused = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
        focused.Setters.Add(new Setter(Control.BorderBrushProperty,
            highContrast ? SystemColors.HighlightBrush : Focus));
        focused.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(3)));
        style.Triggers.Add(focused);
        return style;
    }

    public static Style CreateResourceComboBoxStyle()
    {
        var highContrast = SystemParameters.HighContrast;
        var style = new Style(typeof(ComboBox));
        style.Setters.Add(new Setter(Control.BackgroundProperty,
            highContrast ? SystemColors.WindowBrush : Surface));
        style.Setters.Add(new Setter(Control.ForegroundProperty,
            highContrast ? SystemColors.WindowTextBrush : Ink));
        style.Setters.Add(new Setter(Control.BorderBrushProperty,
            highContrast ? SystemColors.WindowTextBrush : Divider));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(highContrast ? 2 : 1)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 6, 10, 6)));
        style.Setters.Add(new Setter(System.Windows.Controls.Primitives.Selector.ItemContainerStyleProperty,
            CreateResourceComboBoxItemStyle(highContrast)));
        if (!highContrast)
        {
            style.Setters.Add(new Setter(Control.TemplateProperty, CreateDarkResourceComboBoxTemplate()));
        }
        return style;
    }

    private static Style CreateResourceComboBoxItemStyle(bool highContrast)
    {
        var style = new Style(typeof(ComboBoxItem));
        style.Setters.Add(new Setter(Control.BackgroundProperty,
            highContrast ? SystemColors.WindowBrush : Surface));
        style.Setters.Add(new Setter(Control.ForegroundProperty,
            highContrast ? SystemColors.WindowTextBrush : Ink));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 7, 10, 7)));
        style.Setters.Add(new Setter(Control.TemplateProperty, CreateResourceComboBoxItemTemplate()));
        var selected = new Trigger { Property = ComboBoxItem.IsHighlightedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty,
            highContrast ? SystemColors.HighlightBrush : SurfaceHover));
        selected.Setters.Add(new Setter(Control.ForegroundProperty,
            highContrast ? SystemColors.HighlightTextBrush : Ink));
        style.Triggers.Add(selected);
        return style;
    }

    private static Style CreateButtonStyle(OrbitButtonRole role, bool highContrast)
    {
        var style = new Style(typeof(Button));
        var background = highContrast
            ? SystemColors.ControlBrush
            : role switch
            {
                OrbitButtonRole.Primary => SeaGlassStrong,
                OrbitButtonRole.Private => PrivateViolet,
                OrbitButtonRole.Tab => Surface,
                OrbitButtonRole.Group => Chrome,
                _ => Brushes.Transparent,
            };
        var foreground = highContrast ? SystemColors.ControlTextBrush : Ink;
        var border = highContrast ? SystemColors.ControlTextBrush : Divider;
        style.Setters.Add(new Setter(Control.BackgroundProperty, background));
        style.Setters.Add(new Setter(Control.ForegroundProperty, foreground));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, border));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(9, 5, 9, 5)));
        style.Setters.Add(new Setter(Control.FontFamilyProperty, new FontFamily("Segoe UI Variable Text, Segoe UI")));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 13d));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 40d));
        style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 40d));
        style.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
        style.Setters.Add(new Setter(Control.TemplateProperty, CreateButtonTemplate(role == OrbitButtonRole.Tab ? 8 : 9)));

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, highContrast ? SystemColors.HighlightBrush : SurfaceHover));
        hover.Setters.Add(new Setter(Control.ForegroundProperty, highContrast ? SystemColors.HighlightTextBrush : Ink));
        style.Triggers.Add(hover);
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Control.BackgroundProperty, highContrast ? SystemColors.HighlightBrush : SurfacePressed));
        style.Triggers.Add(pressed);
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Control.BorderBrushProperty, highContrast ? SystemColors.HighlightBrush : Focus));
        focus.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(3)));
        style.Triggers.Add(focus);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, highContrast ? 0.75d : 0.42d));
        style.Triggers.Add(disabled);
        return style;
    }

    private static Style CreateTextBoxStyle(bool highContrast)
    {
        var style = new Style(typeof(TextBox));
        style.Setters.Add(new Setter(Control.BackgroundProperty, highContrast ? SystemColors.WindowBrush : Canvas));
        style.Setters.Add(new Setter(Control.ForegroundProperty, highContrast ? SystemColors.WindowTextBrush : Ink));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, highContrast ? SystemColors.WindowTextBrush : Divider));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        // Fixed-height omniboxes keep their established outer geometry. A
        // centered content host plus restrained internal padding prevents
        // ascenders/descenders from being clipped at fractional DPI scales.
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(13, 3, 13, 3)));
        style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(FrameworkElement.UseLayoutRoundingProperty, true));
        style.Setters.Add(new Setter(UIElement.SnapsToDevicePixelsProperty, true));
        style.Setters.Add(new Setter(TextOptions.TextFormattingModeProperty, TextFormattingMode.Display));
        style.Setters.Add(new Setter(Control.FontFamilyProperty, new FontFamily("Segoe UI Variable Text, Segoe UI")));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 14d));
        style.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
        style.Setters.Add(new Setter(Control.TemplateProperty, CreateTextBoxTemplate()));
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
        focus.Setters.Add(new Setter(Control.BorderBrushProperty, highContrast ? SystemColors.HighlightBrush : Focus));
        focus.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(3)));
        style.Triggers.Add(focus);
        return style;
    }

    private static Style CreateProgressBarStyle(bool highContrast)
    {
        var style = new Style(typeof(ProgressBar));
        style.Setters.Add(new Setter(Control.BackgroundProperty, highContrast ? SystemColors.ControlBrush : Canvas));
        style.Setters.Add(new Setter(Control.ForegroundProperty, highContrast ? SystemColors.HighlightBrush : SeaGlass));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, highContrast ? SystemColors.ControlTextBrush : Divider));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.TemplateProperty, CreateProgressBarTemplate()));
        return style;
    }

#pragma warning disable CS0618
    private static ControlTemplate CreateButtonTemplate(double radius, Thickness? visualMargin = null)
    {
        var border = new FrameworkElementFactory(typeof(Border), "Border");
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
        border.SetValue(Border.SnapsToDevicePixelsProperty, true);
        if (visualMargin is { } margin)
        {
            border.SetValue(FrameworkElement.MarginProperty, margin);
        }
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        content.SetValue(ContentPresenter.ContentTemplateProperty, new TemplateBindingExtension(ContentControl.ContentTemplateProperty));
        content.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        border.AppendChild(content);
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    private static ControlTemplate CreateTextBoxTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        border.SetValue(Border.SnapsToDevicePixelsProperty, true);
        border.SetValue(FrameworkElement.UseLayoutRoundingProperty, true);
        var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        host.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        host.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        host.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        border.AppendChild(host);
        return new ControlTemplate(typeof(TextBox)) { VisualTree = border };
    }

    private static ControlTemplate CreateProgressBarTemplate()
    {
        var track = new FrameworkElementFactory(typeof(Grid), "PART_Track");
        track.SetValue(FrameworkElement.ClipToBoundsProperty, true);
        var background = new FrameworkElementFactory(typeof(Border));
        background.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        background.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        background.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        background.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        track.AppendChild(background);
        var indicator = new FrameworkElementFactory(typeof(Border), "PART_Indicator");
        indicator.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
        indicator.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        indicator.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        track.AppendChild(indicator);
        return new ControlTemplate(typeof(ProgressBar)) { VisualTree = track };
    }

    private static ControlTemplate CreateResourceListBoxItemTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        content.SetValue(ContentPresenter.ContentTemplateProperty, new TemplateBindingExtension(ContentControl.ContentTemplateProperty));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        border.AppendChild(content);
        return new ControlTemplate(typeof(ListBoxItem)) { VisualTree = border };
    }

    private static ControlTemplate CreateResourceListBoxTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));

        var scroller = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ScrollViewer");
        scroller.SetValue(Control.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        scroller.SetValue(ScrollViewer.CanContentScrollProperty, true);
        scroller.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        scroller.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        var presenter = new FrameworkElementFactory(typeof(ItemsPresenter));
        scroller.AppendChild(presenter);
        border.AppendChild(scroller);
        return new ControlTemplate(typeof(ListBox)) { VisualTree = border };
    }

    private static ControlTemplate CreateResourceComboBoxItemTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        border.AppendChild(content);
        return new ControlTemplate(typeof(ComboBoxItem)) { VisualTree = border };
    }

    private static ControlTemplate CreateDarkResourceComboBoxTemplate() =>
        (ControlTemplate)XamlReader.Parse(
            """
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                             TargetType="{x:Type ComboBox}">
              <Grid>
                <ToggleButton x:Name="Toggle" Focusable="False" ClickMode="Press"
                              IsChecked="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}">
                  <ToggleButton.Template>
                    <ControlTemplate TargetType="{x:Type ToggleButton}">
                      <Border Background="#18242D" BorderBrush="#2B3A42" BorderThickness="1"
                              CornerRadius="7" Padding="10,6">
                        <Grid>
                          <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="*" />
                            <ColumnDefinition Width="20" />
                          </Grid.ColumnDefinitions>
                          <ContentPresenter Content="{Binding SelectionBoxItem, RelativeSource={RelativeSource AncestorType={x:Type ComboBox}}}"
                                            TextElement.Foreground="#F2F7F6" VerticalAlignment="Center" />
                          <Path Grid.Column="1" Data="M4,7 L10,13 L16,7" Stroke="#A5B6B8"
                                StrokeThickness="1.8" StrokeStartLineCap="Round"
                                StrokeEndLineCap="Round" HorizontalAlignment="Center"
                                VerticalAlignment="Center" />
                        </Grid>
                      </Border>
                    </ControlTemplate>
                  </ToggleButton.Template>
                </ToggleButton>
                <Popup x:Name="PART_Popup" Placement="Bottom" IsOpen="{TemplateBinding IsDropDownOpen}"
                       AllowsTransparency="True" Focusable="False" PopupAnimation="None">
                  <Border Background="#18242D" BorderBrush="#2B3A42" BorderThickness="1"
                          CornerRadius="7" Padding="3"
                          MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}">
                    <ScrollViewer Background="#18242D" VerticalScrollBarVisibility="Auto"
                                  HorizontalScrollBarVisibility="Disabled">
                      <ItemsPresenter />
                    </ScrollViewer>
                  </Border>
                </Popup>
              </Grid>
            </ControlTemplate>
            """);

    private static ControlTemplate CreateDarkVerticalScrollBarTemplate() =>
        (ControlTemplate)XamlReader.Parse(
            """
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                             TargetType="{x:Type ScrollBar}">
              <Border Background="#0B1117" Padding="2,0">
                <Track x:Name="PART_Track" Orientation="Vertical" IsDirectionReversed="True">
                  <Track.DecreaseRepeatButton>
                    <RepeatButton Command="ScrollBar.PageUpCommand" Background="Transparent"
                                  BorderThickness="0" Focusable="False" />
                  </Track.DecreaseRepeatButton>
                  <Track.Thumb>
                    <Thumb MinHeight="28" Background="#2B3A42" BorderBrush="#278779"
                           BorderThickness="1">
                      <Thumb.Template>
                        <ControlTemplate TargetType="{x:Type Thumb}">
                          <Border Background="{TemplateBinding Background}"
                                  BorderBrush="{TemplateBinding BorderBrush}"
                                  BorderThickness="{TemplateBinding BorderThickness}"
                                  CornerRadius="4" />
                        </ControlTemplate>
                      </Thumb.Template>
                    </Thumb>
                  </Track.Thumb>
                  <Track.IncreaseRepeatButton>
                    <RepeatButton Command="ScrollBar.PageDownCommand" Background="Transparent"
                                  BorderThickness="0" Focusable="False" />
                  </Track.IncreaseRepeatButton>
                </Track>
              </Border>
            </ControlTemplate>
            """);

    private static ControlTemplate CreateDarkHorizontalScrollBarTemplate() =>
        (ControlTemplate)XamlReader.Parse(
            """
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                             TargetType="{x:Type ScrollBar}">
              <Border Background="#0B1117" Padding="0,2">
                <Track x:Name="PART_Track" Orientation="Horizontal" IsDirectionReversed="False">
                  <Track.DecreaseRepeatButton>
                    <RepeatButton Command="ScrollBar.PageLeftCommand" Background="Transparent"
                                  BorderThickness="0" Focusable="False" />
                  </Track.DecreaseRepeatButton>
                  <Track.Thumb>
                    <Thumb MinWidth="28" Background="#2B3A42" BorderBrush="#278779"
                           BorderThickness="1">
                      <Thumb.Template>
                        <ControlTemplate TargetType="{x:Type Thumb}">
                          <Border Background="{TemplateBinding Background}"
                                  BorderBrush="{TemplateBinding BorderBrush}"
                                  BorderThickness="{TemplateBinding BorderThickness}"
                                  CornerRadius="4" />
                        </ControlTemplate>
                      </Thumb.Template>
                    </Thumb>
                  </Track.Thumb>
                  <Track.IncreaseRepeatButton>
                    <RepeatButton Command="ScrollBar.PageRightCommand" Background="Transparent"
                                  BorderThickness="0" Focusable="False" />
                  </Track.IncreaseRepeatButton>
                </Track>
              </Border>
            </ControlTemplate>
            """);
#pragma warning restore CS0618

    private static SolidColorBrush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
#endif
