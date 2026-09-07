namespace OrbitNavigator.Presentation.Shell;

public enum StartupLoadingPhase
{
    Starting = 0,
    PreparingProfile = 1,
    StartingBrowserEngine = 2,
    RestoringSession = 3,
    Ready = 4,
    Failed = 5,
}

public sealed record StartupLoadingState(
    StartupLoadingPhase Phase,
    string Status,
    double? Progress,
    bool CanRetry)
{
    public static StartupLoadingState Create(
        StartupLoadingPhase phase,
        string status,
        double? progress = null,
        bool canRetry = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        if (!Enum.IsDefined(phase) ||
            (progress is not null && (progress < 0 || progress > 1)) ||
            (phase != StartupLoadingPhase.Failed && canRetry))
        {
            throw new ArgumentException("The startup loading state is invalid.");
        }

        return new StartupLoadingState(phase, status.Trim(), progress, canRetry);
    }

    public static StartupLoadingState Initial() =>
        Create(StartupLoadingPhase.Starting, "Charting a private route…", 0.05);

    public static StartupLoadingState ReadyState() =>
        Create(StartupLoadingPhase.Ready, "Browser ready.", 1);

    public static StartupLoadingState Failure(string safeStatus, bool canRetry = true) =>
        Create(StartupLoadingPhase.Failed, safeStatus, null, canRetry);
}
