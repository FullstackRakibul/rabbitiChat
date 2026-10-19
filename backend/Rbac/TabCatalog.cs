// RBAC — tab keys: their canonical spelling and the aliases accepted on input.
//
// What tabs exist, their labels and their groups are declared in
// PermissionCatalog; what each role gets is data (RolePermissions), seeded from
// RolePresetSeeds. This file only turns a spelling into a tab key.

namespace RabbitIC.Api.Rbac;

/// <summary>
/// TabKeys must match the ids the Vue shell uses — that string is the contract
/// between the two halves. Where the shell's id differs from the obvious name (its
/// Documents tab is "docs", its JSON/table tab is "jsonto"), the shell's id is
/// canonical and the other spelling is accepted as an alias, so a grant written
/// either way lands on the same tab.
/// </summary>
public static class TabCatalog
{
    /// <summary>The Access tab itself. Never grantable to one user: it would let them change their own access.</summary>
    public const string RbacTab = "rbac";

    /// <summary>Canonical keys, in the catalog's display order.</summary>
    public static readonly string[] All = PermissionCatalog.TabKeys.ToArray();

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
        ["access"] = RbacTab,
        // The 5Lakhs module is spelled "fivelakhs" in folder and storage-key
        // names; accept that spelling on input so the two can never drift into
        // a grant that silently matches nothing.
        ["fivelakhs"] = "5lakhs",
        // The splitter is spoken of as "the splitter"; accept the short forms so a
        // grant written either way lands on the same tab.
        ["splitter"] = "component-splitter",
        ["componentsplitter"] = "component-splitter",
        ["5lakh"] = "5lakhs",
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
        PermissionCatalog.Find(PermissionKeys.Tab(tabKey))?.DisplayLabel ?? tabKey;

    /// <summary>
    /// True when a tab may be handed to one user beyond their role. Everything is
    /// grantable except the Access tab: that screen changes roles and grants, so
    /// granting it would let a user change their own access.
    /// </summary>
    public static bool IsGrantable(string? tabKey) =>
        Canonical(tabKey) is { } key &&
        !string.Equals(key, RbacTab, StringComparison.OrdinalIgnoreCase);
}
