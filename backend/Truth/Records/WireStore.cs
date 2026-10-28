// Saving what a source returned (docs/TRUTH_PROMPT_P5.md §3.2, §3.3, §3.6):
//
//   · upsert by (source, external id): a changed incident updates its entry and keeps the old
//     version in TrWireEntryHistory — never a second entry;
//   · extraction: CVE IDs, versions and techs (tech-keywords.json), and the numbers a cluster
//     compares — fixed versions and patch times;
//   · clustering: one CVE in common, or the same tech within 72 hours and similar titles
//     (token Jaccard ≥ 0.5); disagreements between a cluster's sources are recorded, not resolved;
//   · suggestions for a person — possible urgent, version line changed. Nothing here publishes
//     a summary, changes an item, edits VersionLines or sends mail.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.Data;
using RabbitIC.Api.Models;

namespace RabbitIC.Api.Truth.Records;

/// <summary>tech-keywords.json: {"php": ["php", "php-fpm", …], …}. Word matches in a title or excerpt tag the entry.</summary>
public sealed class TechKeywords
{
    public IReadOnlyDictionary<string, string[]> Map { get; }

    public TechKeywords(IConfiguration config)
    {
        var path = config["Truth:Records:KeywordsFile"];
        if (string.IsNullOrWhiteSpace(path)) path = Path.Combine(AppContext.BaseDirectory, "config", "tech-keywords.json");
        var map = new Dictionary<string, string[]>();
        if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject o)
            foreach (var (tech, words) in o)
                if (words is JsonArray a) map[tech.ToLowerInvariant()] = a.Select(w => w!.GetValue<string>().ToLowerInvariant()).ToArray();
        Map = map;
    }

    public IEnumerable<string> Find(string text)
    {
        var lower = " " + Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}\p{N}.+#-]+", " ") + " ";
        foreach (var (tech, words) in Map)
            if (words.Any(w => lower.Contains(" " + w + " ", StringComparison.Ordinal))) yield return tech;
    }

    /// <summary>A name ("PHP", "Node.js") as a tech key when a keyword says so; else the name itself, lowercased.</summary>
    public string Normalise(string name)
    {
        var n = name.Trim().ToLowerInvariant();
        return Map.FirstOrDefault(kv => kv.Key == n || kv.Value.Contains(n)).Key ?? n;
    }
}

public sealed record SaveResult(int Added, int Updated, int Unchanged, List<int> Touched);

public sealed partial class WireStore(AppDbContext db, TechKeywords keywords)
{
    public const double ClusterJaccard = 0.5;
    public static readonly TimeSpan ClusterWindow = TimeSpan.FromHours(72);

    public async Task<SaveResult> SaveAsync(WireSource source, IReadOnlyList<ParsedEntry> parsed, DateTime now, CancellationToken ct)
    {
        int added = 0, updated = 0, unchanged = 0;
        var touched = new List<TrWireEntry>();
        var ids = parsed.Select(p => p.ExternalId).Distinct().ToList();
        var existing = await db.TrWireEntries.Where(e => e.SourceId == source.Id && ids.Contains(e.ExternalId)).ToDictionaryAsync(e => e.ExternalId, ct);
        foreach (var p in parsed.GroupBy(p => p.ExternalId).Select(g => g.First()))
        {
            var hash = Hash(p.Title, p.Excerpt, p.State);
            if (existing.TryGetValue(p.ExternalId, out var e))
            {
                e.LastSeenAtUtc = now;
                if (e.ContentHash == hash) { unchanged++; continue; }
                db.TrWireEntryHistories.Add(new TrWireEntryHistory { EntryId = e.Id, AtUtc = e.UpdatedAtUtc, State = e.State, Title = e.Title, Excerpt = e.Excerpt });
                Fill(e, source, p, hash);
                e.UpdatedAtUtc = p.UpdatedAtUtc > e.UpdatedAtUtc ? p.UpdatedAtUtc : now;
                updated++;
                touched.Add(e);
            }
            else
            {
                e = new TrWireEntry
                {
                    SourceId = source.Id, ExternalId = p.ExternalId, FirstSeenAtUtc = now, LastSeenAtUtc = now,
                    PublishedAtUtc = p.PublishedAtUtc, UpdatedAtUtc = p.UpdatedAtUtc,
                    Visibility = source.Publish == "auto" ? WireVisibility.Public : WireVisibility.Inbox,
                };
                Fill(e, source, p, hash);
                db.TrWireEntries.Add(e);
                added++;
                touched.Add(e);
            }
        }
        await db.SaveChangesAsync(ct);
        var clusters = new HashSet<int>();
        foreach (var e in touched) clusters.Add(await ClusterAsync(e, now, ct));
        foreach (var c in clusters) await RecomputeConflictsAsync(c, ct);
        await db.SaveChangesAsync(ct);
        await SuggestAsync(source, touched, now, ct);
        return new SaveResult(added, updated, unchanged, touched.Select(t => t.Id).ToList());
    }

