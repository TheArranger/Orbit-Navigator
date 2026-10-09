using System.Net;
using System.Text;
using System.Text.Json;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Sync.Accounts;
using Xunit;

namespace OrbitNavigator.Sync.Tests.Accounts;

public sealed class MyOrbitSyncConsentAuthorizationProtocolTests
{
    [Fact]
    public async Task SeparateConsentRequestsExactlyFourScopesWithoutBrowserCredentials()
    {
        var handler = new RecordingHandler(HttpStatusCode.Created, new
        {
            request_uri = "urn:ietf:params:oauth:request_uri:mopr_" + new string('A', 43),
            expires_in = 600,
        });
        using var protocol = Create(handler);
        var result = await protocol.PushAuthorizationAsync(new(
            new Uri("http://127.0.0.1:49152/my-orbit/callback"),
            new string('s', 43), new string('c', 43), "Sync fixture"), CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Contains("scope=" + Uri.EscapeDataString(MyOrbitAuthorizationProtocol.TabsHistorySyncScope),
            handler.Body.Replace("+", "%20", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("orbit.sync.settings", handler.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Cookie", handler.HeaderNames);
        Assert.DoesNotContain("Origin", handler.HeaderNames);
        Assert.DoesNotContain("Referer", handler.HeaderNames);
    }

    [Fact]
    public async Task SyncTokenTypeIsDistinctAndDisposalZerosBothCredentials()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, Tokens(MyOrbitAuthorizationProtocol.TabsHistorySyncScope));
        using var protocol = Create(handler);
        using var code = Secret("moac_");
        using var verifier = new SensitiveUtf8Buffer(Encoding.ASCII.GetBytes(new string('v', 64)));
        var result = await protocol.ExchangeCodeAsync(new(
            new Uri("http://127.0.0.1:49152/my-orbit/callback"), code, verifier), CancellationToken.None);
        Assert.True(result.IsSuccess);
        var tokens = result.Value!;
        var access = tokens.AccessCredential.Bytes;
        var refresh = tokens.RefreshCredential.Bytes;
        tokens.Dispose();
        Assert.All(access.ToArray(), value => Assert.Equal(0, value));
        Assert.All(refresh.ToArray(), value => Assert.Equal(0, value));
        Assert.True(tokens.AccessCredential.Bytes.IsEmpty);
        Assert.True(tokens.RefreshCredential.Bytes.IsEmpty);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("orbit.navigator.link")]
    [InlineData("orbit.navigator.link orbit.sync.history")]
    [InlineData("orbit.navigator.link orbit.sync.history orbit.sync.open_tabs orbit.sync.devices orbit.sync.settings")]
    [InlineData("orbit.navigator.link orbit.sync.history orbit.sync.open_tabs orbit.sync.devices orbit.sync.devices")]
    [InlineData("orbit.navigator.link orbit.sync.history orbit.sync.open_tabs orbit.sync.open_tabs")]
    [InlineData("orbit.navigator.link orbit.sync.history orbit.sync.open_tabs ORBIT.SYNC.DEVICES")]
    [InlineData("orbit.navigator.link  orbit.sync.history orbit.sync.open_tabs orbit.sync.devices")]
    [InlineData(" orbit.navigator.link orbit.sync.history orbit.sync.open_tabs orbit.sync.devices")]
    [InlineData("orbit.navigator.link orbit.sync.history orbit.sync.open_tabs orbit.sync.devices ")]
    [InlineData("orbit.navigator.link\torbit.sync.history orbit.sync.open_tabs orbit.sync.devices")]
    [InlineData("orbit.navigator.link\u00a0orbit.sync.history orbit.sync.open_tabs orbit.sync.devices")]
    public async Task ReducedExpandedOrDuplicatedScopesFailClosed(string? scope)
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, Tokens(scope));
        using var protocol = Create(handler);
        using var refresh = Secret("mort_");
        var result = await protocol.RefreshAsync(refresh, CancellationToken.None);
        Assert.Equal(ControllerErrorCode.IntegrityFailure, result.Error?.Code);
        Assert.Null(result.Value);
    }

