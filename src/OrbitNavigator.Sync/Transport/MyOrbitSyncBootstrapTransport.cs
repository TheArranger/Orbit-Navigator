using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Privacy;
using OrbitNavigator.Contracts.Sync;
using OrbitNavigator.Sync.Accounts;

namespace OrbitNavigator.Sync.Transport;

internal sealed record MyOrbitRecoveryEnvelopeUpload(
    DeviceId DeviceId,
    ClientFence Fence,
    ProfileId ProfileId,
    long EnvelopeRevision,
    long ReplacesRevision,
    WrappedSyncKeyset WrappedKeyset);

internal sealed record MyOrbitRecoveryEnvelopeUploadReceipt(
    ProfileId ProfileId,
    SyncKeysetId KeysetId,
    long KeyEpoch,
    long EnvelopeRevision,
    bool Changed,
    DateTimeOffset StoredAtUtc);

internal sealed record MyOrbitRecoveryEnvelopeDownload(
    ProfileId ProfileId,
    long EnvelopeRevision,
    WrappedSyncKeyset WrappedKeyset);

internal interface IMyOrbitRecoveryEnvelopeTransport
{
    ValueTask<ControllerResult<MyOrbitRecoveryEnvelopeUploadReceipt>> UploadAsync(
        SyncOperationContext context,
        OpaqueAuthHandle authorization,
        MyOrbitRecoveryEnvelopeUpload request,
        CancellationToken cancellationToken);

    ValueTask<ControllerResult<MyOrbitRecoveryEnvelopeDownload>> FetchAsync(
        SyncOperationContext context,
        OpaqueAuthHandle authorization,
        DeviceId deviceId,
        ClientFence fence,
        CancellationToken cancellationToken);
}

/// <summary>
/// Strict cookie-free client for the provider-generated sync-device identity.
/// Provider connection identifiers never cross this boundary as sync DeviceIds.
/// </summary>
internal sealed class MyOrbitSyncDeviceRegistry : ISyncDeviceRegistry, IDisposable
{
    private readonly MyOrbitSyncBootstrapHttpClient _client;

    public MyOrbitSyncDeviceRegistry(
        MyOrbitAccountProviderOptions options,
        IMyOrbitBearerCredentialResolver credentials)
        : this(options, credentials, MyOrbitSyncBootstrapHttpClient.CreateHandler(), ownsHandler: true)
    {
    }

    internal MyOrbitSyncDeviceRegistry(
        MyOrbitAccountProviderOptions options,
        IMyOrbitBearerCredentialResolver credentials,
        HttpMessageHandler handler,
        bool ownsHandler = false)
    {
        _client = new(options, credentials, handler, ownsHandler);
    }

    public async ValueTask<ControllerResult<SyncDeviceRegistration>> RegisterAsync(
        SyncOperationContext context,
        OpaqueAuthHandle authorization,
        SyncDeviceRegistrationRequest request,
        CancellationToken cancellationToken)
    {
        var invalid = MyOrbitSyncBootstrapRules.ValidateContext(context, authorization);
        if (invalid is not null)
            return ControllerResult<SyncDeviceRegistration>.Failure(invalid);
        var normalizedName = MyOrbitSyncBootstrapRules.NormalizeDisplayName(request?.DisplayName);
        if (normalizedName is null || request!.PublicIdentityKey.Length is < 32 or > 4096)
            return MyOrbitSyncBootstrapRules.Invalid<SyncDeviceRegistration>();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("display_name", normalizedName);
            writer.WriteString("public_identity_key", MyOrbitSyncBootstrapRules.Encode(request.PublicIdentityKey.Span));
            writer.WriteEndObject();
        }

        using var content = MyOrbitSyncBootstrapRules.JsonContent(stream.ToArray());
        var response = await _client.SendAsync(
            HttpMethod.Post,
            "navigator-sync/v1/devices/register",
            content,
            authorization,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
            return ControllerResult<SyncDeviceRegistration>.Failure(response.Error!);
        using var owned = response.Value!;
        if (owned.StatusCode != HttpStatusCode.OK)
            return MyOrbitSyncBootstrapRules.ProviderFailure<SyncDeviceRegistration>(owned.StatusCode);
        return ParseRegistration(owned.Payload, normalizedName);
    }

