using System.Net;
using System.Text;
using System.Text.Json;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Sync;
using OrbitNavigator.Sync.Accounts;
using OrbitNavigator.Sync.Transport;
using Xunit;

namespace OrbitNavigator.Sync.Tests.Transport;

public sealed class MyOrbitOpaqueSyncTransportTests
{
    [Fact]
    public async Task PushSerializesOnlyCanonicalAadAndEncryptedBytes()
    {
        var handler = new RecordingHandler(request => Json(new
        {
            cursor = "7",
            fence = FenceJson(),
            accepted_envelope_count = 1,
            accepted_tombstone_count = 0,
            accepted_purge_count = 0,
            new_record_count = 1,
        }));
        using var transport = Transport(handler, out var resolver);
        var envelope = Envelope();

        var result = await transport.PushAsync(
            Context(),
            resolver.Handle,
            new SyncPushRequest(Device, Fence, [envelope], [], []),
            default);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value!.AcceptedEnvelopeCount);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/navigator-sync/v1/records/push", request.Uri.AbsolutePath);
        Assert.Equal("Bearer", request.AuthorizationScheme);
        Assert.DoesNotContain("Cookie", request.HeaderNames);
        Assert.DoesNotContain("Origin", request.HeaderNames);
        Assert.DoesNotContain("Referer", request.HeaderNames);
        Assert.DoesNotContain("https://private.example", request.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Private title", request.Body, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(request.Body);
        var record = json.RootElement.GetProperty("records")[0];
        Assert.Equal("history", record.GetProperty("aad").GetProperty("category").GetString());
        Assert.Equal("upsert", record.GetProperty("aad").GetProperty("record_kind").GetString());
        Assert.Equal(Base64Url(envelope.Ciphertext.Span), record.GetProperty("ciphertext").GetString());
    }

    [Fact]
    public async Task PullAcceptsOnlyStrictTabsHistoryEncryptedRecords()
    {
        var history = Envelope();
        var tombstone = Tombstone();
        var handler = new RecordingHandler(_ => Json(new
        {
            cursor = "9",
            fence = FenceJson(),
            records = new[] { Wire(history), Wire(tombstone) },
            has_more = false,
        }));
        using var transport = Transport(handler, out var resolver);

        var result = await transport.PullAsync(
            Context(),
            resolver.Handle,
            new SyncPullRequest(Device, Fence, null, 25),
            default);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value!.Envelopes);
        Assert.Single(result.Value.Tombstones);
        Assert.Empty(result.Value.Purges);
        Assert.Equal("9", result.Value.Cursor.Value);
        Assert.Contains("after=0", Assert.Single(handler.Requests).Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SettingsAndPurgeNeverReachCredentialOrNetwork()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("network must not run"));
        using var transport = Transport(handler, out var resolver);
        var settings = Envelope() with
        {
            Aad = Envelope().Aad with { Category = SyncDataCategory.Settings },
        };

        var result = await transport.PushAsync(
            Context(),
            resolver.Handle,
            new SyncPushRequest(Device, Fence, [settings], [], []),
            default);

        Assert.Equal(ControllerErrorCode.InvalidRequest, result.Error?.Code);
        Assert.Equal(0, resolver.ResolveCalls);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task WrongFenceOrMalformedCryptoShapeFailsClosed()
    {
        var handler = new RecordingHandler(_ => Json(new
        {
            cursor = "9",
            fence = new
            {
                device_id = Guid.NewGuid().ToString("N"),
                client_generation = 0,
                minimum_accepted_generation = 0,
            },
            records = new[] { Wire(Envelope()) },
            has_more = false,
        }));
        using var transport = Transport(handler, out var resolver);

        var result = await transport.PullAsync(
            Context(), resolver.Handle, new SyncPullRequest(Device, Fence, null, 25), default);

        Assert.Equal(ControllerErrorCode.IntegrityFailure, result.Error?.Code);
    }

    [Fact]
    public async Task RemoteRecordForAnotherProfileFailsClosed()
    {
        var foreign = Envelope() with
        {
            Aad = Envelope().Aad with { ProfileId = new ProfileId(Guid.NewGuid()) },
        };
        var handler = new RecordingHandler(_ => Json(new
        {
            cursor = "9",
            fence = FenceJson(),
            records = new[] { Wire(foreign) },
            has_more = false,
        }));
        using var transport = Transport(handler, out var resolver);

        var result = await transport.PullAsync(
            Context(), resolver.Handle, new SyncPullRequest(Device, Fence, null, 25), default);

        Assert.Equal(ControllerErrorCode.IntegrityFailure, result.Error?.Code);
    }

