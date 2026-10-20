// ম্যাট্রিক্স — the RMG KPI library (docs/MATRIX_PROMPT.md §3.7).
//
// Each KPI is a named MatrixQuery ("{d}" is the dataset it runs on, "{prev}" the
// snapshot before it, "{from}"/"{to}" a period) plus its shape: one value, a
// breakdown, a per-line breakdown with min/median/max, or a computation over two
// queries (attrition, attendance %). The query and the arithmetic on top of it are
// code; the KPI's names, plain-words formula and thresholds are seeded into
// MxKpiDefinitions and editable there.

namespace RabbitIC.Api.Matrix;

public static class MxKpiShape
{
    public const string Value = "value";
    public const string Rows = "rows";
    public const string PerLine = "per-line";
    public const string Attrition = "attrition";
    public const string AttendancePct = "attendance-pct";
    public const string LinesBelow = "lines-below";
    public const string OpenIssues = "open-issues";
}

/// <param name="ActiveOnly">Adds status = Active when the dataset has a status column (a headcount counts the active).</param>
/// <param name="Unit">people, %, ratio, lines, hours, tk, time.</param>
public sealed record MxKpi(
    string Key, string Area, string En, string Bn, string Words, string Unit, string Shape, string Query,
    bool ActiveOnly = false, string? GroupField = null, string[]? Kinds = null);

public static class KpiLibrary
{
    private static readonly string[] People = { MxReportKind.Master, MxReportKind.Manpower, MxReportKind.SeparationList };
    private static readonly string[] Att = { MxReportKind.AttendanceDaily };
    private static readonly string[] Lookup = { MxReportKind.DesignationLookup };

    private const string Sewing = "[\"line_type\", \"=\", \"Sewing\"], [\"line_no\", \"not_null\"]";
    private const string Staff = "[\"emp_type\", \"in\", [\"Management\", \"Non-Management\"]]";
    private const string Workers = "[\"emp_type\", \"in\", [\"Schedule-A\", \"Schedule-B\"]]";
    private const string Present = "[\"att_status\", \"in\", [\"P\", \"LT\"]]";

    private static MxKpi By(string key, string field, string en, string bn) =>
        new(key, "manpower", en, bn, $"Active people, counted by {field.Replace('_', ' ')}.", "people", MxKpiShape.Rows,
            $"{{\"dataset\": \"{{d}}\", \"group_by\": [\"{field}\"], \"measures\": [[\"count\", \"*\", \"value\"]], \"sort\": [[\"value\", \"desc\"]], \"limit\": 500}}",
            ActiveOnly: true, GroupField: field, Kinds: People);

    private static MxKpi PerLine(string key, string en, string bn, string words, string measure) =>
        new(key, "lines", en, bn, words, "people", MxKpiShape.PerLine,
            $"{{\"dataset\": \"{{d}}\", \"filter\": [{Sewing}], \"group_by\": [\"line_no\"], \"measures\": [{measure}], \"sort\": [[\"line_no\", \"asc\"]], \"limit\": 500}}",
            ActiveOnly: true, GroupField: "line_no", Kinds: People);

