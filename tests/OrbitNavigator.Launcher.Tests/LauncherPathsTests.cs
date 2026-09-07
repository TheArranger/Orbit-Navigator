using OrbitNavigator.Launcher;
using Xunit;

namespace OrbitNavigator.Launcher.Tests;

public sealed class LauncherPathsTests
{
    [Fact]
    public void ResolveApplicationPath_UsesFixedPayloadLocation()
    {
        var root = Path.Combine(Path.GetTempPath(), "Orbit Navigator");

        var result = LauncherPaths.ResolveApplicationPath(root);

        Assert.Equal(
            Path.GetFullPath(Path.Combine(root, "app", "OrbitNavigator.App.exe")),
            result);
    }

    [Fact]
    public void ResolveApplicationPath_RejectsBlankRoot()
    {
        Assert.Throws<ArgumentException>(() => LauncherPaths.ResolveApplicationPath(" "));
    }
}
