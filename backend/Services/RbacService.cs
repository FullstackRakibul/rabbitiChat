// RBAC — business rules and guardrails.
//
// Every refusal in this module is decided here, never in an endpoint. What a
// user may open or do is AccessPolicy's answer; this service decides who may
// CHANGE that, and refuses a change that would break an invariant
// (docs/RBAC_ROLES_PROMPT.md §0 rule 7):
//
//   1. Only an access manager (SystemAdmin, or a role given action:access.manage)
//      may call the admin surface at all.
//   2. Nobody may change their OWN role or tabs — an admin who could demote
//      themselves could lock themselves out, and a stolen session could
//      self-escalate. Nor may a manager's preset edit take away their own access.
//   3. The LAST SystemAdmin may not be demoted — there must always be someone
//      left who can hand out the role.
//   4. SystemAdmin always has everything, and Settings is always on.
//
// Endpoints translate the returned outcome into a status code and never re-derive
// a rule, so the UI and the API cannot disagree about what is allowed.

using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.Data;
using RabbitIC.Api.DTOs.Rbac;
using RabbitIC.Api.Models;
using RabbitIC.Api.Rbac;
using RabbitIC.Api.Repositories;

namespace RabbitIC.Api.Services;

/// <summary>Why an operation was refused. <see cref="Ok"/> is the only success.</summary>
public enum RbacOutcome
{
    Ok,

    /// <summary>No such user.</summary>
    NotFound,

    /// <summary>The caller may not manage access, or is acting on themselves.</summary>
    Forbidden,

    /// <summary>Would leave the system with no SystemAdmin.</summary>
    Conflict,

    /// <summary>The input named an unknown role or key, or breaks an invariant (see <see cref="RbacResult{T}.Invariant"/>).</summary>
    Invalid,
}

/// <summary>An outcome, the value when it succeeded, and a reason when it did not.</summary>
public record RbacResult<T>(RbacOutcome Outcome, T? Value = default, string? Error = null, string? Invariant = null)
{
    public static RbacResult<T> Success(T value) => new(RbacOutcome.Ok, value);

    public static RbacResult<T> Fail(RbacOutcome outcome, string error) =>
        new(outcome, default, error);

    /// <summary>A 400 that names the invariant it would break, in the error and on its own.</summary>
    public static RbacResult<T> Breaks(string invariant, string why) =>
        new(RbacOutcome.Invalid, default, $"Invariant {invariant}: {why}", invariant);
}

/// <summary>The invariants a save may be refused for (§0 rule 7), as named in a 400.</summary>
public static class AccessInvariants
{
    public const string SystemAdminHasEverything = "system-admin-has-everything";
    public const string SettingsAlwaysOn = "settings-always-on";
    public const string LastSystemAdmin = "last-system-admin";
    public const string OwnAccess = "own-access";
}

