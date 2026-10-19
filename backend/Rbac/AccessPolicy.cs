// Access — what a user may open and do. THE rule, in one place.
//
//   effective(user, key) =
//       SystemAdmin                → allowed
//       key is tab:settings        → allowed
//       user override deny         → denied
//       user override allow        → allowed
//       otherwise                  → RolePermissions[role, key]
//     …and then every parent must be effective too.
//
// TabAccessMiddleware, ModelPolicy and every capability check (HR library
// editing, Rulebook confirm, Procedures upload, …) ask here. A copy of this
// comparison anywhere else would be a second rule that drifts.
//
// A user's answer is cached under their id, their role and the revision; any
// change to a preset, an override or a role bumps the revision, so the next
// request of every user reads the saved rows — no restart. The revision also
// goes out on every response as X-Access-Rev, so a shell can tell its menu is
// stale without polling.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using RabbitIC.Api.Data;
using RabbitIC.Api.Models;

namespace RabbitIC.Api.Rbac;

/// <summary>Why a key is allowed or not — what the User page will show (§3.4).</summary>
public static class AccessReason
{
    public const string Invariant = "invariant";
    public const string Role = "role";
    public const string Allow = "allow";
    public const string Deny = "deny";
    public const string ParentMissing = "parent missing";
}

public sealed record AccessDecision(bool Allowed, string Reason);

/// <summary>One user's (or one role's) effective access over the whole catalog.</summary>
public sealed class EffectiveAccess
{
    public required string Role { get; init; }
    public required IReadOnlyDictionary<string, AccessDecision> Decisions { get; init; }

    public bool Has(string key) => Decisions.TryGetValue(key, out var d) && d.Allowed;

    /// <summary>Allowed keys of one kind, in catalog order.</summary>
    public List<string> Allowed(string kind) =>
        PermissionCatalog.All.Where(p => p.Kind == kind && Has(p.Key)).Select(p => p.Key).ToList();

    /// <summary>Allowed tab keys without the prefix, in catalog order — the shape /api/me/tabs has always had.</summary>
    public List<string> Tabs() => Allowed(PermissionCatalog.KindTab).Select(k => k["tab:".Length..]).ToList();
}

public sealed class AccessPolicy(AppDbContext db, IMemoryCache cache)
{
    private const string PresetsKey = "access:presets";

    // Process-wide, like the cache. A boot stamp keeps two runs from sharing a value.
    private static readonly string Boot = DateTime.UtcNow.Ticks.ToString("x");
    private static long _counter;

    /// <summary>Changes whenever any preset, override or role changes. Sent as X-Access-Rev.</summary>
    public static string Revision => $"{Boot}.{Interlocked.Read(ref _counter)}";

    /// <summary>
    /// The one SystemAdmin rule: always everything, never reducible. ModelPolicy
    /// asks here too, so "SystemAdmin is unrestricted" is decided once.
    /// </summary>
    public static bool IsSuperUser(User user) => IsSuperUserRole(user.Role);

    public static bool IsSuperUserRole(string? role) => UserRoles.Normalize(role) == UserRoles.SystemAdmin;

    /// <summary>The keys no role may ever lose (§0 rule 7).</summary>
    public static bool IsAlwaysOn(string key) =>
        string.Equals(key, PermissionKeys.TabSettings, StringComparison.OrdinalIgnoreCase);

    // ---- reads ----

    public async Task<EffectiveAccess> GetAsync(User user, CancellationToken ct = default)
    {
        var role = UserRoles.Normalize(user.Role);
        var key = $"access:{Revision}:{user.Id}:{role}";
        if (cache.TryGetValue(key, out EffectiveAccess? hit) && hit is not null) return hit;

        var overrides = await db.UserPermissionOverrides.AsNoTracking()
            .Where(o => o.UserId == user.Id)
            .ToDictionaryAsync(o => o.Key, o => o.Effect, StringComparer.OrdinalIgnoreCase, ct);

        var access = Compute(role, await PresetAsync(role, ct), overrides);
        cache.Set(key, access, TimeSpan.FromMinutes(10));
        return access;
    }

    public async Task<bool> HasAsync(User user, string key, CancellationToken ct = default) =>
        (await GetAsync(user, ct)).Has(key);

    /// <summary>What a role gets before any override.</summary>
    public async Task<EffectiveAccess> ForRoleAsync(string role, CancellationToken ct = default)
    {
        var canonical = UserRoles.Normalize(role);
        return Compute(canonical, await PresetAsync(canonical, ct), new Dictionary<string, string>());
    }

    /// <summary>One role's stored values, key → allowed. A key with no row reads as not allowed.</summary>
    public async Task<IReadOnlyDictionary<string, bool>> PresetAsync(string role, CancellationToken ct = default)
    {
        var all = await PresetsAsync(ct);
        return all.TryGetValue(UserRoles.Normalize(role), out var preset)
            ? preset
            : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Every role's preset. Cached; dropped by <see cref="Invalidate"/>.</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, bool>>> PresetsAsync(CancellationToken ct = default)
    {
        var key = $"{PresetsKey}:{Revision}";
        if (cache.TryGetValue(key, out IReadOnlyDictionary<string, IReadOnlyDictionary<string, bool>>? hit) && hit is not null)
            return hit;

        var rows = await db.RolePermissions.AsNoTracking().ToListAsync(ct);
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, bool>> map = rows
            .GroupBy(r => UserRoles.Normalize(r.Role))
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyDictionary<string, bool>)g
                    .GroupBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(x => x.Key, x => x.First().Allowed, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

        cache.Set(key, map, TimeSpan.FromMinutes(10));
        return map;
    }

    /// <summary>
    /// Every user's next request reads the saved rows. Called after every change
    /// to a preset, an override or a user's role.
    /// </summary>
    public static void Invalidate() => Interlocked.Increment(ref _counter);

    // ---- the rule ----

    /// <summary>
    /// effective(role, preset, overrides) for every catalog key — the function in
    /// the header. Pure, so a save can ask what a change WOULD do before writing it.
    /// </summary>
    public static EffectiveAccess Compute(
        string role, IReadOnlyDictionary<string, bool> preset, IReadOnlyDictionary<string, string> overrides)
    {
        var super = IsSuperUserRole(role);
        var raw = new Dictionary<string, AccessDecision>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in PermissionCatalog.All)
        {
            overrides.TryGetValue(p.Key, out var effect);
            raw[p.Key] =
                super || IsAlwaysOn(p.Key) ? new(true, AccessReason.Invariant)
                : effect == UserPermissionOverride.Deny ? new(false, AccessReason.Deny)
                : effect == UserPermissionOverride.Allow ? new(true, AccessReason.Allow)
                : new(preset.TryGetValue(p.Key, out var allowed) && allowed, AccessReason.Role);
        }

        // Parents: the catalog lists a parent before its children, so one pass
        // in catalog order sees every parent already settled.
        var final = new Dictionary<string, AccessDecision>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in PermissionCatalog.All)
        {
            var d = raw[p.Key];
            if (d.Allowed && p.Parent is not null && !(final.TryGetValue(p.Parent, out var parent) && parent.Allowed))
                d = new(false, AccessReason.ParentMissing);
            final[p.Key] = d;
        }

        return new EffectiveAccess { Role = UserRoles.Normalize(role), Decisions = final };
    }
}
