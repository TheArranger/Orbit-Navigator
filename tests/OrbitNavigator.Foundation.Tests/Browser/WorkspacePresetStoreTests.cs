using System.Text.Json;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Foundation.Browser;
using OrbitNavigator.Foundation.Profiles;
using Xunit;

namespace OrbitNavigator.Foundation.Tests.Browser;

public sealed class WorkspacePresetStoreTests
{
    [Fact]
    public async Task PresetsAreDurableOrderedAndOptimisticallyRevisioned()
    {
        using var temp = new TempDirectory();
        var clock = new FixedClock(new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero));
        var storage = new FileProfileStorage(temp.Path);
        var store = new WorkspacePresetStore(storage, clock);
        var context = Context(BrowserProfileMode.Normal);
        var tabs = new[]
        {
            new WorkspacePresetTab(new Uri("https://example.com/one"), "One"),
            new WorkspacePresetTab(new Uri("https://example.com/two"), "Two"),
        };

        var created = await store.UpsertAsync(new(
            context,
            default,
            null,
            "Morning research",
            "Research",
            tabs)
        {
            ColorToken = "Violet",
            Note = "Start with the first site.",
            Artwork = new(StoredWorkspaceArtworkKind.Site, tabs[0].Target, null, "First site"),
            FirstTabIndex = 1,
        });
        var durable = await new WorkspacePresetStore(storage, clock).QueryAsync(context);
        var stale = await store.UpsertAsync(new(
            context,
            default,
            null,
            "Stale",
            null,
            tabs));

