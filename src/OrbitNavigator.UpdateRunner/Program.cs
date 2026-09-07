using System.Diagnostics;
using OrbitNavigator.Contracts.Updates;
using OrbitNavigator.Updates;

namespace OrbitNavigator.UpdateRunner;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (!UpdateRunnerArguments.TryParse(args, out var options))
        {
            return 2;
        }

        var stagingRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "updates", "staging"))
            .TrimEnd(Path.DirectorySeparatorChar);
        var packagePath = Path.GetFullPath(options!.PackagePath);
        if (!packagePath.StartsWith(
                stagingRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(packagePath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (options.WaitForProcessId is { } waitPid)
        {
            try
            {
                using var process = Process.GetProcessById(waitPid);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
            }
            catch (ArgumentException)
            {
                // The application already exited.
            }
            catch (TimeoutException)
            {
                return 4;
            }
        }

        var package = new UpdatePackageInfo(
            new Version(0, 0),
            new Uri("https://updates.invalid/staged"),
            options.Sha256,
            options.SizeBytes,
            true);
        var staged = new StagedUpdatePackage(package, packagePath);
        var integrity = await UpdatePackageIntegrity.VerifyAsync(staged);
        if (!integrity.IsSuccess)
        {
            return 5;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = packagePath,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(packagePath)!,
                // Unsigned Beta packages must always retain the visible Windows and
                // Setup trust surfaces. No silent installer arguments are permitted.
            });
            return 0;
        }
        catch
        {
            return 6;
        }
    }
}