    private void Fill(TrWireEntry e, WireSource source, ParsedEntry p, string hash)
    {
        var text = $"{p.Title} {p.Excerpt}";
        e.Kind = p.Kind;
        e.Title = p.Title;
        e.Excerpt = p.Excerpt;
        e.Url = p.Url;
        e.State = p.State;
        e.Severity = p.Severity;
        e.ContentHash = hash;
        e.PublishedAtUtc = p.PublishedAtUtc < e.PublishedAtUtc || e.PublishedAtUtc == default ? p.PublishedAtUtc : e.PublishedAtUtc;
        e.ComponentsJson = Json(p.Components ?? Array.Empty<string>());
        e.CveIdsJson = Json((p.CveIds ?? Array.Empty<string>()).Concat(CveIds(text)).Select(c => c.ToUpperInvariant()).Distinct().OrderBy(c => c));
        e.TechsJson = Json(source.Tech.Concat((p.Techs ?? Array.Empty<string>()).Select(keywords.Normalise)).Concat(keywords.Find(text)).Distinct());
        e.VersionsJson = Json((p.Versions ?? Array.Empty<string>()).Concat(Versions(text)).Distinct().Take(12));
        e.UpdatesJson = JsonSerializer.Serialize((p.Updates ?? Array.Empty<WireUpdate>()).Select(u => new { at = TruthJson.Utc(u.AtUtc), state = u.State }));
        var facts = new JsonObject
        {
            ["fixed"] = new JsonArray((p.Fixed ?? Array.Empty<string>()).Concat(FixedIn(text)).Distinct().Select(v => (JsonNode)v!).ToArray()),
            ["patchTime"] = new JsonArray(PatchTimes(text).Distinct().Select(v => (JsonNode)v!).ToArray()),
        };
        if (p.Line is not null) facts["line"] = p.Line;
        e.FactsJson = facts.ToJsonString();
    }

    // ─────────────────────────── extraction ───────────────────────────

    [GeneratedRegex(@"\bCVE-\d{4}-\d{4,}\b", RegexOptions.IgnoreCase)]
    private static partial Regex Cve();

    [GeneratedRegex(@"(?<![\d.])v?(\d{1,3}\.\d{1,3}\.\d{1,4})(?![\d.])")]
    private static partial Regex Version3();

    [GeneratedRegex(@"\b(?:fixed|patched|resolved|addressed)\s+in\s+(?:version\s+)?v?(\d{1,3}\.\d{1,3}(?:\.\d{1,4})?)|\bupdate\s+to\s+(?:version\s+)?v?(\d{1,3}\.\d{1,3}(?:\.\d{1,4})?)", RegexOptions.IgnoreCase)]
    private static partial Regex Fixed();

    [GeneratedRegex(@"\b([01]?\d|2[0-3]):([0-5]\d)\s*UTC\b", RegexOptions.IgnoreCase)]
    private static partial Regex UtcTime();

    public static IEnumerable<string> CveIds(string text) => Cve().Matches(text).Select(m => m.Value);
    public static IEnumerable<string> Versions(string text) => Version3().Matches(text).Select(m => m.Groups[1].Value);
    public static IEnumerable<string> FixedIn(string text) =>
        Fixed().Matches(text).Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Select(v => v.TrimEnd('.'));
    public static IEnumerable<string> PatchTimes(string text) =>
        UtcTime().Matches(text).Select(m => $"{int.Parse(m.Groups[1].Value):00}:{m.Groups[2].Value} UTC");

    // ─────────────────────────── clustering ───────────────────────────

