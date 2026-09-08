using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Sync;
using OrbitNavigator.Sync.Accounts;

namespace OrbitNavigator.Sync.Transport;

internal interface IMyOrbitBearerCredentialResolver
{
    ControllerResult<SensitiveUtf8Buffer> Resolve(OpaqueAuthHandle handle);
}

/// <summary>
/// Cookie-free My Orbit relay client. Browser data is encrypted before this
/// boundary; only canonical routing AAD and ciphertext are serialized here.
/// </summary>
internal sealed class MyOrbitOpaqueSyncTransport : ISyncTransport, IDisposable
{
    internal const int MaximumResponseBytes = 32 * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly IMyOrbitBearerCredentialResolver _credentials;
    private readonly bool _ownsHandler;

    public MyOrbitOpaqueSyncTransport(
        MyOrbitAccountProviderOptions options,
        IMyOrbitBearerCredentialResolver credentials)
        : this(options, credentials, CreateHandler(), ownsHandler: true)
    {
    }

    internal MyOrbitOpaqueSyncTransport(
        MyOrbitAccountProviderOptions options,
        IMyOrbitBearerCredentialResolver credentials,
        HttpMessageHandler handler,
        bool ownsHandler = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _ownsHandler = ownsHandler;
        _http = new HttpClient(handler ?? throw new ArgumentNullException(nameof(handler)), ownsHandler)
        {
            BaseAddress = options.Authority,
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoStore = true };
    }