    public ValueTask<ControllerResult<SyncDeviceRevocationReceipt>> RevokeAsync(
        SyncOperationContext context,
        OpaqueAuthHandle authorization,
        SensitiveActionAuthorizationToken sensitiveAuthorization,
        DeviceId deviceId,
        CancellationToken cancellationToken)
    {
        var invalid = MyOrbitSyncBootstrapRules.ValidateContext(context, authorization);
        if (invalid is not null)
            return ValueTask.FromResult(ControllerResult<SyncDeviceRevocationReceipt>.Failure(invalid));

        // V1 revokes the owning OAuth connection through the account controller.
        // The provider deliberately has no separate sync-device deletion endpoint.
        return ValueTask.FromResult(ControllerResult<SyncDeviceRevocationReceipt>.Failure(
            ControllerError.Create(
                ControllerErrorCode.NotSupported,
                "sync.device.revoke-through-account")));
    }

    public void Dispose() => _client.Dispose();

    private static ControllerResult<SyncDeviceRegistration> ParseRegistration(
        byte[] payload,
        string expectedDisplayName)
    {
        try
        {
            using var json = JsonDocument.Parse(payload, MyOrbitSyncBootstrapRules.StrictDocumentOptions);
            var root = json.RootElement;
            if (!MyOrbitSyncBootstrapRules.HasExactProperties(
                    root, "device_id", "display_name", "registered_at", "fence") ||
                !MyOrbitSyncBootstrapRules.ParseId(root.GetProperty("device_id"), out var deviceGuid) ||
                root.GetProperty("display_name").GetString() != expectedDisplayName ||
                !MyOrbitSyncBootstrapRules.ParseTimestamp(root.GetProperty("registered_at"), out var registeredAt) ||
                !MyOrbitSyncBootstrapRules.TryParseFence(root.GetProperty("fence"), out var fence) ||
                fence!.DeviceId.Value != deviceGuid)
            {
                return MyOrbitSyncBootstrapRules.Integrity<SyncDeviceRegistration>();
            }

            return ControllerResult<SyncDeviceRegistration>.Success(new(
                new DeviceId(deviceGuid),
                expectedDisplayName,
                registeredAt,
                fence));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return MyOrbitSyncBootstrapRules.Integrity<SyncDeviceRegistration>();
        }
    }
}

/// <summary>
/// Exchanges only an opaque recovery-code-wrapped root-key envelope. Recovery
/// codes, derived keys, and unwrapped root keys never enter this transport.
/// </summary>
internal sealed class MyOrbitRecoveryEnvelopeTransport : IMyOrbitRecoveryEnvelopeTransport, IDisposable
{
    private readonly MyOrbitSyncBootstrapHttpClient _client;

    public MyOrbitRecoveryEnvelopeTransport(
        MyOrbitAccountProviderOptions options,
        IMyOrbitBearerCredentialResolver credentials)
        : this(options, credentials, MyOrbitSyncBootstrapHttpClient.CreateHandler(), ownsHandler: true)
    {
    }

    internal MyOrbitRecoveryEnvelopeTransport(
        MyOrbitAccountProviderOptions options,
        IMyOrbitBearerCredentialResolver credentials,
        HttpMessageHandler handler,
        bool ownsHandler = false)
    {
        _client = new(options, credentials, handler, ownsHandler);
    }

    public async ValueTask<ControllerResult<MyOrbitRecoveryEnvelopeUploadReceipt>> UploadAsync(
        SyncOperationContext context,
        OpaqueAuthHandle authorization,
        MyOrbitRecoveryEnvelopeUpload request,
        CancellationToken cancellationToken)
    {
        var invalid = MyOrbitSyncBootstrapRules.ValidateContext(context, authorization);
        if (invalid is not null)
            return ControllerResult<MyOrbitRecoveryEnvelopeUploadReceipt>.Failure(invalid);
        if (!MyOrbitSyncBootstrapRules.ValidUpload(context, request))
            return MyOrbitSyncBootstrapRules.Invalid<MyOrbitRecoveryEnvelopeUploadReceipt>();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("device_id", MyOrbitSyncBootstrapRules.Id(request.DeviceId.Value));
            MyOrbitSyncBootstrapRules.WriteFence(writer, request.Fence);
            writer.WriteString("profile_id", MyOrbitSyncBootstrapRules.Id(request.ProfileId.Value));
            writer.WriteNumber("envelope_revision", request.EnvelopeRevision);
            writer.WriteNumber("replaces_revision", request.ReplacesRevision);
            writer.WritePropertyName("wrapped_keyset");
            MyOrbitSyncBootstrapRules.WriteWrappedKeyset(writer, request.WrappedKeyset);
            writer.WriteEndObject();
        }

