using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.Data;
using RabbitIC.Api.Models;

namespace RabbitIC.Api.Services;

/// <summary>
/// EF-backed <see cref="ISearchHistoryService"/>.
///
/// Owner scoping is a filter on every query rather than a check after the fact,
/// so "not mine" and "not there" are indistinguishable by construction: the
/// caller gets null either way and answers 404. A 403 would confirm the row
/// exists, which is exactly what should not be revealed to someone probing ids.
/// </summary>
public class SearchHistoryService : ISearchHistoryService
{
    /// <summary>
    /// Queries longer than this are stored truncated. A pasted wall of text is
    /// still a usable history entry at this length, and the column stays sane.
    /// </summary>
    private const int MaxQueryChars = 500;

    private const int MaxTitleChars = 200;

    private readonly AppDbContext _db;

    public SearchHistoryService(AppDbContext db) => _db = db;

    public async Task<SearchHistoryDto> RecordAsync(
        int userId, string query, int maxResults, CancellationToken ct = default)
    {
        var text = Normalize(query);
        var now = DateTime.Now;

        // Dedupe on the query itself, ignoring case and surrounding whitespace, and
        // deliberately including soft-deleted rows: running a deleted query again
        // revives it rather than leaving a hidden duplicate behind.
        var existing = await _db.SearchHistories
            .Where(h => h.OwnerUserId == userId && h.Query.ToLower() == text.ToLower())
            .OrderBy(h => h.Id)
            .FirstOrDefaultAsync(ct);

        if (existing is not null)
        {
            existing.RunCount++;
            existing.LastRunAt = now;
            existing.UpdatedAt = now;
            existing.MaxResults = maxResults;   // the setting it most recently ran with

            if (existing.IsDeleted)
            {
                existing.IsDeleted = false;
                existing.DeletedAt = null;
            }

            await _db.SaveChangesAsync(ct);
            return Project(existing);
        }

        var row = new SearchHistory
        {
            OwnerUserId = userId,
            Query = text,
            MaxResults = maxResults,
            RunCount = 1,
            LastRunAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.SearchHistories.Add(row);
        await _db.SaveChangesAsync(ct);
        return Project(row);
    }

    public async Task<IReadOnlyList<SearchHistoryDto>> ListAsync(int userId, CancellationToken ct = default) =>
        await Owned(userId)
            .Where(h => !h.IsDeleted)
            .OrderByDescending(h => h.LastRunAt).ThenByDescending(h => h.Id)
            .Select(h => Project(h))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<SearchHistoryDto>> ListDeletedAsync(int userId, CancellationToken ct = default) =>
        await Owned(userId)
            .Where(h => h.IsDeleted)
            .OrderByDescending(h => h.DeletedAt).ThenByDescending(h => h.Id)
            .Select(h => Project(h))
            .ToListAsync(ct);

    public async Task<SearchHistoryDto?> RenameAsync(
        int userId, int id, string? title, CancellationToken ct = default)
    {
        var row = await Owned(userId).FirstOrDefaultAsync(h => h.Id == id, ct);
        if (row is null) return null;

        var trimmed = title?.Trim();

        // An empty title means "go back to showing the query", not "store a blank".
        row.Title = string.IsNullOrEmpty(trimmed)
            ? null
            : Truncate(trimmed, MaxTitleChars);

        row.UpdatedAt = DateTime.Now;
        await _db.SaveChangesAsync(ct);
        return Project(row);
    }

    public async Task<SearchHistoryDto?> SoftDeleteAsync(int userId, int id, CancellationToken ct = default)
    {
        var row = await Owned(userId).FirstOrDefaultAsync(h => h.Id == id, ct);
        if (row is null) return null;

        // Idempotent: deleting an already-deleted row keeps the original timestamp
        // rather than resetting it.
        if (!row.IsDeleted)
        {
            row.IsDeleted = true;
            row.DeletedAt = DateTime.Now;
            row.UpdatedAt = row.DeletedAt.Value;
            await _db.SaveChangesAsync(ct);
        }

        return Project(row);
    }

    public async Task<SearchHistoryDto?> RestoreAsync(int userId, int id, CancellationToken ct = default)
    {
        var row = await Owned(userId).FirstOrDefaultAsync(h => h.Id == id, ct);
        if (row is null) return null;

        if (row.IsDeleted)
        {
            row.IsDeleted = false;
            row.DeletedAt = null;
            row.UpdatedAt = DateTime.Now;
            await _db.SaveChangesAsync(ct);
        }

        return Project(row);
    }

    /// <summary>Every read starts here — there is no path to another user's row.</summary>
    private IQueryable<SearchHistory> Owned(int userId) =>
        _db.SearchHistories.Where(h => h.OwnerUserId == userId);

    private static string Normalize(string query) =>
        Truncate((query ?? "").Trim(), MaxQueryChars);

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

    private static SearchHistoryDto Project(SearchHistory h) =>
        new(h.Id, h.Query, h.Title, h.MaxResults, h.RunCount, h.LastRunAt, h.CreatedAt, h.IsDeleted);
}
