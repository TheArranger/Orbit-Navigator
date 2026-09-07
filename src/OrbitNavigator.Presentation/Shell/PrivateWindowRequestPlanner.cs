using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.Presentation.Shell;

public static class PrivateWindowRequestPlanner
{
    public static CreatePrivateWindowIntent Create(BrowsingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.IsStructurallyValid || context.Privacy.IsPrivate)
        {
            throw new ArgumentException("A valid normal browsing context is required.", nameof(context));
        }

        return new CreatePrivateWindowIntent(context.Privacy, context.WindowId);
    }
}