        using var content = MyOrbitSyncBootstrapRules.JsonContent(stream.ToArray());
        var response = await _client.SendAsync(
            HttpMethod.Put,
            "navigator-sync/v1/keysets/recovery-envelope",
            content,
            authorization,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
            return ControllerResult<MyOrbitRecoveryEnvelopeUploadReceipt>.Failure(response.Error!);
        using var owned = response.Value!;
        if (owned.StatusCode != HttpStatusCode.OK)
            return MyOrbitSyncBootstrapRules.ProviderFailure<MyOrbitRecoveryEnvelopeUploadReceipt>(owned.StatusCode);
        return ParseUploadReceipt(owned.Payload, request);
    }

    public async ValueTask<ControllerResult<MyOrbitRecoveryEnvelopeDownload>> FetchAsync(
        SyncOperationContext context,
        OpaqueAuthHandle authorization,
        DeviceId deviceId,
        ClientFence fence,
        CancellationToken cancellationToken)
    {
        var invalid = MyOrbitSyncBootstrapRules.ValidateContext(context, authorization);
        if (invalid is not null)
            return ControllerResult<MyOrbitRecoveryEnvelopeDownload>.Failure(invalid);
        if (deviceId.IsEmpty || fence is not { IsDefined: true } || fence.DeviceId != deviceId)
            return MyOrbitSyncBootstrapRules.Invalid<MyOrbitRecoveryEnvelopeDownload>();

        var relative = "navigator-sync/v1/keysets/recovery-envelope?device_id=" +
            MyOrbitSyncBootstrapRules.Id(deviceId.Value) +
            "&client_generation=" + MyOrbitSyncBootstrapRules.Number(fence.ClientGeneration) +
            "&minimum_accepted_generation=" + MyOrbitSyncBootstrapRules.Number(fence.MinimumAcceptedGeneration);
        var response = await _client.SendAsync(
            HttpMethod.Get,
            relative,
            null,
            authorization,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
            return ControllerResult<MyOrbitRecoveryEnvelopeDownload>.Failure(response.Error!);
        using var owned = response.Value!;
        if (owned.StatusCode != HttpStatusCode.OK)
            return MyOrbitSyncBootstrapRules.ProviderFailure<MyOrbitRecoveryEnvelopeDownload>(owned.StatusCode);
        return ParseDownload(owned.Payload);
    }

    public void Dispose() => _client.Dispose();

    private static ControllerResult<MyOrbitRecoveryEnvelopeUploadReceipt> ParseUploadReceipt(
        byte[] payload,
        MyOrbitRecoveryEnvelopeUpload request)
    {
        try
        {
            using var json = JsonDocument.Parse(payload, MyOrbitSyncBootstrapRules.StrictDocumentOptions);
            var root = json.RootElement;
            if (!MyOrbitSyncBootstrapRules.HasExactProperties(
                    root, "profile_id", "keyset_id", "key_epoch", "envelope_revision", "changed", "stored_at") ||
                !MyOrbitSyncBootstrapRules.ParseId(root.GetProperty("profile_id"), out var profile) ||
                !MyOrbitSyncBootstrapRules.ParseId(root.GetProperty("keyset_id"), out var keyset) ||
                profile != request.ProfileId.Value || keyset != request.WrappedKeyset.KeysetId.Value ||
                root.GetProperty("key_epoch").GetInt64() != request.WrappedKeyset.Generation ||
                root.GetProperty("envelope_revision").GetInt64() != request.EnvelopeRevision ||
                !MyOrbitSyncBootstrapRules.ParseTimestamp(root.GetProperty("stored_at"), out var storedAt))
            {
                return MyOrbitSyncBootstrapRules.Integrity<MyOrbitRecoveryEnvelopeUploadReceipt>();
            }

            return ControllerResult<MyOrbitRecoveryEnvelopeUploadReceipt>.Success(new(
                new ProfileId(profile),
                new SyncKeysetId(keyset),
                request.WrappedKeyset.Generation,
                request.EnvelopeRevision,
                root.GetProperty("changed").GetBoolean(),
                storedAt));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return MyOrbitSyncBootstrapRules.Integrity<MyOrbitRecoveryEnvelopeUploadReceipt>();
        }
    }

    private static ControllerResult<MyOrbitRecoveryEnvelopeDownload> ParseDownload(byte[] payload)
    {
        try
        {
            using var json = JsonDocument.Parse(payload, MyOrbitSyncBootstrapRules.StrictDocumentOptions);
            var root = json.RootElement;
            if (!MyOrbitSyncBootstrapRules.HasExactProperties(
                    root, "profile_id", "envelope_revision", "wrapped_keyset") ||
                !MyOrbitSyncBootstrapRules.ParseId(root.GetProperty("profile_id"), out var profile) ||
                root.GetProperty("envelope_revision").GetInt64() < 1 ||
                !MyOrbitSyncBootstrapRules.TryParseWrappedKeyset(
                    root.GetProperty("wrapped_keyset"), out var wrapped))
            {
                return MyOrbitSyncBootstrapRules.Integrity<MyOrbitRecoveryEnvelopeDownload>();
            }

            return ControllerResult<MyOrbitRecoveryEnvelopeDownload>.Success(new(
                new ProfileId(profile),
                root.GetProperty("envelope_revision").GetInt64(),
                wrapped!));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return MyOrbitSyncBootstrapRules.Integrity<MyOrbitRecoveryEnvelopeDownload>();
        }
    }
}

internal sealed class MyOrbitSyncBootstrapHttpClient : IDisposable
{
    internal const int MaximumResponseBytes = 64 * 1024;
    private readonly HttpClient _http;

