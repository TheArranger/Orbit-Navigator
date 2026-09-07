using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Foundation.Security;
using Xunit;

namespace OrbitNavigator.Foundation.Tests.Security;

public sealed class WindowsUserPresenceProofAdapterTests
{
    [Fact]
    public async Task VerifiedProofIsShortLivedAndOneUse()
    {
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var adapter = new WindowsUserPresenceProofAdapter(new AllowVerifier(), clock);
        var context = Context(BrowserProfileMode.Normal);
        var issue = WindowsUserPresenceIssueRequest.Create(
            context,
            WindowsUserPresencePurpose.ForgottenOrbitPasswordVaultReset,
            TimeSpan.FromMinutes(1)).Value!;
        var proof = (await adapter.IssueAsync(issue)).Value!;
        var validation = WindowsUserPresenceValidationRequest.Create(
            context,
            proof,
            WindowsUserPresencePurpose.ForgottenOrbitPasswordVaultReset).Value!;

        var first = await adapter.ValidateAndConsumeAsync(validation);
        var replay = await adapter.ValidateAndConsumeAsync(validation);

        Assert.True(first.IsSuccess);
        Assert.False(replay.IsSuccess);
        Assert.Equal(ControllerErrorCode.AlreadyHandled, replay.Error?.Code);
    }

    [Fact]
    public async Task SessionInvalidationRevokesOutstandingProof()
    {
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var adapter = new WindowsUserPresenceProofAdapter(new AllowVerifier(), clock);
        var context = Context(BrowserProfileMode.Normal);
        var issue = WindowsUserPresenceIssueRequest.Create(
            context,
            WindowsUserPresencePurpose.ForgottenOrbitPasswordVaultReset,
            TimeSpan.FromMinutes(1)).Value!;
        var proof = (await adapter.IssueAsync(issue)).Value!;
        var request = WindowsUserPresenceValidationRequest.Create(
            context,
            proof,
            proof.Purpose).Value!;

        var receipt = await adapter.InvalidateSessionAsync(
            context.ProfileId,
            context.SessionId,
            WindowsUserPresenceInvalidationReason.WindowsSessionLocked);
        var validation = await adapter.ValidateAndConsumeAsync(request);

        Assert.Equal(1, receipt.Value?.InvalidatedProofCount);
        Assert.False(validation.IsSuccess);
    }

    private static PrivacyContext Context(BrowserProfileMode mode) =>
        new(
            new ProfileId(Guid.NewGuid()),
            new BrowserSessionId(Guid.NewGuid()),
            mode);

    private sealed class AllowVerifier : IWindowsUserPresenceVerifier
    {
        public ValueTask<bool> VerifyAsync(
            WindowsUserPresencePurpose purpose,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
