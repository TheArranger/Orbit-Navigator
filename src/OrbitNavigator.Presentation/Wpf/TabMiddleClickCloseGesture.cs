#if ORBIT_WPF
using System.Windows;
using System.Windows.Input;

namespace OrbitNavigator.Presentation.Wpf;

/// <summary>Owns one middle-button gesture across a tab and its child controls.</summary>
internal static class TabMiddleClickCloseGesture
{
    public static void Attach(UIElement target, Action requestClose, Action cancelDrag)
    {
        var pressed = false;
        MouseButtonEventHandler down = (_, args) =>
        {
            if (args.ChangedButton != MouseButton.Middle)
            {
                return;
            }

            args.Handled = true;
            cancelDrag();
            pressed = true;
        };
        MouseButtonEventHandler up = (_, args) =>
        {
            if (args.ChangedButton != MouseButton.Middle)
            {
                return;
            }

            args.Handled = true;
            if (!pressed)
            {
                return;
            }

            // Clear before dispatch: a synchronous accepted projection may
            // rebuild the card, and preview/bubble share one physical gesture.
            pressed = false;
            requestClose();
        };

        // Preview suppresses child activation/capture. The handled-event
        // bubbling fallback also covers controls that consume their own input.
        target.AddHandler(Mouse.PreviewMouseDownEvent, down, handledEventsToo: true);
        target.AddHandler(Mouse.MouseDownEvent, down, handledEventsToo: true);
        target.AddHandler(Mouse.PreviewMouseUpEvent, up, handledEventsToo: true);
        target.AddHandler(Mouse.MouseUpEvent, up, handledEventsToo: true);
        target.MouseLeave += (_, _) => pressed = false;
    }
}
#endif
