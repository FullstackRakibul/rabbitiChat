// ম্যাট্রিক্স — dashboards (docs/MATRIX_PROMPT.md §3.8): the automatic layouts, one per
// report kind, and the rules a saved layout must follow.
//
// A layout is { "auto"?: "<key>", "widgets": [ widget, … ] }, and a widget is
//
//   { "id": "w1", "type": "kpi" | "bar" | "stacked-bar" | "line" | "donut" | "gauge" | "heatmap"
//                         | "table" | "timeline" | "line-board",
//     "title": { "en": "…", "bn": "…" },           optional for a KPI widget (its names)
//     "kpi": "<KpiLibrary key>", "dataset": "current:master"     a named KPI …
//     "query": { MatrixQuery },                                   … or a query of its own
//     "x": 0, "y": 0, "w": 3, "h": 2,                             a 12-column grid
//     "options": { "series": "snapshots", "target": 20, "status": "operators", … } }
//
// Every number a widget shows is its KPI's or its query's result, run by MatrixService
// with the dashboard's filters added (MatrixQuery.ApplyScope).

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RabbitIC.Api.Models;

namespace RabbitIC.Api.Matrix;

public static class MxDashboardAuto
{
    public const string ManpowerOverview = "manpower-overview";
    public const string LineBoard = "line-board";
    public const string AttendanceToday = "attendance-today";
    public const string JoinersSeparations = "joiners-separations";
    public const string DataQuality = "data-quality";
}

public static partial class DashboardLibrary
{
    public static readonly string[] WidgetTypes =
        { "kpi", "bar", "stacked-bar", "line", "donut", "gauge", "heatmap", "table", "timeline", "line-board" };

    public const int Columns = 12;
    public const int MaxWidgets = 40;

    /// <summary>The dashboard filters (§3.8), the fields each can sit on, in order.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> FilterFields = new Dictionary<string, string[]>
    {
        ["unit"] = new[] { "unit" },
        ["section"] = new[] { "section" },
        ["line"] = new[] { "line_no" },
        ["emp_type"] = new[] { "emp_type" },
        ["status"] = new[] { "status", "att_status" },
        ["shift"] = new[] { "shift" },
        ["date"] = new[] { "att_date", "separation_date", "effect_date" },
    };

    /// <summary>The automatic dashboards a report kind brings (Data quality comes with every kind).</summary>
    public static string[] AutoFor(string kind) => kind switch
    {
        MxReportKind.Master => new[] { MxDashboardAuto.ManpowerOverview, MxDashboardAuto.LineBoard, MxDashboardAuto.DataQuality },
        MxReportKind.AttendanceDaily => new[] { MxDashboardAuto.AttendanceToday, MxDashboardAuto.DataQuality },
        MxReportKind.SeparationList or MxReportKind.Manpower => new[] { MxDashboardAuto.JoinersSeparations, MxDashboardAuto.DataQuality },
        _ => new[] { MxDashboardAuto.DataQuality },
    };

    public static (string En, string Bn, string? Kind) NameOf(string auto) => auto switch
    {
        MxDashboardAuto.ManpowerOverview => ("Manpower overview", "জনবলের সারসংক্ষেপ", MxReportKind.Master),
        MxDashboardAuto.LineBoard => ("Line board", "লাইন বোর্ড", MxReportKind.Master),
        MxDashboardAuto.AttendanceToday => ("Attendance today", "আজকের হাজিরা", MxReportKind.AttendanceDaily),
        MxDashboardAuto.JoinersSeparations => ("Joiners and separations", "যোগদান ও বিচ্ছেদ", MxReportKind.SeparationList),
        _ => ("Data quality", "ডেটার মান", null),
    };

    // ───────────────────────── automatic layouts ─────────────────────────

    private const string Sewing = "[\"line_type\", \"=\", \"Sewing\"], [\"line_no\", \"not_null\"]";
    private const string Active = "[\"status\", \"in\", [\"Active\"]]";
    private const string Present = "[\"att_status\", \"in\", [\"P\", \"LT\"]]";

    private static JsonObject Kpi(string id, string kpi, string dataset, string type, int x, int y, int w, int h, string? options = null) => new()
    {
        ["id"] = id, ["type"] = type, ["kpi"] = kpi, ["dataset"] = dataset, ["x"] = x, ["y"] = y, ["w"] = w, ["h"] = h,
        ["options"] = JsonNode.Parse(options ?? "{}"),
    };