    [Fact]
    public async Task RedirectAndSetCookieResponsesFailClosed()
    {
        foreach (var response in new[]
        {
            new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://other.example/") },
            },
            CookieResponse(),
        })
        {
            var handler = new RecordingHandler(_ => response);
            using var transport = Transport(handler, out var resolver);
            var result = await transport.PullAsync(
                Context(), resolver.Handle, new SyncPullRequest(Device, Fence, null, 25), default);
            Assert.Equal(ControllerErrorCode.IntegrityFailure, result.Error?.Code);
        }
    }

    private static MyOrbitOpaqueSyncTransport Transport(
        HttpMessageHandler handler,
        out CredentialResolver resolver)
    {
        resolver = new CredentialResolver();
        return new MyOrbitOpaqueSyncTransport(
            MyOrbitAccountProviderOptions.Create(new Uri("https://my-orbit.example/")).Value!,
            resolver,
            handler);
    }

    private static SyncOperationContext Context()
    {
        var browsing = new BrowsingContext(
            new PrivacyContext(Profile, new BrowserSessionId(Guid.NewGuid()), BrowserProfileMode.Normal),
            new BrowserWindowId(Guid.NewGuid()),
            new BrowserTabId(Guid.NewGuid()),
            null);
        return SyncOperationContext.Authorize(browsing, new SyncOperationId(Guid.NewGuid())).Value!;
    }

    private static EncryptedSyncEnvelope Envelope() => new(
        Aad(SyncRecordKind.Upsert, SyncDataCategory.History, 0),
        Enumerable.Repeat((byte)1, 12).ToArray(),
        Encoding.UTF8.GetBytes("opaque-ciphertext-not-a-url"),
        Enumerable.Repeat((byte)2, 16).ToArray());

    private static EncryptedSyncTombstone Tombstone() => new(
        Aad(SyncRecordKind.Tombstone, SyncDataCategory.OpenTabs, 1),
        Enumerable.Repeat((byte)3, 12).ToArray(),
        Encoding.UTF8.GetBytes("opaque-tombstone"),
        Enumerable.Repeat((byte)4, 16).ToArray());

    private static CanonicalSyncAad Aad(
        SyncRecordKind kind,
        SyncDataCategory category,
        long sequence) => new(
            1,
            1,
            Profile,
            Device,
            Keyset,
            0,
            kind,
            new SyncEnvelopeId(sequence == 0
                ? Guid.ParseExact("44444444444444444444444444444444", "N")
                : Guid.ParseExact("66666666666666666666666666666666", "N")),
            category,
            new SyncEntityId(Guid.ParseExact("55555555555555555555555555555555", "N")),
            null,
            0,
            sequence);

    private static object Wire(EncryptedSyncEnvelope value) => Wire(
        value.Aad, value.Nonce, value.Ciphertext, value.AuthenticationTag);

    private static object Wire(EncryptedSyncTombstone value) => Wire(
        value.Aad, value.Nonce, value.Ciphertext, value.AuthenticationTag);

    private static object Wire(
        CanonicalSyncAad aad,
        ReadOnlyMemory<byte> nonce,
        ReadOnlyMemory<byte> ciphertext,
        ReadOnlyMemory<byte> tag) => new
        {
            aad = new
            {
                protocol_version = aad.ProtocolVersion,
                schema_version = aad.SchemaVersion,
                profile_id = aad.ProfileId.Value.ToString("N"),
                device_id = aad.DeviceId.Value.ToString("N"),
                keyset_id = aad.KeysetId.Value.ToString("N"),
                key_epoch = aad.KeyEpoch,
                record_kind = aad.RecordKind == SyncRecordKind.Upsert ? "upsert" : "tombstone",
                envelope_id = aad.EnvelopeId.Value.ToString("N"),
                category = aad.Category == SyncDataCategory.History ? "history" : "open_tabs",
                entity_id = aad.EntityId.Value.ToString("N"),
                operation_id = (string?)null,
                client_generation = aad.ClientGeneration,
                client_sequence = aad.ClientSequence,
            },
            nonce = Base64Url(nonce.Span),
            ciphertext = Base64Url(ciphertext.Span),
            authentication_tag = Base64Url(tag.Span),
        };

    private static object FenceJson() => new
    {
        device_id = Device.Value.ToString("N"),
        client_generation = 0,
        minimum_accepted_generation = 0,
    };

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage CookieResponse()
    {
        var response = Json(new { error = "unexpected" });
        response.Headers.TryAddWithoutValidation("Set-Cookie", "session=forbidden");
        return response;
    }

    private static string Base64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static readonly ProfileId Profile = new(
        Guid.ParseExact("22222222222222222222222222222222", "N"));
    private static readonly DeviceId Device = new(
        Guid.ParseExact("11111111111111111111111111111111", "N"));
    private static readonly SyncKeysetId Keyset = new(
        Guid.ParseExact("33333333333333333333333333333333", "N"));
    private static readonly ClientFence Fence = new(Device, 0, 0);

    private sealed class CredentialResolver : IMyOrbitBearerCredentialResolver
    {
        public OpaqueAuthHandle Handle { get; } = new(Guid.NewGuid());
        public int ResolveCalls { get; private set; }

        public ControllerResult<SensitiveUtf8Buffer> Resolve(OpaqueAuthHandle handle)
        {
            ResolveCalls++;
            return handle == Handle
                ? ControllerResult<SensitiveUtf8Buffer>.Success(new(
                    Encoding.ASCII.GetBytes("moat_" + new string('A', 43))))
                : ControllerResult<SensitiveUtf8Buffer>.Failure(ControllerError.Create(
                    ControllerErrorCode.Unavailable, "sync.authorization.unavailable"));
        }
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new(
                request.RequestUri!,
                request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Headers.Authorization?.Scheme,
                request.Headers.Select(item => item.Key)
                    .Concat(request.Content?.Headers.Select(item => item.Key) ?? [])
                    .ToArray()));
            return response(request);
        }
    }

    private sealed record RecordedRequest(
        Uri Uri,
        string Body,
        string? AuthorizationScheme,
        IReadOnlyList<string> HeaderNames);
}
