using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Privacy;
using OrbitNavigator.Privacy.Clipboard;
using Xunit;

namespace OrbitNavigator.Privacy.Tests.Clipboard;

public sealed class ClipboardShelfControllerTests
{
    [Fact]
    public async Task PrivateReadAndMutationsMakeZeroProviderOrPlatformCalls()
    {
        var leases = new FakeLeases();
        var target = new FakeTarget();
        await using var controller = new ClipboardShelfController(leases, target, new FakeClock());
        var context = Browsing(BrowserProfileMode.Private);

        var read = await controller.GetAsync(context, default);
        var capture = await controller.CaptureAsync(Capture(context), default);
        var use = await controller.UseAsync(
            new UseClipboardShelfItemIntent(
                context,
                new ClipboardShelfItemId(context.Privacy.ProfileId, Guid.NewGuid())),
            default);

        Assert.True(read.IsSuccess);
        Assert.Equal(ClipboardShelfAvailability.Unavailable, read.Value!.Availability);
        Assert.Equal(ClipboardShelfUnavailableReason.PrivateMode, read.Value.UnavailableReason);
        Assert.Empty(read.Value.Items);
        Assert.Equal(ControllerErrorCode.PolicyDenied, capture.Error?.Code);
        Assert.Equal(ControllerErrorCode.PolicyDenied, use.Error?.Code);
        Assert.Equal(0, leases.Calls);
        Assert.Equal(0, target.Calls);
    }

    [Fact]
    public async Task ShelfKeepsNewestTenItemsPerProfile()
    {
        var leases = new FakeLeases();
        var target = new FakeTarget();
        var clock = new FakeClock();
        await using var controller = new ClipboardShelfController(leases, target, clock);
        var context = Browsing(BrowserProfileMode.Normal);

        for (var index = 0; index < 11; index++)
        {
            leases.Next = $"item-{index}";
            var captured = await controller.CaptureAsync(
                Capture(context, clock.UtcNow.AddSeconds(index)),
                default);
            Assert.True(captured.IsSuccess);
        }

        var read = await controller.GetAsync(context, default);

        Assert.True(read.IsSuccess);
        Assert.Equal(ClipboardShelfReadModel.MaximumCapacity, read.Value!.Capacity);
        Assert.Equal(10, read.Value.Items.Count);
        Assert.Equal("item-10", read.Value.Items[0].DisplayPreview);
        Assert.DoesNotContain(read.Value.Items, item => item.DisplayPreview == "item-0");
        Assert.All(leases.Issued, content => Assert.True(content.IsDisposed));
    }

    [Fact]
    public async Task UseWritesThroughPrivacyTargetWithoutReturningContent()
    {
        var leases = new FakeLeases { Next = "private-by-reference" };
        var target = new FakeTarget();
        var clock = new FakeClock();
        await using var controller = new ClipboardShelfController(leases, target, clock);
        var context = Browsing(BrowserProfileMode.Normal);
        Assert.True((await controller.CaptureAsync(Capture(context), default)).IsSuccess);
        var item = (await controller.GetAsync(context, default)).Value!.Items.Single();

        var used = await controller.UseAsync(
            new UseClipboardShelfItemIntent(context, item.Id),
            default);

        Assert.True(used.IsSuccess);
        Assert.Equal(context.TabId, used.Value!.DestinationTabId);
        Assert.Equal("private-by-reference", target.LastValue);
        Assert.Equal(1, target.Calls);
        Assert.DoesNotContain(
            typeof(ClipboardShelfUseReceipt).GetProperties(),
            property => property.PropertyType == typeof(string));
    }

