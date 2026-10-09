namespace OrbitNavigator.App.Support;

/// <summary>
/// A human-reviewed handoff, not an API client. Only the three context fields
/// documented by Portfolio's reportContext contract may leave the browser.
/// </summary>
internal static class ProblemReportRoute
{
    internal const string Project = "orbit-navigator";
    internal const string Platform = "windows";
    internal const string PortfolioForm = "https://iamtheparadox.com/report-issue";
    internal static Uri PublicBugForm { get; } = new(
        "https://github.com/TheArranger/Orbit-Navigator/issues/new?template=bug_report.yml");
    internal static Uri PrivateSecurityForm { get; } = new(
        "https://github.com/TheArranger/Orbit-Navigator/security/advisories/new");

    internal static string DisplayVersion(Version? version) => version is null
        ? "Unknown"
        : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";

    internal static Uri Create(Version? version, bool includeVersion)
    {
        var address = $"{PortfolioForm}?project={Project}&platform={Platform}";
        if (includeVersion && version is not null)
            address += "&version=" + Uri.EscapeDataString(DisplayVersion(version));
        return new Uri(address, UriKind.Absolute);
    }

    internal static bool IsAllowed(Uri? destination, Version? version) =>
        destination is { IsAbsoluteUri: true } &&
        destination.Scheme == Uri.UriSchemeHttps &&
        destination.IsDefaultPort &&
        string.IsNullOrEmpty(destination.UserInfo) &&
        string.IsNullOrEmpty(destination.Fragment) &&
        (string.Equals(destination.AbsoluteUri, Create(version, false).AbsoluteUri, StringComparison.Ordinal) ||
         string.Equals(destination.AbsoluteUri, Create(version, true).AbsoluteUri, StringComparison.Ordinal) ||
         string.Equals(destination.AbsoluteUri, PublicBugForm.AbsoluteUri, StringComparison.Ordinal) ||
         string.Equals(destination.AbsoluteUri, PrivateSecurityForm.AbsoluteUri, StringComparison.Ordinal));
}
