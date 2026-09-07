using System.IO;

namespace OrbitNavigator.App;

internal sealed record LocalDataPaths(
    string Root,
    string ProfilesRoot,
    string ProfileStorageRoot,
    string WebViewRoot,
    string LogsRoot,
    string OfflineReadingRoot,
    string UpdatesRoot)
{
    public static LocalDataPaths Create(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var resolved = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (!Path.IsPathFullyQualified(resolved))
        {
            throw new ArgumentException("The local data root must be absolute.", nameof(root));
        }

        return new(
            resolved,
            Child(resolved, "profiles"),
            Child(resolved, "profiles", "data"),
            Child(resolved, "webview"),
            Child(resolved, "logs"),
            Child(resolved, "offline-reading"),
            Child(resolved, "updates"));
    }

    private static string Child(string root, params string[] segments)
    {
        var candidate = Path.GetFullPath(segments.Aggregate(
            root,
            static (current, segment) => Path.Combine(current, segment)));
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A local data path escaped its configured root.");
        }
        return candidate;
    }
}
