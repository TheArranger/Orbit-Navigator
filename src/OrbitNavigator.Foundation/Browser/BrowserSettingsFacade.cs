using System.Text.Json;
using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Updates;

namespace OrbitNavigator.Foundation.Browser;

public sealed class BrowserSettingsFacade : IBrowserSettingsFacade
{
    private const string StorageKey = "core";
    private readonly IProfileStorage _storage;
    private readonly ProfileStorageNamespace _namespace;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public BrowserSettingsFacade(IProfileStorage storage)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _namespace = ProfileStorageNamespace.Create("browser.settings").Value!;
    }

    public async ValueTask<ControllerResult<BrowserSettingsSnapshot>> GetAsync(
        PrivacyContext context,
        CancellationToken cancellationToken = default)
    {
        var address = Address(context);
        if (!address.IsSuccess)
        {
            return ControllerResult<BrowserSettingsSnapshot>.Failure(address.Error!);
        }

        var read = await _storage.ReadAsync(address.Value!, cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            return read.Error?.Code == ControllerErrorCode.NotFound
                ? ControllerResult<BrowserSettingsSnapshot>.Success(new(
                    context,
                    default,
                    DefaultValues()))
                : ControllerResult<BrowserSettingsSnapshot>.Failure(read.Error!);
        }

        try
        {
            var values = JsonSerializer.Deserialize<BrowserCoreSettings>(read.Value!.Payload.Span);
            return values is not null && Valid(values)
                ? ControllerResult<BrowserSettingsSnapshot>.Success(new(
                    context,
                    new BrowserSettingsRevision(read.Value.Revision.Value),
                    values))
                : Corrupt();
        }
        catch (JsonException)
        {
            return Corrupt();
        }
    }

    public async ValueTask<ControllerResult<BrowserSettingsSnapshot>> UpdateAsync(
        UpdateBrowserSettingsIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (intent.Context is not { IsStructurallyValid: true } || !Valid(intent.Values))
        {
            return Invalid();
        }
        if (intent.Context.IsPrivate)
        {
            return ControllerResult<BrowserSettingsSnapshot>.Failure(ControllerError.Create(
                ControllerErrorCode.PolicyDenied,
                "error.browser_settings.private_write_denied"));
        }

        var address = Address(intent.Context).Value!;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await _storage.ReadAsync(address, cancellationToken).ConfigureAwait(false);
            ProfileStorageRevision? expected;
            if (current.IsSuccess)
            {
                if (intent.ExpectedRevision.IsEmpty ||
                    current.Value!.Revision.Value != intent.ExpectedRevision.Value)
                {
                    return Conflict();
                }
                expected = current.Value.Revision;
            }
            else if (current.Error?.Code == ControllerErrorCode.NotFound &&
                intent.ExpectedRevision.IsEmpty)
            {
                expected = null;
            }
            else if (current.Error?.Code == ControllerErrorCode.NotFound)
            {
                return Conflict();
            }
            else
            {
                return ControllerResult<BrowserSettingsSnapshot>.Failure(current.Error!);
            }

            var request = ProfileStorageWriteRequest.Create(
                address,
                JsonSerializer.SerializeToUtf8Bytes(intent.Values),
                expected).Value!;
            var write = await _storage.WriteAsync(request, cancellationToken).ConfigureAwait(false);
            return write.IsSuccess
                ? ControllerResult<BrowserSettingsSnapshot>.Success(new(
                    intent.Context,
                    new BrowserSettingsRevision(write.Value!.Revision.Value),
                    intent.Values))
                : ControllerResult<BrowserSettingsSnapshot>.Failure(write.Error!);
        }
        finally
        {
            _gate.Release();
        }
    }

    private ControllerResult<ProfileStorageAddress> Address(PrivacyContext context)
    {
        if (context is not { IsStructurallyValid: true })
        {
            return ControllerResult<ProfileStorageAddress>.Failure(ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "error.browser_settings.context_invalid"));
        }
        var normalContext = context.IsPrivate
            ? new PrivacyContext(context.ProfileId, context.SessionId, BrowserProfileMode.Normal)
            : context;
        return ProfileStorageAddress.Create(
            normalContext,
            _namespace,
            ProfileStorageKey.Create(StorageKey).Value,
            ProfileStorageDurability.Persistent);
    }

    private static BrowserCoreSettings DefaultValues() =>
        new("duckduckgo", true, true, UpdatePreference.NotifyOnly);

    private static bool Valid(BrowserCoreSettings values) =>
        values is not null &&
        string.Equals(values.SearchProviderId, "duckduckgo", StringComparison.Ordinal) &&
        Enum.IsDefined(values.UpdatePreference);

    private static ControllerResult<BrowserSettingsSnapshot> Invalid() =>
        ControllerResult<BrowserSettingsSnapshot>.Failure(ControllerError.Create(
            ControllerErrorCode.InvalidRequest,
            "error.browser_settings.invalid"));

    private static ControllerResult<BrowserSettingsSnapshot> Conflict() =>
        ControllerResult<BrowserSettingsSnapshot>.Failure(ControllerError.Create(
            ControllerErrorCode.Conflict,
            "error.browser_settings.revision_conflict"));

    private static ControllerResult<BrowserSettingsSnapshot> Corrupt() =>
        ControllerResult<BrowserSettingsSnapshot>.Failure(ControllerError.Create(
            ControllerErrorCode.IntegrityFailure,
            "error.browser_settings.storage_corrupt"));
}
