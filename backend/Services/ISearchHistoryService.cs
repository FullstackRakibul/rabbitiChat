namespace RabbitIC.Api.Services;

/// <summary>
/// What the API returns for one remembered query.
///
/// Note what is absent: there is no results field, and no shape here could carry
/// one. History is the query plus how it has been used.
/// </summary>
public record SearchHistoryDto(
    int Id,
    string Query,
    string? Title,
    int MaxResults,
    int RunCount,
    DateTime LastRunAt,
    DateTime CreatedAt,
    bool IsDeleted);

/// <summary>
/// Owner-scoped history of search queries.
///
/// Consumers depend on this interface rather than the DbContext, so the dedupe
/// rule and the soft-delete guarantee live in exactly one place — the search
/// endpoint cannot accidentally write a second row for the same query, and
/// nothing anywhere can hard-delete one.
/// </summary>
public interface ISearchHistoryService
{
    /// <summary>
    /// Records that a query was run. An existing active row with the same
    /// normalised query is bumped (RunCount + LastRunAt) instead of duplicated; a
    /// previously soft-deleted one is revived, because re-running a query is a
    /// clear statement that the user still wants it.
    /// </summary>
    Task<SearchHistoryDto> RecordAsync(int userId, string query, int maxResults, CancellationToken ct = default);

    /// <summary>Active rows only, newest run first.</summary>
    Task<IReadOnlyList<SearchHistoryDto>> ListAsync(int userId, CancellationToken ct = default);

    /// <summary>Soft-deleted rows only, most recently deleted first. Backs the "Show deleted" view.</summary>
    Task<IReadOnlyList<SearchHistoryDto>> ListDeletedAsync(int userId, CancellationToken ct = default);

    /// <summary>Null when the id is absent or belongs to someone else — callers map both to 404.</summary>
    Task<SearchHistoryDto?> RenameAsync(int userId, int id, string? title, CancellationToken ct = default);

    /// <summary>Sets IsDeleted + DeletedAt. Null when the id is absent or foreign.</summary>
    Task<SearchHistoryDto?> SoftDeleteAsync(int userId, int id, CancellationToken ct = default);

    /// <summary>Clears the delete flags. Null when the id is absent or foreign.</summary>
    Task<SearchHistoryDto?> RestoreAsync(int userId, int id, CancellationToken ct = default);
}
