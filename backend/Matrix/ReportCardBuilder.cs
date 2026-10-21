// ম্যাট্রিক্স — the report card (docs/MATRIX_PROMPT.md §3.8): built by code, at import.
//
// For the dataset's kind: its main KPIs with values and citation tokens, the top and
// bottom three of its key breakdowns (the lines with the fewest operators…), the
// change against the previous snapshot of the same kind and layout, and its open
// quality issues. Its text rendering — what a summary is written from in M4 — is
// kept to 1,000–3,000 tokens, and that size is part of the card.

using System.Globalization;
using System.Text;
using RabbitIC.Api.DTOs.Matrix;
using RabbitIC.Api.Extraction;
using RabbitIC.Api.Models;

namespace RabbitIC.Api.Matrix;

public static class ReportCardBuilder
{
    /// <summary>Cards made by an older builder are made again when read (CardAsync).</summary>
    public const int Version = 3;

    /// <summary>A per-line KPI shown as one tile: its median, citing every row it counts (the KPI named here).</summary>
    private static readonly Dictionary<string, string> MedianCites = new() { ["operators_per_line"] = "sewing_operators" };
    private sealed record Plan(string[] Items, string[] Breakdowns);

    private static Plan PlanFor(string kind) => kind switch
    {
        MxReportKind.Master or MxReportKind.Manpower => new(
            new[] { "active_headcount", "sewing_lines_total", "operators_per_line", "female_pct", "mgmt_worker_ratio", "headcount_without_grade_mapping" },
            new[] { "headcount_by_unit", "headcount_by_section", "operators_per_line", "headcount_by_designation" }),
        MxReportKind.AttendanceDaily => new(
            new[] { "rows_total", "att_present", "att_late", "late_pct", "ot_over_2h", "ot_over_4h", "ot_total", "earliest_in", "latest_out" },
            new[] { "shift_mix", "present_by_department" }),
        MxReportKind.SeparationList => new(
            new[] { "rows_total", "separations_total", "active_headcount" },
            new[] { "separations_by_status", "separations_by_tenure_band" }),
        MxReportKind.DesignationLookup => new(new[] { "rows_total", "grades_count" }, new[] { "designations_by_grade" }),
        _ => new(new[] { "rows_total" }, Array.Empty<string>()),
    };

    public static async Task<MxReportCardDto> BuildAsync(
        MxDataset d, MxQueryContext ctx, List<MxDsColumn> cols, Func<string, MxDataset, MxDataset?, Task<MxKpiResult>> run,
        List<MxQualityIssue> issues)
    {
        var plan = PlanFor(d.ReportKind);
        async Task<MxKpiResult?> Try(string key, MxDataset on)
        {
            // A KPI whose fields this dataset lacks (or the caller may not see) is left out of the card.
            try { return await run(key, on, null); }
            catch (MxQueryError) { return null; }
        }

        // A per-line KPI's value is its median line.
        static double? ValueOf(MxKpiResult r) => r.Value ?? r.Summary?.Median;

        var items = new List<MxCardItem>();
        foreach (var key in plan.Items)
        {
            if (await Try(key, d) is not { } r || (ValueOf(r) is null && r.Text is null)) continue;
            if (r.Summary is { } s && MedianCites.TryGetValue(key, out var citeKey))
                items.Add(new MxCardItem(key, $"{r.NameEn} (median)", $"{r.NameBn} (মধ্যমা)", s.Median, null, "per line",
                    (await Try(citeKey, d))?.Cite, $"lowest {Fmt(s.Min)}, highest {Fmt(s.Max)}, over {s.Count} lines"));
            else
                items.Add(new MxCardItem(key, r.NameEn, r.NameBn, r.Value, r.Text, r.Unit, r.Cite));
        }

        var breakdowns = new List<MxCardBreakdown>();
        foreach (var key in plan.Breakdowns)
        {
            if (await Try(key, d) is not { } r || r.Rows.Count == 0) continue;
            var entries = r.Rows.Where(x => x["value"] is double).Select(x =>
                new MxCardEntry(Label(r.GroupBy, x.GetValueOrDefault(r.GroupBy ?? "")), (double)x["value"]!, r.Unit, x.GetValueOrDefault("_cite") as string)).ToList();
            breakdowns.Add(new MxCardBreakdown(key, r.NameEn, r.NameBn,
                entries.OrderByDescending(e => e.Value).Take(3).ToList(),
                entries.Count > 3 ? entries.OrderBy(e => e.Value).Take(3).ToList() : new List<MxCardEntry>(), entries.Count));
        }

        // The snapshot before this one, of the same kind and layout.
        MxCardComparison? comparison = null;
        var previous = MatrixQuery.PreviousSnapshot(d, ctx.Datasets);
        if (previous is not null)
        {
            var changes = new List<MxCardChange>();
            foreach (var item in items.Where(i => i.Value is not null))
                if (await Try(item.Key, previous) is { } pr && ValueOf(pr) is { } before)
                    changes.Add(new MxCardChange(item.Key, item.Label, before, item.Value, Math.Round(item.Value!.Value - before, 2)));
            comparison = new MxCardComparison(previous.Id, previous.ReportDate, changes);
        }

        var scope = new List<string>();
        if (cols.Any(c => c.Key == "unit") && await Try("headcount_by_unit", d) is { } units)
            scope.AddRange(units.Rows.Select(x => x.GetValueOrDefault("unit")?.ToString()).Where(u => u is not null).Select(u => u!).Take(20));

        var quality = issues.Where(i => i.State == MxQualityIssue.Open).Select(i => new MxCardIssue(i.Check, i.Severity, i.Count)).ToList();
        var card = new MxReportCardDto(d.Id, d.ReportKind, d.Name, d.ReportDate, scope, items, breakdowns, comparison, quality, 0, DateTime.UtcNow, Version);
        return card with { Tokens = PoPromptBuilder.EstimateTokens(Render(card)) };
    }

