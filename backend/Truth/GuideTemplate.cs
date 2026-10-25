// সত্য মন্ত্রণালয় — the fix guides' templated commands (docs/TRUTH_PROMPT.md §6.2).
//
// The same renderer as frontend/src/truth/lib/template.js, case for case
// (scripts/truth/template-cases.json): {{name}}, {{#if cond}} … {{else}} … {{/if}}
// (nestable), conditions `name`, `!name`, `name OP value` joined by && and ||,
// versions compared as versions. A line holding only an if/else/if tag goes
// whole. No eval; plain text in, plain text out. The server uses it for the
// variant grid, the safety check on every variant, and the share pages.

using System.Text;
using System.Text.RegularExpressions;

namespace RabbitIC.Api.Truth;

public sealed record RenderResult(string Text, List<string> Errors);

public static partial class GuideTemplate
{
    [GeneratedRegex(@"\{\{\s*(#if\s+[^}]*?|else|/if|[a-zA-Z][\w-]*)\s*\}\}")]
    private static partial Regex Tag();

    [GeneratedRegex(@"^[ \t]*\{\{\s*(#if\s+[^}]*?|else|/if)\s*\}\}[ \t]*$")]
    private static partial Regex Standalone();

    [GeneratedRegex(@"^\s*(!)?\s*([a-zA-Z][\w-]*)\s*(?:(==|!=|>=|<=|>|<)\s*(""[^""]*""|[^\s""]+))?\s*$")]
    private static partial Regex Atom();

    [GeneratedRegex(@"^\d+(\.\d+)*$")]
    private static partial Regex Version();

    public static bool IsVersion(string s) => Version().IsMatch(s);

    public static int CompareVersions(string a, string b)
    {
        var x = a.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        var y = b.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var d = (i < x.Length ? x[i] : 0) - (i < y.Length ? y[i] : 0);
            if (d != 0) return d < 0 ? -1 : 1;
        }
        return 0;
    }

    private static bool Truthy(string? v) =>
        !string.IsNullOrEmpty(v) && !new[] { "false", "0", "no" }.Contains(v.ToLowerInvariant());

    public static bool EvalCondition(string cond, IReadOnlyDictionary<string, string> values, List<string>? errors = null)
    {
        errors ??= new List<string>();
        return cond.Split("||").Any(or => or.Split("&&").All(part =>
        {
            var m = Atom().Match(part);
            if (!m.Success) { errors.Add($"bad condition \"{part.Trim()}\""); return false; }
            var not = m.Groups[1].Success;
            var name = m.Groups[2].Value;
            if (!values.TryGetValue(name, out var v)) { errors.Add($"unknown variable \"{name}\""); return false; }
            if (!m.Groups[3].Success) return not ? !Truthy(v) : Truthy(v);
            if (not) { errors.Add($"\"!\" with a comparison in \"{part.Trim()}\""); return false; }
            var op = m.Groups[3].Value;
            var raw = m.Groups[4].Value;
            var w = raw.StartsWith('"') ? raw[1..^1] : raw;
            if (IsVersion(v) && IsVersion(w))
            {
                var c = CompareVersions(v, w);
                return op switch { "==" => c == 0, "!=" => c != 0, ">=" => c >= 0, "<=" => c <= 0, ">" => c > 0, _ => c < 0 };
            }
            if (op == "==") return v == w;
            if (op == "!=") return v != w;
            errors.Add($"\"{op}\" needs two versions in \"{part.Trim()}\"");
            return false;
        }));
    }

    private sealed class Frame
    {
        public bool On;
        public bool Parent;
        public bool Elsed;
    }

    public static RenderResult Render(string? template, IReadOnlyDictionary<string, string> values)
    {
        var errors = new List<string>();
        var tokens = new List<(string? Text, string? Tag)>();
        foreach (var line in Regex.Split(template ?? "", @"(?<=\n)"))
        {
            if (line.Length == 0) continue;
            var body = line.EndsWith("\r\n") ? line[..^2] : line.EndsWith('\n') ? line[..^1] : line;
            var standalone = Standalone().Match(body);
            if (standalone.Success) { tokens.Add((null, standalone.Groups[1].Value.Trim())); continue; }
            var last = 0;
            foreach (Match m in Tag().Matches(line))
            {
                if (m.Index > last) tokens.Add((line[last..m.Index], null));
                tokens.Add((null, m.Groups[1].Value.Trim()));
                last = m.Index + m.Length;
            }
            if (last < line.Length) tokens.Add((line[last..], null));
        }

        var sb = new StringBuilder();
        var stack = new List<Frame>();
        bool Printing() => stack.All(f => f.On);
        foreach (var (text, tag) in tokens)
        {
            if (text is not null) { if (Printing()) sb.Append(text); continue; }
            if (tag!.StartsWith("#if", StringComparison.Ordinal))
            {
                var parent = Printing();
                var on = parent && EvalCondition(tag[3..].Trim(), values, parent ? errors : new List<string>());
                stack.Add(new Frame { On = on, Parent = parent });
            }
            else if (tag == "else")
            {
                var top = stack.LastOrDefault();
                if (top is null || top.Elsed) { errors.Add("{{else}} without its {{#if}}"); continue; }
                top.Elsed = true;
                top.On = top.Parent && !top.On;
            }
            else if (tag == "/if")
            {
                if (stack.Count == 0) errors.Add("{{/if}} without its {{#if}}");
                else stack.RemoveAt(stack.Count - 1);
            }
            else if (Printing())
            {
                if (values.TryGetValue(tag, out var v)) sb.Append(v);
                else { errors.Add($"unknown variable \"{tag}\""); sb.Append("{{").Append(tag).Append("}}"); }
            }
        }
        if (stack.Count > 0) errors.Add($"{stack.Count} {{{{#if}}}} not closed");
        return new RenderResult(sb.ToString(), errors.Distinct().ToList());
    }
}
