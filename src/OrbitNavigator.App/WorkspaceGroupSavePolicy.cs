using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Foundation.Browser;
using OrbitNavigator.Presentation.Workspace;

namespace OrbitNavigator.App;

internal static class WorkspaceGroupSavePolicy
{
    public static bool TryValidate(
        BrowserWorkspaceSnapshot snapshot,
        BrowserTabGroupId groupId,
        WorkspacePresetDraftPresentation candidate,
        out WorkspacePresetDraftPresentation validated)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        validated = null!;
        var group = snapshot.Groups.SingleOrDefault(item => item.GroupId == groupId);
        if (group is null || !group.IsTemporary)
        {
            return false;
        }

        try
        {
            validated = candidate.Validate();
        }
        catch (ArgumentException)
        {
            return false;
        }

        var authoritativeTargets = snapshot.Browser.Tabs
            .Where(tab => tab.GroupId == groupId && IsNormalWebAddress(tab.Address))
            .Select(tab => CanonicalWebAddress.Normalize(tab.Address!))
            .ToArray();
        var requestedTargets = validated.Tabs
            .Select(tab => CanonicalWebAddress.Normalize(tab.Target))
            .ToArray();
        if (authoritativeTargets.Length == 0 ||
            !authoritativeTargets.SequenceEqual(requestedTargets))
        {
            return false;
        }

        return validated.Artwork.Kind != WorkspaceArtworkKind.Site ||
            validated.Artwork.SiteAddress is { } artworkSite &&
            requestedTargets.Contains(CanonicalWebAddress.Normalize(artworkSite));
    }

    private static bool IsNormalWebAddress(Uri? address) =>
        address is { IsAbsoluteUri: true } &&
        address.Scheme is "http" or "https" &&
        string.IsNullOrEmpty(address.UserInfo) &&
        !string.IsNullOrWhiteSpace(address.IdnHost);
}
