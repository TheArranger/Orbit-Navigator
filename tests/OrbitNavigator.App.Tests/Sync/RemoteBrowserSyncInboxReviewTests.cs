using System.Text;
using System.Text.Json.Nodes;
using OrbitNavigator.App.Sync;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Sync;
using OrbitNavigator.Sync.Integration;
using OrbitNavigator.Sync.State;
using Xunit;

namespace OrbitNavigator.App.Tests.Sync;

/// <summary>Independent negative review of the received-record persistence boundary.</summary>
public sealed class RemoteBrowserSyncInboxReviewTests
{
    [Theory]
    [InlineData("null-aad")]
    [InlineData("null-mutations")]
    [InlineData("null-pages")]
    [InlineData("changed-payload")]
    [InlineData("removed-mutation")]
    [InlineData("removed-pages")]
    [InlineData("changed-digest")]
    [InlineData("duplicate-page")]
    [InlineData("duplicate-envelope")]
    [InlineData("null-cursor")]
    [InlineData("changed-cursor")]
    [InlineData("null-envelope-ids")]
    [InlineData("missing-envelope-reference")]
    [InlineData("duplicate-envelope-reference")]
    [InlineData("wrong-envelope-reference")]
    public async Task CorruptStoredStateCannotBeReadOrAcknowledgedAsAnExactReplay(string corruption)
    {
        var fixture = new Fixture();
        await fixture.SeedAsync();
        fixture.Storage.CorruptInbox(state =>
        {
            var mutations = state["Mutations"]!.AsArray();
            var pages = state["Pages"]!.AsArray();
            switch (corruption)
            {
                case "null-aad": mutations[0]!["Aad"] = null; break;
                case "null-mutations": state["Mutations"] = null; break;
                case "null-pages": state["Pages"] = null; break;
                case "changed-payload": mutations[0]!["History"]!["Title"] = "Changed after authentication"; break;
                case "removed-mutation": mutations.Clear(); break;
                case "removed-pages": pages.Clear(); break;
                case "changed-digest": pages[0]!["Digest"] = new string('A', 64); break;
                case "duplicate-page": pages.Add(pages[0]!.DeepClone()); break;
                case "duplicate-envelope": mutations.Add(mutations[0]!.DeepClone()); break;
                case "null-cursor": pages[0]!["Cursor"] = null; break;
                case "changed-cursor": pages[0]!["Cursor"] = "different-valid-cursor"; break;
                case "null-envelope-ids": pages[0]!["EnvelopeIds"] = null; break;
                case "missing-envelope-reference": pages[0]!["EnvelopeIds"]!.AsArray().Clear(); break;
                case "duplicate-envelope-reference":
                    var references = pages[0]!["EnvelopeIds"]!.AsArray();
                    references.Add(references[0]!.DeepClone());
                    break;
                case "wrong-envelope-reference": pages[0]!["EnvelopeIds"]![0]!["Value"] = Guid.NewGuid().ToString(); break;
            }
        });
        var writes = fixture.Storage.Writes;
        var restarted = fixture.NewTarget();

        var read = await restarted.ReadAsync(fixture.Browsing, Operation());
        var replay = await restarted.ApplyAsync(fixture.Browsing, Operation(), fixture.Page);

        Assert.False(read.IsSuccess);
        Assert.Equal(ControllerErrorCode.IntegrityFailure, read.Error!.Code);
        Assert.False(replay.IsSuccess);
        Assert.Equal(ControllerErrorCode.IntegrityFailure, replay.Error!.Code);
        Assert.Equal(writes, fixture.Storage.Writes);
    }

    [Fact]
    public async Task MalformedStoredJsonFailsWithoutAnExceptionOrOverwrite()
    {
        var fixture = new Fixture();
        await fixture.SeedAsync();
        fixture.Storage.ReplaceInbox("{ \"Mutations\": [");
        var writes = fixture.Storage.Writes;

        Assert.False((await fixture.NewTarget().ReadAsync(fixture.Browsing, Operation())).IsSuccess);
        Assert.False((await fixture.NewTarget().ApplyAsync(fixture.Browsing, Operation(), fixture.Page)).IsSuccess);
        Assert.Equal(writes, fixture.Storage.Writes);
    }

    [Fact]
    public async Task SchemaValidPayloadWithNullTitleCannotReachStorage()
    {
        var fixture = new Fixture();
        await fixture.BindAsync();
        var upsert = fixture.Page.Upserts[0];
        var malformed = fixture.Page with
        {
            Upserts = [upsert with { Payload = ((HistorySyncRecord)upsert.Payload) with { Title = null! } }],
        };
        var writes = fixture.Storage.Writes;

        var result = await fixture.NewTarget().ApplyAsync(fixture.Browsing, Operation(), malformed);

        Assert.False(result.IsSuccess);
        Assert.Equal(writes, fixture.Storage.Writes);
    }

