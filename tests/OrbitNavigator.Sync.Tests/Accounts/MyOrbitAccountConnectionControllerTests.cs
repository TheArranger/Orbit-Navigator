using System.Text;
using OrbitNavigator.Contracts.Accounts;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Sync;
using OrbitNavigator.Sync.Accounts;
using Xunit;

namespace OrbitNavigator.Sync.Tests.Accounts;

public sealed class MyOrbitAccountConnectionControllerTests
{
    [Fact]
    public async Task PrivateContextIsRejectedBeforeVaultProviderLoopbackOrLauncher()
    {
        var rig = new Rig();
        var context = Context(BrowserProfileMode.Private);

        var query = await rig.Controller.QueryAsync(new(context, Operation(), MyOrbitAccountRevision.Initial));
        var begin = await rig.Controller.BeginExternalLinkAsync(new(
            context, Operation(), MyOrbitAccountRevision.Initial, "Private PC"));
        var cancel = await rig.Controller.CancelPendingLinkAsync(new(
            context, Operation(), MyOrbitAccountRevision.Initial, new MyOrbitLinkAttemptId(Guid.NewGuid())));
        var disconnect = await rig.Controller.DisconnectCurrentDeviceAsync(new(
            context, Operation(), MyOrbitAccountRevision.Initial));
        var devices = await rig.Controller.QueryDevicesAsync(new(
            context, Operation(), MyOrbitAccountRevision.Initial));
        var revoke = await rig.Controller.RevokeDeviceAsync(new(
            context, Operation(), MyOrbitAccountRevision.Initial, new DeviceId(Guid.NewGuid())));

        Assert.Equal(ControllerErrorCode.PolicyDenied, query.Error?.Code);
        Assert.Equal(ControllerErrorCode.PolicyDenied, begin.Error?.Code);
        Assert.All(new[] { cancel.Error, disconnect.Error, devices.Error, revoke.Error },
            error => Assert.Equal(ControllerErrorCode.PolicyDenied, error?.Code));
        Assert.Equal(0, rig.Vault.Calls);
        Assert.Equal(0, rig.Protocol.Calls);
        Assert.Equal(0, rig.Loopback.BindCalls);
        Assert.Equal(0, rig.Launcher.Calls);
    }

    [Fact]
    public async Task ProviderUnavailableIsUiSafeAndLeavesLocalBrowsingAvailable()
    {
        var controller = MyOrbitAccountConnectionController.ProviderUnavailable();
        var context = Context();

        var result = await controller.QueryAsync(new(context, Operation(), MyOrbitAccountRevision.Initial));

        Assert.True(result.IsSuccess);
        Assert.Equal(MyOrbitAccountConnectionStateKind.ProviderUnavailable, result.Value?.State);
        Assert.True(result.Value?.LocalBrowsingAvailable);
        Assert.Equal(MyOrbitAccountCapabilities.None, result.Value?.Capabilities);
        Assert.Empty(result.Value!.GrantedScopes);
    }

    [Fact]
    public async Task CancelledNormalOperationReturnsTypedResult()
    {
        var rig = new Rig();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await rig.Controller.QueryAsync(new(
            Context(), Operation(), MyOrbitAccountRevision.Initial), cancellation.Token);

        Assert.Equal(ControllerErrorCode.Cancelled, result.Error?.Code);
    }

