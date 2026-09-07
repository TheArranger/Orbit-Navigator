using System.IO;
using System.Text.Json;

namespace OrbitNavigator.App;

internal sealed record AcceptanceRunContext(
    bool IsAcceptance,
    Guid RunId,
    LocalDataPaths Paths)
{
    private const string RootArgument = "--acceptance-profile-root";
    private const string RunIdArgument = "--acceptance-run-id";

    public static AcceptanceRunContext Create(
        IReadOnlyList<string> arguments,
        string localApplicationData,
        string temporaryRoot,
        string? legacyEnvironmentOverride)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var root = ReadValue(arguments, RootArgument);
        var runIdText = ReadValue(arguments, RunIdArgument);
        if ((root is null) != (runIdText is null))
        {
            throw new InvalidOperationException(
                "Acceptance launches require both an isolated profile root and a run identifier.");
        }

        if (root is not null)
        {
            if (!Guid.TryParse(runIdText, out var runId) || runId == Guid.Empty)
            {
                throw new InvalidOperationException("The acceptance run identifier is invalid.");
            }
            var resolved = LocalDataRootResolver.Resolve(localApplicationData, temporaryRoot, root);
            return new(true, runId, LocalDataPaths.Create(resolved));
        }

        if (!string.IsNullOrWhiteSpace(legacyEnvironmentOverride))
        {
            throw new InvalidOperationException(
                "Environment-only acceptance roots are no longer accepted. Use the paired command-line arguments.");
        }

        var canonical = LocalDataRootResolver.Resolve(localApplicationData, temporaryRoot, null);
        return new(false, Guid.Empty, LocalDataPaths.Create(canonical));
    }

    public async Task WriteAttestationAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAcceptance)
        {
            return;
        }

        Directory.CreateDirectory(Paths.Root);
        var path = Path.Combine(Paths.Root, "acceptance-root-attestation.json");
        var temporary = path + ".tmp";
        var payload = JsonSerializer.Serialize(new
        {
            RunId,
            ProcessId = Environment.ProcessId,
            ExecutablePath = Environment.ProcessPath,
            Paths.Root,
            Paths.ProfilesRoot,
            Paths.ProfileStorageRoot,
            Paths.WebViewRoot,
            Paths.LogsRoot,
            Paths.OfflineReadingRoot,
            Paths.UpdatesRoot,
            StartedAtUtc = DateTimeOffset.UtcNow,
        }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(temporary, payload, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    private static string? ReadValue(IReadOnlyList<string> arguments, string name)
    {
        string? value = null;
        for (var index = 0; index < arguments.Count; index++)
        {
            if (!string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (value is not null || index + 1 >= arguments.Count ||
                arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"The {name} argument is missing or duplicated.");
            }
            value = arguments[++index];
        }
        return value;
    }
}
