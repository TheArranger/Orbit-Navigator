using OrbitNavigator.Contracts.Infrastructure;

namespace OrbitNavigator.Foundation.Runtime;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