    [Fact]
    public async Task BeginUsesExternalBrowserAndCompletesOnlyAfterStateBoundCallbackAndVaultSave()
    {
        var rig = new Rig();
        var context = Context();
        var initial = await rig.Controller.QueryAsync(new(context, Operation(), MyOrbitAccountRevision.Initial));

        var begun = await rig.Controller.BeginExternalLinkAsync(new(
            context,
            Operation(),
            initial.Value!.Revision,
            "Test PC"));

        Assert.True(begun.IsSuccess);
        Assert.Equal(MyOrbitAccountConnectionStateKind.LinkPending, begun.Value?.Status.State);
        Assert.Equal(1, rig.Launcher.Calls);
        Assert.NotNull(rig.Protocol.PushedRequest);
        Assert.Equal(43, rig.Protocol.PushedRequest!.CodeChallenge.Length);
        Assert.DoesNotContain("state", rig.Launcher.LastUri!.Query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("scope", rig.Launcher.LastUri.Query, StringComparison.OrdinalIgnoreCase);

        rig.Listener.Complete(rig.Protocol.PushedRequest.State);
        var connected = await PollAsync(rig.Controller, context, begun.Value!.Status.Revision,
            MyOrbitAccountConnectionStateKind.Connected);

        Assert.Equal(MyOrbitAccountConnectionStateKind.Connected, connected.State);
        Assert.Equal(
            [MyOrbitAccountScope.LinkAccount, MyOrbitAccountScope.ManageLinkedDevices],
            connected.GrantedScopes);
        Assert.Equal(1, rig.Protocol.ExchangeCalls);
        Assert.Equal(1, rig.Vault.SaveCalls);
        Assert.Equal("orbit-user", connected.AccountLabel);
    }

    [Fact]
    public async Task CallbackStateMismatchFailsClosedWithoutCodeExchangeOrVaultWrite()
    {
        var rig = new Rig();
        var context = Context();
        var initial = await rig.Controller.QueryAsync(new(context, Operation(), MyOrbitAccountRevision.Initial));
        var begun = await rig.Controller.BeginExternalLinkAsync(new(
            context, Operation(), initial.Value!.Revision, "Test PC"));

        rig.Listener.Complete("x".PadLeft(43, 'x'));
        var failed = await PollAsync(rig.Controller, context, begun.Value!.Status.Revision,
            MyOrbitAccountConnectionStateKind.Failed);

        Assert.Equal(MyOrbitAccountConnectionStateKind.Failed, failed.State);
        Assert.Equal(0, rig.Protocol.ExchangeCalls);
        Assert.Equal(0, rig.Vault.SaveCalls);
    }

    [Fact]
    public async Task CancelRequiresCurrentRevisionAndAttemptAndPreventsCompletion()
    {
        var rig = new Rig();
        var context = Context();
        var initial = await rig.Controller.QueryAsync(new(context, Operation(), MyOrbitAccountRevision.Initial));
        var begun = await rig.Controller.BeginExternalLinkAsync(new(
            context, Operation(), initial.Value!.Revision, "Test PC"));
        var stale = await rig.Controller.CancelPendingLinkAsync(new(
            context, Operation(), initial.Value.Revision, begun.Value!.AttemptId));
        Assert.Equal(ControllerErrorCode.Conflict, stale.Error?.Code);

        var cancelled = await rig.Controller.CancelPendingLinkAsync(new(
            context, Operation(), begun.Value.Status.Revision, begun.Value.AttemptId));

        Assert.True(cancelled.IsSuccess);
        Assert.Equal(MyOrbitAccountConnectionStateKind.SignedOut, cancelled.Value?.State);
        Assert.Equal(0, rig.Protocol.ExchangeCalls);
        Assert.Equal(0, rig.Vault.SaveCalls);
    }

    [Fact]
    public async Task CancelRacingWithCodeExchangeNeverPersistsAndRevokesOrphanGrant()
    {
        var rig = new Rig();
        rig.Protocol.HoldExchange = true;
        var context = Context();
        var initial = await rig.Controller.QueryAsync(new(context, Operation(), MyOrbitAccountRevision.Initial));
        var begun = await rig.Controller.BeginExternalLinkAsync(new(
            context, Operation(), initial.Value!.Revision, "Test PC"));
        rig.Listener.Complete(rig.Protocol.PushedRequest!.State);
        await rig.Protocol.ExchangeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var cancelled = await rig.Controller.CancelPendingLinkAsync(new(
            context, Operation(), begun.Value!.Status.Revision, begun.Value.AttemptId));
        rig.Protocol.ReleaseExchange.TrySetResult();

        for (var attempt = 0; attempt < 100 && rig.Protocol.RevokeCalls == 0; attempt++)
            await Task.Delay(10);
        Assert.True(cancelled.IsSuccess);
        Assert.Equal(MyOrbitAccountConnectionStateKind.SignedOut, cancelled.Value?.State);
        Assert.Equal(0, rig.Vault.SaveCalls);
        Assert.Equal(1, rig.Protocol.RevokeCalls);
    }

    [Fact]
    public async Task DeviceQueryRotatesRefreshCredentialBeforeListingAndDoesNotEnableSync()
    {
        var rig = new Rig(linked: true);
        var context = Context();
        var status = await rig.Controller.QueryAsync(new(context, Operation(), MyOrbitAccountRevision.Initial));

        var devices = await rig.Controller.QueryDevicesAsync(new(
            context, Operation(), status.Value!.Revision));

        Assert.True(devices.IsSuccess);
        Assert.Equal(1, rig.Protocol.RefreshCalls);
        Assert.Equal(1, rig.Vault.SaveCalls);
        Assert.Equal(1, rig.Protocol.ListCalls);
        Assert.Single(devices.Value!.Devices);
        Assert.Equal(MyOrbitAccountConnectionStateKind.Connected, devices.Value.Status.State);
        Assert.DoesNotContain(devices.Value.Status.GrantedScopes,
            scope => Enum.GetName(scope)!.Contains("History", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailedDisconnectPersistsRevocationOutboxAndLaterQueryRetries()
    {
        var rig = new Rig(linked: true);
        var context = Context();
        var status = await rig.Controller.QueryAsync(new(context, Operation(), MyOrbitAccountRevision.Initial));
        rig.Protocol.RevokeResults.Enqueue(ControllerResult.Failure(ControllerError.Create(
            ControllerErrorCode.Unavailable, "account.link.provider-unavailable", isRetryable: true)));
        rig.Protocol.RevokeResults.Enqueue(ControllerResult.Success());

        var disconnected = await rig.Controller.DisconnectCurrentDeviceAsync(new(
            context, Operation(), status.Value!.Revision));

        Assert.True(disconnected.IsSuccess);
        Assert.Equal(MyOrbitAccountConnectionStateKind.Failed, disconnected.Value?.State);
        Assert.Equal(1, rig.Vault.MarkPendingCalls);

        var retried = await rig.Controller.QueryAsync(new(
            context, Operation(), disconnected.Value!.Revision));
        Assert.Equal(MyOrbitAccountConnectionStateKind.Revoked, retried.Value?.State);
        Assert.Equal(1, rig.Vault.DeleteCalls);
    }

    [Fact]
    public async Task DisposalIsIdempotentAndClearsLoadedRefreshCredential()
    {
        var rig = new Rig(linked: true);
        var loaded = rig.Vault.Loaded!;
        _ = await rig.Controller.QueryAsync(new(Context(), Operation(), MyOrbitAccountRevision.Initial));

        await rig.Controller.DisposeAsync();
        await rig.Controller.DisposeAsync();

        Assert.True(loaded.RefreshCredential.Bytes.IsEmpty);
        Assert.True(rig.Protocol.Disposed);
    }

    private static async Task<MyOrbitAccountConnectionState> PollAsync(
        IMyOrbitAccountConnectionController controller,
        BrowsingContext context,
        MyOrbitAccountRevision revision,
        MyOrbitAccountConnectionStateKind expected)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var result = await controller.QueryAsync(new(context, Operation(), revision));
            if (result.IsSuccess && result.Value!.State == expected)
                return result.Value;
            await Task.Delay(10);
        }
        throw new TimeoutException($"State did not become {expected}.");
    }

    private static BrowsingContext Context(BrowserProfileMode mode = BrowserProfileMode.Normal) => new(
        new PrivacyContext(new ProfileId(Guid.NewGuid()), new BrowserSessionId(Guid.NewGuid()), mode),
        new BrowserWindowId(Guid.NewGuid()),
        new BrowserTabId(Guid.NewGuid()),
        null);

    private static SyncOperationId Operation() => new(Guid.NewGuid());

    private sealed class Rig
    {
        public Rig(bool linked = false)
        {
            if (linked) Vault.Loaded = Credential();
            Controller = new MyOrbitAccountConnectionController(
                Protocol, Vault, Launcher, Loopback, Clock);
        }
        public TestClock Clock { get; } = new();
        public FakeProtocol Protocol { get; } = new();
        public FakeVault Vault { get; } = new();
        public FakeLauncher Launcher { get; } = new();
        public FakeLoopbackFactory Loopback { get; } = new();
        public FakeLoopbackListener Listener => Loopback.Listener;
        public MyOrbitAccountConnectionController Controller { get; }
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class FakeLauncher : IMyOrbitSystemBrowserLauncher
    {
        public int Calls { get; private set; }
        public Uri? LastUri { get; private set; }
        public ValueTask<ControllerResult> LaunchAsync(Uri authorizationRequest, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastUri = authorizationRequest;
            return ValueTask.FromResult(ControllerResult.Success());
        }
    }

    private sealed class FakeLoopbackFactory : IMyOrbitLoopbackListenerFactory
    {
        public int BindCalls { get; private set; }
        public FakeLoopbackListener Listener { get; } = new();
        public ControllerResult<IMyOrbitLoopbackListener> Bind()
        {
            BindCalls++;
            return ControllerResult<IMyOrbitLoopbackListener>.Success(Listener);
        }
    }

    private sealed class FakeLoopbackListener : IMyOrbitLoopbackListener
    {
        private readonly TaskCompletionSource<ControllerResult<MyOrbitLoopbackCallback>> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Uri RedirectUri { get; } = new("http://127.0.0.1:49152/my-orbit/callback");
        public void Complete(string state) => completion.TrySetResult(
            ControllerResult<MyOrbitLoopbackCallback>.Success(new(
                state,
                new SensitiveUtf8Buffer(Token("moac_")),
                null)));
        public async ValueTask<ControllerResult<MyOrbitLoopbackCallback>> ReceiveAsync(CancellationToken cancellationToken)
        {
            try { return await completion.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException)
            {
                return ControllerResult<MyOrbitLoopbackCallback>.Failure(ControllerError.Create(
                    ControllerErrorCode.Cancelled, "account.link.cancelled"));
            }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeProtocol : IMyOrbitAuthorizationProtocol
    {
        public int Calls { get; private set; }
        public int ExchangeCalls { get; private set; }
        public int RefreshCalls { get; private set; }
        public int ListCalls { get; private set; }
        public int RevokeCalls { get; private set; }
        public bool HoldExchange { get; set; }
        public TaskCompletionSource ExchangeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseExchange { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public MyOrbitPushedAuthorizationRequest? PushedRequest { get; private set; }
        public Queue<ControllerResult> RevokeResults { get; } = new();
        public bool Disposed { get; private set; }
        public ValueTask<ControllerResult<MyOrbitPushedAuthorization>> PushAuthorizationAsync(
            MyOrbitPushedAuthorizationRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            PushedRequest = request;
            return ValueTask.FromResult(ControllerResult<MyOrbitPushedAuthorization>.Success(new(
                new Uri("https://my-orbit.example/oauth2/authorize?client_id=orbit-navigator&request_uri=opaque"),
                DateTimeOffset.UtcNow.AddMinutes(10))));
        }
        public async ValueTask<ControllerResult<MyOrbitTokenSet>> ExchangeCodeAsync(
            MyOrbitCodeExchangeRequest request, CancellationToken cancellationToken)
        {
            Calls++; ExchangeCalls++;
            ExchangeStarted.TrySetResult();
            if (HoldExchange)
                await ReleaseExchange.Task;
            return ControllerResult<MyOrbitTokenSet>.Success(Tokens());
        }
        public ValueTask<ControllerResult<MyOrbitTokenSet>> RefreshAsync(
            SensitiveUtf8Buffer refreshCredential, CancellationToken cancellationToken)
        {
            Calls++; RefreshCalls++;
            return ValueTask.FromResult(ControllerResult<MyOrbitTokenSet>.Success(Tokens()));
        }
        public ValueTask<ControllerResult> RevokeAsync(
            SensitiveUtf8Buffer credential, CancellationToken cancellationToken)
        {
            Calls++; RevokeCalls++;
            return ValueTask.FromResult(RevokeResults.Count == 0 ? ControllerResult.Success() : RevokeResults.Dequeue());
        }
        public ValueTask<ControllerResult<MyOrbitProtocolDeviceList>> ListDevicesAsync(
            SensitiveUtf8Buffer accessCredential, CancellationToken cancellationToken)
        {
            Calls++; ListCalls++;
            return ValueTask.FromResult(ControllerResult<MyOrbitProtocolDeviceList>.Success(new(
                "orbit-user",
                [new(new DeviceId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
                    "Test PC", true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null)])));
        }
        public ValueTask<ControllerResult> RevokeDeviceAsync(
            SensitiveUtf8Buffer accessCredential, DeviceId deviceId, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(ControllerResult.Success());
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeVault : IMyOrbitCredentialVault
    {
        public int Calls { get; private set; }
        public int SaveCalls { get; private set; }
        public int MarkPendingCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public StoredMyOrbitCredential? Loaded { get; set; }
        public ValueTask<ControllerResult<StoredMyOrbitCredential>> LoadAsync(SyncOperationContext context, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(Loaded is null
                ? ControllerResult<StoredMyOrbitCredential>.Failure(ControllerError.Create(
                    ControllerErrorCode.NotFound, "error.profile_storage.not_found"))
                : ControllerResult<StoredMyOrbitCredential>.Success(Loaded));
        }
        public ValueTask<ControllerResult<StoredMyOrbitCredential>> SaveAsync(
            SyncOperationContext context, StoredMyOrbitCredential? expected, MyOrbitTokenSet tokens,
            bool revocationPending, CancellationToken cancellationToken)
        {
            Calls++; SaveCalls++;
            Loaded = FromTokens(tokens, revocationPending);
            return ValueTask.FromResult(ControllerResult<StoredMyOrbitCredential>.Success(Loaded));
        }
        public ValueTask<ControllerResult<StoredMyOrbitCredential>> MarkRevocationPendingAsync(
            SyncOperationContext context, StoredMyOrbitCredential expected, CancellationToken cancellationToken)
        {
            Calls++; MarkPendingCalls++;
            Loaded = new(expected.RefreshCredential.Clone(), expected.ConnectionId, expected.AccountLabel,
                expected.AuthorizedAtUtc, expected.LastRefreshedAtUtc, expected.RefreshExpiresAtUtc, true,
                new ProfileStorageRevision(Guid.NewGuid()));
            return ValueTask.FromResult(ControllerResult<StoredMyOrbitCredential>.Success(Loaded));
        }
        public ValueTask<ControllerResult> DeleteAsync(
            SyncOperationContext context, StoredMyOrbitCredential expected, CancellationToken cancellationToken)
        {
            Calls++; DeleteCalls++; Loaded = null;
            return ValueTask.FromResult(ControllerResult.Success());
        }
    }

    private static MyOrbitTokenSet Tokens() => new(
        new SensitiveUtf8Buffer(Token("moat_")),
        new SensitiveUtf8Buffer(Token("mort_")),
        new DeviceId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
        "orbit-user",
        DateTimeOffset.UtcNow.AddMinutes(10),
        DateTimeOffset.UtcNow.AddDays(30),
        DateTimeOffset.UtcNow);

    private static StoredMyOrbitCredential Credential() => new(
        new SensitiveUtf8Buffer(Token("mort_")),
        new DeviceId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
        "orbit-user",
        DateTimeOffset.UtcNow.AddDays(-1),
        DateTimeOffset.UtcNow.AddDays(-1),
        DateTimeOffset.UtcNow.AddDays(30),
        false,
        new ProfileStorageRevision(Guid.NewGuid()));

    private static StoredMyOrbitCredential FromTokens(MyOrbitTokenSet tokens, bool pending) => new(
        tokens.RefreshCredential.Clone(), tokens.ConnectionId, tokens.AccountLabel,
        tokens.IssuedAtUtc, tokens.IssuedAtUtc, tokens.RefreshExpiresAtUtc, pending,
        new ProfileStorageRevision(Guid.NewGuid()));

    private static byte[] Token(string prefix) => Encoding.ASCII.GetBytes(prefix + new string('A', 43));
}