    private static string Label(string? field, object? value) => value is null ? "(none)"
        : field == "line_no" ? $"LINE - {value}" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

    /// <summary>
    /// Every statement of the card with its label, [C1], [C2] … in the order Render writes them:
    /// the items, the breakdowns' highest and lowest entries, the changes since the previous
    /// snapshot, and the open quality issues. A summary point cites these labels; the page
    /// opens a label's rows through its citation token.
    /// </summary>
    public static List<MxCardLabel> Labels(MxReportCardDto card)
    {
        var list = new List<MxCardLabel>();
        void Add(string key, string text, string? cite) => list.Add(new MxCardLabel($"C{list.Count + 1}", key, text, cite));
        foreach (var i in card.Items)
            Add(i.Key, $"{i.Label} ({i.LabelBn}): {i.Text ?? Fmt(i.Value)} {i.Unit}{(i.Detail is null ? "" : $" ({i.Detail})")}", i.Cite);
        foreach (var b in card.Breakdowns)
        {
            foreach (var e in b.Top) Add(b.Key, $"{e.Label} {Fmt(e.Value)}", e.Cite);
            foreach (var e in b.Bottom) Add(b.Key, $"{e.Label} {Fmt(e.Value)}", e.Cite);
        }
        if (card.Comparison is { } c)
            foreach (var x in c.Items)
                Add(x.Key, $"{x.Label} {Fmt(x.Before)} → {Fmt(x.After)} ({(x.Change >= 0 ? "+" : "")}{Fmt(x.Change)})",
                    card.Items.FirstOrDefault(i => i.Key == x.Key)?.Cite);
        foreach (var q in card.Quality) Add("quality", $"{q.Check} {q.Count} ({q.Severity})", null);
        return list;
    }

    /// <summary>The card as text, every statement labelled [C1], [C2] … — the form a summary is written from.</summary>
    public static string Render(MxReportCardDto card)
    {
        var labels = Labels(card);
        var n = 0;
        string Next() => $"[{labels[n++].Label}] {labels[n - 1].Text}";
        var sb = new StringBuilder();
        sb.AppendLine($"Report card — {card.Kind}, {card.Name}, report date {card.ReportDate ?? "unknown"}");
        if (card.Scope.Count > 0) sb.AppendLine($"Scope: {string.Join(", ", card.Scope)}");
        foreach (var _ in card.Items) sb.AppendLine(Next());
        foreach (var b in card.Breakdowns)
        {
            // "of 8": the card lists part of the breakdown, and says so.
            sb.AppendLine($"{b.Label} ({b.LabelBn}) — {b.Groups} groups; highest {b.Top.Count}: {string.Join("; ", b.Top.Select(_ => Next()))}");
            if (b.Bottom.Count > 0) sb.AppendLine($"  lowest {b.Bottom.Count}: {string.Join("; ", b.Bottom.Select(_ => Next()))}");
        }
        if (card.Comparison is { } c)
            sb.AppendLine($"Against the previous snapshot ({c.PreviousDate}): {string.Join("; ", c.Items.Select(_ => Next()))}");
        if (card.Quality.Count > 0) sb.AppendLine($"Open quality issues: {string.Join("; ", card.Quality.Select(_ => Next()))}");
        return sb.ToString();
    }

    private static string Fmt(double? v) => v is null ? "—" : v.Value.ToString("0.##", CultureInfo.InvariantCulture);
}
