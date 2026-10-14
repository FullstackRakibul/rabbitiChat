// মানব সম্পদ — the HR library (docs/HR_MODULE_PROMPT.md §3.2).
//
// HR's own context: the rules document, procedure files, code tables and notes.
// Each item is a ContextSource row with Kind = "hr", shared across everyone with
// the `hr` tab (the exception lives in ContextSourceService; this class never
// reads ContextSources around it). On top of that row this class owns what is
// HR-specific:
//
//   · type and mode — `always` items go to the model whole, `search` items are
//     split (HrSplitter) and embedded, and only their best excerpts are sent;
//   · keeping the chunks true to the item: any change to the content or the
//     mode replaces the item's chunks, in the database and in the one
//     VectorIndex, so no stale excerpt can outlive the text it came from;
//   · retrieval, gated on Kind = "hr" before scoring, like the owner gate for
//     documents;
//   · seeding from docs/hr/ on start, for files not in the library yet — an edit
//     made in the app is never overwritten by the seed.
//
// Embedding needs Ollama. When it is down, chunks are stored un-embedded and
// picked up by EmbedPendingAsync later (on start, and after every save), so a
// save never fails because the embedding model is away.

using System.Text;
using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.Data;
using RabbitIC.Api.Extraction;
using RabbitIC.Api.Models;
using RabbitIC.Api.Retrieval;
using RabbitIC.Api.Services;

namespace RabbitIC.Api.Hr;

/// <summary>A library item as the list and the editor see it. Content only on a single-item read.</summary>
public sealed record HrItemDto(
    int Id, string Name, string HrType, string Mode, int Version,
    string? ApprovedBy, DateTime? ApprovedAt, bool IsActive, int SortOrder,
    int Chars, int Tokens, int ChunkCount, int EmbeddedCount, DateTime UpdatedAt, bool CanEdit,
    string? Content = null);

public sealed record HrChunkDto(
    int Id, string? SourceRef, string? Section, int LineFrom, int LineTo,
    int Chars, int Tokens, bool Embedded, string Text);

public sealed record HrHitDto(
    int ItemId, string ItemName, string? SourceRef, string? Section, int LineFrom, int LineTo,
    float Score, string Text);

/// <summary>Create or change an item. Null members mean "leave unchanged"; ApprovedBy "" clears the approval.</summary>
public sealed record HrItemInput(
    string? Name, string? HrType, string? Mode, string? Content, bool? IsActive, string? ApprovedBy);

