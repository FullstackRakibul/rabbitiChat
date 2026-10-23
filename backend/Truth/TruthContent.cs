// সত্য মন্ত্রণালয় — the editable content documents (docs/TRUTH_PROMPT.md §5, §3A.5, §3A.7):
//
//   site       the global site text (TruthText)
//   memes      the meme banners: decoration, never on safety information
//   glossary   the Newspeak Dictionary: plain one-line explanations of terms
//
// Each has a built-in default and a schema — the frontend's own files, embedded
// here (RabbitIC.Api.csproj) — and an admin override published as versions
// (TrSiteContent, by Kind). The rules a meme or an entry is held to are read
// from its schema (lengths, enums, the id pattern), and its strings go through
// the site text's plain-text check: no HTML, https links only, no braces.
// frontend/src/truth/lib/content-docs.js applies the same rules in the editor.

using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RabbitIC.Api.Truth;

public static class TruthContent
{
    public const string Site = "site";
    public const string Memes = "memes";
    public const string Glossary = "glossary";
    public static readonly string[] Kinds = { Site, Memes, Glossary };

    private static readonly Dictionary<string, Lazy<JsonObject>> Defaults = Kinds.ToDictionary(k => k, k => new Lazy<JsonObject>(() => Load($"Truth.{k}.default.json")));
    private static readonly Dictionary<string, Lazy<JsonObject>> Schemas = Kinds.ToDictionary(k => k, k => new Lazy<JsonObject>(() => Load($"Truth.{k}.schema.json")));

    public static bool IsKind(string? kind) => kind is not null && Kinds.Contains(kind);

    public static JsonObject Default(string kind) => (JsonObject)Defaults[kind].Value.DeepClone();

    public static JsonObject Schema(string kind) => (JsonObject)Schemas[kind].Value.DeepClone();

    public static int SchemaVersion(string kind) => Schemas[kind].Value["x-schemaVersion"]?.GetValue<int>() ?? 1;

    internal static JsonObject Load(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource {name} is missing (see RabbitIC.Api.csproj).");
        return JsonNode.Parse(stream)!.AsObject();
    }

    public static List<TextError> Validate(string kind, JsonNode? doc) => kind switch
    {
        Site => TruthText.Validate(doc),
        Memes => ValidateMemes(doc),
        Glossary => ValidateGlossary(doc),
        _ => new() { new("", "unknown content kind") },
    };

    private static List<TextError> ValidateMemes(JsonNode? doc)
    {
        var errors = new List<TextError>();
        var schema = Schemas[Memes].Value;
        var meme = schema["$defs"]!["meme"]!["properties"]!;
        var maxItems = schema["properties"]!["memes"]!["maxItems"]!.GetValue<int>();
        if (doc is not JsonObject root || root["memes"] is not JsonArray list) return new() { new("", "must be { \"memes\": [ … ] }") };
        foreach (var (k, _) in root) if (k != "memes") errors.Add(new(k, "unknown key"));
        if (list.Count > maxItems) errors.Add(new("memes", $"at most {maxItems} memes"));

        var allowedKeys = meme.AsObject().Select(p => p.Key).ToHashSet();
        var required = schema["$defs"]!["meme"]!["required"]!.AsArray().Select(r => r!.GetValue<string>()).ToList();
        var ids = new HashSet<string>();
        for (var i = 0; i < list.Count; i++)
        {
            var at = $"memes[{i}]";
            if (list[i] is not JsonObject m) { errors.Add(new(at, "must be an object")); continue; }
            foreach (var (k, _) in m) if (!allowedKeys.Contains(k)) errors.Add(new($"{at}.{k}", "unknown key"));
            foreach (var k in required) if (!m.ContainsKey(k)) errors.Add(new($"{at}.{k}", "missing key"));

            var id = Str(m["id"]);
            if (id is null || !Regex.IsMatch(id, meme["id"]!["pattern"]!.GetValue<string>())) errors.Add(new($"{at}.id", "lower-case letters, digits and dashes"));
            else if (!ids.Add(id)) errors.Add(new($"{at}.id", $"\"{id}\" is used twice"));
            foreach (var lang in new[] { "bn", "en" })
            {
                if (!m.ContainsKey(lang)) continue;
                var max = meme[lang]!["maxLength"]!.GetValue<int>();
                var lines = meme[lang]!["x-maxLines"]!.GetValue<int>();
                foreach (var msg in TruthText.CheckString(m[lang], Array.Empty<string>(), max, required: true)) errors.Add(new($"{at}.{lang}", msg.Replace("Bangla text is required", "text is required")));
                if (Str(m[lang]) is { } s && s.Split('\n').Length > lines) errors.Add(new($"{at}.{lang}", $"at most {lines} lines"));
            }
            Enum(m["mood"], meme["mood"]!, $"{at}.mood", errors, m.ContainsKey("mood"));
            Enum(m["style"], meme["style"]!, $"{at}.style", errors, m.ContainsKey("style"));
            if (m.ContainsKey("pose")) Enum(m["pose"], meme["pose"]!, $"{at}.pose", errors, true);
            if (m.ContainsKey("placement"))
            {
                var places = meme["placement"]!["items"]!["enum"]!.AsArray().Select(p => p!.GetValue<string>()).ToHashSet();
                if (m["placement"] is not JsonArray pl || pl.Count == 0) errors.Add(new($"{at}.placement", "at least one placement"));
                else foreach (var p in pl)
                    if (Str(p) is not { } ps || !places.Contains(ps)) errors.Add(new($"{at}.placement", $"unknown placement \"{p}\"; one of: {string.Join(", ", places)}"));
                    else if (pl.Count(x => Str(x) == ps) > 1) errors.Add(new($"{at}.placement", $"\"{ps}\" twice"));
            }
            if (m.ContainsKey("weight") && !(m["weight"] is JsonValue w && w.TryGetValue<int>(out var wv) && wv >= 1 && wv <= 10)) errors.Add(new($"{at}.weight", "a whole number 1–10"));
            if (m.ContainsKey("active") && !(m["active"] is JsonValue a && a.TryGetValue<bool>(out _))) errors.Add(new($"{at}.active", "true or false"));
        }
        return errors.DistinctBy(e => (e.Path, e.Message)).ToList();
    }

