using System.Net;
using System.Text;
using System.Text.Json;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Privacy;
using OrbitNavigator.Contracts.Sync;
using OrbitNavigator.Sync.Accounts;
using OrbitNavigator.Sync.Transport;
using Xunit;

namespace OrbitNavigator.Sync.Tests.Transport;

public sealed class MyOrbitSyncBootstrapTransportTests
{
    [Fact]
    public async Task DeviceRegistrationUsesProviderGeneratedIdentityAndExactCookieFreeShape()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, new
        {
            device_id = Device.Value.ToString("N"),
            display_name = "Windows PC",
            registered_at = Timestamp,
            fence = FenceJson(),
        }));
        var resolver = new CredentialResolver();
        using var registry = Registry(handler, resolver);

        var result = await registry.RegisterAsync(
            Context(),
            resolver.Handle,
            new SyncDeviceRegistrationRequest("  Windows   PC  ", Enumerable.Repeat((byte)7, 32).ToArray()),
            default);

        Assert.True(result.IsSuccess);
        Assert.Equal(Device, result.Value!.DeviceId);
        Assert.Equal(Fence, result.Value.Fence);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/navigator-sync/v1/devices/register", request.Uri.AbsolutePath);
        Assert.Equal("Bearer", request.AuthorizationScheme);
        Assert.DoesNotContain("Cookie", request.HeaderNames);
        Assert.DoesNotContain("Origin", request.HeaderNames);
        Assert.DoesNotContain("Referer", request.HeaderNames);
        using var json = JsonDocument.Parse(request.Body);
        Assert.Equal(
            ["display_name", "public_identity_key"],
            json.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal("Windows PC", json.RootElement.GetProperty("display_name").GetString());
        Assert.Equal(Base64Url(Enumerable.Repeat((byte)7, 32).ToArray()),
            json.RootElement.GetProperty("public_identity_key").GetString());
    }

    [Fact]
    public async Task InvalidRegistrationNeverResolvesCredentialOrCallsNetwork()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("network must not run"));
        var resolver = new CredentialResolver();
        using var registry = Registry(handler, resolver);

        var result = await registry.RegisterAsync(
            Context(), resolver.Handle, new SyncDeviceRegistrationRequest("Windows PC", new byte[31]), default);

        Assert.Equal(ControllerErrorCode.InvalidRequest, result.Error?.Code);
        Assert.Equal(0, resolver.ResolveCalls);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SyncDeviceRevocationIsExplicitlyAccountOwnedAndNeverCallsNetwork()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("network must not run"));
        var resolver = new CredentialResolver();
        using var registry = Registry(handler, resolver);

        var result = await registry.RevokeAsync(
            Context(), resolver.Handle, null!, Device, default);

        Assert.Equal(ControllerErrorCode.NotSupported, result.Error?.Code);
        Assert.Equal("sync.device.revoke-through-account", result.Error?.MessageKey);
        Assert.Equal(0, resolver.ResolveCalls);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RecoveryUploadSerializesOnlyOpaqueWrappedEnvelopeAndValidatesBoundReceipt()
    {
        var wrapped = Wrapped();
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, new
        {
            profile_id = Profile.Value.ToString("N"),
            keyset_id = Keyset.Value.ToString("N"),
            key_epoch = 0,
            envelope_revision = 1,
            changed = true,
            stored_at = Timestamp,
        }));
        var resolver = new CredentialResolver();
        using var transport = Recovery(handler, resolver);

        var result = await transport.UploadAsync(
            Context(),
            resolver.Handle,
            new MyOrbitRecoveryEnvelopeUpload(Device, Fence, Profile, 1, 0, wrapped),
            default);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.Changed);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("/navigator-sync/v1/keysets/recovery-envelope", request.Uri.AbsolutePath);
        Assert.DoesNotContain("recovery code", request.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("root key", request.Body, StringComparison.OrdinalIgnoreCase);
        using var json = JsonDocument.Parse(request.Body);
        Assert.Equal(Profile.Value.ToString("N"), json.RootElement.GetProperty("profile_id").GetString());
        var envelope = json.RootElement.GetProperty("wrapped_keyset");
        Assert.Equal("recovery_code", envelope.GetProperty("wrap_method").GetString());
        Assert.Equal(Base64Url(wrapped.WrappedKeyCiphertext.Span),
            envelope.GetProperty("wrapped_key_ciphertext").GetString());
    }

    [Fact]
    public async Task RecoveryFetchReturnsAccountSyncProfileWithoutSilentlyAdoptingIt()
    {
        var accountSyncProfile = new ProfileId(Guid.ParseExact("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "N"));
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, new
        {
            profile_id = accountSyncProfile.Value.ToString("N"),
            envelope_revision = 3,
            wrapped_keyset = WrappedJson(),
        }));
        var resolver = new CredentialResolver();
        using var transport = Recovery(handler, resolver);

        var result = await transport.FetchAsync(Context(), resolver.Handle, Device, Fence, default);

        Assert.True(result.IsSuccess);
        Assert.Equal(accountSyncProfile, result.Value!.ProfileId);
        Assert.Equal(3, result.Value.EnvelopeRevision);
        Assert.Equal(Keyset, result.Value.WrappedKeyset.KeysetId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Contains("device_id=" + Device.Value.ToString("N"), request.Uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("Cookie", request.HeaderNames);
    }

    [Fact]
    public async Task RecoveryRejectsMalformedShapeRedirectCookieAndContradictoryReceipt()
    {
        var responses = new Func<HttpRequestMessage, HttpResponseMessage>[]
        {
            _ => Json(HttpStatusCode.OK, new
            {
                profile_id = Profile.Value.ToString("N"),
                envelope_revision = 1,
                wrapped_keyset = WrappedJson(),
                unexpected = true,
            }),
            _ => new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://other.example/") },
            },
            _ => CookieResponse(),
            _ => Json(HttpStatusCode.OK, new
            {
                profile_id = Guid.NewGuid().ToString("N"),
                keyset_id = Keyset.Value.ToString("N"),
                key_epoch = 0,
                envelope_revision = 1,
                changed = true,
                stored_at = Timestamp,
            }),
        };

        foreach (var response in responses.Take(3))
        {
            var resolver = new CredentialResolver();
            var handler = new RecordingHandler(response);
            using var transport = Recovery(handler, resolver);
            var fetched = await transport.FetchAsync(Context(), resolver.Handle, Device, Fence, default);
            Assert.Equal(ControllerErrorCode.IntegrityFailure, fetched.Error?.Code);
        }

        {
            var resolver = new CredentialResolver();
            var handler = new RecordingHandler(responses[3]);
            using var transport = Recovery(handler, resolver);
            var uploaded = await transport.UploadAsync(
                Context(), resolver.Handle,
                new MyOrbitRecoveryEnvelopeUpload(Device, Fence, Profile, 1, 0, Wrapped()), default);
            Assert.Equal(ControllerErrorCode.IntegrityFailure, uploaded.Error?.Code);
        }
    }

    private static MyOrbitSyncDeviceRegistry Registry(
        HttpMessageHandler handler,
        CredentialResolver resolver) => new(
            Options(), resolver, handler);

    private static MyOrbitRecoveryEnvelopeTransport Recovery(
        HttpMessageHandler handler,
        CredentialResolver resolver) => new(
            Options(), resolver, handler);

    private static MyOrbitAccountProviderOptions Options() =>
        MyOrbitAccountProviderOptions.Create(new Uri("https://my-orbit.example/")).Value!;

    private static SyncOperationContext Context()
    {
        var browsing = new BrowsingContext(
            new PrivacyContext(Profile, new BrowserSessionId(Guid.NewGuid()), BrowserProfileMode.Normal),
            new BrowserWindowId(Guid.NewGuid()),
            new BrowserTabId(Guid.NewGuid()),
            null);
        return SyncOperationContext.Authorize(browsing, new SyncOperationId(Guid.NewGuid())).Value!;
    }

    private static WrappedSyncKeyset Wrapped() => new(
        Keyset,
        0,
        SyncKeyWrapMethod.RecoveryCode,
        new SyncKdfParameters(
            SyncKdfAlgorithm.Pbkdf2Sha256,
            Enumerable.Repeat((byte)1, 32).ToArray(),
            600_000,
            0,
            1,
            32),
        Enumerable.Repeat((byte)2, 12).ToArray(),
        Enumerable.Repeat((byte)3, 32).ToArray(),
        Enumerable.Repeat((byte)4, 16).ToArray());

    private static object WrappedJson()
    {
        var wrapped = Wrapped();
        return new
        {
            keyset_id = wrapped.KeysetId.Value.ToString("N"),
            generation = wrapped.Generation,
            wrap_method = "recovery_code",
            kdf = new
            {
                algorithm = "pbkdf2_sha256",
                salt = Base64Url(wrapped.Kdf.Salt.Span),
                iterations = wrapped.Kdf.Iterations,
                memory_kib = wrapped.Kdf.MemoryKiB,
                parallelism = wrapped.Kdf.Parallelism,
                derived_key_size_bytes = wrapped.Kdf.DerivedKeySizeBytes,
            },
            nonce = Base64Url(wrapped.Nonce.Span),
            wrapped_key_ciphertext = Base64Url(wrapped.WrappedKeyCiphertext.Span),
            authentication_tag = Base64Url(wrapped.AuthenticationTag.Span),
        };
    }

    private static object FenceJson() => new
    {
        device_id = Device.Value.ToString("N"),
        client_generation = 0,
        minimum_accepted_generation = 0,
    };

    private static HttpResponseMessage Json(HttpStatusCode status, object value)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
        };
        response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoStore = true };
        response.Headers.TryAddWithoutValidation("Pragma", "no-cache");
        return response;
    }

    private static HttpResponseMessage CookieResponse()
    {
        var response = Json(HttpStatusCode.OK, new
        {
            profile_id = Profile.Value.ToString("N"),
            envelope_revision = 1,
            wrapped_keyset = WrappedJson(),
        });
        response.Headers.TryAddWithoutValidation("Set-Cookie", "session=forbidden");
        return response;
    }

    private static string Base64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private const string Timestamp = "2026-09-07T12:34:56.123456Z";
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
                request.Method,
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
        HttpMethod Method,
        Uri Uri,
        string Body,
        string? AuthorizationScheme,
        IReadOnlyList<string> HeaderNames);
}