        var preset = Assert.Single(durable.Value!.Presets);
        Assert.True(created.IsSuccess);
        Assert.False(created.Value!.Revision.IsEmpty);
        Assert.False(preset.Id.IsEmpty);
        Assert.Equal(context.ProfileId, preset.Id.ProfileId);
        Assert.Equal(new[] { "One", "Two" }, preset.Tabs.Select(tab => tab.DisplayTitle));
        Assert.Equal(clock.UtcNow, preset.CreatedAtUtc);
        Assert.Equal(clock.UtcNow, preset.UpdatedAtUtc);
        Assert.Equal("Violet", preset.ColorToken);
        Assert.Equal("Start with the first site.", preset.Note);
        Assert.Equal(StoredWorkspaceArtworkKind.Site, preset.Artwork.Kind);
        Assert.Equal(1, preset.FirstTabIndex);
        Assert.False(stale.IsSuccess);
        Assert.Equal(ControllerErrorCode.Conflict, stale.Error?.Code);
    }

    [Fact]
    public async Task PrivateWindowCanReadButCannotMutateNormalPresets()
    {
        using var temp = new TempDirectory();
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var store = new WorkspacePresetStore(new FileProfileStorage(temp.Path), clock);
        var profile = new ProfileId(Guid.NewGuid());
        var normal = Context(BrowserProfileMode.Normal, profile);
        var privateContext = Context(BrowserProfileMode.Private, profile);
        var tab = new WorkspacePresetTab(new Uri("https://example.com/"), "Example");
        var created = await store.UpsertAsync(new(
            normal,
            default,
            null,
            "Normal preset",
            null,
            [tab]));
        var preset = Assert.Single(created.Value!.Presets);

        var privateQuery = await store.QueryAsync(privateContext);
        var privateUpsert = await store.UpsertAsync(new(
            privateContext,
            privateQuery.Value!.Revision,
            preset.Id,
            "Changed",
            null,
            [tab]));
        var privateRemove = await store.RemoveAsync(new(
            privateContext,
            privateQuery.Value.Revision,
            preset.Id));
        var normalQuery = await store.QueryAsync(normal);

        Assert.Single(privateQuery.Value.Presets);
        Assert.Equal(ControllerErrorCode.PolicyDenied, privateUpsert.Error?.Code);
        Assert.Equal(ControllerErrorCode.PolicyDenied, privateRemove.Error?.Code);
        Assert.Equal("Normal preset", Assert.Single(normalQuery.Value!.Presets).Name);
        Assert.Equal(created.Value.Revision, normalQuery.Value.Revision);
    }

    [Fact]
    public async Task NonHttpTargetsAreRejectedWithoutMutation()
    {
        using var temp = new TempDirectory();
        var store = new WorkspacePresetStore(
            new FileProfileStorage(temp.Path),
            new FixedClock(DateTimeOffset.UtcNow));
        var context = Context(BrowserProfileMode.Normal);

        var result = await store.UpsertAsync(new(
            context,
            default,
            null,
            "Unsafe",
            null,
            [new WorkspacePresetTab(new Uri("file:///C:/secret.txt"), "Local file")]));
        var query = await store.QueryAsync(context);

        Assert.Equal(ControllerErrorCode.InvalidRequest, result.Error?.Code);
        Assert.Empty(query.Value!.Presets);
    }

    [Fact]
    public async Task LegacyCatalogMigratesKnownMyOrbitOriginAndWritesVersionedEnvelopeByCas()
    {
        using var temp = new TempDirectory();
        var storage = new FileProfileStorage(temp.Path);
        var profile = new ProfileId(Guid.NewGuid());
        var context = Context(BrowserProfileMode.Normal, profile);
        var id = new WorkspacePresetId(profile, Guid.NewGuid());
        var created = new DateTimeOffset(2026, 8, 18, 18, 31, 57, TimeSpan.FromHours(-7));
        var legacy = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            new
            {
                Id = id,
                Name = "Test",
                GroupName = (string?)null,
                Tabs = new[]
                {
                    new { Target = new Uri("https://my-orbit.ping-it.cc/path?q=1"), DisplayTitle = "Morbit" },
                },
                CreatedAtUtc = created,
                UpdatedAtUtc = created,
            },
        });
        var address = WorkspaceCatalogAddress(context);
        var seeded = await storage.WriteAsync(ProfileStorageWriteRequest.Create(address, legacy).Value!);

        var migrated = await new WorkspacePresetStore(storage, new FixedClock(created)).QueryAsync(context);
        var stored = await storage.ReadAsync(address);
        using var document = JsonDocument.Parse(stored.Value!.Payload);

        Assert.True(migrated.IsSuccess);
        var preset = Assert.Single(migrated.Value!.Presets);
        Assert.Equal("https://my-orbit.snap-it.cc/path?q=1", preset.Tabs[0].Target.AbsoluteUri);
        Assert.Equal("SeaGlass", preset.ColorToken);
        Assert.Equal(0, preset.FirstTabIndex);
        Assert.Equal(created, preset.CreatedAtUtc);
        Assert.NotEqual(seeded.Value!.Revision.Value, migrated.Value.Revision.Value);
        Assert.Equal(WorkspacePresetStore.CurrentSchemaVersion,
            document.RootElement.GetProperty("SchemaVersion").GetInt32());
    }

    [Fact]
    public async Task PrivateLegacyReadNormalizesInMemoryWithoutWritingOrRewritingUnknownOrigin()
    {
        using var temp = new TempDirectory();
        var storage = new FileProfileStorage(temp.Path);
        var profile = new ProfileId(Guid.NewGuid());
        var normal = Context(BrowserProfileMode.Normal, profile);
        var privateContext = Context(BrowserProfileMode.Private, profile);
        var id = new WorkspacePresetId(profile, Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;
        var legacy = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            new
            {
                Id = id,
                Name = "Unknown",
                GroupName = (string?)null,
                Tabs = new[] { new { Target = new Uri("https://example.net/a"), DisplayTitle = "Example" } },
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            },
        });
        var address = WorkspaceCatalogAddress(normal);
        var seeded = await storage.WriteAsync(ProfileStorageWriteRequest.Create(address, legacy).Value!);

        var queried = await new WorkspacePresetStore(storage, new FixedClock(now)).QueryAsync(privateContext);
        var after = await storage.ReadAsync(address);

        Assert.True(queried.IsSuccess);
        Assert.Equal("https://example.net/a", Assert.Single(queried.Value!.Presets).Tabs[0].Target.AbsoluteUri);
        Assert.Equal(seeded.Value!.Revision, after.Value!.Revision);
        Assert.Equal(seeded.Value.Revision.Value, queried.Value.Revision.Value);
    }

    private static ProfileStorageAddress WorkspaceCatalogAddress(PrivacyContext context) =>
        ProfileStorageAddress.Create(
            context,
            ProfileStorageNamespace.Create("browser.workspace-presets").Value,
            ProfileStorageKey.Create("catalog").Value,
            ProfileStorageDurability.Persistent).Value!;

    private static PrivacyContext Context(BrowserProfileMode mode, ProfileId? profile = null) =>
        new(
            profile ?? new ProfileId(Guid.NewGuid()),
            new BrowserSessionId(Guid.NewGuid()),
            mode);

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
