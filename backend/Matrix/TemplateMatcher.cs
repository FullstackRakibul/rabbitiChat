// ম্যাট্রিক্স — templates and mapping suggestions (docs/MATRIX_PROMPT.md §3.3, §3.5).
//
// A layout seen before (same sheet name and header texts → same fingerprint)
// takes its saved template's mapping, and imports with no questions. Otherwise
// synonyms suggest first; only the headers left over go to the model, with five
// sample values MASKED (letters → x, digits → 9) and the catalog list, in a
// schema-forced JSON answer with one repair round. A model's suggestion is never
// trusted: it waits for a person to confirm it.

using System.Text.Json;
using RabbitIC.Api.DTOs.Matrix;
using RabbitIC.Api.Models;
using RabbitIC.Api.ModelSources;
using RabbitIC.Api.Rbac;

namespace RabbitIC.Api.Matrix;

public sealed record MxTemplateColumn(int Index, string Header, string? Field, string Sensitivity);

public sealed class TemplateMatcher(IHttpClientFactory http, MatrixModel models, ILogger<TemplateMatcher> log)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The latest version of each fingerprint's template, applied to the tables that match it.</summary>
    public static void ApplyTemplates(MxProfile profile, IReadOnlyList<MxTemplate> templates)
    {
        var latest = templates.GroupBy(t => t.Fingerprint).ToDictionary(g => g.Key, g => g.OrderByDescending(t => t.Version).First());
        foreach (var table in profile.Sheets.SelectMany(s => s.Tables))
        {
            if (!latest.TryGetValue(table.Fingerprint, out var t)) continue;
            table.Template = new MxTemplateRef(t.Id, t.Name, t.Version);
            table.Kind = t.ReportKind;
            var mapping = Mapping(t);
            foreach (var col in table.Columns)
            {
                var m = mapping.FirstOrDefault(x => x.Index == col.Index);
                if (m is null || col.Sensitivity == MxSensitivity.Sensitive) continue;
                col.Sensitivity = m.Sensitivity;
                // "keep": imported unmapped, as classified when the template was saved.
                col.Suggestion = m.Field == "keep" ? null : new MxSuggestion { Field = m.Field, Source = "template", Confidence = 1, Confirmed = true };
            }
        }
    }

    public static List<MxTemplateColumn> Mapping(MxTemplate t) =>
        JsonSerializer.Deserialize<List<MxTemplateColumn>>(t.MappingJson, Json) ?? new();

    /// <summary>"Ab-123 x" → "Xx-999 x": the shape of a value, none of its content.</summary>
    public static string Mask(string value)
    {
        var s = value.Length > 24 ? value[..24] : value;
        return new string(s.Select(ch => char.IsDigit(ch) ? '9' : char.IsUpper(ch) ? 'X' : char.IsLetter(ch) ? 'x' : ch).ToArray());
    }


    private static readonly object Schema = new
    {
        type = "object",
        properties = new
        {
            suggestions = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        header = new { type = "string" },
                        field = new { type = "string", @enum = FieldCatalog.All.Where(f => !f.Derived).Select(f => f.Key).Append("none").ToArray() },
                        confidence = new { type = "number" },
                    },
                    required = new[] { "header", "field", "confidence" },
                },
            },
        },
        required = new[] { "suggestions" },
    };

    private sealed record ModelSuggestion(string? Header, string? Field, double Confidence);
    private sealed record ModelAnswer(List<ModelSuggestion>? Suggestions);

    /// <summary>
    /// Asks the model about the columns no synonym or template matched. Returns a
    /// note when it could not (no model, refused by the policy, unreachable), so
    /// the profile can say why those columns have no suggestion.
    /// </summary>
    public async Task<string?> SuggestAsync(User user, List<MxTableScan> scans, CancellationToken ct)
    {
        var open = scans
            .Where(s => s.Profile.Template is null)
            .SelectMany(s => s.Profile.Columns.Where(c => c.Suggestion is null && c.Sensitivity != MxSensitivity.Sensitive && c.Header.Length > 0)
                .Select(c => (Scan: s, Col: c)))
            .ToList();
        if (open.Count == 0) return null;

        var asked = open
            .GroupBy(x => x.Col.Header.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                header = g.Key,
                samples = g.SelectMany(x => x.Scan.Samples.GetValueOrDefault(x.Col.Index) ?? new()).Take(5).Select(Mask).ToList(),
            })
            .Take(60)
            .ToList();

        var (model, refused) = await models.ResolveAsync(user, null, ct);
        if (model is null) return refused == "No model is configured for Matrix." ? "No model is configured for Matrix, so unmatched columns have no suggestion." : refused;

        var catalog = string.Join("\n", FieldCatalog.All.Where(f => !f.Derived).Select(f => $"{f.Key} — {f.En} ({f.Group})"));
        var messages = new List<SchemaJsonMessage>
        {
            new("system", "You map column headers from a garment factory's HR and attendance spreadsheets to a fixed field catalog. "
                + "Sample values are masked: x is any letter, 9 any digit. Answer with JSON only. Use \"none\" when no field fits."),
            new("user", $"Field catalog:\n{catalog}\n\nColumns:\n{JsonSerializer.Serialize(asked, Json)}"),
        };

        var numCtx = models.NumCtx;
        var first = await SchemaJsonChat.CallAsync(http, model, Schema, messages, numCtx, ct);
        var (answer, problems) = Read(first.Content, asked.Select(a => a.header).ToHashSet(StringComparer.OrdinalIgnoreCase));
        if (problems.Count > 0 && first.Content is not null)
        {
            // One repair round, with what was wrong.
            messages.Add(new("assistant", first.Content));
            messages.Add(new("user", "These answers were invalid: " + string.Join("; ", problems) + ". Answer again for every column, using only keys from the list or \"none\"."));
            var second = await SchemaJsonChat.CallAsync(http, model, Schema, messages, numCtx, ct);
            var (repaired, still) = Read(second.Content, asked.Select(a => a.header).ToHashSet(StringComparer.OrdinalIgnoreCase));
            if (still.Count < problems.Count) answer = repaired;
        }
        if (answer is null)
        {
            log.LogWarning("Matrix: the model gave no usable mapping suggestions ({Error}).", first.Error);
            return first.Error ?? "The model's answer could not be read.";
        }

        foreach (var (scan, col) in open)
        {
            var s = answer.FirstOrDefault(a => string.Equals(a.Header?.Trim(), col.Header.Trim(), StringComparison.OrdinalIgnoreCase));
            var field = FieldCatalog.Find(s?.Field);
            if (s is null || field is null || field.Derived) continue;
            // A field another column of the table already has stays with that column.
            if (scan.Profile.Columns.Any(c => c != col && c.Suggestion?.Field == field.Key)) continue;
            col.Suggestion = new MxSuggestion { Field = field.Key, Source = "model", Confidence = Math.Clamp(s.Confidence, 0, 1), Confirmed = false };
        }
        return null;
    }

    private static (List<ModelSuggestion>? Answer, List<string> Problems) Read(string? content, HashSet<string> asked)
    {
        var problems = new List<string>();
        if (content is null) return (null, new List<string> { "no answer" });
        ModelAnswer? parsed;
        try { parsed = JsonSerializer.Deserialize<ModelAnswer>(content, Json); }
        catch (JsonException) { return (null, new List<string> { "not JSON of the requested shape" }); }
        var list = parsed?.Suggestions ?? new();
        foreach (var s in list)
        {
            if (s.Header is null || !asked.Contains(s.Header.Trim())) problems.Add($"\"{s.Header}\" was not asked about");
            if (s.Field != "none" && FieldCatalog.Find(s.Field) is null) problems.Add($"\"{s.Field}\" is not a catalog key");
        }
        foreach (var h in asked.Where(h => !list.Any(s => string.Equals(s.Header?.Trim(), h, StringComparison.OrdinalIgnoreCase))))
            problems.Add($"\"{h}\" has no answer");
        return (list, problems);
    }
}
