using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Sync;
using OrbitNavigator.Sync.Integration;
using OrbitNavigator.Sync.State;
using Xunit;

namespace OrbitNavigator.Sync.Tests.State;

public sealed class ProfileStorageLocalSyncChangeCatalogTests
{
    [Fact]
    public async Task PrivateRecordIsDeniedBeforeStorage()
    {
        var storage = new MemoryStorage();
        var catalog = new ProfileStorageLocalSyncChangeCatalog(storage);

        var result = await catalog.RecordAsync(
            Browsing(BrowserProfileMode.Private),
            Operation(),
            SyncRecordKind.Upsert,
            SyncDataCategory.History,
            Entity());

        Assert.Equal(ControllerErrorCode.PolicyDenied, result.Error?.Code);
        Assert.Equal(0, storage.CallCount);
    }

    [Fact]
    public async Task JournalPersistsOnlyOpaqueChangeMetadataAndStableCursors()
    {
        var storage = new MemoryStorage();
        var catalog = new ProfileStorageLocalSyncChangeCatalog(storage);
        var browsing = Browsing(BrowserProfileMode.Normal, KnownProfile);
        var firstEntity = Entity();
        var secondEntity = Entity();

        var first = await catalog.RecordAsync(
            browsing,
            Operation(),
            SyncRecordKind.Upsert,
            SyncDataCategory.History,
            firstEntity);
        var second = await catalog.RecordAsync(
            browsing,
            Operation(),
            SyncRecordKind.Tombstone,
            SyncDataCategory.OpenTabs,
            secondEntity);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal("1", first.Value!.Cursor.Value);
        Assert.Equal("2", second.Value!.Cursor.Value);
        Assert.NotNull(storage.Payload);
        Assert.DoesNotContain("https", System.Text.Encoding.UTF8.GetString(storage.Payload!));

        var page = await catalog.ListPendingAsync(
            Context(browsing),
            first.Value.Cursor,
            10,
            CancellationToken.None);
        Assert.True(page.IsSuccess);
        var change = Assert.Single(page.Value!.Changes);
        Assert.Equal(secondEntity, change.EntityId);
        Assert.Equal(SyncRecordKind.Tombstone, change.RecordKind);
        Assert.Equal("2", page.Value.NextCursor!.Value);
        Assert.False(page.Value.HasMore);
    }

    [Fact]
    public async Task PaginationPreservesCanonicalAppendOrderAcrossRestart()
    {
        var storage = new MemoryStorage();
        var browsing = Browsing(BrowserProfileMode.Normal, KnownProfile);
        var catalog = new ProfileStorageLocalSyncChangeCatalog(storage);
        for (var index = 0; index < 3; index++)
        {
            Assert.True((await catalog.RecordAsync(
                browsing,
                Operation(),
                SyncRecordKind.Upsert,
                index == 1 ? SyncDataCategory.OpenTabs : SyncDataCategory.History,
                Entity())).IsSuccess);
        }

        var restarted = new ProfileStorageLocalSyncChangeCatalog(storage);
        var firstPage = await restarted.ListPendingAsync(
            Context(browsing),
            null,
            2,
            CancellationToken.None);
        var secondPage = await restarted.ListPendingAsync(
            Context(browsing),
            firstPage.Value!.NextCursor,
            2,
            CancellationToken.None);

        Assert.True(firstPage.IsSuccess);
        Assert.Equal(2, firstPage.Value.Changes.Count);
        Assert.True(firstPage.Value.HasMore);
        Assert.Equal("2", firstPage.Value.NextCursor!.Value);
        Assert.True(secondPage.IsSuccess);
        Assert.Single(secondPage.Value!.Changes);
        Assert.False(secondPage.Value.HasMore);
        Assert.Equal("3", secondPage.Value.NextCursor!.Value);
    }

    [Fact]
    public async Task CorruptJournalFailsClosed()
    {
        var storage = new MemoryStorage { Payload = [1, 2, 3, 4] };
        var browsing = Browsing(BrowserProfileMode.Normal, KnownProfile);
        var catalog = new ProfileStorageLocalSyncChangeCatalog(storage);

        var result = await catalog.ListPendingAsync(
            Context(browsing),
            null,
            10,
            CancellationToken.None);

        Assert.Equal(ControllerErrorCode.IntegrityFailure, result.Error?.Code);
    }

