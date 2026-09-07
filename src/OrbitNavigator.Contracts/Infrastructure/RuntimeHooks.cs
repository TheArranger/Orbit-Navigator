using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.Contracts.Infrastructure;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public enum NetworkAvailability
{
    Unavailable = 0,
    Available = 1,
}

public sealed class NetworkAvailabilityChangedEventArgs : EventArgs
{
    public NetworkAvailabilityChangedEventArgs(
        NetworkAvailability previous,
        NetworkAvailability current,
        DateTimeOffset observedAtUtc)
    {
        if (!Enum.IsDefined(previous))
        {
            throw new ArgumentOutOfRangeException(nameof(previous));
        }

        if (!Enum.IsDefined(current))
        {
            throw new ArgumentOutOfRangeException(nameof(current));
        }

        Previous = previous;
        Current = current;
        ObservedAtUtc = observedAtUtc;
    }

    public NetworkAvailability Previous { get; }

    public NetworkAvailability Current { get; }

    public DateTimeOffset ObservedAtUtc { get; }
}

public interface INetworkState
{
    NetworkAvailability Availability { get; }

    event EventHandler<NetworkAvailabilityChangedEventArgs>? AvailabilityChanged;
}

public enum HostLifecycleTransition
{
    WindowsSessionLocked = 0,
    WindowsSessionUnlocked = 1,
    BrowserSessionEnding = 2,
    ApplicationStopping = 3,
}

public sealed class HostLifecycleEventArgs : EventArgs
{
    public HostLifecycleEventArgs(
        HostLifecycleTransition transition,
        DateTimeOffset observedAtUtc,
        ProfileId? profileId = null,
        BrowserSessionId? sessionId = null)
    {
        if (!Enum.IsDefined(transition))
        {
            throw new ArgumentOutOfRangeException(nameof(transition));
        }

        if (sessionId is not null && profileId is null)
        {
            throw new ArgumentException(
                "A browser session lifecycle event must identify its profile.",
                nameof(profileId));
        }

        if (profileId is { IsEmpty: true })
        {
            throw new ArgumentException("Profile identifiers cannot be empty.", nameof(profileId));
        }

        if (sessionId is { IsEmpty: true })
        {
            throw new ArgumentException("Session identifiers cannot be empty.", nameof(sessionId));
        }

        Transition = transition;
        ObservedAtUtc = observedAtUtc;
        ProfileId = profileId;
        SessionId = sessionId;
    }

    public HostLifecycleTransition Transition { get; }

    public DateTimeOffset ObservedAtUtc { get; }

    public ProfileId? ProfileId { get; }

    public BrowserSessionId? SessionId { get; }
}

public interface IHostLifecycleEvents
{
    event EventHandler<HostLifecycleEventArgs>? Transitioned;
}
