using System.Diagnostics;
using System.Windows.Forms;

namespace OrbitNavigator.Launcher;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var smokeOnly = args.Any(argument =>
            string.Equals(argument, "--launcher-smoke", StringComparison.OrdinalIgnoreCase));
        var launcherDirectory = AppContext.BaseDirectory;
        var applicationPath = LauncherPaths.ResolveApplicationPath(launcherDirectory);

        if (!File.Exists(applicationPath))
        {
            if (!smokeOnly)
            {
                MessageBox.Show(
                    "Orbit Navigator's application files are missing. Reinstall or rebuild Orbit Navigator.",
                    "Orbit Navigator",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            return 2;
        }

        if (smokeOnly)
        {
            return 0;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = applicationPath,
            WorkingDirectory = Path.GetDirectoryName(applicationPath)!,
            UseShellExecute = false,
        };

        foreach (var argument in args)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            Process.Start(startInfo);
            return 0;
        }
        catch (Exception)
        {
            MessageBox.Show(
                "Orbit Navigator could not start. Reinstall the application or contact support.",
                "Orbit Navigator",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 3;
        }
    }
}
