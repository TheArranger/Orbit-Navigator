namespace OrbitNavigator.Presentation.Navigation;

public enum OmniboxTargetKind
{
    Address = 0,
    Search = 1,
}

public sealed record OmniboxTarget(
    OmniboxTargetKind Kind,
    Uri Uri,
    string DisplayInput);

public sealed class OmniboxTargetResolver
{
    public static readonly Uri DefaultSearchEndpoint = new("https://duckduckgo.com/");

    public OmniboxTarget Resolve(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var normalized = input.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Omnibox input cannot be empty.", nameof(input));
        }

        if (TryResolveWebAddress(normalized, out var address))
        {
            return new OmniboxTarget(OmniboxTargetKind.Address, address, normalized);
        }

        var search = new UriBuilder(DefaultSearchEndpoint)
        {
            Query = $"q={Uri.EscapeDataString(normalized)}",
        }.Uri;
        return new OmniboxTarget(OmniboxTargetKind.Search, search, normalized);
    }

    private static bool TryResolveWebAddress(string input, out Uri address)
    {
        if (Uri.TryCreate(input, UriKind.Absolute, out var absolute) &&
            absolute.Scheme is "http" or "https" &&
            string.IsNullOrEmpty(absolute.UserInfo) &&
            !string.IsNullOrWhiteSpace(absolute.IdnHost))
        {
            address = absolute;
            return true;
        }

        if (input.Any(char.IsWhiteSpace) ||
            input.Contains("://", StringComparison.Ordinal) ||
            !(input.Contains('.', StringComparison.Ordinal) ||
              input.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)))
        {
            address = null!;
            return false;
        }

        if (Uri.TryCreate($"https://{input}", UriKind.Absolute, out var inferred) &&
            string.IsNullOrEmpty(inferred.UserInfo) &&
            !string.IsNullOrWhiteSpace(inferred.IdnHost))
        {
            address = inferred;
            return true;
        }

        address = null!;
        return false;
    }
}
