using System.Net.Sockets;
using System.Text;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Sync.Accounts;
using Xunit;

namespace OrbitNavigator.Sync.Tests.Accounts;

public sealed class TcpMyOrbitLoopbackListenerTests
{
    [Fact]
    public async Task ExactLoopbackCallbackIsSingleUseAndResponseNeverEchoesSecrets()
    {
        var bound = new TcpMyOrbitLoopbackListenerFactory().Bind();
        Assert.True(bound.IsSuccess);
        await using var listener = bound.Value!;
        var state = new string('s', 43);
        var code = "moac_" + new string('A', 43);
        var receive = listener.ReceiveAsync(CancellationToken.None).AsTask();

        var browserResponse = await SendAsync(listener.RedirectUri,
            $"/my-orbit/callback?code={code}&state={state}");
        var callback = await receive;

        Assert.True(callback.IsSuccess);
        using var receivedCode = callback.Value!.AuthorizationCode;
        Assert.Equal(state, callback.Value.State);
        Assert.Equal(code, Encoding.ASCII.GetString(receivedCode!.Bytes.Span));
        Assert.Contains("Cache-Control: no-store", browserResponse, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Content-Security-Policy: default-src 'none'", browserResponse, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(code, browserResponse, StringComparison.Ordinal);
        Assert.DoesNotContain(state, browserResponse, StringComparison.Ordinal);

        var replay = await listener.ReceiveAsync(CancellationToken.None);
        Assert.Equal(ControllerErrorCode.AlreadyHandled, replay.Error?.Code);
    }

    [Fact]
    public async Task DuplicateStateQueryIsRejected()
    {
        var bound = new TcpMyOrbitLoopbackListenerFactory().Bind();
        await using var listener = bound.Value!;
        var receive = listener.ReceiveAsync(CancellationToken.None).AsTask();
        var state = new string('s', 43);
        var code = "moac_" + new string('A', 43);

        _ = await SendAsync(listener.RedirectUri,
            $"/my-orbit/callback?code={code}&state={state}&state={state}");
        var callback = await receive;

        Assert.Equal(ControllerErrorCode.InvalidRequest, callback.Error?.Code);
    }

    private static async Task<string> SendAsync(Uri endpoint, string target)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(endpoint.Host, endpoint.Port);
        await using var stream = client.GetStream();
        var request = Encoding.ASCII.GetBytes(
            $"GET {target} HTTP/1.1\r\nHost: {endpoint.Authority}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }
}
