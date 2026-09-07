using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.Presentation.Accounts;

/// <summary>Redacted connection states safe to expose to Settings.</summary>
public enum MyOrbitAccountConnectionState
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

public enum MyOrbitAccountSettingsIntentKind
{
    Query = 0,
    BeginExternalLink = 1,
    CancelPendingLink = 2,
    DisconnectCurrentDevice = 3,
    QueryDevices = 4,
    RevokeDevice = 5,
}

public enum MyOrbitAccountOperationOutcome
{
    Accepted = 0,
    Stale = 1,
    PolicyDenied = 2,
    ProviderUnavailable = 3,
    Failed = 4,
}

public enum MyOrbitAccountSettingsFocusTarget
{
    Status = 0,
    Link = 1,
    Cancel = 2,
    Disconnect = 3,
    RefreshDevices = 4,
    DeviceList = 5,
}

/// <summary>A privacy-safe device summary. It never carries network or credential data.</summary>
public sealed record MyOrbitDevicePresentation(
    DeviceId DeviceId,
    string DisplayName,
    bool IsCurrentDevice,
    DateTimeOffset? RegisteredAtUtc,
    DateTimeOffset? LastSeenAtUtc,
    bool IsRevoked,
    DateTimeOffset? RevokedAtUtc,
    bool CanRevoke,
    string? RevokeUnavailableReason)
{
    public MyOrbitDevicePresentation Validate()
    {
        if (DeviceId.IsEmpty)
        {
            throw new ArgumentException("A device ID is required.", nameof(DeviceId));
        }

        if (string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Trim().Length > 120)
        {
            throw new ArgumentException("A safe device display name is required.", nameof(DisplayName));
        }

        if (RevokeUnavailableReason?.Trim().Length > 300)
        {
            throw new ArgumentException("A device explanation is too long.", nameof(RevokeUnavailableReason));
        }

        if (IsCurrentDevice && CanRevoke)
        {
            throw new ArgumentException("The current device disconnects through the account action.");
        }

        if (IsRevoked && (CanRevoke || RevokedAtUtc is null))
        {
            throw new ArgumentException("A revoked device must be immutable and timestamped.");
        }

        if (!CanRevoke && !IsCurrentDevice && !IsRevoked && string.IsNullOrWhiteSpace(RevokeUnavailableReason))
        {
            throw new ArgumentException("Unavailable device revocation requires a safe explanation.");
        }

        return this;
    }
}

public sealed record MyOrbitAccountSettingsCapabilities(
    bool CanBeginExternalLink,
    bool CanCancelPendingLink,
    bool CanDisconnectCurrentDevice,
    bool CanQueryDevices,
    bool CanRevokeDevices);

/// <summary>
/// Immutable, redacted Settings projection. Authorization protocol values are
/// deliberately absent from this Presentation boundary.
/// </summary>
public sealed record MyOrbitAccountSettingsPresentationState(
    PrivacyContext Privacy,
    long Revision,
    MyOrbitAccountConnectionState ConnectionState,
    string? SafeProviderLabel,
    string? SafeAccountLabel,
    string SafeStatusMessage,
    DateTimeOffset? LinkStartedAtUtc,
    DateTimeOffset? LinkExpiresAtUtc,
    IReadOnlyList<MyOrbitDevicePresentation> Devices,
    MyOrbitAccountSettingsCapabilities Capabilities)
{
    public const string LinkScopeDescription =
        "This connects Orbit Navigator to My Orbit for account and device management only. It does not enable tab, history, settings, or browsing-data sync.";

    public const string SystemBrowserDisclosure =
        "Linking opens your default system browser. Orbit Navigator never asks for or displays your My Orbit password, authorization code, or security credentials.";

    public bool LocalBrowsingRemainsAvailable => true;

    public MyOrbitAccountSettingsPresentationState Validate()
    {
        if (Privacy.ProfileId.IsEmpty || Privacy.SessionId.IsEmpty || Revision < 0)
        {
            throw new ArgumentException("A valid profile, session, and monotonic revision are required.");
        }

        if (!Enum.IsDefined(ConnectionState))
        {
            throw new ArgumentOutOfRangeException(nameof(ConnectionState));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(SafeStatusMessage);
        if (SafeStatusMessage.Trim().Length > 500 || SafeProviderLabel?.Trim().Length > 120 ||
            SafeAccountLabel?.Trim().Length > 200)
        {
            throw new ArgumentException("A safe account presentation label is too long.");
        }
        ArgumentNullException.ThrowIfNull(Devices);
        ArgumentNullException.ThrowIfNull(Capabilities);
        foreach (var device in Devices)
        {
            device.Validate();
        }

        if (Devices.Select(device => device.DeviceId).Distinct().Count() != Devices.Count)
        {
            throw new ArgumentException("Device IDs must be unique.", nameof(Devices));
        }

        if (Privacy.IsPrivate && Capabilities != new MyOrbitAccountSettingsCapabilities(false, false, false, false, false))
        {
            throw new ArgumentException("Private Settings cannot expose account mutations.");
        }

        if (ConnectionState == MyOrbitAccountConnectionState.ProviderUnavailable &&
            Capabilities != new MyOrbitAccountSettingsCapabilities(false, false, false, false, false))
        {
            throw new ArgumentException("An unavailable provider cannot expose account operations.");
        }

        if (ConnectionState == MyOrbitAccountConnectionState.LinkPending && LinkStartedAtUtc is null)
        {
            throw new ArgumentException("A pending link must expose its safe start time.");
        }

        return this;
    }
}

public sealed record MyOrbitAccountSettingsIntent(
    Guid StableIntentId,
    PrivacyContext Privacy,
    long ExpectedRevision,
    MyOrbitAccountSettingsIntentKind Kind,
    DeviceId? DeviceId = null)
{
    public MyOrbitAccountSettingsIntent Validate()
    {
        if (StableIntentId == Guid.Empty || Privacy.ProfileId.IsEmpty || Privacy.SessionId.IsEmpty || ExpectedRevision < 0)
        {
            throw new ArgumentException("A stable intent ID, privacy context, and expected revision are required.");
        }

        if (!Enum.IsDefined(Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(Kind));
        }

        if (Kind == MyOrbitAccountSettingsIntentKind.RevokeDevice)
        {
            if (DeviceId is null || DeviceId.Value.IsEmpty)
            {
                throw new ArgumentException("Revoke Device requires a device ID.", nameof(DeviceId));
            }
        }
        else if (DeviceId is not null)
        {
            throw new ArgumentException("Only Revoke Device may carry a device ID.", nameof(DeviceId));
        }

        return this;
    }
}

public sealed record MyOrbitAccountOperationResult(
    Guid StableIntentId,
    MyOrbitAccountOperationOutcome Outcome,
    string SafeMessage,
    MyOrbitAccountSettingsPresentationState? RefreshedState = null)
{
    public MyOrbitAccountOperationResult Validate()
    {
        if (StableIntentId == Guid.Empty || !Enum.IsDefined(Outcome))
        {
            throw new ArgumentException("A valid operation identity and outcome are required.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(SafeMessage);
        if (SafeMessage.Trim().Length > 500)
        {
            throw new ArgumentException("A safe operation message is too long.", nameof(SafeMessage));
        }
        RefreshedState?.Validate();
        return this;
    }
}
