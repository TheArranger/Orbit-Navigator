using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Presentation.Accounts;

using Xunit;

namespace OrbitNavigator.Presentation.Tests;

public sealed class MyOrbitAccountSettingsPresentationTests
{
    [Fact]
    public void ConnectionStatesMatchFrozenHostBoundary()
    {
        Assert.Equal(
            [
                "ProviderUnavailable",
                "SignedOut",
                "LinkPending",
                "Connected",
                "ReauthorizationRequired",
                "Revoked",
                "Failed",
                "Loading",
            ],
            Enum.GetNames<MyOrbitAccountConnectionState>());
    }

    [Fact]
    public void PresentationBoundaryContainsNoCredentialOrProtocolValues()
    {
        var publicTypes = new[]
        {
            typeof(MyOrbitAccountSettingsPresentationState),
            typeof(MyOrbitDevicePresentation),
            typeof(MyOrbitAccountSettingsIntent),
            typeof(MyOrbitAccountOperationResult),
        };
        var forbidden = new[] { "Password", "Cookie", "Token", "Code", "Verifier", "AuthorizationUri", "CallbackUri" };

        foreach (var property in publicTypes.SelectMany(type => type.GetProperties()))
        {
            Assert.DoesNotContain(forbidden, term =>
                property.Name.Contains(term, StringComparison.OrdinalIgnoreCase));
            Assert.NotEqual(typeof(Uri), property.PropertyType);
        }

        Assert.Contains("account and device management only", MyOrbitAccountSettingsPresentationState.LinkScopeDescription);
        Assert.Contains("does not enable", MyOrbitAccountSettingsPresentationState.LinkScopeDescription, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrivateAndUnavailableStatesFailClosed()
    {
        var context = Context(BrowserProfileMode.Private);
        var unsafeState = State(context, MyOrbitAccountConnectionState.SignedOut) with
        {
            Capabilities = new(true, false, false, false, false),
        };
        Assert.Throws<ArgumentException>(unsafeState.Validate);

        var unavailable = State(Context(BrowserProfileMode.Normal), MyOrbitAccountConnectionState.ProviderUnavailable) with
        {
            Capabilities = new(true, false, false, false, false),
        };
        Assert.Throws<ArgumentException>(unavailable.Validate);
    }

    [Fact]
    public void EveryIntentCarriesPrivacyAndExpectedRevisionAndOnlyRevokeCarriesDevice()
    {
        var context = Context(BrowserProfileMode.Normal);
        var deviceId = new DeviceId(Guid.NewGuid());
        var revoke = new MyOrbitAccountSettingsIntent(
            Guid.NewGuid(), context, 14, MyOrbitAccountSettingsIntentKind.RevokeDevice, deviceId).Validate();
        Assert.Equal(14, revoke.ExpectedRevision);
        Assert.Equal(context, revoke.Privacy);

        Assert.Throws<ArgumentException>(() => new MyOrbitAccountSettingsIntent(
            Guid.NewGuid(), context, 14, MyOrbitAccountSettingsIntentKind.RevokeDevice).Validate());
        Assert.Throws<ArgumentException>(() => new MyOrbitAccountSettingsIntent(
            Guid.NewGuid(), context, 14, MyOrbitAccountSettingsIntentKind.Query, deviceId).Validate());
    }

    private static MyOrbitAccountSettingsPresentationState State(
        PrivacyContext context,
        MyOrbitAccountConnectionState connectionState) => new(
            context,
            1,
            connectionState,
            "My Orbit",
            null,
            "Safe status.",
            connectionState == MyOrbitAccountConnectionState.LinkPending ? DateTimeOffset.UtcNow : null,
            null,
            [],
            new(false, false, false, false, false));

    private static PrivacyContext Context(BrowserProfileMode mode) => new(
        new ProfileId(Guid.NewGuid()),
        new BrowserSessionId(Guid.NewGuid()),
        mode);
}
