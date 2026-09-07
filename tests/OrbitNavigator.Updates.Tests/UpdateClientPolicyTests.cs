using System.Net;
using System.Net.Http.Headers;
using OrbitNavigator.Contracts.Updates;
using Xunit;

namespace OrbitNavigator.Updates.Tests;

public sealed class UpdateClientPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NewInstallDefaultsToPrimaryWithRandomLocalSeed()
    {
        var first = UpdateClientState.CreateNew();
        var second = UpdateClientState.CreateNew();

        Assert.Equal(UpdateReleaseChannel.Primary, first.Channel);
        Assert.False(first.BetaChannelOptIn);
        Assert.Equal(UpdateClientState.InstallSeedBytes, first.GetInstallSeed().Length);
        Assert.NotEqual(first.InstallSeedBase64, second.InstallSeedBase64);
    }

    [Fact]
    public void BetaSelectionRequiresExplicitOptInAndIsExclusive()
    {
        var primary = UpdateClientState.CreateNew();

        Assert.Throws<InvalidOperationException>(() =>
            primary.SelectChannel(UpdateReleaseChannel.Beta, explicitUserOptIn: false));
        var beta = primary.SelectChannel(UpdateReleaseChannel.Beta, explicitUserOptIn: true);
        var selectedPrimary = beta.SelectChannel(
            UpdateReleaseChannel.Primary,
            explicitUserOptIn: false);

        Assert.Equal(UpdateReleaseChannel.Beta, beta.Channel);
        Assert.True(beta.BetaChannelOptIn);
        Assert.Equal(UpdateReleaseChannel.Primary, selectedPrimary.Channel);
        Assert.False(selectedPrimary.BetaChannelOptIn);
    }

    [Fact]
    public void ChannelReleaseSequencesAreIndependentAndCannotDowngrade()
    {
        var state = UpdateClientState.CreateNew()
            .WithAcceptedReleaseSequence(UpdateReleaseChannel.Primary, 12)
            .WithAcceptedReleaseSequence(UpdateReleaseChannel.Beta, 75);

        Assert.Equal(12, state.GetAcceptedReleaseSequence(UpdateReleaseChannel.Primary));
        Assert.Equal(75, state.GetAcceptedReleaseSequence(UpdateReleaseChannel.Beta));
        Assert.Throws<InvalidOperationException>(() => state.WithAcceptedReleaseSequence(
            UpdateReleaseChannel.Primary,
            11));
        Assert.Equal(75, state.GetAcceptedReleaseSequence(UpdateReleaseChannel.Beta));
    }

    [Fact]
    public async Task InstallSeedAndChannelStatePersistLocally()
    {
        using var temp = new TempDirectory();
        var store = new FileUpdateClientStateStore(Path.Combine(temp.Path, "update-client.json"));
        var created = await store.LoadOrCreateAsync();
        var beta = created
            .SelectChannel(UpdateReleaseChannel.Beta, explicitUserOptIn: true)
            .WithEntityTag(UpdateReleaseChannel.Beta, "\"beta-v7\"");
        await store.SaveAsync(beta);

        var reloaded = await store.LoadOrCreateAsync();

        Assert.Equal(created.InstallSeedBase64, reloaded.InstallSeedBase64);
        Assert.Equal(UpdateReleaseChannel.Beta, reloaded.Channel);
        Assert.Equal("\"beta-v7\"", reloaded.GetEntityTag(UpdateReleaseChannel.Beta));
        Assert.Null(reloaded.GetEntityTag(UpdateReleaseChannel.Primary));
    }

    [Fact]
    public void RolloutDecisionIsDeterministicAndChannelBound()
    {
        var state = UpdateClientState.CreateNew();
        var manifest = Manifest("primary", 41, 2_500, 10_000);

        var first = UpdateRolloutGate.Evaluate(manifest, state, Now);
        var second = UpdateRolloutGate.Evaluate(manifest, state, Now);

        Assert.Equal(first, second);
        Assert.InRange(first.ActiveBasisPoints, 2_500, 10_000);
        Assert.Throws<InvalidOperationException>(() => UpdateRolloutGate.Evaluate(
            manifest with { Channel = "beta" },
            state,
            Now));
    }

    [Fact]
    public void FutureRolloutDoesNotBecomeEligibleEarly()
    {
        var state = UpdateClientState.CreateNew();
        var manifest = Manifest("primary", 41, 10_000, 10_000) with
        {
            Rollout = new UpdateRolloutSchedule(
                Now.AddHours(2),
                Now.AddHours(2),
                10_000,
                10_000),
        };

        var decision = UpdateRolloutGate.Evaluate(manifest, state, Now);

        Assert.False(decision.IsEligible);
        Assert.Equal(0, decision.ActiveBasisPoints);
        Assert.Equal(Now.AddHours(2), decision.ReevaluateAtUtc);
    }

    [Fact]
    public void InitialJitterAndExponentialRetryStayBounded()
    {
        var state = UpdateClientState.CreateNew();

        var initial = UpdateCheckSchedule.GetInitialDelay(state);
        var firstFailure = UpdateCheckSchedule.GetFailureDelay(state, 1);
        var sixthFailure = UpdateCheckSchedule.GetFailureDelay(state, 6);
        var twentiethFailure = UpdateCheckSchedule.GetFailureDelay(state, 20);

        Assert.InRange(initial, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(15));
        Assert.InRange(firstFailure, TimeSpan.FromMinutes(11.25), TimeSpan.FromMinutes(18.75));
        Assert.True(sixthFailure > firstFailure);
        Assert.InRange(twentiethFailure, TimeSpan.FromHours(9), TimeSpan.FromHours(15));
    }

    [Fact]
    public void ConditionalRequestUsesOnlySharedServerEntityTag()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://updates.example.test/primary/manifest.json");
        UpdateConditionalRequest.ApplyEntityTag(request, "\"primary-v41\"");
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.ETag = new EntityTagHeaderValue("\"primary-v42\"");

        Assert.Equal("\"primary-v41\"", request.Headers.IfNoneMatch.Single().ToString());
        Assert.Equal("\"primary-v42\"", UpdateConditionalRequest.ReadEntityTag(response));
        Assert.DoesNotContain(request.Headers, header =>
            header.Key.Contains("Install", StringComparison.OrdinalIgnoreCase) ||
            header.Key.Contains("Client", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UnsignedPrimaryIsAlwaysBlocked()
    {
        var decision = UpdateApplyPolicy.Evaluate(
            UpdateReleaseChannel.Primary,
            UpdatePublisherTrust.Unsigned,
            UpdatePreference.Automatic,
            deliberateUnsignedBetaConfirmation: true);

        Assert.Equal(UpdateApplyDisposition.Blocked, decision.Disposition);
        Assert.Equal("error.update.primary_requires_trusted_publisher", decision.MessageKey);
    }

    [Fact]
    public void UnsignedBetaCanNeverSilentlyAutoApply()
    {
        var beforeConfirmation = UpdateApplyPolicy.Evaluate(
            UpdateReleaseChannel.Beta,
            UpdatePublisherTrust.Unsigned,
            UpdatePreference.Automatic,
            deliberateUnsignedBetaConfirmation: false);
        var afterConfirmation = UpdateApplyPolicy.Evaluate(
            UpdateReleaseChannel.Beta,
            UpdatePublisherTrust.Unsigned,
            UpdatePreference.Automatic,
            deliberateUnsignedBetaConfirmation: true);

        Assert.Equal(
            UpdateApplyDisposition.RequiresDeliberateConfirmation,
            beforeConfirmation.Disposition);
        Assert.Equal(
            UpdateApplyPolicy.UnsignedBetaChannelDisclosureKey,
            beforeConfirmation.MessageKey);
        Assert.Equal(UpdateApplyDisposition.UserApprovedOnly, afterConfirmation.Disposition);
    }

    [Fact]
    public void TrustedPrimaryMayHonorAutomaticPreference()
    {
        var decision = UpdateApplyPolicy.Evaluate(
            UpdateReleaseChannel.Primary,
            UpdatePublisherTrust.TrustedPublisher,
            UpdatePreference.Automatic,
            deliberateUnsignedBetaConfirmation: false);

        Assert.Equal(UpdateApplyDisposition.AutomaticAllowed, decision.Disposition);
    }

    private static VerifiedUpdateManifest Manifest(
        string channel,
        long releaseSequence,
        int initialBasisPoints,
        int finalBasisPoints) =>
        new(
            new UpdatePackageInfo(
                new Version(1, 1, 0),
                new Uri($"https://updates.example.test/{channel}/OrbitNavigator-1.1.0.exe"),
                new string('A', 64),
                250_000_000,
                true),
            channel,
            releaseSequence,
            Now.AddHours(-1),
            Now.AddDays(7),
            new UpdateRolloutSchedule(
                Now.AddHours(-1),
                Now.AddHours(1),
                initialBasisPoints,
                finalBasisPoints),
            $"{channel}-2026-a");
}
