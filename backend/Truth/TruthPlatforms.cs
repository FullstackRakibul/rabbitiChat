// সত্য মন্ত্রণালয় — the platforms fix guides are written for (docs/TRUTH_PROMPT.md §6.5)
// and the ini directives the safety check knows (§6.4).
//
// Both are data, not code: frontend/src/truth/content/platforms.json and
// directives.json, embedded at build — or, when Truth:PlatformsFile or
// Truth:DirectivesFile names a file, that file, read at start. Adding a platform
// is a line of JSON (gate G2.2 adds one that way).

using System.Text.Json.Nodes;

namespace RabbitIC.Api.Truth;

public sealed record PlatformDef(string Key, string Group, string NameBn, string NameEn, string Shell, string? HintBn, string? HintEn);

public sealed class TruthPlatforms
{
    public IReadOnlyList<PlatformDef> All { get; }
    public JsonArray Groups { get; }
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, SafetyCheck.Directive>> Directives { get; }

    public TruthPlatforms(IConfiguration config)
    {
        var platforms = Read(config["Truth:PlatformsFile"], "Truth.platforms.json");
        Groups = platforms["groups"]?.AsArray() ?? new JsonArray();
        All = (platforms["platforms"]?.AsArray() ?? new JsonArray()).Select(p => new PlatformDef(
            p!["key"]!.GetValue<string>(), p["group"]?.GetValue<string>() ?? "", p["bn"]!.GetValue<string>(), p["en"]!.GetValue<string>(),
            p["shell"]?.GetValue<string>() ?? "bash", p["hintBn"]?.GetValue<string>(), p["hintEn"]?.GetValue<string>())).ToList();
        Directives = SafetyCheck.LoadDirectives(Read(config["Truth:DirectivesFile"], "Truth.directives.json"));
    }

    private static JsonObject Read(string? file, string resource) =>
        !string.IsNullOrWhiteSpace(file) && File.Exists(file)
            ? JsonNode.Parse(File.ReadAllText(file))!.AsObject()
            : TruthContent.Load(resource);

    public PlatformDef? Find(string key) => All.FirstOrDefault(p => p.Key == key);
}