public sealed partial class HrLibrary(
    AppDbContext db, IContextSourceService sources, EmbeddingService embedder, VectorIndex index,
    IConfiguration config, ILogger<HrLibrary> log)
{
    public const string Kind = ContextSource.KindHr;

    public static readonly string[] Types = { "rules", "procedure", "codes", "note" };
    public static readonly string[] Modes = { "always", "search" };
    public static readonly string[] UploadExtensions = { ".md", ".txt", ".sql", ".csv" };
    public const int MaxUploadBytes = 2 * 1024 * 1024;

    /// <summary>§3.2: rules, codes and notes go whole; procedures are searched.</summary>
    public static string DefaultMode(string type) => type == "procedure" ? "search" : "always";

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
            .Select(c => new { c.Id, c.SourceRef, c.Text, Embedded = c.Vector.Length > 0 })
            .ToListAsync(ct);

        return rows.Select(r =>
        {
            var (section, from, to) = ParseRef(r.SourceRef);
            return new HrChunkDto(r.Id, r.SourceRef, section, from, to, r.Text.Length,
                PoPromptBuilder.EstimateTokens(r.Text), r.Embedded, r.Text);
        }).ToList();
    }

    /// <summary>
    /// The best `search` excerpts for a question: Kind = "hr" only, active items
    /// only, top <paramref name="top"/> above Hr:MinScore.
    /// </summary>
    public async Task<IReadOnlyList<HrHitDto>> RetrieveAsync(string question, int top = 8, CancellationToken ct = default)
    {
        var vector = await embedder.EmbedAsync(question, ct);
        var minScore = float.TryParse(config["Hr:MinScore"], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var m) ? m : 0.3f;

        var active = (await sources.ListSharedAsync(Kind, ct))
            .Where(i => i.IsActive).ToDictionary(i => i.Id, i => i.Name);

        // HR chunks carry no owner, so the owner gate passes them; the kind gate
        // is what keeps documents out of HR answers and HR out of documents.
        return index.Search(vector, userId: 0, topK: Math.Max(top * 2, top), minScore: minScore, kind: Kind)
            .Where(h => h.ContextSourceId is int id && active.ContainsKey(id))
            .Take(top)
            .Select(h =>
            {
                var (section, from, to) = ParseRef(h.SourceRef);
                return new HrHitDto(h.ContextSourceId!.Value, active[h.ContextSourceId.Value], h.SourceRef,
                    section, from, to, h.Score, h.Text);
            })
            .ToList();
    }

    /// <summary>HR chunks in memory and in the database, per item — the "no orphans" evidence.</summary>
    public async Task<object> IndexStatsAsync(CancellationToken ct = default)
    {
        var inIndex = index.CountByContextSource(Kind);
        var inDb = (await db.EmbeddingChunks.AsNoTracking()
                .Where(c => c.Kind == Kind && c.ContextSourceId != null && c.Vector.Length > 0)
                .GroupBy(c => c.ContextSourceId!.Value)
                .Select(g => new { Id = g.Key, N = g.Count() })
                .ToListAsync(ct))
            .ToDictionary(x => x.Id, x => x.N);

        var ids = inIndex.Keys.Union(inDb.Keys).OrderBy(i => i);
        return new
        {
            indexChunks = inIndex.Values.Sum(),
            dbChunks = inDb.Values.Sum(),
            byItem = ids.ToDictionary(
                i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                i => new { index = inIndex.GetValueOrDefault(i), db = inDb.GetValueOrDefault(i) }),
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
        if ((await sources.ListSharedAsync(Kind, ct)).Any(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)))
            return (SharedOutcome.Ok, null, $"An item named \"{name}\" is already in the library.");

        var item = new ContextSource
        {
            Kind = Kind,
            Name = name,
            HrType = type,
            Mode = mode,
            Content = Normalize(input.Content),
            IsActive = input.IsActive ?? true,
        };
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

        var item = write.Item!;
        // The chunks follow the text, the mode and the name (the name is in every SourceRef).
        if (item.Content != beforeContent || item.Mode != beforeMode || item.Name != beforeName)
            await RechunkAsync(item, ct);

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
            index.RemoveContextSource(id);
            return SharedOutcome.Ok;
        }
        finally { ChunkGate.Release(); }
    }

    /// <summary>
    /// Reads an uploaded file as text, honouring a byte-order mark — two of the
    /// procedure files HR exported are UTF-16.
    /// </summary>
    public static async Task<(string? Text, string? Error)> ReadUploadAsync(IFormFile file, CancellationToken ct)
    {
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!UploadExtensions.Contains(ext))
            return (null, $"Only {string.Join(", ", UploadExtensions)} files can be added.");
        if (file.Length > MaxUploadBytes)
            return (null, $"The file is larger than {MaxUploadBytes / 1024 / 1024} MB.");

        using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = await reader.ReadToEndAsync(ct);
        return text.Contains('\0') ? (null, "The file is not text.") : (text, null);
    }

    /// <summary>What a file name becomes in the library, and its type: the seed and uploads agree.</summary>
    public static (string Name, string HrType) Describe(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var bare = Path.GetFileNameWithoutExtension(fileName);
        return ext switch
        {
            ".sql" => (fileName, "procedure"),
            ".csv" => (bare, "codes"),
            _ when bare.Contains("RULES", StringComparison.OrdinalIgnoreCase) => (bare, "rules"),
            _ when bare.Contains("CODE", StringComparison.OrdinalIgnoreCase) => (bare, "codes"),
            _ => (bare, "note"),
        };
    }

    // ───────────────────────── chunks ─────────────────────────

    /// <summary>Replaces an item's chunks: removed from the database and the index, re-split if `search`, then embedded.</summary>
    private async Task RechunkAsync(ContextSource item, CancellationToken ct)
    {
        await ChunkGate.WaitAsync(ct);
        try
        {
            await db.EmbeddingChunks.Where(c => c.ContextSourceId == item.Id).ExecuteDeleteAsync(ct);
            index.RemoveContextSource(item.Id);

            if (item.Mode != "search") return;

            foreach (var chunk in HrSplitter.Split(item.Name, item.Content))
            {
                db.EmbeddingChunks.Add(new EmbeddingChunk
                {
                    ContextSourceId = item.Id,
                    Kind = Kind,
                    SourceRef = chunk.SourceRef,
                    Text = chunk.Text,
                    Vector = Array.Empty<byte>(),   // embedded below, or later if Ollama is away
                });
            }
            await db.SaveChangesAsync(ct);
        }
        finally { ChunkGate.Release(); }

        await EmbedPendingAsync(item.Id, ct);
    }

    /// <summary>
    /// Embeds every HR chunk still without a vector (of one item, or all) and adds
    /// it to the index. Stops at the first embedding failure and leaves the rest
    /// for the next call. Returns how many were embedded.
    /// </summary>
    public async Task<int> EmbedPendingAsync(int? itemId = null, CancellationToken ct = default)
    {
        var pending = await db.EmbeddingChunks.AsNoTracking()
            .Where(c => c.Kind == Kind && c.Vector.Length == 0 && (itemId == null || c.ContextSourceId == itemId))
            .OrderBy(c => c.Id).Select(c => c.Id).ToListAsync(ct);

        var done = 0;
        foreach (var chunkId in pending)
        {
            await ChunkGate.WaitAsync(ct);
            try
            {
                var chunk = await db.EmbeddingChunks.FirstOrDefaultAsync(c => c.Id == chunkId, ct);
                if (chunk is null || chunk.Vector.Length > 0) continue;   // replaced or embedded meanwhile

                // The vector is made from the chunk WITH its SourceRef as a first line:
                // procedure code names things as @ServiceBenefitAmount, so without the
                // file and section ("§Service Benefit") a question asked in words has
                // little to match. The stored text — what the model is shown — stays
                // the code alone.
                float[] vector;
                try { vector = await embedder.EmbedAsync(EmbeddingText(chunk), ct); }
                catch (Exception e) when (e is HttpRequestException or InvalidOperationException or TaskCanceledException)
                {
                    log.LogWarning("HR library: embedding is unavailable ({Message}); {Count} chunk(s) wait for the next try.",
                        e.Message, pending.Count - done);
                    break;
                }

                chunk.Vector = VectorOps.ToBlob(vector);
                await db.SaveChangesAsync(ct);
                index.Add(chunk);
                done++;
            }
            finally { ChunkGate.Release(); }
        }
        return done;
    }

    // ───────────────────────── seed ─────────────────────────

    /// <summary>
    /// Adds every docs/hr/*.md and docs/hr/procedures/*.sql file not already in
    /// the library (matched by name), owned by <paramref name="owner"/>. Existing
    /// items are never touched, so an HR edit survives every restart. Chunks are
    /// written un-embedded; EmbedPendingAsync runs after start-up.
    /// </summary>
    public async Task<int> SeedAsync(string dir, User owner, CancellationToken ct = default)
    {
        var files = Directory.EnumerateFiles(dir, "*.md")
            .Concat(Directory.Exists(Path.Combine(dir, "procedures"))
                ? Directory.EnumerateFiles(Path.Combine(dir, "procedures"), "*.sql")
                : Enumerable.Empty<string>())
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

        var existing = (await sources.ListSharedAsync(Kind, ct)).Select(i => i.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = 0;
        foreach (var file in files)
        {
            var (name, type) = Describe(Path.GetFileName(file));
            if (existing.Contains(name)) continue;

            var item = new ContextSource
            {
                Kind = Kind,
                Name = name,
                HrType = type,
                Mode = DefaultMode(type),
                Content = Normalize(await File.ReadAllTextAsync(file, ct)),   // BOM-aware: UTF-16 files read as text
                IsActive = true,
            };
            var write = await sources.CreateSharedAsync(owner, item, ct);
            if (write.Outcome != SharedOutcome.Ok) continue;

            await ChunkGate.WaitAsync(ct);
            try
            {
                if (item.Mode == "search")
                {
                    foreach (var chunk in HrSplitter.Split(item.Name, item.Content))
                        db.EmbeddingChunks.Add(new EmbeddingChunk
                        {
                            ContextSourceId = item.Id, Kind = Kind, SourceRef = chunk.SourceRef,
                            Text = chunk.Text, Vector = Array.Empty<byte>(),
                        });
                    await db.SaveChangesAsync(ct);
                }
            }
            finally { ChunkGate.Release(); }
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

    // ───────────────────────── helpers ─────────────────────────

    /// <summary>
    /// What a chunk is embedded as: its SourceRef, then its text with code
    /// identifiers opened into words — @ServiceBenefitAmount reads as "Service
    /// Benefit Amount", Attendance_DailyAttendance as "Attendance Daily
    /// Attendance" — so a question asked in plain words can meet the code that
    /// answers it. Only the vector sees this form; the stored text is untouched.
    /// </summary>
    private static string EmbeddingText(EmbeddingChunk chunk)
    {
        var words = IdentifierBoundary().Replace(chunk.Text.Replace('@', ' ').Replace('_', ' '), " ");
        return string.IsNullOrWhiteSpace(chunk.SourceRef) ? words : $"{chunk.SourceRef}\n{words}";
    }

    [System.Text.RegularExpressions.GeneratedRegex("(?<=[a-z])(?=[A-Z])")]
    private static partial System.Text.RegularExpressions.Regex IdentifierBoundary();

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
        return new HrItemDto(i.Id, i.Name, i.HrType ?? "note", i.Mode ?? "always", i.Version,
            i.ApprovedBy, i.ApprovedAt, i.IsActive, i.SortOrder,
            i.Content.Length, PoPromptBuilder.EstimateTokens(i.Content), chunks, embedded, i.UpdatedAt, canEdit,
            withContent ? i.Content : null);
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

    private static (string? Section, int From, int To) ParseRef(string? sourceRef)
    {
        var m = sourceRef is null ? null : HrSplitter.SourceRefPattern().Match(sourceRef);
        if (m is null || !m.Success) return (null, 0, 0);
        return (m.Groups["section"].Success ? m.Groups["section"].Value : null,
            int.Parse(m.Groups["from"].Value), int.Parse(m.Groups["to"].Value));
    }
}
