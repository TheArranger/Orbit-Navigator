using Microsoft.Web.WebView2.Core;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Privacy;
using OrbitNavigator.WebViewHost.Permissions;
using Xunit;

namespace OrbitNavigator.WebViewHost.Tests.Permissions;

public sealed class PermissionHostMappingTests
{
    [Fact]
    public void AutoplayAndPopupMapToTypedOnDemandCapabilities()
    {
        Assert.Equal(
            WebPermissionCapability.Autoplay,
            WebPermissionCapabilityMapper.Map(CoreWebView2PermissionKind.Autoplay));
        Assert.Equal(WebPermissionCapability.Popups, WebPermissionCapabilityMapper.Popup);
        Assert.Equal(
            WebPermissionCapability.Unknown,
            WebPermissionCapabilityMapper.Map(CoreWebView2PermissionKind.UnknownPermission));
    }

    [Fact]
    public async Task CompletionIsOneUseAndNeverSavedInWebViewProfile()
    {
        var registry = new PermissionCompletionRegistry();
        var requestId = new RequestId(Guid.NewGuid());
        var tabId = new BrowserTabId(Guid.NewGuid());
        PermissionHostCompletion? delivered = null;
        var registration = registry.Register(new PendingPermissionRegistration(
            requestId,
            tabId,
            WebPermissionCapability.Autoplay,
            DateTimeOffset.UtcNow.AddMinutes(1),
            completion => delivered = completion));
        var completion = PermissionHostCompletion.FromAcceptedResponse(
            requestId,
            tabId,
            WebPermissionCapability.Autoplay,
            PermissionDecision.Allow);

        var first = await registry.CompleteAsync(completion, CancellationToken.None);
        var replay = await registry.CompleteAsync(completion, CancellationToken.None);

        Assert.True(registration.IsSuccess);
        Assert.True(first.IsSuccess);
        Assert.False(replay.IsSuccess);
        Assert.False(delivered?.SaveInProfile);
        Assert.Equal(PermissionHostDisposition.Allow, delivered?.Disposition);
    }

    [Fact]
    public void ExpiredPromptCompletesFailClosed()
    {
        var registry = new PermissionCompletionRegistry();
        var now = DateTimeOffset.UtcNow;
        PermissionHostCompletion? delivered = null;
        registry.Register(new PendingPermissionRegistration(
            new RequestId(Guid.NewGuid()),
            new BrowserTabId(Guid.NewGuid()),
            WebPermissionCapability.Popups,
            now.AddSeconds(-1),
            completion => delivered = completion));

        var count = registry.Expire(now);

        Assert.Equal(1, count);
        Assert.True(delivered?.IsFailClosed);
        Assert.Equal(PermissionHostDisposition.Deny, delivered?.Disposition);
        Assert.Equal(PermissionDecisionSource.RequestExpired, delivered?.Source);
        Assert.False(delivered?.SaveInProfile);
    }

    [Fact]
    public async Task PersistentCompletionFromWorkerRunsOnOwningDispatcher()
    {
        var ready = new TaskCompletionSource<DispatcherPermissionCompletion>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        System.Windows.Threading.Dispatcher? ownerDispatcher = null;
        var abandonDispatcher = 0;
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                Volatile.Write(ref ownerDispatcher, dispatcher);
                var dispatcherCompletion = new DispatcherPermissionCompletion(
                    dispatcher,
                    _ =>
                    {
                        applied.TrySetResult(Environment.CurrentManagedThreadId);
                        dispatcher.BeginInvokeShutdown(
                            System.Windows.Threading.DispatcherPriority.Send);
                    });
                ready.TrySetResult(dispatcherCompletion);
                if (Volatile.Read(ref abandonDispatcher) == 0)
                {
                    System.Windows.Threading.Dispatcher.Run();
                }
            }
            catch (Exception exception)
            {
                ready.TrySetException(exception);
                applied.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "Permission completion dispatcher test",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        try
        {
            var dispatcherCompletion = await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var ownerThreadId = thread.ManagedThreadId;
            var completion = PermissionHostCompletion.FromAcceptedResponse(
                new RequestId(Guid.NewGuid()),
                new BrowserTabId(Guid.NewGuid()),
                WebPermissionCapability.Geolocation,
                PermissionDecision.Allow,
                source: PermissionDecisionSource.UserResponse);
            await Task.Run(() => dispatcherCompletion.Complete(completion))
                .WaitAsync(TimeSpan.FromSeconds(30));

            var appliedThreadId = await applied.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.NotEqual(Environment.CurrentManagedThreadId, ownerThreadId);
            Assert.Equal(ownerThreadId, appliedThreadId);
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        }
        finally
        {
            Interlocked.Exchange(ref abandonDispatcher, 1);
            var dispatcher = Volatile.Read(ref ownerDispatcher);
            if (dispatcher is not null && !dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
            {
                dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Send);
            }
            thread.Join(TimeSpan.FromSeconds(30));
        }
    }