    public MyOrbitSyncBootstrapHttpClient(
        MyOrbitAccountProviderOptions options,
        IMyOrbitBearerCredentialResolver credentials,
        HttpMessageHandler handler,
        bool ownsHandler)
    {
        ArgumentNullException.ThrowIfNull(options);
        Credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _http = new HttpClient(handler ?? throw new ArgumentNullException(nameof(handler)), ownsHandler)
        {
            BaseAddress = options.Authority,
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoStore = true };
    }

    private IMyOrbitBearerCredentialResolver Credentials { get; }

    public async ValueTask<ControllerResult<MyOrbitBootstrapResponse>> SendAsync(
        HttpMethod method,
        string relativePath,
        HttpContent? content,
        OpaqueAuthHandle authorization,
        CancellationToken cancellationToken)
    {
        var resolved = Credentials.Resolve(authorization);
        if (!resolved.IsSuccess)
            return ControllerResult<MyOrbitBootstrapResponse>.Failure(resolved.Error!);
        using var bearer = resolved.Value!;
        try
        {
            using var request = new HttpRequestMessage(method, relativePath) { Content = content };
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer", Encoding.ASCII.GetString(bearer.Bytes.Span));
            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400 ||
                response.Headers.Location is not null ||
                response.Headers.TryGetValues("Set-Cookie", out _) ||
                response.Headers.CacheControl?.NoStore != true ||
                !response.Headers.TryGetValues("Pragma", out var pragma) ||
                !pragma.Contains("no-cache", StringComparer.OrdinalIgnoreCase))
            {
                return MyOrbitSyncBootstrapRules.Integrity<MyOrbitBootstrapResponse>();
            }

            var payload = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            return payload is null
                ? MyOrbitSyncBootstrapRules.Integrity<MyOrbitBootstrapResponse>()
                : ControllerResult<MyOrbitBootstrapResponse>.Success(new(response.StatusCode, payload));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ControllerResult<MyOrbitBootstrapResponse>.Failure(ControllerError.Create(
                ControllerErrorCode.Cancelled, "sync.bootstrap.cancelled", isRetryable: true));
        }
        catch (HttpRequestException)
        {
            return ControllerResult<MyOrbitBootstrapResponse>.Failure(ControllerError.Create(
                ControllerErrorCode.Unavailable, "sync.bootstrap.unavailable", isRetryable: true));
        }
    }

    public void Dispose() => _http.Dispose();

    internal static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        UseCookies = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    private static async Task<byte[]?> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return output.ToArray();
            if (output.Length + read > MaximumResponseBytes)
                return null;
            output.Write(buffer, 0, read);
        }
    }
}

internal sealed record MyOrbitBootstrapResponse(HttpStatusCode StatusCode, byte[] Payload) : IDisposable
{
    public void Dispose() => CryptographicOperations.ZeroMemory(Payload);
}

