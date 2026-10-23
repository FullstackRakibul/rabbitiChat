// Access — the first value of every role preset.
//
// These are the baselines and capability checks that were written in code before
// role presets moved to the database (docs/RBAC_ROLES_PROMPT.md §2, §3.2). They
// are read ONCE per missing (role, key) row by RbacSeeder.SeedRolePermissions and
// never again: after that the row is the truth and an administrator edits it in
// Access → Roles. So the upgrade changes nobody's access, and a key added to the
// catalog later starts every role on the value given here.

using RabbitIC.Api.Models;

namespace RabbitIC.Api.Rbac;

public static class RolePresetSeeds
{
    // ---- Tabs (the TabCatalog baselines before this build) ----

    /// <summary>A plain account: the self-service tools that need no model and show nobody else's data.</summary>
    private static readonly string[] GeneralTabs =
    {
        "dashboard", "todo", "notes", "sources", "settings", "5lakhs",
    };

    /// <summary>General plus the model-backed and knowledge tooling.</summary>
    private static readonly string[] SystemTabs = GeneralTabs.Concat(new[]
    {
        "chat", "workspace", "docs", "search", "ielts", "component-splitter",
    }).ToArray();

    /// <summary>System plus the Oddity gateway it operates.</summary>
    private static readonly string[] GroundControlTabs = SystemTabs.Append("oddity").ToArray();

    /// <summary>The HR module and the two personal tools (docs/HR_MODULE_PROMPT.md §3.1).</summary>
    private static readonly string[] HrAssistantTabs = { "hr", "notes", "settings" };

    /// <summary>The HR module, where it sees only Procedures, and Settings (docs/HR_PROCEDURE_ANALYZER_PROMPT.md §3.7).</summary>
    private static readonly string[] DevOpsTabs = { "hr", "settings" };

    /// <summary>
    /// Matrix (docs/MATRIX_PROMPT.md §3.10): System and hr-assistant get the tab,
    /// every section, upload, dashboard editing and names — not salary. Templates,
    /// fixes and KPI definitions stay SystemAdmin's. Ground Control is System plus
    /// the Oddity gateway by construction, so it gets what System gets (a move from
    /// System to Ground Control removes nothing). Other roles: none.
    /// </summary>
    private static readonly string[] MatrixRoles = { UserRoles.System, UserRoles.GroundControl, UserRoles.HrAssistant };
    private static readonly string[] MatrixActions =
        { PermissionKeys.MatrixUpload, PermissionKeys.MatrixDashboardsEdit, PermissionKeys.MatrixViewPersonal };

    private static IReadOnlyCollection<string> TabsFor(string role) => role switch
    {
        UserRoles.GroundControl => GroundControlTabs,
        UserRoles.System => SystemTabs,
        UserRoles.HrAssistant => HrAssistantTabs,
        UserRoles.DevOps => DevOpsTabs,
        _ => GeneralTabs,
    };

    // ---- Sections and actions (the role checks in the services before this build) ----

    /// <summary>Who held each capability, as the services checked it.</summary>
    private static readonly Dictionary<string, string[]> ActionHolders = new(StringComparer.OrdinalIgnoreCase)
    {
        // ContextSourceService.CanEditShared, and HrRulebook.CanEdit which was the same check.
        [PermissionKeys.HrLibraryEdit] = new[] { UserRoles.HrAssistant },
        [PermissionKeys.HrRulebookEdit] = new[] { UserRoles.HrAssistant },
        [PermissionKeys.HrRulebookConfirm] = new[] { UserRoles.HrAssistant },
        // HrProcedures.CanUpload, CanAnswer, CanEditRegistry.
        [PermissionKeys.HrProceduresUpload] = new[] { UserRoles.DevOps },
        [PermissionKeys.HrProceduresAnswer] = new[] { UserRoles.DevOps, UserRoles.HrAssistant },
        [PermissionKeys.HrLookupsEdit] = new[] { UserRoles.DevOps },
        // PO Studio's buyer profiles had no check of their own: anyone with the docs tab.
        [PermissionKeys.PoProfilesEdit] = UserRoles.All,
        // SystemAdmin only: /api/hr/library/index-stats, /api/logs/audit, the Access screen.
        [PermissionKeys.HrLibraryStats] = Array.Empty<string>(),
        [PermissionKeys.LogsAudit] = Array.Empty<string>(),
        [PermissionKeys.AccessManage] = Array.Empty<string>(),
        // The Ministry of Truth (docs/TRUTH_PROMPT.md §8): the admin preset only. Its tab is in no role's list above.
        [PermissionKeys.TruthPublish] = Array.Empty<string>(),
    };

    /// <summary>
    /// The value a role starts with for one key. SystemAdmin starts with
    /// everything (and AccessPolicy holds it there). Sections start on for every
    /// role — they mean nothing without their tab — except that DevOps sees only
    /// Procedures in HR, which HrTab.vue decided by role until now.
    /// </summary>
    public static bool InitialValue(string role, PermissionDef p)
    {
        if (role == UserRoles.SystemAdmin) return true;

        if (p.Key == PermissionKeys.TabMatrix || p.Key.StartsWith("section:matrix.", StringComparison.Ordinal))
            return MatrixRoles.Contains(role);
        if (p.Key.StartsWith("action:matrix.", StringComparison.Ordinal))
            return MatrixRoles.Contains(role) && MatrixActions.Contains(p.Key);

        return p.Kind switch
        {
            PermissionCatalog.KindTab => TabsFor(role).Contains(p.Key["tab:".Length..], StringComparer.OrdinalIgnoreCase),
            PermissionCatalog.KindSection => role != UserRoles.DevOps || p.Key == PermissionKeys.HrProcedures,
            _ => ActionHolders.TryGetValue(p.Key, out var holders) && holders.Contains(role),
        };
    }
}