    public async ValueTask<ControllerResult<SyncPushReceipt>> PushAsync(
        SyncOperationContext context,
        OpaqueAuthHandle authorization,
        SyncPushRequest request,
        CancellationToken cancellationToken)
    {
        var validation = ValidateContext(context, authorization);
        if (validation is not null)
            return ControllerResult<SyncPushReceipt>.Failure(validation);
        if (!SyncTransferRules.ValidatePush(request).IsValid ||
            request.Purges.Count != 0 ||
            request.Envelopes.Any(value => !AllowedLocal(value.Aad, context, request)) ||
            request.Tombstones.Any(value => !AllowedLocal(value.Aad, context, request)))
        {
            return Invalid<SyncPushReceipt>();
        }

        var credential = _credentials.Resolve(authorization);
        if (!credential.IsSuccess)
            return ControllerResult<SyncPushReceipt>.Failure(credential.Error!);
        using var bearer = credential.Value!;
        try
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("device_id", Id(request.DeviceId.Value));
                WriteFence(writer, request.Fence);
                writer.WritePropertyName("records");
                writer.WriteStartArray();
                foreach (var envelope in request.Envelopes)
                    WriteRecord(writer, envelope.Aad, envelope.Nonce.Span, envelope.Ciphertext.Span,
                        envelope.AuthenticationTag.Span);
                foreach (var tombstone in request.Tombstones)
                    WriteRecord(writer, tombstone.Aad, tombstone.Nonce.Span, tombstone.Ciphertext.Span,
                        tombstone.AuthenticationTag.Span);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            using var content = new ByteArrayContent(stream.ToArray());
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            var response = await SendAsync(HttpMethod.Post, "navigator-sync/v1/records/push", content,
                bearer, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccess)
                return ControllerResult<SyncPushReceipt>.Failure(response.Error!);
            using var owned = response.Value!;
            if (owned.StatusCode != HttpStatusCode.OK)
                return ProviderFailure<SyncPushReceipt>(owned.StatusCode);
            return ParsePushReceipt(owned.Payload, request.Fence);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return Integrity<SyncPushReceipt>();
        }
    }

    public async ValueTask<ControllerResult<SyncPullPage>> PullAsync(
        SyncOperationContext context,
        OpaqueAuthHandle authorization,
        SyncPullRequest request,
        CancellationToken cancellationToken)
    {
        var validation = ValidateContext(context, authorization);
        if (validation is not null)
            return ControllerResult<SyncPullPage>.Failure(validation);
        if (!SyncTransferRules.ValidatePull(request).IsValid ||
            !ValidCursor(request.After))
            return Invalid<SyncPullPage>();

        var credential = _credentials.Resolve(authorization);
        if (!credential.IsSuccess)
            return ControllerResult<SyncPullPage>.Failure(credential.Error!);
        using var bearer = credential.Value!;
        var relative = "navigator-sync/v1/records/pull?device_id=" + Id(request.DeviceId.Value) +
            "&client_generation=" + Number(request.Fence.ClientGeneration) +
            "&minimum_accepted_generation=" + Number(request.Fence.MinimumAcceptedGeneration) +
            "&after=" + Uri.EscapeDataString(request.After?.Value ?? "0") +
            "&maximum_items=" + Number(request.MaximumItems);
        var response = await SendAsync(HttpMethod.Get, relative, null, bearer, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccess)
            return ControllerResult<SyncPullPage>.Failure(response.Error!);
        using var owned = response.Value!;
        if (owned.StatusCode != HttpStatusCode.OK)
            return ProviderFailure<SyncPullPage>(owned.StatusCode);
        var parsed = ParsePullPage(owned.Payload, request.Fence);
        if (!parsed.IsSuccess)
            return parsed;
        return parsed.Value!.Envelopes.Any(value => !AllowedRemote(value.Aad, context, request.Fence)) ||
            parsed.Value.Tombstones.Any(value => !AllowedRemote(value.Aad, context, request.Fence))
            ? Integrity<SyncPullPage>()
            : parsed;
    }

    public void Dispose()
    {
        _http.Dispose();
        _ = _ownsHandler;
    }

    private static ControllerResult<SyncPushReceipt> ParsePushReceipt(
        byte[] payload,
        ClientFence expectedFence)
    {
        try
        {
            using var json = JsonDocument.Parse(payload, StrictDocumentOptions);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 6)
                return Integrity<SyncPushReceipt>();
            var cursor = ParseCursor(root.GetProperty("cursor"));
            var fence = ParseFence(root.GetProperty("fence"));
            var envelopes = root.GetProperty("accepted_envelope_count").GetInt32();
            var tombstones = root.GetProperty("accepted_tombstone_count").GetInt32();
            var purges = root.GetProperty("accepted_purge_count").GetInt32();
            _ = root.GetProperty("new_record_count").GetInt32();
            if (cursor is null || fence != expectedFence || envelopes < 0 || tombstones < 0 || purges != 0)
                return Integrity<SyncPushReceipt>();
            return ControllerResult<SyncPushReceipt>.Success(new(
                cursor,
                fence,
                envelopes,
                tombstones,
                purges));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return Integrity<SyncPushReceipt>();
        }
    }

    private static ControllerResult<SyncPullPage> ParsePullPage(
        byte[] payload,
        ClientFence expectedFence)
    {
        try
        {
            using var json = JsonDocument.Parse(payload, StrictDocumentOptions);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 4)
                return Integrity<SyncPullPage>();
            var cursor = ParseCursor(root.GetProperty("cursor"));
            var fence = ParseFence(root.GetProperty("fence"));
            var hasMore = root.GetProperty("has_more").GetBoolean();
            if (cursor is null || fence != expectedFence)
                return Integrity<SyncPullPage>();

            var envelopes = new List<EncryptedSyncEnvelope>();
            var tombstones = new List<EncryptedSyncTombstone>();
            foreach (var record in root.GetProperty("records").EnumerateArray())
            {
                if (envelopes.Count + tombstones.Count >= 250)
                    return Integrity<SyncPullPage>();
                var decoded = ParseRecord(record);
                if (decoded is null || !Allowed(decoded.Aad))
                    return Integrity<SyncPullPage>();
                if (decoded.Aad.RecordKind == SyncRecordKind.Upsert)
                    envelopes.Add(new(decoded.Aad, decoded.Nonce, decoded.Ciphertext, decoded.Tag));
                else if (decoded.Aad.RecordKind == SyncRecordKind.Tombstone)
                    tombstones.Add(new(decoded.Aad, decoded.Nonce, decoded.Ciphertext, decoded.Tag));
                else
                    return Integrity<SyncPullPage>();
            }
            return ControllerResult<SyncPullPage>.Success(new(
                cursor,
                fence,
                envelopes.AsReadOnly(),
                tombstones.AsReadOnly(),
                Array.Empty<EncryptedPurgeCommand>(),
                hasMore));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return Integrity<SyncPullPage>();
        }
    }

    private static DecodedRecord? ParseRecord(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || element.EnumerateObject().Count() != 4)
            return null;
        var aad = ParseAad(element.GetProperty("aad"));
        var nonce = Decode(element.GetProperty("nonce"), SyncProtocol.NonceSizeBytes, SyncProtocol.NonceSizeBytes);
        var ciphertext = Decode(element.GetProperty("ciphertext"), 1, SyncProtocol.MaximumCiphertextSizeBytes);
        var tag = Decode(element.GetProperty("authentication_tag"),
            SyncProtocol.AuthenticationTagSizeBytes, SyncProtocol.AuthenticationTagSizeBytes);
        return aad is null || nonce is null || ciphertext is null || tag is null
            ? null
            : new(aad, nonce, ciphertext, tag);
    }

    private static CanonicalSyncAad? ParseAad(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 13 ||
            !ParseId(value.GetProperty("profile_id"), out var profile) ||
            !ParseId(value.GetProperty("device_id"), out var device) ||
            !ParseId(value.GetProperty("keyset_id"), out var keyset) ||
            !ParseId(value.GetProperty("envelope_id"), out var envelope) ||
            !ParseId(value.GetProperty("entity_id"), out var entity))
            return null;
        var category = value.GetProperty("category").GetString() switch
        {
            "history" => SyncDataCategory.History,
            "open_tabs" => SyncDataCategory.OpenTabs,
            _ => (SyncDataCategory?)null,
        };
        var kind = value.GetProperty("record_kind").GetString() switch
        {
            "upsert" => SyncRecordKind.Upsert,
            "tombstone" => SyncRecordKind.Tombstone,
            _ => (SyncRecordKind?)null,
        };
        var operation = value.GetProperty("operation_id");
        if (category is null || kind is null || operation.ValueKind != JsonValueKind.Null)
            return null;
        var aad = new CanonicalSyncAad(
            value.GetProperty("protocol_version").GetInt32(),
            value.GetProperty("schema_version").GetInt32(),
            new ProfileId(profile),
            new DeviceId(device),
            new SyncKeysetId(keyset),
            value.GetProperty("key_epoch").GetInt64(),
            kind.Value,
            new SyncEnvelopeId(envelope),
            category.Value,
            new SyncEntityId(entity),
            null,
            value.GetProperty("client_generation").GetInt64(),
            value.GetProperty("client_sequence").GetInt64());
        return SyncContractRules.ValidateAad(aad).IsValid ? aad : null;
    }

    private static void WriteRecord(
        Utf8JsonWriter writer,
        CanonicalSyncAad aad,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("aad");
        writer.WriteStartObject();
        writer.WriteNumber("protocol_version", aad.ProtocolVersion);
        writer.WriteNumber("schema_version", aad.SchemaVersion);
        writer.WriteString("profile_id", Id(aad.ProfileId.Value));
        writer.WriteString("device_id", Id(aad.DeviceId.Value));
        writer.WriteString("keyset_id", Id(aad.KeysetId.Value));
        writer.WriteNumber("key_epoch", aad.KeyEpoch);
        writer.WriteString("record_kind", Kind(aad.RecordKind));
        writer.WriteString("envelope_id", Id(aad.EnvelopeId.Value));
        writer.WriteString("category", Category(aad.Category));
        writer.WriteString("entity_id", Id(aad.EntityId.Value));
        writer.WriteNull("operation_id");
        writer.WriteNumber("client_generation", aad.ClientGeneration);
        writer.WriteNumber("client_sequence", aad.ClientSequence);
        writer.WriteEndObject();
        writer.WriteString("nonce", Encode(nonce));
        writer.WriteString("ciphertext", Encode(ciphertext));
        writer.WriteString("authentication_tag", Encode(tag));
        writer.WriteEndObject();
    }

    private static void WriteFence(Utf8JsonWriter writer, ClientFence fence)
    {
        writer.WritePropertyName("fence");
        writer.WriteStartObject();
        writer.WriteString("device_id", Id(fence.DeviceId.Value));
        writer.WriteNumber("client_generation", fence.ClientGeneration);
        writer.WriteNumber("minimum_accepted_generation", fence.MinimumAcceptedGeneration);
        writer.WriteEndObject();
    }

    private static ClientFence? ParseFence(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 3 ||
            !ParseId(value.GetProperty("device_id"), out var device))
            return null;
        var fence = new ClientFence(
            new DeviceId(device),
            value.GetProperty("client_generation").GetInt64(),
            value.GetProperty("minimum_accepted_generation").GetInt64());
        return fence.IsDefined ? fence : null;
    }

    private static SyncCursor? ParseCursor(JsonElement value)
    {
        var text = value.GetString();
        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? new SyncCursor(text!)
            : null;
    }

    private async ValueTask<ControllerResult<OwnedResponse>> SendAsync(
        HttpMethod method,
        string relative,
        HttpContent? content,
        SensitiveUtf8Buffer bearer,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(method, relative) { Content = content };
            request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer", Encoding.ASCII.GetString(bearer.Bytes.Span));
            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400 ||
                response.Headers.Location is not null ||
                response.Headers.TryGetValues("Set-Cookie", out _))
                return Integrity<OwnedResponse>();
            var bytes = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            return bytes is null
                ? Integrity<OwnedResponse>()
                : ControllerResult<OwnedResponse>.Success(new(response.StatusCode, bytes));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ControllerResult<OwnedResponse>.Failure(ControllerError.Create(
                ControllerErrorCode.Cancelled, "sync.transport.cancelled"));
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
        {
            return ControllerResult<OwnedResponse>.Failure(ControllerError.Create(
                ControllerErrorCode.Unavailable, "sync.transport.unavailable", isRetryable: true));
        }
    }

    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > MaximumResponseBytes) return null;
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static HttpMessageHandler CreateHandler() => new HttpClientHandler
    {
        UseCookies = false,
        AllowAutoRedirect = false,
    };

    private static ControllerError? ValidateContext(SyncOperationContext? context, OpaqueAuthHandle authorization)
    {
        if (context is null || authorization.IsEmpty)
            return Error(ControllerErrorCode.InvalidRequest, "sync.transport.invalid");
        return context.Browsing.Privacy.IsPrivate
            ? Error(ControllerErrorCode.PolicyDenied, "sync.private-mode.policy-denied")
            : null;
    }

    private static bool Allowed(CanonicalSyncAad aad) =>
        aad.Category is SyncDataCategory.History or SyncDataCategory.OpenTabs &&
        aad.RecordKind is SyncRecordKind.Upsert or SyncRecordKind.Tombstone &&
        aad.OperationId is null;

    private static bool AllowedLocal(
        CanonicalSyncAad aad,
        SyncOperationContext context,
        SyncPushRequest request) =>
        Allowed(aad) &&
        aad.ProfileId == context.Browsing.Privacy.ProfileId &&
        aad.DeviceId == request.DeviceId &&
        aad.ClientGeneration == request.Fence.ClientGeneration;

    private static bool AllowedRemote(
        CanonicalSyncAad aad,
        SyncOperationContext context,
        ClientFence fence) =>
        Allowed(aad) &&
        aad.ProfileId == context.Browsing.Privacy.ProfileId &&
        aad.ClientGeneration >= fence.MinimumAcceptedGeneration;

    private static bool ValidCursor(SyncCursor? cursor) => cursor is null ||
        long.TryParse(cursor.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0;

    private static string Category(SyncDataCategory category) => category switch
    {
        SyncDataCategory.History => "history",
        SyncDataCategory.OpenTabs => "open_tabs",
        _ => throw new InvalidOperationException("That category is not enabled for this transport."),
    };

    private static string Kind(SyncRecordKind kind) => kind switch
    {
        SyncRecordKind.Upsert => "upsert",
        SyncRecordKind.Tombstone => "tombstone",
        _ => throw new InvalidOperationException("Purge records are not enabled for this transport."),
    };

    private static string Encode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[]? Decode(JsonElement value, int minimum, int maximum)
    {
        var text = value.GetString();
        if (string.IsNullOrEmpty(text) || text.Length > ((maximum + 2) / 3) * 4 ||
            text.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            return null;
        try
        {
            var normalized = text.Replace('-', '+').Replace('_', '/');
            normalized += new string('=', (4 - normalized.Length % 4) % 4);
            var bytes = Convert.FromBase64String(normalized);
            return bytes.Length >= minimum && bytes.Length <= maximum ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool ParseId(JsonElement value, out Guid id) =>
        Guid.TryParseExact(value.GetString(), "N", out id) && id != Guid.Empty;

    private static string Id(Guid value) => value.ToString("N", CultureInfo.InvariantCulture);
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static ControllerResult<T> Invalid<T>() where T : class =>
        ControllerResult<T>.Failure(Error(ControllerErrorCode.InvalidRequest, "sync.transport.invalid"));

    private static ControllerResult<T> Integrity<T>() where T : class =>
        ControllerResult<T>.Failure(Error(ControllerErrorCode.IntegrityFailure, "sync.transport.integrity-failure"));

    private static ControllerResult<T> ProviderFailure<T>(HttpStatusCode status) where T : class =>
        ControllerResult<T>.Failure(Error(
            status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? ControllerErrorCode.Unavailable
                : status == HttpStatusCode.Conflict
                    ? ControllerErrorCode.StaleClient
                    : ControllerErrorCode.Unavailable,
            status == HttpStatusCode.Conflict ? "sync.transport.stale-client" : "sync.transport.unavailable"));

    private static ControllerError Error(ControllerErrorCode code, string key) =>
        ControllerError.Create(code, key, isRetryable: code == ControllerErrorCode.Unavailable);

    private sealed record DecodedRecord(
        CanonicalSyncAad Aad,
        byte[] Nonce,
        byte[] Ciphertext,
        byte[] Tag);

    private sealed record OwnedResponse(HttpStatusCode StatusCode, byte[] Payload) : IDisposable
    {
        public void Dispose() => Array.Clear(Payload);
    }

    private static readonly JsonDocumentOptions StrictDocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 8,
    };
}
