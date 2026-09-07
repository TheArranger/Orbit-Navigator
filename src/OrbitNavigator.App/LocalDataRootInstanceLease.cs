using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace OrbitNavigator.App;

internal sealed class LocalDataRootInstanceLease : IDisposable
{
    private Mutex? _mutex;

    private LocalDataRootInstanceLease(Mutex mutex)
    {
        _mutex = mutex;
    }

    public static LocalDataRootInstanceLease? TryAcquire(string localDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localDataRoot);
        var canonical = Path.GetFullPath(localDataRoot).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar).ToUpperInvariant();
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        var mutex = new Mutex(initiallyOwned: true, $"Local\\OrbitNavigator.Profile.{digest}", out var created);
        if (!created)
        {
            mutex.Dispose();
            return null;
        }
        return new(mutex);
    }

    public void Dispose()
    {
        var mutex = Interlocked.Exchange(ref _mutex, null);
        if (mutex is null)
        {
            return;
        }
        mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
