using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OrbitNavigator.App.Support;
using Xunit;

namespace OrbitNavigator.App.Tests;

public sealed class ProblemReportControlTests
{
    private static readonly Version CurrentVersion = new(0, 2, 1, 0);

    [Fact]
    public void OpeningSettingsDoesNotNavigateAndVersionRequiresOptIn() => RunSta(() =>
    {
        var requests = new List<Uri>();
        var control = new ProblemReportControl(CurrentVersion, uri =>
        {
            requests.Add(uri);
            return Task.FromResult(true);
        });

        Assert.Empty(requests);
        Assert.False(control.IncludeVersion);
        Assert.Contains("no app version", control.ContextPreview);
        Assert.Contains("No report has been sent", control.Status);
        Assert.Equal("Report a problem", AutomationProperties.GetName(control));

        var body = Assert.IsType<StackPanel>(control.Content);
        var version = Assert.Single(body.Children.OfType<CheckBox>());
        version.IsChecked = true;
        Assert.Empty(requests);
        Assert.Contains("version 0.2.1", control.ContextPreview);
        var reportButton = Assert.Single(body.Children.OfType<Button>());
        reportButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Single(requests);
        Assert.Equal(ProblemReportRoute.Create(CurrentVersion, true), requests[0]);
        Assert.Contains("no report has been sent by Orbit", control.Status);
    });

    [Fact]
    public void FailedNavigationDoesNotClaimDeliveryAndAllowsRetry() => RunSta(() =>
    {
        var attempts = 0;
        var control = new ProblemReportControl(CurrentVersion, _ => Task.FromResult(++attempts > 1));
        var route = ProblemReportRoute.Create(CurrentVersion, false);

        control.OpenAsync(route).GetAwaiter().GetResult();
        Assert.Contains("could not be opened", control.Status);
        Assert.Contains("No report has been sent", control.Status);
        control.OpenAsync(route).GetAwaiter().GetResult();
        Assert.Equal(2, attempts);
        Assert.Contains("no report has been sent by Orbit", control.Status);
    });

    [Fact]
    public void ErrorsDoNotExposeExceptionData() => RunSta(() =>
    {
        var control = new ProblemReportControl(CurrentVersion,
            _ => Task.FromException<bool>(new InvalidOperationException("secret profile path and token")));

        control.OpenAsync(ProblemReportRoute.Create(CurrentVersion, false)).GetAwaiter().GetResult();

        Assert.Contains("No report has been sent", control.Status);
        Assert.DoesNotContain("secret", control.Status);
        var body = Assert.IsType<StackPanel>(control.Content);
        Assert.True(Assert.Single(body.Children.OfType<Button>()).IsEnabled);
    });

    [Fact]
    public void UnapprovedContextAndDestinationsNeverReachTheBrowser() => RunSta(() =>
    {
        var attempts = 0;
        var control = new ProblemReportControl(CurrentVersion, _ =>
        {
            attempts++;
            return Task.FromResult(true);
        });

        control.OpenAsync(ProblemReportRoute.Create(CurrentVersion, true)).GetAwaiter().GetResult();
        control.OpenAsync(new Uri("https://iamtheparadox.com/api/bugs")).GetAwaiter().GetResult();

        Assert.Equal(0, attempts);
        Assert.Contains("not allowed", control.Status);
        Assert.Contains("No report has been sent", control.Status);
    });

    [Fact]
    public void FallbackIsExplicitlyPublicAndDoesNotClaimPortfolioDelivery() => RunSta(() =>
    {
        var control = new ProblemReportControl(CurrentVersion, _ => Task.FromResult(true));

        control.OpenAsync(ProblemReportRoute.PublicBugForm).GetAwaiter().GetResult();

        Assert.Contains("Public GitHub", control.Status);
        Assert.Contains("no Portfolio work order has been sent", control.Status);
    });

    [Fact]
    public void MissingNavigationSeamDisablesActionsWithClearStatus() => RunSta(() =>
    {
        var control = new ProblemReportControl(null, null);
        var body = Assert.IsType<StackPanel>(control.Content);

        Assert.False(Assert.Single(body.Children.OfType<Button>()).IsEnabled);
        Assert.False(Assert.Single(body.Children.OfType<CheckBox>()).IsEnabled);
        Assert.Contains("unavailable", control.Status);
        Assert.Contains("No report has been sent", control.Status);
    });

    [Fact]
    public void CopyIsAnExplicitLocalActionAndContainsOnlyReviewedContext() => RunSta(() =>
    {
        var copied = new List<string>();
        var control = new ProblemReportControl(CurrentVersion, null, copied.Add);
        Assert.Empty(copied);

        control.CopyLink();

        Assert.Equal(ProblemReportRoute.Create(CurrentVersion, false).AbsoluteUri, Assert.Single(copied));
        Assert.Contains("no report has been sent", control.Status);
    });

    [Fact]
    public void ClipboardFailureIsRecoverableAndDoesNotExposeExceptionData() => RunSta(() =>
    {
        var control = new ProblemReportControl(CurrentVersion, null,
            _ => throw new InvalidOperationException("secret clipboard detail"));

        control.CopyLink();

        Assert.Contains("could not be copied", control.Status);
        Assert.DoesNotContain("secret", control.Status);
    });

    [Theory]
    [InlineData(900)]
    [InlineData(360)]
    public void ReportSurfaceRendersOffscreenAtWideAndNarrowWidths(int width) => RunSta(() =>
    {
        var calls = 0;
        var control = new ProblemReportControl(CurrentVersion, _ =>
        {
            calls++;
            return Task.FromResult(true);
        });
        var surface = new Border
        {
            Width = width,
            Padding = new Thickness(24),
            Background = FoundationSettingsControl.SettingsBackgroundBrush,
            Child = control,
        };
        surface.Measure(new Size(width, double.PositiveInfinity));
        var height = (int)Math.Ceiling(surface.DesiredSize.Height);
        surface.Arrange(new Rect(0, 0, width, height));
        surface.UpdateLayout();
        surface.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Render, () => { });
        Assert.InRange(height, 300, 1600);
        Assert.Equal(0, calls);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        bitmap.Freeze();
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        Assert.True(pixels.Count(value => value > 96) > 1000, "Report surface capture was blank.");

        // Opt-in artifact capture for review. No real window, browser profile,
        // network destination, or system clipboard is used by this test.
        var outputDirectory = Environment.GetEnvironmentVariable("ORBIT_SUPPORT_PREVIEW_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(outputDirectory, $"report-a-problem-{width}.png"));
            encoder.Save(file);
        }
    });

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Report control test exceeded its STA timeout.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
