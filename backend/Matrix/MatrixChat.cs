// ম্যাট্রিক্স — Ask: plan, query, answer, cite (docs/MATRIX_PROMPT.md §3.9).
//
// 1. Plan. One schema-forced call (temperature 0, thinking off) returns one of:
//    answer from the report card · up to three MatrixQuery objects · a KPI's
//    definition · what is missing. Its system message — persona, the query language,
//    the fields of the datasets in scope, the KPIs — is the same for every question on
//    a dataset, so Ollama reuses its cache; the card and the question come after it.
// 2. Run. The server validates and runs the queries for the asker (their permissions);
//    an invalid query gets one repair round with the validator's errors. Rows are
//    labelled R1, R2 … — at most 50 go to the model — with their citation tokens.
// 3. Answer. Streamed; every number copied from the rows or the card, or written as
//    [[= … ]]; every claim cited [R1], [C3] or [KPI: name]. Then the exact number check
//    and the citations that name nothing ("unknown source").
//
// The model never sees a row's personal columns: a query may not group or measure a
// name, and rows are aggregates.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RabbitIC.Api.DTOs.Matrix;
using RabbitIC.Api.Models;

namespace RabbitIC.Api.Matrix;

/// <summary>The planner's answer: Kind is answer_from_card, queries, definition or need.</summary>
public sealed record MxPlan(string Kind, List<JsonElement> Queries, string? Definition, string? Need);

public static partial class MatrixChat
{
    public const string ConversationKind = "matrix";
    public const int MaxQueries = 3;
    public const int MaxRowsToModel = 50;
    public const string CouldNotPlan = "I couldn't form a query for that";