    [Theory]
    [InlineData(SyncRecordKind.Purge, SyncDataCategory.History)]
    [InlineData(SyncRecordKind.Upsert, SyncDataCategory.Settings)]
    public async Task V1RejectsPurgesAndSettingsBeforeWrite(
        SyncRecordKind kind,
        SyncDataCategory category)
    {
        var storage = new MemoryStorage();
        var catalog = new ProfileStorageLocalSyncChangeCatalog(storage);

        var result = await catalog.RecordAsync(
            Browsing(BrowserProfileMode.Normal),
            Operation(),
            kind,
            category,
            Entity());

        Assert.Equal(ControllerErrorCode.InvalidRequest, result.Error?.Code);
        Assert.Equal(0, storage.WriteCount);
    }

    [Fact]
    public async Task MixedProfileJournalFailsIntegrityValidation()
    {
        var storage = new MemoryStorage();
        var catalog = new ProfileStorageLocalSyncChangeCatalog(storage);
        Assert.True((await catalog.RecordAsync(
            Browsing(BrowserProfileMode.Normal, KnownProfile),
            Operation(),
            SyncRecordKind.Upsert,
            SyncDataCategory.History,
            Entity())).IsSuccess);

        var result = await catalog.ListPendingAsync(
            Context(Browsing(BrowserProfileMode.Normal, new ProfileId(Guid.NewGuid()))),
            null,
            10,
            CancellationToken.None);

        Assert.Equal(ControllerErrorCode.IntegrityFailure, result.Error?.Code);
    }

    private static readonly ProfileId KnownProfile =
        new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));

    private static SyncOperationId Operation() => new(Guid.NewGuid());
    private static SyncEntityId Entity() => new(Guid.NewGuid());

    private static BrowsingContext Browsing(
        BrowserProfileMode mode,
        ProfileId? profile = null) =>
        new(
            new PrivacyContext(
                profile ?? new ProfileId(Guid.NewGuid()),
                new BrowserSessionId(Guid.NewGuid()),
                mode),
            new BrowserWindowId(Guid.NewGuid()),
            new BrowserTabId(Guid.NewGuid()),
            null);

    private static SyncOperationContext Context(BrowsingContext browsing) =>
        SyncOperationContext.Authorize(browsing, Operation()).Value!;

    private sealed class MemoryStorage : IProfileStorage
    {
        public byte[]? Payload { get; set; }
        public ProfileStorageRevision? Revision { get; private set; }
        public int CallCount { get; private set; }
        public int WriteCount { get; private set; }

        public ValueTask<ControllerResult<ProfileStorageEntry>> ReadAsync(
            ProfileStorageAddress address,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (Payload is null)
            {
                return ValueTask.FromResult(ControllerResult<ProfileStorageEntry>.Failure(
                    ControllerError.Create(
                        ControllerErrorCode.NotFound,
                        "error.profile_storage.not-found")));
            }

            Revision ??= new ProfileStorageRevision(Guid.NewGuid());
            return ValueTask.FromResult(ProfileStorageEntry.Create(Revision.Value, Payload));
        }

        public ValueTask<ControllerResult<ProfileStorageWriteReceipt>> WriteAsync(
            ProfileStorageWriteRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            WriteCount++;
            if (request.ExpectedRevision is { } expected && expected != Revision)
            {
                return ValueTask.FromResult(ControllerResult<ProfileStorageWriteReceipt>.Failure(
                    ControllerError.Create(
                        ControllerErrorCode.Conflict,
                        "error.profile_storage.revision-conflict")));
            }
            Payload = request.Payload.ToArray();
            Revision = new ProfileStorageRevision(Guid.NewGuid());
            return ValueTask.FromResult(
                ControllerResult<ProfileStorageWriteReceipt>.Success(new(Revision.Value)));
        }

        public ValueTask<ControllerResult> DeleteAsync(
            ProfileStorageAddress address,
            ProfileStorageRevision? expectedRevision = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Payload = null;
            Revision = null;
            return ValueTask.FromResult(ControllerResult.Success());
        }
    }
}
