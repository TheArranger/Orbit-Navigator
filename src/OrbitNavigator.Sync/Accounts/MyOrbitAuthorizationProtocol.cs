using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;

namespace OrbitNavigator.Sync.Accounts;

internal sealed class MyOrbitAuthorizationProtocol : IMyOrbitAuthorizationProtocol
{
    internal const string ClientId = "orbit-navigator";
    internal const string LinkScope = "orbit.navigator.link";
    private const int MaximumResponseBytes = 64 * 1024;
    private readonly Uri _authority;
    private readonly HttpClient _http;
    private readonly IClock _clock;
    private readonly bool _ownsHandler;

    public MyOrbitAuthorizationProtocol(MyOrbitAccountProviderOptions options, IClock clock)
        : this(options, clock, CreateHandler(), ownsHandler: true)
    {
    }

    internal MyOrbitAuthorizationProtocol(
        MyOrbitAccountProviderOptions options,
        IClock clock,
        HttpMessageHandler handler,
        bool ownsHandler = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _authority = options.Authority;
        _ownsHandler = ownsHandler;
        _http = new HttpClient(handler, disposeHandler: ownsHandler)
        {
            BaseAddress = _authority,
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoStore = true };
    }

    public async ValueTask<ControllerResult<MyOrbitPushedAuthorization>> PushAuthorizationAsync(
        MyOrbitPushedAuthorizationRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null || !ValidRedirect(request.RedirectUri) ||
            !ValidState(request.State) || !ValidChallenge(request.CodeChallenge) ||
            !ValidDeviceName(request.DeviceDisplayName))
            return Invalid<MyOrbitPushedAuthorization>();

        using var content = ZeroingFormContent.Create(
            ("client_id", ClientId),
            ("redirect_uri", request.RedirectUri.AbsoluteUri),
            ("scope", LinkScope),
            ("state", request.State),
            ("code_challenge", request.CodeChallenge),
            ("code_challenge_method", "S256"),
            ("response_type", "code"),
            ("device_name", request.DeviceDisplayName));
        var response = await SendAsync(HttpMethod.Post, "oauth2/par", content, null, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccess)
            return ControllerResult<MyOrbitPushedAuthorization>.Failure(response.Error!);
        using var owned = response.Value!;
        if (owned.StatusCode != HttpStatusCode.Created)
            return ProviderFailure<MyOrbitPushedAuthorization>(owned.StatusCode);

