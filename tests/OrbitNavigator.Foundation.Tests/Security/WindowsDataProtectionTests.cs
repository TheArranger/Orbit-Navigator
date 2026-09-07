using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Foundation.Security;
using Xunit;

namespace OrbitNavigator.Foundation.Tests.Security;

public sealed class WindowsDataProtectionTests
{
    [Fact]
    public async Task RoundTripIsProfileAndPurposeBound()
    {
        var service = new WindowsDataProtection();
        var context = Context();
        byte[] plaintext = [3, 1, 4, 1, 5, 9];

        var protectedResult = await service.ProtectAsync(new ProtectKeyRequest(
            context,
            WindowsKeyProtectionPurpose.ProfileStorageKey,
            plaintext));
        Assert.True(protectedResult.IsSuccess);
        Assert.NotEqual(plaintext, protectedResult.Value?.Bytes.ToArray());

        var unprotected = await service.UnprotectAsync(new UnprotectKeyRequest(
            context,
            WindowsKeyProtectionPurpose.ProfileStorageKey,
            protectedResult.Value!));

        Assert.True(unprotected.IsSuccess);
        using var material = unprotected.Value!;
        Assert.Equal(plaintext, material.Bytes.ToArray());
    }

    [Fact]
    public async Task PurposeSubstitutionFailsIntegrityCheck()
    {
        var service = new WindowsDataProtection();
        var context = Context();
        var protectedResult = await service.ProtectAsync(new ProtectKeyRequest(
            context,
            WindowsKeyProtectionPurpose.ProfileStorageKey,
            new byte[] { 8, 6, 7, 5 }));

        var unprotected = await service.UnprotectAsync(new UnprotectKeyRequest(
            context,
            WindowsKeyProtectionPurpose.LocalPasswordVaultKey,
            protectedResult.Value!));

        Assert.False(unprotected.IsSuccess);
        Assert.Equal(ControllerErrorCode.IntegrityFailure, unprotected.Error?.Code);
    }

    private static PrivacyContext Context() =>
        new(
            new ProfileId(Guid.NewGuid()),
            new BrowserSessionId(Guid.NewGuid()),
            BrowserProfileMode.Normal);
}
