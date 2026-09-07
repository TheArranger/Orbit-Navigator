using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.Presentation.Common;

public enum AnnouncementPriority
{
    Polite = 0,
    Assertive = 1,
}

public sealed record PresentationAnnouncement(
    string MessageKey,
    AnnouncementPriority Priority = AnnouncementPriority.Polite);

public sealed record PresentationFailure(
    string MessageKey,
    bool IsRetryable,
    ControllerErrorCode? Code = null)
{
    public static PresentationFailure From(ControllerError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new PresentationFailure(error.MessageKey, error.IsRetryable, error.Code);
    }
}

public sealed class PresentationStateChangedEventArgs<TState> : EventArgs
    where TState : class
{
    public PresentationStateChangedEventArgs(TState state)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
    }

    public TState State { get; }
}

public abstract class PresentationStateSource<TState>
    where TState : class
{
    private readonly object stateLock = new();
    private TState state;

    protected PresentationStateSource(TState initialState)
    {
        state = initialState ?? throw new ArgumentNullException(nameof(initialState));
    }

    public event EventHandler<PresentationStateChangedEventArgs<TState>>? StateChanged;

    public TState State
    {
        get
        {
            lock (stateLock)
            {
                return state;
            }
        }
    }

    protected void Publish(TState nextState)
    {
        ArgumentNullException.ThrowIfNull(nextState);
        lock (stateLock)
        {
            state = nextState;
        }

        StateChanged?.Invoke(this, new PresentationStateChangedEventArgs<TState>(nextState));
    }
}
