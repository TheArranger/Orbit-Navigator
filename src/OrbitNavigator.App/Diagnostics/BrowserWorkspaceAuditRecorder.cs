using System.IO;
using System.Text.Json;
using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.App.Diagnostics;

public sealed record BrowserWorkspaceAuditSnapshot(
    DateTimeOffset TimestampUtc,
    BrowserWindowId WindowId,
    bool IsPrivate,
    long Revision,
    int TabCount,
    int GroupCount,
    int HostCount,
    int ProjectionEntryCount,
    string Reason);

public interface IBrowserWorkspaceAuditSink
{
    void Record(BrowserWorkspaceAuditSnapshot snapshot);
}

internal sealed class FileBrowserWorkspaceAuditSink : IBrowserWorkspaceAuditSink
{
    private readonly string _path;
    private readonly object _gate = new();

    public FileBrowserWorkspaceAuditSink(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    }

    public void Record(BrowserWorkspaceAuditSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var line = JsonSerializer.Serialize(snapshot) + Environment.NewLine;
        lock (_gate)
        {
            File.AppendAllText(_path, line);
        }
    }
}
