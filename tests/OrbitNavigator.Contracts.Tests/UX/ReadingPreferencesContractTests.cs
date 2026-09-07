using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.UX;
using Xunit;

namespace OrbitNavigator.Contracts.Tests.UX;

public sealed class ReadingPreferencesContractTests
{
    [Fact]
    public void PrivateContextAllowsOnlySessionTarget()
    {
        var context = Browsing(BrowserProfileMode.Private);

        var global = ReadingPreferencesTarget.GlobalPersistent(context);
        var site = ReadingPreferencesTarget.SitePersistent(context, Site("https://example.test"));
        var session = ReadingPreferencesTarget.Session(context);

        Assert.Equal(ControllerErrorCode.PolicyDenied, global.Error?.Code);
        Assert.Equal(ControllerErrorCode.PolicyDenied, site.Error?.Code);
        Assert.True(session.IsSuccess);
        Assert.Equal(ReadingPreferencesScope.Session, session.Value?.Scope);
    }

    [Fact]
    public void PreviewAndApplyAreBoundToProfileSessionAndTab()
    {
        var context = Browsing(BrowserProfileMode.Normal);
        var target = ReadingPreferencesTarget.GlobalPersistent(context).Value!;
        var previewId = new ReadingPreviewId(
            context.Privacy.ProfileId,
            context.Privacy.SessionId,
            context.TabId,
            Guid.NewGuid());
        var state = State();
        var apply = new ApplyReadingPreferencesIntent(target, previewId, state);

        Assert.False(apply.PreviewId.IsEmpty);
        Assert.Equal(target.Context.Privacy.ProfileId, apply.PreviewId.ProfileId);
        Assert.Equal(target.Context.Privacy.SessionId, apply.PreviewId.SessionId);
        Assert.Equal(target.Context.TabId, apply.PreviewId.TabId);
        Assert.Equal(ControllerErrorCode.Conflict, ReadingPreviewErrors.Stale().Code);
        Assert.Equal(ControllerErrorCode.NotFound, ReadingPreviewErrors.NotFound().Code);
    }

    [Fact]
    public void ReadingEffectResultFactoriesPreserveDiscriminatedInvariants()
    {
        var applied = ReadingEffectResult.Applied();
        var unsupported = ReadingEffectResult.Unsupported(
            [ReadingPreferenceField.WordSpacing, ReadingPreferenceField.LineFocus]);
        var error = ControllerError.Create(
            ControllerErrorCode.InternalFailure,
            "error.reading.host_failed");
        var failed = ReadingEffectResult.Failed(error);

        Assert.Equal(ReadingEffectResultKind.Applied, applied.Kind);
        Assert.Empty(applied.UnsupportedFields);
        Assert.Null(applied.Error);
        Assert.Equal(ReadingEffectResultKind.Unsupported, unsupported.Kind);
        Assert.NotEmpty(unsupported.UnsupportedFields);
        Assert.Null(unsupported.Error);
        Assert.Equal(ReadingEffectResultKind.Failed, failed.Kind);
        Assert.Empty(failed.UnsupportedFields);
        Assert.Same(error, failed.Error);
    }

    [Fact]
    public void CapabilitiesExposeEveryApprovedReadingDimensionAndTarget()
    {
        var capabilities = new ReadingPreferencesCapabilities(
            Enum.GetValues<ReadingPreferencesScope>(),
            ["System", "OpenDyslexic"],
            new DecimalRange(0.5m, 3m, 0.1m),
            new DecimalRange(1m, 3m, 0.1m),
            new DecimalRange(0m, 1m, 0.05m),
            new DecimalRange(0m, 2m, 0.05m),
            Enum.GetValues<ReadingContrast>(),
            Enum.GetValues<ReadingTint>(),
            Enum.GetValues<ReadingLineFocus>(),
            Enum.GetValues<ReadingPreferenceField>());

        Assert.Contains(ReadingPreferencesScope.GlobalPersistent, capabilities.SupportedTargets);
        Assert.Contains(ReadingPreferencesScope.SitePersistent, capabilities.SupportedTargets);
        Assert.Contains(ReadingPreferencesScope.Session, capabilities.SupportedTargets);
        Assert.Contains(ReadingPreferenceField.WordSpacing, capabilities.GlobalSyncAllowlist);
        Assert.Contains(ReadingPreferenceField.Contrast, capabilities.GlobalSyncAllowlist);
        Assert.Contains(ReadingPreferenceField.Tint, capabilities.GlobalSyncAllowlist);
        Assert.Contains(ReadingPreferenceField.LineFocus, capabilities.GlobalSyncAllowlist);
    }

    private static ReadingPreferencesState State() =>
        new(
            true,
            new ReadingPreferencesValues(
                "System",
                1.1m,
                1.5m,
                0.05m,
                0.1m,
                ReadingContrast.High,
                ReadingTint.Warm,
                ReadingLineFocus.ThreeLines));

    private static SiteIdentity Site(string uri) => SiteIdentity.Create(new Uri(uri)).Value!;

    private static BrowsingContext Browsing(BrowserProfileMode mode) =>
        new(
            new PrivacyContext(
                new ProfileId(Guid.NewGuid()),
                new BrowserSessionId(Guid.NewGuid()),
                mode),
            new BrowserWindowId(Guid.NewGuid()),
            new BrowserTabId(Guid.NewGuid()),
            null);
}
