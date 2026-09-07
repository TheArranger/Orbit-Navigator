using System.Security.Cryptography;
using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.Sync.Accounts;

public sealed class MyOrbitAccountProviderOptions
{
    private MyOrbitAccountProviderOptions(Uri authority)
    {
        Authority = authority;
    }

    public Uri Authority { get; }

    public static ControllerResult<MyOrbitAccountProviderOptions> Create(Uri authority)
    {
        if (authority is null ||
            !authority.IsAbsoluteUri ||
            authority.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(authority.UserInfo) ||
            !string.IsNullOrEmpty(authority.Query) ||
            !string.IsNullOrEmpty(authority.Fragment) ||
            authority.AbsolutePath != "/")
        {
            return ControllerResult<MyOrbitAccountProviderOptions>.Failure(
                ControllerError.Create(
                    ControllerErrorCode.InvalidRequest,
                    "account.link.provider-invalid"));
        }

        return ControllerResult<MyOrbitAccountProviderOptions>.Success(new(authority));
    }
}

/// <summary>
/// Host adapter that invokes the Windows system-default browser. The URI is a
/// short-lived PAR request location and must never be returned to Presentation.
/// </summary>
public interface IMyOrbitSystemBrowserLauncher
{
    ValueTask<ControllerResult> LaunchAsync(
        Uri authorizationRequest,
        CancellationToken cancellationToken = default);
}

internal interface IMyOrbitAuthorizationProtocol : IDisposable
{
    ValueTask<ControllerResult<MyOrbitPushedAuthorization>> PushAuthorizationAsync(
        MyOrbitPushedAuthorizationRequest request,
        CancellationToken cancellationToken);

    ValueTask<ControllerResult<MyOrbitTokenSet>> ExchangeCodeAsync(
        MyOrbitCodeExchangeRequest request,
        CancellationToken cancellationToken);

    ValueTask<ControllerResult<MyOrbitTokenSet>> RefreshAsync(
        SensitiveUtf8Buffer refreshCredential,
        CancellationToken cancellationToken);

    ValueTask<ControllerResult> RevokeAsync(
        SensitiveUtf8Buffer credential,
        CancellationToken cancellationToken);

    ValueTask<ControllerResult<MyOrbitProtocolDeviceList>> ListDevicesAsync(
        SensitiveUtf8Buffer accessCredential,
        CancellationToken cancellationToken);

    ValueTask<ControllerResult> RevokeDeviceAsync(
        SensitiveUtf8Buffer accessCredential,
        DeviceId deviceId,
        CancellationToken cancellationToken);
}

internal interface IMyOrbitCredentialVault
{
    ValueTask<ControllerResult<StoredMyOrbitCredential>> LoadAsync(
        OrbitNavigator.Contracts.Sync.SyncOperationContext context,
        CancellationToken cancellationToken);

    ValueTask<ControllerResult<StoredMyOrbitCredential>> SaveAsync(
        OrbitNavigator.Contracts.Sync.SyncOperationContext context,
        StoredMyOrbitCredential? expected,
        MyOrbitTokenSet tokens,
        bool revocationPending,
        CancellationToken cancellationToken);

    ValueTask<ControllerResult<StoredMyOrbitCredential>> MarkRevocationPendingAsync(
        OrbitNavigator.Contracts.Sync.SyncOperationContext context,
        StoredMyOrbitCredential expected,
        CancellationToken cancellationToken);

    ValueTask<ControllerResult> DeleteAsync(
        OrbitNavigator.Contracts.Sync.SyncOperationContext context,
        StoredMyOrbitCredential expected,
        CancellationToken cancellationToken);
}

internal interface IMyOrbitLoopbackListener : IAsyncDisposable
{
    Uri RedirectUri { get; }

    ValueTask<ControllerResult<MyOrbitLoopbackCallback>> ReceiveAsync(
        CancellationToken cancellationToken);
}

internal interface IMyOrbitLoopbackListenerFactory
{
    ControllerResult<IMyOrbitLoopbackListener> Bind();
}

internal sealed record MyOrbitPushedAuthorizationRequest(
    Uri RedirectUri,
    string State,
    string CodeChallenge,
    string DeviceDisplayName);

internal sealed record MyOrbitPushedAuthorization(
    Uri AuthorizationUri,
    DateTimeOffset ExpiresAtUtc);

