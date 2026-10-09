using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using OrbitNavigator.Presentation.Wpf;

namespace OrbitNavigator.App.Support;

/// <summary>
/// Reviews the small outbound form context. Reporting, diagnostics, and image
/// selection remain explicit user actions on the authenticated Portfolio form.
/// The callback opens an ordinary Orbit tab; it never submits a work order.
/// </summary>
internal sealed class ProblemReportControl : UserControl
{
    private readonly Version? _version;
    private readonly Func<Uri, Task<bool>>? _openPage;
    private readonly System.Action<string> _copyLink;
    private readonly CheckBox _includeVersion;
    private readonly TextBlock _contextPreview;
    private readonly TextBlock _status;
    private readonly Button[] _actions;
    private bool _opening;

    internal ProblemReportControl(
        Version? version,
        Func<Uri, Task<bool>>? openPage,
        System.Action<string>? copyLink = null)
    {
        _version = version;
        _openPage = openPage;
        _copyLink = copyLink ?? Clipboard.SetText;
        FoundationSettingsControl.ApplySettingsSurfaceTheme(this);
        AutomationProperties.SetName(this, "Report a problem");

        var body = new StackPanel { Margin = new Thickness(0, 22, 0, 0) };
        body.Children.Add(new TextBlock
        {
            Text = "Report a problem",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = FoundationSettingsControl.SettingsTextBrush,
        });
        body.Children.Add(Paragraph(
            "Open the private report form at iamtheparadox.com in an Orbit tab. Sign in to your Portfolio account, describe the issue, and review everything before submitting. A work order is created only after the website confirms your submission."));

        _includeVersion = new CheckBox
        {
            Content = $"Include Orbit Navigator version {ProblemReportRoute.DisplayVersion(version)}",
            IsChecked = false,
            IsEnabled = version is not null,
            Margin = new Thickness(0, 4, 0, 4),
        };
        FoundationSettingsControl.ApplySettingsCheckBoxTheme(_includeVersion);
        AutomationProperties.SetName(_includeVersion, "Include app version in report form link");
        AutomationProperties.SetHelpText(_includeVersion, "Optional. Only the application version is added; no device identifiers or logs are collected.");
        body.Children.Add(_includeVersion);
        _contextPreview = Paragraph(string.Empty);
        AutomationProperties.SetName(_contextPreview, "Report form context preview");
        body.Children.Add(_contextPreview);
        _includeVersion.Checked += (_, _) => UpdatePreview();
        _includeVersion.Unchecked += (_, _) => UpdatePreview();
        UpdatePreview();

        body.Children.Add(Paragraph(
            "Orbit does not attach browsing addresses or history, page contents, private-window activity, cookies, passwords, tokens, logs, or screenshots. On the form, you may choose to type device details or attach redacted screenshots. Never include secrets or private browsing information."));
        var report = Action("Open private report form", OrbitButtonRole.Primary);
        report.Click += async (_, _) => await OpenAsync(ProblemReportRoute.Create(_version, IncludeVersion));
        body.Children.Add(report);

        body.Children.Add(Paragraph(
            "If the Portfolio form is unavailable, you can retry later or use the public GitHub bug form. GitHub issues are public and do not create a Portfolio work order. Security vulnerabilities belong in the private security form."));
        var fallback = Action("Open public GitHub bug form", OrbitButtonRole.Quiet);
        fallback.Click += async (_, _) => await OpenAsync(ProblemReportRoute.PublicBugForm);
        var security = Action("Open private security form", OrbitButtonRole.Quiet);
        security.Click += async (_, _) => await OpenAsync(ProblemReportRoute.PrivateSecurityForm);
        var secondaryActions = new WrapPanel();
        var copy = Action("Copy private report link", OrbitButtonRole.Quiet);
        copy.Click += (_, _) => CopyLink();
        secondaryActions.Children.Add(copy);
        secondaryActions.Children.Add(fallback);
        secondaryActions.Children.Add(security);
        body.Children.Add(secondaryActions);
        _actions = [report, fallback, security];
        _status = Paragraph(openPage is null
            ? "Report links are unavailable in this window. No report has been sent."
            : "No report has been sent. Opening a form does not submit it.");
        AutomationProperties.SetName(_status, "Report form status");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        body.Children.Add(_status);
        foreach (var action in _actions) action.IsEnabled = openPage is not null;
        Content = body;
    }

    internal bool IncludeVersion => _includeVersion.IsChecked == true;
    internal string ContextPreview => _contextPreview.Text;
    internal string Status => _status.Text;

    internal void CopyLink()
    {
        try
        {
            _copyLink(ProblemReportRoute.Create(_version, IncludeVersion).AbsoluteUri);
            _status.Text = "Private report link copied. Open it when ready; no report has been sent.";
        }
        catch (Exception)
        {
            _status.Text = "The report link could not be copied. Retry later; no report has been sent.";
        }
    }

    internal async Task OpenAsync(Uri destination)
    {
        // This control never accepts an arbitrary URI from web content or a setting.
        if (!ProblemReportRoute.IsAllowed(destination, _version) ||
            (destination.AbsolutePath == "/report-issue" &&
             !string.Equals(destination.AbsoluteUri, ProblemReportRoute.Create(_version, IncludeVersion).AbsoluteUri, StringComparison.Ordinal)))
        {
            _status.Text = "That report destination is not allowed. No report has been sent.";
            return;
        }
        if (_opening || _openPage is null) return;
        _opening = true;
        foreach (var action in _actions) action.IsEnabled = false;
        try
        {
            var opened = await _openPage(destination);
            _status.Text = opened
                ? destination == ProblemReportRoute.PublicBugForm
                    ? "Public GitHub form opened. Review and submit there; no Portfolio work order has been sent."
                    : "Form opened in Orbit. Review and submit on the website; no report has been sent by Orbit. If it cannot load, retry later."
                : "The report form could not be opened. No report has been sent. Check your connection or retry later.";
        }
        catch (Exception)
        {
            // Do not expose exception messages that can contain profile or URL data.
            _status.Text = "The report form could not be opened. No report has been sent. Check your connection or retry later.";
        }
        finally
        {
            _opening = false;
            foreach (var action in _actions) action.IsEnabled = true;
        }
    }

    private void UpdatePreview() => _contextPreview.Text =
        $"Link context: Orbit Navigator · Windows{(IncludeVersion ? " · version " + ProblemReportRoute.DisplayVersion(_version) : " · no app version")}. No report text or attachments are included.";

    private static TextBlock Paragraph(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = FoundationSettingsControl.SettingsSecondaryTextBrush,
        Margin = new Thickness(0, 8, 0, 10),
    };

    private static Button Action(string label, OrbitButtonRole role)
    {
        var button = new Button
        {
            Content = label,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 8, 6),
        };
        FoundationSettingsControl.ApplySettingsButtonTheme(button, role);
        AutomationProperties.SetName(button, label);
        return button;
    }
}