    private static JsonObject Query(string id, string en, string bn, string type, string query, int x, int y, int w, int h, string? options = null) => new()
    {
        ["id"] = id, ["type"] = type, ["title"] = new JsonObject { ["en"] = en, ["bn"] = bn }, ["query"] = JsonNode.Parse(query),
        ["x"] = x, ["y"] = y, ["w"] = w, ["h"] = h, ["options"] = JsonNode.Parse(options ?? "{}"),
    };

    /// <summary>The layout an automatic dashboard starts with. <paramref name="kinds"/>: the kinds imported so far (Data quality has one widget each).</summary>
    public static JsonObject AutoLayout(string auto, IReadOnlyCollection<string> kinds)
    {
        const string M = "current:master";
        const string A = "current:attendance-daily";
        const string S = "current:separation-list";
        const string P = "current:manpower";
        var widgets = auto switch
        {
            MxDashboardAuto.ManpowerOverview => new JsonArray(
                Kpi("headcount", "active_headcount", M, "kpi", 0, 0, 3, 2),
                Kpi("female", "female_pct", M, "kpi", 3, 0, 3, 2),
                Kpi("lines", "sewing_lines_total", M, "kpi", 6, 0, 3, 2),
                Kpi("no-grade", "headcount_without_grade_mapping", M, "kpi", 9, 0, 3, 2),
                Kpi("by-unit", "headcount_by_unit", M, "bar", 0, 2, 6, 4),
                Kpi("by-section", "headcount_by_section", M, "donut", 6, 2, 6, 4),
                Kpi("by-gender", "headcount_by_gender", M, "donut", 0, 6, 4, 4),
                Kpi("by-grade", "headcount_by_grade", M, "bar", 4, 6, 4, 4),
                Kpi("tenure", "tenure_band_distribution", M, "bar", 8, 6, 4, 4),
                Query("section-gender", "Sections by gender", "সেকশন ও লিঙ্গ", "stacked-bar",
                    $"{{\"dataset\": \"{M}\", \"filter\": [{Active}], \"group_by\": [\"section\", \"gender\"], \"measures\": [[\"count\", \"*\", \"people\"]], \"limit\": 500}}", 0, 10, 8, 4),
                Kpi("ratio", "mgmt_worker_ratio", M, "gauge", 8, 10, 4, 4),
                Kpi("trend", "active_headcount", M, "line", 0, 14, 12, 4, "{\"series\": \"snapshots\"}")),
            MxDashboardAuto.LineBoard => new JsonArray(
                Kpi("lines", "sewing_lines_total", M, "kpi", 0, 0, 4, 2),
                Kpi("below", "lines_below_operator_count", M, "kpi", 4, 0, 4, 2),
                Kpi("ratio", "mgmt_worker_ratio", M, "kpi", 8, 0, 4, 2),
                Query("board", "Sewing lines", "সেলাই লাইন", "line-board",
                    $"{{\"dataset\": \"{M}\", \"filter\": [{Active}, {Sewing}], \"group_by\": [\"line_no\"], \"measures\": ["
                    + "[\"count_if\", [\"designation\", \"contains\", \"Operator\"], \"operators\"], "
                    + "[\"count_if\", [\"designation\", \"contains\", \"Helper\"], \"helpers\"], "
                    + "[\"count_if\", [\"designation\", \"contains_any\", [\"Quality Inspector\", \"Quality Controller\", \"Checker\"]], \"qc\"], "
                    + "[\"ratio\", [\"count_if\", [\"designation\", \"contains\", \"Operator\"]], [\"count_if\", [\"designation\", \"contains\", \"Helper\"]], \"op_helper\"]], "
                    + "\"sort\": [[\"line_no\", \"asc\"]], \"limit\": 500}", 0, 2, 12, 8,
                    "{\"status\": \"operators\", \"thresholdsOf\": \"operators_per_line\"}"),
                Kpi("operators", "operators_per_line", M, "bar", 0, 10, 12, 4, "{\"target\": 20}"),
                Query("unit-lines", "Lines per unit", "ইউনিট অনুযায়ী লাইন", "bar",
                    $"{{\"dataset\": \"{M}\", \"filter\": [{Active}, {Sewing}], \"group_by\": [\"unit\"], \"measures\": [[\"count_distinct\", \"line_no\", \"lines\"]], \"limit\": 500}}", 0, 14, 6, 4),
                Kpi("helpers", "helpers_per_line", M, "bar", 6, 14, 6, 4)),
            MxDashboardAuto.AttendanceToday => new JsonArray(
                Kpi("present", "att_present", A, "kpi", 0, 0, 2, 2),
                Kpi("late", "att_late", A, "kpi", 2, 0, 2, 2),
                Kpi("late-pct", "late_pct", A, "kpi", 4, 0, 2, 2),
                Kpi("att-pct", "attendance_pct", A, "kpi", 6, 0, 2, 2),
                Kpi("ot2", "ot_over_2h", A, "kpi", 8, 0, 2, 2),
                Kpi("ot4", "ot_over_4h", A, "kpi", 10, 0, 2, 2),
                Kpi("by-dept", "present_by_department", A, "bar", 0, 2, 6, 4),
                Query("dept-status", "Departments by status", "বিভাগ ও হাজিরার অবস্থা", "stacked-bar",
                    $"{{\"dataset\": \"{A}\", \"group_by\": [\"department\", \"att_status\"], \"measures\": [[\"count\", \"*\", \"people\"]], \"limit\": 500}}", 6, 2, 6, 4),
                Query("by-line", "Present and late by line", "লাইন অনুযায়ী উপস্থিত ও দেরি", "bar",
                    $"{{\"dataset\": \"{A}\", \"join\": [{{\"dataset\": \"{M}\", \"on\": \"emp_id\"}}], \"filter\": [[\"master.line_type\", \"=\", \"Sewing\"]], \"group_by\": [\"master.line_no\"], "
                    + $"\"measures\": [[\"count_if\", {Present}, \"present\"], [\"count_if\", [\"att_status\", \"=\", \"LT\"], \"late\"]], \"limit\": 500}}", 0, 6, 12, 4),
                Query("ot", "OT distribution", "ওভারটাইম বণ্টন", "bar",
                    $"{{\"dataset\": \"{A}\", \"filter\": [[\"ot_hours\", \"not_null\"]], \"group_by\": [\"ot_hours\"], \"measures\": [[\"count\", \"*\", \"people\"]], \"limit\": 500}}", 0, 10, 6, 4),
                Kpi("shift", "shift_mix", A, "donut", 6, 10, 6, 4),
                Query("dept-shift", "Departments by shift", "বিভাগ ও শিফট", "heatmap",
                    $"{{\"dataset\": \"{A}\", \"filter\": [{Present}], \"group_by\": [\"department\", \"shift\"], \"measures\": [[\"count\", \"*\", \"people\"]], \"limit\": 500}}", 0, 14, 6, 4),
                Kpi("trend", "att_present", A, "line", 6, 14, 6, 4, "{\"series\": \"snapshots\"}")),
            MxDashboardAuto.JoinersSeparations => new JsonArray(
                Kpi("separations", "separations_total", S, "kpi", 0, 0, 3, 2),
                Kpi("joiners", "joiners_since_previous", P, "kpi", 3, 0, 3, 2),
                Kpi("left", "separations_since_previous", P, "kpi", 6, 0, 3, 2),
                Kpi("attrition", "attrition_pct", P, "kpi", 9, 0, 3, 2),
                Query("sep-month", "Separations by month", "মাস অনুযায়ী বিচ্ছেদ", "timeline",
                    $"{{\"dataset\": \"{S}\", \"filter\": [[\"status\", \"not_null\"], [\"status\", \"not_in\", [\"Active\"]], [\"effect_date\", \"not_null\"]], \"group_by\": [\"effect_date:month\"], \"measures\": [[\"count\", \"*\", \"people\"]], \"limit\": 500}}", 0, 2, 12, 4),
                Kpi("by-status", "separations_by_status", S, "donut", 0, 6, 6, 4),
                Kpi("by-tenure", "separations_by_tenure_band", S, "bar", 6, 6, 6, 4),
                Query("join-month", "Joiners by month", "মাস অনুযায়ী যোগদান", "timeline",
                    $"{{\"dataset\": \"{S}\", \"filter\": [[\"joining_date\", \"not_null\"]], \"group_by\": [\"joining_date:month\"], \"measures\": [[\"count\", \"*\", \"people\"]], \"limit\": 500}}", 0, 10, 12, 4),
                Kpi("trend", "active_headcount", P, "line", 0, 14, 12, 4, "{\"series\": \"snapshots\"}")),
            _ => QualityWidgets(kinds),
        };
        return new JsonObject { ["auto"] = auto, ["widgets"] = widgets };
    }

