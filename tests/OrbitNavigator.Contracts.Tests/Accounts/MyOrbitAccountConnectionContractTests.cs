using OrbitNavigator.Contracts.Accounts;
using OrbitNavigator.Contracts.Common;
using Xunit;

namespace OrbitNavigator.Contracts.Tests.Accounts;

public sealed class MyOrbitAccountConnectionContractTests
{
    [Fact]
    public void FrozenStateNamesAreExact()
    {
        Assert.Equal(
            [
                "ProviderUnavailable", "SignedOut", "LinkPending", "Connected",
                "ReauthorizationRequired", "Revoked", "Failed", "Loading",
            ],
            Enum.GetNames<MyOrbitAccountConnectionStateKind>());
    }

    [Fact]
    public void ScopeCopyIsLinkAndDeviceManagementOnly()
    {
        Assert.Equal(
            ["LinkAccount", "ManageLinkedDevices"],
            Enum.GetNames<MyOrbitAccountScope>());
    }

    [Fact]
    public void EveryPresentationOperationCarriesExpectedRevision()
    {
        var intents = new[]
        {
            typeof(MyOrbitAccountQuery),
            typeof(BeginMyOrbitExternalLinkIntent),
            typeof(CancelMyOrbitExternalLinkIntent),
            typeof(DisconnectMyOrbitCurrentDeviceIntent),
            typeof(QueryMyOrbitDevicesIntent),
            typeof(RevokeMyOrbitDeviceIntent),
        };

        Assert.All(intents, type => Assert.Equal(
            typeof(MyOrbitAccountRevision),
            type.GetProperty("ExpectedRevision")?.PropertyType));
    }

    [Fact]
    public void PresentationDtosCannotCarryProtocolSecretsOrLocations()
    {
        var forbidden = new[]
        {
            "Uri", "Url", "Ip", "Token", "Code", "Cookie", "Password", "Verifier", "State",
        };
        var publicTypes = new[]
        {
            typeof(MyOrbitAccountConnectionState),
            typeof(MyOrbitExternalLinkReceipt),
            typeof(MyOrbitLinkedDevice),
            typeof(MyOrbitDeviceCollection),
        };

        foreach (var type in publicTypes)
        {
            foreach (var property in type.GetProperties())
            {
                Assert.DoesNotContain(
                    forbidden,
                    word => property.Name.Contains(word, StringComparison.OrdinalIgnoreCase) &&
                        property.Name != nameof(MyOrbitAccountConnectionState.State));
            }
        }
    }

    [Fact]
    public void DeviceDtoContainsOnlyAuthoritativeSafeFields()
    {
        Assert.Equal(
            [
                "DeviceId", "DisplayName", "IsCurrent", "RegisteredAtUtc",
                "LastSeenAtUtc", "RevokedAtUtc",
            ],
            typeof(MyOrbitLinkedDevice).GetProperties().Select(property => property.Name));
    }

    [Fact]
    public void StateCopiesScopeInput()
    {
        var scopes = new List<MyOrbitAccountScope> { MyOrbitAccountScope.LinkAccount };
        var privacy = new PrivacyContext(
            new ProfileId(Guid.NewGuid()),
            new BrowserSessionId(Guid.NewGuid()),
            BrowserProfileMode.Normal);
        var state = new MyOrbitAccountConnectionState(
            privacy,
            MyOrbitAccountRevision.Initial,
            MyOrbitAccountConnectionStateKind.SignedOut,
            "My Orbit",
            null,
            scopes,
            null,
            null,
            null,
            null,
            MyOrbitAccountCapabilities.BeginExternalLink);

        scopes.Add(MyOrbitAccountScope.ManageLinkedDevices);

        Assert.Equal([MyOrbitAccountScope.LinkAccount], state.GrantedScopes);
        Assert.True(state.LocalBrowsingAvailable);
    }

    [Fact]
    public void AccountLabelCannotBeAnEmailAddress()
    {
        var privacy = new PrivacyContext(
            new ProfileId(Guid.NewGuid()),
            new BrowserSessionId(Guid.NewGuid()),
            BrowserProfileMode.Normal);

        Assert.Throws<ArgumentException>(() => new MyOrbitAccountConnectionState(
            privacy,
            MyOrbitAccountRevision.Initial,
            MyOrbitAccountConnectionStateKind.Connected,
            "My Orbit",
            "person@example.com",
            [MyOrbitAccountScope.LinkAccount],
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddDays(1),
            MyOrbitAccountCapabilities.DisconnectCurrentDevice));
    }
}
