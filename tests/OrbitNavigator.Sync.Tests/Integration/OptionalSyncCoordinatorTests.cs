using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Sync;
using OrbitNavigator.Sync.Integration;
using OrbitNavigator.Sync.State;
using Xunit;

namespace OrbitNavigator.Sync.Tests.Integration;

public sealed class OptionalSyncCoordinatorTests
{
    [Fact]
    public async Task PrivateBrowsingRejectsBeforeEveryDependency()
    {
        var rig = new TestRig();
        var privateBrowsing = rig.Browsing with
        {
            Privacy = rig.Browsing.Privacy with { Mode = BrowserProfileMode.Private },
        };

        var result = await rig.Coordinator.SynchronizeAsync(
            privateBrowsing,
            rig.OperationId,
            default);

        Assert.False(result.IsSuccess);
        Assert.Equal(ControllerErrorCode.PolicyDenied, result.Error?.Code);
        Assert.Equal(0, rig.TotalDependencyCalls);
    }

    [Fact]
    public async Task SignedOutReturnsUnavailableWithoutTransportOrLocalSyncCalls()
    {
        var rig = new TestRig();
        rig.SessionProvider.Error = ControllerError.Create(
            ControllerErrorCode.Unavailable,
            "sync.account.signed-out");

        var result = await rig.RunAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(ControllerErrorCode.Unavailable, result.Error?.Code);
        Assert.Equal("sync.account.signed-out", result.Error?.MessageKey);
        Assert.Equal(0, rig.Transport.PushCalls + rig.Transport.PullCalls);
        Assert.Equal(0, rig.Catalog.Calls);
        Assert.Equal(0, rig.Checkpoints.LoadCalls);
        var availability = SyncAvailabilityPolicy.Evaluate(hasAccountAuthorization: false);
        Assert.True(availability.LocalBrowsingAvailable);
        Assert.False(availability.SyncAvailable);
    }

    [Fact]
    public async Task PushProjectsAndEncryptsOnlyAllowlistedChanges()
    {
        var rig = new TestRig();
        var history = new SyncEntityId(Guid.NewGuid());
        var settings = new SyncEntityId(Guid.NewGuid());
        var tab = new SyncEntityId(Guid.NewGuid());
        rig.Catalog.Pages.Enqueue(new LocalSyncChangePage(
            [
                new LocalSyncChange(SyncRecordKind.Upsert, SyncDataCategory.History, history),
                new LocalSyncChange(SyncRecordKind.Upsert, SyncDataCategory.Settings, settings),
                new LocalSyncChange(SyncRecordKind.Tombstone, SyncDataCategory.OpenTabs, tab),
            ],
            new LocalChangeCursor("local-1"),
            false));

        var result = await rig.RunAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(
            [SyncDataCategory.History, SyncDataCategory.Settings],
            rig.Projector.Categories);
        var pushed = Assert.Single(rig.Transport.PushRequests);
        Assert.Equal(2, pushed.Envelopes.Count);
        Assert.Single(pushed.Tombstones);
        Assert.Empty(pushed.Purges);
        Assert.All(
            pushed.Envelopes.Select(value => value.Aad.Category)
                .Concat(pushed.Tombstones.Select(value => value.Aad.Category)),
            category => Assert.True(SyncAllowlist.IsAllowed(category)));
        Assert.Equal(1, rig.Checkpoints.CommitPushCalls);
        Assert.Equal("local-1", rig.Checkpoints.Current.PushedThrough?.Value);
    }

    [Fact]
    public async Task InvalidCategoryIsRejectedBeforeProjectionOrTransport()
    {
        var rig = new TestRig();
        rig.Catalog.Pages.Enqueue(new LocalSyncChangePage(
            [
                new LocalSyncChange(
                    SyncRecordKind.Upsert,
                    (SyncDataCategory)999,
                    new SyncEntityId(Guid.NewGuid())),
            ],
            new LocalChangeCursor("invalid"),
            false));

        var result = await rig.RunAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(ControllerErrorCode.IntegrityFailure, result.Error?.Code);
        Assert.Equal(0, rig.Projector.Calls);
        Assert.Equal(0, rig.Transport.PushCalls + rig.Transport.PullCalls);
    }

