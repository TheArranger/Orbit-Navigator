using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;

namespace OrbitNavigator.Sync.Accounts;

/// <summary>
/// Separate, uncomposed tabs/history authorization candidate. It requests exactly
/// the four v1 scopes and never reads or writes the account-link credential vault.
/// Successful protocol authorization alone does not enable a sync session: the
/// host still requires explicit consent, device registration, protected keyset,
/// profile binding, and the authenticated apply/scheduling composition gates.
/// </summary>
internal sealed class MyOrbitSyncConsentAuthorizationProtocol : IDisposable
{
    private readonly MyOrbitAuthorizationProtocol _protocol;

    public MyOrbitSyncConsentAuthorizationProtocol(
        MyOrbitAccountProviderOptions options,
        IClock clock,
        HttpMessageHandler? handler = null)
    {
        _protocol = MyOrbitAuthorizationProtocol.ForTabsHistorySyncConsent(options, clock, handler);
    }

    public ValueTask<ControllerResult<MyOrbitPushedAuthorization>> PushAuthorizationAsync(
        MyOrbitPushedAuthorizationRequest request,
        CancellationToken cancellationToken) =>
        _protocol.PushAuthorizationAsync(request, cancellationToken);

    public async ValueTask<ControllerResult<MyOrbitSyncConsentTokens>> ExchangeCodeAsync(
        MyOrbitCodeExchangeRequest request,
        CancellationToken cancellationToken) =>
        Isolate(await _protocol.ExchangeCodeAsync(request, cancellationToken).ConfigureAwait(false));

    public async ValueTask<ControllerResult<MyOrbitSyncConsentTokens>> RefreshAsync(
        SensitiveUtf8Buffer refreshCredential,
        CancellationToken cancellationToken) =>
        Isolate(await _protocol.RefreshAsync(refreshCredential, cancellationToken).ConfigureAwait(false));

    public ValueTask<ControllerResult> RevokeAsync(
        SensitiveUtf8Buffer credential,
        CancellationToken cancellationToken) =>
        _protocol.RevokeAsync(credential, cancellationToken);

    private static ControllerResult<MyOrbitSyncConsentTokens> Isolate(
        ControllerResult<MyOrbitTokenSet> result) =>
        result.IsSuccess
            ? ControllerResult<MyOrbitSyncConsentTokens>.Success(new(result.Value!))
            : ControllerResult<MyOrbitSyncConsentTokens>.Failure(result.Error!);

    public void Dispose() => _protocol.Dispose();
}

/// <summary>
/// Distinct token ownership type deliberately not accepted by IMyOrbitCredentialVault.
/// Never expose the contained protocol token set to the account-link lifecycle.
/// </summary>
internal sealed class MyOrbitSyncConsentTokens : IDisposable
{
    private readonly MyOrbitTokenSet _tokens;

    internal MyOrbitSyncConsentTokens(MyOrbitTokenSet tokens) => _tokens = tokens;

    public SensitiveUtf8Buffer AccessCredential => _tokens.AccessCredential;
    public SensitiveUtf8Buffer RefreshCredential => _tokens.RefreshCredential;
    public DeviceId ConnectionId => _tokens.ConnectionId;
    public string? AccountLabel => _tokens.AccountLabel;
    public DateTimeOffset AccessExpiresAtUtc => _tokens.AccessExpiresAtUtc;
    public DateTimeOffset RefreshExpiresAtUtc => _tokens.RefreshExpiresAtUtc;
    public void Dispose() => _tokens.Dispose();
}
