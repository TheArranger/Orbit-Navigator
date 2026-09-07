namespace OrbitNavigator.App;

internal enum ApprovedProjectLink
{
    Donation = 0,
    Portfolio = 1,
}

/// <summary>
/// Host-owned fixed destinations for project links rendered by Presentation.
/// Presentation emits intent only and cannot supply or alter a destination.
/// </summary>
internal static class ApprovedProjectLinks
{
    private static readonly Uri Donation = new("https://ko-fi.com/paradoxthecreator");
    private static readonly Uri Portfolio = new("https://iamtheparadox.com/");

    public static Uri Resolve(ApprovedProjectLink link) => link switch
    {
        ApprovedProjectLink.Donation => Donation,
        ApprovedProjectLink.Portfolio => Portfolio,
        _ => throw new ArgumentOutOfRangeException(nameof(link)),
    };
}