        try
        {
            using var json = JsonDocument.Parse(owned.Payload);
            var root = json.RootElement;
            var requestUri = root.GetProperty("request_uri").GetString();
            var expiresIn = root.GetProperty("expires_in").GetInt32();
            const string prefix = "urn:ietf:params:oauth:request_uri:mopr_";
            if (requestUri is null || !requestUri.StartsWith(prefix, StringComparison.Ordinal) ||
                requestUri.Length != prefix.Length + 43 ||
                !requestUri[prefix.Length..].All(IsBase64Url) ||
                expiresIn is < 30 or > 600)
                return Integrity<MyOrbitPushedAuthorization>();

            var query = "client_id=" + Uri.EscapeDataString(ClientId) +
                "&request_uri=" + Uri.EscapeDataString(requestUri);
            var authorizationUri = new Uri(_authority, "oauth2/authorize?" + query);
            return ControllerResult<MyOrbitPushedAuthorization>.Success(new(
                authorizationUri,
                _clock.UtcNow.AddSeconds(expiresIn)));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return Integrity<MyOrbitPushedAuthorization>();
        }
    }

    public async ValueTask<ControllerResult<MyOrbitTokenSet>> ExchangeCodeAsync(
        MyOrbitCodeExchangeRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null || !ValidRedirect(request.RedirectUri) ||
            !ValidAuthorizationCode(request.AuthorizationCode.Bytes.Span) ||
            !ValidVerifier(request.CodeVerifier.Bytes.Span))
            return Invalid<MyOrbitTokenSet>();

        using var content = ZeroingFormContent.CreateSensitive(
            [("grant_type", "authorization_code"), ("client_id", ClientId),
             ("redirect_uri", request.RedirectUri.AbsoluteUri)],
            [("code", request.AuthorizationCode.Bytes),
             ("code_verifier", request.CodeVerifier.Bytes)]);
        return await RequestTokensAsync(content, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ControllerResult<MyOrbitTokenSet>> RefreshAsync(
        SensitiveUtf8Buffer refreshCredential,
        CancellationToken cancellationToken)
    {
        if (refreshCredential is null || !ValidRefreshToken(refreshCredential.Bytes.Span))
            return Invalid<MyOrbitTokenSet>();
        using var content = ZeroingFormContent.CreateSensitive(
            [("grant_type", "refresh_token"), ("client_id", ClientId)],
            [("refresh_token", refreshCredential.Bytes)]);
        return await RequestTokensAsync(content, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ControllerResult> RevokeAsync(
        SensitiveUtf8Buffer credential,
        CancellationToken cancellationToken)
    {
        if (credential is null ||
            !(ValidRefreshToken(credential.Bytes.Span) || ValidAccessToken(credential.Bytes.Span)))
            return ControllerResult.Failure(InvalidError());
        using var content = ZeroingFormContent.CreateSensitive(
            [("client_id", ClientId)],
            [("token", credential.Bytes)]);
        var response = await SendAsync(HttpMethod.Post, "oauth2/revoke", content, null, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccess)
            return ControllerResult.Failure(response.Error!);
        using var owned = response.Value!;
        return owned.StatusCode == HttpStatusCode.OK
            ? ControllerResult.Success()
            : ControllerResult.Failure(ProviderError(owned.StatusCode));
    }

    public async ValueTask<ControllerResult<MyOrbitProtocolDeviceList>> ListDevicesAsync(
        SensitiveUtf8Buffer accessCredential,
        CancellationToken cancellationToken)
    {
        if (accessCredential is null || !ValidAccessToken(accessCredential.Bytes.Span))
            return Invalid<MyOrbitProtocolDeviceList>();
        var response = await SendAsync(
            HttpMethod.Get,
            "oauth2/connections",
            null,
            accessCredential,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
            return ControllerResult<MyOrbitProtocolDeviceList>.Failure(response.Error!);
        using var owned = response.Value!;
        if (owned.StatusCode != HttpStatusCode.OK)
            return ProviderFailure<MyOrbitProtocolDeviceList>(owned.StatusCode);

        try
        {
            using var json = JsonDocument.Parse(owned.Payload);
            var root = json.RootElement;
            var label = SafeAccountLabel(root.TryGetProperty("account_label", out var labelValue)
                ? labelValue.GetString()
                : null);
            var devices = new List<MyOrbitProtocolDevice>();
            foreach (var item in root.GetProperty("connections").EnumerateArray())
            {
                if (devices.Count >= 100)
                    return Integrity<MyOrbitProtocolDeviceList>();
                var idText = item.GetProperty("connection_id").GetString();
                var name = item.GetProperty("device_name").GetString();
                if (!Guid.TryParseExact(idText, "N", out var id) || !ValidDeviceName(name))
                    return Integrity<MyOrbitProtocolDeviceList>();
                devices.Add(new(
                    new DeviceId(id),
                    name!,
                    item.GetProperty("is_current").GetBoolean(),
                    ParseRequiredTimestamp(item.GetProperty("created_at")),
                    ParseOptionalTimestamp(item.GetProperty("last_used_at")),
                    ParseOptionalTimestamp(item.GetProperty("revoked_at"))));
            }
            if (devices.Select(device => device.DeviceId).Distinct().Count() != devices.Count)
                return Integrity<MyOrbitProtocolDeviceList>();
            return ControllerResult<MyOrbitProtocolDeviceList>.Success(new(label, devices.AsReadOnly()));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            return Integrity<MyOrbitProtocolDeviceList>();
        }
    }

    public async ValueTask<ControllerResult> RevokeDeviceAsync(
        SensitiveUtf8Buffer accessCredential,
        DeviceId deviceId,
        CancellationToken cancellationToken)
    {
        if (accessCredential is null || !ValidAccessToken(accessCredential.Bytes.Span) || deviceId.IsEmpty)
            return ControllerResult.Failure(InvalidError());
        using var empty = ZeroingFormContent.Create();
        var response = await SendAsync(
            HttpMethod.Post,
            $"oauth2/connections/{deviceId.Value:N}/revoke",
            empty,
            accessCredential,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
            return ControllerResult.Failure(response.Error!);
        using var owned = response.Value!;
        return owned.StatusCode == HttpStatusCode.OK
            ? ControllerResult.Success()
            : ControllerResult.Failure(ProviderError(owned.StatusCode));
    }

    public void Dispose()
    {
        _http.Dispose();
        _ = _ownsHandler;
    }

    private async ValueTask<ControllerResult<MyOrbitTokenSet>> RequestTokensAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(HttpMethod.Post, "oauth2/token", content, null, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccess)
            return ControllerResult<MyOrbitTokenSet>.Failure(response.Error!);
        using var owned = response.Value!;
        if (owned.StatusCode != HttpStatusCode.OK)
            return ProviderFailure<MyOrbitTokenSet>(owned.StatusCode);
        return ParseTokenSet(owned.Payload);
    }

    private ControllerResult<MyOrbitTokenSet> ParseTokenSet(byte[] payload)
    {
        byte[]? access = null;
        byte[]? refresh = null;
        try
        {
            string? tokenType = null;
            string? scope = null;
            string? connection = null;
            string? label = null;
            string? refreshExpiry = null;
            var expiresIn = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var reader = new Utf8JsonReader(payload, new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4,
            });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return Integrity<MyOrbitTokenSet>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                    return Integrity<MyOrbitTokenSet>();
                var name = reader.GetString();
                if (!reader.Read())
                    return Integrity<MyOrbitTokenSet>();
                if ((name is "access_token" or "refresh_token" or "token_type" or "scope" or
                    "connection_id" or "account_label" or "refresh_expires_at" or "expires_in") &&
                    !seen.Add(name))
                    return Integrity<MyOrbitTokenSet>();
                switch (name)
                {
                    case "access_token": access = CopyUnescapedAscii(ref reader, 256); break;
                    case "refresh_token": refresh = CopyUnescapedAscii(ref reader, 256); break;
                    case "token_type": tokenType = reader.GetString(); break;
                    case "scope": scope = reader.GetString(); break;
                    case "connection_id": connection = reader.GetString(); break;
                    case "account_label": label = reader.GetString(); break;
                    case "refresh_expires_at": refreshExpiry = reader.GetString(); break;
                    case "expires_in": expiresIn = reader.GetInt32(); break;
                    default: reader.Skip(); break;
                }
            }

            if (reader.TokenType != JsonTokenType.EndObject || reader.Read())
                return Integrity<MyOrbitTokenSet>();

            if (tokenType != "Bearer" || scope != LinkScope ||
                access is null || refresh is null ||
                !ValidAccessToken(access) || !ValidRefreshToken(refresh) ||
                !Guid.TryParseExact(connection, "N", out var connectionId) ||
                expiresIn is < 30 or > 3600 ||
                !DateTimeOffset.TryParse(refreshExpiry, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var refreshExpires) ||
                refreshExpires <= _clock.UtcNow)
                return Integrity<MyOrbitTokenSet>();

            var accessOwned = new SensitiveUtf8Buffer(access);
            var refreshOwned = new SensitiveUtf8Buffer(refresh);
            var issued = _clock.UtcNow;
            return ControllerResult<MyOrbitTokenSet>.Success(new(
                accessOwned,
                refreshOwned,
                new DeviceId(connectionId),
                SafeAccountLabel(label),
                issued.AddSeconds(expiresIn),
                refreshExpires,
                issued));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return Integrity<MyOrbitTokenSet>();
        }
        finally
        {
            if (access is not null) CryptographicOperations.ZeroMemory(access);
            if (refresh is not null) CryptographicOperations.ZeroMemory(refresh);
        }
    }

    private async ValueTask<ControllerResult<OwnedResponse>> SendAsync(
        HttpMethod method,
        string relativePath,
        HttpContent? content,
        SensitiveUtf8Buffer? bearer,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(method, relativePath) { Content = content };
            request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            if (bearer is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Bearer",
                    Encoding.ASCII.GetString(bearer.Bytes.Span));
            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400 ||
                response.Headers.Location is not null ||
                response.Headers.TryGetValues("Set-Cookie", out _))
                return Integrity<OwnedResponse>();
            var payload = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            return ControllerResult<OwnedResponse>.Success(new(response.StatusCode, payload));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ControllerResult<OwnedResponse>.Failure(ControllerError.Create(
                ControllerErrorCode.Cancelled,
                "account.link.cancelled"));
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
        {
            return ControllerResult<OwnedResponse>.Failure(ControllerError.Create(
                ControllerErrorCode.Unavailable,
                "account.link.provider-unavailable",
                isRetryable: true));
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var block = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(block, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (buffer.Length + read > MaximumResponseBytes)
                    throw new IOException("Provider response exceeded the allowed size.");
                buffer.Write(block, 0, read);
            }
            return buffer.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(block);
            ArrayPool<byte>.Shared.Return(block);
        }
    }

    private static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        UseCookies = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    private static DateTimeOffset ParseRequiredTimestamp(JsonElement element)
    {
        if (!DateTimeOffset.TryParse(element.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value))
            throw new FormatException();
        return value;
    }

    private static DateTimeOffset? ParseOptionalTimestamp(JsonElement element)
    {
        var text = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        return string.IsNullOrEmpty(text) ? null : ParseRequiredTimestamp(element);
    }

    private static byte[] CopyUnescapedAscii(ref Utf8JsonReader reader, int maximumLength)
    {
        if (reader.TokenType != JsonTokenType.String || reader.ValueIsEscaped || reader.HasValueSequence ||
            reader.ValueSpan.IsEmpty || reader.ValueSpan.Length > maximumLength ||
            !IsAscii(reader.ValueSpan))
            throw new JsonException();
        return reader.ValueSpan.ToArray();
    }

    internal static string? SafeAccountLabel(string? value)
    {
        var label = value?.Trim();
        return string.IsNullOrEmpty(label) || label.Length > 80 || label.Contains('@') ||
            label.Any(char.IsControl)
            ? null
            : label;
    }

    private static bool ValidRedirect(Uri uri) =>
        uri is { IsAbsoluteUri: true, Scheme: "http", Host: "127.0.0.1" } &&
        uri.Port is >= 1024 and <= 65535 && uri.AbsolutePath == "/my-orbit/callback" &&
        string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) && string.IsNullOrEmpty(uri.UserInfo);

    private static bool ValidState(string value) =>
        value.Length is >= 32 and <= 128 && value.All(IsUnreserved);

    private static bool ValidChallenge(string value) => value.Length == 43 && value.All(IsBase64Url);

    private static bool ValidDeviceName(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 80 && !value.Any(char.IsControl);

    private static bool ValidAuthorizationCode(ReadOnlySpan<byte> value) => ValidPrefixedToken(value, "moac_"u8);
    private static bool ValidAccessToken(ReadOnlySpan<byte> value) => ValidPrefixedToken(value, "moat_"u8);
    private static bool ValidRefreshToken(ReadOnlySpan<byte> value) => ValidPrefixedToken(value, "mort_"u8);
    private static bool ValidVerifier(ReadOnlySpan<byte> value)
    {
        if (value.Length is < 43 or > 128) return false;
        foreach (var character in value)
        {
            if (!(char.IsAsciiLetterOrDigit((char)character) ||
                character is (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~'))
                return false;
        }
        return true;
    }

    private static bool ValidPrefixedToken(ReadOnlySpan<byte> value, ReadOnlySpan<byte> prefix)
    {
        if (value.Length != prefix.Length + 43 || !value.StartsWith(prefix)) return false;
        foreach (var character in value[prefix.Length..])
        {
            if (!(char.IsAsciiLetterOrDigit((char)character) || character is (byte)'-' or (byte)'_'))
                return false;
        }
        return true;
    }

    private static bool IsAscii(ReadOnlySpan<byte> value)
    {
        foreach (var character in value)
        {
            if (character > 0x7f) return false;
        }
        return true;
    }

    private static bool IsUnreserved(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_' or '~';
    private static bool IsBase64Url(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '_';

    private static ControllerResult<T> Invalid<T>() where T : class =>
        ControllerResult<T>.Failure(InvalidError());

    private static ControllerError InvalidError() => ControllerError.Create(
        ControllerErrorCode.InvalidRequest,
        "account.link.request-invalid");

    private static ControllerResult<T> Integrity<T>() where T : class =>
        ControllerResult<T>.Failure(ControllerError.Create(
            ControllerErrorCode.IntegrityFailure,
            "account.link.provider-response-invalid"));

    private static ControllerResult<T> ProviderFailure<T>(HttpStatusCode status) where T : class =>
        ControllerResult<T>.Failure(ProviderError(status));

    private static ControllerError ProviderError(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ControllerError.Create(
            ControllerErrorCode.Expired,
            "account.link.reauthorization-required"),
        HttpStatusCode.TooManyRequests => ControllerError.Create(
            ControllerErrorCode.Unavailable,
            "account.link.rate-limited",
            isRetryable: true),
        _ => ControllerError.Create(
            ControllerErrorCode.Unavailable,
            "account.link.provider-unavailable",
            isRetryable: true),
    };

    private sealed class OwnedResponse : IDisposable
    {
        public OwnedResponse(HttpStatusCode statusCode, byte[] payload)
        {
            StatusCode = statusCode;
            Payload = payload;
        }
        public HttpStatusCode StatusCode { get; }
        public byte[] Payload { get; }
        public void Dispose() => CryptographicOperations.ZeroMemory(Payload);
    }

    private sealed class ZeroingFormContent : ByteArrayContent
    {
        private byte[]? _owned;

        private ZeroingFormContent(byte[] bytes) : base(bytes)
        {
            _owned = bytes;
            Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        }

        public static ZeroingFormContent Create(params (string Name, string Value)[] values) =>
            new(Encode(values, []));

        public static ZeroingFormContent CreateSensitive(
            (string Name, string Value)[] values,
            (string Name, ReadOnlyMemory<byte> Value)[] sensitive) =>
            new(Encode(values, sensitive));

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (_owned is null) return;
            CryptographicOperations.ZeroMemory(_owned);
            _owned = null;
        }

        private static byte[] Encode(
            (string Name, string Value)[] values,
            (string Name, ReadOnlyMemory<byte> Value)[] sensitive)
        {
            var parts = new List<byte[]>();
            try
            {
                foreach (var (name, value) in values)
                    parts.Add(Encoding.ASCII.GetBytes(Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(value)));
                foreach (var (name, value) in sensitive)
                {
                    var nameBytes = Encoding.ASCII.GetBytes(Uri.EscapeDataString(name));
                    var part = new byte[nameBytes.Length + 1 + value.Length];
                    nameBytes.CopyTo(part, 0);
                    part[nameBytes.Length] = (byte)'=';
                    value.Span.CopyTo(part.AsSpan(nameBytes.Length + 1));
                    CryptographicOperations.ZeroMemory(nameBytes);
                    parts.Add(part);
                }
                var length = parts.Sum(part => part.Length) + Math.Max(0, parts.Count - 1);
                var result = new byte[length];
                var offset = 0;
                for (var index = 0; index < parts.Count; index++)
                {
                    if (index > 0) result[offset++] = (byte)'&';
                    parts[index].CopyTo(result, offset);
                    offset += parts[index].Length;
                }
                return result;
            }
            finally
            {
                foreach (var part in parts) CryptographicOperations.ZeroMemory(part);
            }
        }
    }
}
