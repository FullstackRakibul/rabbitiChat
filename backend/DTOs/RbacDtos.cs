// RBAC — wire contracts for the admin surface.
//
// Entities never cross the boundary: no secret hash, salt or token appears in
// any shape here, so an admin listing users cannot leak credential material.

namespace RabbitIC.Api.DTOs.Rbac;

// ---- Output ----

/// <summary>A user in the admin search list.</summary>
public record UserSummaryDto(int Id, string Username, string Role, int TabCount, DateTime CreatedAt);

/// <summary>One page of search results — the UI needs the total to page.</summary>
public record UserPageDto(List<UserSummaryDto> Items, int Total, int Page, int PageSize);

/// <summary>
/// One user in detail. Three tab lists, because the admin UI needs to tell them
/// apart: what the role already gives (shown checked and locked), what was
/// granted on top (toggleable), and the union the shell actually renders.
/// </summary>
public record UserDetailDto(
    int Id,
    string Username,
    string Role,
    DateTime CreatedAt,
    List<string> EffectiveTabs,
    List<string> RoleDefaultTabs,
    List<string> GrantedTabs);

/// <summary>A tab in the admin checklist.</summary>
public record TabOptionDto(string Key, string Label);

/// <summary>
/// A role in the admin picker, with the tabs it gives before any grant — so the
/// UI can preview a role change without keeping its own copy of the baselines.
/// </summary>
public record RoleOptionDto(string Key, string Label, List<string> DefaultTabs);

/// <summary>
/// What the shell asks for on login: the caller's own role and tabs. Deliberately
/// not the admin detail shape — it carries no other user's data and needs no
/// admin rights. RoleLabel is how the role is shown (e.g. "hr-assistant"), so the
/// header's user block needs no copy of the label list.
/// </summary>
public record MyTabsDto(int Id, string Username, string Role, string RoleLabel, bool IsSystemAdmin, List<string> Tabs);

// ---- Input ----

public record SetRoleInput(string? Role);

/// <summary>
/// The complete set of explicit grants for a user — this REPLACES what is there,
/// it does not merge. Keys already covered by the role baseline are accepted and
/// dropped rather than rejected, so the UI can post back whatever it has checked.
/// </summary>
public record SetTabsInput(List<string>? TabKeys);

/// <summary>
/// One role's model policy, as the Access screen's "Models by role" shows it.
/// An empty <paramref name="AllowedModels"/> means every local model.
/// </summary>
/// <param name="CanRestrict">
/// False for SystemAdmin, whose row is read-only: an administrator locked out of
/// every model could not undo it through a UI that needs one.
/// </param>
/// <param name="Stale">
/// Allowed models that are no longer installed locally. They stay in
/// <paramref name="AllowedModels"/> — hiding them would make the editor post
/// them back unchanged — but the UI marks them and an admin can untick them.
/// </param>
public record RoleModelPolicyDto(
    string Role,
    string Label,
    List<string> AllowedModels,
    string DefaultModel,
    bool CanRestrict,
    DateTime? UpdatedAtUtc,
    List<string> Stale);

/// <summary>
/// A saved policy. An empty list clears the restriction, and then the default is
/// cleared too — the caller's own fallback decides, as it always has.
/// </summary>
public record RoleModelPolicyInput(List<string>? AllowedModels, string? DefaultModel);

// ---- Access: the catalog, role presets and the caller's own access (docs/RBAC_ROLES_PROMPT.md §3) ----

/// <summary>One permission as the Access screen draws it. AlwaysOn: no role may turn it off (Settings).</summary>
public record PermissionDto(string Key, string Kind, string En, string Bn, string Label, string Group, string? Parent, bool AlwaysOn);

/// <summary>GET /api/access/catalog — every permission, in display order, and the module menu's groups.</summary>
public record CatalogDto(List<string> Groups, List<PermissionDto> Permissions);

/// <summary>
/// What one role gets, for every catalog key. Locked: SystemAdmin, whose column
/// cannot be changed. UserCount: how many users a change would reach.
/// </summary>
public record RolePresetDto(string Role, string Label, bool Locked, int UserCount, Dictionary<string, bool> Values);

public record RolePresetsDto(List<RolePresetDto> Roles);

/// <summary>PUT /api/access/roles/{role}. Only keys whose value differs are written.</summary>
public record SaveRolePresetInput(Dictionary<string, bool>? Values, string? Note);

public record AccessChangeDto(string Key, bool Before, bool After);

/// <summary>What a save changed, and how many users of the role it reached (0 when nothing changed).</summary>
public record SaveRolePresetResult(string Role, List<AccessChangeDto> Changed, int AffectedUsers, Dictionary<string, bool> Values);

/// <summary>One module the caller may open, as the shell's menus show it.</summary>
public record MyModuleDto(string Key, string Label, string En, string Bn, string Group);

/// <summary>The caller's model policy: an empty Allowed means no restriction.</summary>
public record MyModelsDto(List<string> Allowed, string? Default, bool Locked);

/// <summary>
/// GET /api/me/access — everything the shell draws from, in one response. Tabs is
/// the same list /api/me/tabs returns; Revision is the X-Access-Rev it was built at.
/// </summary>
public record MyAccessDto(
    int Id, string Username, string Role, string RoleLabel, bool IsSystemAdmin,
    List<string> Tabs, List<string> Sections, List<string> Actions,
    List<MyModuleDto> Modules, MyModelsDto Models, string Revision);
