using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Presentation.QuickView;

namespace OrbitNavigator.App.QuickView;

internal sealed record QuickViewSessionSnapshot(
    long Revision,
    QuickViewHostState HostState,
    Uri? Address,
    string Title,
    string SafeStatusMessage);

/// <summary>
/// Serial revision gate for one ephemeral Quick View surface. It never owns a
/// WebView, network client, browser tab, or persisted state.
/// </summary>
internal sealed class QuickViewSessionState
{
    private readonly bool _isPrivate;

    public QuickViewSessionState(PrivacyContext privacy)
    {
        if (privacy is not { IsStructurallyValid: true })
        {
            throw new ArgumentException("A valid privacy context is required.", nameof(privacy));
        }
        _isPrivate = privacy.IsPrivate;
        Snapshot = new(
            0,
            QuickViewHostState.Unavailable,
            null,
            "Quick View",
            _isPrivate
                ? "Quick View is unavailable in private browsing."
                : "Open a normal website to use Quick View.");
    }

    public QuickViewSessionSnapshot Snapshot { get; private set; }

    public void SetSelectedSite(Uri? selectedAddress)
    {
        if (Snapshot.HostState is QuickViewHostState.Opening or QuickViewHostState.Open or
            QuickViewHostState.Closing)
        {
            return;
        }
        var address = IsWebAddress(selectedAddress) && !_isPrivate
            ? new Uri(selectedAddress!.AbsoluteUri)
            : null;
        var nextState = address is null ? QuickViewHostState.Unavailable : QuickViewHostState.Ready;
        if (Snapshot.HostState == nextState && Snapshot.Address is null)
        {
            return;
        }
        Set(new(
            NextRevision(),
            nextState,
            null,
            "Quick View",
            _isPrivate
                ? "Quick View is unavailable in private browsing."
                : address is null
                    ? "Open a normal website to use Quick View."
                    : "Quick View is ready."));
    }

    public ControllerResult<QuickViewSessionSnapshot> BeginOpen(long expectedRevision, Uri target)
    {
        var validation = ValidateExpected(expectedRevision);
        if (validation is not null)
        {
            return ControllerResult<QuickViewSessionSnapshot>.Failure(validation);
        }
        if (_isPrivate)
        {
            return Denied();
        }
        if (Snapshot.HostState != QuickViewHostState.Ready || !IsWebAddress(target))
        {
            return Unavailable();
        }
        Set(new(
            NextRevision(),
            QuickViewHostState.Opening,
            new Uri(target.AbsoluteUri),
            target.Host,
            "Opening Quick View."));
        return ControllerResult<QuickViewSessionSnapshot>.Success(Snapshot);
    }

    public ControllerResult<QuickViewSessionSnapshot> BeginNavigate(long expectedRevision, Uri target)
    {
        var validation = ValidateExpected(expectedRevision);
        if (validation is not null)
        {
            return ControllerResult<QuickViewSessionSnapshot>.Failure(validation);
        }
        if (_isPrivate)
        {
            return Denied();
        }
        if (Snapshot.HostState != QuickViewHostState.Open || !IsWebAddress(target))
        {
            return Unavailable();
        }
        Set(new(
            NextRevision(),
            QuickViewHostState.Opening,
            new Uri(target.AbsoluteUri),
            target.Host,
            "Opening Quick View."));
        return ControllerResult<QuickViewSessionSnapshot>.Success(Snapshot);
    }

    public void CompleteOpen(Uri address, string title)
    {
        if (Snapshot.HostState != QuickViewHostState.Opening || !IsWebAddress(address))
        {
            throw new InvalidOperationException("Quick View is not opening a valid web address.");
        }
        Set(new(
            NextRevision(),
            QuickViewHostState.Open,
            new Uri(address.AbsoluteUri),
            NormalizeTitle(title, address),
            "Temporary mini-browser. This surface is not a separate tab."));
    }

