namespace OrbitNavigator.Launcher;

public static class LauncherPaths
{
    public const string PayloadDirectoryName = "app";
    public const string ApplicationFileName = "OrbitNavigator.App.exe";

    public static string ResolveApplicationPath(string launcherDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherDirectory);

        var root = Path.GetFullPath(launcherDirectory);
        var target = Path.GetFullPath(Path.Combine(root, PayloadDirectoryName, ApplicationFileName));
        var expectedPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!target.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The application payload resolved outside the launcher root.");
        }

        return target;
    }
}

