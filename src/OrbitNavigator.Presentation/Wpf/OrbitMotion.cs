#if ORBIT_WPF
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace OrbitNavigator.Presentation.Wpf;

public static class OrbitMotion
{
    public static void Reveal(FrameworkElement element, bool reducedMotion)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.Visibility = Visibility.Visible;
        element.BeginAnimation(UIElement.OpacityProperty, null);
        if (reducedMotion || SystemParameters.HighContrast)
        {
            element.Opacity = 1;
            element.RenderTransform = Transform.Identity;
            return;
        }

        var offset = new TranslateTransform(0, -4);
        element.RenderTransform = offset;
        element.RenderTransformOrigin = new Point(0.5, 0);
        element.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        offset.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(-4, 0, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            });
    }
}
#endif