    [Fact]
    public async Task NewKeyEpochCannotReadOrReplayAnOldEpochInbox()
    {
        var fixture = new Fixture();
        await fixture.SeedAsync();
        var rotated = new RemoteBrowserSyncInbox(fixture.Storage, fixture.Browsing.Privacy.ProfileId,
            fixture.Keyset, 1, fixture.Fence);
        var upsert = fixture.Page.Upserts[0];
        var page = fixture.Page with { Upserts = [upsert with { Aad = upsert.Aad with { KeyEpoch = 1 } }] };

        Assert.False((await rotated.ReadAsync(fixture.Browsing, Operation())).IsSuccess);
        Assert.False((await rotated.ApplyAsync(fixture.Browsing, Operation(), page)).IsSuccess);
    }

    private static SyncOperationId Operation() => new(Guid.NewGuid());

    private sealed class Fixture
    {
        internal MemoryStorage Storage { get; } = new();
        internal BrowsingContext Browsing { get; } = new(
            new(new(Guid.NewGuid()), new(Guid.NewGuid()), BrowserProfileMode.Normal),
            new(Guid.NewGuid()), new(Guid.NewGuid()), null);
        internal ProfileId Account { get; } = new(Guid.NewGuid());
        internal SyncKeysetId Keyset { get; } = new(Guid.NewGuid());
        internal ClientFence Fence { get; } = new(new(Guid.NewGuid()), 0, 0);
        internal AuthenticatedSyncPage Page { get; }

        internal Fixture()
        {
            var aad = new CanonicalSyncAad(1, 1, Account, new(Guid.NewGuid()), Keyset, 0,
                SyncRecordKind.Upsert, new(Guid.NewGuid()), SyncDataCategory.History,
                new(Guid.NewGuid()), null, 0, 1);
            Page = new(new("review-page"), Fence,
                [new(aad, new HistorySyncRecord(aad.EntityId, 1, DateTimeOffset.UnixEpoch,
                    "https://example.test/review", "Reviewed item", DateTimeOffset.UnixEpoch, 1))], [], []);
        }

        internal RemoteBrowserSyncInbox NewTarget() => new(Storage, Browsing.Privacy.ProfileId, Keyset, 0, Fence);
        internal async Task BindAsync() => Assert.True((await new LocalSyncProfileBindingStore(Storage)
            .BindAsync(Browsing, Operation(), Account, LocalSyncProfileBindingKind.JoinedAccount)).IsSuccess);
        internal async Task SeedAsync()
        {
            await BindAsync();
            Assert.True((await NewTarget().ApplyAsync(Browsing, Operation(), Page)).IsSuccess);
        }
    }

    private sealed class MemoryStorage : IProfileStorage
    {
        private readonly Dictionary<string, ProfileStorageEntry> _entries = [];
        internal int Writes { get; private set; }

        internal void CorruptInbox(Action<JsonObject> mutate)
        {
            var state = JsonNode.Parse(_entries["sync.remote-browser"].Payload.Span)!.AsObject();
            mutate(state);
            ReplaceInbox(state.ToJsonString());
        }

        internal void ReplaceInbox(string json)
        {
            var entry = _entries["sync.remote-browser"];
            _entries["sync.remote-browser"] = ProfileStorageEntry.Create(entry.Revision, Encoding.UTF8.GetBytes(json)).Value!;
        }

        public ValueTask<ControllerResult<ProfileStorageEntry>> ReadAsync(
            ProfileStorageAddress address, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_entries.TryGetValue(address.Namespace.Value, out var entry)
                ? ControllerResult<ProfileStorageEntry>.Success(entry)
                : ControllerResult<ProfileStorageEntry>.Failure(ControllerError.Create(ControllerErrorCode.NotFound, "test.not-found")));

        public ValueTask<ControllerResult<ProfileStorageWriteReceipt>> WriteAsync(
            ProfileStorageWriteRequest request, CancellationToken cancellationToken = default)
        {
            if (request.ExpectedRevision is { } expected &&
                (!_entries.TryGetValue(request.Address.Namespace.Value, out var previous) || previous.Revision != expected))
                return ValueTask.FromResult(ControllerResult<ProfileStorageWriteReceipt>.Failure(
                    ControllerError.Create(ControllerErrorCode.Conflict, "test.conflict")));
            var revision = new ProfileStorageRevision(Guid.NewGuid());
            _entries[request.Address.Namespace.Value] = ProfileStorageEntry.Create(revision, request.Payload.Span).Value!;
            Writes++;
            return ValueTask.FromResult(ControllerResult<ProfileStorageWriteReceipt>.Success(new(revision)));
        }

        public ValueTask<ControllerResult> DeleteAsync(ProfileStorageAddress address,
            ProfileStorageRevision? expectedRevision = null, CancellationToken cancellationToken = default)
        {
            _entries.Remove(address.Namespace.Value);
            return ValueTask.FromResult(ControllerResult.Success());
        }
    }
}
