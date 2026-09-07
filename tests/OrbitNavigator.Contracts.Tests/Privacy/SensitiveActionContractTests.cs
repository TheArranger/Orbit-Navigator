using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Privacy;
using Xunit;

namespace OrbitNavigator.Contracts.Tests.Privacy;

public sealed class SensitiveActionContractTests
{
    [Theory]
    [InlineData(SensitiveActionKind.ViewSavedPasswords)]
    [InlineData(SensitiveActionKind.AutofillSavedPassword)]
    [InlineData(SensitiveActionKind.DeleteSyncedData)]
    [InlineData(SensitiveActionKind.ResetEncryptedSync)]
    [InlineData(SensitiveActionKind.RevokeSyncDevice)]
    [InlineData(SensitiveActionKind.ResetLocalVault)]
    [InlineData(SensitiveActionKind.ClearProtectedLocalData)]
    public void AuthorizationTokenBindsPurposeProfileAndSession(SensitiveActionKind purpose)
    {
        var context = Privacy(BrowserProfileMode.Normal);
        var now = DateTimeOffset.UtcNow;

        var result = SensitiveActionAuthorizationToken.Create(
            new SensitiveActionAuthorizationTokenId(Guid.NewGuid()),
            context,
            purpose,
            now,
            now.AddMinutes(1));

        Assert.True(result.IsSuccess);
        Assert.True(result.Value?.IsBoundTo(context, purpose));
    }

    [Fact]
    public void ForgottenPasswordResetCannotUseOrbitAuthorizationToken()
    {
        var context = Privacy(BrowserProfileMode.Normal);
        var now = DateTimeOffset.UtcNow;

        var result = SensitiveActionAuthorizationToken.Create(
            new SensitiveActionAuthorizationTokenId(Guid.NewGuid()),
            context,
            SensitiveActionKind.ForgottenOrbitPasswordVaultResetWithWindowsUserPresence,
            now,
            now.AddMinutes(1));

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void ForgottenPasswordResetRejectsPrivateContextEvenWithBoundWindowsProof()
    {
        var privacy = Privacy(BrowserProfileMode.Private);
        var context = Browsing(privacy);
        var now = DateTimeOffset.UtcNow;
        var proof = WindowsUserPresenceProof.Create(
            new WindowsUserPresenceProofId(Guid.NewGuid()),
            WindowsUserPresencePurpose.ForgottenOrbitPasswordVaultReset,
            privacy.ProfileId,
            privacy.SessionId,
            now,
            now.AddMinutes(1)).Value!;

        var result = ForgottenOrbitPasswordVaultResetRequest.Create(context, proof, true);

        Assert.False(result.IsSuccess);
        Assert.Equal(ControllerErrorCode.PolicyDenied, result.Error?.Code);
    }

    private static PrivacyContext Privacy(BrowserProfileMode mode) =>
        new(
            new ProfileId(Guid.NewGuid()),
            new BrowserSessionId(Guid.NewGuid()),
            mode);

    private static BrowsingContext Browsing(PrivacyContext privacy) =>
        new(
            privacy,
            new BrowserWindowId(Guid.NewGuid()),
            new BrowserTabId(Guid.NewGuid()),
            null);
}
