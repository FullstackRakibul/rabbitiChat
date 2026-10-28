// The Records Department's source registry (docs/TRUTH_PROMPT_P5.md §2): which public feeds the
// collector reads, how often, how far it trusts them and whether their rows go straight to the
// wire or to the admin's inbox first. It lives in backend/config/truth-sources.json (committed);
// an admin's switch or edited definition is stored in TrWireSourceState and laid over it. Adding a
// source needs no code: only an adapter name from Adapters.

using System.Text.Json;
using System.Text.Json.Nodes;
using RabbitIC.Api.Models;

namespace RabbitIC.Api.Truth.Records;

public sealed record WireSource(
    string Id, string Name, string Kind, string Adapter, string Url, string? Homepage, IReadOnlyList<string> Tech,
    string Tier, int EveryMinutes, bool Enabled, string Publish, string Monogram, int Hue, int MaxKB, JsonObject Options,
    string Origin)
{
    public bool IsStatus => Kind == WireKind.Status;
}

public sealed class SourceRegistry
{
    public const int MinimumMinutes = 5;
    public const int DefaultMaxKB = 2048, MaximumMaxKB = 8192;

    public static readonly string[] Adapters =
        { "incidentio-rss", "statuspage-feed", "rss-atom", "cisa-kev-json", "osv-api", "github-advisories", "endoflife-json", "url-drop" };
    public static readonly string[] Tiers = { "official", "authority", "community", "news" };

    private readonly List<WireSource> fileSources = new();
    private readonly List<string> loadErrors = new();

    public SourceRegistry(IConfiguration config)
    {
        var path = config["Truth:Records:SourcesFile"];
        if (string.IsNullOrWhiteSpace(path)) path = Path.Combine(AppContext.BaseDirectory, "config", "truth-sources.json");
        File = path;
        if (!System.IO.File.Exists(path)) { loadErrors.Add($"{Path.GetFileName(path)} not found."); return; }
        try
        {
            var root = JsonNode.Parse(System.IO.File.ReadAllText(path))?["sources"] as JsonArray ?? new JsonArray();
            foreach (var node in root)
            {
                if (node is not JsonObject o) { loadErrors.Add("A source that is not an object was skipped."); continue; }
                var (source, error) = Parse(o, "file");
                if (source is null) { loadErrors.Add(error!); continue; }
                if (fileSources.Any(s => s.Id == source.Id)) { loadErrors.Add($"{source.Id}: the id is used twice; the second was skipped."); continue; }
                fileSources.Add(source);
            }
        }
        catch (JsonException e) { loadErrors.Add($"{Path.GetFileName(path)} is not valid JSON: {e.Message}"); }
    }

    public string File { get; }
    public IReadOnlyList<string> LoadErrors => loadErrors;

    /// <summary>The file's sources with every admin override and switch laid over them.</summary>
    public List<WireSource> Effective(IEnumerable<TrWireSourceState> states)
    {
        var byId = states.ToDictionary(s => s.SourceId);
        var list = new List<WireSource>();
        foreach (var f in fileSources)
            list.Add(Apply(f, byId.GetValueOrDefault(f.Id)));
        foreach (var s in byId.Values.Where(s => s.OverrideJson is not null && fileSources.All(f => f.Id != s.SourceId)))
            if (Overridden(s) is { } added) list.Add(Apply(added, s));
        return list;
    }

    private static WireSource Apply(WireSource source, TrWireSourceState? state)
    {
        var s = state is null ? source : Overridden(state) ?? source;
        return state?.Enabled is { } on ? s with { Enabled = on } : s;
    }

