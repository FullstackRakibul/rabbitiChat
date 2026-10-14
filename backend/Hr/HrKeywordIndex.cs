// মানব সম্পদ — the keyword half of hybrid search (docs/HR_MODULE_PROMPT.md §3.2).
//
// HR questions are full of exact tokens that embeddings handle badly — EXL,
// Schedule-A, unit 27, status 11, column names — so every HR chunk is also in an
// SQLite FTS5 table and searched with BM25. FTS5 ships in the e_sqlite3 build
// the app already uses; no package is added.
//
// One row per chunk: its text, the same text with code identifiers opened into
// words (so "service benefit" meets @ServiceBenefitAmount), and the chunk and
// item ids. The tokenizer keeps combining marks inside words, so a Bengali word
// is one token instead of being cut at every vowel sign.
//
// The table is not part of the EF model (EnsureCreated cannot make a virtual
// table), so it is created here on every start, and filled from EmbeddingChunks
// for any HR chunk that has no row yet — a library built before Phase 2.

using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.Data;
using RabbitIC.Api.Models;

namespace RabbitIC.Api.Hr;

public sealed record HrKeywordHit(int ChunkId, int ItemId, int Rank, string? Snippet);

public static class HrKeywordIndex
{
    private static readonly Regex Token = new(@"[\p{L}\p{M}\p{N}]+", RegexOptions.Compiled);
    private static readonly Regex IdentifierBoundary = new("(?<=[a-z])(?=[A-Z])", RegexOptions.Compiled);

    /// <summary>The searchable second form of a chunk: identifiers opened into words.</summary>
    public static string Words(string? sourceRef, string text) =>
        $"{sourceRef}\n{IdentifierBoundary.Replace(text.Replace('@', ' ').Replace('_', ' '), " ")}";

    public static void EnsureSchema(AppDbContext db)
    {
        db.Database.ExecuteSqlRaw(@"
            CREATE VIRTUAL TABLE IF NOT EXISTS ""HrChunkFts"" USING fts5(
                ""Text"", ""Words"", ""ChunkId"" UNINDEXED, ""ItemId"" UNINDEXED,
                tokenize = ""unicode61 remove_diacritics 0 categories 'L* N* Co M*'""
            );");

        var indexed = db.Database.SqlQueryRaw<int>(@"SELECT ""ChunkId"" AS ""Value"" FROM ""HrChunkFts""").ToHashSet();
        var missing = db.EmbeddingChunks.AsNoTracking()
            .Where(c => c.Kind == ContextSource.KindHr && c.ContextSourceId != null)
            .Select(c => new { c.Id, c.ContextSourceId, c.SourceRef, c.Text })
            .AsEnumerable()
            .Where(c => !indexed.Contains(c.Id))
            .ToList();
        foreach (var c in missing)
            Insert(db, c.Id, c.ContextSourceId!.Value, c.SourceRef, c.Text);
    }

    /// <summary>Makes the item's keyword rows exactly its current chunks.</summary>
    public static async Task ReplaceAsync(AppDbContext db, int itemId, IEnumerable<EmbeddingChunk> chunks, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($@"DELETE FROM ""HrChunkFts"" WHERE ""ItemId"" = {itemId}", ct);
        foreach (var c in chunks) Insert(db, c.Id, itemId, c.SourceRef, c.Text);
    }

    public static Task RemoveAsync(AppDbContext db, int itemId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($@"DELETE FROM ""HrChunkFts"" WHERE ""ItemId"" = {itemId}", ct);

    /// <summary>
    /// The main column leaves out @variables: they are the procedure's internal
    /// names, and a chunk that repeats @EXL six times in arithmetic must not
    /// outrank the one that lists the attendance code 'EXL'. Their opened-up words
    /// stay searchable in the second column, at a quarter of the weight.
    /// </summary>
    private static void Insert(AppDbContext db, int chunkId, int itemId, string? sourceRef, string text) =>
        db.Database.ExecuteSqlInterpolated(
            $@"INSERT INTO ""HrChunkFts"" (""Text"", ""Words"", ""ChunkId"", ""ItemId"") VALUES ({Variable.Replace(text, " ")}, {Words(sourceRef, text)}, {chunkId}, {itemId})");

    private static readonly Regex Variable = new(@"@\w+", RegexOptions.Compiled);

    /// <summary>
    /// BM25 over the question's words (any of them), limited to <paramref name="itemIds"/>.
    /// Rank 1 is the best. The question is reduced to plain word tokens, each
    /// quoted, so nothing typed can become FTS5 query syntax.
    /// </summary>
    public static async Task<IReadOnlyList<HrKeywordHit>> SearchAsync(
        AppDbContext db, string question, IReadOnlyCollection<int> itemIds, int top, CancellationToken ct)
    {
        var tokens = Token.Matches(question).Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToList();
        if (tokens.Count == 0 || itemIds.Count == 0) return Array.Empty<HrKeywordHit>();
        var match = string.Join(" OR ", tokens.Select(t => $"\"{t.Replace("\"", "\"\"")}\""));

        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT ""ChunkId"", ""ItemId"", snippet(""HrChunkFts"", -1, '«', '»', '…', 14)
            FROM ""HrChunkFts""
            WHERE ""HrChunkFts"" MATCH $match
              AND ""ItemId"" IN (SELECT value FROM json_each($items))
            ORDER BY bm25(""HrChunkFts"", 1.0, 0.25)
            LIMIT $top;";
        Add(cmd, "$match", match);
        Add(cmd, "$items", JsonSerializer.Serialize(itemIds));
        Add(cmd, "$top", top);

        var hits = new List<HrKeywordHit>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            hits.Add(new HrKeywordHit(reader.GetInt32(0), reader.GetInt32(1), hits.Count + 1, reader.IsDBNull(2) ? null : reader.GetString(2)));
        return hits;
    }

    /// <summary>Keyword rows per item — the library's consistency check.</summary>
    public static async Task<Dictionary<int, int>> CountByItemAsync(AppDbContext db, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT ""ItemId"", COUNT(*) FROM ""HrChunkFts"" GROUP BY ""ItemId"";";
        var counts = new Dictionary<int, int>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) counts[reader.GetInt32(0)] = reader.GetInt32(1);
        return counts;
    }

    private static void Add(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