    public ControllerResult<QuickViewSessionSnapshot> BeginClose(long expectedRevision)
    {
        var validation = ValidateExpected(expectedRevision);
        if (validation is not null)
        {
            return ControllerResult<QuickViewSessionSnapshot>.Failure(validation);
        }
        if (_isPrivate)
        {
            return Denied();
        }
        if (Snapshot.HostState is not (QuickViewHostState.Opening or QuickViewHostState.Open or
            QuickViewHostState.Failed))
        {
            return Unavailable();
        }
        Set(Snapshot with
        {
            Revision = NextRevision(),
            HostState = QuickViewHostState.Closing,
            SafeStatusMessage = "Closing Quick View.",
        });
        return ControllerResult<QuickViewSessionSnapshot>.Success(Snapshot);
    }

    public void CompleteClose(Uri? selectedAddress)
    {
        if (Snapshot.HostState != QuickViewHostState.Closing)
        {
            throw new InvalidOperationException("Quick View is not closing.");
        }
        var ready = !_isPrivate && IsWebAddress(selectedAddress);
        Set(new(
            NextRevision(),
            ready ? QuickViewHostState.Ready : QuickViewHostState.Unavailable,
            null,
            "Quick View",
            ready ? "Quick View is ready." : _isPrivate
                ? "Quick View is unavailable in private browsing."
                : "Open a normal website to use Quick View."));
    }

    public void UpdateOpenDocument(Uri address, string title)
    {
        if (Snapshot.HostState != QuickViewHostState.Open || !IsWebAddress(address))
        {
            return;
        }
        Set(Snapshot with
        {
            Revision = NextRevision(),
            Address = new Uri(address.AbsoluteUri),
            Title = NormalizeTitle(title, address),
        });
    }

    public void MarkFailed(string safeMessage)
    {
        Set(Snapshot with
        {
            Revision = NextRevision(),
            HostState = QuickViewHostState.Failed,
            SafeStatusMessage = string.IsNullOrWhiteSpace(safeMessage)
                ? "Quick View could not be opened."
                : safeMessage.Trim(),
        });
    }

    public ControllerResult<QuickViewSessionSnapshot> AcceptEphemeralAction(long expectedRevision)
    {
        var validation = ValidateExpected(expectedRevision);
        if (validation is not null)
        {
            return ControllerResult<QuickViewSessionSnapshot>.Failure(validation);
        }
        if (_isPrivate)
        {
            return Denied();
        }
        Set(Snapshot with { Revision = NextRevision() });
        return ControllerResult<QuickViewSessionSnapshot>.Success(Snapshot);
    }

    private ControllerError? ValidateExpected(long expectedRevision) =>
        expectedRevision < 0 || expectedRevision != Snapshot.Revision
            ? ControllerError.Create(
                ControllerErrorCode.Conflict,
                "error.quick_view.revision_conflict")
            : null;

    private long NextRevision() => Snapshot.Revision == long.MaxValue
        ? throw new InvalidOperationException("Quick View revision exhausted.")
        : Snapshot.Revision + 1;

    private void Set(QuickViewSessionSnapshot snapshot) => Snapshot = snapshot;

    private static bool IsWebAddress(Uri? address) =>
        address is { IsAbsoluteUri: true } &&
        address.Scheme is "http" or "https" &&
        string.IsNullOrEmpty(address.UserInfo) &&
        !string.IsNullOrWhiteSpace(address.IdnHost);

    private static string NormalizeTitle(string title, Uri address)
    {
        var normalized = string.IsNullOrWhiteSpace(title) ? address.Host : title.Trim();
        return normalized.Length <= 300 ? normalized : normalized[..300];
    }

    private static ControllerResult<QuickViewSessionSnapshot> Denied() =>
        ControllerResult<QuickViewSessionSnapshot>.Failure(ControllerError.Create(
            ControllerErrorCode.PolicyDenied,
            "error.quick_view.private_unavailable"));

    private static ControllerResult<QuickViewSessionSnapshot> Unavailable() =>
        ControllerResult<QuickViewSessionSnapshot>.Failure(ControllerError.Create(
            ControllerErrorCode.Unavailable,
            "error.quick_view.unavailable"));
}