    [Theory]
    [MemberData(nameof(EverySyncScopeOrdering))]
    public async Task CodeExchangeAndRefreshAcceptEveryOrderingOfTheExactUniqueGrant(string scope)
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, Tokens(scope));
        using var protocol = Create(handler);
        using var code = Secret("moac_");
        using var verifier = new SensitiveUtf8Buffer(Encoding.ASCII.GetBytes(new string('v', 64)));
        var exchanged = await protocol.ExchangeCodeAsync(new(
            new Uri("http://127.0.0.1:49152/my-orbit/callback"), code, verifier), CancellationToken.None);
        Assert.True(exchanged.IsSuccess);
        using var issued = exchanged.Value!;
        var refreshed = await protocol.RefreshAsync(issued.RefreshCredential, CancellationToken.None);
        Assert.True(refreshed.IsSuccess);
        using var rotated = refreshed.Value!;
    }

    public static IEnumerable<object[]> EverySyncScopeOrdering()
    {
        var values = new[] { "orbit.navigator.link", "orbit.sync.history", "orbit.sync.open_tabs", "orbit.sync.devices" };
        foreach (var first in values)
        foreach (var second in values.Where(value => value != first))
        foreach (var third in values.Where(value => value != first && value != second))
        {
            var fourth = values.Single(value => value != first && value != second && value != third);
            yield return new object[] { string.Join(' ', first, second, third, fourth) };
        }
    }

    [Fact]
    public async Task ExistingLinkProtocolStillRejectsFullSyncScope()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, Tokens(MyOrbitAuthorizationProtocol.TabsHistorySyncScope));
        using var link = new MyOrbitAuthorizationProtocol(Options(), new TestClock(), handler);
        using var refresh = Secret("mort_");
        Assert.Equal(ControllerErrorCode.IntegrityFailure,
            (await link.RefreshAsync(refresh, CancellationToken.None)).Error?.Code);
    }

    [Fact]
    public async Task RefreshReuseOrRevocationRequiresFreshAuthorizationNotOfflineRetry()
    {
        var handler = new RecordingHandler(HttpStatusCode.BadRequest,
            new { error = "invalid_grant", error_description = "Fixture revoked connection." });
        using var sync = Create(handler);
        using var refresh = Secret("mort_");
        var result = await sync.RefreshAsync(refresh, CancellationToken.None);
        Assert.Equal(ControllerErrorCode.Expired, result.Error?.Code);
        Assert.False(result.Error?.IsRetryable);
    }

    [Theory]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("ABCDEFABCDEFABCDEFABCDEFABCDEFAB")]
    public async Task EmptyOrNoncanonicalConnectionIdentityFailsClosed(string connection)
    {
        var handler = new RecordingHandler(HttpStatusCode.OK,
            Tokens(MyOrbitAuthorizationProtocol.TabsHistorySyncScope, connection));
        using var sync = Create(handler);
        using var refresh = Secret("mort_");
        Assert.Equal(ControllerErrorCode.IntegrityFailure,
            (await sync.RefreshAsync(refresh, CancellationToken.None)).Error?.Code);
    }

    private static MyOrbitAccountProviderOptions Options() =>
        MyOrbitAccountProviderOptions.Create(new Uri("https://my-orbit.example/")).Value!;
    private static MyOrbitSyncConsentAuthorizationProtocol Create(HttpMessageHandler handler) =>
        new(Options(), new TestClock(), handler);
    private static SensitiveUtf8Buffer Secret(string prefix) =>
        new(Encoding.ASCII.GetBytes(prefix + new string('A', 43)));
    private static object Tokens(string? scope, string connection = "11111111111111111111111111111111") => new
    {
        token_type = "Bearer", access_token = "moat_" + new string('A', 43),
        refresh_token = "mort_" + new string('A', 43), expires_in = 600,
        refresh_expires_at = "2026-12-15T12:00:00Z", scope, connection_id = connection,
        account_label = "disposable-fixture",
    };
    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    }
    private sealed class RecordingHandler(HttpStatusCode status, object payload) : HttpMessageHandler
    {
        public string Body { get; private set; } = "";
        public string[] HeaderNames { get; private set; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(token);
            HeaderNames = request.Headers.Select(header => header.Key).ToArray();
            return new(status) { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };
        }
    }
}
