using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Sync;

namespace OrbitNavigator.Contracts.Accounts;

public enum MyOrbitAccountConnectionStateKind
{
    ProviderUnavailable = 0,
    SignedOut = 1,
    LinkPending = 2,
    Connected = 3,
    ReauthorizationRequired = 4,
    Revoked = 5,
    Failed = 6,
    Loading = 7,
}

public enum MyOrbitAccountScope
{
    LinkAccount = 0,
    ManageLinkedDevices = 1,
}

[Flags]
public enum MyOrbitAccountCapabilities
{
    None = 0,
    BeginExternalLink = 1 << 0,
    CancelPendingLink = 1 << 1,
    DisconnectCurrentDevice = 1 << 2,
    QueryDevices = 1 << 3,
    RevokeDevice = 1 << 4,
}

public readonly record struct MyOrbitAccountRevision(long Value)
{
    public static MyOrbitAccountRevision Initial => new(0);

    public bool IsDefined => Value >= 0;
}

public readonly record struct MyOrbitLinkAttemptId(Guid Value)
{
    public bool IsEmpty => Value == Guid.Empty;
}

/// <summary>
/// Presentation-safe state. Protocol locations and credentials are deliberately
/// absent from this boundary.
/// </summary>
public sealed class MyOrbitAccountConnectionState
{
    private static readonly Regex MessageKeyPattern = new(
        "^[a-z0-9]+(?:[._-][a-z0-9]+)*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly ReadOnlyCollection<MyOrbitAccountScope> _grantedScopes;

    public MyOrbitAccountConnectionState(
        PrivacyContext privacy,
        MyOrbitAccountRevision revision,
        MyOrbitAccountConnectionStateKind state,
        string? providerLabel,
        string? accountLabel,
        IEnumerable<MyOrbitAccountScope> grantedScopes,
        string? messageKey,
        DateTimeOffset? authorizedAtUtc,
        DateTimeOffset? lastRefreshedAtUtc,
        DateTimeOffset? credentialExpiresAtUtc,
        MyOrbitAccountCapabilities capabilities,
        bool localBrowsingAvailable = true)
    {
        if (privacy is not { IsStructurallyValid: true } ||
            !revision.IsDefined ||
            !Enum.IsDefined(state) ||
            !SafeLabel(providerLabel, allowNull: true) ||
            !SafeAccountLabel(accountLabel) ||
            (messageKey is not null && !MessageKeyPattern.IsMatch(messageKey)) ||
            (capabilities & ~AllCapabilities) != 0 ||
            !localBrowsingAvailable ||
            lastRefreshedAtUtc < authorizedAtUtc ||
            credentialExpiresAtUtc < lastRefreshedAtUtc)
        {
            throw new ArgumentException("The account state contains an invalid or unsafe field.");
        }
        Privacy = privacy ?? throw new ArgumentNullException(nameof(privacy));
        Revision = revision;
        State = state;
        ProviderLabel = providerLabel;
        AccountLabel = accountLabel;
        var scopes = (grantedScopes ?? throw new ArgumentNullException(nameof(grantedScopes))).Distinct().ToArray();
        if (scopes.Any(scope => !Enum.IsDefined(scope)))
            throw new ArgumentException("Account scopes must be defined values.", nameof(grantedScopes));
        _grantedScopes = new ReadOnlyCollection<MyOrbitAccountScope>(scopes);
        MessageKey = messageKey;
        AuthorizedAtUtc = authorizedAtUtc;
        LastRefreshedAtUtc = lastRefreshedAtUtc;
        CredentialExpiresAtUtc = credentialExpiresAtUtc;
        Capabilities = capabilities;
        LocalBrowsingAvailable = localBrowsingAvailable;
    }

    public PrivacyContext Privacy { get; }

    public ProfileId ProfileId => Privacy.ProfileId;

    public MyOrbitAccountRevision Revision { get; }

    public MyOrbitAccountConnectionStateKind State { get; }

    public string? ProviderLabel { get; }

    public string? AccountLabel { get; }

    public IReadOnlyList<MyOrbitAccountScope> GrantedScopes => _grantedScopes;

    public string? MessageKey { get; }

    public DateTimeOffset? AuthorizedAtUtc { get; }

    public DateTimeOffset? LastRefreshedAtUtc { get; }

    public DateTimeOffset? CredentialExpiresAtUtc { get; }

    public MyOrbitAccountCapabilities Capabilities { get; }

    public bool LocalBrowsingAvailable { get; }

    private static MyOrbitAccountCapabilities AllCapabilities =>
        MyOrbitAccountCapabilities.BeginExternalLink |
        MyOrbitAccountCapabilities.CancelPendingLink |
        MyOrbitAccountCapabilities.DisconnectCurrentDevice |
        MyOrbitAccountCapabilities.QueryDevices |
        MyOrbitAccountCapabilities.RevokeDevice;

    private static bool SafeLabel(string? value, bool allowNull) =>
        value is null ? allowNull :
        !string.IsNullOrWhiteSpace(value) && value.Length <= 80 && !value.Any(char.IsControl);

    private static bool SafeAccountLabel(string? value) =>
        value is null || SafeLabel(value, allowNull: true) && !value.Contains('@');
}

public sealed record MyOrbitAccountQuery(
    BrowsingContext Context,
    SyncOperationId OperationId,
    MyOrbitAccountRevision ExpectedRevision);

public sealed record BeginMyOrbitExternalLinkIntent(
    BrowsingContext Context,
    SyncOperationId OperationId,
    MyOrbitAccountRevision ExpectedRevision,
    string DeviceDisplayName);

public sealed record CancelMyOrbitExternalLinkIntent(
    BrowsingContext Context,
    SyncOperationId OperationId,
    MyOrbitAccountRevision ExpectedRevision,
    MyOrbitLinkAttemptId AttemptId);

public sealed record DisconnectMyOrbitCurrentDeviceIntent(
    BrowsingContext Context,
    SyncOperationId OperationId,
    MyOrbitAccountRevision ExpectedRevision);

public sealed record QueryMyOrbitDevicesIntent(
    BrowsingContext Context,
    SyncOperationId OperationId,
    MyOrbitAccountRevision ExpectedRevision);

public sealed record RevokeMyOrbitDeviceIntent(
    BrowsingContext Context,
    SyncOperationId OperationId,
    MyOrbitAccountRevision ExpectedRevision,
    DeviceId DeviceId);

public sealed record MyOrbitExternalLinkReceipt(
    MyOrbitLinkAttemptId AttemptId,
    DateTimeOffset ExpiresAtUtc,
    MyOrbitAccountConnectionState Status);

public sealed class MyOrbitLinkedDevice
{
    public MyOrbitLinkedDevice(
        DeviceId deviceId,
        string displayName,
        bool isCurrent,
        DateTimeOffset registeredAtUtc,
        DateTimeOffset? lastSeenAtUtc,
        DateTimeOffset? revokedAtUtc)
    {
        if (deviceId.IsEmpty || string.IsNullOrWhiteSpace(displayName) || displayName.Length > 80 ||
            displayName.Any(char.IsControl) || lastSeenAtUtc < registeredAtUtc || revokedAtUtc < registeredAtUtc)
            throw new ArgumentException("The linked-device summary contains an invalid field.");
        DeviceId = deviceId;
        DisplayName = displayName;
        IsCurrent = isCurrent;
        RegisteredAtUtc = registeredAtUtc;
        LastSeenAtUtc = lastSeenAtUtc;
        RevokedAtUtc = revokedAtUtc;
    }

