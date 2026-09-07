using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Privacy;
using OrbitNavigator.Foundation.Profiles;

namespace OrbitNavigator.App.Composition;

/// <summary>
/// Foundation-owned registration seam for privacy-owned rule persistence.
/// Privacy supplies the factories and owns hydration/serialization logic; each
/// factory receives storage restricted to one normal profile's persistent data.
/// </summary>
public sealed class PrivacyPersistenceComposition
{
    public PrivacyPersistenceComposition(ProfileId profileId, IProfileStorage profileStorage)
    {
        SiteProtectionRules = new NormalProfilePersistentStorage(profileStorage, profileId);
        PermissionRules = new NormalProfilePersistentStorage(profileStorage, profileId);
    }

    public IProfileStorage SiteProtectionRules { get; }

    public IProfileStorage PermissionRules { get; }

    public ISiteProtectionController CreateSiteProtectionController(
        Func<IProfileStorage, ISiteProtectionController> factory) =>
        (factory ?? throw new ArgumentNullException(nameof(factory)))(SiteProtectionRules);

    public IPermissionBroker CreatePermissionBroker(
        Func<IProfileStorage, IPermissionBroker> factory) =>
        (factory ?? throw new ArgumentNullException(nameof(factory)))(PermissionRules);
}