    private static List<TextError> ValidateGlossary(JsonNode? doc)
    {
        var errors = new List<TextError>();
        var schema = Schemas[Glossary].Value;
        var entry = schema["$defs"]!["entry"]!["properties"]!;
        var maxItems = schema["properties"]!["entries"]!["maxItems"]!.GetValue<int>();
        if (doc is not JsonObject root || root["entries"] is not JsonArray list) return new() { new("", "must be { \"entries\": [ … ] }") };
        foreach (var (k, _) in root) if (k != "entries") errors.Add(new(k, "unknown key"));
        if (list.Count > maxItems) errors.Add(new("entries", $"at most {maxItems} entries"));
        var ids = new HashSet<string>();
        for (var i = 0; i < list.Count; i++)
        {
            var at = $"entries[{i}]";
            if (list[i] is not JsonObject e) { errors.Add(new(at, "must be an object")); continue; }
            foreach (var (k, _) in e) if (entry[k] is null) errors.Add(new($"{at}.{k}", "unknown key"));
            var id = Str(e["id"]);
            if (id is null || !Regex.IsMatch(id, entry["id"]!["pattern"]!.GetValue<string>())) errors.Add(new($"{at}.id", "lower-case letters, digits and dashes"));
            else if (!ids.Add(id)) errors.Add(new($"{at}.id", $"\"{id}\" is used twice"));
            foreach (var part in new[] { "term", "def" })
            {
                var max = entry[part]!["x-maxLength"]!.GetValue<int>();
                if (e[part] is not JsonObject pair) { errors.Add(new($"{at}.{part}", "needs bn and en")); continue; }
                foreach (var (k, _) in pair) if (k is not ("bn" or "en")) errors.Add(new($"{at}.{part}.{k}", "unknown key"));
                foreach (var lang in new[] { "bn", "en" })
                foreach (var msg in TruthText.CheckString(pair[lang], Array.Empty<string>(), max, required: true))
                    errors.Add(new($"{at}.{part}.{lang}", msg.Replace("Bangla text is required", "text is required")));
            }
            var items = entry["match"]!;
            if (e["match"] is not JsonArray match || match.Count < items["minItems"]!.GetValue<int>() || match.Count > items["maxItems"]!.GetValue<int>())
                errors.Add(new($"{at}.match", $"{items["minItems"]}–{items["maxItems"]} words to underline"));
            else foreach (var w in match)
                if (Str(w) is not { } ws || ws.Trim().Length < 2 || ws.Length > 30) errors.Add(new($"{at}.match", "each word 2–30 characters"));
        }
        return errors.DistinctBy(e => (e.Path, e.Message)).ToList();
    }

    private static void Enum(JsonNode? value, JsonNode schema, string path, List<TextError> errors, bool present)
    {
        if (!present) return;
        var allowed = schema["enum"]!.AsArray().Select(v => v?.GetValue<string>()).ToList();
        var s = value is null ? null : Str(value);
        if (value is not null && s is null || !allowed.Contains(s))
            errors.Add(new(path, $"one of: {string.Join(", ", allowed.Select(a => a ?? "null"))}"));
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