    /// <summary>Puts one entry in a cluster (§3.3) and returns its id. A status incident is its own story.</summary>
    private async Task<int> ClusterAsync(TrWireEntry e, DateTime now, CancellationToken ct)
    {
        var cves = TruthJson.Strings(e.CveIdsJson);
        int? target = null;
        string key;
        if (e.Kind == WireKind.Status) key = $"status:{e.SourceId}:{e.Id}";
        else if (cves.Count > 0)
        {
            key = $"cve:{cves[0]}";
            var sharing = new List<TrWireEntry>();
            foreach (var cve in cves)
            {
                var like = $"%\"{cve}\"%";
                sharing.AddRange(await db.TrWireEntries.Where(x => x.Id != e.Id && x.ClusterId != null && EF.Functions.Like(x.CveIdsJson, like)).ToListAsync(ct));
            }
            var clusters = sharing.Select(x => x.ClusterId!.Value).Distinct().OrderBy(x => x).ToList();
            if (clusters.Count > 0)
            {
                target = clusters[0];
                // Two stories turned out to be one: their entries move to the older cluster.
                foreach (var other in clusters.Skip(1))
                    await db.TrWireEntries.Where(x => x.ClusterId == other).ExecuteUpdateAsync(s => s.SetProperty(x => x.ClusterId, target), ct);
            }
        }
        else
        {
            key = $"story:{e.SourceId}:{e.Id}";
            var techs = TruthJson.Strings(e.TechsJson);
            var tokens = Tokens(e.Title);
            if (techs.Count > 0 && tokens.Count > 0)
            {
                var from = e.PublishedAtUtc - ClusterWindow;
                var to = e.PublishedAtUtc + ClusterWindow;
                var near = await db.TrWireEntries.Where(x => x.Id != e.Id && x.ClusterId != null && x.Kind != WireKind.Status
                    && x.PublishedAtUtc >= from && x.PublishedAtUtc <= to).ToListAsync(ct);
                target = near
                    .Where(x => TruthJson.Strings(x.TechsJson).Intersect(techs).Any())
                    .Select(x => (x.ClusterId, Score: Jaccard(tokens, Tokens(x.Title))))
                    .Where(x => x.Score >= ClusterJaccard)
                    .OrderByDescending(x => x.Score).Select(x => x.ClusterId).FirstOrDefault();
            }
        }
        if (target is null && e.ClusterId is { } own) target = own;
        if (target is null)
        {
            var c = new TrWireCluster { Key = key, CreatedAtUtc = now, UpdatedAtUtc = now };
            db.TrWireClusters.Add(c);
            await db.SaveChangesAsync(ct);
            target = c.Id;
        }
        e.ClusterId = target;
        var cluster = await db.TrWireClusters.FindAsync(new object[] { target.Value }, ct);
        if (cluster is not null) cluster.UpdatedAtUtc = now;
        await db.SaveChangesAsync(ct);
        return target.Value;
    }

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
        { "the", "a", "an", "and", "or", "of", "in", "on", "for", "to", "with", "by", "is", "are", "at", "from", "new", "via" };

    public static HashSet<string> Tokens(string title) =>
        Regex.Split(title.ToLowerInvariant(), @"[^\p{L}\p{N}.]+").Select(t => t.Trim('.')).Where(t => t.Length > 1 && !Stop.Contains(t)).ToHashSet();

    public static double Jaccard(HashSet<string> a, HashSet<string> b) =>
        a.Count == 0 || b.Count == 0 ? 0 : (double)a.Intersect(b).Count() / a.Union(b).Count();

    /// <summary>
    /// "Sources differ" (§3.3): for each version line, the fixed versions the cluster's sources give,
    /// and the patch times — any field with more than one value across different sources.
    /// </summary>
    public async Task RecomputeConflictsAsync(int clusterId, CancellationToken ct)
    {
        var cluster = await db.TrWireClusters.FindAsync(new object[] { clusterId }, ct);
        if (cluster is null) return;
        var entries = await db.TrWireEntries.Where(e => e.ClusterId == clusterId && e.Visibility != WireVisibility.Hidden).ToListAsync(ct);
        var conflicts = new JsonArray();
        if (entries.Select(e => e.SourceId).Distinct().Count() > 1)
        {
            var facts = entries.Select(e => (e.SourceId, Facts: JsonNode.Parse(e.FactsJson) as JsonObject ?? new JsonObject())).ToList();
            var fixedByLine = facts
                .SelectMany(f => (f.Facts["fixed"] as JsonArray ?? new JsonArray()).Select(v => (f.SourceId, Value: v!.GetValue<string>())))
                .GroupBy(x => string.Join('.', x.Value.Split('.').Take(2)));
            foreach (var line in fixedByLine)
                AddConflict(conflicts, "fixed", line.Key, line.ToList());
            AddConflict(conflicts, "patchTime", null,
                facts.SelectMany(f => (f.Facts["patchTime"] as JsonArray ?? new JsonArray()).Select(v => (f.SourceId, Value: v!.GetValue<string>()))).ToList());
        }
        cluster.ConflictsJson = conflicts.ToJsonString();
    }

