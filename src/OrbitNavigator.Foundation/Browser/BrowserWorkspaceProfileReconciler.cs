using OrbitNavigator.Contracts.Browser;
using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.Foundation.Browser;

public sealed record BrowserWorkspaceReconciliationResult(
    BrowserWorkspaceSessionSnapshot Snapshot,
    bool WasRepaired,
    IReadOnlyList<string> RepairReasons);

public static class BrowserWorkspaceProfileReconciler
{
    public static ControllerResult<BrowserWorkspaceReconciliationResult> Reconcile(
        BrowserWorkspaceSessionSnapshot candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.ProfileId.IsEmpty || candidate.WindowId.IsEmpty ||
            candidate.Tabs is not { Count: > 0 and <= BrowserWorkspaceSessionStore.MaximumTabs } ||
            candidate.Groups is not { Count: <= BrowserWorkspaceSessionStore.MaximumGroups })
        {
            return Invalid();
        }

        var reasons = new HashSet<string>(StringComparer.Ordinal);
        var seenTabs = new HashSet<BrowserTabId>();
        var tabs = new List<BrowserWorkspaceSessionTab>(candidate.Tabs.Count);
        foreach (var tab in candidate.Tabs)
        {
            if (tab is null || tab.TabId.IsEmpty || !seenTabs.Add(tab.TabId))
            {
                return Invalid();
            }

            var address = NormalizeAddress(tab.Address, reasons);
            var title = NormalizeTitle(tab.Title, reasons);
            tabs.Add(tab with { Address = address, Title = title });
        }

        var validGroups = new List<BrowserWorkspaceSessionGroup>();
        var seenGroups = new HashSet<BrowserTabGroupId>();
        foreach (var group in candidate.Groups)
        {
            if (group is null || group.GroupId.IsEmpty || !seenGroups.Add(group.GroupId) ||
                string.IsNullOrWhiteSpace(group.Name))
            {
                reasons.Add("workspace.group.invalid_removed");
                continue;
            }
            validGroups.Add(group with
            {
                Name = group.Name.Trim()[..Math.Min(group.Name.Trim().Length, 60)],
                ColorToken = ValidColor(group.ColorToken) ? group.ColorToken : "SeaGlass",
                TabOrder = group.TabOrder?.ToArray() ?? [],
            });
            if (!ValidColor(group.ColorToken))
            {
                reasons.Add("workspace.group.color_repaired");
            }
        }

        var tabIds = tabs.Select(tab => tab.TabId).ToHashSet();
        var claimed = new HashSet<BrowserTabId>();
        var membership = new Dictionary<BrowserTabId, BrowserTabGroupId>();
        var repairedGroups = new List<BrowserWorkspaceSessionGroup>();
        foreach (var group in validGroups)
        {
            var requested = group.TabOrder.Where(tabIds.Contains).ToHashSet();
            foreach (var tab in tabs.Where(tab => tab.GroupId == group.GroupId))
            {
                requested.Add(tab.TabId);
            }

            var order = tabs.Select(tab => tab.TabId)
                .Where(tabId => requested.Contains(tabId) && claimed.Add(tabId))
                .ToArray();
            if (order.Length == 0)
            {
                reasons.Add("workspace.group.empty_removed");
                continue;
            }
            if (!order.SequenceEqual(group.TabOrder))
            {
                reasons.Add("workspace.group.membership_reconciled");
            }
            foreach (var tabId in order)
            {
                membership[tabId] = group.GroupId;
            }
            repairedGroups.Add(group with { TabOrder = order });
        }

        var repairedTabs = tabs.Select(tab =>
        {
            BrowserTabGroupId? groupId = membership.TryGetValue(tab.TabId, out var resolved)
                ? resolved
                : null;
            if (tab.GroupId != groupId)
            {
                reasons.Add("workspace.tab.group_reference_reconciled");
            }
            return tab with { GroupId = groupId };
        }).ToArray();
        var selected = repairedTabs.Any(tab => tab.TabId == candidate.SelectedTabId)
            ? candidate.SelectedTabId
            : repairedTabs[0].TabId;
        if (selected != candidate.SelectedTabId)
        {
            reasons.Add("workspace.selection_repaired");
        }

        var snapshot = candidate with
        {
            SelectedTabId = selected,
            Tabs = repairedTabs,
            Groups = repairedGroups,
        };
        return ControllerResult<BrowserWorkspaceReconciliationResult>.Success(new(
            snapshot,
            reasons.Count > 0,
            reasons.Order(StringComparer.Ordinal).ToArray()));
    }

    public static ControllerResult<BrowserWorkspaceReconciliationResult> ReconcileLegacy(
        PrivacyContext context,
        BrowserWindowId windowId,
        BrowserState browser,
        TabGroupMetadataSnapshot legacy)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentNullException.ThrowIfNull(legacy);
        var legacyGroups = legacy.Groups.Select(group => new BrowserWorkspaceSessionGroup(
            group.GroupId,
            group.Name,
            group.IsCollapsed,
            "SeaGlass",
            true,
            group.TabOrder)).ToArray();
        var tabs = browser.Tabs.Select(tab => new BrowserWorkspaceSessionTab(
            tab.TabId,
            tab.Address,
            tab.Title,
            tab.GroupId)).ToArray();
        return Reconcile(new(
            context.ProfileId,
            default,
            windowId,
            browser.SelectedTabId ?? browser.Tabs[0].TabId,
            tabs,
            legacyGroups));
    }

    private static Uri? NormalizeAddress(Uri? address, ISet<string> reasons)
    {
        if (address is null)
        {
            return null;
        }
        var normalized = CanonicalWebAddress.Normalize(address);
        if (!normalized.IsAbsoluteUri || normalized.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(normalized.UserInfo) || string.IsNullOrWhiteSpace(normalized.IdnHost))
        {
            reasons.Add("workspace.tab.unsafe_address_cleared");
            return null;
        }
        if (normalized != address)
        {
            reasons.Add("workspace.tab.address_normalized");
        }
        return normalized;
    }

    private static string NormalizeTitle(string? title, ISet<string> reasons)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            reasons.Add("workspace.tab.title_repaired");
            return "New Tab";
        }
        var trimmed = title.Trim();
        if (trimmed.Length <= 512)
        {
            return trimmed;
        }
        reasons.Add("workspace.tab.title_truncated");
        return trimmed[..512];
    }

    private static bool ValidColor(string value) => value is
        "SeaGlass" or "Gold" or "Violet" or "Scarlet" or "Azure" or "Slate";

    private static ControllerResult<BrowserWorkspaceReconciliationResult> Invalid() =>
        ControllerResult<BrowserWorkspaceReconciliationResult>.Failure(ControllerError.Create(
            ControllerErrorCode.IntegrityFailure,
            "error.workspace_session.unrepairable"));
}