internal static class MyOrbitSyncBootstrapRules
{
    internal static readonly JsonDocumentOptions StrictDocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 8,
    };

    public static ControllerError? ValidateContext(
        SyncOperationContext? context,
        OpaqueAuthHandle authorization)
    {
        if (context is null || authorization.IsEmpty)
            return Error(ControllerErrorCode.InvalidRequest, "sync.bootstrap.invalid");
        return context.Browsing.Privacy.IsPrivate
            ? Error(ControllerErrorCode.PolicyDenied, "sync.private-mode.policy-denied")
            : null;
    }

    public static bool ValidUpload(
        SyncOperationContext context,
        MyOrbitRecoveryEnvelopeUpload? request) =>
        request is not null &&
        request.ProfileId == context.Browsing.Privacy.ProfileId &&
        !request.DeviceId.IsEmpty &&
        request.Fence is { IsDefined: true } &&
        request.Fence.DeviceId == request.DeviceId &&
        request.EnvelopeRevision >= 1 &&
        request.ReplacesRevision >= 0 &&
        request.EnvelopeRevision == request.ReplacesRevision + 1 &&
        ValidWrappedKeyset(request.WrappedKeyset);

    public static bool ValidWrappedKeyset(WrappedSyncKeyset? wrapped) =>
        wrapped is not null &&
        wrapped.KeysetId.IsDefined &&
        wrapped.Generation >= 0 &&
        wrapped.WrapMethod == SyncKeyWrapMethod.RecoveryCode &&
        wrapped.Kdf is not null &&
        wrapped.Kdf.Algorithm == SyncKdfAlgorithm.Pbkdf2Sha256 &&
        wrapped.Kdf.Salt.Length == 32 &&
        wrapped.Kdf.Iterations is >= 600_000 and <= 2_000_000 &&
        wrapped.Kdf.MemoryKiB == 0 &&
        wrapped.Kdf.Parallelism == 1 &&
        wrapped.Kdf.DerivedKeySizeBytes == 32 &&
        wrapped.Nonce.Length == 12 &&
        wrapped.WrappedKeyCiphertext.Length == 32 &&
        wrapped.AuthenticationTag.Length == 16;

    public static string? NormalizeDisplayName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = string.Join(' ', value.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized.Length is >= 1 and <= 80 && !normalized.Any(char.IsControl)
            ? normalized
            : null;
    }

    public static bool HasExactProperties(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return false;
        var actual = value.EnumerateObject().Select(property => property.Name).ToArray();
        return actual.Length == expected.Length &&
            actual.Distinct(StringComparer.Ordinal).Count() == actual.Length &&
            actual.ToHashSet(StringComparer.Ordinal).SetEquals(expected);
    }

    public static bool TryParseFence(JsonElement value, out ClientFence? fence)
    {
        fence = null;
        if (!HasExactProperties(value, "device_id", "client_generation", "minimum_accepted_generation") ||
            !ParseId(value.GetProperty("device_id"), out var device))
            return false;
        var candidate = new ClientFence(
            new DeviceId(device),
            value.GetProperty("client_generation").GetInt64(),
            value.GetProperty("minimum_accepted_generation").GetInt64());
        if (!candidate.IsDefined)
            return false;
        fence = candidate;
        return true;
    }

    public static bool TryParseWrappedKeyset(JsonElement value, out WrappedSyncKeyset? wrapped)
    {
        wrapped = null;
        if (!HasExactProperties(
                value, "keyset_id", "generation", "wrap_method", "kdf", "nonce",
                "wrapped_key_ciphertext", "authentication_tag") ||
            !ParseId(value.GetProperty("keyset_id"), out var keyset) ||
            value.GetProperty("wrap_method").GetString() != "recovery_code")
            return false;
        var kdfValue = value.GetProperty("kdf");
        if (!HasExactProperties(
                kdfValue, "algorithm", "salt", "iterations", "memory_kib", "parallelism",
                "derived_key_size_bytes") ||
            kdfValue.GetProperty("algorithm").GetString() != "pbkdf2_sha256")
            return false;

        var salt = Decode(kdfValue.GetProperty("salt"), 32);
        var nonce = Decode(value.GetProperty("nonce"), 12);
        var ciphertext = Decode(value.GetProperty("wrapped_key_ciphertext"), 32);
        var tag = Decode(value.GetProperty("authentication_tag"), 16);
        if (salt is null || nonce is null || ciphertext is null || tag is null)
            return false;
        var kdf = new SyncKdfParameters(
            SyncKdfAlgorithm.Pbkdf2Sha256,
            salt,
            kdfValue.GetProperty("iterations").GetInt32(),
            kdfValue.GetProperty("memory_kib").GetInt32(),
            kdfValue.GetProperty("parallelism").GetInt32(),
            kdfValue.GetProperty("derived_key_size_bytes").GetInt32());
        var candidate = new WrappedSyncKeyset(
            new SyncKeysetId(keyset),
            value.GetProperty("generation").GetInt64(),
            SyncKeyWrapMethod.RecoveryCode,
            kdf,
            nonce,
            ciphertext,
            tag);
        if (!ValidWrappedKeyset(candidate))
            return false;
        wrapped = candidate;
        return true;
    }

    public static void WriteFence(Utf8JsonWriter writer, ClientFence fence)
    {
        writer.WritePropertyName("fence");
        writer.WriteStartObject();
        writer.WriteString("device_id", Id(fence.DeviceId.Value));
        writer.WriteNumber("client_generation", fence.ClientGeneration);
        writer.WriteNumber("minimum_accepted_generation", fence.MinimumAcceptedGeneration);
        writer.WriteEndObject();
    }

    public static void WriteWrappedKeyset(Utf8JsonWriter writer, WrappedSyncKeyset wrapped)
    {
        writer.WriteStartObject();
        writer.WriteString("keyset_id", Id(wrapped.KeysetId.Value));
        writer.WriteNumber("generation", wrapped.Generation);
        writer.WriteString("wrap_method", "recovery_code");
        writer.WritePropertyName("kdf");
        writer.WriteStartObject();
        writer.WriteString("algorithm", "pbkdf2_sha256");
        writer.WriteString("salt", Encode(wrapped.Kdf.Salt.Span));
        writer.WriteNumber("iterations", wrapped.Kdf.Iterations);
        writer.WriteNumber("memory_kib", wrapped.Kdf.MemoryKiB);
        writer.WriteNumber("parallelism", wrapped.Kdf.Parallelism);
        writer.WriteNumber("derived_key_size_bytes", wrapped.Kdf.DerivedKeySizeBytes);
        writer.WriteEndObject();
        writer.WriteString("nonce", Encode(wrapped.Nonce.Span));
        writer.WriteString("wrapped_key_ciphertext", Encode(wrapped.WrappedKeyCiphertext.Span));
        writer.WriteString("authentication_tag", Encode(wrapped.AuthenticationTag.Span));
        writer.WriteEndObject();
    }

    public static StringContent JsonContent(byte[] payload) =>
        new(Encoding.UTF8.GetString(payload), Encoding.UTF8, "application/json");

    public static string Encode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[]? Decode(JsonElement value, int exactLength)
    {
        var text = value.GetString();
        if (string.IsNullOrEmpty(text) || text.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            return null;
        try
        {
            var normalized = text.Replace('-', '+').Replace('_', '/');
            normalized += new string('=', (4 - normalized.Length % 4) % 4);
            var bytes = Convert.FromBase64String(normalized);
            return bytes.Length == exactLength && Encode(bytes) == text ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static bool ParseId(JsonElement value, out Guid id) =>
        Guid.TryParseExact(value.GetString(), "N", out id) && id != Guid.Empty &&
        value.GetString() == id.ToString("N", CultureInfo.InvariantCulture);

    public static bool ParseTimestamp(JsonElement value, out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParseExact(
            value.GetString(),
            "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out timestamp);

    public static string Id(Guid value) => value.ToString("N", CultureInfo.InvariantCulture);

    public static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    public static ControllerResult<T> Invalid<T>() where T : class =>
        ControllerResult<T>.Failure(Error(ControllerErrorCode.InvalidRequest, "sync.bootstrap.invalid"));

    public static ControllerResult<T> Integrity<T>() where T : class =>
        ControllerResult<T>.Failure(Error(ControllerErrorCode.IntegrityFailure, "sync.bootstrap.integrity-failure"));

    public static ControllerResult<T> ProviderFailure<T>(HttpStatusCode status) where T : class =>
        ControllerResult<T>.Failure(Error(
            status == HttpStatusCode.Conflict
                ? ControllerErrorCode.StaleClient
                : status == HttpStatusCode.NotFound
                    ? ControllerErrorCode.NotFound
                    : ControllerErrorCode.Unavailable,
            status == HttpStatusCode.Conflict
                ? "sync.bootstrap.stale-client"
                : status == HttpStatusCode.NotFound
                    ? "sync.bootstrap.not-found"
                    : "sync.bootstrap.unavailable"));

    private static ControllerError Error(ControllerErrorCode code, string key) =>
        ControllerError.Create(code, key, isRetryable: code == ControllerErrorCode.Unavailable);
}
