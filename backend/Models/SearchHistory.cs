namespace RabbitIC.Api.Models;

/// <summary>
/// A query the user has run, kept so it can be re-run later.
///
/// The query is the record's identity: running the same text again bumps
/// <see cref="RunCount"/> rather than adding a row. Result payloads are
/// deliberately absent — search results are metered, licensed, and stale within
/// minutes, so "reuse" here means re-running the query, never replaying a cache.
/// </summary>
public class SearchHistory
{
    public int Id { get; set; }

    /// <summary>Owner — every query is scoped to this, and a foreign id reads as absent (404).</summary>
    public int OwnerUserId { get; set; }

    /// <summary>The search string, verbatim as typed. Matched case-insensitively for dedupe.</summary>
    public string Query { get; set; } = "";

    /// <summary>User-editable display label. Null means "show the query itself".</summary>
    public string? Title { get; set; }

    /// <summary>The maxResults setting this query last ran with, so a re-run behaves the same.</summary>
    public int MaxResults { get; set; }

    /// <summary>How many times this query has been run. Starts at 1.</summary>
    public int RunCount { get; set; } = 1;

    public DateTime LastRunAt { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Soft delete. Rows are never removed: the undo affordance in the UI is a
    /// real restore, and a deleted query that is run again is revived rather than
    /// duplicated.
    /// </summary>
    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }
}
