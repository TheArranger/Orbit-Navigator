using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text.Json;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Sync;
using OrbitNavigator.Sync.Integration;
using OrbitNavigator.Sync.State;

namespace OrbitNavigator.App.Sync;

/// <summary>
/// Durable, atomic received-record projection. It does not replace the local
/// browser session or enable sync. Call only after the codec has authenticated
/// the page; the account identity is independently loaded from the local binding.
/// Remote tabs remain a separate inventory for an explicit restore action.
/// </summary>
public sealed class RemoteBrowserSyncInbox : IAuthenticatedSyncApplyTarget
{
    private const int MaximumMutations = 20_000;
    private const int MaximumPages = 20_000;
    private const int MaximumStorageBytes = 16 * 1024 * 1024;
    private static readonly ProfileStorageNamespace StorageNamespace =
        ProfileStorageNamespace.Create("sync.remote-browser").Value!;
    private static readonly ProfileStorageKey StorageKey = ProfileStorageKey.Create("inbox-v1").Value!;
    private readonly IProfileStorage _storage;
    private readonly LocalSyncProfileBindingStore _bindings;
    private readonly ProfileId _localProfile;
    private readonly SyncKeysetId _keyset;
    private readonly long _keyEpoch;
    private readonly ClientFence _fence;
    // App composes one profile storage adapter; sharing this lock also prevents
    // competing first commits from separate inbox instances using that adapter.
    private static readonly ConditionalWeakTable<IProfileStorage, SemaphoreSlim> StorageGates = new();
    private readonly SemaphoreSlim _gate;

    public RemoteBrowserSyncInbox(IProfileStorage storage, ProfileId localProfile,
        SyncKeysetId keyset, long keyEpoch, ClientFence fence)
    {
        ArgumentNullException.ThrowIfNull(storage);
        if (localProfile.IsEmpty || !keyset.IsDefined || keyEpoch < 0 || fence is not { IsDefined: true })
            throw new ArgumentException("A fully authorized local sync identity is required.");
        _storage = storage;
        _gate = StorageGates.GetValue(storage, _ => new(1, 1));
        _bindings = new(storage);
        _localProfile = localProfile;
        _keyset = keyset;
        _keyEpoch = keyEpoch;
        _fence = fence;
    }

    public ValueTask<ControllerResult<AuthenticatedPageApplyReceipt>> ApplyPageAsync(
        SyncOperationContext context, AuthenticatedSyncPage page, CancellationToken cancellationToken) =>
        ApplyAsync(context.Browsing, context.OperationId, page, cancellationToken);

    public ValueTask<ControllerResult<AuthenticatedPageApplyReceipt>> ApplyAsync(
        BrowsingContext browsing, SyncOperationId operationId, AuthenticatedSyncPage page,
        CancellationToken cancellationToken = default) =>
        SyncOperationGate.ExecuteAsync(browsing, operationId,
            context => ApplyAuthorizedAsync(context, page, cancellationToken));

    public ValueTask<ControllerResult<RemoteBrowserSyncSnapshot>> ReadAsync(
        BrowsingContext browsing, SyncOperationId operationId, CancellationToken cancellationToken = default) =>
        SyncOperationGate.ExecuteAsync(browsing, operationId,
            context => ReadAuthorizedAsync(context, cancellationToken));

