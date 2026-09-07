using OrbitNavigator.Contracts.Updates;

namespace OrbitNavigator.Updates;

public sealed class FileUpdatePreferenceStore : IUpdatePreferenceStore
{
    private readonly string _path;

    public FileUpdatePreferenceStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("Update preference path must be absolute.", nameof(path));
        }

        _path = Path.GetFullPath(path);
    }

    public async ValueTask<UpdatePreference> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return UpdatePreference.NotifyOnly;
        }

        var text = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
        return Enum.TryParse<UpdatePreference>(text, ignoreCase: false, out var preference) &&
            Enum.IsDefined(preference)
            ? preference
            : UpdatePreference.NotifyOnly;
    }

    public async ValueTask SaveAsync(
        UpdatePreference preference,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(preference))
        {
            throw new ArgumentOutOfRangeException(nameof(preference));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temp, preference.ToString(), cancellationToken).ConfigureAwait(false);
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
