using OrbitNavigator.Presentation.Shell;
using Xunit;

namespace OrbitNavigator.Presentation.Tests;

public sealed class StartupLoadingStateTests
{
    [Fact]
    public void ProgressIsNormalizedAndRetryBelongsOnlyToFailure()
    {
        Assert.Throws<ArgumentException>(() => StartupLoadingState.Create(
            StartupLoadingPhase.Starting,
            "Starting",
            1.1));
        Assert.Throws<ArgumentException>(() => StartupLoadingState.Create(
            StartupLoadingPhase.Starting,
            "Starting",
            0.2,
            true));

        var failure = StartupLoadingState.Failure("Could not start.");
        Assert.Equal(StartupLoadingPhase.Failed, failure.Phase);
        Assert.True(failure.CanRetry);
    }
}