    private async ValueTask<ControllerResult<AuthenticatedPageApplyReceipt>> ApplyAuthorizedAsync(
        SyncOperationContext context, AuthenticatedSyncPage page, CancellationToken cancellationToken)
    {
        if (context.Browsing.Privacy.ProfileId != _localProfile || page is null ||
            page.Fence != _fence || page.Cursor is null || !ValidCursor(page.Cursor.Value) ||
            page.Upserts is null || page.Tombstones is null || page.Purges is null ||
            page.Purges.Count != 0 || page.Upserts.Count + page.Tombstones.Count > 250)
            return Failure<AuthenticatedPageApplyReceipt>();

        var binding = await _bindings.LoadAsync(context.Browsing, context.OperationId, cancellationToken).ConfigureAwait(false);
        if (!binding.IsSuccess) return ControllerResult<AuthenticatedPageApplyReceipt>.Failure(binding.Error!);
        var account = binding.Value!.SyncProfileId;
        var incoming = new List<StoredMutation>();
        foreach (var upsert in page.Upserts)
        {
            if (upsert is null || !ValidAad(upsert.Aad, account, SyncRecordKind.Upsert) ||
                !ValidPayload(upsert.Payload) || upsert.Aad.Category != upsert.Payload.Category ||
                upsert.Aad.EntityId != upsert.Payload.EntityId)
                return Failure<AuthenticatedPageApplyReceipt>();
            incoming.Add(new(upsert.Aad, upsert.Payload as HistorySyncRecord, upsert.Payload as OpenTabSyncRecord));
        }
        foreach (var tombstone in page.Tombstones)
        {
            if (tombstone is null || !ValidAad(tombstone.Aad, account, SyncRecordKind.Tombstone) ||
                tombstone.Receipt != Receipt(tombstone.Aad))
                return Failure<AuthenticatedPageApplyReceipt>();
            incoming.Add(new(tombstone.Aad, null, null));
        }
        if (incoming.Select(m => m.Aad.EnvelopeId).Distinct().Count() != incoming.Count ||
            incoming.Select(m => SequenceKey(m.Aad)).Distinct().Count() != incoming.Count)
            return Failure<AuthenticatedPageApplyReceipt>();

        // The digest binds the complete authenticated payload and routing, not
        // merely the relay cursor. A changed replay must not advance a checkpoint.
        var digest = Digest(page.Cursor.Value, incoming);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await LoadAsync(context, account, cancellationToken).ConfigureAwait(false);
            if (!loaded.IsSuccess) return ControllerResult<AuthenticatedPageApplyReceipt>.Failure(loaded.Error!);
            var state = loaded.Value!.State;
            var previousPage = state.Pages.SingleOrDefault(p => p.Cursor == page.Cursor.Value);
            if (previousPage is not null)
                return previousPage.Digest == digest
                    ? Accepted(page)
                    : Failure<AuthenticatedPageApplyReceipt>();

            var envelopes = state.Mutations.ToDictionary(m => m.Aad.EnvelopeId);
            var sequences = state.Mutations.ToDictionary(m => SequenceKey(m.Aad));
            var latest = state.Mutations.GroupBy(m => m.Aad.DeviceId)
                .ToDictionary(g => g.Key, g => g.Max(m => (m.Aad.ClientGeneration, m.Aad.ClientSequence)));
            var additions = new List<StoredMutation>();
            foreach (var mutation in incoming.OrderBy(m => m.Aad.DeviceId.Value)
                .ThenBy(m => m.Aad.ClientGeneration).ThenBy(m => m.Aad.ClientSequence))
            {
                var aad = mutation.Aad;
                if (envelopes.TryGetValue(aad.EnvelopeId, out var replay))
                {
                    if (replay != mutation) return Failure<AuthenticatedPageApplyReceipt>();
                    continue;
                }
                var order = (aad.ClientGeneration, aad.ClientSequence);
                if (sequences.ContainsKey(SequenceKey(aad)) ||
                    latest.TryGetValue(aad.DeviceId, out var high) && order.CompareTo(high) <= 0)
                    return Failure<AuthenticatedPageApplyReceipt>();
                latest[aad.DeviceId] = order;
                envelopes.Add(aad.EnvelopeId, mutation);
                sequences.Add(SequenceKey(aad), mutation);
                additions.Add(mutation);
            }
            if (state.Mutations.Length + additions.Count > MaximumMutations || state.Pages.Length >= MaximumPages)
                return Failure<AuthenticatedPageApplyReceipt>(ControllerErrorCode.Unavailable);
            var updated = state with
            {
                Mutations = [.. state.Mutations, .. additions],
                Pages = [.. state.Pages, new(page.Cursor.Value, digest, incoming.Select(m => m.Aad.EnvelopeId).ToArray())],
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(updated);
            if (bytes.Length > MaximumStorageBytes) return Failure<AuthenticatedPageApplyReceipt>(ControllerErrorCode.Unavailable);
            var request = ProfileStorageWriteRequest.Create(Address(context), bytes, loaded.Value.Revision);
            if (!request.IsSuccess) return ControllerResult<AuthenticatedPageApplyReceipt>.Failure(request.Error!);
            var written = await _storage.WriteAsync(request.Value!, cancellationToken).ConfigureAwait(false);
            return written.IsSuccess
                ? Accepted(page)
                : ControllerResult<AuthenticatedPageApplyReceipt>.Failure(written.Error!);
        }
        finally { _gate.Release(); }
    }