    [Fact]
    public async Task PushCheckpointAdvancesOnlyAfterExactAcceptedReceipt()
    {
        var rig = new TestRig();
        rig.Catalog.Pages.Enqueue(new LocalSyncChangePage(
            [
                new LocalSyncChange(
                    SyncRecordKind.Upsert,
                    SyncDataCategory.History,
                    new SyncEntityId(Guid.NewGuid())),
            ],
            new LocalChangeCursor("must-not-commit"),
            false));
        rig.Transport.ReturnInvalidPushReceipt = true;

        var result = await rig.RunAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(ControllerErrorCode.IntegrityFailure, result.Error?.Code);
        Assert.Equal(1, rig.Transport.PushCalls);
        Assert.Equal(0, rig.Checkpoints.CommitPushCalls);
        Assert.Null(rig.Checkpoints.Current.PushedThrough);
        Assert.Equal(0, rig.Transport.PullCalls);
    }

    [Fact]
    public async Task DecryptIntegrityFailureStopsApplyAndCheckpoint()
    {
        var rig = new TestRig();
        var aad = rig.RemoteAad(
            SyncRecordKind.Upsert,
            SyncDataCategory.History,
            new SyncEntityId(Guid.NewGuid()),
            generation: 4);
        rig.Transport.PullPages.Enqueue(rig.Page(
            "remote-1",
            envelopes: [EncryptedEnvelope(aad)]));
        rig.Codec.DecryptError = ControllerError.Create(
            ControllerErrorCode.IntegrityFailure,
            "sync.crypto.integrity-failure");

        var result = await rig.RunAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(ControllerErrorCode.IntegrityFailure, result.Error?.Code);
        Assert.Equal(1, rig.Codec.DecryptCalls);
        Assert.Equal(0, rig.Apply.Calls);
        Assert.Equal(0, rig.Checkpoints.CommitPullCalls);
    }

    [Fact]
    public async Task RemoteTombstoneIsAuthenticatedBeforeApply()
    {
        var rig = new TestRig();
        var aad = rig.RemoteAad(
            SyncRecordKind.Tombstone,
            SyncDataCategory.OpenTabs,
            new SyncEntityId(Guid.NewGuid()),
            generation: 4);
        rig.Transport.PullPages.Enqueue(rig.Page(
            "remote-tombstone",
            tombstones: [EncryptedTombstone(aad)]));

        var result = await rig.RunAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(1, rig.Codec.TombstoneAuthenticationCalls);
        Assert.Equal(1, rig.Apply.Calls);
        Assert.Single(rig.Apply.Pages.Single().Tombstones);
        Assert.True(
            rig.Events.IndexOf("codec.auth-tombstone") < rig.Events.IndexOf("apply.page"));
        Assert.True(
            rig.Events.IndexOf("apply.page") < rig.Events.IndexOf("checkpoint.pull"));
    }

    [Fact]
    public async Task ApplySeamSuppressesStaleUpsertWhenPageContainsNewerPurge()
    {
        var rig = new TestRig();
        var category = SyncDataCategory.History;
        var staleAad = rig.RemoteAad(
            SyncRecordKind.Upsert,
            category,
            new SyncEntityId(Guid.NewGuid()),
            generation: 2);
        var purgeAad = rig.RemoteAad(
            SyncRecordKind.Purge,
            category,
            new SyncEntityId(Guid.NewGuid()),
            generation: 4,
            operationId: new SyncOperationId(Guid.NewGuid()));
        rig.Transport.PullPages.Enqueue(rig.Page(
            "purge-fence",
            envelopes: [EncryptedEnvelope(staleAad)],
            purges: [EncryptedPurge(purgeAad)]));

        var result = await rig.RunAsync();

        Assert.True(result.IsSuccess);
        Assert.True(rig.Apply.StaleUpsertSuppressed);
        Assert.Equal(1, result.Value!.AppliedPurgeCount);
        Assert.Equal(1, result.Value.AppliedUpsertCount);
        var page = Assert.Single(rig.Apply.Pages);
        Assert.Equal(staleAad, Assert.Single(page.Upserts).Aad);
        Assert.Equal(purgeAad, Assert.Single(page.Purges).Aad);
    }

