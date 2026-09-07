using System.Net;
using System.Net.Http.Headers;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Updates;

namespace OrbitNavigator.Updates;

public sealed class HttpSignedUpdateManifestSource : IUpdateManifestSource
{
    private readonly HttpClient _httpClient;
    private readonly UpdateFeedTrustPolicy _policy;
    private readonly SignedUpdateManifestVerifier _verifier;

    public HttpSignedUpdateManifestSource(
        HttpClient httpClient,
        UpdateFeedTrustPolicy policy,
        SignedUpdateManifestVerifier verifier)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
    }

    public async ValueTask<ControllerResult<UpdatePackageInfo>> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _policy.ManifestUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return Failure(ControllerErrorCode.Unavailable, "error.update.feed_unavailable", true);
            }

            if (response.Content.Headers.ContentLength is < 1 ||
                response.Content.Headers.ContentLength > _policy.MaximumManifestBytes)
            {
                return Failure(ControllerErrorCode.IntegrityFailure, "error.update.manifest_size_invalid");
            }

            await using var content = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var limited = new byte[8192];
            while (true)
            {
                var read = await content.ReadAsync(limited, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > _policy.MaximumManifestBytes)
                {
                    return Failure(
                        ControllerErrorCode.IntegrityFailure,
                        "error.update.manifest_size_invalid");
                }

                buffer.Write(limited, 0, read);
            }

            var verified = _verifier.Verify(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)));
            return verified.IsSuccess
                ? ControllerResult<UpdatePackageInfo>.Success(verified.Value!.Package)
                : ControllerResult<UpdatePackageInfo>.Failure(verified.Error!);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(ControllerErrorCode.Unavailable, "error.update.feed_timeout", true);
        }
        catch (HttpRequestException)
        {
            return Failure(ControllerErrorCode.Unavailable, "error.update.feed_unavailable", true);
        }
    }

    public static HttpClient CreatePrivacyPreservingClient(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = 2,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = timeout };
    }

    private static ControllerResult<UpdatePackageInfo> Failure(
        ControllerErrorCode code,
        string messageKey,
        bool retryable = false) =>
        ControllerResult<UpdatePackageInfo>.Failure(ControllerError.Create(
            code,
            messageKey,
            isRetryable: retryable));
}
