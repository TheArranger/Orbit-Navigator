using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.UX;
using OrbitNavigator.Presentation.Reading;
using Xunit;

namespace OrbitNavigator.Presentation.Tests;

public sealed class ReadingPreferencesPresenterTests
{
    [Fact]
    public async Task PreviewThenApplyClosesPreviewAndCommitsDraft()
    {
        var controller = new FakeReadingController();
        var presenter = new ReadingPreferencesPresenter(controller);
        var target = ReadingPreferencesTarget.GlobalPersistent(TestContexts.Browsing()).Value!;
        await presenter.LoadAsync(target);
        var draft = State(textScale: 1.4m);

        await presenter.PreviewAsync(draft);
        Assert.True(presenter.State.HasPreview);
        await presenter.ApplyAsync();

        Assert.False(presenter.State.HasPreview);
        Assert.Equal(draft, presenter.State.CommittedState);
        Assert.Equal(1, controller.ApplyCount);
        Assert.Equal("ui.reading.applied", presenter.State.Announcement?.MessageKey);
    }

    [Fact]
    public async Task UnsupportedDraftIsAnnouncedWithoutCallingPreviewController()
    {
        var controller = new FakeReadingController();
        var presenter = new ReadingPreferencesPresenter(controller);
        var target = ReadingPreferencesTarget.GlobalPersistent(TestContexts.Browsing()).Value!;
        await presenter.LoadAsync(target);

        await presenter.PreviewAsync(State(wordSpacing: 9m));

        Assert.Contains(ReadingPreferenceField.WordSpacing, presenter.State.UnsupportedFields);
        Assert.Equal(0, controller.BeginCount);
        Assert.Equal(ControllerErrorCode.NotSupported, presenter.State.Failure?.Code);
    }

    private static ReadingPreferencesState State(
        decimal textScale = 1m,
        decimal wordSpacing = 0m) =>
        new(
            true,
            new ReadingPreferencesValues(
                "System",
                textScale,
                1.5m,
                0m,
                wordSpacing,
                ReadingContrast.System,
                ReadingTint.None,
                ReadingLineFocus.Off));

    private sealed class FakeReadingController : IReadingPreferencesController
    {
        private ReadingPreferencesState current = State();

        public int BeginCount { get; private set; }

        public int ApplyCount { get; private set; }

        public ValueTask<ControllerResult<ReadingPreferencesCapabilities>> GetCapabilitiesAsync(
            ReadingPreferencesTarget target,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ControllerResult<ReadingPreferencesCapabilities>.Success(
                new ReadingPreferencesCapabilities(
                    Enum.GetValues<ReadingPreferencesScope>(),
                    ["System", "OpenDyslexic"],
                    new DecimalRange(0.5m, 3m, 0.1m),
                    new DecimalRange(1m, 3m, 0.1m),
                    new DecimalRange(0m, 1m, 0.05m),
                    new DecimalRange(0m, 2m, 0.05m),
                    Enum.GetValues<ReadingContrast>(),
                    Enum.GetValues<ReadingTint>(),
                    Enum.GetValues<ReadingLineFocus>(),
                    Enum.GetValues<ReadingPreferenceField>())));

        public ValueTask<ControllerResult<ReadingPreferencesState>> GetCurrentAsync(
            ReadingPreferencesTarget target,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ControllerResult<ReadingPreferencesState>.Success(current));

        public ValueTask<ControllerResult<ReadingPreviewSession>> BeginPreviewAsync(
            BeginReadingPreviewIntent intent,
            CancellationToken cancellationToken)
        {
            BeginCount++;
            return ValueTask.FromResult(ControllerResult<ReadingPreviewSession>.Success(
                new ReadingPreviewSession(
                    intent.Target,
                    PreviewId(intent.Target),
                    current,
                    intent.State)));
        }

        public ValueTask<ControllerResult<ReadingPreviewSession>> UpdatePreviewAsync(
            UpdateReadingPreviewIntent intent,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ControllerResult<ReadingPreviewSession>.Success(
                new ReadingPreviewSession(intent.Target, intent.PreviewId, current, intent.State)));

        public ValueTask<ControllerResult<ReadingPreferencesState>> ApplyAsync(
            ApplyReadingPreferencesIntent intent,
            CancellationToken cancellationToken)
        {
            ApplyCount++;
            current = intent.State;
            return ValueTask.FromResult(ControllerResult<ReadingPreferencesState>.Success(current));
        }

        public ValueTask<ControllerResult> CancelPreviewAsync(
            CancelReadingPreviewIntent intent,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ControllerResult.Success());

        public ValueTask<ControllerResult> EndPreviewAsync(
            EndReadingPreviewIntent intent,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ControllerResult.Success());

        public ValueTask<ControllerResult<ReadingPreferencesState>> ResetAsync(
            ResetReadingPreferencesIntent intent,
            CancellationToken cancellationToken)
        {
            current = State();
            return ValueTask.FromResult(ControllerResult<ReadingPreferencesState>.Success(current));
        }

        private static ReadingPreviewId PreviewId(ReadingPreferencesTarget target) =>
            new(
                target.Context.Privacy.ProfileId,
                target.Context.Privacy.SessionId,
                target.Context.TabId,
                Guid.NewGuid());
    }
}
