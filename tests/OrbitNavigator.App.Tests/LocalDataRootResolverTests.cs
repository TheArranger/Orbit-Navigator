using Xunit;
using System.Text.Json;
using OrbitNavigator.App.Diagnostics;
using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.App.Tests;

public sealed class LocalDataRootResolverTests
{
    [Fact]
    public void NormalLaunchAlwaysUsesCanonicalLocalRoot()
    {
        var result = LocalDataRootResolver.Resolve(
            @"C:\Users\Test\AppData\Local",
            @"C:\Temp",
            null);

        Assert.Equal(@"C:\Users\Test\AppData\Local\Orbit Navigator", result);
    }

    [Fact]
    public void AcceptanceOverrideMustBeInsideDedicatedTemporaryParent()
    {
        var accepted = LocalDataRootResolver.Resolve(
            @"C:\Users\Test\AppData\Local",
            @"C:\Temp",
            @"C:\Temp\OrbitNavigatorAcceptance\run-123\profile");

        Assert.Equal(@"C:\Temp\OrbitNavigatorAcceptance\run-123\profile", accepted);
        Assert.Throws<InvalidOperationException>(() => LocalDataRootResolver.Resolve(
            @"C:\Users\Test\AppData\Local",
            @"C:\Temp",
            @"C:\Users\Test\AppData\Local\Orbit Navigator"));
        Assert.Throws<InvalidOperationException>(() => LocalDataRootResolver.Resolve(
            @"C:\Users\Test\AppData\Local",
            @"C:\Temp",
            @"C:\Temp\other-profile"));
        Assert.Throws<InvalidOperationException>(() => LocalDataRootResolver.Resolve(
            @"C:\Users\Test\AppData\Local",
            @"C:\Temp",
            @"C:\Temp\OrbitNavigatorAcceptance"));
    }

    [Fact]
    public void AcceptanceContextRequiresPairedExplicitArgumentsAndDerivesEveryPath()
    {
        var runId = Guid.NewGuid();
        var context = AcceptanceRunContext.Create(
            [
                "--acceptance-profile-root",
                @"C:\Temp\OrbitNavigatorAcceptance\run-123\profile",
                "--acceptance-run-id",
                runId.ToString(),
            ],
            @"C:\Users\Test\AppData\Local",
            @"C:\Temp",
            null);

        Assert.True(context.IsAcceptance);
        Assert.Equal(runId, context.RunId);
        Assert.Equal(@"C:\Temp\OrbitNavigatorAcceptance\run-123\profile", context.Paths.Root);
        Assert.Equal(Path.Combine(context.Paths.Root, "profiles", "data"), context.Paths.ProfileStorageRoot);
        Assert.Equal(Path.Combine(context.Paths.Root, "webview"), context.Paths.WebViewRoot);
        Assert.Equal(Path.Combine(context.Paths.Root, "logs"), context.Paths.LogsRoot);
        Assert.Equal(Path.Combine(context.Paths.Root, "offline-reading"), context.Paths.OfflineReadingRoot);
        Assert.Equal(Path.Combine(context.Paths.Root, "updates"), context.Paths.UpdatesRoot);
    }

    [Fact]
    public void AcceptanceContextRejectsEnvironmentOnlyAndUnpairedOverrides()
    {
        Assert.Throws<InvalidOperationException>(() => AcceptanceRunContext.Create(
            [], @"C:\Users\Test\AppData\Local", @"C:\Temp",
            @"C:\Temp\OrbitNavigatorAcceptance\legacy\profile"));
        Assert.Throws<InvalidOperationException>(() => AcceptanceRunContext.Create(
            ["--acceptance-profile-root", @"C:\Temp\OrbitNavigatorAcceptance\run\profile"],
            @"C:\Users\Test\AppData\Local", @"C:\Temp", null));
        Assert.Throws<InvalidOperationException>(() => AcceptanceRunContext.Create(
            ["--acceptance-run-id", Guid.NewGuid().ToString()],
            @"C:\Users\Test\AppData\Local", @"C:\Temp", null));
    }

    [Fact]
    public void SameRootLeaseRejectsASecondOwnerWhileDifferentRootRemainsIndependent()
    {
        var firstRoot = Path.Combine(Path.GetTempPath(), "OrbitNavigatorAcceptance", Guid.NewGuid().ToString("N"));
        var secondRoot = Path.Combine(Path.GetTempPath(), "OrbitNavigatorAcceptance", Guid.NewGuid().ToString("N"));
        using var first = LocalDataRootInstanceLease.TryAcquire(firstRoot);
        using var duplicate = LocalDataRootInstanceLease.TryAcquire(firstRoot);
        using var independent = LocalDataRootInstanceLease.TryAcquire(secondRoot);

        Assert.NotNull(first);
        Assert.Null(duplicate);
        Assert.NotNull(independent);
    }

    [Fact]
    public async Task AcceptanceAttestationAndAuditStayInsideDisposableRootAndContainCountsOnly()
    {
        var runId = Guid.NewGuid();
        var root = Path.Combine(
            Path.GetTempPath(),
            "OrbitNavigatorAcceptance",
            runId.ToString("N"),
            "profile");
        try
        {
            var context = AcceptanceRunContext.Create(
                [
                    "--acceptance-profile-root", root,
                    "--acceptance-run-id", runId.ToString("D"),
                ],
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Path.GetTempPath(),
                null);
            await context.WriteAttestationAsync();
            var attestationPath = Path.Combine(root, "acceptance-root-attestation.json");
            using var attestation = JsonDocument.Parse(await File.ReadAllTextAsync(attestationPath));

            var auditPath = Path.Combine(root, "audit", "workspace-state.jsonl");
            var sink = new FileBrowserWorkspaceAuditSink(auditPath);
            sink.Record(new(
                DateTimeOffset.UtcNow,
                new BrowserWindowId(Guid.NewGuid()),
                false,
                16,
                16,
                2,
                16,
                16,
                "render"));
            var audit = await File.ReadAllTextAsync(auditPath);

            Assert.Equal(runId, attestation.RootElement.GetProperty("RunId").GetGuid());
            Assert.Equal(Path.GetFullPath(root), attestation.RootElement.GetProperty("Root").GetString());
            Assert.Contains("\"TabCount\":16", audit, StringComparison.Ordinal);
            Assert.DoesNotContain("http", audit, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Title", audit, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);
            }
        }
    }
}
