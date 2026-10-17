// RBAC — business rules and guardrails.
//
// Every refusal in this module is decided here, never in an endpoint. The three
// guardrails are:
//
//   1. Only a SystemAdmin may call the admin surface at all.
//   2. Nobody may change their OWN role or tabs — an admin who could demote
//      themselves could lock themselves out, and a stolen session could
//      self-escalate.
//   3. The LAST SystemAdmin may not be demoted — there must always be someone
//      left who can hand out the role.
//
// Endpoints translate the returned outcome into a status code and never re-derive
// a rule, so the UI and the API cannot disagree about what is allowed.

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

    /// <summary>The caller is not a SystemAdmin, or is acting on themselves.</summary>
    Forbidden,

    /// <summary>Would leave the system with no SystemAdmin.</summary>
    Conflict,

    /// <summary>The input named an unknown role.</summary>
    Invalid,
}

/// <summary>An outcome, the value when it succeeded, and a reason when it did not.</summary>
public record RbacResult<T>(RbacOutcome Outcome, T? Value = default, string? Error = null)
{
    public static RbacResult<T> Success(T value) => new(RbacOutcome.Ok, value);

    public static RbacResult<T> Fail(RbacOutcome outcome, string error) =>
        new(outcome, default, error);
}

public class RbacService
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    private readonly IRbacRepository _repo;

    public RbacService(IRbacRepository repo) => _repo = repo;

    // ---- Reads any signed-in user may make about themselves ----

    /// <summary>
    /// The caller's own role and effective tabs. No admin rights needed: it
    /// reveals nothing the caller does not already have.
    /// </summary>
    public async Task<MyTabsDto> GetMyTabsAsync(User user, CancellationToken ct = default)
    {
        var grants = await _repo.GetGrantsAsync(user.Id, ct);
        var role = UserRoles.Normalize(user.Role);

        return new MyTabsDto(
            user.Id,
            user.Username,
            role,
            UserRoles.LabelFor(role),
            role == UserRoles.SystemAdmin,
            TabCatalog.EffectiveTabs(role, grants));
    }

    /// <summary>
    /// Whether a user may see one specific tab. This is the enforcement primitive:
    /// an endpoint belonging to a tab calls it and answers 403 when false, so a
    /// user who reaches a forbidden API directly is refused even though the UI
    /// would never have shown them the tab.
    /// </summary>
    public async Task<bool> CanAccessTabAsync(User user, string tabKey, CancellationToken ct = default)
    {
        var canonical = TabCatalog.Canonical(tabKey);
        if (canonical is null) return false;

        var grants = await _repo.GetGrantsAsync(user.Id, ct);
        return TabCatalog.EffectiveTabs(user.Role, grants)
            .Contains(canonical, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The tab catalogue, for the admin checklist.</summary>
    public IReadOnlyList<TabOptionDto> ListTabs() =>
        TabCatalog.All.Select(k => new TabOptionDto(k, TabCatalog.LabelFor(k))).ToList();

    /// <summary>Every role with its baseline tabs, in catalogue order, for the admin picker.</summary>
    public IReadOnlyList<RoleOptionDto> ListRoles() =>
        UserRoles.All
            .Select(r => new RoleOptionDto(
                r, UserRoles.LabelFor(r), TabCatalog.EffectiveTabs(r, Array.Empty<string>())))
            .ToList();

    // ---- Admin surface ----

    public async Task<RbacResult<UserPageDto>> SearchUsersAsync(
        User caller, string? search, int page, int pageSize, CancellationToken ct = default)
    {
        if (!UserRoles.IsSystemAdmin(caller))
            return RbacResult<UserPageDto>.Fail(RbacOutcome.Forbidden, "SystemAdmin role required.");

        page = page < 1 ? 1 : page;
        pageSize = pageSize is < 1 or > MaxPageSize ? DefaultPageSize : pageSize;

        var (users, total) = await _repo.SearchUsersAsync(search, page, pageSize, ct);
        var grantCounts = await _repo.GetGrantCountsAsync(users.Select(u => u.Id).ToList(), ct);

        // Grants that merely repeat the role baseline are never stored (SetTabsAsync
        // drops them), so baseline + stored grants is the effective tab count
        // without having to load every grant key.
        var items = users
            .Select(u =>
            {
                var role = UserRoles.Normalize(u.Role);
                var extra = grantCounts.TryGetValue(u.Id, out var c) ? c : 0;

                return new UserSummaryDto(
                    u.Id, u.Username, role,
                    TabCatalog.DefaultTabsFor(role).Count + extra,
                    u.CreatedAt);
            })
            .ToList();

        return RbacResult<UserPageDto>.Success(new UserPageDto(items, total, page, pageSize));
    }

    public async Task<RbacResult<UserDetailDto>> GetUserAsync(
        User caller, int userId, CancellationToken ct = default)
    {
        if (!UserRoles.IsSystemAdmin(caller))
            return RbacResult<UserDetailDto>.Fail(RbacOutcome.Forbidden, "SystemAdmin role required.");

        var user = await _repo.GetUserAsync(userId, ct);
        if (user is null)
            return RbacResult<UserDetailDto>.Fail(RbacOutcome.NotFound, "No such user.");

        return RbacResult<UserDetailDto>.Success(await ProjectDetailAsync(user, ct));
    }

    /// <summary>
    /// Sets a user's role. Refused when the caller is not an admin, is the target
    /// themselves, or when this would demote the last remaining SystemAdmin.
    /// </summary>
    public async Task<RbacResult<UserDetailDto>> SetRoleAsync(
        User caller, int userId, SetRoleInput input, CancellationToken ct = default)
    {
        if (!UserRoles.IsSystemAdmin(caller))
            return RbacResult<UserDetailDto>.Fail(RbacOutcome.Forbidden, "SystemAdmin role required.");

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

        // Demoting the only admin would leave nobody able to promote anyone.
        if (current == UserRoles.SystemAdmin && next != UserRoles.SystemAdmin)
        {
            var admins = await _repo.CountSystemAdminsAsync(ct);
            if (admins <= 1)
                return RbacResult<UserDetailDto>.Fail(
                    RbacOutcome.Conflict,
                    "This is the last SystemAdmin — promote someone else first.");
        }

        user.Role = next;
        await _repo.SaveChangesAsync(ct);

        return RbacResult<UserDetailDto>.Success(await ProjectDetailAsync(user, ct));
    }

    /// <summary>
    /// Replaces a user's explicit tab grants. Keys the role already covers are
    /// dropped rather than stored — a baseline is not something to persist, and
    /// storing it would make a later demotion silently keep the tab — and unknown
    /// keys are ignored so the table only ever names real tabs.
    /// </summary>
    public async Task<RbacResult<UserDetailDto>> SetTabsAsync(
        User caller, int userId, SetTabsInput input, CancellationToken ct = default)
    {
        if (!UserRoles.IsSystemAdmin(caller))
            return RbacResult<UserDetailDto>.Fail(RbacOutcome.Forbidden, "SystemAdmin role required.");

        if (caller.Id == userId)
            return RbacResult<UserDetailDto>.Fail(
                RbacOutcome.Forbidden, "You cannot change your own tabs.");

        var user = await _repo.GetUserAsync(userId, ct);
        if (user is null)
            return RbacResult<UserDetailDto>.Fail(RbacOutcome.NotFound, "No such user.");

        var baseline = new HashSet<string>(
            TabCatalog.DefaultTabsFor(user.Role), StringComparer.OrdinalIgnoreCase);

        var wanted = (input.TabKeys ?? new List<string>())
            .Select(TabCatalog.Canonical)
            .Where(k => k is not null && !baseline.Contains(k) && TabCatalog.IsGrantable(k))
            .Select(k => k!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        await _repo.ReplaceGrantsAsync(userId, wanted, ct);
        await _repo.SaveChangesAsync(ct);

        return RbacResult<UserDetailDto>.Success(await ProjectDetailAsync(user, ct));
    }

    // ---- Helpers ----

    private async Task<UserDetailDto> ProjectDetailAsync(User user, CancellationToken ct)
    {
        var grants = await _repo.GetGrantsAsync(user.Id, ct);
        var role = UserRoles.Normalize(user.Role);

        var defaults = TabCatalog.DefaultTabsFor(role).ToList();

        var granted = grants
            .Select(TabCatalog.Canonical)
            .Where(k => k is not null)
            .Select(k => k!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new UserDetailDto(
            user.Id,
            user.Username,
            role,
            user.CreatedAt,
            TabCatalog.EffectiveTabs(role, granted),
            defaults,
            granted);
    }
}
