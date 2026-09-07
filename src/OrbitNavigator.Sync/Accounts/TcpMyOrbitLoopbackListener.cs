using System.Net;
using System.Net.Sockets;
using System.Text;
using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.Sync.Accounts;

internal sealed class TcpMyOrbitLoopbackListenerFactory : IMyOrbitLoopbackListenerFactory
{
    public ControllerResult<IMyOrbitLoopbackListener> Bind()
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(1);
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            if (endpoint.Port < 1024)
            {
                listener.Stop();
                return Failure<IMyOrbitLoopbackListener>("account.link.loopback-unavailable");
            }

            return ControllerResult<IMyOrbitLoopbackListener>.Success(
                new TcpMyOrbitLoopbackListener(listener, endpoint.Port));
        }
        catch (SocketException)
        {
            return Failure<IMyOrbitLoopbackListener>("account.link.loopback-unavailable");
        }
    }

    private static ControllerResult<T> Failure<T>(string messageKey) where T : class =>
        ControllerResult<T>.Failure(ControllerError.Create(
            ControllerErrorCode.Unavailable,
            messageKey,
            isRetryable: true));
}

internal sealed class TcpMyOrbitLoopbackListener : IMyOrbitLoopbackListener
{
    private const int MaximumHeaderBytes = 16 * 1024;
    private static readonly byte[] HeaderTerminator = "\r\n\r\n"u8.ToArray();
    private readonly TcpListener _listener;
    private int _consumed;

    public TcpMyOrbitLoopbackListener(TcpListener listener, int port)
    {
        _listener = listener;
        RedirectUri = new Uri($"http://127.0.0.1:{port}/my-orbit/callback", UriKind.Absolute);
    }

    public Uri RedirectUri { get; }

    public async ValueTask<ControllerResult<MyOrbitLoopbackCallback>> ReceiveAsync(
        CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _consumed, 1) != 0)
            return Failure<MyOrbitLoopbackCallback>(ControllerErrorCode.AlreadyHandled, "account.link.callback-handled");

        try
        {
            using var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            _listener.Stop();
            if (client.Client.RemoteEndPoint is not IPEndPoint remote || !IPAddress.IsLoopback(remote.Address))
                return Failure<MyOrbitLoopbackCallback>(ControllerErrorCode.PolicyDenied, "account.link.callback-origin-invalid");

            await using var stream = client.GetStream();
            var received = await ReadHeadersAsync(stream, cancellationToken).ConfigureAwait(false);
            var parsed = Parse(received);
            await WriteResponseAsync(stream, parsed.IsSuccess, cancellationToken).ConfigureAwait(false);
            return parsed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<MyOrbitLoopbackCallback>(ControllerErrorCode.Cancelled, "account.link.cancelled");
        }
        catch (Exception exception) when (exception is SocketException or IOException)
        {
            return Failure<MyOrbitLoopbackCallback>(ControllerErrorCode.Unavailable, "account.link.callback-unavailable", true);
        }
    }

    public ValueTask DisposeAsync()
    {
        _listener.Stop();
        return ValueTask.CompletedTask;
    }

    private static async Task<byte[]> ReadHeadersAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaximumHeaderBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, buffer.Length - length), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                break;
            length += read;
            if (buffer.AsSpan(0, length).IndexOf(HeaderTerminator) >= 0)
                return buffer.AsSpan(0, length).ToArray();
        }

        throw new IOException("Loopback callback headers exceeded the allowed size.");
    }

    private ControllerResult<MyOrbitLoopbackCallback> Parse(ReadOnlySpan<byte> bytes)
    {
        if (!IsAscii(bytes))
            return Invalid();
        var text = Encoding.ASCII.GetString(bytes);
        var lines = text.Split("\r\n", StringSplitOptions.None);
        if (lines.Length < 3)
            return Invalid();
        var request = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (request.Length != 3 || request[0] != "GET" || request[2] != "HTTP/1.1")
            return Invalid();

        var hostValues = lines.Skip(1)
            .Where(line => line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
            .Select(line => line[5..].Trim())
            .ToArray();
        if (hostValues.Length != 1 ||
            !string.Equals(hostValues[0], RedirectUri.Authority, StringComparison.OrdinalIgnoreCase))
            return Invalid();

        if (!Uri.TryCreate(RedirectUri, request[1], out var callback) ||
            callback.Scheme != Uri.UriSchemeHttp ||
            callback.Host != "127.0.0.1" ||
            callback.Port != RedirectUri.Port ||
            callback.AbsolutePath != "/my-orbit/callback" ||
            !string.IsNullOrEmpty(callback.Fragment))
            return Invalid();

        var values = ParseQuery(callback.Query);
        if (values is null || !values.TryGetValue("state", out var state) || !ValidState(state))
            return Invalid();
        if (values.TryGetValue("code", out var code) && ValidCode(code) && values.Count == 2)
        {
            return ControllerResult<MyOrbitLoopbackCallback>.Success(new(
                state,
                new SensitiveUtf8Buffer(Encoding.ASCII.GetBytes(code)),
                null));
        }
        if (values.TryGetValue("error", out var error) && ValidError(error) && values.Count == 2)
            return ControllerResult<MyOrbitLoopbackCallback>.Success(new(state, null, error));
        return Invalid();
    }

    private static Dictionary<string, string>? ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
                return null;
            var key = Uri.UnescapeDataString(pair[..separator]);
            var value = Uri.UnescapeDataString(pair[(separator + 1)..]);
            if (!result.TryAdd(key, value))
                return null;
        }
        return result;
    }

    private static bool IsAscii(ReadOnlySpan<byte> value)
    {
        foreach (var character in value)
        {
            if (character > 0x7f)
                return false;
        }
        return true;
    }

    private static bool ValidState(string value) =>
        value.Length is >= 32 and <= 128 && value.All(IsUnreserved);

    private static bool ValidCode(string value) =>
        value.StartsWith("moac_", StringComparison.Ordinal) &&
        value.Length == 48 &&
        value[5..].All(IsBase64Url);

    private static bool ValidError(string value) =>
        value.Length is >= 1 and <= 64 && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static bool IsUnreserved(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_' or '~';

    private static bool IsBase64Url(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '_';

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        bool success,
        CancellationToken cancellationToken)
    {
        var message = success
            ? "Orbit Navigator received the response. You may close this tab."
            : "Orbit Navigator could not accept this response. Return to the browser and try again.";
        var body = Encoding.UTF8.GetBytes(
            "<!doctype html><meta charset=utf-8><meta name=referrer content=no-referrer>" +
            "<title>Orbit Navigator</title><p>" + message + "</p>");
        var header = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\n" +
            "Cache-Control: no-store\r\nPragma: no-cache\r\nReferrer-Policy: no-referrer\r\n" +
            "Content-Security-Policy: default-src 'none'; style-src 'none'; img-src 'none'\r\n" +
            $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }

    private static ControllerResult<MyOrbitLoopbackCallback> Invalid() =>
        Failure<MyOrbitLoopbackCallback>(ControllerErrorCode.InvalidRequest, "account.link.callback-invalid");

    private static ControllerResult<T> Failure<T>(
        ControllerErrorCode code,
        string messageKey,
        bool retryable = false) where T : class =>
        ControllerResult<T>.Failure(ControllerError.Create(code, messageKey, isRetryable: retryable));
}