    private async ValueTask<ControllerResult<RemoteBrowserSyncSnapshot>> ReadAuthorizedAsync(
        SyncOperationContext context, CancellationToken cancellationToken)
    {
        if (context.Browsing.Privacy.ProfileId != _localProfile) return Failure<RemoteBrowserSyncSnapshot>();
        var binding = await _bindings.LoadAsync(context.Browsing, context.OperationId, cancellationToken).ConfigureAwait(false);
        if (!binding.IsSuccess) return ControllerResult<RemoteBrowserSyncSnapshot>.Failure(binding.Error!);
        var loaded = await LoadAsync(context, binding.Value!.SyncProfileId, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess) return ControllerResult<RemoteBrowserSyncSnapshot>.Failure(loaded.Error!);
        // Device-scoped identity avoids another device's record closing or
        // rewriting this device's tabs. Tombstones remove only that origin's item.
        var current = loaded.Value!.State.Mutations
            .GroupBy(m => (m.Aad.DeviceId, m.Aad.Category, m.Aad.EntityId))
            .Select(g => g.MaxBy(m => (m.Aad.ClientGeneration, m.Aad.ClientSequence))!)
            .Where(m => m.Aad.RecordKind == SyncRecordKind.Upsert).ToArray();
        return ControllerResult<RemoteBrowserSyncSnapshot>.Success(new(_localProfile,
            binding.Value.SyncProfileId,
            current.Where(m => m.History is not null).Select(m => new RemoteHistoryItem(m.Aad.DeviceId, m.History!)).ToArray(),
            current.Where(m => m.Tab is not null).OrderBy(m => m.Aad.DeviceId.Value).ThenBy(m => m.Tab!.Position)
                .Select(m => new RemoteOpenTabItem(m.Aad.DeviceId, m.Tab!)).ToArray()));
    }

    private async ValueTask<ControllerResult<Loaded>> LoadAsync(SyncOperationContext context,
        ProfileId account, CancellationToken cancellationToken)
    {
        var read = await _storage.ReadAsync(Address(context), cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess) return read.Error!.Code == ControllerErrorCode.NotFound
            ? ControllerResult<Loaded>.Success(new(null, new(1, _localProfile, account, _keyset, _keyEpoch, _fence, [], [])))
            : ControllerResult<Loaded>.Failure(read.Error);
        if (read.Value!.Payload.Length > MaximumStorageBytes) return Failure<Loaded>();
        try
        {
            var state = JsonSerializer.Deserialize<StoredState>(read.Value.Payload.Span);
            if (state is null || state.Version != 1 || state.LocalProfileId != _localProfile || state.AccountProfileId != account ||
                state.Keyset != _keyset || state.KeyEpoch != _keyEpoch || state.Fence != _fence ||
                state.Mutations is null || state.Pages is null || state.Mutations.Length > MaximumMutations || state.Pages.Length > MaximumPages ||
                state.Mutations.Any(m => m is null || m.Aad is null || !ValidAad(m.Aad, account, m.Aad.RecordKind) ||
                    m.Aad.RecordKind == SyncRecordKind.Upsert && (!ValidPayload(m.Payload) || m.Payload!.EntityId != m.Aad.EntityId || m.Payload.Category != m.Aad.Category) ||
                    m.Aad.RecordKind == SyncRecordKind.Tombstone && (m.History is not null || m.Tab is not null) ||
                    m.History is not null && m.Tab is not null) ||
                state.Mutations.Select(m => m.Aad.EnvelopeId).Distinct().Count() != state.Mutations.Length ||
                state.Mutations.Select(m => SequenceKey(m.Aad)).Distinct().Count() != state.Mutations.Length ||
                state.Pages.Any(p => p is null || !ValidCursor(p.Cursor) || p.Digest is null || p.Digest.Length != 64 || p.Digest.Any(c => !Uri.IsHexDigit(c))) ||
                state.Pages.Select(p => p.Cursor).Distinct().Count() != state.Pages.Length ||
                !ValidPageProvenance(state))
                return Failure<Loaded>();
            return ControllerResult<Loaded>.Success(new(read.Value.Revision, state));
        }
        catch (JsonException) { return Failure<Loaded>(); }
    }

    private bool ValidAad(CanonicalSyncAad? aad, ProfileId account, SyncRecordKind kind) =>
        SyncContractRules.ValidateAad(aad).IsValid && aad!.RecordKind == kind &&
        kind is SyncRecordKind.Upsert or SyncRecordKind.Tombstone &&
        aad.Category is SyncDataCategory.History or SyncDataCategory.OpenTabs &&
        aad.ProfileId == account && aad.KeysetId == _keyset && aad.KeyEpoch == _keyEpoch &&
        aad.ClientGeneration >= _fence.MinimumAcceptedGeneration;

