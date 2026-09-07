using System.Collections.Concurrent;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;

namespace OrbitNavigator.Foundation.Profiles;

public sealed class WebViewProfileLifecycle : IWebViewProfileLifecycle
{
    private static readonly TimeSpan PrivateDeleteRetryWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PrivateDeleteRetryDelay = TimeSpan.FromMilliseconds(100);

    private readonly string _root;
    private readonly ConcurrentDictionary<(ProfileId, BrowserSessionId), int> _privateLeases = new();

    public WebViewProfileLifecycle(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Path.IsPathFullyQualified(root))
        {
            throw new ArgumentException("WebView profile root must be absolute.", nameof(root));
        }

        _root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
    }

    public ValueTask<ControllerResult<IWebViewProfileLease>> AcquireAsync(
        PrivacyContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);
        if (!context.IsStructurallyValid)
        {
            return ValueTask.FromResult(ControllerResult<IWebViewProfileLease>.Failure(
                ControllerError.Create(
                    ControllerErrorCode.InvalidRequest,
                    "error.webview_profile.context_invalid")));
        }

        var path = Resolve(context);
        Directory.CreateDirectory(path);
        var descriptorResult = WebViewProfileDescriptor.Create(
            context,
            context.IsPrivate ? $"private-{context.SessionId.Value:N}" : $"profile-{context.ProfileId.Value:N}",
            path);
        if (!descriptorResult.IsSuccess)
        {
            return ValueTask.FromResult(ControllerResult<IWebViewProfileLease>.Failure(
                descriptorResult.Error!));
        }

        if (context.IsPrivate)
        {
            _privateLeases.AddOrUpdate(
                (context.ProfileId, context.SessionId),
                1,
                static (_, current) => checked(current + 1));
        }

        return ValueTask.FromResult(ControllerResult<IWebViewProfileLease>.Success(
            new Lease(this, descriptorResult.Value!)));
    }

    public async ValueTask<ControllerResult<WebViewProfileSessionEndReceipt>> EndSessionAsync(
        PrivacyContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);
        if (!context.IsStructurallyValid)
        {
            return ControllerResult<WebViewProfileSessionEndReceipt>.Failure(
                ControllerError.Create(
                    ControllerErrorCode.InvalidRequest,
                    "error.webview_profile.context_invalid"));
        }

        var deleted = false;
        if (context.IsPrivate)
        {
            _privateLeases.TryRemove((context.ProfileId, context.SessionId), out _);
            deleted = await DeletePrivateDirectoryAsync(context, cancellationToken).ConfigureAwait(false);
        }

        return ControllerResult<WebViewProfileSessionEndReceipt>.Success(
            new WebViewProfileSessionEndReceipt(context.ProfileId, context.SessionId, deleted));
    }

    private async ValueTask ReleaseAsync(WebViewProfileDescriptor descriptor)
    {
        if (!descriptor.Context.IsPrivate)
        {
            return;
        }

        var key = (descriptor.Context.ProfileId, descriptor.Context.SessionId);
        var remaining = _privateLeases.AddOrUpdate(
            key,
            0,
            static (_, current) => Math.Max(0, current - 1));
        if (remaining == 0)
        {
            _privateLeases.TryRemove(key, out _);
            await DeletePrivateDirectoryAsync(
                descriptor.Context,
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async ValueTask<bool> DeletePrivateDirectoryAsync(
        PrivacyContext context,
        CancellationToken cancellationToken)
    {
        var path = Resolve(context);
        EnsureWithinRoot(path);
        var startedAt = DateTimeOffset.UtcNow;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(path))
            {
                return true;
            }

            try
            {
                Directory.Delete(path, recursive: true);
                return !Directory.Exists(path);
            }
            catch (DirectoryNotFoundException)
            {
                return true;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException &&
                DateTimeOffset.UtcNow - startedAt < PrivateDeleteRetryWindow)
            {
                // WebView2 releases its per-profile lockfile asynchronously after
                // the control is disposed. Keep cleanup bounded, cancellable, and
                // local to the exact validated private-session directory.
                await Task.Delay(PrivateDeleteRetryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    private string Resolve(PrivacyContext context)
    {
        var path = context.IsPrivate
            ? Path.Combine(
                _root,
                "private",
                context.ProfileId.Value.ToString("N"),
                context.SessionId.Value.ToString("N"))
            : Path.Combine(_root, "normal", context.ProfileId.Value.ToString("N"));
        var fullPath = Path.GetFullPath(path);
        EnsureWithinRoot(fullPath);
        return fullPath;
    }

    private void EnsureWithinRoot(string path)
    {
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Resolved WebView path escaped its configured root.");
        }
    }

    private sealed class Lease : IWebViewProfileLease
    {
        private WebViewProfileLifecycle? _owner;

        public Lease(WebViewProfileLifecycle owner, WebViewProfileDescriptor descriptor)
        {
            _owner = owner;
            Descriptor = descriptor;
        }

        public WebViewProfileDescriptor Descriptor { get; }

        public async ValueTask DisposeAsync()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is not null)
            {
                await owner.ReleaseAsync(Descriptor).ConfigureAwait(false);
            }
        }
    }
}
