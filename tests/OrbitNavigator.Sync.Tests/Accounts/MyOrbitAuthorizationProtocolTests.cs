using System.Net;
using System.Text;
using System.Text.Json;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Sync.Accounts;
using Xunit;

namespace OrbitNavigator.Sync.Tests.Accounts;

public sealed class MyOrbitAuthorizationProtocolTests
{
    [Fact]
    public async Task ParRequestsOnlyLinkScopeAndBrowserUriContainsOnlyParHandle()
    {
        var handler = new RecordingHandler(request => Json(
            HttpStatusCode.Created,
            new { request_uri = "urn:ietf:params:oauth:request_uri:mopr_" + new string('A', 43), expires_in = 600 }));
        using var protocol = Protocol(handler);
        var state = new string('s', 43);
        var challenge = new string('c', 43);

        var result = await protocol.PushAuthorizationAsync(new(
            new Uri("http://127.0.0.1:49152/my-orbit/callback"),
            state,
            challenge,
            "Test PC"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var body = Assert.Single(handler.Requests).Body;
        Assert.Contains("scope=orbit.navigator.link", body, StringComparison.Ordinal);
        Assert.DoesNotContain("orbit.sync", body, StringComparison.Ordinal);
        Assert.Contains("code_challenge_method=S256", body, StringComparison.Ordinal);
        Assert.Contains("response_type=code", body, StringComparison.Ordinal);
        Assert.DoesNotContain(state, result.Value!.AuthorizationUri.AbsoluteUri, StringComparison.Ordinal);
        Assert.DoesNotContain(challenge, result.Value.AuthorizationUri.AbsoluteUri, StringComparison.Ordinal);
        Assert.DoesNotContain("redirect_uri", result.Value.AuthorizationUri.Query, StringComparison.Ordinal);
        Assert.Equal("/oauth2/authorize", result.Value.AuthorizationUri.AbsolutePath);
    }

    [Fact]
    public async Task TokenTransportHasNoCookiesOriginsOrReferrersAndAcceptsOnlyLinkScope()
    {
        var handler = new RecordingHandler(request => Json(HttpStatusCode.OK, TokenResponse("orbit.navigator.link")));
        using var protocol = Protocol(handler);
        using var code = new SensitiveUtf8Buffer(Token("moac_"));
        using var verifier = new SensitiveUtf8Buffer(Encoding.ASCII.GetBytes(new string('v', 64)));

        var result = await protocol.ExchangeCodeAsync(new(
            new Uri("http://127.0.0.1:49152/my-orbit/callback"), code, verifier), CancellationToken.None);

        Assert.True(result.IsSuccess);
        using var tokens = result.Value!;
        var request = Assert.Single(handler.Requests);
        Assert.False(request.Headers.ContainsKey("Cookie"));
        Assert.False(request.Headers.ContainsKey("Origin"));
        Assert.False(request.Headers.ContainsKey("Referer"));
        Assert.Contains("grant_type=authorization_code", request.Body, StringComparison.Ordinal);
        Assert.Contains("code_verifier=", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TokenResponseWithAnyBrowsingSyncScopeFailsClosed()
    {
        var handler = new RecordingHandler(request => Json(
            HttpStatusCode.OK,
            TokenResponse("orbit.navigator.link orbit.sync.open_tabs")));
        using var protocol = Protocol(handler);
        using var code = new SensitiveUtf8Buffer(Token("moac_"));
        using var verifier = new SensitiveUtf8Buffer(Encoding.ASCII.GetBytes(new string('v', 64)));

        var result = await protocol.ExchangeCodeAsync(new(
            new Uri("http://127.0.0.1:49152/my-orbit/callback"), code, verifier), CancellationToken.None);

        Assert.Equal(ControllerErrorCode.IntegrityFailure, result.Error?.Code);
    }

    [Fact]
    public async Task DuplicateSecurityCriticalTokenFieldFailsClosed()
    {
        var payload = "{\"token_type\":\"Bearer\",\"scope\":\"orbit.navigator.link\"," +
            "\"scope\":\"orbit.navigator.link\",\"access_token\":\"" +
            Encoding.ASCII.GetString(Token("moat_")) + "\",\"refresh_token\":\"" +
            Encoding.ASCII.GetString(Token("mort_")) + "\",\"expires_in\":600," +
            "\"refresh_expires_at\":\"2026-09-15T12:00:00Z\"," +
            "\"connection_id\":\"11111111111111111111111111111111\"}";
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        });
        using var protocol = Protocol(handler);
        using var code = new SensitiveUtf8Buffer(Token("moac_"));
        using var verifier = new SensitiveUtf8Buffer(Encoding.ASCII.GetBytes(new string('v', 64)));

        var result = await protocol.ExchangeCodeAsync(new(
            new Uri("http://127.0.0.1:49152/my-orbit/callback"), code, verifier), CancellationToken.None);

        Assert.Equal(ControllerErrorCode.IntegrityFailure, result.Error?.Code);
    }

    [Fact]
    public async Task RedirectOrSetCookieOnProtocolEndpointFailsClosed()
    {
        foreach (var response in new[]
        {
            new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://evil.example/") } },
            CookieResponse(),
        })
        {
            var handler = new RecordingHandler(_ => response);
            using var protocol = Protocol(handler);
            var result = await protocol.PushAuthorizationAsync(new(
                new Uri("http://127.0.0.1:49152/my-orbit/callback"),
                new string('s', 43),
                new string('c', 43),
                "Test PC"), CancellationToken.None);
            Assert.Equal(ControllerErrorCode.IntegrityFailure, result.Error?.Code);
        }
    }

    [Theory]
    [InlineData("http://my-orbit.example/")]
    [InlineData("https://user@my-orbit.example/")]
    [InlineData("https://my-orbit.example/path")]
    [InlineData("https://my-orbit.example/?tenant=x")]
    public void ProviderAuthorityMustBeExactHttpsOrigin(string value)
    {
        var result = MyOrbitAccountProviderOptions.Create(new Uri(value));
        Assert.False(result.IsSuccess);
    }

    private static MyOrbitAuthorizationProtocol Protocol(HttpMessageHandler handler) => new(
        MyOrbitAccountProviderOptions.Create(new Uri("https://my-orbit.example/")).Value!,
        new TestClock(),
        handler);

    private static object TokenResponse(string scope) => new
    {
        token_type = "Bearer",
        access_token = Encoding.ASCII.GetString(Token("moat_")),
        expires_in = 600,
        refresh_token = Encoding.ASCII.GetString(Token("mort_")),
        refresh_expires_at = "2026-09-15T12:00:00Z",
        scope,
        connection_id = "11111111111111111111111111111111",
        account_label = "orbit-user",
    };

    private static HttpResponseMessage Json(HttpStatusCode status, object value) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage CookieResponse()
    {
        var response = Json(HttpStatusCode.Created,
            new { request_uri = "urn:ietf:params:oauth:request_uri:mopr_" + new string('A', 43), expires_in = 600 });
        response.Headers.TryAddWithoutValidation("Set-Cookie", "session=forbidden");
        return response;
    }

    private static byte[] Token(string prefix) => Encoding.ASCII.GetBytes(prefix + new string('A', 43));

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var headers = request.Content is null
                ? request.Headers.AsEnumerable()
                : request.Headers.Concat(request.Content.Headers);
            Requests.Add(new(
                request.Method,
                request.RequestUri!,
                headers.ToDictionary(
                    item => item.Key,
                    item => string.Join(",", item.Value),
                    StringComparer.OrdinalIgnoreCase),
                body));
            return response(request);
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        Uri Uri,
        IReadOnlyDictionary<string, string> Headers,
        string Body);
}