    public static readonly object PlanSchema = new
    {
        type = "object",
        properties = new
        {
            kind = new { type = "string", @enum = new[] { "answer_from_card", "queries", "definition", "need" } },
            queries = new
            {
                type = "array",
                maxItems = MaxQueries,
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        dataset = new { type = "string" },
                        join = new
                        {
                            type = "array",
                            items = new { type = "object", properties = new { dataset = new { type = "string" }, on = new { type = "string" } }, required = new[] { "dataset", "on" } },
                        },
                        filter = new { type = "array", items = new { type = "array" } },
                        group_by = new { type = "array", items = new { type = "string" } },
                        measures = new { type = "array", items = new { type = "array" } },
                        sort = new { type = "array", items = new { type = "array" } },
                        limit = new { type = "integer" },
                    },
                    required = new[] { "dataset", "measures" },
                },
            },
            definition = new { type = "string" },
            need = new { type = "string" },
        },
        required = new[] { "kind" },
    };

    // ───────────────────────── the plan prompt ─────────────────────────

    /// <summary>
    /// The planner's system message for a scope: the same text for every question on it.
    /// <paramref name="datasets"/>: the dataset in scope first, then the current datasets it can join.
    /// </summary>
    /// <param name="scope">The dataset a Report page's Ask is about; null for Ask across every current dataset.</param>
    public static string PlanSystem(MxDataset? scope, IReadOnlyList<(MxDataset Dataset, List<MxDsColumn> Columns)> datasets,
        IReadOnlyList<MxKpi> kpis, MxQueryContext ctx, IReadOnlyDictionary<string, List<string>>? samples = null,
        IReadOnlyList<MxCardUnavailable>? unavailable = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You turn a manager's question about garment-factory (RMG) HR data into a plan. Answer with JSON only, one of:");
        sb.AppendLine("- {\"kind\": \"answer_from_card\"} only when a line of the report card states the whole answer. A breakdown on the card lists only its "
            + "highest and lowest three entries: for a full breakdown, or an entry not listed, plan a query;");
        sb.AppendLine($"- {{\"kind\": \"queries\", \"queries\": [MatrixQuery, …]}} with at most {MaxQueries} queries when numbers must be computed;");
        sb.AppendLine("- {\"kind\": \"definition\", \"definition\": \"<kpi key>\"} when the question asks what a KPI means or how it is computed;");
        sb.AppendLine("- {\"kind\": \"need\", \"need\": \"<what is missing>\"} when these datasets cannot answer it.");
        sb.AppendLine("People's names, IDs, phone numbers, addresses and other personal details are never available: for such a question plan \"need\".");
        sb.AppendLine();
        sb.AppendLine("MatrixQuery:");
        sb.AppendLine("{\"dataset\": \"<id>\", \"join\": [{\"dataset\": \"current:master\", \"on\": \"emp_id\"}], \"filter\": [[field, op, value]], \"group_by\": [field], \"measures\": [[op, field or condition, name]], \"sort\": [[name, \"desc\"]], \"limit\": 50}");
        sb.AppendLine("Filter ops: = != in not_in contains contains_any starts_with between > >= < <= is_null not_null.");
        sb.AppendLine("Measures, each ending with its output name (lower_case): [\"count\", \"*\", n] · [\"count_distinct\", field, n] · [\"sum\" | \"avg\" | \"min\" | \"max\", field, n] · "
            + "[\"count_if\", [field, op, value], n] · [\"ratio\" | \"pct\", [measure], [measure], n] (pct = first ÷ second × 100).");
        sb.AppendLine("A joined dataset's field is written <kind>.<field> (\"master.line_no\"). A date is grouped by month as \"<field>:month\".");
        sb.AppendLine("Count active people only: add [\"status\", \"in\", [\"Active\"]] when the dataset has status. Sewing lines: [\"line_type\", \"=\", \"Sewing\"] with line_no.");
        sb.AppendLine("Make each query give the asked number itself: count_distinct for how many lines, units or sections; pct for a percentage; "
            + "group_by only when the question asks for a breakdown.");
        sb.AppendLine("Operators, helpers and QC are counted by designation, not as everyone on a line: [\"count_if\", [\"designation\", \"contains\", \"Operator\"], \"operators\"].");
        sb.AppendLine("Present in attendance: [\"att_status\", \"in\", [\"P\", \"LT\"]]; late: [\"att_status\", \"=\", \"LT\"]. Designations are matched with contains (\"Operator\", \"Helper\").");
        sb.AppendLine();
        sb.AppendLine("Examples:");
        var example = scope?.Id.ToString(CultureInfo.InvariantCulture)
            ?? (datasets.Any(x => x.Dataset.ReportKind == MxReportKind.Master) ? "current:master" : $"current:{datasets.FirstOrDefault().Dataset?.ReportKind ?? "master"}");
        if (scope is null)
            sb.AppendLine("This conversation is about all the current datasets below: there is no report card, so plan queries (or a definition, or what is missing). "
                + "Use the dataset refs exactly as written; a question about two kinds may need a join or one query per dataset.");
        sb.AppendLine($"Headcount by section → {{\"kind\": \"queries\", \"queries\": [{{\"dataset\": \"{example}\", \"filter\": [[\"status\", \"in\", [\"Active\"]]], \"group_by\": [\"section\"], \"measures\": [[\"count\", \"*\", \"headcount\"]]}}]}}");
        sb.AppendLine($"Sewing lines in Unit-2 → {{\"kind\": \"queries\", \"queries\": [{{\"dataset\": \"{example}\", \"filter\": [[\"status\", \"in\", [\"Active\"]], "
            + "[\"line_type\", \"=\", \"Sewing\"], [\"line_no\", \"not_null\"], [\"unit\", \"=\", \"Unit-2\"]], \"measures\": [[\"count_distinct\", \"line_no\", \"lines\"]]}]}");
        sb.AppendLine($"Female % in Cutting → {{\"kind\": \"queries\", \"queries\": [{{\"dataset\": \"{example}\", \"filter\": [[\"status\", \"in\", [\"Active\"]], [\"section\", \"=\", \"Cutting\"]], \"measures\": [[\"pct\", [\"count_if\", [\"gender\", \"=\", \"Female\"]], [\"count\", \"*\"], \"female_pct\"]]}}]}}");
        sb.AppendLine();
        foreach (var (d, cols) in datasets)
        {
            var reference = d.Id == scope?.Id ? d.Id.ToString(CultureInfo.InvariantCulture) : $"current:{d.ReportKind}";
            sb.AppendLine(d.Id == scope?.Id
                ? $"Dataset in scope: \"{reference}\" — {d.ReportKind}, {d.RowCount} rows, report date {d.ReportDate ?? "unknown"}. Fields:"
                : scope is null
                    ? $"Dataset \"{reference}\" — {d.ReportKind}, {d.RowCount} rows, report date {d.ReportDate ?? "unknown"} (joined as {d.ReportKind}.<field>). Fields:"
                    : $"Can be joined: \"{reference}\" (as {d.ReportKind}.<field>), report date {d.ReportDate ?? "unknown"}. Fields:");
            foreach (var c in cols.Where(c => c.Sensitivity != MxSensitivity.Sensitive))
            {
                var f = FieldCatalog.Find(c.Field);
                var note = c.Sensitivity switch
                {
                    MxSensitivity.Personal => " — personal: never grouped, measured or shown",
                    MxSensitivity.Salary => ctx.CanSalary ? " — salary" : " — salary: this person may not see it",
                    _ => "",
                };
                var values = samples is not null && samples.TryGetValue($"{d.Id}:{c.Key}", out var vs) && vs.Count > 0 ? $" — e.g. {string.Join(", ", vs)}" : "";
                sb.AppendLine($"  {c.Key} — {f?.En ?? c.Header.Trim()} ({c.Type}){note}{values}");
            }
        }
        sb.AppendLine();
        sb.AppendLine("KPIs (for a definition):");
        foreach (var k in kpis)
            sb.AppendLine(unavailable?.FirstOrDefault(u => u.Key == k.Key) is { } u
                ? $"  {k.Key} — {k.En} — NOT AVAILABLE in this dataset: {u.Reason}"
                : $"  {k.Key} — {k.En}");
        sb.AppendLine("A KPI marked NOT AVAILABLE cannot be computed here: for a question about it, plan answer_from_card (or queries for the rest of the question). "
            + "The answer says it cannot be computed and why; never a query for another number to put in its place.");
        return sb.ToString();
    }

    /// <summary>The planner's message: the card, the conversation so far (briefly), the question.</summary>
    public static string PlanUser(string card, IReadOnlyList<(string Question, string Answer)> history, string question)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Report card:").AppendLine(card.Length > 0 ? card : "(none: the question is about all the current datasets)");
        foreach (var (q, a) in history)
            sb.AppendLine($"Earlier question: {q}").AppendLine($"Earlier answer: {(a.Length > 400 ? a[..400] + "…" : a)}");
        sb.AppendLine($"Question: {question}");
        return sb.ToString();
    }

    // "by status", "per line", "each unit", "a breakdown" — not "all" or "list", which name totals and files ("the separation list").
    [GeneratedRegex(@"\b(by|per|each|every|breakdown)\b|অনুযায়ী|প্রতিটি|প্রত্যেক", RegexOptions.IgnoreCase)]
    private static partial Regex Breakdown();

    /// <summary>True when the question asks for a breakdown or a list rather than one number.</summary>
    public static bool AsksBreakdown(string question) => Breakdown().IsMatch(question);

    public const string GroupByMessage =
        "This question asks for a breakdown, but the query counts everything as one number. Group it by the field the question names "
        + "(group_by), so that every group is a row. Return the plan with the same JSON shape.";

    /// <summary>True when every query of a plan gives one number (no group_by).</summary>
    public static bool Ungrouped(MxPlan plan) => plan.Kind == "queries" && plan.Queries.Count > 0
        && plan.Queries.All(q => !q.TryGetProperty("group_by", out var g) || g.ValueKind != JsonValueKind.Array || g.GetArrayLength() == 0);

    public const string FullBreakdownMessage =
        "This question asks for a breakdown, and the report card holds only part of its breakdowns (the top and bottom three). "
        + "Plan a query that returns every group. Return the plan with the same JSON shape.";

    // ───────────────────────── what is not available ─────────────────────────

    /// <summary>More ways a question names a KPI than its names (lower case).</summary>
    private static readonly Dictionary<string, string[]> Aliases = new()
    {
        ["attendance_pct"] = new[] { "attendance rate", "attendance %", "attendance percentage", "attendance percent", "উপস্থিতির হার", "হাজিরার হার", "উপস্থিতি হার" },
        ["female_pct"] = new[] { "female percentage", "female rate", "নারীর হার", "নারী কর্মীর হার" },
        ["late_pct"] = new[] { "late percentage", "late rate", "দেরির হার" },
    };

    private static IEnumerable<string> NamesOf(MxCardUnavailable u) =>
        new[] { u.Label, u.LabelBn }.Select(n => n.Replace("%", "").Trim().ToLowerInvariant()).Where(n => n.Length > 2)
            .Concat(Aliases.GetValueOrDefault(u.Key) ?? Array.Empty<string>());

    /// <summary>The unavailable KPIs a question asks for.</summary>
    public static List<MxCardUnavailable> AskedUnavailable(string question, IReadOnlyList<MxCardUnavailable>? unavailable)
    {
        var q = question.ToLowerInvariant();
        return (unavailable ?? Array.Empty<MxCardUnavailable>()).Where(u => NamesOf(u).Any(q.Contains)).ToList();
    }

    /// <summary>A sentence: up to . । ! ? or a line break — a decimal point (5.92) does not end one.</summary>
    [GeneratedRegex(@"(?:[^.।!?\n]|\.(?=\d))+")]
    private static partial Regex Sentence();

    [GeneratedRegex(@"[\d০-৯]+(?:[.,][\d০-৯]+)?\s*%")]
    private static partial Regex Percent();

    [GeneratedRegex(@"not available|cannot be computed|can't be computed|cannot be calculated|isn't available|is not available|পাওয়া যায় না|গণনা করা যায় না|হিসাব করা যায় না|নির্ণয় করা যায় না|সম্ভব নয়|করা যাবে না|করা যায়নি|পাওয়া যায়নি|উপলব্ধ নয়|cannot be determined|not possible")]
    private static partial Regex SaysUnavailable();

    /// <summary>
    /// How an answer treats an unavailable KPI it was asked for: Substituted — a sentence naming it
    /// gives a percentage; Said — a sentence naming it says it cannot be computed.
    /// </summary>
    public static (bool Substituted, bool Said) Treatment(string answer, MxCardUnavailable u)
    {
        var names = NamesOf(u).ToList();
        var naming = Sentence().Matches(answer.ToLowerInvariant()).Select(m => m.Value).Where(s => names.Any(s.Contains)).ToList();
        return (naming.Any(s => Percent().IsMatch(s) && !SaysUnavailable().IsMatch(s)), naming.Any(s => SaysUnavailable().IsMatch(s)) || SaysUnavailable().IsMatch(answer.ToLowerInvariant()) && answer.Contains(u.Reason[..Math.Min(20, u.Reason.Length)]));
    }

    /// <summary>The sentence code adds when the answer did not say it, or put another number in its place.</summary>
    public static string UnavailableNote(MxCardUnavailable u, string label, bool bengali, bool substituted) =>
        (substituted ? (bengali ? "\n\n⚠ ওপরের শতাংশটি সেটি নয়। " : "\n\n⚠ The percentage above is not it. ") : "\n\n")
        + (bengali ? $"{u.LabelBn} গণনা করা যায় না: {u.Reason} [{label}]" : $"{u.Label} cannot be computed: {u.Reason} [{label}]");

    public static string RepairMessage(IEnumerable<string> errors) =>
        "These queries are not valid:\n" + string.Join("\n", errors.Select(e => "- " + e))
        + "\nReturn a corrected plan, with the same JSON shape. Use only the fields listed.";

    /// <summary>The planner's JSON as a plan, or null when it is not one.</summary>
    public static MxPlan? ParsePlan(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            string? Str(string p) => r.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var queries = r.TryGetProperty("queries", out var qs) && qs.ValueKind == JsonValueKind.Array
                ? qs.EnumerateArray().Where(q => q.ValueKind == JsonValueKind.Object).Select(q => q.Clone()).Take(MaxQueries).ToList()
                : new List<JsonElement>();
            var kind = Str("kind") ?? (r.TryGetProperty("answer_from_card", out var afc) && afc.ValueKind == JsonValueKind.True ? "answer_from_card"
                : queries.Count > 0 ? "queries" : Str("definition") is not null ? "definition" : Str("need") is not null ? "need" : null);
            return kind is null ? null : new MxPlan(kind, queries, Str("definition"), Str("need"));
        }
        catch (JsonException) { return null; }
    }

    // ───────────────────────── the answer prompt ─────────────────────────

    /// <summary>Bengali when the question is written in Bengali script.</summary>
    public static bool IsBengali(string text) => text.Any(c => c is >= 'ঀ' and <= '৿');

    public static string AnswerSystem(bool bengali) =>
        "You answer a manager's question about one garment-factory (RMG) report, from the results below.\n"
        + (bengali ? "- Answer in Bengali (বাংলা).\n" : "- Answer in English.\n")
        + "- Be short: one or two sentences, or a few bullets.\n"
        + "- Copy every number from the results or the report card, or write the calculation as [[= expression ]] with numbers from them. Never work a number out yourself.\n"
        + "- A formula holds only numbers, + - * / and parentheses, such as [[= 2226 + 1738 ]]: never a row label or a function.\n"
        + "- Cite where each number comes from: [R1] for result row R1, [C3] for report card line C3, [KPI: <name>] for a KPI's definition.\n"
        + "- If the question asks for something the report card lists under \"Not available\", say that it cannot be computed and why. Never give another number, or another percentage, in its place.\n"
        + "- If the results do not answer the question, say so plainly. Never name a person.";

    /// <summary>Result rows as the model reads them: "R3: unit = ADL-Unit-1; headcount = 2226".</summary>
    public static string ResultsText(IReadOnlyList<MxAskResult> results, string? definition)
    {
        var sb = new StringBuilder();
        foreach (var (r, i) in results.Select((r, i) => (r, i)))
        {
            sb.AppendLine($"Query {i + 1}: {r.Plain} — {r.Matched} matching rows{(r.Rows.Count > r.Shown ? $", the first {r.Shown} groups shown" : "")}.");
            if (r.Error is not null) { sb.AppendLine($"  Not run: {r.Error}"); continue; }
            foreach (var row in r.Rows.Take(r.Shown))
                sb.AppendLine($"{row.Label}: {string.Join("; ", r.Columns.Select(c => $"{c} = {Value(row.Values.GetValueOrDefault(c))}"))}");
        }
        if (definition is not null) sb.AppendLine(definition);
        return sb.ToString();
    }

    public static string AnswerUser(string card, string results, string question) =>
        $"Report card:\n{(card.Length > 0 ? card : "(none: the question is about all the current datasets)\n")}\nResults:\n{(results.Length == 0 ? "(no query was run)\n" : results)}\nQuestion: {question}";

    public static string Value(object? v) => v switch
    {
        null => "(none)",
        double d => d.ToString("0.####", CultureInfo.InvariantCulture),
        float f => f.ToString("0.####", CultureInfo.InvariantCulture),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    // ───────────────────────── citations ─────────────────────────

    [GeneratedRegex(@"\[(R\d+|C\d+|KPI:\s*[^\]]{1,80}|Card)\]")]
    private static partial Regex Citation();

    /// <summary>Citations that name nothing this answer was given — "unknown source".</summary>
    public static List<string> UnknownCitations(string answer, ISet<string> rowLabels, ISet<string> cardLabels, IReadOnlyList<MxKpi> kpis)
    {
        bool Known(string c)
        {
            if (c == "Card") return true;
            if (c.StartsWith('R')) return rowLabels.Contains(c);
            if (c.StartsWith('C')) return cardLabels.Contains(c);
            var name = c[4..].Trim();
            return kpis.Any(k => string.Equals(k.Key, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(k.En, name, StringComparison.OrdinalIgnoreCase) || k.Bn == name);
        }
        return Citation().Matches(answer).Select(m => Regex.Replace(m.Groups[1].Value, @"\s+", " ").Trim()).Where(c => !Known(c)).Distinct().ToList();
    }

    /// <summary>The words of a refusal for a query the person may not run, naming the permission.</summary>
    public static string Refusal(string error, bool bengali) => bengali
        ? $"এই প্রশ্নের উত্তর দেওয়া যাবে না: {error}"
        : $"I can't answer that: {error}";
}

/// <summary>One labelled row of a result: R3, its values, and the token that reopens its rows.</summary>
public sealed record MxAskRow(string Label, Dictionary<string, object?> Values, string? Cite);

/// <summary>One query as run for an answer: its JSON, its plain words, what it read, its rows.</summary>
public sealed record MxAskResult(JsonElement Query, string Plain, List<MxQueryDataset> Datasets, long Matched, List<string> Columns,
    List<string> GroupBy, List<string> Measures, List<MxAskRow> Rows, int Shown, string? Error);
