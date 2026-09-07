namespace OrbitNavigator.Contracts.Infrastructure;

public enum LocalAccountState
{
    SignedOut = 0,
    SignedIn = 1,
}

public enum LocalBrowsingReadinessState
{
    Ready = 0,
    WebViewRuntimeUnavailable = 1,
    ProfileStorageUnavailable = 2,
    WindowsKeyProtectionUnavailable = 3,
}

public sealed record LocalBrowsingPrerequisites(
    bool WebViewRuntimeAvailable,
    bool ProfileStorageAvailable,
    bool WindowsKeyProtectionAvailable,
    NetworkAvailability NetworkAvailability,
    LocalAccountState AccountState);

public sealed class LocalBrowsingReadiness
{
    private LocalBrowsingReadiness(LocalBrowsingReadinessState state)
    {
        State = state;
    }

    public LocalBrowsingReadinessState State { get; }

    public bool IsReady => State == LocalBrowsingReadinessState.Ready;

    public bool RequiresAccount => false;

    public bool RequiresNetwork => false;

    public static LocalBrowsingReadiness Evaluate(LocalBrowsingPrerequisites prerequisites)
    {
        ArgumentNullException.ThrowIfNull(prerequisites);

        var state = !prerequisites.WebViewRuntimeAvailable
            ? LocalBrowsingReadinessState.WebViewRuntimeUnavailable
            : !prerequisites.ProfileStorageAvailable
                ? LocalBrowsingReadinessState.ProfileStorageUnavailable
                : !prerequisites.WindowsKeyProtectionAvailable
                    ? LocalBrowsingReadinessState.WindowsKeyProtectionUnavailable
                    : LocalBrowsingReadinessState.Ready;

        return new LocalBrowsingReadiness(state);
    }
}