    [Fact]
    public void ExistingPersistentRuleCompletesWithoutWebViewProfileStorage()
    {
        var profile = new ProfileId(Guid.NewGuid());
        var session = new BrowserSessionId(Guid.NewGuid());
        var site = SiteIdentity.Create(new Uri("https://permission.test/path")).Value!;
        var context = new BrowsingContext(
            new PrivacyContext(profile, session, BrowserProfileMode.Normal),
            new BrowserWindowId(Guid.NewGuid()),
            new BrowserTabId(Guid.NewGuid()),
            site);
        var request = new PermissionBrokerRequest(
            new RequestId(Guid.NewGuid()),
            new ResponseToken(Guid.NewGuid()),
            context,
            site,
            WebPermissionCapability.Geolocation,
            [PermissionAllowScope.Once, PermissionAllowScope.Session, PermissionAllowScope.Persistent],
            PermissionDecision.Ask,
            PermissionDecision.Deny,
            true,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddSeconds(30));
        var rule = new PermissionRule(
            new PermissionRuleId(profile, Guid.NewGuid()),
            profile,
            null,
            site,
            WebPermissionCapability.Geolocation,
            PermissionDecision.Allow,
            PermissionAllowScope.Persistent,
            DateTimeOffset.UtcNow,
            null);

        var completion = ExistingPermissionRuleResolver.Resolve(
            request,
            new SitePermissionState(context, site, [rule]));

        Assert.NotNull(completion);
        Assert.Equal(PermissionHostDisposition.Allow, completion.Disposition);
        Assert.Equal(PermissionDecisionSource.ExistingRule, completion.Source);
        Assert.False(completion.SaveInProfile);
        Assert.Equal(rule.Id, completion.AppliedRule?.Id);
    }

    [Fact]
    public void ExistingRuleResolutionRejectsOnceAndPrivatePersistentRules()
    {
        var profile = new ProfileId(Guid.NewGuid());
        var session = new BrowserSessionId(Guid.NewGuid());
        var site = SiteIdentity.Create(new Uri("https://permission.test")).Value!;
        var normal = new BrowsingContext(
            new PrivacyContext(profile, session, BrowserProfileMode.Normal),
            new BrowserWindowId(Guid.NewGuid()),
            new BrowserTabId(Guid.NewGuid()),
            site);
        var request = new PermissionBrokerRequest(
            new RequestId(Guid.NewGuid()),
            new ResponseToken(Guid.NewGuid()),
            normal,
            site,
            WebPermissionCapability.Geolocation,
            [PermissionAllowScope.Once],
            PermissionDecision.Ask,
            PermissionDecision.Deny,
            true,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddSeconds(30));
        PermissionRule Rule(PermissionAllowScope scope) => new(
            new PermissionRuleId(profile, Guid.NewGuid()),
            profile,
            scope == PermissionAllowScope.Persistent ? null : session,
            site,
            WebPermissionCapability.Geolocation,
            PermissionDecision.Allow,
            scope,
            DateTimeOffset.UtcNow,
            request.ExpiresAtUtc);

        Assert.Null(ExistingPermissionRuleResolver.Resolve(
            request,
            new SitePermissionState(normal, site, [Rule(PermissionAllowScope.Once)])));

        var privateContext = normal with
        {
            Privacy = normal.Privacy with { Mode = BrowserProfileMode.Private },
        };
        Assert.Null(ExistingPermissionRuleResolver.Resolve(
            request with { Context = privateContext },
            new SitePermissionState(
                privateContext,
                site,
                [Rule(PermissionAllowScope.Persistent)])));
    }
}
