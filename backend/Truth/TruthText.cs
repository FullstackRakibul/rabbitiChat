// সত্য মন্ত্রণালয় — the global site text (docs/TRUTH_PROMPT.md §5).
//
// The built-in default and its schema are the frontend's own files
// (frontend/src/truth/content/), embedded in this assembly at build time, so the
// public shell, the share pages and the emails read one text. These are the
// same rules as frontend/src/truth/lib/site-text.js: plain text with {name}
// placeholders, no HTML, https links only, Bangla required, English may be empty
// (it then falls back to the default). A change to one is a change to both.

using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RabbitIC.Api.Truth;

public sealed record TextError(string Path, string Message);

public static partial class TruthText
{
    [GeneratedRegex(@"\{([a-zA-Z][a-zA-Z0-9]*)\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"<\s*[a-zA-Z!/?]|&(#[0-9]+|#x[0-9a-fA-F]+|[a-zA-Z][a-zA-Z0-9]*);")]
    private static partial Regex Html();

    [GeneratedRegex(@"\b([a-zA-Z][a-zA-Z0-9+.-]*)://")]
    private static partial Regex Scheme();

    [GeneratedRegex(@"\b(javascript|data|vbscript):", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptScheme();

    [GeneratedRegex(@"[\u0000-\u0009\u000B-\u001F\u007F]")]
    private static partial Regex Control();

    private static readonly Lazy<JsonObject> DefaultDoc = new(() => Load("Truth.site.default.json"));
    private static readonly Lazy<JsonObject> SchemaDoc = new(() => Load("Truth.site.schema.json"));

    /// <summary>The built-in text, both languages. A fresh copy: callers may change it.</summary>
    public static JsonObject Default => (JsonObject)DefaultDoc.Value.DeepClone();

    public static JsonObject Schema => (JsonObject)SchemaDoc.Value.DeepClone();

    public static int SchemaVersion => SchemaDoc.Value["x-schemaVersion"]?.GetValue<int>() ?? 1;

    private static JsonObject Load(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource {name} is missing (see RabbitIC.Api.csproj).");
        return JsonNode.Parse(stream)!.AsObject();
    }

    /// <summary>The default with the override's non-empty strings over it — only keys the default has, only strings.</summary>
    public static JsonObject Merge(JsonObject? overrideDoc) => (JsonObject)MergeNode(DefaultDoc.Value, overrideDoc);

    private static JsonNode MergeNode(JsonNode baseNode, JsonNode? over)
    {
        if (baseNode is not JsonObject obj)
        {
            var text = over is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : baseNode.GetValue<string>();
            return JsonValue.Create(text)!;
        }
        var result = new JsonObject();
        foreach (var (key, child) in obj)
            result[key] = MergeNode(child!, over is JsonObject o ? o[key] : null);
        return result;
    }

    /// <summary>One key of the merged text in a language, formatted: <c>Get(text, "en", "item.patchAt", ("time", "…"))</c>.</summary>
    public static string Get(JsonObject merged, string lang, string key, params (string Name, string Value)[] args)
    {
        JsonNode? node = merged[lang == "en" ? "en" : "bn"];
        foreach (var part in key.Split('.')) node = node?[part];
        var text = node is JsonValue v && v.TryGetValue<string>(out var s) ? s : key;
        return Format(text, args.ToDictionary(a => a.Name, a => a.Value));
    }

    public static string Format(string text, IReadOnlyDictionary<string, string> args) =>
        Placeholder().Replace(text, m => args.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

    /// <summary>The problems of one string. Mirrors checkString in site-text.js.</summary>
    public static List<string> CheckString(JsonNode? value, IReadOnlyCollection<string> placeholders, int maxLength, bool required)
    {
        if (value is not JsonValue jv || !jv.TryGetValue<string>(out var s)) return new() { "must be text" };
        var errors = new List<string>();
        if (required && string.IsNullOrWhiteSpace(s)) errors.Add("Bangla text is required");
        if (s.Length > maxLength) errors.Add($"longer than {maxLength} characters");
        if (Html().IsMatch(s)) errors.Add("HTML is not allowed");
        if (Control().IsMatch(s)) errors.Add("control characters are not allowed");
        if (ScriptScheme().IsMatch(s) || Scheme().Matches(s).Any(m => !m.Groups[1].Value.Equals("https", StringComparison.OrdinalIgnoreCase)))
            errors.Add("only https links are allowed");
        foreach (Match m in Placeholder().Matches(s))
        {
            if (placeholders.Contains(m.Groups[1].Value)) continue;
            errors.Add($"unknown placeholder {{{m.Groups[1].Value}}}" + (placeholders.Count > 0
                ? "; allowed: " + string.Join(' ', placeholders.Select(p => "{" + p + "}"))
                : "; this text takes none"));
        }
        if (Placeholder().Replace(s, "").IndexOfAny(new[] { '{', '}' }) >= 0) errors.Add("a { or } that is not a placeholder");
        return errors.Distinct().ToList();
    }

    /// <summary>Validates a whole override document. Mirrors validateText in site-text.js.</summary>
    public static List<TextError> Validate(JsonNode? doc)
    {
        var errors = new List<TextError>();
        if (doc is not JsonObject root) return new() { new("", "must be an object with bn and en") };
        foreach (var (k, _) in root)
            if (k is not ("bn" or "en")) errors.Add(new(k, "unknown key"));
        var lang = SchemaDoc.Value["$defs"]!["lang"]!;
        foreach (var code in new[] { "bn", "en" })
        {
            if (!root.ContainsKey(code)) { errors.Add(new(code, "missing key")); continue; }
            Walk(lang, root[code], code, code == "bn", errors);
        }
        return errors;
    }

    private static void Walk(JsonNode schema, JsonNode? value, string path, bool required, List<TextError> errors)
    {
        if (schema["type"]?.GetValue<string>() == "object")
        {
            if (value is not JsonObject obj) { errors.Add(new(path, "must be an object")); return; }
            var props = schema["properties"]!.AsObject();
            foreach (var (k, _) in obj)
                if (!props.ContainsKey(k)) errors.Add(new($"{path}.{k}", "unknown key"));
            foreach (var (k, child) in props)
            {
                if (!obj.ContainsKey(k)) errors.Add(new($"{path}.{k}", "missing key"));
                else Walk(child!, obj[k], $"{path}.{k}", required, errors);
            }
            return;
        }
        var allowed = schema["x-placeholders"]?.AsArray().Select(p => p!.GetValue<string>()).ToList() ?? new List<string>();
        var max = schema["maxLength"]?.GetValue<int>() ?? 600;
        foreach (var message in CheckString(value, allowed, max, required)) errors.Add(new(path, message));
    }
}