    private static void AddConflict(JsonArray into, string field, string? line, List<(string SourceId, string Value)> values)
    {
        var byValue = values.GroupBy(v => v.Value).ToList();
        if (byValue.Count < 2 || values.Select(v => v.SourceId).Distinct().Count() < 2) return;
        into.Add(new JsonObject
        {
            ["field"] = field, ["line"] = line,
            ["values"] = new JsonArray(byValue.OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => (JsonNode)new JsonObject
            {
                ["value"] = g.Key, ["sources"] = new JsonArray(g.Select(x => (JsonNode)x.SourceId!).Distinct().ToArray()),
            }).ToArray()),
        });
    }

    // ─────────────────────────── suggestions (§3.6) ───────────────────────────

    private async Task SuggestAsync(WireSource source, List<TrWireEntry> touched, DateTime now, CancellationToken ct)
    {
        if (touched.Count == 0) return;
        var techs = await db.TrTechs.ToListAsync(ct);
        var tracked = techs.SelectMany(t => new[] { t.Slug.ToLowerInvariant(), keywords.Normalise(t.Name) }).ToHashSet();
        foreach (var e in touched)
        {
            var entryTechs = TruthJson.Strings(e.TechsJson);
            if (source.Adapter == "cisa-kev-json" && entryTechs.FirstOrDefault(tracked.Contains) is { } kevTech)
                await AddSuggestionAsync("urgent", $"kev:{e.ExternalId}", e.ClusterId, kevTech,
                    $"New CISA KEV entry for {kevTech}: {e.ExternalId}.", new JsonObject { ["entryId"] = e.Id }, now, ct);
            if (e.Kind == WireKind.Status && Regex.IsMatch($"{e.Title} {e.Excerpt}", @"\bmajor outage\b", RegexOptions.IgnoreCase))
                await AddSuggestionAsync("urgent", $"outage:{e.SourceId}:{e.ExternalId}", e.ClusterId, source.Tech.FirstOrDefault(),
                    $"{source.Name} reports a major outage: {e.Title}", new JsonObject { ["entryId"] = e.Id }, now, ct);
            if (source.Adapter == "endoflife-json" && JsonNode.Parse(e.FactsJson)?["line"]?.GetValue<string>() is { } line
                && source.Tech.FirstOrDefault() is { } slug && techs.FirstOrDefault(t => t.Slug == slug) is { } tech
                && !TruthJson.Strings(tech.VersionLinesJson).Contains(line))
                await AddSuggestionAsync("versionLine", $"line:{slug}:{line}", e.ClusterId, slug,
                    $"{tech.Name} {line} is a maintained version line (latest {TruthJson.Strings(e.VersionsJson).FirstOrDefault()}); the version checker does not know it.",
                    new JsonObject { ["line"] = line, ["version"] = TruthJson.Strings(e.VersionsJson).FirstOrDefault(), ["entryId"] = e.Id }, now, ct);
        }
        // A story three sources tell within 24 hours.
        foreach (var clusterId in touched.Where(t => t.Kind != WireKind.Status && t.ClusterId is not null).Select(t => t.ClusterId!.Value).Distinct())
        {
            var since = now.AddHours(-24);
            var sources = await db.TrWireEntries.Where(x => x.ClusterId == clusterId && x.Visibility != WireVisibility.Hidden && x.FirstSeenAtUtc >= since)
                .Select(x => x.SourceId).Distinct().CountAsync(ct);
            if (sources >= 3)
                await AddSuggestionAsync("urgent", $"cluster3:{clusterId}", clusterId, null, $"{sources} sources on one story within 24 hours.",
                    new JsonObject { ["sources"] = sources }, now, ct);
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task AddSuggestionAsync(string kind, string key, int? clusterId, string? tech, string reason, JsonObject payload, DateTime now, CancellationToken ct)
    {
        if (await db.TrWireSuggestions.AnyAsync(s => s.Key == key, ct) || db.TrWireSuggestions.Local.Any(s => s.Key == key)) return;
        db.TrWireSuggestions.Add(new TrWireSuggestion
        {
            Kind = kind, Key = key, ClusterId = clusterId, TechSlug = tech, Reason = reason.Length > 300 ? reason[..300] : reason,
            PayloadJson = payload.ToJsonString(), CreatedAtUtc = now,
        });
    }

    private static string Hash(string title, string excerpt, string? state) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{title}\n{excerpt}\n{state}")))[..32];

    private static string Json(IEnumerable<string> values) => JsonSerializer.Serialize(values.ToList());
}
