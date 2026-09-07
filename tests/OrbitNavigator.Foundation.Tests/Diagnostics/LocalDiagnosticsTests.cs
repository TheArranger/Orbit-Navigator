using System.Text.Json;
using OrbitNavigator.Foundation.Diagnostics;
using Xunit;

namespace OrbitNavigator.Foundation.Tests.Diagnostics;

public sealed class LocalDiagnosticsTests
{
    [Fact]
    public async Task LocalLogContainsOnlyAllowlistedEventFields()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "orbit.log.jsonl");
        await using var diagnostics = new JsonLineLocalDiagnostics(path);

        await diagnostics.WriteAsync(new LocalDiagnosticEvent(
            DateTimeOffset.UtcNow,
            LocalDiagnosticSeverity.Warning,
            LocalDiagnosticCode.NavigationBlocked,
            "policy-denied"));

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var names = document.RootElement.EnumerateObject().Select(item => item.Name).ToArray();
        Assert.Equal(
            new[] { "TimestampUtc", "Severity", "Code", "ErrorCode" },
            names);
        Assert.DoesNotContain(names, name =>
            name.Contains("Url", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Content", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Account", StringComparison.OrdinalIgnoreCase));
    }
}
