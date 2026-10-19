// RBAC — data access for roles and tab grants.
//
// Reads are plain LINQ over EF Core. The repository knows nothing about who may
// do what: every guardrail lives in RbacService, so there is exactly one place
// to read when asking "can this be refused?".

using RabbitIC.Api.Models;

namespace RabbitIC.Api.Repositories;

public interface IRbacRepository
{
    /// <summary>
    /// One page of users whose username contains <paramref name="search"/>
    /// (case-insensitive; null or blank matches everyone), ordered by username.
    /// </summary>
    Task<(List<User> Items, int Total)> SearchUsersAsync(
        string? search, int page, int pageSize, CancellationToken ct = default);

    Task<User?> GetUserAsync(int userId, CancellationToken ct = default);

    Task<User?> GetUserByUsernameAsync(string username, CancellationToken ct = default);

    /// <summary>Granted tab keys for one user, without the "tab:" prefix.</summary>
    Task<List<string>> GetGrantsAsync(int userId, CancellationToken ct = default);

    /// <summary>Grant counts for a set of users, keyed by user id — avoids an N+1 over the list.</summary>
    Task<Dictionary<int, int>> GetGrantCountsAsync(List<int> userIds, CancellationToken ct = default);

    /// <summary>
    /// Replaces a user's tab grants (allow overrides on tab keys) with exactly
    /// <paramref name="tabKeys"/>; returns the keys added and removed, for the change log.
    /// </summary>
    Task<(List<string> Added, List<string> Removed)> ReplaceGrantsAsync(
        int userId, IReadOnlyCollection<string> tabKeys, int byUserId, CancellationToken ct = default);

    /// <summary>
    /// How many users currently hold SystemAdmin. The last-admin guardrail is a
    /// count check, so this is the one query it depends on.
    /// </summary>
    Task<int> CountSystemAdminsAsync(CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}