    [Fact]
    public async Task StalePullFenceStopsBeforeCryptographyApplyAndCheckpoint()
    {
        var rig = new TestRig();
        var staleFence = new ClientFence(
            rig.Session.DeviceId,
            rig.Session.Fence.ClientGeneration - 1,
            rig.Session.Fence.MinimumAcceptedGeneration - 1);
        rig.Transport.PullPages.Enqueue(new SyncPullPage(
            new SyncCursor("stale"),
            staleFence,
            [],
            [],
            [],
            false));

        var result = await rig.RunAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(ControllerErrorCode.StaleClient, result.Error?.Code);
        Assert.Equal(0, rig.Codec.TotalReceiveCalls);
        Assert.Equal(0, rig.Apply.Calls);
        Assert.Equal(0, rig.Checkpoints.CommitPullCalls);
    }

    [Fact]
    public async Task PullCursorIsCommittedOnlyAfterWholePageApplies()
    {
        var failing = new TestRig();
        failing.Transport.PullPages.Enqueue(failing.Page("apply-fails"));
        failing.Apply.Error = ControllerError.Create(
            ControllerErrorCode.Unavailable,
            "sync.test.apply-unavailable",
            isRetryable: true);

        var failed = await failing.RunAsync();

        Assert.False(failed.IsSuccess);
        Assert.Equal(1, failing.Apply.Calls);
        Assert.Equal(0, failing.Checkpoints.CommitPullCalls);
        Assert.Null(failing.Checkpoints.Current.PulledThrough);

        var successful = new TestRig();
        successful.Transport.PullPages.Enqueue(successful.Page("apply-succeeds"));
        var completed = await successful.RunAsync();

        Assert.True(completed.IsSuccess);
        Assert.Equal("apply-succeeds", successful.Checkpoints.Current.PulledThrough?.Value);
        Assert.True(
            successful.Events.IndexOf("apply.page") < successful.Events.IndexOf("checkpoint.pull"));
    }

