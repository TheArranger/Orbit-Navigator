using OrbitNavigator.ClipboardShelf.Presentation;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Privacy;
using Xunit;

namespace OrbitNavigator.ClipboardShelf.Presentation.Tests;

public sealed class ClipboardShelfPresenterTests
{
    [Fact]
    public async Task PrivateShelfIsUnavailableEmptyAndNeverMutates()
    {
        var controller = new FakeClipboardShelfController(
            ClipboardShelfReadModel.PrivateUnavailable());
        var presenter = new ClipboardShelfPresenter(controller);
        var context = Browsing(BrowserProfileMode.Private);

        await presenter.LoadAsync(context);
        await presenter.UseAsync(new ClipboardShelfItemId(context.Privacy.ProfileId, Guid.NewGuid()));
        presenter.RequestClear();
        await presenter.ConfirmClearAsync();

        Assert.False(presenter.State.IsAvailable);
        Assert.Equal(ClipboardShelfUnavailableReason.PrivateMode, presenter.State.UnavailableReason);
        Assert.Empty(presenter.State.Items);
        Assert.Equal(0, controller.UseCalls);
        Assert.Equal(0, controller.ClearCalls);
        Assert.Equal("ui.clipboard_shelf.private_unavailable", presenter.State.Announcement?.MessageKey);
    }

    [Fact]
    public async Task UseExposesOnlyReceiptAndClearRequiresConfirmation()
    {
        var context = Browsing(BrowserProfileMode.Normal);
        var item = new ClipboardShelfItemSummary(
            new ClipboardShelfItemId(context.Privacy.ProfileId, Guid.NewGuid()),
            ClipboardShelfContentKind.Text,
            "Visible preview",
            null,
            DateTimeOffset.UtcNow);
        var controller = new FakeClipboardShelfController(
            new ClipboardShelfReadModel(
                ClipboardShelfAvailability.Available,
                ClipboardShelfUnavailableReason.None,
                ClipboardShelfReadModel.MaximumCapacity,
                [item]));
        var presenter = new ClipboardShelfPresenter(controller);
        await presenter.LoadAsync(context);

        await presenter.UseAsync(item.Id);
        Assert.Equal(item.Id, presenter.State.LastUseReceipt?.ItemId);
        Assert.Equal(1, controller.UseCalls);
        await presenter.ConfirmClearAsync();
        Assert.Equal(0, controller.ClearCalls);
        presenter.RequestClear();
        await presenter.ConfirmClearAsync();

        Assert.Equal(1, controller.ClearCalls);
        Assert.True(controller.LastClear?.Confirmed);
        Assert.Empty(presenter.State.Items);
        Assert.Equal("ui.clipboard_shelf.cleared", presenter.State.Announcement?.MessageKey);
    }

    private static BrowsingContext Browsing(BrowserProfileMode mode) =>
        new(
            new PrivacyContext(
                new ProfileId(Guid.NewGuid()),
                new BrowserSessionId(Guid.NewGuid()),
                mode),
            new BrowserWindowId(Guid.NewGuid()),
            new BrowserTabId(Guid.NewGuid()),
            null);

    private sealed class FakeClipboardShelfController : IClipboardShelfController
    {
        private readonly ClipboardShelfReadModel model;

        public FakeClipboardShelfController(ClipboardShelfReadModel model)
        {
            this.model = model;
        }

        public int UseCalls { get; private set; }

        public int ClearCalls { get; private set; }

        public ClearClipboardShelfIntent? LastClear { get; private set; }

        public ValueTask<ControllerResult<ClipboardShelfReadModel>> GetAsync(
            BrowsingContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ControllerResult<ClipboardShelfReadModel>.Success(model));

        public ValueTask<ControllerResult<ClipboardShelfUseReceipt>> UseAsync(
            UseClipboardShelfItemIntent intent,
            CancellationToken cancellationToken)
        {
            UseCalls++;
            return ValueTask.FromResult(ControllerResult<ClipboardShelfUseReceipt>.Success(
                new ClipboardShelfUseReceipt(
                    intent.ItemId,
                    intent.Context.TabId,
                    DateTimeOffset.UtcNow)));
        }

        public ValueTask<ControllerResult> ClearAsync(
            ClearClipboardShelfIntent intent,
            CancellationToken cancellationToken)
        {
            ClearCalls++;
            LastClear = intent;
            return ValueTask.FromResult(ControllerResult.Success());
        }
    }
}
