using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;

namespace OrbitNavigator.Presentation.Wpf.Tests;

internal static class StaTest
{
    private static readonly object ExecutionGate = new();
    private static readonly Lazy<Dispatcher> TestDispatcher = new(CreateDispatcher);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    public static void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (ExecutionGate)
        {
            var dispatcher = TestDispatcher.Value;
            ExceptionDispatchInfo? failure = null;
            var operation = dispatcher.InvokeAsync(() =>
            {
                try
                {
                    action();
                }
                catch (Exception exception)
                {
                    failure = ExceptionDispatchInfo.Capture(exception);
                }
            });
            operation.Task.WaitAsync(TestTimeout).GetAwaiter().GetResult();
            // Drain deferred popup/unload work before the next assertion creates
            // another window. Keep the STA alive, just like the real application.
            dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle)
                .Task.WaitAsync(TestTimeout).GetAwaiter().GetResult();
            failure?.Throw();
        }
    }

    private static Dispatcher CreateDispatcher()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            ready.SetResult(dispatcher);
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "Orbit WPF test dispatcher",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        // Repeated per-test InvokeShutdown can deadlock WPF's native WISP tablet
        // worker while destroying popup capture HWNDs. A single pumped STA also
        // models production more faithfully; the background thread ends with the
        // disposable testhost rather than delaying test-process termination.
        return ready.Task.WaitAsync(TestTimeout).GetAwaiter().GetResult();
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