    private static JsonArray QualityWidgets(IReadOnlyCollection<string> kinds)
    {
        var list = new JsonArray();
        var i = 0;
        foreach (var kind in MxReportKind.All.Where(kinds.Contains))
        {
            list.Add(Kpi($"issues-{kind}", "open_issues_by_check", $"current:{kind}", "bar", i % 2 * 6, i / 2 * 4, 6, 4,
                $"{{\"titleSuffix\": \"{kind}\"}}"));
            i++;
        }
        return list;
    }

    /// <summary>Adds a Data quality widget for each kind that has none yet; true when it added one.</summary>
    public static bool AddQualityKinds(JsonObject layout, IReadOnlyCollection<string> kinds)
    {
        if (layout["widgets"] is not JsonArray widgets) return false;
        var have = widgets.Select(w => w?["id"]?.ToString()).ToHashSet();
        var bottom = widgets.Select(w => (w?["y"]?.GetValue<int>() ?? 0) + (w?["h"]?.GetValue<int>() ?? 0)).DefaultIfEmpty(0).Max();
        var added = false;
        foreach (var w in QualityWidgets(kinds).Where(w => !have.Contains(w!["id"]!.ToString())).ToList())
        {
            var copy = w!.DeepClone().AsObject();
            copy["x"] = 0; copy["y"] = bottom; copy["w"] = 6;
            bottom += 4;
            widgets.Add(copy);
            added = true;
        }
        return added;
    }

