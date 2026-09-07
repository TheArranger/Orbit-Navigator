using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;

namespace OrbitNavigator.Presentation.Wpf.Tests;

internal static class StaTest
{
    public static void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    public static void Prepare(FrameworkElement element, double width = 1200, double height = 800)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    public static T FindByAutomationName<T>(DependencyObject root, string name)
        where T : DependencyObject
    {
        foreach (var current in Descendants(root))
        {
            if (current is T match && AutomationProperties.GetName(current) == name)
            {
                return match;
            }
        }

        throw new Xunit.Sdk.XunitException($"No {typeof(T).Name} named '{name}' was found.");
    }

    public static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }
}
