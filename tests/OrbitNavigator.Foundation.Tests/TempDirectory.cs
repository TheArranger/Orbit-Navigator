namespace OrbitNavigator.Foundation.Tests;

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "OrbitNavigator.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        var fullPath = System.IO.Path.GetFullPath(Path);
        var allowedRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "OrbitNavigator.Tests")) + System.IO.Path.DirectorySeparatorChar;
        if (fullPath.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }
}
