// মানব সম্পদ — the HR library (docs/HR_MODULE_PROMPT.md §3.2).
//
// HR's own context: the rules document, procedure files, code tables and notes,
// in any of .md .txt .sql .json .csv .pdf .xlsx. Each item is a ContextSource row
// with Kind = "hr", shared across everyone with the `hr` tab (the exception
// lives in ContextSourceService; this class never reads ContextSources around
// it). On top of that row this class owns what is HR-specific:
//
//   · type and mode — `always` items go to the model whole, `search` items are
//     split (HrSplitter) and found per question. The library has no size limit;
//     the always budget does: active always items, with the persona, stay within
//     8,000 tokens, so the chat never meets an over-budget library;
//   · keeping the chunks true to the item: any change to the content or the mode
//     replaces the item's chunks in the database, the vector index and the
//     keyword index together, so no stale excerpt outlives the text it came from;
//   · indexing in the background (HrIndexer): a save splits and keyword-indexes
//     at once and returns; embeddings follow, with "n of m" progress, and a
//     failure marks the item until Retry;
//   · hybrid search — vector and BM25, merged by reciprocal rank — gated on
//     Kind = "hr" and on the items a question is scoped to;
//   · seeding from docs/hr/ on start, for files not in the library yet — an edit
//     made in the app is never overwritten by the seed.

using System.Text;
using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.Data;
using RabbitIC.Api.Extraction;
using RabbitIC.Api.MinimalApis;
using RabbitIC.Api.Models;
using RabbitIC.Api.Retrieval;
using RabbitIC.Api.Services;

namespace RabbitIC.Api.Hr;

/// <summary>
/// A library item as the list and the editor see it. Content only on a single-item
/// read. IndexState is "ready", "indexing" (EmbeddedCount of ChunkCount) or "failed".
/// </summary>
public sealed record HrItemDto(
    int Id, string Name, string HrType, string Mode, int Version,
    string? ApprovedBy, DateTime? ApprovedAt, bool IsActive, int SortOrder,
    int Chars, int Tokens, int ChunkCount, int EmbeddedCount, DateTime UpdatedAt, bool CanEdit,
    string IndexState, string? IndexError, string? Content = null);

public sealed record HrChunkDto(
    int Id, string? SourceRef, string? Section, int LineFrom, int LineTo,
    int Chars, int Tokens, bool Embedded, string Text);

/// <summary>Phase 1's /api/hr/retrieve shape, kept for its callers.</summary>
public sealed record HrHitDto(
    int ItemId, string ItemName, string? SourceRef, string? Section, int LineFrom, int LineTo,
    float Score, string Text);

/// <summary>
/// One hybrid-search result. Score is the reciprocal-rank sum; each side's own
/// rank is kept so the merge can be checked (null: that side did not find it).
/// </summary>
public sealed record HrSearchHit(
    int ChunkId, int ItemId, string ItemName, int Version, string? SourceRef, string? Section,
    int? LineFrom, int? LineTo, double Score, int? VectorRank, int? KeywordRank, string Text, string? Snippet);

/// <summary>Create or change an item. Null members mean "leave unchanged"; ApprovedBy "" clears the approval.</summary>
public sealed record HrItemInput(
    string? Name, string? HrType, string? Mode, string? Content, bool? IsActive, string? ApprovedBy);