    [Fact]
    public async Task CrossProfileItemCannotBeReadThroughUseTarget()
    {
        var leases = new FakeLeases();
        var target = new FakeTarget();
        await using var controller = new ClipboardShelfController(leases, target, new FakeClock());
        var context = Browsing(BrowserProfileMode.Normal);
        var other = Browsing(BrowserProfileMode.Normal);

        var result = await controller.UseAsync(
            new UseClipboardShelfItemIntent(
                context,
                new ClipboardShelfItemId(other.Privacy.ProfileId, Guid.NewGuid())),
            default);

        Assert.False(result.IsSuccess);
        Assert.Equal(ControllerErrorCode.InvalidRequest, result.Error?.Code);
        Assert.Equal(0, target.Calls);
    }

    [Fact]
    public async Task ClearRequiresConfirmationAndOnlyClearsCurrentProfile()
    {
        var leases = new FakeLeases();
        await using var controller = new ClipboardShelfController(leases, new FakeTarget(), new FakeClock());
        var first = Browsing(BrowserProfileMode.Normal);
        var second = Browsing(BrowserProfileMode.Normal);
        Assert.True((await controller.CaptureAsync(Capture(first), default)).IsSuccess);
        Assert.True((await controller.CaptureAsync(Capture(second), default)).IsSuccess);

        var rejected = await controller.ClearAsync(new ClearClipboardShelfIntent(first, false), default);
        var cleared = await controller.ClearAsync(new ClearClipboardShelfIntent(first, true), default);

        Assert.Equal(ControllerErrorCode.InvalidRequest, rejected.Error?.Code);
        Assert.True(cleared.IsSuccess);
        Assert.Empty((await controller.GetAsync(first, default)).Value!.Items);
        Assert.Single((await controller.GetAsync(second, default)).Value!.Items);
    }

    [Fact]
    public async Task DisposalIsIdempotent()
    {
        var controller = new ClipboardShelfController(new FakeLeases(), new FakeTarget(), new FakeClock());

        await controller.DisposeAsync();
        await controller.DisposeAsync();
    }

    private static ClipboardShelfCaptureRequest Capture(
        BrowsingContext context,
        DateTimeOffset? capturedAtUtc = null) =>
        new(
            context,
            new ClipboardCaptureLeaseId(context.Privacy.ProfileId, Guid.NewGuid()),
            ClipboardShelfContentKind.Text,
            ClipboardCaptureClassification.ExplicitUserInitiatedNonSensitive,
            context.CurrentSite,
            capturedAtUtc ?? DateTimeOffset.Parse("2026-08-08T12:00:00Z"));

    private static BrowsingContext Browsing(BrowserProfileMode mode) =>
        new(
            new PrivacyContext(
                new ProfileId(Guid.NewGuid()),
                new BrowserSessionId(Guid.NewGuid()),
                mode),
            new BrowserWindowId(Guid.NewGuid()),
            new BrowserTabId(Guid.NewGuid()),
            SiteIdentity.Create(new Uri("https://example.test")).Value);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = DateTimeOffset.Parse("2026-08-08T12:00:30Z");
    }

    private sealed class FakeLeases : IClipboardCaptureLeaseProvider
    {
        public int Calls { get; private set; }
        public string Next { get; set; } = "value";
        public List<SensitiveClipboardContent> Issued { get; } = [];

        public ValueTask<ControllerResult<SensitiveClipboardContent>> RedeemAsync(
            BrowsingContext context,
            ClipboardCaptureLeaseId leaseId,
            CancellationToken cancellationToken)
        {
            Calls++;
            var created = SensitiveClipboardContent.Create(Next.AsSpan());
            if (created.IsSuccess)
            {
                Issued.Add(created.Value!);
            }

            return ValueTask.FromResult(created);
        }
    }

    private sealed class FakeTarget : IClipboardShelfUseTarget
    {
        public int Calls { get; private set; }
        public string? LastValue { get; private set; }

        public ValueTask<ControllerResult> WriteAsync(
            BrowsingContext destination,
            ClipboardShelfContentKind kind,
            SensitiveClipboardContent content,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastValue = new string(content.Characters.Span);
            return ValueTask.FromResult(ControllerResult.Success());
        }
    }
}