internal sealed record MyOrbitCodeExchangeRequest(
    Uri RedirectUri,
    SensitiveUtf8Buffer AuthorizationCode,
    SensitiveUtf8Buffer CodeVerifier);

internal sealed record MyOrbitLoopbackCallback(
    string State,
    SensitiveUtf8Buffer? AuthorizationCode,
    string? Error);

internal sealed record MyOrbitProtocolDevice(
    DeviceId DeviceId,
    string DisplayName,
    bool IsCurrent,
    DateTimeOffset RegisteredAtUtc,
    DateTimeOffset? LastSeenAtUtc,
    DateTimeOffset? RevokedAtUtc);

internal sealed record MyOrbitProtocolDeviceList(
    string? AccountLabel,
    IReadOnlyList<MyOrbitProtocolDevice> Devices);

internal sealed class MyOrbitTokenSet : IDisposable
{
    public MyOrbitTokenSet(
        SensitiveUtf8Buffer accessCredential,
        SensitiveUtf8Buffer refreshCredential,
        DeviceId connectionId,
        string? accountLabel,
        DateTimeOffset accessExpiresAtUtc,
        DateTimeOffset refreshExpiresAtUtc,
        DateTimeOffset issuedAtUtc)
    {
        AccessCredential = accessCredential;
        RefreshCredential = refreshCredential;
        ConnectionId = connectionId;
        AccountLabel = accountLabel;
        AccessExpiresAtUtc = accessExpiresAtUtc;
        RefreshExpiresAtUtc = refreshExpiresAtUtc;
        IssuedAtUtc = issuedAtUtc;
    }

    public SensitiveUtf8Buffer AccessCredential { get; }
    public SensitiveUtf8Buffer RefreshCredential { get; }
    public DeviceId ConnectionId { get; }
    public string? AccountLabel { get; }
    public DateTimeOffset AccessExpiresAtUtc { get; }
    public DateTimeOffset RefreshExpiresAtUtc { get; }
    public DateTimeOffset IssuedAtUtc { get; }

    public void Dispose()
    {
        AccessCredential.Dispose();
        RefreshCredential.Dispose();
    }
}

internal sealed class StoredMyOrbitCredential : IDisposable
{
    public StoredMyOrbitCredential(
        SensitiveUtf8Buffer refreshCredential,
        DeviceId connectionId,
        string? accountLabel,
        DateTimeOffset authorizedAtUtc,
        DateTimeOffset lastRefreshedAtUtc,
        DateTimeOffset refreshExpiresAtUtc,
        bool revocationPending,
        OrbitNavigator.Contracts.Infrastructure.ProfileStorageRevision storageRevision)
    {
        RefreshCredential = refreshCredential;
        ConnectionId = connectionId;
        AccountLabel = accountLabel;
        AuthorizedAtUtc = authorizedAtUtc;
        LastRefreshedAtUtc = lastRefreshedAtUtc;
        RefreshExpiresAtUtc = refreshExpiresAtUtc;
        RevocationPending = revocationPending;
        StorageRevision = storageRevision;
    }

    public SensitiveUtf8Buffer RefreshCredential { get; }
    public DeviceId ConnectionId { get; }
    public string? AccountLabel { get; }
    public DateTimeOffset AuthorizedAtUtc { get; }
    public DateTimeOffset LastRefreshedAtUtc { get; }
    public DateTimeOffset RefreshExpiresAtUtc { get; }
    public bool RevocationPending { get; }
    public OrbitNavigator.Contracts.Infrastructure.ProfileStorageRevision StorageRevision { get; }

    public void Dispose() => RefreshCredential.Dispose();
}

internal sealed class SensitiveUtf8Buffer : IDisposable
{
    private byte[]? _bytes;

    public SensitiveUtf8Buffer(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            throw new ArgumentException("Sensitive values cannot be empty.", nameof(bytes));
        _bytes = bytes.ToArray();
    }

    public ReadOnlyMemory<byte> Bytes => _bytes ?? ReadOnlyMemory<byte>.Empty;

    public SensitiveUtf8Buffer Clone() => new(Bytes.Span);

    public void Dispose()
    {
        if (_bytes is null)
            return;
        CryptographicOperations.ZeroMemory(_bytes);
        _bytes = null;
    }
}