public class RbacService
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;
    private const string ManagersOnly = "Managing access requires SystemAdmin or the Manage access permission.";

    private readonly IRbacRepository _repo;
    private readonly AccessPolicy _access;
    private readonly ModelPolicy _models;
    private readonly AppDbContext _db;

    public RbacService(IRbacRepository repo, AccessPolicy access, ModelPolicy models, AppDbContext db)
    {
        _repo = repo;
        _access = access;
        _models = models;
        _db = db;
    }

    // ---- Reads any signed-in user may make about themselves ----

    /// <summary>
    /// The caller's own role and effective tabs. No admin rights needed: it
    /// reveals nothing the caller does not already have.
    /// </summary>
    public async Task<MyTabsDto> GetMyTabsAsync(User user, CancellationToken ct = default)
    {
        var access = await _access.GetAsync(user, ct);
        return new MyTabsDto(
            user.Id, user.Username, access.Role, UserRoles.LabelFor(access.Role),
            AccessPolicy.IsSuperUser(user), access.Tabs());
    }

    /// <summary>
    /// Everything the shell needs to draw itself for the caller, in one response:
    /// tabs (with their labels and menu groups), sections, actions and the model
    /// policy. Only the caller's own keys — never the rest of the catalog.
    /// </summary>
    public async Task<MyAccessDto> GetMyAccessAsync(User user, CancellationToken ct = default)
    {
        var access = await _access.GetAsync(user, ct);
        var allowedModels = await _models.AllowedAsync(user, ct);

        var modules = PermissionCatalog.All
            .Where(p => p.Kind == PermissionCatalog.KindTab && access.Has(p.Key))
            .Select(p => new MyModuleDto(p.Key["tab:".Length..], p.DisplayLabel, p.En, p.Bn, p.Group))
            .ToList();

        return new MyAccessDto(
            user.Id, user.Username, access.Role, UserRoles.LabelFor(access.Role), AccessPolicy.IsSuperUser(user),
            access.Tabs(),
            access.Allowed(PermissionCatalog.KindSection),
            access.Allowed(PermissionCatalog.KindAction),
            modules,
            new MyModelsDto(allowedModels.ToList(), await _models.DefaultForAsync(user, ct), allowedModels.Count == 1),
            AccessPolicy.Revision);
    }

    /// <summary>The tab catalogue, for the admin checklist.</summary>
    public IReadOnlyList<TabOptionDto> ListTabs() =>
        TabCatalog.All.Select(k => new TabOptionDto(k, TabCatalog.LabelFor(k))).ToList();

    /// <summary>Every role with the tabs its preset gives, for the admin picker.</summary>
    public async Task<IReadOnlyList<RoleOptionDto>> ListRolesAsync(CancellationToken ct = default)
    {
        var list = new List<RoleOptionDto>();
        foreach (var r in UserRoles.All)
            list.Add(new RoleOptionDto(r, UserRoles.LabelFor(r), (await _access.ForRoleAsync(r, ct)).Tabs()));
        return list;
    }

    /// <summary>The permission catalog, for the Access screen.</summary>
    public static CatalogDto Catalog() => new(
        PermissionCatalog.Groups.ToList(),
        PermissionCatalog.All.Select(p => new PermissionDto(
            p.Key, p.Kind, p.En, p.Bn, p.DisplayLabel, p.Group, p.Parent, AccessPolicy.IsAlwaysOn(p.Key))).ToList());

    public Task<bool> CanManageAsync(User caller, CancellationToken ct = default) =>
        _access.HasAsync(caller, PermissionKeys.AccessManage, ct);

    // ---- Role presets ----

    public async Task<RbacResult<RolePresetsDto>> GetRolePresetsAsync(User caller, CancellationToken ct = default)
    {
        if (!await CanManageAsync(caller, ct))
            return RbacResult<RolePresetsDto>.Fail(RbacOutcome.Forbidden, ManagersOnly);

        var counts = await UserCountsAsync(ct);
        var roles = new List<RolePresetDto>();
        foreach (var role in UserRoles.All)
            roles.Add(await ProjectPresetAsync(role, counts, ct));
        return RbacResult<RolePresetsDto>.Success(new RolePresetsDto(roles));
    }

    /// <summary>
    /// Saves what one role gets. Only the keys whose value changes are written,
    /// each logged with before → after. The whole body is refused — nothing
    /// written — when any key is unknown or any change breaks an invariant.
    /// </summary>
    public async Task<RbacResult<SaveRolePresetResult>> SaveRolePresetAsync(
        User caller, string role, SaveRolePresetInput input, CancellationToken ct = default)
    {
        if (!await CanManageAsync(caller, ct))
            return RbacResult<SaveRolePresetResult>.Fail(RbacOutcome.Forbidden, ManagersOnly);

        if (!UserRoles.IsKnown(role))
            return RbacResult<SaveRolePresetResult>.Fail(RbacOutcome.Invalid, $"'{role}' is not a known role.");
        var canonical = UserRoles.Normalize(role);

        var wanted = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in input.Values ?? new Dictionary<string, bool>())
        {
            var def = PermissionCatalog.Find(key);
            if (def is null)
                return RbacResult<SaveRolePresetResult>.Fail(RbacOutcome.Invalid, $"'{key}' is not a permission key.");
            // Phase P1 of docs/RBAC_ROLES_PROMPT.md: role presets are edited for tabs;
            // sections and actions keep their seeded values until the expanded Roles page.
            if (def.Kind != PermissionCatalog.KindTab)
                return RbacResult<SaveRolePresetResult>.Fail(RbacOutcome.Invalid,
                    $"'{def.Key}' is a {def.Kind}: only tab keys can be changed here yet.");
            wanted[def.Key] = value;
        }
        if (wanted.Count == 0)
            return RbacResult<SaveRolePresetResult>.Fail(RbacOutcome.Invalid, "Send at least one key in values.");

        if (AccessPolicy.IsSuperUserRole(canonical) && wanted.Values.Any(v => !v))
            return RbacResult<SaveRolePresetResult>.Breaks(AccessInvariants.SystemAdminHasEverything,
                "SystemAdmin always has every tab, section and action, so nothing can be turned off for it.");

        if (wanted.FirstOrDefault(kv => AccessPolicy.IsAlwaysOn(kv.Key) && !kv.Value).Key is { } off)
            return RbacResult<SaveRolePresetResult>.Breaks(AccessInvariants.SettingsAlwaysOn,
                $"{PermissionCatalog.Find(off)!.En} holds the account and sign-out, so it is on for every role.");

        var current = await _access.PresetAsync(canonical, ct);
        var next = new Dictionary<string, bool>(current, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in wanted) next[k] = v;

        // A manager who is not SystemAdmin must not save away their own way back in.
        if (!AccessPolicy.IsSuperUser(caller) && UserRoles.Normalize(caller.Role) == canonical)
        {
            var own = await _db.UserPermissionOverrides.AsNoTracking().Where(o => o.UserId == caller.Id)
                .ToDictionaryAsync(o => o.Key, o => o.Effect, StringComparer.OrdinalIgnoreCase, ct);
            var after = AccessPolicy.Compute(canonical, next, own);
            if (!after.Has(PermissionKeys.TabRbac) || !after.Has(PermissionKeys.AccessManage))
                return RbacResult<SaveRolePresetResult>.Breaks(AccessInvariants.OwnAccess,
                    "This change would take away your own access to Access.");
        }

        var changes = wanted
            .Where(kv => (current.TryGetValue(kv.Key, out var was) && was) != kv.Value)
            .Select(kv => new AccessChangeDto(kv.Key, current.TryGetValue(kv.Key, out var was) && was, kv.Value))
            .ToList();

        if (changes.Count > 0)
        {
            var note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim();
            var keys = changes.Select(c => c.Key).ToList();
            var rows = await _db.RolePermissions.Where(r => r.Role == canonical && keys.Contains(r.Key)).ToListAsync(ct);
            var now = DateTime.UtcNow;
            foreach (var c in changes)
            {
                var row = rows.FirstOrDefault(r => string.Equals(r.Key, c.Key, StringComparison.OrdinalIgnoreCase));
                if (row is null)
                {
                    row = new RolePermission { Role = canonical, Key = c.Key };
                    _db.RolePermissions.Add(row);
                }
                row.Allowed = c.After;
                row.UpdatedByUserId = caller.Id;
                row.UpdatedAtUtc = now;
                _db.AccessChangeLog.Add(new AccessChangeLogEntry
                {
                    AtUtc = now, ByUserId = caller.Id, Scope = AccessChangeLogEntry.ScopeRole, Target = canonical,
                    Key = c.Key, Before = Bool(c.Before), After = Bool(c.After), Note = note,
                });
            }
            await _db.SaveChangesAsync(ct);
            AccessPolicy.Invalidate();
        }

        var counts = await UserCountsAsync(ct);
        var preset = await ProjectPresetAsync(canonical, counts, ct);
        return RbacResult<SaveRolePresetResult>.Success(new SaveRolePresetResult(
            canonical, changes, changes.Count > 0 ? preset.UserCount : 0, preset.Values));
    }

    private async Task<RolePresetDto> ProjectPresetAsync(string role, IReadOnlyDictionary<string, int> counts, CancellationToken ct)
    {
        var effective = await _access.ForRoleAsync(role, ct);
        var preset = await _access.PresetAsync(role, ct);
        // What the role's row says, except where an invariant holds a key on.
        var values = PermissionCatalog.All.ToDictionary(
            p => p.Key,
            p => effective.Decisions[p.Key].Reason == AccessReason.Invariant || (preset.TryGetValue(p.Key, out var v) && v));
        return new RolePresetDto(role, UserRoles.LabelFor(role), AccessPolicy.IsSuperUserRole(role),
            counts.GetValueOrDefault(role), values);
    }

    private async Task<IReadOnlyDictionary<string, int>> UserCountsAsync(CancellationToken ct) =>
        (await _db.Users.AsNoTracking().Select(u => u.Role).ToListAsync(ct))
            .GroupBy(UserRoles.Normalize)
            .ToDictionary(g => g.Key, g => g.Count());

    private static string Bool(bool v) => v ? "true" : "false";

    // ---- Users ----

    public async Task<RbacResult<UserPageDto>> SearchUsersAsync(
        User caller, string? search, int page, int pageSize, CancellationToken ct = default)
    {
        if (!await CanManageAsync(caller, ct))
            return RbacResult<UserPageDto>.Fail(RbacOutcome.Forbidden, ManagersOnly);

        page = page < 1 ? 1 : page;
        pageSize = pageSize is < 1 or > MaxPageSize ? DefaultPageSize : pageSize;

        var (users, total) = await _repo.SearchUsersAsync(search, page, pageSize, ct);

        var items = new List<UserSummaryDto>();
        foreach (var u in users)
        {
            var access = await _access.GetAsync(u, ct);
            items.Add(new UserSummaryDto(u.Id, u.Username, access.Role, access.Tabs().Count, u.CreatedAt));
        }

        return RbacResult<UserPageDto>.Success(new UserPageDto(items, total, page, pageSize));
    }

    public async Task<RbacResult<UserDetailDto>> GetUserAsync(
        User caller, int userId, CancellationToken ct = default)
    {
        if (!await CanManageAsync(caller, ct))
            return RbacResult<UserDetailDto>.Fail(RbacOutcome.Forbidden, ManagersOnly);

        var user = await _repo.GetUserAsync(userId, ct);
        if (user is null)
            return RbacResult<UserDetailDto>.Fail(RbacOutcome.NotFound, "No such user.");

        return RbacResult<UserDetailDto>.Success(await ProjectDetailAsync(user, ct));
    }

    /// <summary>
    /// Sets a user's role. Refused when the caller may not manage access, is the
    /// target themselves, or when this would demote the last remaining SystemAdmin.
    /// Only a SystemAdmin may make someone SystemAdmin or change a SystemAdmin's role.
    /// </summary>
    public async Task<RbacResult<UserDetailDto>> SetRoleAsync(
        User caller, int userId, SetRoleInput input, CancellationToken ct = default)
    {
        if (!await CanManageAsync(caller, ct))
            return RbacResult<UserDetailDto>.Fail(RbacOutcome.Forbidden, ManagersOnly);

        if (caller.Id == userId)
            return RbacResult<UserDetailDto>.Fail(
                RbacOutcome.Forbidden, "You cannot change your own role.");

        if (!UserRoles.IsKnown(input.Role))
            return RbacResult<UserDetailDto>.Fail(
                RbacOutcome.Invalid,
                $"Role must be one of: {string.Join(", ", UserRoles.All)}.");

        var user = await _repo.GetUserAsync(userId, ct);
        if (user is null)
            return RbacResult<UserDetailDto>.Fail(RbacOutcome.NotFound, "No such user.");

        var next = UserRoles.Normalize(input.Role);
        var current = UserRoles.Normalize(user.Role);
        var demotesAdmin = AccessPolicy.IsSuperUserRole(current) && !AccessPolicy.IsSuperUserRole(next);

        // Demoting the only admin would leave nobody able to promote anyone.
        if (demotesAdmin && await _repo.CountSystemAdminsAsync(ct) <= 1)
            return RbacResult<UserDetailDto>.Breaks(AccessInvariants.LastSystemAdmin,
                "This is the last SystemAdmin — promote someone else first.");

        if ((demotesAdmin || AccessPolicy.IsSuperUserRole(next)) && !AccessPolicy.IsSuperUser(caller))
            return RbacResult<UserDetailDto>.Fail(RbacOutcome.Forbidden,
                "Only a SystemAdmin can give or take away the SystemAdmin role.");

        if (current != next)
        {
            user.Role = next;
            _db.AccessChangeLog.Add(new AccessChangeLogEntry
            {
                ByUserId = caller.Id, Scope = AccessChangeLogEntry.ScopeUser, Target = user.Id.ToString(),
                Key = "role", Before = current, After = next,
            });
            await _repo.SaveChangesAsync(ct);
            AccessPolicy.Invalidate();
        }

        return RbacResult<UserDetailDto>.Success(await ProjectDetailAsync(user, ct));
    }

    /// <summary>
    /// Replaces a user's explicit tab grants (allow overrides on tab keys). Keys
    /// the role already gives are dropped rather than stored — storing them would
    /// make a later demotion silently keep the tab — and unknown keys are ignored
    /// so the table only ever names real tabs.
    /// </summary>
    public async Task<RbacResult<UserDetailDto>> SetTabsAsync(
        User caller, int userId, SetTabsInput input, CancellationToken ct = default)
    {
        if (!await CanManageAsync(caller, ct))
            return RbacResult<UserDetailDto>.Fail(RbacOutcome.Forbidden, ManagersOnly);

        if (caller.Id == userId)
            return RbacResult<UserDetailDto>.Fail(
                RbacOutcome.Forbidden, "You cannot change your own tabs.");

        var user = await _repo.GetUserAsync(userId, ct);
        if (user is null)
            return RbacResult<UserDetailDto>.Fail(RbacOutcome.NotFound, "No such user.");

        var baseline = new HashSet<string>((await _access.ForRoleAsync(user.Role, ct)).Tabs(), StringComparer.OrdinalIgnoreCase);

        var wanted = (input.TabKeys ?? new List<string>())
            .Select(TabCatalog.Canonical)
            .Where(k => k is not null && !baseline.Contains(k) && TabCatalog.IsGrantable(k))
            .Select(k => k!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var (added, removed) = await _repo.ReplaceGrantsAsync(userId, wanted, caller.Id, ct);
        foreach (var (key, before, after) in added.Select(k => (k, (string?)null, (string?)UserPermissionOverride.Allow))
                     .Concat(removed.Select(k => (k, (string?)UserPermissionOverride.Allow, (string?)null))))
            _db.AccessChangeLog.Add(new AccessChangeLogEntry
            {
                ByUserId = caller.Id, Scope = AccessChangeLogEntry.ScopeUser, Target = user.Id.ToString(),
                Key = key, Before = before, After = after,
            });
        await _repo.SaveChangesAsync(ct);
        if (added.Count + removed.Count > 0) AccessPolicy.Invalidate();

        return RbacResult<UserDetailDto>.Success(await ProjectDetailAsync(user, ct));
    }

    // ---- Helpers ----

    private async Task<UserDetailDto> ProjectDetailAsync(User user, CancellationToken ct)
    {
        var grants = await _repo.GetGrantsAsync(user.Id, ct);
        var access = await _access.GetAsync(user, ct);

        var granted = grants
            .Select(TabCatalog.Canonical)
            .Where(k => k is not null)
            .Select(k => k!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new UserDetailDto(
            user.Id,
            user.Username,
            access.Role,
            user.CreatedAt,
            access.Tabs(),
            (await _access.ForRoleAsync(access.Role, ct)).Tabs(),
            granted);
    }
}
