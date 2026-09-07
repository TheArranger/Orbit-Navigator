using System.Text.Json;

namespace OrbitNavigator.Foundation.Diagnostics;

public enum LocalDiagnosticSeverity
{
    Information = 0,
    Warning = 1,
    Error = 2,
}

public enum LocalDiagnosticCode
{
    ApplicationStarted = 0,
    ApplicationStopped = 1,
    WebViewRuntimeUnavailable = 2,
    WebViewInitializationFailed = 3,
    NavigationBlocked = 4,
    PermissionDenied = 5,
    UpdateCheckFailed = 6,
    UpdateReady = 7,
}

public sealed record LocalDiagnosticEvent(
    DateTimeOffset TimestampUtc,
    LocalDiagnosticSeverity Severity,
    LocalDiagnosticCode Code,
    string? ErrorCode = null);

public interface ILocalDiagnostics
{
    ValueTask WriteAsync(
        LocalDiagnosticEvent diagnosticEvent,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Writes event codes to a local JSON-lines file. It intentionally accepts no
/// URL, page title, query, content, account, or device fields.
/// </summary>
public sealed class JsonLineLocalDiagnostics : ILocalDiagnostics, IAsyncDisposable
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonLineLocalDiagnostics(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("Diagnostics path must be absolute.", nameof(path));
        }

        _path = Path.GetFullPath(path);
    }

    public async ValueTask WriteAsync(
        LocalDiagnosticEvent diagnosticEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);
        if (!Enum.IsDefined(diagnosticEvent.Severity) || !Enum.IsDefined(diagnosticEvent.Code))
        {
            throw new ArgumentOutOfRangeException(nameof(diagnosticEvent));
        }

        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var line = JsonSerializer.Serialize(diagnosticEvent) + Environment.NewLine;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(_path, line, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
