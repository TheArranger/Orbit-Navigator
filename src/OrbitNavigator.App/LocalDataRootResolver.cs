using System.IO;

namespace OrbitNavigator.App;

internal static class LocalDataRootResolver
{
    internal const string AcceptanceEnvironmentVariable = "ORBIT_ACCEPTANCE_PROFILE_ROOT";

    public static string Resolve(string localApplicationData, string temporaryRoot, string? acceptanceOverride)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationData);
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryRoot);
        var canonical = Path.GetFullPath(Path.Combine(localApplicationData, "Orbit Navigator"));
        if (string.IsNullOrWhiteSpace(acceptanceOverride))
        {
            return canonical;
        }

        var allowedParent = Path.GetFullPath(Path.Combine(temporaryRoot, "OrbitNavigatorAcceptance"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(acceptanceOverride)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var requiredPrefix = allowedParent + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals(canonical, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The acceptance profile root must be a child of the disposable Orbit acceptance directory.");
        }

        return candidate;
    }
}