    [Fact]
    public async Task ApplyIsIdempotentWhenCheckpointCommitFailsAndPageIsRetried()
    {
        var rig = new TestRig();
        var aad = rig.RemoteAad(
            SyncRecordKind.Upsert,
            SyncDataCategory.Settings,
            new SyncEntityId(Guid.NewGuid()),
            generation: 3);
        var page = rig.Page("retry-page", envelopes: [EncryptedEnvelope(aad)]);
        rig.Transport.PullPages.Enqueue(page);
        rig.Transport.PullPages.Enqueue(page);
        rig.Checkpoints.FailNextPullCommit = true;

        var first = await rig.RunAsync();
        var second = await rig.RunAsync();

        Assert.False(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(2, rig.Apply.Calls);
        Assert.Single(rig.Apply.UniqueAppliedEnvelopeIds);
        Assert.Equal("retry-page", rig.Checkpoints.Current.PulledThrough?.Value);
    }

    [Fact]
    public async Task PullLoopIsStrictlyBounded()
    {
        var rig = new TestRig();
        for (var index = 0; index < OptionalSyncCoordinator.MaximumPullPagesPerRun; index++)
        {
            rig.Transport.PullPages.Enqueue(rig.Page(
                $"page-{index}",
                hasMore: true));
        }

        var result = await rig.RunAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(ControllerErrorCode.Unavailable, result.Error?.Code);
        Assert.Equal(OptionalSyncCoordinator.MaximumPullPagesPerRun, rig.Transport.PullCalls);
        Assert.Equal(OptionalSyncCoordinator.MaximumPullPagesPerRun, rig.Apply.Calls);
    }

    [Fact]
    public async Task CancellationInsideNormalGateMakesZeroDependencyCalls()
    {
        var rig = new TestRig();
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        var result = await rig.Coordinator.SynchronizeAsync(
            rig.Browsing,
            rig.OperationId,
            source.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal(ControllerErrorCode.Cancelled, result.Error?.Code);
        Assert.Equal(0, rig.TotalDependencyCalls);
    }

    private static EncryptedSyncEnvelope EncryptedEnvelope(CanonicalSyncAad aad) =>
        new(aad, new byte[12], new byte[] { 1 }, new byte[16]);

    private static EncryptedSyncTombstone EncryptedTombstone(CanonicalSyncAad aad) =>
        new(aad, new byte[12], new byte[] { 1 }, new byte[16]);

    private static EncryptedPurgeCommand EncryptedPurge(CanonicalSyncAad aad) =>
        new(aad, new byte[12], new byte[] { 1 }, new byte[16]);

    private sealed class TestRig
    {
        public TestRig()
        {
            OperationId = new SyncOperationId(Guid.NewGuid());
            Browsing = new BrowsingContext(
                new PrivacyContext(
                    new ProfileId(Guid.NewGuid()),
                    new BrowserSessionId(Guid.NewGuid()),
                    BrowserProfileMode.Normal),
                new BrowserWindowId(Guid.NewGuid()),
                new BrowserTabId(Guid.NewGuid()),
                null);
            var device = new DeviceId(Guid.NewGuid());
            Session = new OptionalSyncSession(
                new OpaqueAuthHandle(Guid.NewGuid()),
                device,
                new SyncKeyMaterialHandle(Guid.NewGuid()),
                new SyncKeysetId(Guid.NewGuid()),
                2,
                new ClientFence(device, 5, 1));
            Scope = new SyncStateScope(
                Browsing.Privacy.ProfileId,
                device,
                Session.Fence.ClientGeneration);
            SessionProvider.Session = Session;
            Checkpoints.Current = new SyncCheckpoint(Scope, 0, null, null);
            Transport.Fence = Session.Fence;
            Apply.Events = Events;
            Codec.Events = Events;
            Checkpoints.Events = Events;
            Coordinator = new OptionalSyncCoordinator(
                SessionProvider,
                Catalog,
                Projector,
                Codec,
                Transport,
                Apply,
                Checkpoints,
                Sequences);
        }

        public SyncOperationId OperationId { get; }

        public BrowsingContext Browsing { get; }

        public OptionalSyncSession Session { get; }

        public SyncStateScope Scope { get; }

        public List<string> Events { get; } = [];

        public FakeSessionProvider SessionProvider { get; } = new();

        public FakeChangeCatalog Catalog { get; } = new();

        public FakeProjector Projector { get; } = new();

        public FakeCodec Codec { get; } = new();

        public FakeTransport Transport { get; } = new();

        public FakeApplyTarget Apply { get; } = new();

        public FakeCheckpointStore Checkpoints { get; } = new();

        public FakeSequenceAllocator Sequences { get; } = new();

        public OptionalSyncCoordinator Coordinator { get; }

        public int TotalDependencyCalls =>
            SessionProvider.Calls +
            Catalog.Calls +
            Projector.Calls +
            Codec.TotalCalls +
            Transport.PushCalls +
            Transport.PullCalls +
            Apply.Calls +
            Checkpoints.TotalCalls +
            Sequences.Calls;

        public ValueTask<ControllerResult<OptionalSyncRunReceipt>> RunAsync() =>
            Coordinator.SynchronizeAsync(Browsing, OperationId, default);

        public CanonicalSyncAad RemoteAad(
            SyncRecordKind kind,
            SyncDataCategory category,
            SyncEntityId entityId,
            long generation,
            SyncOperationId? operationId = null) =>
            new(
                SyncProtocol.CurrentProtocolVersion,
                SyncProtocol.CurrentSchemaVersion,
                Browsing.Privacy.ProfileId,
                new DeviceId(Guid.NewGuid()),
                Session.KeysetId,
                Session.KeyEpoch,
                kind,
                new SyncEnvelopeId(Guid.NewGuid()),
                category,
                entityId,
                operationId,
                generation,
                generation * 10);

        public SyncPullPage Page(
            string cursor,
            IReadOnlyList<EncryptedSyncEnvelope>? envelopes = null,
            IReadOnlyList<EncryptedSyncTombstone>? tombstones = null,
            IReadOnlyList<EncryptedPurgeCommand>? purges = null,
            bool hasMore = false) =>
            new(
                new SyncCursor(cursor),
                Session.Fence,
                envelopes ?? [],
                tombstones ?? [],
                purges ?? [],
                hasMore);
    }

    private sealed class FakeSessionProvider : IOptionalSyncSessionProvider
    {
        public int Calls { get; private set; }

        public OptionalSyncSession? Session { get; set; }

        public ControllerError? Error { get; set; }

        public ValueTask<ControllerResult<OptionalSyncSession>> GetAuthorizedSessionAsync(
            SyncOperationContext context,
            CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(Error is null
                ? ControllerResult<OptionalSyncSession>.Success(Session!)
                : ControllerResult<OptionalSyncSession>.Failure(Error));
        }
    }

    private sealed class FakeChangeCatalog : ILocalSyncChangeCatalog
    {
        public int Calls { get; private set; }

        public Queue<LocalSyncChangePage> Pages { get; } = new();

        public ValueTask<ControllerResult<LocalSyncChangePage>> ListPendingAsync(
            SyncOperationContext context,
            LocalChangeCursor? after,
            int maximumItems,
            CancellationToken cancellationToken)
        {
            Calls++;
            var page = Pages.Count > 0
                ? Pages.Dequeue()
                : new LocalSyncChangePage([], null, false);
            return ValueTask.FromResult(ControllerResult<LocalSyncChangePage>.Success(page));
        }
    }

    private sealed class FakeProjector : ISyncRecordProjector
    {
        public int Calls { get; private set; }

        public List<SyncDataCategory> Categories { get; } = [];

        public ValueTask<ControllerResult<SyncRecordPayload>> ProjectAsync(
            SyncOperationContext context,
            SyncDataCategory category,
            SyncEntityId entityId,
            CancellationToken cancellationToken)
        {
            Calls++;
            Categories.Add(category);
            return ValueTask.FromResult(ControllerResult<SyncRecordPayload>.Success(
                Payload(category, entityId)));
        }
    }

    private sealed class FakeCodec : ISyncEnvelopeCodec
    {
        public int EncryptCalls { get; private set; }

        public int EncryptTombstoneCalls { get; private set; }

        public int DecryptCalls { get; private set; }

        public int TombstoneAuthenticationCalls { get; private set; }

        public int PurgeAuthenticationCalls { get; private set; }

        public int TotalCalls =>
            EncryptCalls +
            EncryptTombstoneCalls +
            DecryptCalls +
            TombstoneAuthenticationCalls +
            PurgeAuthenticationCalls;

        public int TotalReceiveCalls =>
            DecryptCalls + TombstoneAuthenticationCalls + PurgeAuthenticationCalls;

        public ControllerError? DecryptError { get; set; }

        public List<string> Events { get; set; } = [];

        public ValueTask<ControllerResult<EncryptedSyncEnvelope>> EncryptAsync(
            SyncOperationContext context,
            SyncKeyMaterialHandle keyMaterial,
            CanonicalSyncAad aad,
            SyncRecordPayload record,
            CancellationToken cancellationToken)
        {
            EncryptCalls++;
            return ValueTask.FromResult(ControllerResult<EncryptedSyncEnvelope>.Success(
                EncryptedEnvelope(aad)));
        }

        public ValueTask<ControllerResult<SyncRecordPayload>> DecryptAsync(
            SyncOperationContext context,
            SyncKeyMaterialHandle keyMaterial,
            EncryptedSyncEnvelope envelope,
            CancellationToken cancellationToken)
        {
            DecryptCalls++;
            return ValueTask.FromResult(DecryptError is null
                ? ControllerResult<SyncRecordPayload>.Success(
                    Payload(envelope.Aad.Category, envelope.Aad.EntityId))
                : ControllerResult<SyncRecordPayload>.Failure(DecryptError));
        }

        public ValueTask<ControllerResult<EncryptedSyncTombstone>> EncryptTombstoneAsync(
            SyncOperationContext context,
            SyncKeyMaterialHandle keyMaterial,
            CanonicalSyncAad aad,
            CancellationToken cancellationToken)
        {
            EncryptTombstoneCalls++;
            return ValueTask.FromResult(ControllerResult<EncryptedSyncTombstone>.Success(
                EncryptedTombstone(aad)));
        }

        public ValueTask<ControllerResult<AuthenticatedSyncTombstoneReceipt>> DecryptAndValidateTombstoneAsync(
            SyncOperationContext context,
            SyncKeyMaterialHandle keyMaterial,
            EncryptedSyncTombstone tombstone,
            CancellationToken cancellationToken)
        {
            TombstoneAuthenticationCalls++;
            Events.Add("codec.auth-tombstone");
            var aad = tombstone.Aad;
            return ValueTask.FromResult(
                ControllerResult<AuthenticatedSyncTombstoneReceipt>.Success(
                    new AuthenticatedSyncTombstoneReceipt(
                        aad.ProfileId,
                        aad.DeviceId,
                        aad.KeysetId,
                        aad.KeyEpoch,
                        aad.EnvelopeId,
                        aad.Category,
                        aad.EntityId,
                        aad.ClientGeneration,
                        aad.ClientSequence)));
        }

        public ValueTask<ControllerResult<EncryptedPurgeCommand>> EncryptPurgeAsync(
            SyncOperationContext context,
            SyncKeyMaterialHandle keyMaterial,
            CanonicalSyncAad aad,
            DecryptedPurgeMarker marker,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ControllerResult<EncryptedPurgeCommand>.Failure(NotSupported()));

        public ValueTask<ControllerResult<DecryptedPurgeMarker>> DecryptAndValidatePurgeAsync(
            SyncOperationContext context,
            SyncKeyMaterialHandle keyMaterial,
            EncryptedPurgeCommand command,
            CancellationToken cancellationToken)
        {
            PurgeAuthenticationCalls++;
            var aad = command.Aad;
            return ValueTask.FromResult(ControllerResult<DecryptedPurgeMarker>.Success(
                new DecryptedPurgeMarker(
                    aad.OperationId!.Value,
                    aad.ProfileId,
                    aad.Category,
                    aad.ClientGeneration,
                    aad.KeysetId)));
        }

        private static ControllerError NotSupported() =>
            ControllerError.Create(
                ControllerErrorCode.NotSupported,
                "sync.test.not-supported");
    }

    private sealed class FakeTransport : ISyncTransport
    {
        public int PushCalls { get; private set; }

        public int PullCalls { get; private set; }

        public ClientFence? Fence { get; set; }

        public List<SyncPushRequest> PushRequests { get; } = [];

        public List<SyncPullRequest> PullRequests { get; } = [];

        public Queue<SyncPullPage> PullPages { get; } = new();

        public bool ReturnInvalidPushReceipt { get; set; }

        public ValueTask<ControllerResult<SyncPushReceipt>> PushAsync(
            SyncOperationContext context,
            OpaqueAuthHandle authorization,
            SyncPushRequest request,
            CancellationToken cancellationToken)
        {
            PushCalls++;
            PushRequests.Add(request);
            return ValueTask.FromResult(ControllerResult<SyncPushReceipt>.Success(
                new SyncPushReceipt(
                    new SyncCursor($"push-{PushCalls}"),
                    request.Fence,
                    ReturnInvalidPushReceipt
                        ? request.Envelopes.Count + 1
                        : request.Envelopes.Count,
                    request.Tombstones.Count,
                    request.Purges.Count)));
        }

        public ValueTask<ControllerResult<SyncPullPage>> PullAsync(
            SyncOperationContext context,
            OpaqueAuthHandle authorization,
            SyncPullRequest request,
            CancellationToken cancellationToken)
        {
            PullCalls++;
            PullRequests.Add(request);
            var page = PullPages.Count > 0
                ? PullPages.Dequeue()
                : new SyncPullPage(
                    new SyncCursor($"empty-{PullCalls}"),
                    Fence!,
                    [],
                    [],
                    [],
                    false);
            return ValueTask.FromResult(ControllerResult<SyncPullPage>.Success(page));
        }
    }

    private sealed class FakeApplyTarget : IAuthenticatedSyncApplyTarget
    {
        private readonly Dictionary<SyncDataCategory, long> _purgeFences = [];

        public int Calls { get; private set; }

        public ControllerError? Error { get; set; }

        public bool StaleUpsertSuppressed { get; private set; }

        public List<AuthenticatedSyncPage> Pages { get; } = [];

        public HashSet<SyncEnvelopeId> UniqueAppliedEnvelopeIds { get; } = [];

        public List<string> Events { get; set; } = [];

        public ValueTask<ControllerResult<AuthenticatedPageApplyReceipt>> ApplyPageAsync(
            SyncOperationContext context,
            AuthenticatedSyncPage page,
            CancellationToken cancellationToken)
        {
            Calls++;
            Events.Add("apply.page");
            Pages.Add(page);
            if (Error is not null)
            {
                return ValueTask.FromResult(
                    ControllerResult<AuthenticatedPageApplyReceipt>.Failure(Error));
            }

            foreach (var purge in page.Purges)
            {
                _purgeFences[purge.Aad.Category] = Math.Max(
                    _purgeFences.GetValueOrDefault(purge.Aad.Category),
                    purge.Marker.ClientGeneration);
                UniqueAppliedEnvelopeIds.Add(purge.Aad.EnvelopeId);
            }

            foreach (var upsert in page.Upserts)
            {
                if (_purgeFences.TryGetValue(upsert.Aad.Category, out var purgeGeneration) &&
                    upsert.Aad.ClientGeneration <= purgeGeneration)
                {
                    StaleUpsertSuppressed = true;
                }

                UniqueAppliedEnvelopeIds.Add(upsert.Aad.EnvelopeId);
            }

            foreach (var tombstone in page.Tombstones)
                UniqueAppliedEnvelopeIds.Add(tombstone.Aad.EnvelopeId);

            return ValueTask.FromResult(
                ControllerResult<AuthenticatedPageApplyReceipt>.Success(
                    new AuthenticatedPageApplyReceipt(
                        page.Cursor,
                        page.Upserts.Count,
                        page.Tombstones.Count,
                        page.Purges.Count)));
        }
    }

    private sealed class FakeCheckpointStore : IDurableSyncCheckpointStore
    {
        public int LoadCalls { get; private set; }

        public int CommitPushCalls { get; private set; }

        public int CommitPullCalls { get; private set; }

        public int TotalCalls => LoadCalls + CommitPushCalls + CommitPullCalls;

        public bool FailNextPullCommit { get; set; }

        public SyncCheckpoint Current { get; set; } = null!;

        public List<string> Events { get; set; } = [];

        public ValueTask<ControllerResult<SyncCheckpoint>> LoadAsync(
            SyncOperationContext context,
            SyncStateScope scope,
            CancellationToken cancellationToken)
        {
            LoadCalls++;
            return ValueTask.FromResult(ControllerResult<SyncCheckpoint>.Success(Current));
        }

        public ValueTask<ControllerResult<SyncCheckpoint>> CommitPushAsync(
            SyncOperationContext context,
            SyncCheckpoint expected,
            LocalChangeCursor pushedThrough,
            CancellationToken cancellationToken)
        {
            CommitPushCalls++;
            if (expected != Current)
                return ValueTask.FromResult(Conflict());
            Current = Current with
            {
                Version = Current.Version + 1,
                PushedThrough = pushedThrough,
            };
            return ValueTask.FromResult(ControllerResult<SyncCheckpoint>.Success(Current));
        }

        public ValueTask<ControllerResult<SyncCheckpoint>> CommitPullAsync(
            SyncOperationContext context,
            SyncCheckpoint expected,
            SyncCursor pulledThrough,
            CancellationToken cancellationToken)
        {
            CommitPullCalls++;
            Events.Add("checkpoint.pull");
            if (FailNextPullCommit)
            {
                FailNextPullCommit = false;
                return ValueTask.FromResult(
                    ControllerResult<SyncCheckpoint>.Failure(
                        ControllerError.Create(
                            ControllerErrorCode.Unavailable,
                            "sync.test.checkpoint-unavailable",
                            isRetryable: true)));
            }

            if (expected != Current)
                return ValueTask.FromResult(Conflict());
            Current = Current with
            {
                Version = Current.Version + 1,
                PulledThrough = pulledThrough,
            };
            return ValueTask.FromResult(ControllerResult<SyncCheckpoint>.Success(Current));
        }

        private static ControllerResult<SyncCheckpoint> Conflict() =>
            ControllerResult<SyncCheckpoint>.Failure(
                ControllerError.Create(
                    ControllerErrorCode.Conflict,
                    "sync.test.checkpoint-conflict"));
    }

    private sealed class FakeSequenceAllocator : IDurableSyncSequenceAllocator
    {
        private long _next;

        public int Calls { get; private set; }

        public ValueTask<ControllerResult<SyncSequenceReservation>> ReserveNextAsync(
            SyncOperationContext context,
            SyncStateScope scope,
            CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(
                ControllerResult<SyncSequenceReservation>.Success(
                    new SyncSequenceReservation(scope, _next++)));
        }
    }

    private static SyncRecordPayload Payload(
        SyncDataCategory category,
        SyncEntityId entityId) => category switch
    {
        SyncDataCategory.History => new HistorySyncRecord(
            entityId,
            1,
            DateTimeOffset.UnixEpoch,
            "https://example.test/",
            "Example",
            DateTimeOffset.UnixEpoch,
            1),
        SyncDataCategory.Settings => new SettingsSyncRecord(
            entityId,
            1,
            DateTimeOffset.UnixEpoch,
            SyncSettingPersistenceScope.GlobalPersistent,
            []),
        SyncDataCategory.OpenTabs => new OpenTabSyncRecord(
            entityId,
            1,
            DateTimeOffset.UnixEpoch,
            "https://example.test/tab",
            "Tab",
            0,
            null),
        _ => throw new ArgumentOutOfRangeException(nameof(category)),
    };
}