    public DeviceId DeviceId { get; }
    public string DisplayName { get; }
    public bool IsCurrent { get; }
    public DateTimeOffset RegisteredAtUtc { get; }
    public DateTimeOffset? LastSeenAtUtc { get; }
    public DateTimeOffset? RevokedAtUtc { get; }
}

public sealed class MyOrbitDeviceCollection
{
    private readonly ReadOnlyCollection<MyOrbitLinkedDevice> _devices;

    public MyOrbitDeviceCollection(
        MyOrbitAccountConnectionState status,
        IEnumerable<MyOrbitLinkedDevice> devices)
    {
        Status = status ?? throw new ArgumentNullException(nameof(status));
        _devices = new ReadOnlyCollection<MyOrbitLinkedDevice>(
            (devices ?? throw new ArgumentNullException(nameof(devices))).ToArray());
    }

    public MyOrbitAccountConnectionState Status { get; }

    public IReadOnlyList<MyOrbitLinkedDevice> Devices => _devices;
}

public interface IMyOrbitAccountConnectionController
{
    ValueTask<ControllerResult<MyOrbitAccountConnectionState>> QueryAsync(
        MyOrbitAccountQuery query,
        CancellationToken cancellationToken = default);

    ValueTask<ControllerResult<MyOrbitExternalLinkReceipt>> BeginExternalLinkAsync(
        BeginMyOrbitExternalLinkIntent intent,
        CancellationToken cancellationToken = default);

    ValueTask<ControllerResult<MyOrbitAccountConnectionState>> CancelPendingLinkAsync(
        CancelMyOrbitExternalLinkIntent intent,
        CancellationToken cancellationToken = default);

    ValueTask<ControllerResult<MyOrbitAccountConnectionState>> DisconnectCurrentDeviceAsync(
        DisconnectMyOrbitCurrentDeviceIntent intent,
        CancellationToken cancellationToken = default);

    ValueTask<ControllerResult<MyOrbitDeviceCollection>> QueryDevicesAsync(
        QueryMyOrbitDevicesIntent intent,
        CancellationToken cancellationToken = default);

    ValueTask<ControllerResult<MyOrbitDeviceCollection>> RevokeDeviceAsync(
        RevokeMyOrbitDeviceIntent intent,
        CancellationToken cancellationToken = default);
}