public sealed class HrLibrary(
    AppDbContext db, IContextSourceService sources, EmbeddingService embedder, VectorIndex index,
    HrIndexer indexer, IConfiguration config, ILogger<HrLibrary> log)
{
    public const string Kind = ContextSource.KindHr;

    public static readonly string[] Types = { "rules", "procedure", "codes", "note" };
    public static readonly string[] Modes = { "always", "search" };
    public static readonly string[] UploadExtensions = { ".md", ".txt", ".sql", ".json", ".csv", ".pdf", ".xlsx" };
    public const long MaxUploadBytes = 20L * 1024 * 1024;   // the same limit as Documents

    /// <summary>The always budget: persona plus every active always item (§3.2, §3.3).</summary>
    public const int AlwaysCapTokens = 8000;

    /// <summary>§3.2: rules and codes go whole; procedures and notes are searched.</summary>
    public static string DefaultMode(string type) => type is "rules" or "codes" ? "always" : "search";

    /// <summary>
    /// One lock for every change to HR chunks, whoever makes it — a save, or the
    /// background embedder — so an embed can never re-add a chunk a save just
    /// removed.
    /// </summary>
    private static readonly SemaphoreSlim ChunkGate = new(1, 1);

    // ───────────────────────── reads ─────────────────────────

    public async Task<IReadOnlyList<HrItemDto>> ListAsync(User caller, CancellationToken ct = default)
    {
        var items = await sources.ListSharedAsync(Kind, ct);
        var counts = await ChunkCountsAsync(ct);
        var canEdit = ContextSourceService.CanEditShared(caller);
        return items.Select(i => Project(i, counts, canEdit, withContent: false)).ToList();
    }

    public async Task<HrItemDto?> GetAsync(User caller, int id, CancellationToken ct = default)
    {
        var item = await sources.GetSharedAsync(Kind, id, ct);
        return item is null ? null : Project(item, await ChunkCountsAsync(ct), ContextSourceService.CanEditShared(caller), withContent: true);
    }

    public async Task<IReadOnlyList<HrChunkDto>?> ChunksAsync(int id, CancellationToken ct = default)
    {
        if (await sources.GetSharedAsync(Kind, id, ct) is null) return null;

        var rows = await db.EmbeddingChunks.AsNoTracking()
            .Where(c => c.Kind == Kind && c.ContextSourceId == id)
            .OrderBy(c => c.Id)
            .Select(c => new { c.Id, c.SourceRef, c.Text, c.LineFrom, c.LineTo, Embedded = c.Vector.Length > 0 })
            .ToListAsync(ct);

        return rows.Select(r => new HrChunkDto(r.Id, r.SourceRef, SectionOf(r.SourceRef), r.LineFrom ?? 0, r.LineTo ?? 0,
            r.Text.Length, PoPromptBuilder.EstimateTokens(r.Text), r.Embedded, r.Text)).ToList();
    }

    /// <summary>Active always items in SortOrder — what the chat sends whole.</summary>
    public async Task<IReadOnlyList<ContextSource>> AlwaysItemsAsync(CancellationToken ct = default) =>
        (await sources.ListSharedAsync(Kind, ct)).Where(i => i.IsActive && i.Mode == "always").ToList();

    // ───────────────────────── search ─────────────────────────

    /// <summary>
    /// Hybrid search (§3.2): the vector side's top 20 and the keyword side's top
    /// 20, merged by reciprocal rank (1/(60+rank) summed), best <paramref name="top"/>
    /// kept, whole chunks only. Active items only; with <paramref name="scope"/>,
    /// only those items. If the embedding model is away the keyword side answers
    /// alone rather than the search failing.
    /// </summary>
    public async Task<IReadOnlyList<HrSearchHit>> SearchAsync(
        string query, IReadOnlyCollection<int>? scope, int top, CancellationToken ct = default)
    {
        var items = (await sources.ListSharedAsync(Kind, ct))
            .Where(i => i.IsActive && (scope is null || scope.Count == 0 || scope.Contains(i.Id)))
            .ToDictionary(i => i.Id);
        if (items.Count == 0 || string.IsNullOrWhiteSpace(query)) return Array.Empty<HrSearchHit>();

        var vectorRanks = new Dictionary<int, int>();
        try
        {
            var vector = await embedder.EmbedAsync(query, ct);
            var hits = index.Search(vector, userId: 0, topK: 20, minScore: MinScore, kind: Kind,
                contextSources: items.Keys.ToHashSet());
            for (var i = 0; i < hits.Count; i++) vectorRanks[hits[i].ChunkId] = i + 1;
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
            log.LogWarning("HR search: the vector side is unavailable ({Message}); keyword results only.", e.Message);
        }

        var keyword = await HrKeywordIndex.SearchAsync(db, query, items.Keys.ToList(), 20, ct);
        var keywordRanks = keyword.ToDictionary(k => k.ChunkId, k => k.Rank);
        var snippets = keyword.ToDictionary(k => k.ChunkId, k => k.Snippet);

        var merged = vectorRanks.Keys.Union(keywordRanks.Keys)
            .Select(id => (Id: id, Score:
                (vectorRanks.TryGetValue(id, out var v) ? 1.0 / (60 + v) : 0) +
                (keywordRanks.TryGetValue(id, out var k) ? 1.0 / (60 + k) : 0)))
            .OrderByDescending(x => x.Score).ThenBy(x => x.Id)
            .Take(top).ToList();

        var ids = merged.Select(m => m.Id).ToList();
        var rows = await db.EmbeddingChunks.AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .Select(c => new { c.Id, c.ContextSourceId, c.SourceRef, c.Text, c.LineFrom, c.LineTo })
            .ToDictionaryAsync(c => c.Id, ct);

        return merged.Where(m => rows.ContainsKey(m.Id) && rows[m.Id].ContextSourceId is int item && items.ContainsKey(item))
            .Select(m =>
            {
                var r = rows[m.Id];
                var item = items[r.ContextSourceId!.Value];
                return new HrSearchHit(r.Id, item.Id, item.Name, item.Version, r.SourceRef, SectionOf(r.SourceRef), r.LineFrom, r.LineTo,
                    Math.Round(m.Score, 6), vectorRanks.TryGetValue(r.Id, out var v) ? v : null,
                    keywordRanks.TryGetValue(r.Id, out var k) ? k : null, r.Text,
                    snippets.TryGetValue(r.Id, out var s) && s is not null ? s : Preview(r.Text));
            }).ToList();
    }

    /// <summary>Phase 1's retrieval route, now answered by the hybrid search.</summary>
    public async Task<IReadOnlyList<HrHitDto>> RetrieveAsync(string question, int top = 8, CancellationToken ct = default) =>
        (await SearchAsync(question, null, top, ct))
            .Select(h => new HrHitDto(h.ItemId, h.ItemName, h.SourceRef, h.Section, h.LineFrom ?? 0, h.LineTo ?? 0, (float)h.Score, h.Text))
            .ToList();

    /// <summary>HR chunks in memory, in the database and in the keyword index, per item — the "no orphans" evidence.</summary>
    public async Task<object> IndexStatsAsync(CancellationToken ct = default)
    {
        var inIndex = index.CountByContextSource(Kind);
        var inDb = (await db.EmbeddingChunks.AsNoTracking()
                .Where(c => c.Kind == Kind && c.ContextSourceId != null && c.Vector.Length > 0)
                .GroupBy(c => c.ContextSourceId!.Value)
                .Select(g => new { Id = g.Key, N = g.Count() })
                .ToListAsync(ct))
            .ToDictionary(x => x.Id, x => x.N);
        var inFts = await HrKeywordIndex.CountByItemAsync(db, ct);

        var ids = inIndex.Keys.Union(inDb.Keys).Union(inFts.Keys).OrderBy(i => i);
        return new
        {
            indexChunks = inIndex.Values.Sum(),
            dbChunks = inDb.Values.Sum(),
            ftsRows = inFts.Values.Sum(),
            byItem = ids.ToDictionary(
                i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                i => new { index = inIndex.GetValueOrDefault(i), db = inDb.GetValueOrDefault(i), fts = inFts.GetValueOrDefault(i) }),
        };
    }

    // ───────────────────────── writes ─────────────────────────

    public async Task<(SharedOutcome Outcome, HrItemDto? Item, string? Error)> CreateAsync(
        User editor, HrItemInput input, CancellationToken ct = default)
    {
        var name = (input.Name ?? "").Trim();
        if (name.Length == 0) return (SharedOutcome.Ok, null, "name is required.");
        var type = (input.HrType ?? "note").Trim().ToLowerInvariant();
        if (!Types.Contains(type)) return (SharedOutcome.Ok, null, $"type must be one of: {string.Join(", ", Types)}.");
        var mode = (input.Mode ?? DefaultMode(type)).Trim().ToLowerInvariant();
        if (!Modes.Contains(mode)) return (SharedOutcome.Ok, null, $"mode must be one of: {string.Join(", ", Modes)}.");
        if (!ContextSourceService.CanEditShared(editor)) return (SharedOutcome.Forbidden, null, null);

        var existing = await sources.ListSharedAsync(Kind, ct);
        if (existing.Any(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)))
            return (SharedOutcome.Ok, null, $"An item named \"{name}\" is already in the library.");

        var content = Normalize(input.Content);
        if (HrSplitter.Validate(name, content) is { } invalid) return (SharedOutcome.Ok, null, invalid);

        var active = input.IsActive ?? true;
        if (AlwaysCapError(existing, null, active && mode == "always" ? content : null) is { } capError)
            return (SharedOutcome.Ok, null, capError);

        var item = new ContextSource { Kind = Kind, Name = name, HrType = type, Mode = mode, Content = content, IsActive = active };
        ApplyApproval(item, input.ApprovedBy);

        var write = await sources.CreateSharedAsync(editor, item, ct);
        if (write.Outcome != SharedOutcome.Ok) return (write.Outcome, null, null);

        await RechunkAsync(write.Item!, ct);
        return (SharedOutcome.Ok, await GetAsync(editor, write.Item!.Id, ct), null);
    }

    public async Task<(SharedOutcome Outcome, HrItemDto? Item, string? Error)> UpdateAsync(
        User editor, int id, HrItemInput input, CancellationToken ct = default)
    {
        var type = input.HrType?.Trim().ToLowerInvariant();
        if (type is not null && !Types.Contains(type)) return (SharedOutcome.Ok, null, $"type must be one of: {string.Join(", ", Types)}.");
        var mode = input.Mode?.Trim().ToLowerInvariant();
        if (mode is not null && !Modes.Contains(mode)) return (SharedOutcome.Ok, null, $"mode must be one of: {string.Join(", ", Modes)}.");

        // The state the item would be in after this change — checked before anything is written.
        var all = await sources.ListSharedAsync(Kind, ct);
        var current = all.FirstOrDefault(i => i.Id == id);
        if (current is not null && ContextSourceService.CanEditShared(editor))
        {
            var name = string.IsNullOrWhiteSpace(input.Name) ? current.Name : input.Name.Trim();
            var content = input.Content is null ? current.Content : Normalize(input.Content);
            if (input.Content is not null && HrSplitter.Validate(name, content) is { } invalid)
                return (SharedOutcome.Ok, null, invalid);
            var willBeAlways = (mode ?? current.Mode) == "always" && (input.IsActive ?? current.IsActive);
            if (AlwaysCapError(all, id, willBeAlways ? content : null) is { } capError)
                return (SharedOutcome.Ok, null, capError);
        }

        string? beforeName = null, beforeMode = null, beforeContent = null;
        var write = await sources.UpdateSharedAsync(editor, Kind, id, item =>
        {
            (beforeName, beforeMode, beforeContent) = (item.Name, item.Mode, item.Content);
            if (!string.IsNullOrWhiteSpace(input.Name)) item.Name = input.Name.Trim();
            if (type is not null) item.HrType = type;
            if (mode is not null) item.Mode = mode;
            if (input.Content is not null) item.Content = Normalize(input.Content);
            if (input.IsActive is bool active) item.IsActive = active;
            ApplyApproval(item, input.ApprovedBy);
        }, ct);
        if (write.Outcome != SharedOutcome.Ok) return (write.Outcome, null, null);

        var updated = write.Item!;
        // The chunks follow the text, the mode and the name (the name is in every SourceRef).
        if (updated.Content != beforeContent || updated.Mode != beforeMode || updated.Name != beforeName)
            await RechunkAsync(updated, ct);

        return (SharedOutcome.Ok, await GetAsync(editor, id, ct), null);
    }

    public async Task<SharedOutcome> DeleteAsync(User editor, int id, CancellationToken ct = default)
    {
        await ChunkGate.WaitAsync(ct);
        try
        {
            var write = await sources.DeleteSharedAsync(editor, Kind, id, ct);
            if (write.Outcome != SharedOutcome.Ok) return write.Outcome;

            // The foreign key cascades in SQLite; deleting explicitly as well keeps
            // the outcome the same on a connection with foreign keys off.
            await db.EmbeddingChunks.Where(c => c.ContextSourceId == id).ExecuteDeleteAsync(ct);
            await HrKeywordIndex.RemoveAsync(db, id, ct);
            index.RemoveContextSource(id);
            return SharedOutcome.Ok;
        }
        finally { ChunkGate.Release(); }
    }

    /// <summary>Clears a failed item's error and queues it again.</summary>
    public async Task<SharedOutcome> RetryAsync(User editor, int id, CancellationToken ct = default)
    {
        if (await sources.GetSharedAsync(Kind, id, ct) is null) return SharedOutcome.NotFound;
        if (!ContextSourceService.CanEditShared(editor)) return SharedOutcome.Forbidden;
        await db.ContextSources.Where(s => s.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IndexError, (string?)null), ct);
        indexer.Enqueue(id);
        return SharedOutcome.Ok;
    }

    // ───────────────────────── uploads ─────────────────────────

    /// <summary>
    /// Reads an uploaded file as text: PDF and XLSX through the extractors
    /// Documents uses, each page or sheet under its own heading (so it lands in
    /// the SourceRef); everything else as text, honouring a byte-order mark — two
    /// of the procedure files HR exported are UTF-16.
    /// </summary>
    public static async Task<(string? Text, string? Error)> ReadUploadAsync(IFormFile file, CancellationToken ct)
    {
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext == ".docx" || ext == ".doc") return (null, "Word files cannot be read here; save it as PDF first.");
        if (!UploadExtensions.Contains(ext))
            return (null, $"Only {string.Join(" ", UploadExtensions)} files can be added.");
        if (file.Length > MaxUploadBytes)
            return (null, $"The file is larger than {MaxUploadBytes / 1024 / 1024} MB.");

        try
        {
            if (ext is ".pdf" or ".xlsx")
            {
                await using var stream = file.OpenReadStream();
                var sections = ext == ".pdf" ? DocumentApis.ExtractPdf(stream, file.FileName) : DocumentApis.ExtractXlsx(stream, file.FileName);
                if (sections.Count == 0) return (null, "No text could be read from this file. A scanned PDF needs OCR first.");
                var sb = new StringBuilder();
                foreach (var s in sections)
                {
                    // "x.pdf p3" → "Page 3"; "x.xlsx [Rates]" → "Sheet Rates".
                    var label = ext == ".pdf"
                        ? "Page " + s.SourceRef[(s.SourceRef.LastIndexOf(" p", StringComparison.Ordinal) + 2)..]
                        : "Sheet " + s.SourceRef[(s.SourceRef.LastIndexOf('[') + 1)..].TrimEnd(']');
                    sb.Append("## ").Append(label).Append("\n\n").Append(s.Text.Trim()).Append("\n\n");
                }
                return (sb.ToString().TrimEnd() + "\n", null);
            }

            using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = await reader.ReadToEndAsync(ct);
            return text.Contains('\0') ? (null, "The file is not text.") : (text, null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return (null, $"The file could not be read: {e.Message}");
        }
    }

    /// <summary>
    /// What a file name becomes in the library, and its type: the seed and uploads
    /// agree. Markdown and plain text drop their extension (the rules document is
    /// cited as HR_SETTLEMENT_RULES); every other format keeps it, because the
    /// splitter reads the format from it.
    /// </summary>
    public static (string Name, string HrType) Describe(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var bare = Path.GetFileNameWithoutExtension(fileName);
        return ext switch
        {
            ".sql" => (fileName, "procedure"),
            ".csv" => (fileName, "codes"),
            ".json" or ".pdf" or ".xlsx" => (fileName, "note"),
            _ when bare.Contains("RULES", StringComparison.OrdinalIgnoreCase) => (bare, "rules"),
            _ when bare.Contains("CODE", StringComparison.OrdinalIgnoreCase) => (bare, "codes"),
            _ => (bare, "note"),
        };
    }

    // ───────────────────────── chunks and embedding ─────────────────────────

    /// <summary>
    /// Replaces an item's chunks: removed from the database, the vector index and
    /// the keyword index; re-split if `search` and keyword-indexed at once; then
    /// queued for embedding in the background.
    /// </summary>
    private async Task RechunkAsync(ContextSource item, CancellationToken ct)
    {
        await ChunkGate.WaitAsync(ct);
        try
        {
            await db.EmbeddingChunks.Where(c => c.ContextSourceId == item.Id).ExecuteDeleteAsync(ct);
            index.RemoveContextSource(item.Id);
            await HrKeywordIndex.RemoveAsync(db, item.Id, ct);
            await db.ContextSources.Where(s => s.Id == item.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IndexError, (string?)null), ct);

            if (item.Mode != "search") return;
            var rows = AddChunks(item);
            await db.SaveChangesAsync(ct);
            await HrKeywordIndex.ReplaceAsync(db, item.Id, rows, ct);
        }
        finally { ChunkGate.Release(); }

        indexer.Enqueue(item.Id);
    }

    private List<EmbeddingChunk> AddChunks(ContextSource item)
    {
        var rows = HrSplitter.Split(item.Name, item.Content).Select(chunk => new EmbeddingChunk
        {
            ContextSourceId = item.Id,
            Kind = Kind,
            SourceRef = chunk.SourceRef,
            Text = chunk.Text,
            LineFrom = chunk.LineFrom,
            LineTo = chunk.LineTo,
            Vector = Array.Empty<byte>(),   // embedded by HrIndexer
        }).ToList();
        db.EmbeddingChunks.AddRange(rows);
        return rows;
    }

    /// <summary>
    /// Embeds an item's chunks that have no vector yet and adds them to the index.
    /// On the first failure the item is marked failed with the reason and the rest
    /// wait for Retry. Called by HrIndexer only.
    /// </summary>
    public async Task EmbedItemAsync(int itemId, CancellationToken ct)
    {
        var pending = await db.EmbeddingChunks.AsNoTracking()
            .Where(c => c.Kind == Kind && c.ContextSourceId == itemId && c.Vector.Length == 0)
            .OrderBy(c => c.Id).Select(c => c.Id).ToListAsync(ct);

        foreach (var chunkId in pending)
        {
            await ChunkGate.WaitAsync(ct);
            try
            {
                var chunk = await db.EmbeddingChunks.FirstOrDefaultAsync(c => c.Id == chunkId, ct);
                if (chunk is null || chunk.Vector.Length > 0) continue;   // replaced or embedded meanwhile

                float[] vector;
                try { vector = await embedder.EmbedAsync(EmbeddingText(chunk), ct); }
                catch (Exception e) when (e is HttpRequestException or InvalidOperationException or TaskCanceledException)
                {
                    if (ct.IsCancellationRequested) throw;
                    var reason = e.Message.Length > 300 ? e.Message[..300] : e.Message;
                    await db.ContextSources.Where(s => s.Id == itemId)
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.IndexError, $"Embedding failed: {reason}"), ct);
                    log.LogWarning("HR library: item {Item} could not be embedded ({Message}); it waits for Retry.", itemId, e.Message);
                    return;
                }

                chunk.Vector = VectorOps.ToBlob(vector);
                await db.SaveChangesAsync(ct);
                index.Add(chunk);
            }
            finally { ChunkGate.Release(); }
        }
    }

    // ───────────────────────── seed ─────────────────────────

    /// <summary>
    /// Adds every docs/hr/*.md and docs/hr/procedures/*.sql file not already in
    /// the library (matched by name), owned by <paramref name="owner"/>. Existing
    /// items are never touched, so an HR edit survives every restart. An always
    /// item that would break the always budget is seeded as search instead.
    /// </summary>
    public async Task<int> SeedAsync(string dir, User owner, CancellationToken ct = default)
    {
        var files = Directory.EnumerateFiles(dir, "*.md")
            .Concat(Directory.Exists(Path.Combine(dir, "procedures"))
                ? Directory.EnumerateFiles(Path.Combine(dir, "procedures"), "*.sql")
                : Enumerable.Empty<string>())
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

        var added = 0;
        foreach (var file in files)
        {
            var existing = await sources.ListSharedAsync(Kind, ct);
            var (name, type) = Describe(Path.GetFileName(file));
            if (existing.Any(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase))) continue;

            var content = Normalize(await File.ReadAllTextAsync(file, ct));   // BOM-aware: UTF-16 files read as text
            var mode = DefaultMode(type);
            if (mode == "always" && AlwaysCapError(existing, null, content) is not null) mode = "search";

            var item = new ContextSource { Kind = Kind, Name = name, HrType = type, Mode = mode, Content = content, IsActive = true };
            var write = await sources.CreateSharedAsync(owner, item, ct);
            if (write.Outcome != SharedOutcome.Ok) continue;

            if (item.Mode == "search")
            {
                await ChunkGate.WaitAsync(ct);
                try
                {
                    var rows = AddChunks(item);
                    await db.SaveChangesAsync(ct);
                    await HrKeywordIndex.ReplaceAsync(db, item.Id, rows, ct);
                }
                finally { ChunkGate.Release(); }
            }
            added++;
        }
        return added;
    }

    /// <summary>
    /// Where docs/hr is: Hr:SeedDir when set, else the first docs/hr above the
    /// content root (a checkout running in place). Null when there is none, as
    /// in a published build — the library then starts from what HR adds.
    /// </summary>
    public static string? FindSeedDir(IConfiguration config, IHostEnvironment env)
    {
        if (config["Hr:SeedDir"] is { Length: > 0 } configured)
            return Directory.Exists(configured) ? configured : null;

        for (var dir = new DirectoryInfo(env.ContentRootPath); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "hr");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    // ───────────────────────── the always budget ─────────────────────────

    /// <summary>
    /// The message for a change that would take persona plus active always items
    /// past the cap, or null. <paramref name="changedId"/> is left out of the
    /// current total and <paramref name="newAlwaysContent"/> (when it will be an
    /// active always item) counted instead.
    /// </summary>
    private static string? AlwaysCapError(IEnumerable<ContextSource> all, int? changedId, string? newAlwaysContent)
    {
        if (newAlwaysContent is null) return null;   // nothing new is going to be sent whole
        var total = PoPromptBuilder.EstimateTokens(HrPrompt.Persona)
            + all.Where(i => i.Id != changedId && i.IsActive && i.Mode == "always").Sum(i => PoPromptBuilder.EstimateTokens(i.Content))
            + PoPromptBuilder.EstimateTokens(newAlwaysContent);
        return total <= AlwaysCapTokens
            ? null
            : $"Always items would use {total.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} of 8,000 tokens; make this item Search or switch another off.";
    }

    /// <summary>Persona plus active always items now — the Library header's running total.</summary>
    public async Task<int> AlwaysTokensAsync(CancellationToken ct = default) =>
        PoPromptBuilder.EstimateTokens(HrPrompt.Persona)
        + (await AlwaysItemsAsync(ct)).Sum(i => PoPromptBuilder.EstimateTokens(i.Content));

    // ───────────────────────── helpers ─────────────────────────

    private float MinScore =>
        float.TryParse(config["Hr:MinScore"], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var m) ? m : 0.3f;

    /// <summary>
    /// What a chunk is embedded as: its SourceRef, then its text with code
    /// identifiers opened into words — @ServiceBenefitAmount reads as "Service
    /// Benefit Amount" — so a question asked in plain words can meet the code that
    /// answers it. Only the vector sees this form; the stored text is untouched.
    /// </summary>
    private static string EmbeddingText(EmbeddingChunk chunk) => HrKeywordIndex.Words(chunk.SourceRef, chunk.Text);

    private async Task<Dictionary<int, (int Chunks, int Embedded)>> ChunkCountsAsync(CancellationToken ct) =>
        (await db.EmbeddingChunks.AsNoTracking()
            .Where(c => c.Kind == Kind && c.ContextSourceId != null)
            .GroupBy(c => c.ContextSourceId!.Value)
            .Select(g => new { Id = g.Key, Chunks = g.Count(), Embedded = g.Count(c => c.Vector.Length > 0) })
            .ToListAsync(ct))
        .ToDictionary(x => x.Id, x => (x.Chunks, x.Embedded));

    private static HrItemDto Project(ContextSource i, Dictionary<int, (int Chunks, int Embedded)> counts, bool canEdit, bool withContent)
    {
        var (chunks, embedded) = counts.GetValueOrDefault(i.Id);
        var state = i.IndexError is not null ? "failed" : embedded < chunks ? "indexing" : "ready";
        return new HrItemDto(i.Id, i.Name, i.HrType ?? "note", i.Mode ?? "always", i.Version,
            i.ApprovedBy, i.ApprovedAt, i.IsActive, i.SortOrder,
            i.Content.Length, PoPromptBuilder.EstimateTokens(i.Content), chunks, embedded, i.UpdatedAt, canEdit,
            state, i.IndexError, withContent ? i.Content : null);
    }

    private static void ApplyApproval(ContextSource item, string? approvedBy)
    {
        if (approvedBy is null) return;
        var who = approvedBy.Trim();
        item.ApprovedBy = who.Length == 0 ? null : who;
        item.ApprovedAt = who.Length == 0 ? null : DateTime.Now;
    }

    /// <summary>One line ending, no byte-order mark: line numbers then mean the same everywhere.</summary>
    private static string Normalize(string? text) =>
        (text ?? "").Replace("﻿", "").Replace("\r\n", "\n").Replace('\r', '\n');

    private static string Preview(string text)
    {
        var t = text.Replace('\n', ' ').Trim();
        return t.Length <= 180 ? t : t[..180] + "…";
    }

    /// <summary>"ProcessFinalSettelment.sql §Service Benefit L686-711" → "Service Benefit"; null without a § part.</summary>
    private static string? SectionOf(string? sourceRef)
    {
        if (sourceRef is null) return null;
        var at = sourceRef.IndexOf(" §", StringComparison.Ordinal);
        if (at < 0) return null;
        var rest = sourceRef[(at + 2)..];
        var lines = System.Text.RegularExpressions.Regex.Match(rest, @" L\d+-\d+$");
        return lines.Success ? rest[..lines.Index] : rest;
    }
}