    // ───────────────────────── validation ─────────────────────────

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,40}$")]
    private static partial Regex WidgetId();

    [GeneratedRegex(@"^(current|previous|snapshots):[a-z-]+$|^\d{1,9}$")]
    private static partial Regex DatasetRef();

    /// <summary>
    /// Why a layout cannot be saved, or null. Widget types, KPI keys and the grid come from
    /// fixed lists; a widget's own query is compiled by MatrixQuery for the saving user, so a
    /// layout can hold nothing that user could not run.
    /// </summary>
    public static string? Problem(JsonElement layout, MxQueryContext ctx)
    {
        if (layout.ValueKind != JsonValueKind.Object) return "The layout is an object with \"widgets\".";
        if (layout.GetRawText().Length > 200_000) return "The layout is larger than 200 KB.";
        if (!layout.TryGetProperty("widgets", out var ws) || ws.ValueKind != JsonValueKind.Array) return "The layout needs a \"widgets\" list.";
        if (ws.GetArrayLength() > MaxWidgets) return $"At most {MaxWidgets} widgets.";
        var ids = new HashSet<string>();
        foreach (var w in ws.EnumerateArray())
        {
            if (w.ValueKind != JsonValueKind.Object) return "Each widget is an object.";
            var id = Str(w, "id");
            if (id is null || !WidgetId().IsMatch(id)) return "Each widget needs an id: letters, digits, - and _, at most 40.";
            if (!ids.Add(id)) return $"Two widgets are called \"{id}\".";
            string P(string m) => $"Widget \"{id}\": {m}";
            var type = Str(w, "type");
            if (type is null || !WidgetTypes.Contains(type)) return P($"type must be one of {string.Join(", ", WidgetTypes)}.");
            int Int(string name) => w.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : -1;
            int x = Int("x"), y = Int("y"), wd = Int("w"), h = Int("h");
            if (x < 0 || wd < 1 || x + wd > Columns) return P($"x and w must fit the {Columns}-column grid (x ≥ 0, w ≥ 1, x + w ≤ {Columns}).");
            if (y < 0 || y > 400 || h < 1 || h > 24) return P("y is 0–400 and h is 1–24.");
            if (w.TryGetProperty("title", out var t))
            {
                if (t.ValueKind != JsonValueKind.Object) return P("title is { en, bn }.");
                foreach (var lang in new[] { "en", "bn" })
                    if (t.TryGetProperty(lang, out var s) && (s.ValueKind != JsonValueKind.String || s.GetString()!.Length > 120)) return P($"title.{lang} is text of at most 120 characters.");
            }
            if (w.TryGetProperty("options", out var o) && (o.ValueKind != JsonValueKind.Object || o.GetRawText().Length > 4000)) return P("options is an object of at most 4,000 characters.");

            var kpi = Str(w, "kpi");
            var hasQuery = w.TryGetProperty("query", out var q) && q.ValueKind != JsonValueKind.Null;
            if (kpi is null == !hasQuery) return P("give either \"kpi\" (a KPI key) or \"query\" (a MatrixQuery), not both.");
            if (kpi is not null)
            {
                if (KpiLibrary.Find(kpi) is null) return P($"no KPI \"{kpi}\".");
                if (Str(w, "dataset") is { } d && !DatasetRef().IsMatch(d)) return P($"dataset \"{d}\" is not an id or current:/previous:/snapshots:<kind>.");
            }
            else
            {
                try { MatrixQuery.Compile(q, ctx); }
                catch (MxQueryError e) { return P(e.Message); }
            }
        }
        return null;
    }

    private static string? Str(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind is JsonValueKind.String ? v.GetString() : null;
}
