// RBAC — EF Core / SQLite implementation. LINQ only; the sole raw SQL in this
// module is the ALTER TABLE / CREATE TABLE in SchemaUpgrade, which EF can't express.

using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.Data;
using RabbitIC.Api.Models;

namespace RabbitIC.Api.Repositories;

public class RbacRepository : IRbacRepository
{
    private readonly AppDbContext _db;

    public RbacRepository(AppDbContext db) => _db = db;

    public async Task<(List<User> Items, int Total)> SearchUsersAsync(
        string? search, int page, int pageSize, CancellationToken ct = default)
    {
        var query = _db.Users.AsNoTracking().AsQueryable();

        var term = search?.Trim();
        if (!string.IsNullOrEmpty(term))
        {
            // ToLower rather than Contains(…, StringComparison): EF translates the
            // former to SQL, while the latter would silently fall back to client
            // evaluation over every user row.
            var lowered = term.ToLower();
            query = query.Where(u => u.Username.ToLower().Contains(lowered));
        }

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderBy(u => u.Username)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, total);
    }

    public Task<User?> GetUserAsync(int userId, CancellationToken ct = default) =>
        _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);

    public Task<User?> GetUserByUsernameAsync(string username, CancellationToken ct = default) =>
        _db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);

    // Tab grants are "allow" overrides on "tab:<key>" (UserPermissionOverrides);
    // these three read and write only those rows, so the grant API keeps its shape.

    public Task<List<string>> GetGrantsAsync(int userId, CancellationToken ct = default) =>
        _db.UserPermissionOverrides
            .Where(o => o.UserId == userId && o.Effect == UserPermissionOverride.Allow && o.Key.StartsWith(TabPrefix))
            .Select(o => o.Key.Substring(TabPrefix.Length))
            .ToListAsync(ct);

    public async Task<Dictionary<int, int>> GetGrantCountsAsync(
        List<int> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0) return new Dictionary<int, int>();

        var counts = await _db.UserPermissionOverrides
            .Where(o => userIds.Contains(o.UserId) && o.Effect == UserPermissionOverride.Allow && o.Key.StartsWith(TabPrefix))
            .GroupBy(o => o.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return counts.ToDictionary(c => c.UserId, c => c.Count);
    }

    public async Task<(List<string> Added, List<string> Removed)> ReplaceGrantsAsync(
        int userId, IReadOnlyCollection<string> tabKeys, int byUserId, CancellationToken ct = default)
    {
        var existing = await _db.UserPermissionOverrides
            .Where(o => o.UserId == userId && o.Effect == UserPermissionOverride.Allow && o.Key.StartsWith(TabPrefix))
            .ToListAsync(ct);

        // Diff rather than delete-all-then-insert, so the time of a grant that was
        // already there survives and the key is never transiently missing.
        var wanted = new HashSet<string>(tabKeys.Select(k => TabPrefix + k), StringComparer.OrdinalIgnoreCase);
        var removed = existing.Where(r => !wanted.Contains(r.Key)).ToList();
        foreach (var row in removed) _db.UserPermissionOverrides.Remove(row);

        var present = new HashSet<string>(existing.Select(r => r.Key), StringComparer.OrdinalIgnoreCase);
        var added = wanted.Where(k => !present.Contains(k)).ToList();
        foreach (var key in added)
            _db.UserPermissionOverrides.Add(new UserPermissionOverride
            {
                UserId = userId, Key = key, Effect = UserPermissionOverride.Allow,
                UpdatedByUserId = byUserId, UpdatedAtUtc = DateTime.UtcNow,
            });

        return (added, removed.Select(r => r.Key).ToList());
    }

    private const string TabPrefix = "tab:";

    /// <summary>
    /// Case-insensitive on purpose: authorisation normalises the role before
    /// comparing, so a row stored as "systemadmin" still grants admin rights. If
    /// this count matched case-sensitively, such a row would hold power without
    /// being counted — and the last-admin guardrail would let the real last admin
    /// be demoted. The two must agree on what counts as an admin.
    /// </summary>
    public Task<int> CountSystemAdminsAsync(CancellationToken ct = default) =>
        _db.Users.CountAsync(u => u.Role.ToLower() == AdminRoleLowered, ct);

    private static readonly string AdminRoleLowered = UserRoles.SystemAdmin.ToLower();

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}