    private static bool ValidPayload(SyncRecordPayload? payload)
    {
        var values = payload switch
        {
            HistorySyncRecord h => (h.AbsoluteUrl, h.Title),
            OpenTabSyncRecord t => (t.AbsoluteUrl, t.Title),
            _ => (null, null),
        };
        return values.Item1 is not null && values.Item2 is not null &&
            payload!.ModifiedAtUtc != default && SyncContractRules.ValidateRecord(payload).IsValid &&
            !values.Item2.Any(char.IsControl) &&
            (payload is not HistorySyncRecord history || history.LastVisitedAtUtc != default && history.VisitCount > 0) &&
            (payload is not OpenTabSyncRecord tab || tab.GroupLabel is null || !tab.GroupLabel.Any(char.IsControl)) &&
            Uri.TryCreate(values.Item1, UriKind.Absolute, out var uri) && string.IsNullOrEmpty(uri.UserInfo) &&
            !string.IsNullOrWhiteSpace(uri.IdnHost);
    }

    private static bool ValidCursor(string? value) => value is { Length: > 0 and <= 4096 } && !value.Any(char.IsControl);
    // Coordinator receipts account for every authenticated input, including an
    // idempotently applied replay. They are not a count of new disk mutations.
    private static ControllerResult<AuthenticatedPageApplyReceipt> Accepted(AuthenticatedSyncPage page) =>
        ControllerResult<AuthenticatedPageApplyReceipt>.Success(new(page.Cursor, page.Upserts.Count, page.Tombstones.Count, 0));
    private static string Digest(string cursor, IReadOnlyList<StoredMutation> mutations) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Cursor = cursor, Mutations = mutations })));
    private static bool ValidPageProvenance(StoredState state)
    {
        var mutations = state.Mutations.ToDictionary(m => m.Aad.EnvelopeId);
        var referenced = new HashSet<SyncEnvelopeId>();
        foreach (var page in state.Pages)
        {
            if (page.EnvelopeIds is null || page.EnvelopeIds.Length > 250 ||
                page.EnvelopeIds.Distinct().Count() != page.EnvelopeIds.Length ||
                page.EnvelopeIds.Any(id => !mutations.ContainsKey(id))) return false;
            var ordered = page.EnvelopeIds.Select(id => mutations[id]).ToArray();
            if (Digest(page.Cursor, ordered) != page.Digest) return false;
            referenced.UnionWith(page.EnvelopeIds);
        }
        return referenced.Count == mutations.Count;
    }
    private static (DeviceId, long, long) SequenceKey(CanonicalSyncAad a) => (a.DeviceId, a.ClientGeneration, a.ClientSequence);
    private static AuthenticatedSyncTombstoneReceipt Receipt(CanonicalSyncAad a) => new(a.ProfileId, a.DeviceId,
        a.KeysetId, a.KeyEpoch, a.EnvelopeId, a.Category, a.EntityId, a.ClientGeneration, a.ClientSequence);
    private static ProfileStorageAddress Address(SyncOperationContext c) => ProfileStorageAddress.Create(
        c.Browsing.Privacy, StorageNamespace, StorageKey, ProfileStorageDurability.Persistent).Value!;
    private static ControllerResult<T> Failure<T>(ControllerErrorCode code = ControllerErrorCode.IntegrityFailure) where T : class =>
        ControllerResult<T>.Failure(ControllerError.Create(code, "sync.remote-browser.unavailable"));
    private sealed record Loaded(ProfileStorageRevision? Revision, StoredState State);
    private sealed record StoredState(int Version, ProfileId LocalProfileId, ProfileId AccountProfileId,
        SyncKeysetId Keyset, long KeyEpoch, ClientFence Fence, StoredMutation[] Mutations, StoredPage[] Pages);
    private sealed record StoredPage(string Cursor, string Digest, SyncEnvelopeId[] EnvelopeIds);
    private sealed record StoredMutation(CanonicalSyncAad Aad, HistorySyncRecord? History, OpenTabSyncRecord? Tab)
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public SyncRecordPayload? Payload => (SyncRecordPayload?)History ?? Tab;
    }
}

public sealed record RemoteHistoryItem(DeviceId DeviceId, HistorySyncRecord Record);
public sealed record RemoteOpenTabItem(DeviceId DeviceId, OpenTabSyncRecord Record);
public sealed record RemoteBrowserSyncSnapshot(ProfileId LocalProfileId, ProfileId AccountProfileId,
    IReadOnlyList<RemoteHistoryItem> History, IReadOnlyList<RemoteOpenTabItem> OpenTabs);
