using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.Foundation.Profiles;

public sealed class LocalProfileIdentityStore
{
    private readonly string _path;

    public LocalProfileIdentityStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("Profile identity path must be absolute.", nameof(path));
        }

        _path = Path.GetFullPath(path);
    }

    public async ValueTask<ProfileId> LoadOrCreateAsync(
        CancellationToken cancellationToken = default)
    {
        if (File.Exists(_path))
        {
            var existing = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
            if (Guid.TryParseExact(existing.Trim(), "N", out var value) && value != Guid.Empty)
            {
                return new ProfileId(value);
            }
        }

        var profileId = new ProfileId(Guid.NewGuid());
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temp,
                profileId.Value.ToString("N"),
                cancellationToken).ConfigureAwait(false);
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }

        return profileId;
    }
}