    private static WireSource? Overridden(TrWireSourceState state)
    {
        if (state.OverrideJson is null) return null;
        try { return JsonNode.Parse(state.OverrideJson) is JsonObject o ? Parse(o, "admin").Source : null; }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// One definition, checked (§2): the adapter is known, the URL is https, the interval is at least
    /// 5 minutes (a smaller one from the file is raised to 5; the admin's form refuses it), and
    /// publish defaults by kind and tier — news and community go to the inbox.
    /// </summary>
    public static (WireSource? Source, string? Error) Parse(JsonObject o, string origin)
    {
        string? Str(string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
        int? Int(string k) => o[k] is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;
        bool? Bool(string k) => o[k] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

        var id = Str("id");
        if (id is null || !System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-z0-9][a-z0-9-]{1,40}$"))
            return (null, $"{id ?? "(no id)"}: the id is 2–41 lowercase letters, digits and dashes.");
        var name = Str("name");
        if (name is null || name.Length > 60) return (null, $"{id}: a name of up to 60 characters is needed.");
        var kind = Str("kind");
        if (kind is null || !WireKind.All.Contains(kind)) return (null, $"{id}: kind is one of {string.Join(", ", WireKind.All)}.");
        var adapter = Str("adapter");
        if (adapter is null || !Adapters.Contains(adapter)) return (null, $"{id}: adapter is one of {string.Join(", ", Adapters)}.");
        var url = Str("url") ?? "";
        if (adapter != "url-drop")
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return (null, $"{id}: the url is not an absolute URL.");
            if (u.Scheme != Uri.UriSchemeHttps) return (null, $"{id}: only https URLs are fetched.");
        }
        var homepage = Str("homepage");
        if (homepage is not null && (!Uri.TryCreate(homepage, UriKind.Absolute, out var h) || h.Scheme != Uri.UriSchemeHttps))
            return (null, $"{id}: the homepage must be an https URL.");
        var tier = Str("tier") ?? "news";
        if (!Tiers.Contains(tier)) return (null, $"{id}: tier is one of {string.Join(", ", Tiers)}.");
        var tech = (o["tech"] as JsonArray ?? new JsonArray()).Select(t => t?.GetValue<string>()?.Trim().ToLowerInvariant())
            .Where(t => !string.IsNullOrEmpty(t)).Select(t => t!).Distinct().ToList();
        var every = Int("everyMinutes") ?? 30;
        if (every < MinimumMinutes)
        {
            if (origin == "admin") return (null, $"{id}: a source is fetched at most every {MinimumMinutes} minutes.");
            every = MinimumMinutes;
        }
        var publish = Str("publish") ?? DefaultPublish(kind, tier);
        if (publish is not ("auto" or "inbox")) return (null, $"{id}: publish is auto or inbox.");
        var maxKB = Int("maxKB") ?? DefaultMaxKB;
        if (maxKB is < 1 or > MaximumMaxKB) return (null, $"{id}: maxKB is 1–{MaximumMaxKB}.");
        var monogram = Str("monogram") ?? string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));
        if (monogram.Length is < 1 or > 2) monogram = name[..1].ToUpperInvariant();
        var hue = Int("hue") ?? Math.Abs(id.Aggregate(7, (a, c) => a * 31 + c)) % 360;
        return (new WireSource(id, name, kind, adapter, url, homepage, tech, tier, every, Bool("enabled") ?? true, publish,
            monogram, hue % 360, maxKB, o["options"] as JsonObject ?? new JsonObject(), origin), null);
    }

    /// <summary>§2: status, and advisories or releases from official or authority sources, go out on their own.</summary>
    public static string DefaultPublish(string kind, string tier) =>
        kind == WireKind.Status || (tier is "official" or "authority" && kind is WireKind.Advisory or WireKind.Release) ? "auto" : "inbox";

    public static JsonObject ToJson(WireSource s) => new()
    {
        ["id"] = s.Id, ["name"] = s.Name, ["kind"] = s.Kind, ["adapter"] = s.Adapter, ["url"] = s.Url, ["homepage"] = s.Homepage,
        ["tech"] = new JsonArray(s.Tech.Select(t => (JsonNode)t!).ToArray()), ["tier"] = s.Tier, ["everyMinutes"] = s.EveryMinutes,
        ["enabled"] = s.Enabled, ["publish"] = s.Publish, ["monogram"] = s.Monogram, ["hue"] = s.Hue, ["maxKB"] = s.MaxKB,
        ["options"] = s.Options.DeepClone(),
    };
}
