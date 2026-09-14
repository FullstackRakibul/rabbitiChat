// RBAC — the tab catalogue and the role → default-tabs map.
//
// Deliberately a constant map rather than a table: role baselines change when
// the SHIPPED TAB SET changes, which is a code change anyway. Keeping it here
// means a new tab is one line in one file, and there is no migration or admin
// step needed before the new tab can be granted.

using RabbitIC.Api.Models;

namespace RabbitIC.Api.Rbac;

/// <summary>
/// Every tab the shell can render, and which roles get it by default.
///
/// TabKeys must match the ids the Vue shell uses in its tab list — that string
/// is the contract between the two halves. Where the shell's id differs from the
/// obvious name (its Documents tab is "docs", its JSON/table tab is "jsonto"),
/// the shell's id is canonical here and the other spelling is accepted as an
/// alias, so a grant written either way lands on the same tab.
/// </summary>
public static class TabCatalog
{
    /// <summary>The RBAC admin tab itself — SystemAdmin only, never grantable below it.</summary>
    public const string RbacTab = "rbac";

    /// <summary>
    /// Canonical keys, in the order the UI should list them. The shell's own tab
    /// ids, so a granted key always matches a renderable tab.
    /// </summary>
    public static readonly string[] All =
    {
        "dashboard",
        "chat",
        "workspace",
        "docs",
        "todo",
        "search",
        "jsonto",
        "5lakhs",
        "sources",
        "settings",
        RbacTab,
    };

    /// <summary>
    /// Human labels for the admin checklist. A key with no entry falls back to
    /// the key itself, so adding a tab to <see cref="All"/> alone still works.
    /// </summary>
    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dashboard"] = "Dashboard",
        ["chat"] = "Chat",
        ["workspace"] = "Workspace",
        ["docs"] = "Documents",
        ["todo"] = "To-Do",
        ["search"] = "Search",
        ["jsonto"] = "JSON to ...",
        ["5lakhs"] = "5 Lakhs",
        ["sources"] = "Sources",
        ["settings"] = "Settings",
        [RbacTab] = "Access control",
    };

    /// <summary>
    /// Alternative spellings accepted on input, mapped to the canonical key.
    /// Lets the API take "documents"/"table" (the names used in the spec and in
    /// conversation) without the shell having to rename its tabs.
    /// </summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["documents"] = "docs",
        ["document"] = "docs",
        ["table"] = "jsonto",
        ["json"] = "jsonto",
        ["jsontable"] = "jsonto",
        ["todos"] = "todo",
        ["to-do"] = "todo",
        ["admin"] = RbacTab,
    };

    // ---- Role baselines ----
    // Additive by construction: each tier is the previous tier plus more, so a
    // promotion can never remove a tab.

    /// <summary>The everyday tabs — what a plain account gets with no grants at all.</summary>
    private static readonly string[] GeneralTabs =
    {
        "chat", "dashboard", "todo", "jsonto", "5lakhs", "settings",
    };

    /// <summary>General plus the document/knowledge tooling.</summary>
    private static readonly string[] SystemExtraTabs =
    {
        "workspace", "docs", "search", "sources",
    };

    /// <summary>
    /// Tabs a role gets without any explicit grant.
    /// SystemAdmin gets everything in <see cref="All"/>, including the RBAC tab —
    /// that tab is never in a lower tier, so it cannot be granted to a non-admin.
    /// </summary>
    public static IReadOnlyCollection<string> DefaultTabsFor(string? role) =>
        UserRoles.Normalize(role) switch
        {
            UserRoles.SystemAdmin => All,
            UserRoles.System => GeneralTabs.Concat(SystemExtraTabs).ToArray(),
            _ => GeneralTabs,
        };

    /// <summary>
    /// Canonical form of a tab key, or null when it names no known tab.
    /// Unknown keys are dropped rather than stored, so the grant table can never
    /// accumulate rows that point at nothing.
    /// </summary>
    public static string? Canonical(string? tabKey)
    {
        var key = tabKey?.Trim();
        if (string.IsNullOrEmpty(key)) return null;

        var exact = All.FirstOrDefault(t => string.Equals(t, key, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        return Aliases.TryGetValue(key, out var mapped) ? mapped : null;
    }

    public static string LabelFor(string tabKey) =>
        Labels.TryGetValue(tabKey, out var label) ? label : tabKey;

    /// <summary>
    /// Effective tabs = role baseline ∪ explicit grants, in catalogue order so
    /// the UI gets a stable list. Grants that name an unknown tab are ignored.
    /// </summary>
    public static List<string> EffectiveTabs(string? role, IEnumerable<string> grants)
    {
        var set = new HashSet<string>(DefaultTabsFor(role), StringComparer.OrdinalIgnoreCase);

        foreach (var grant in grants)
        {
            var canonical = Canonical(grant);
            if (canonical is not null) set.Add(canonical);
        }

        return All.Where(set.Contains).ToList();
    }
}