    public static readonly IReadOnlyList<MxKpi> All = new[]
    {
        // ── Manpower ──
        new MxKpi("active_headcount", "manpower", "Active headcount", "সক্রিয় জনবল", "People whose status is Active.", "people", MxKpiShape.Value,
            "{\"dataset\": \"{d}\", \"measures\": [[\"count\", \"*\", \"value\"]]}", ActiveOnly: true, Kinds: People),
        new MxKpi("rows_total", "manpower", "Rows in the file", "ফাইলের সারি", "Every row of the dataset.", "people", MxKpiShape.Value,
            "{\"dataset\": \"{d}\", \"measures\": [[\"count\", \"*\", \"value\"]]}"),
        By("headcount_by_unit", "unit", "Headcount by unit", "ইউনিট অনুযায়ী জনবল"),
        By("headcount_by_department", "department", "Headcount by department", "বিভাগ অনুযায়ী জনবল"),
        By("headcount_by_section", "section", "Headcount by section", "সেকশন অনুযায়ী জনবল"),
        By("headcount_by_line_type", "line_type", "Headcount by line type", "লাইনের ধরন অনুযায়ী জনবল"),
        By("headcount_by_designation", "designation", "Headcount by designation", "পদবি অনুযায়ী জনবল"),
        By("headcount_by_grade", "grade_id", "Headcount by grade", "গ্রেড অনুযায়ী জনবল"),
        By("headcount_by_emp_type", "emp_type", "Headcount by employee type", "কর্মীর ধরন অনুযায়ী জনবল"),
        By("headcount_by_gender", "gender", "Headcount by gender", "লিঙ্গ অনুযায়ী জনবল"),
        By("tenure_band_distribution", "tenure_band", "Tenure bands", "চাকরিকালের পরিসর"),
        new MxKpi("headcount_by_line", "lines", "Headcount by sewing line", "সেলাই লাইন অনুযায়ী জনবল", "Active people of section Sewing, by line.", "people", MxKpiShape.Rows,
            $"{{\"dataset\": \"{{d}}\", \"filter\": [{Sewing}], \"group_by\": [\"line_no\"], \"measures\": [[\"count\", \"*\", \"value\"]], \"sort\": [[\"line_no\", \"asc\"]], \"limit\": 500}}",
            ActiveOnly: true, GroupField: "line_no", Kinds: People),
        new MxKpi("female_pct", "manpower", "Female %", "নারী %", "Active women ÷ active people × 100.", "%", MxKpiShape.Value,
            "{\"dataset\": \"{d}\", \"measures\": [[\"pct\", [\"count_if\", [\"gender\", \"=\", \"Female\"]], [\"count\", \"*\"], \"value\"]]}", ActiveOnly: true, Kinds: People),
        new MxKpi("mgmt_worker_ratio", "manpower", "Management : worker ratio", "ব্যবস্থাপনা : শ্রমিক অনুপাত",
            "Active Management and Non-Management people ÷ active Schedule-A and Schedule-B workers.", "ratio", MxKpiShape.Value,
            $"{{\"dataset\": \"{{d}}\", \"measures\": [[\"ratio\", [\"count_if\", {Staff}], [\"count_if\", {Workers}], \"value\"]]}}", ActiveOnly: true, Kinds: People),

        // ── Lines ──
        new MxKpi("sewing_lines_per_unit", "lines", "Sewing lines per unit", "ইউনিট অনুযায়ী সেলাই লাইন", "Distinct sewing line numbers among active people, by unit.", "lines", MxKpiShape.Rows,
            $"{{\"dataset\": \"{{d}}\", \"filter\": [{Sewing}], \"group_by\": [\"unit\"], \"measures\": [[\"count_distinct\", \"line_no\", \"value\"]], \"sort\": [[\"value\", \"desc\"]], \"limit\": 500}}",
            ActiveOnly: true, GroupField: "unit", Kinds: People),
        new MxKpi("sewing_lines_total", "lines", "Sewing lines", "সেলাই লাইন", "Distinct sewing line numbers among active people.", "lines", MxKpiShape.Value,
            $"{{\"dataset\": \"{{d}}\", \"filter\": [{Sewing}], \"measures\": [[\"count_distinct\", \"line_no\", \"value\"]]}}", ActiveOnly: true, Kinds: People),
        PerLine("operators_per_line", "Operators per line", "লাইনপ্রতি অপারেটর", "Active people whose designation contains “Operator”, per sewing line; with the lowest, median and highest line.",
            "[\"count_if\", [\"designation\", \"contains\", \"Operator\"], \"value\"]"),
        PerLine("helpers_per_line", "Helpers per line", "লাইনপ্রতি হেলপার", "Active people whose designation contains “Helper”, per sewing line.",
            "[\"count_if\", [\"designation\", \"contains\", \"Helper\"], \"value\"]"),
        PerLine("qc_per_line", "QC per line", "লাইনপ্রতি কিউসি", "Active Quality Inspectors, Quality Controllers and Checkers, per sewing line.",
            "[\"count_if\", [\"designation\", \"contains_any\", [\"Quality Inspector\", \"Quality Controller\", \"Checker\"]], \"value\"]"),
        PerLine("supervisors_per_line", "Supervisors per line", "লাইনপ্রতি সুপারভাইজার", "Active people whose designation contains “Supervisor”, per sewing line.",
            "[\"count_if\", [\"designation\", \"contains\", \"Supervisor\"], \"value\"]"),
        new MxKpi("op_helper_ratio_per_line", "lines", "Operator : helper ratio per line", "লাইনপ্রতি অপারেটর : হেলপার", "Operators ÷ helpers, per sewing line.", "ratio", MxKpiShape.PerLine,
            $"{{\"dataset\": \"{{d}}\", \"filter\": [{Sewing}], \"group_by\": [\"line_no\"], \"measures\": [[\"ratio\", [\"count_if\", [\"designation\", \"contains\", \"Operator\"]], [\"count_if\", [\"designation\", \"contains\", \"Helper\"]], \"value\"]], \"limit\": 500}}",
            ActiveOnly: true, GroupField: "line_no", Kinds: People),
        new MxKpi("lines_below_operator_count", "lines", "Lines below the operator count", "কম অপারেটরের লাইন", "Sewing lines with fewer operators than the threshold (default 20).", "lines", MxKpiShape.LinesBelow,
            "", Kinds: People),

        // ── Joiners and separations ──
        new MxKpi("joiners_in_period", "movement", "New joiners in the period", "সময়কালে নতুন যোগদান", "People whose joining date falls in the period (default: the 30 days to the report date).", "people", MxKpiShape.Value,
            "{\"dataset\": \"{d}\", \"filter\": [[\"joining_date\", \"between\", \"{from}\", \"{to}\"]], \"measures\": [[\"count\", \"*\", \"value\"]]}", Kinds: People),
        new MxKpi("separations_total", "movement", "Separations", "বিচ্ছেদ", "People whose status is not Active.", "people", MxKpiShape.Value,
            "{\"dataset\": \"{d}\", \"filter\": [[\"status\", \"not_null\"], [\"status\", \"not_in\", [\"Active\"]]], \"measures\": [[\"count\", \"*\", \"value\"]]}", Kinds: People),
        new MxKpi("separations_by_status", "movement", "Separations by status", "অবস্থা অনুযায়ী বিচ্ছেদ", "People whose status is not Active, by status (Left, Resignation, Removal, …).", "people", MxKpiShape.Rows,
            "{\"dataset\": \"{d}\", \"filter\": [[\"status\", \"not_null\"], [\"status\", \"not_in\", [\"Active\"]]], \"group_by\": [\"status\"], \"measures\": [[\"count\", \"*\", \"value\"]], \"sort\": [[\"value\", \"desc\"]], \"limit\": 500}",
            GroupField: "status", Kinds: People),
        new MxKpi("separations_by_tenure_band", "movement", "Separations by tenure band", "চাকরিকাল অনুযায়ী বিচ্ছেদ", "Separated people, by tenure band.", "people", MxKpiShape.Rows,
            "{\"dataset\": \"{d}\", \"filter\": [[\"status\", \"not_null\"], [\"status\", \"not_in\", [\"Active\"]]], \"group_by\": [\"tenure_band\"], \"measures\": [[\"count\", \"*\", \"value\"]], \"limit\": 500}",
            GroupField: "tenure_band", Kinds: People),
        new MxKpi("joiners_since_previous", "movement", "Joiners since the previous snapshot", "আগের তালিকার পর যোগদান", "IDs in this snapshot that the previous one does not have.", "people", MxKpiShape.Value,
            "{\"dataset\": \"{d}\", \"filter\": [[\"emp_id\", \"not_in_dataset\", \"{prev}\"]], \"measures\": [[\"count\", \"*\", \"value\"]]}", Kinds: People),
        new MxKpi("separations_since_previous", "movement", "Separations since the previous snapshot", "আগের তালিকার পর বিচ্ছেদ", "IDs in the previous snapshot that this one no longer has.", "people", MxKpiShape.Value,
            "{\"dataset\": \"{prev}\", \"filter\": [[\"emp_id\", \"not_in_dataset\", \"{d}\"]], \"measures\": [[\"count\", \"*\", \"value\"]]}", Kinds: People),
        new MxKpi("attrition_pct", "movement", "Attrition %", "কর্মী ঝরে পড়ার হার %", "Separations since the previous snapshot ÷ the average of its and this snapshot's active headcount × 100.", "%", MxKpiShape.Attrition,
            "", Kinds: People),

        // ── Tenure and grade ──
        new MxKpi("headcount_by_equivalency_grade", "grade", "Staff by equivalency grade", "সমতুল্য গ্রেড অনুযায়ী কর্মকর্তা", "Active Management and Non-Management people, by the grade the designation lookup gives their designation.", "people", MxKpiShape.Rows,
            $"{{\"dataset\": \"{{d}}\", \"join\": [{{\"dataset\": \"current:designation-lookup\", \"on\": \"designation\"}}], \"filter\": [{Staff}], \"group_by\": [\"designation-lookup.equivalency_grade\"], \"measures\": [[\"count\", \"*\", \"value\"]], \"limit\": 500}}",
            ActiveOnly: true, GroupField: "designation-lookup.equivalency_grade", Kinds: People),
        new MxKpi("headcount_without_grade_mapping", "grade", "Staff without a grade mapping", "গ্রেড মেলেনি এমন কর্মকর্তা", "Active Management and Non-Management people whose designation is not in the lookup.", "people", MxKpiShape.Value,
            $"{{\"dataset\": \"{{d}}\", \"join\": [{{\"dataset\": \"current:designation-lookup\", \"on\": \"designation\"}}], \"filter\": [{Staff}, [\"designation-lookup.designation\", \"is_null\"]], \"measures\": [[\"count\", \"*\", \"value\"]]}}",
            ActiveOnly: true, Kinds: People),
        new MxKpi("designations_by_grade", "grade", "Designations by grade", "গ্রেড অনুযায়ী পদবি", "Designations in the equivalency lookup, by grade.", "people", MxKpiShape.Rows,
            "{\"dataset\": \"{d}\", \"group_by\": [\"equivalency_grade\"], \"measures\": [[\"count\", \"*\", \"value\"]], \"limit\": 500}", GroupField: "equivalency_grade", Kinds: Lookup),
        new MxKpi("grades_count", "grade", "Grades", "গ্রেড", "Distinct equivalency grades in the lookup.", "grades", MxKpiShape.Value,
            "{\"dataset\": \"{d}\", \"measures\": [[\"count_distinct\", \"equivalency_grade\", \"value\"]]}", Kinds: Lookup),

        // ── Attendance (daily) ──
        new MxKpi("att_present", "attendance", "Present", "উপস্থিত", "Status P or LT (late counts as present).", "people", MxKpiShape.Value,
            $"{{\"dataset\": \"{{d}}\", \"measures\": [[\"count_if\", {Present}, \"value\"]]}}", Kinds: Att),
        new MxKpi("att_late", "attendance", "Late", "দেরিতে উপস্থিত", "Status LT.", "people", MxKpiShape.Value,
            "{\"dataset\": \"{d}\", \"measures\": [[\"count_if\", [\"att_status\", \"=\", \"LT\"], \"value\"]]}", Kinds: Att),
        new MxKpi("late_pct", "attendance", "Late %", "দেরি %", "Late ÷ present × 100.", "%", MxKpiShape.Value,
            $"{{\"dataset\": \"{{d}}\", \"measures\": [[\"pct\", [\"count_if\", [\"att_status\", \"=\", \"LT\"]], [\"count_if\", {Present}], \"value\"]]}}", Kinds: Att),
        new MxKpi("present_by_department", "attendance", "Present by department", "বিভাগ অনুযায়ী উপস্থিত", "Present people (P or LT), by department.", "people", MxKpiShape.Rows,
            $"{{\"dataset\": \"{{d}}\", \"group_by\": [\"department\"], \"measures\": [[\"count_if\", {Present}, \"value\"]], \"sort\": [[\"value\", \"desc\"]], \"limit\": 500}}", GroupField: "department", Kinds: Att),
        new MxKpi("attendance_pct", "attendance", "Attendance %", "উপস্থিতির হার %", "Present people ÷ the active manpower of the same units (from the master or manpower list) × 100.", "%", MxKpiShape.AttendancePct,
            "", Kinds: Att),
        new MxKpi("ot_total", "attendance", "OT hours", "মোট ওভারটাইম", "The sum of OT hours.", "hours", MxKpiShape.Value,
            "{\"dataset\": \"{d}\", \"measures\": [[\"sum\", \"ot_hours\", \"value\"]]}", Kinds: Att),
        new MxKpi("ot_avg", "attendance", "Average OT hours", "গড় ওভারটাইম", "OT hours ÷ people.", "hours", MxKpiShape.Value,
            "{\"dataset\": \"{d}\", \"measures\": [[\"avg\", \"ot_hours\", \"value\"]]}", Kinds: Att),
        new MxKpi("ot_over_2h", "attendance", "People with OT above 2 h", "২ ঘণ্টার বেশি ওভারটাইম", "People whose OT hours are more than 2.", "people", MxKpiShape.Value,
            "{\"dataset\": \"{d}\", \"measures\": [[\"count_if\", [\"ot_hours\", \">\", 2], \"value\"]]}", Kinds: Att),
        new MxKpi("ot_over_4h", "attendance", "People with OT above 4 h", "৪ ঘণ্টার বেশি ওভারটাইম", "People whose OT hours are more than 4.", "people", MxKpiShape.Value,
            "{\"dataset\": \"{d}\", \"measures\": [[\"count_if\", [\"ot_hours\", \">\", 4], \"value\"]]}", Kinds: Att),
        new MxKpi("shift_mix", "attendance", "Shift mix", "শিফট অনুযায়ী", "People by shift.", "people", MxKpiShape.Rows,
            "{\"dataset\": \"{d}\", \"group_by\": [\"shift\"], \"measures\": [[\"count\", \"*\", \"value\"]], \"sort\": [[\"value\", \"desc\"]], \"limit\": 500}", GroupField: "shift", Kinds: Att),
        new MxKpi("earliest_in", "attendance", "Earliest in", "সবার আগে প্রবেশ", "The earliest in time.", "time", MxKpiShape.Value,
            "{\"dataset\": \"{d}\", \"measures\": [[\"min\", \"in_time\", \"value\"]]}", Kinds: Att),
        new MxKpi("latest_out", "attendance", "Latest out", "সবার শেষে প্রস্থান", "The latest out time.", "time", MxKpiShape.Value,
            "{\"dataset\": \"{d}\", \"measures\": [[\"max\", \"out_time\", \"value\"]]}", Kinds: Att),

        // ── Salary (permission) ──
        new MxKpi("gross_by_unit", "salary", "Gross payroll by unit", "ইউনিট অনুযায়ী মোট বেতন", "The sum of active people's gross salary, by unit.", "tk", MxKpiShape.Rows,
            "{\"dataset\": \"{d}\", \"group_by\": [\"unit\"], \"measures\": [[\"sum\", \"gross\", \"value\"]], \"sort\": [[\"value\", \"desc\"]], \"limit\": 500}", ActiveOnly: true, GroupField: "unit", Kinds: People),
        new MxKpi("avg_gross_by_designation", "salary", "Average gross by designation", "পদবি অনুযায়ী গড় বেতন", "Active people's gross salary averaged by designation.", "tk", MxKpiShape.Rows,
            "{\"dataset\": \"{d}\", \"group_by\": [\"designation\"], \"measures\": [[\"avg\", \"gross\", \"value\"]], \"sort\": [[\"value\", \"desc\"]], \"limit\": 500}", ActiveOnly: true, GroupField: "designation", Kinds: People),
        new MxKpi("increment_total", "salary", "Increment total", "মোট ইনক্রিমেন্ট", "The sum of increment amounts.", "tk", MxKpiShape.Value,
            "{\"dataset\": \"{d}\", \"measures\": [[\"sum\", \"increment_amount\", \"value\"]]}", Kinds: People),

        // ── Quality ──
        new MxKpi("open_issues_by_check", "quality", "Open quality issues", "খোলা ত্রুটি", "Open quality issues of the dataset, by check.", "issues", MxKpiShape.OpenIssues, ""),
    };

    private static readonly Dictionary<string, MxKpi> ByKey = All.ToDictionary(k => k.Key);

    public static MxKpi? Find(string? key) => key is not null && ByKey.TryGetValue(key, out var k) ? k : null;

    /// <summary>The middle value; the mean of the two middle ones for an even count.</summary>
    public static double? Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return null;
        var s = values.OrderBy(v => v).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }
}
