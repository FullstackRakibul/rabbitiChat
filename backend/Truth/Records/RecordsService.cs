// The Records Department (docs/TRUTH_PROMPT_P5.md): one source's run (fetch → parse → save, with
// the schedule and backoff), what the admin sees and decides (§5), and what the public reads (§4.3).
//
// Trust rules (§0, §3.6): automation fills the wire with titles, short feed excerpts, times and
// links only. A model's digest stays a draft; an item made from a cluster is a draft item in an
// issue, published by a person the usual way. Nothing here sends mail or edits VersionLines on
// its own — approving a suggestion is a person's action, and every one is audited.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AngleSharp.Html.Parser;
using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.Data;
using RabbitIC.Api.DTOs.Truth;
using RabbitIC.Api.Models;
using RabbitIC.Api.ModelSources;

namespace RabbitIC.Api.Truth.Records;

public sealed record RunReport(string SourceId, bool Ok, int Status, bool NotModified, int Parsed, int Added, int Updated, string? Error, int Ms,
    List<object>? Preview = null);

public sealed class RecordsService(
    AppDbContext db, SourceRegistry registry, SafeFetcher fetcher, WireStore store, IHttpClientFactory http,
    IConfiguration config, TruthService truth)
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);
    public const int FlagAfterFailures = 10;
    public const int PageSize = 50;
    public const int DropTextMax = 100_000;
    public const string DropSourceId = "url-drop";

    public async Task<List<WireSource>> SourcesAsync(CancellationToken ct) =>
        registry.Effective(await db.TrWireSourceStates.AsNoTracking().ToListAsync(ct));

    private async Task<TrWireSourceState> StateAsync(string sourceId, CancellationToken ct)
    {
        var state = await db.TrWireSourceStates.FirstOrDefaultAsync(s => s.SourceId == sourceId, ct)
            ?? db.TrWireSourceStates.Local.FirstOrDefault(s => s.SourceId == sourceId);
        if (state is null) db.TrWireSourceStates.Add(state = new TrWireSourceState { SourceId = sourceId });
        return state;
    }

    // ─────────────────────────── running a source (§3.1) ───────────────────────────

    /// <summary>Sources whose time has come: on, not flagged, not the editor's URL drop.</summary>
    public async Task<List<WireSource>> DueAsync(DateTime now, CancellationToken ct)
    {
        var states = await db.TrWireSourceStates.AsNoTracking().ToListAsync(ct);
        var byId = states.ToDictionary(s => s.SourceId);
        return registry.Effective(states).Where(s => s.Enabled && s.Adapter != "url-drop" && s.Adapter != "osv-api"
            && byId.GetValueOrDefault(s.Id) is not { Flagged: true }
            && (byId.GetValueOrDefault(s.Id)?.NextRunAtUtc ?? DateTime.MinValue) <= now).ToList();
    }

    /// <summary>
    /// One fetch of one registered source. <paramref name="save"/> false is the admin's "Test now":
    /// fetch and parse, show what came back, store nothing and leave the schedule alone.
    /// </summary>
    public async Task<RunReport> RunAsync(string sourceId, bool save, CancellationToken ct)
    {
        // One run of a source at a time: a person's "Collect now" waits for a scheduled run, never races it.
        var gate = Gates.GetOrAdd(sourceId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try { return await RunLockedAsync(sourceId, save, ct); }
        finally { gate.Release(); }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Gates = new();

    private async Task<RunReport> RunLockedAsync(string sourceId, bool save, CancellationToken ct)
    {
        var source = (await SourcesAsync(ct)).FirstOrDefault(s => s.Id == sourceId);
        if (source is null) return new RunReport(sourceId, false, 0, false, 0, 0, 0, "Not in the registry.", 0);
        if (source.Adapter is "url-drop" or "osv-api") return new RunReport(sourceId, false, 0, false, 0, 0, 0, $"{source.Adapter} is not fetched on a schedule.", 0);
        var now = DateTime.UtcNow;
        var state = await StateAsync(source.Id, ct);
        var fetched = await fetcher.GetAsync(new Uri(source.Url), source.MaxKB * 1024,
            save ? state.ETag : null, save ? state.LastModified : null, ct);
        RunReport report;
        if (fetched.NotModified) report = new RunReport(source.Id, true, 304, true, 0, 0, 0, null, fetched.Ms);
        else if (!fetched.Ok) report = new RunReport(source.Id, false, fetched.Status, false, 0, 0, 0, fetched.Error, fetched.Ms);
        else
        {
            try
            {
                var parsed = Adapters.Parse(source, fetched.Text, now);
                if (!save)
                    return new RunReport(source.Id, true, 200, false, parsed.Count, 0, 0, null, fetched.Ms,
                        parsed.Take(10).Select(p => (object)new { p.Title, p.Excerpt, p.Url, at = TruthJson.Utc(p.PublishedAtUtc), p.State, p.Kind }).ToList());
                var saved = await store.SaveAsync(source, parsed, now, ct);
                report = new RunReport(source.Id, true, 200, false, parsed.Count, saved.Added, saved.Updated, null, fetched.Ms);
                state.ETag = fetched.ETag;
                state.LastModified = fetched.LastModified;
            }
            catch (FeedFormatException e) { report = new RunReport(source.Id, false, 200, false, 0, 0, 0, e.Message, fetched.Ms); }
        }
        if (!save) return report;
        state.LastAttemptAtUtc = now;
        state.LastDurationMs = report.Ms;
        if (report.Ok)
        {
            state.Failures = 0;
            state.LastSuccessAtUtc = now;
            state.NextRunAtUtc = now + Jittered(source.EveryMinutes);
        }
        else
        {
            state.Failures++;
            state.LastError = report.Error is { Length: > 300 } long_ ? long_[..300] : report.Error;
            state.LastErrorAtUtc = now;
            state.NextRunAtUtc = now + Backoff(state.Failures, source.EveryMinutes);
            if (state.Failures >= FlagAfterFailures) state.Flagged = true;
        }
        await db.SaveChangesAsync(ct);
        return report;
    }

    /// <summary>Every <paramref name="minutes"/> (never under 5), ±20 %.</summary>
    public static TimeSpan Jittered(int minutes) =>
        TimeSpan.FromMinutes(Math.Max(SourceRegistry.MinimumMinutes, minutes) * (0.8 + Random.Shared.NextDouble() * 0.4));

    /// <summary>5 minutes after the first failure, doubling, at most an hour — and never sooner than the usual interval.</summary>
    public static TimeSpan Backoff(int failures, int minutes) =>
        TimeSpan.FromMinutes(Math.Max(Math.Max(SourceRegistry.MinimumMinutes, minutes), Math.Min(60, 5 * Math.Pow(2, Math.Max(0, failures - 1)))));

    // ─────────────────────────── admin: sources (§5) ───────────────────────────

    public async Task<object> SourcesViewAsync(CancellationToken ct)
    {
        var states = await db.TrWireSourceStates.AsNoTracking().ToDictionaryAsync(s => s.SourceId, ct);
        var since = DateTime.UtcNow.AddHours(-24);
        var counts = await db.TrWireEntries.Where(e => e.FirstSeenAtUtc >= since).GroupBy(e => e.SourceId)
            .Select(g => new { g.Key, n = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.n, ct);
        return new
        {
            file = Path.GetFileName(registry.File), loadErrors = registry.LoadErrors,
            sources = registry.Effective(states.Values).Select(s =>
            {
                var st = states.GetValueOrDefault(s.Id);
                return new
                {
                    definition = SourceRegistry.ToJson(s), s.Origin, overridden = st?.OverrideJson is not null,
                    health = new
                    {
                        lastSuccess = TruthJson.Utc(st?.LastSuccessAtUtc), lastAttempt = TruthJson.Utc(st?.LastAttemptAtUtc),
                        lastError = st?.LastError, lastErrorAt = TruthJson.Utc(st?.LastErrorAtUtc), failures = st?.Failures ?? 0,
                        flagged = st?.Flagged ?? false, nextRun = TruthJson.Utc(st?.NextRunAtUtc), entries24h = counts.GetValueOrDefault(s.Id),
                        ms = st?.LastDurationMs ?? 0,
                    },
                };
            }),
        };
    }

    /// <summary>The switch. Turning a flagged source back on clears its failures: a person looked.</summary>
    public async Task<TrResult<bool>> SetEnabledAsync(string id, bool on, User user, CancellationToken ct)
    {
        if ((await SourcesAsync(ct)).All(s => s.Id != id)) return TrResult<bool>.NotFound("No such source.");
        var state = await StateAsync(id, ct);
        state.Enabled = on;
        if (on && state.Flagged) { state.Flagged = false; state.Failures = 0; state.NextRunAtUtc = null; }
        Audit(user, on ? "source.on" : "source.off", id, null);
        await db.SaveChangesAsync(ct);
        return TrResult<bool>.Ok(true);
    }

    /// <summary>Adds or edits a source definition, checked like the file's — and stricter: under 5 minutes is refused.</summary>
    public async Task<TrResult<bool>> SaveOverrideAsync(JsonObject definition, User user, CancellationToken ct)
    {
        var (source, error) = SourceRegistry.Parse(definition, "admin");
        if (source is null) return TrResult<bool>.Invalid(error!);
        var state = await StateAsync(source.Id, ct);
        state.OverrideJson = SourceRegistry.ToJson(source).ToJsonString();
        state.NextRunAtUtc = null;
        Audit(user, "source.override", source.Id, null);
        await db.SaveChangesAsync(ct);
        return TrResult<bool>.Ok(true);
    }

    public async Task<TrResult<bool>> RemoveOverrideAsync(string id, User user, CancellationToken ct)
    {
        var state = await db.TrWireSourceStates.FirstOrDefaultAsync(s => s.SourceId == id, ct);
        if (state?.OverrideJson is null) return TrResult<bool>.NotFound("That source has no override.");
        state.OverrideJson = null;
        Audit(user, "source.override.removed", id, null);
        await db.SaveChangesAsync(ct);
        return TrResult<bool>.Ok(true);
    }

    // ─────────────────────────── admin: inbox, visibility, suggestions, audit ───────────────────────────

    public async Task<object> InboxAsync(CancellationToken ct)
    {
        var sources = (await SourcesAsync(ct)).ToDictionary(s => s.Id);
        var inbox = await db.TrWireEntries.AsNoTracking().Where(e => e.Visibility == WireVisibility.Inbox)
            .OrderByDescending(e => e.PublishedAtUtc).Take(200).ToListAsync(ct);
        var conflicted = await db.TrWireClusters.AsNoTracking().Where(c => c.ConflictsJson != "[]").OrderByDescending(c => c.UpdatedAtUtc).Take(50).ToListAsync(ct);
        return new
        {
            entries = inbox.Select(e => AdminEntry(e, sources.GetValueOrDefault(e.SourceId))),
            flagged = conflicted.Select(c => new { clusterId = c.Id, conflicts = JsonNode.Parse(c.ConflictsJson), updatedAt = TruthJson.Utc(c.UpdatedAtUtc) }),
        };
    }

    public async Task<object?> ClusterAdminAsync(int id, CancellationToken ct)
    {
        var c = await db.TrWireClusters.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return null;
        var sources = (await SourcesAsync(ct)).ToDictionary(s => s.Id);
        var entries = await db.TrWireEntries.AsNoTracking().Where(e => e.ClusterId == id).OrderBy(e => e.PublishedAtUtc).ToListAsync(ct);
        var digests = await db.TrWireDigests.AsNoTracking().Where(d => d.ClusterId == id).OrderByDescending(d => d.CreatedAtUtc).ToListAsync(ct);
        return new
        {
            clusterId = c.Id, conflicts = JsonNode.Parse(c.ConflictsJson),
            entries = entries.Select(e => AdminEntry(e, sources.GetValueOrDefault(e.SourceId))),
            digests = digests.Select(d => new { d.Id, d.TextBn, d.TextEn, d.Model, numberProblems = JsonNode.Parse(d.NumberProblemsJson), d.ItemId, createdAt = TruthJson.Utc(d.CreatedAtUtc) }),
        };
    }

    private static object AdminEntry(TrWireEntry e, WireSource? s) => new
    {
        e.Id, e.SourceId, source = s?.Name ?? e.SourceId, tier = s?.Tier, e.Kind, e.Title, e.Excerpt, e.Url, e.State, e.Severity, e.Visibility, e.ClusterId,
        at = TruthJson.Utc(e.PublishedAtUtc), updatedAt = TruthJson.Utc(e.UpdatedAtUtc),
        cves = TruthJson.Strings(e.CveIdsJson), techs = TruthJson.Strings(e.TechsJson), pageTextLength = e.PageText?.Length ?? 0,
    };

    /// <summary>Publish to the wire, send back to the inbox, or hide (logged — §3.2).</summary>
    public async Task<TrResult<bool>> SetVisibilityAsync(int entryId, string visibility, string? note, User user, CancellationToken ct)
    {
        if (visibility is not (WireVisibility.Public or WireVisibility.Inbox or WireVisibility.Hidden)) return TrResult<bool>.Invalid("public, inbox or hidden.");
        var e = await db.TrWireEntries.FindAsync(new object[] { entryId }, ct);
        if (e is null) return TrResult<bool>.NotFound("No such entry.");
        if (visibility == WireVisibility.Hidden && string.IsNullOrWhiteSpace(note)) return TrResult<bool>.Invalid("Say why it is hidden (a short note).");
        var from = e.Visibility;
        e.Visibility = visibility;
        Audit(user, $"entry.{visibility}", $"entry:{e.Id}", $"{from} → {visibility}" + (string.IsNullOrWhiteSpace(note) ? "" : $": {note.Trim()}"));
        await db.SaveChangesAsync(ct);
        if (e.ClusterId is { } c) { await store.RecomputeConflictsAsync(c, ct); await db.SaveChangesAsync(ct); }
        return TrResult<bool>.Ok(true);
    }

    public async Task<object> SuggestionsAsync(CancellationToken ct) =>
        (await db.TrWireSuggestions.AsNoTracking().OrderBy(s => s.Status != "open").ThenByDescending(s => s.CreatedAtUtc).Take(200).ToListAsync(ct))
        .Select(s => new { s.Id, s.Kind, s.Reason, s.ClusterId, s.TechSlug, payload = JsonNode.Parse(s.PayloadJson), s.Status, createdAt = TruthJson.Utc(s.CreatedAtUtc), decidedAt = TruthJson.Utc(s.DecidedAtUtc) });

    /// <summary>
    /// A person's decision on a suggestion: "ignore", "done" (they acted on it, e.g. made an item), or
    /// "approve" for a version line — which then, and only then, adds the line to Tech.VersionLines.
    /// </summary>
    public async Task<TrResult<bool>> DecideAsync(int id, string decision, User user, CancellationToken ct)
    {
        var s = await db.TrWireSuggestions.FindAsync(new object[] { id }, ct);
        if (s is null) return TrResult<bool>.NotFound("No such suggestion.");
        if (s.Status != "open") return TrResult<bool>.Invalid("Already decided.");
        switch (decision)
        {
            case "ignore": s.Status = "ignored"; break;
            case "done": s.Status = "done"; break;
            case "approve" when s.Kind == "versionLine":
                var line = JsonNode.Parse(s.PayloadJson)?["line"]?.GetValue<string>();
                var tech = await db.TrTechs.FirstOrDefaultAsync(t => t.Slug == s.TechSlug, ct);
                if (line is null || tech is null) return TrResult<bool>.Invalid("The technology or line is gone.");
                var lines = TruthJson.Strings(tech.VersionLinesJson);
                if (!lines.Contains(line)) lines.Add(line);
                tech.VersionLinesJson = TruthJson.FromStrings(lines.OrderBy(l => l, Comparer<string>.Create(GuideTemplate.CompareVersions)));
                s.Status = "done";
                break;
            default: return TrResult<bool>.Invalid("ignore, done, or approve (version lines only).");
        }
        s.DecidedByUserId = user.Id;
        s.DecidedAtUtc = DateTime.UtcNow;
        Audit(user, $"suggestion.{decision}", $"suggestion:{s.Id}", s.Reason);
        await db.SaveChangesAsync(ct);
        return TrResult<bool>.Ok(true);
    }

    public async Task<object> AuditAsync(CancellationToken ct)
    {
        var rows = await db.TrWireAudits.AsNoTracking().OrderByDescending(a => a.AtUtc).Take(300).ToListAsync(ct);
        var ids = rows.Select(r => r.UserId).Distinct().ToList();
        var names = await db.Users.Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Username, ct);
        return rows.Select(a => new { at = TruthJson.Utc(a.AtUtc), by = names.GetValueOrDefault(a.UserId), a.Action, a.Target, a.Note });
    }

    private void Audit(User user, string action, string target, string? note) =>
        db.TrWireAudits.Add(new TrWireAudit { AtUtc = DateTime.UtcNow, UserId = user.Id, Action = action, Target = target, Note = note is { Length: > 500 } ? note[..500] : note });

    // ─────────────────────────── URL drop (§3.4) ───────────────────────────

    /// <summary>
    /// An editor's link to a page with no feed: fetched once under the same guard, robots.txt first.
    /// The readable text is kept privately for the number check; the inbox gets the title and link.
    /// </summary>
    public async Task<TrResult<int>> DropAsync(string url, User user, CancellationToken ct)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)) return TrResult<int>.Invalid("Paste a full link.");
        if (uri.Scheme != Uri.UriSchemeHttps) return TrResult<int>.Invalid("Only https links are fetched.");
        var (allowed, why) = await fetcher.RobotsAsync(uri, ct);
        if (!allowed) return TrResult<int>.Invalid($"Not fetched: {why}.");
        var fetched = await fetcher.GetAsync(uri, 2 * 1024 * 1024, ct: ct);
        if (!fetched.Ok) return TrResult<int>.Invalid($"Could not fetch it: {fetched.Error}");
        var doc = new HtmlParser().ParseDocument(fetched.Text);
        foreach (var junk in doc.QuerySelectorAll("script, style, noscript, nav, header, footer, aside, form, iframe, svg")) junk.Remove();
        var title = doc.QuerySelector("meta[property='og:title']")?.GetAttribute("content") ?? doc.Title ?? uri.Host;
        var main = doc.QuerySelector("article") ?? doc.QuerySelector("main") ?? doc.Body;
        var text = Adapters.Clean(main?.TextContent ?? "", keepLines: false);
        if (text.Length > DropTextMax) text = text[..DropTextMax];
        var description = doc.QuerySelector("meta[name='description']")?.GetAttribute("content") ?? doc.QuerySelector("meta[property='og:description']")?.GetAttribute("content");
        var source = (await SourcesAsync(ct)).FirstOrDefault(s => s.Id == DropSourceId)
            ?? new WireSource(DropSourceId, "URL drop", WireKind.News, "url-drop", "", null, Array.Empty<string>(), "community", 60, true, "inbox", "UD", 30, 2048, new JsonObject(), "file");
        // Always the inbox, whatever the definition says: a dropped page is never published as is.
        source = source with { Publish = "inbox" };
        var now = DateTime.UtcNow;
        var link = Adapters.NormaliseUrl(uri.ToString());
        var parsed = new ParsedEntry(Adapters.Hash(link), source.Kind, Adapters.Clean(title).Length > 300 ? Adapters.Clean(title)[..300] : Adapters.Clean(title),
            Adapters.Excerpt(description ?? ""), link, now, now);
        var saved = await store.SaveAsync(source, new[] { parsed }, now, ct);
        var entry = await db.TrWireEntries.FirstAsync(e => e.SourceId == source.Id && e.ExternalId == parsed.ExternalId, ct);
        entry.PageText = text;
        Audit(user, "drop", $"entry:{entry.Id}", link);
        await db.SaveChangesAsync(ct);
        return TrResult<int>.Ok(entry.Id);
    }

    // ─────────────────────────── AI digest and items (§3.5, §3.6) ───────────────────────────

    private static readonly JsonObject DigestSchema = JsonNode.Parse("""
        {"type":"object","properties":{"summaryBn":{"type":"string"},"summaryEn":{"type":"string"}},"required":["summaryBn","summaryEn"]}
        """)!.AsObject();

    /// <summary>A model's 2–3 line Bangla + English summary of a cluster, from stored excerpts and text only: always a draft.</summary>
    public async Task<TrResult<int>> DigestAsync(int clusterId, User user, CancellationToken ct)
    {
        var entries = await db.TrWireEntries.Where(e => e.ClusterId == clusterId && e.Visibility != WireVisibility.Hidden).ToListAsync(ct);
        if (entries.Count == 0) return TrResult<int>.NotFound("No such cluster, or nothing in it is visible.");
        var model = config["Truth:Records:DigestModel"] ?? config["Ollama:DefaultModel"];
        if (string.IsNullOrWhiteSpace(model)) return TrResult<int>.Invalid("No model is configured (Truth:Records:DigestModel).");
        var material = new StringBuilder();
        foreach (var e in entries)
        {
            material.AppendLine($"[{e.SourceId}] {e.Title}");
            if (e.Excerpt.Length > 0) material.AppendLine(e.Excerpt);
            if (e.PageText is { } page) material.AppendLine(page.Length > 6000 ? page[..6000] : page);
            material.AppendLine();
        }
        var messages = new[]
        {
            new SchemaJsonMessage("system",
                "You write a short public security bulletin entry. Summarise the reports below in 2–3 plain sentences, once in Bangla (summaryBn) " +
                "and once in English (summaryEn), in your own words. Use only facts in the reports. Copy every number, version, CVE ID, date " +
                "and time exactly as a report gives it, and add none of your own. No markdown, no HTML, no links."),
            new SchemaJsonMessage("user", material.ToString()),
        };
        var answer = await SchemaJsonChat.CallAsync(http, model!, DigestSchema, messages, 8192, ct);
        if (answer.Error is not null || answer.Content is null) return TrResult<int>.Invalid($"The model did not answer: {answer.Error}");
        string bn, en;
        try
        {
            var o = JsonNode.Parse(answer.Content) as JsonObject;
            bn = Adapters.Clean(o?["summaryBn"]?.GetValue<string>() ?? "");
            en = Adapters.Clean(o?["summaryEn"]?.GetValue<string>() ?? "");
        }
        catch (JsonException) { return TrResult<int>.Invalid("The model's answer was not the expected JSON."); }
        if (bn.Length == 0) return TrResult<int>.Invalid("The model returned no Bangla summary.");
        bn = bn.Length > 1000 ? bn[..1000] : bn;
        en = en.Length > 1000 ? en[..1000] : en;
        var sourcesForCheck = entries.Select(e => new TrSource { Title = e.Title, FetchedText = $"{e.Excerpt}\n{e.PageText}" }).ToList();
        var problems = NumberCheck.Run(new[] { ("summaryBn", bn), ("summaryEn", en) }, sourcesForCheck, new Dictionary<string, string>())
            .Where(f => !f.Ok).Select(f => f.Token).ToList();
        var digest = new TrWireDigest
        {
            ClusterId = clusterId, TextBn = bn, TextEn = en, Model = model!, NumberProblemsJson = JsonSerializer.Serialize(problems),
            CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = user.Id,
        };
        db.TrWireDigests.Add(digest);
        Audit(user, "digest", $"cluster:{clusterId}", $"{problems.Count} number(s) not in the sources");
        await db.SaveChangesAsync(ct);
        return TrResult<int>.Ok(digest.Id);
    }

    /// <summary>
    /// "Create item from cluster": a draft item in a draft or published issue, with every visible
    /// entry as a reference and its stored text for the number check. It goes public only when a
    /// person publishes the issue, through every check publishing has.
    /// </summary>
    public async Task<TrResult<int>> ItemFromClusterAsync(int clusterId, int issueId, int? techId, int? digestId, User user, CancellationToken ct)
    {
        var entries = await db.TrWireEntries.Where(e => e.ClusterId == clusterId && e.Visibility != WireVisibility.Hidden).OrderBy(e => e.PublishedAtUtc).ToListAsync(ct);
        if (entries.Count == 0) return TrResult<int>.NotFound("No such cluster, or nothing in it is visible.");
        var sources = (await SourcesAsync(ct)).ToDictionary(s => s.Id);
        var digest = digestId is null ? null : await db.TrWireDigests.FirstOrDefaultAsync(d => d.Id == digestId && d.ClusterId == clusterId, ct);
        if (digestId is not null && digest is null) return TrResult<int>.NotFound("No such digest for this cluster.");
        if (techId is null)
        {
            var slugs = entries.SelectMany(e => TruthJson.Strings(e.TechsJson)).Distinct().ToList();
            techId = (await db.TrTechs.Where(t => slugs.Contains(t.Slug)).Select(t => (int?)t.Id).FirstOrDefaultAsync(ct));
            if (techId is null) return TrResult<int>.Invalid("None of the cluster's technologies is in the Ministry's list; choose one.");
        }
        var best = Best(entries, sources);
        var severity = entries.Select(e => e.Severity).Where(s => s is not null).OrderBy(s => Array.IndexOf(TrSeverity.All, s)).FirstOrDefault() ?? "medium";
        var order = await db.TrItems.CountAsync(i => i.IssueId == issueId, ct) + 1;
        var refs = entries.Take(20).Select(e =>
        {
            var s = sources.GetValueOrDefault(e.SourceId);
            var kind = s?.Tier switch { "official" or "authority" => "official", "community" => "community", _ => "news" };
            return new SourceInput(e.Url, s?.Name ?? e.SourceId, null, e.Title, e.PublishedAtUtc.ToString("yyyy-MM-dd"), kind,
                $"{e.Title}\n{e.Excerpt}\n{e.PageText}".Trim());
        }).Where(r => r.Url.StartsWith("https://", StringComparison.Ordinal)).ToList();
        var input = new ItemInput(techId.Value, severity, Cut(best.Title, 200), null,
            digest?.TextBn ?? Cut(best.Excerpt.Length > 0 ? best.Excerpt : best.Title, 4000), digest?.TextEn,
            null, null, null, null, null, null, Cut(user.Username, 80), order, null, null, refs);
        var made = await truth.CreateItemAsync(issueId, input, user, ct);
        if (made.Outcome != TrOutcome.Ok) return made;
        var itemId = made.Value;
        var item = await db.TrItems.FindAsync(new object[] { itemId }, ct);
        item!.WireClusterId = clusterId;
        if (digest is not null) digest.ItemId = itemId;
        foreach (var s in await db.TrWireSuggestions.Where(x => x.ClusterId == clusterId && x.Status == "open" && x.Kind == "urgent").ToListAsync(ct))
        { s.Status = "done"; s.DecidedByUserId = user.Id; s.DecidedAtUtc = DateTime.UtcNow; }
        Audit(user, "item.fromCluster", $"cluster:{clusterId}", $"draft item {itemId} in issue {issueId}");
        await db.SaveChangesAsync(ct);
        return TrResult<int>.Ok(itemId);
    }

    private static string Cut(string s, int n) => s.Length <= n ? s : s[..n];

    // ─────────────────────────── public (§4.3) ───────────────────────────

    private static int TierRank(string? tier) => tier switch { "official" => 0, "authority" => 1, "community" => 2, _ => 3 };

    private static TrWireEntry Best(IEnumerable<TrWireEntry> entries, IReadOnlyDictionary<string, WireSource> sources) =>
        entries.OrderBy(e => TierRank(sources.GetValueOrDefault(e.SourceId)?.Tier)).ThenBy(e => e.PublishedAtUtc).First();

    private static JsonObject SourceRef(string id, IReadOnlyDictionary<string, WireSource> sources)
    {
        var s = sources.GetValueOrDefault(id);
        return new JsonObject { ["id"] = id, ["name"] = s?.Name ?? id, ["monogram"] = s?.Monogram ?? id[..1].ToUpperInvariant(), ["hue"] = s?.Hue ?? 0, ["tier"] = s?.Tier ?? "news", ["homepage"] = s?.Homepage };
    }

    /// <summary>
    /// The live status strip (§4.1): one chip per status source. The state comes from the newest
    /// open incident; with no good check in 30 minutes the chip says stale and guesses nothing.
    /// </summary>
    public async Task<JsonObject> PublicStatusAsync(DateTime now, CancellationToken ct)
    {
        var states = await db.TrWireSourceStates.AsNoTracking().ToDictionaryAsync(s => s.SourceId, ct);
        var statusSources = registry.Effective(states.Values).Where(s => s.IsStatus && s.Enabled).ToList();
        var ids = statusSources.Select(s => s.Id).ToList();
        var entries = await db.TrWireEntries.AsNoTracking()
            .Where(e => ids.Contains(e.SourceId) && e.Visibility == WireVisibility.Public)
            .OrderByDescending(e => e.UpdatedAtUtc).ToListAsync(ct);
        var chips = new JsonArray();
        foreach (var s in statusSources)
        {
            var st = states.GetValueOrDefault(s.Id);
            var mine = entries.Where(e => e.SourceId == s.Id).ToList();
            var open = mine.FirstOrDefault(e => WireState.IsOpen(e.State) || e.State == WireState.Maintenance);
            var stale = st?.LastSuccessAtUtc is not { } ok || now - ok > StaleAfter;
            var state = stale ? "stale" : open is null ? "operational" : open.State == WireState.Maintenance ? "maintenance" : "incident";
            var chip = SourceRef(s.Id, ToDict(statusSources));
            chip["state"] = state;
            chip["lastChecked"] = TruthJson.Utc(st?.LastSuccessAtUtc);
            chip["open"] = open is null || stale ? null : StatusIncident(open);
            chip["recent"] = new JsonArray(mine.Where(e => e.State == WireState.Resolved).Take(5).Select(e => (JsonNode)StatusIncident(e)).ToArray());
            chips.Add(chip);
        }
        return new JsonObject { ["serverNow"] = TruthJson.Utc(now), ["sources"] = chips };
    }

    private static Dictionary<string, WireSource> ToDict(IEnumerable<WireSource> s) => s.ToDictionary(x => x.Id);

    private static JsonObject StatusIncident(TrWireEntry e) => new()
    {
        ["clusterId"] = e.ClusterId, ["title"] = e.Title, ["state"] = e.State, ["latest"] = e.Excerpt, ["url"] = e.Url,
        ["at"] = TruthJson.Utc(e.PublishedAtUtc), ["updatedAt"] = TruthJson.Utc(e.UpdatedAtUtc),
    };

    public sealed record WireFilter(string? Tech, string? Type, string? Severity, string? Tier, bool Open);

    /// <summary>The wire (§4.2): public rows, newest first, 50 at a time by cursor.</summary>
    public async Task<JsonObject> PublicWireAsync(string? cursor, WireFilter filter, DateTime now, CancellationToken ct)
    {
        var states = await db.TrWireSourceStates.AsNoTracking().ToListAsync(ct);
        var sources = ToDict(registry.Effective(states));
        var since = now.AddDays(-180);
        var entries = await db.TrWireEntries.AsNoTracking()
            .Where(e => e.Visibility == WireVisibility.Public && e.ClusterId != null && e.UpdatedAtUtc >= since)
            .ToListAsync(ct);
        var verified = await VerifiedAsync(entries.Select(e => e.ClusterId!.Value).Distinct().ToList(), ct);
        var rows = entries.GroupBy(e => e.ClusterId!.Value).Select(g => Row(g.Key, g.ToList(), sources, verified)).ToList();
        IEnumerable<JsonObject> q = rows;
        if (filter.Tech is { Length: > 0 } tech) q = q.Where(r => (r["techs"] as JsonArray)!.Any(t => t!.GetValue<string>() == tech));
        if (filter.Type is { Length: > 0 } type) q = q.Where(r => r["kind"]!.GetValue<string>() == type);
        if (filter.Severity is { Length: > 0 } sev) q = q.Where(r => r["severity"]?.GetValue<string>() == sev);
        if (filter.Tier is { Length: > 0 } tier) q = q.Where(r => r["source"]!["tier"]!.GetValue<string>() == tier);
        if (filter.Open) q = q.Where(r => WireState.IsOpen(r["state"]?.GetValue<string>()) || r["state"]?.GetValue<string>() == WireState.Maintenance);
        var ordered = q.OrderByDescending(r => (long)r["sortKey"]!).ThenByDescending(r => (int)r["clusterId"]!).ToList();
        if (ParseCursor(cursor) is { } c)
            ordered = ordered.Where(r => (long)r["sortKey"]! < c.Ticks || ((long)r["sortKey"]! == c.Ticks && (int)r["clusterId"]! < c.Id)).ToList();
        var page = ordered.Take(PageSize).ToList();
        string? next = ordered.Count > PageSize && page.LastOrDefault() is { } last ? MakeCursor((long)last["sortKey"]!, (int)last["clusterId"]!) : null;
        foreach (var r in page) r.Remove("sortKey");
        var lastCollected = states.Where(s => s.LastSuccessAtUtc is not null).Select(s => s.LastSuccessAtUtc).DefaultIfEmpty().Max();
        return new JsonObject
        {
            ["serverNow"] = TruthJson.Utc(now), ["lastCollected"] = TruthJson.Utc(lastCollected),
            ["rows"] = new JsonArray(page.Select(r => (JsonNode)r).ToArray()), ["next"] = next,
        };
    }

    private JsonObject Row(int clusterId, List<TrWireEntry> entries, IReadOnlyDictionary<string, WireSource> sources, IReadOnlyDictionary<int, JsonObject> verified)
    {
        var best = Best(entries, sources);
        var latest = entries.Max(e => e.UpdatedAtUtc > e.PublishedAtUtc ? e.UpdatedAtUtc : e.PublishedAtUtc);
        return new JsonObject
        {
            ["clusterId"] = clusterId, ["kind"] = best.Kind, ["title"] = best.Title, ["at"] = TruthJson.Utc(latest),
            ["publishedAt"] = TruthJson.Utc(entries.Min(e => e.PublishedAtUtc)),
            ["source"] = SourceRef(best.SourceId, sources), ["sources"] = entries.Select(e => e.SourceId).Distinct().Count(),
            ["severity"] = entries.Select(e => e.Severity).Where(s => s is not null).OrderBy(s => Array.IndexOf(TrSeverity.All, s)).FirstOrDefault(),
            ["state"] = best.State,
            ["cves"] = Arr(entries.SelectMany(e => TruthJson.Strings(e.CveIdsJson)).Distinct().OrderBy(x => x)),
            ["techs"] = Arr(entries.SelectMany(e => TruthJson.Strings(e.TechsJson)).Distinct().OrderBy(x => x)),
            ["auto"] = true, ["verified"] = verified.GetValueOrDefault(clusterId)?.DeepClone(),
            ["sortKey"] = latest.Ticks,
        };
    }

    /// <summary>The expanded row (§4.2): every source with tier and link, the update history, "Sources differ".</summary>
    public async Task<JsonObject?> PublicClusterAsync(int id, CancellationToken ct)
    {
        var cluster = await db.TrWireClusters.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (cluster is null) return null;
        var entries = await db.TrWireEntries.AsNoTracking().Where(e => e.ClusterId == id && e.Visibility == WireVisibility.Public)
            .OrderBy(e => e.PublishedAtUtc).ToListAsync(ct);
        if (entries.Count == 0) return null;
        var sources = ToDict(await SourcesAsync(ct));
        var entryIds = entries.Select(e => e.Id).ToList();
        var history = await db.TrWireEntryHistories.AsNoTracking().Where(h => entryIds.Contains(h.EntryId)).OrderBy(h => h.AtUtc).ToListAsync(ct);
        var verified = await VerifiedAsync(new List<int> { id }, ct);
        // Conflicts name sources by id: only the ones whose entry is public stay.
        var publicSources = entries.Select(e => e.SourceId).ToHashSet();
        var conflicts = new JsonArray();
        foreach (var c in JsonNode.Parse(cluster.ConflictsJson) as JsonArray ?? new JsonArray())
        {
            var values = (c!["values"] as JsonArray)!.Select(v => new
            {
                value = v!["value"]!.GetValue<string>(),
                srcs = (v["sources"] as JsonArray)!.Select(s => s!.GetValue<string>()).Where(publicSources.Contains).ToList(),
            }).Where(v => v.srcs.Count > 0).ToList();
            if (values.Count < 2) continue;
            conflicts.Add(new JsonObject
            {
                ["field"] = c["field"]!.GetValue<string>(), ["line"] = c["line"]?.GetValue<string>(),
                ["values"] = new JsonArray(values.Select(v => (JsonNode)new JsonObject
                {
                    ["value"] = v.value, ["sources"] = new JsonArray(v.srcs.Select(s => (JsonNode)SourceRef(s, sources)).ToArray()),
                }).ToArray()),
            });
        }
        return new JsonObject
        {
            ["clusterId"] = id, ["title"] = Best(entries, sources).Title,
            ["cves"] = Arr(entries.SelectMany(e => TruthJson.Strings(e.CveIdsJson)).Distinct().OrderBy(x => x)),
            ["techs"] = Arr(entries.SelectMany(e => TruthJson.Strings(e.TechsJson)).Distinct().OrderBy(x => x)),
            ["versions"] = Arr(entries.SelectMany(e => TruthJson.Strings(e.VersionsJson)).Distinct().Take(12)),
            ["conflicts"] = conflicts, ["verified"] = verified.GetValueOrDefault(id)?.DeepClone(),
            ["entries"] = new JsonArray(entries.Select(e => (JsonNode)new JsonObject
            {
                ["source"] = SourceRef(e.SourceId, sources), ["kind"] = e.Kind, ["title"] = e.Title, ["excerpt"] = e.Excerpt, ["url"] = e.Url,
                ["at"] = TruthJson.Utc(e.PublishedAtUtc), ["updatedAt"] = TruthJson.Utc(e.UpdatedAtUtc), ["state"] = e.State, ["severity"] = e.Severity,
                ["components"] = JsonNode.Parse(e.ComponentsJson),
                ["history"] = new JsonArray(Updates(e, history.Where(h => h.EntryId == e.Id)).Select(u => (JsonNode)u).ToArray()),
            }).ToArray()),
        };
    }

    /// <summary>The feed's own list of updates when it has one; else what the collector saw change; then now.</summary>
    private static IEnumerable<JsonObject> Updates(TrWireEntry e, IEnumerable<TrWireEntryHistory> seen)
    {
        var listed = JsonNode.Parse(e.UpdatesJson) as JsonArray ?? new JsonArray();
        if (listed.Count > 0)
            return listed.Select(u => new JsonObject { ["at"] = u!["at"]?.GetValue<string>(), ["state"] = u["state"]?.GetValue<string>() }).Reverse();
        return seen.Select(h => new JsonObject { ["at"] = TruthJson.Utc(h.AtUtc), ["state"] = h.State })
            .Append(new JsonObject { ["at"] = TruthJson.Utc(e.UpdatedAtUtc), ["state"] = e.State })
            .Where(u => u["state"] is not null);
    }

    /// <summary>Clusters with an item in a published issue: the row links to the verified item.</summary>
    private async Task<Dictionary<int, JsonObject>> VerifiedAsync(List<int> clusterIds, CancellationToken ct)
    {
        var found = await (from i in db.TrItems.AsNoTracking()
                           join s in db.TrIssues.AsNoTracking() on i.IssueId equals s.Id
                           where i.WireClusterId != null && clusterIds.Contains(i.WireClusterId.Value) && s.Status == TrIssueStatus.Published
                           select new { ClusterId = i.WireClusterId!.Value, ItemId = i.Id, s.Slug }).ToListAsync(ct);
        var published = new Dictionary<int, JsonObject>();
        foreach (var f in found)
            published.TryAdd(f.ClusterId, new JsonObject { ["issueSlug"] = f.Slug, ["itemId"] = f.ItemId });
        return published;
    }

    private static JsonArray Arr(IEnumerable<string> values) => new(values.Select(v => (JsonNode)v!).ToArray());

    private static string MakeCursor(long ticks, int id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ticks}_{id}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static (long Ticks, int Id)? ParseCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > 60) return null;
        try
        {
            var s = cursor.Replace('-', '+').Replace('_', '/');
            s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(s)).Split('_');
            return parts.Length == 2 && long.TryParse(parts[0], out var t) && int.TryParse(parts[1], out var i) ? (t, i) : null;
        }
        catch (FormatException) { return null; }
    }

    public static string ETagOf(string json) => $"\"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..32]}\"";
}
