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
/// admin rights.
/// </summary>
public record MyTabsDto(int Id, string Username, string Role, bool IsSystemAdmin, List<string> Tabs);

// ---- Input ----

public record SetRoleInput(string? Role);

/// <summary>
/// The complete set of explicit grants for a user — this REPLACES what is there,
/// it does not merge. Keys already covered by the role baseline are accepted and
/// dropped rather than rejected, so the UI can post back whatever it has checked.
/// </summary>
public record SetTabsInput(List<string>? TabKeys);
