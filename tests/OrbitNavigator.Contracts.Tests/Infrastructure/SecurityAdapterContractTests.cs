using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using Xunit;

namespace OrbitNavigator.Contracts.Tests.Infrastructure;

public sealed class SecurityAdapterContractTests
{
    [Fact]
    public void ProtectedKeyBlobCopiesCallerOwnedBytes()
    {
        byte[] source = [1, 2, 3, 4];

        var result = ProtectedKeyBlob.Create("dpapi-v1", source);
        source[0] = 99;

        Assert.True(result.IsSuccess);
        Assert.Equal((byte)1, result.Value?.Bytes.Span[0]);
    }

    [Fact]
    public void KeyProtectionRequestsAreProfileAndPurposeBound()
    {
        var context = CreateContext(BrowserProfileMode.Normal);
        var request = new ProtectKeyRequest(
            context,
            WindowsKeyProtectionPurpose.LocalPasswordVaultKey,
            new byte[] { 7 });

        Assert.True(request.IsStructurallyValid);
        Assert.Equal(context.ProfileId, request.ProfileId);
        Assert.Same(context, request.Context);
        Assert.Equal(WindowsKeyProtectionPurpose.LocalPasswordVaultKey, request.Purpose);
    }

    [Fact]
    public void MyOrbitCredentialHasDedicatedProtectionPurpose()
    {
        Assert.Equal(4, (int)WindowsKeyProtectionPurpose.MyOrbitConnectionCredential);
        Assert.NotEqual(
            WindowsKeyProtectionPurpose.SyncKeysetWrappingKey,
            WindowsKeyProtectionPurpose.MyOrbitConnectionCredential);
        Assert.NotEqual(
            WindowsKeyProtectionPurpose.LocalPasswordVaultKey,
            WindowsKeyProtectionPurpose.MyOrbitConnectionCredential);
    }

    [Fact]
    public void UserPresenceProofRetainsPurposeProfileSessionAndExpiryBindings()
    {
        var profileId = new ProfileId(Guid.NewGuid());
        var sessionId = new BrowserSessionId(Guid.NewGuid());
        var issuedAt = DateTimeOffset.UtcNow;

        var result = WindowsUserPresenceProof.Create(
            new WindowsUserPresenceProofId(Guid.NewGuid()),
            WindowsUserPresencePurpose.ForgottenOrbitPasswordVaultReset,
            profileId,
            sessionId,
            issuedAt,
            issuedAt.AddMinutes(2));

        Assert.True(result.IsSuccess);
        Assert.Equal(profileId, result.Value?.ProfileId);
        Assert.Equal(sessionId, result.Value?.SessionId);
        Assert.Equal(WindowsUserPresencePurpose.ForgottenOrbitPasswordVaultReset, result.Value?.Purpose);
        Assert.True(result.Value?.ExpiresAtUtc > result.Value?.IssuedAtUtc);
    }

    [Fact]
    public void UserPresenceProofRejectsNonExpiringLifetime()
    {
        var now = DateTimeOffset.UtcNow;

        var result = WindowsUserPresenceProof.Create(
            new WindowsUserPresenceProofId(Guid.NewGuid()),
            WindowsUserPresencePurpose.ForgottenOrbitPasswordVaultReset,
            new ProfileId(Guid.NewGuid()),
            new BrowserSessionId(Guid.NewGuid()),
            now,
            now);

        Assert.False(result.IsSuccess);
        Assert.Equal(ControllerErrorCode.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public void UserPresenceIssueLifetimeIsShortLived()
    {
        var context = CreateContext(BrowserProfileMode.Normal);
        var valid = WindowsUserPresenceIssueRequest.Create(
            context,
            WindowsUserPresencePurpose.ForgottenOrbitPasswordVaultReset,
            TimeSpan.FromMinutes(5));
        var tooLong = WindowsUserPresenceIssueRequest.Create(
            context,
            WindowsUserPresencePurpose.ForgottenOrbitPasswordVaultReset,
            TimeSpan.FromMinutes(6));

        Assert.True(valid.IsSuccess);
        Assert.False(tooLong.IsSuccess);
    }

    [Fact]
    public void ValidationRequestCarriesIndependentExpectedBindings()
    {
        var profileId = new ProfileId(Guid.NewGuid());
        var sessionId = new BrowserSessionId(Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;
        var proof = Assert.IsType<WindowsUserPresenceProof>(WindowsUserPresenceProof.Create(
            new WindowsUserPresenceProofId(Guid.NewGuid()),
            WindowsUserPresencePurpose.ForgottenOrbitPasswordVaultReset,
            profileId,
            sessionId,
            now,
            now.AddMinutes(1)).Value);

        var request = WindowsUserPresenceValidationRequest.Create(
            CreateContext(BrowserProfileMode.Normal, profileId, sessionId),
            proof,
            WindowsUserPresencePurpose.ForgottenOrbitPasswordVaultReset);

        Assert.True(request.IsSuccess);
        Assert.Equal(profileId, request.Value?.ExpectedProfileId);
        Assert.Equal(sessionId, request.Value?.ExpectedSessionId);
    }

    [Fact]
    public void PrivateContextCannotIssueOrValidateUserPresenceProof()
    {
        var context = CreateContext(BrowserProfileMode.Private);
        var issue = WindowsUserPresenceIssueRequest.Create(
            context,
            WindowsUserPresencePurpose.ForgottenOrbitPasswordVaultReset,
            TimeSpan.FromMinutes(1));

        Assert.False(issue.IsSuccess);
        Assert.Equal(ControllerErrorCode.PolicyDenied, issue.Error?.Code);

        var now = DateTimeOffset.UtcNow;
        var proof = Assert.IsType<WindowsUserPresenceProof>(WindowsUserPresenceProof.Create(
            new WindowsUserPresenceProofId(Guid.NewGuid()),
            WindowsUserPresencePurpose.ForgottenOrbitPasswordVaultReset,
            context.ProfileId,
            context.SessionId,
            now,
            now.AddMinutes(1)).Value);
        var validation = WindowsUserPresenceValidationRequest.Create(
            context,
            proof,
            WindowsUserPresencePurpose.ForgottenOrbitPasswordVaultReset);

        Assert.False(validation.IsSuccess);
        Assert.Equal(ControllerErrorCode.PolicyDenied, validation.Error?.Code);
    }

    [Fact]
    public void SessionLifecycleEventCannotOmitProfileIdentity()
    {
        Assert.Throws<ArgumentException>(() => new HostLifecycleEventArgs(
            HostLifecycleTransition.BrowserSessionEnding,
            DateTimeOffset.UtcNow,
            sessionId: new BrowserSessionId(Guid.NewGuid())));
    }

    private static PrivacyContext CreateContext(
        BrowserProfileMode mode,
        ProfileId? profileId = null,
        BrowserSessionId? sessionId = null) =>
        new(
            profileId ?? new ProfileId(Guid.NewGuid()),
            sessionId ?? new BrowserSessionId(Guid.NewGuid()),
            mode);
}
