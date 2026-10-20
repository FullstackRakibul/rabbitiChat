// ম্যাট্রিক্স — quality checks at import (docs/MATRIX_PROMPT.md §3.5).
//
// Each check counts what it finds, keeps up to 20 sample cells (Sheet!A12) and
// the rows they are on (for the viewer's highlight), and says how to fix it.
// A fix never happens here: it is a separate, logged step that keeps the raw
// value in _raw_<column> (MatrixRepository.ApplyFixAsync).

using System.Globalization;
using System.Text.RegularExpressions;
using RabbitIC.Api.Hr;
using RabbitIC.Api.Models;

namespace RabbitIC.Api.Matrix;

public static class MxCheck
{
    public const string DupEmpId = "dup_emp_id";
    public const string CrossSheetPerson = "cross_sheet_person";
    public const string IdLeadingZeros = "id_leading_zeros";
    public const string NullText = "null_text";
    public const string MixedDateFormats = "mixed_date_formats";
    public const string SeparationBeforeJoining = "separation_before_joining";
    public const string DesignationNotInLookup = "designation_not_in_lookup";
    public const string TrailingSpaces = "trailing_spaces";
    public const string NearDuplicateValues = "near_duplicate_values";
    public const string NumbersAsDates = "numbers_as_dates";
    public const string EmptyRequired = "empty_required";
    public const string FormulaMismatch = "formula_mismatch";

    public const string FixTrim = "trim";
    public const string FixUnify = "unify";
    public const string FixPadIds = "pad-ids";
}

/// <summary>What the checks need beyond the rows: the other tables of the upload, and the designation lookup.</summary>
public sealed record MxCheckContext(
    IReadOnlyList<(string Sheet, HashSet<string> EmpIds)> OtherTables,
    IReadOnlySet<string>? LookupDesignations);

public static class QualityChecks
{
    private const int MaxSamples = 20;
    private static readonly string[] Required = { "emp_id", "unit", "status" };

    public static List<MxQualityIssue> Run(MxTableScan scan, MxImportBuild build, MxCheckContext context)
    {
        var issues = new List<MxQualityIssue>();
        var sheet = scan.Profile.Sheet;
        var rows = build.SourceRows;
        string Ref(MxDsColumn col, int i) => $"{sheet}!{MxCells.Letter(scan.FirstCol + col.SourceIndex!.Value)}{rows[i]}";
        MxDsColumn? Col(string field) => build.Columns.FirstOrDefault(c => c.Field == field && !c.Derived);
        List<MxValue> Raw(MxDsColumn c) => build.Raw[c.Key];

        void Add(string check, string severity, int count, MxDsColumn? col, IEnumerable<int> at, string detail, string fix, string? fixKind = null)
        {
            if (count <= 0) return;
            var idx = at.Take(MaxSamples).ToList();
            issues.Add(new MxQualityIssue
            {
                Check = check, Severity = severity, Count = count, Field = col?.Key, Detail = detail, SuggestedFix = fix, FixKind = fixKind,
                SampleRefsJson = Json(col is null ? idx.Select(i => $"{sheet}!row {rows[i]}").ToList() : idx.Select(i => Ref(col, i)).ToList()),
                SampleRowsJson = Json(idx.Select(i => rows[i]).ToList()),
            });
        }

        // ---- identity ----
        if (Col("emp_id") is { } emp)
        {
            var ids = Raw(emp).Select(WorkbookProfiler.KeyText).ToList();
            var seen = new Dictionary<string, int>();
            var dups = new List<int>();
            for (var i = 0; i < ids.Count; i++)
                if (ids[i] is { } id && !seen.TryAdd(id, i)) dups.Add(i);
            Add(MxCheck.DupEmpId, "error", dups.Count, emp, dups,
                $"{dups.Count} row(s) repeat an employee ID already in this table.",
                "Keep one row per person in the source file, or confirm the repeats are different people.");

            var byNumber = ids.Select((id, i) => (id, i)).Where(x => x.id is not null && x.id.All(char.IsAsciiDigit))
                .GroupBy(x => x.id!.TrimStart('0')).Where(gr => gr.Select(x => x.id).Distinct().Count() > 1).ToList();
            Add(MxCheck.IdLeadingZeros, "warning", byNumber.Count, emp,
                byNumber.SelectMany(gr => gr.Where(x => x.id!.Length < 6).Select(x => x.i)),
                $"{byNumber.Count} ID(s) appear both with and without leading zeros (e.g. 000512 and 512).",
                "Pad every ID to six digits.", MxCheck.FixPadIds);

            var mine = ids.Where(x => x is not null).Select(x => x!).ToHashSet();
            foreach (var (other, otherIds) in context.OtherTables)
            {
                var both = mine.Count(otherIds.Contains);
                var at = ids.Select((id, i) => (id, i)).Where(x => x.id is not null && otherIds.Contains(x.id)).Select(x => x.i);
                Add(MxCheck.CrossSheetPerson, "info", both, emp, at,
                    $"{both} people in this table are also in sheet '{other}' of the same upload.",
                    "Import one of the two, or confirm both are needed.");
            }
        }

        // ---- per column ----
        var nullText = 0;
        var nullAt = new List<int>();
        MxDsColumn? nullCol = null;
        foreach (var col in build.Columns.Where(c => !c.Derived))
        {
            var raw = Raw(col);
            for (var i = 0; i < raw.Count; i++)
                if (raw[i].Kind == MxValueKind.NullText) { nullText++; if (nullAt.Count < MaxSamples) { nullAt.Add(i); nullCol ??= col; } }

            if (col.Type == MxType.Date)
            {
                var formats = raw.Select((v, i) => (v, i)).Where(x => x.v.Kind == MxValueKind.Date).GroupBy(x => x.v.DateFormat).OrderByDescending(gr => gr.Count()).ToList();
                if (formats.Count > 1)
                {
                    var minority = formats.Skip(1).SelectMany(gr => gr.Select(x => x.i)).ToList();
                    Add(MxCheck.MixedDateFormats, "warning", minority.Count, col, minority,
                        $"{col.Header}: {string.Join(", ", formats.Select(gr => $"{gr.Key} ×{gr.Count()}"))}. All were read as dates.",
                        "Use one date format in the source file.");
                }
            }

            if (col.Type is MxType.Text && FieldCatalog.Find(col.Field)?.Role is not MxRole.Id)
            {
                var spaced = raw.Select((v, i) => (v, i)).Where(x => x.v.Kind == MxValueKind.Text && x.v.Text is { } s && s != s.Trim()).Select(x => x.i).ToList();
                Add(MxCheck.TrailingSpaces, "warning", spaced.Count, col, spaced,
                    $"{spaced.Count} {col.Header} value(s) have leading or trailing spaces, so they group apart from the same value without them.",
                    "Trim the spaces.", MxCheck.FixTrim);
            }

            if (col.Type is MxType.Text && FieldCatalog.Find(col.Field)?.Role == MxRole.Dimension)
            {
                var groups = raw.Select((v, i) => (v, i)).Where(x => x.v.Kind == MxValueKind.Text && !string.IsNullOrWhiteSpace(x.v.Text))
                    .GroupBy(x => FieldCatalog.Normalise(x.v.Text)).Where(gr => gr.Select(x => x.v.Text!.Trim()).Distinct().Count() > 1).ToList();
                var variants = groups.Select(gr => string.Join(" / ", gr.Select(x => x.v.Text!.Trim()).Distinct().Select(s => $"\"{s}\"")));
                Add(MxCheck.NearDuplicateValues, "warning", groups.Count, col,
                    groups.SelectMany(gr => { var top = gr.GroupBy(x => x.v.Text!.Trim()).OrderByDescending(x => x.Count()).First().Key; return gr.Where(x => x.v.Text!.Trim() != top).Select(x => x.i); }),
                    $"{col.Header}: {string.Join("; ", variants)} look like the same value written differently.",
                    "Use one spelling — the most common one.", MxCheck.FixUnify);
            }

            if (col.Type is MxType.Integer or MxType.Real)
            {
                var asDates = raw.Select((v, i) => (v, i)).Where(x => x.v.Kind == MxValueKind.Date).Select(x => x.i).ToList();
                Add(MxCheck.NumbersAsDates, "warning", asDates.Count, col, asDates,
                    $"{asDates.Count} {col.Header} number(s) are formatted as dates in the file; their numbers were used.",
                    "Format the column as numbers in the source file.");
            }
        }
        Add(MxCheck.NullText, "info", nullText, null, nullAt,
            $"{nullText} cell(s) hold the text \"NULL\" or \"-\"; they were read as empty.",
            "Leave empty cells empty in the source report.");

        foreach (var field in Required)
        {
            if (Col(field) is not { } col) continue;
            var empty = Raw(col).Select((v, i) => (v, i)).Where(x => x.v.Kind is MxValueKind.Null or MxValueKind.NullText).Select(x => x.i).ToList();
            Add(MxCheck.EmptyRequired, "error", empty.Count, col, empty,
                $"{empty.Count} row(s) have no {col.Header}.", $"Fill in {col.Header} for every row.");
        }

        // ---- dates that disagree ----
        if (Col("joining_date") is { } join && (Col("separation_date") ?? Col("effect_date")) is { } sep)
        {
            var bad = new List<int>();
            for (var i = 0; i < rows.Count; i++)
                if (Iso(Raw(join)[i]) is { } j && Iso(Raw(sep)[i]) is { } s && string.CompareOrdinal(s, j) < 0) bad.Add(i);
            Add(MxCheck.SeparationBeforeJoining, "error", bad.Count, sep, bad,
                $"{bad.Count} row(s) have a {sep.Header} earlier than the joining date.", "Correct the dates in the source system.");
        }

        // ---- the designation lookup ----
        if (context.LookupDesignations is { Count: > 0 } lookup && Col("designation") is { } des)
        {
            var missing = Raw(des).Select((v, i) => (Name: v.Text?.Trim(), i)).Where(x => !string.IsNullOrEmpty(x.Name) && !lookup.Contains(x.Name!.ToLowerInvariant()))
                .GroupBy(x => x.Name!, StringComparer.OrdinalIgnoreCase).ToList();
            Add(MxCheck.DesignationNotInLookup, "warning", missing.Count, des, missing.Select(gr => gr.First().i),
                $"{missing.Count} designation(s) are not in the equivalency lookup: {string.Join(", ", missing.Take(10).Select(gr => $"\"{gr.Key}\" ×{gr.Count()}"))}.",
                "Add them to the designation equivalency list, or correct their spelling.");
        }

        // ---- formulas whose cached result disagrees with their inputs ----
        foreach (var col in build.Columns.Where(c => !c.Derived && c.SourceIndex is not null))
        {
            var gridCol = scan.FirstCol + col.SourceIndex!.Value;
            var bad = new List<int>();
            var checkedAny = false;
            for (var i = 0; i < rows.Count; i++)
            {
                var cell = scan.Grid.Cell(rows[i], gridCol);
                if (cell?.Formula is not { } f || cell.Type != ClosedXML.Excel.XLDataType.Number) continue;
                if (Recompute(scan.Grid, rows[i], f) is not { } value) continue;
                checkedAny = true;
                if (Math.Abs((double)value - cell.Number) > 0.01) bad.Add(i);
            }
            if (checkedAny)
                Add(MxCheck.FormulaMismatch, "warning", bad.Count, col, bad,
                    $"{bad.Count} {col.Header} value(s) differ from what their formula gives with the row's own inputs.",
                    "Recalculate the sheet in Excel before exporting, or correct the inputs.");
        }

        return issues;
    }

    private static readonly Regex CellRef = new(@"\$?([A-Z]{1,3})\$?(\d+)");

    /// <summary>
    /// A same-row formula such as (R2-2450)/1.5, evaluated with that row's cached
    /// inputs by the HR evaluator (HrCalculator). Null when the formula reads
    /// another row or uses anything but arithmetic — those are not checked.
    /// </summary>
    public static decimal? Recompute(MxGrid g, int row, string formula)
    {
        var ok = true;
        var expr = CellRef.Replace(formula, m =>
        {
            if (int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) != row) { ok = false; return "0"; }
            var c = ClosedXML.Excel.XLHelper.GetColumnNumberFromLetter(m.Groups[1].Value);
            var cell = g.Cell(row, c);
            if (cell is null || cell.IsEmpty) return "0";
            if (cell.Type != ClosedXML.Excel.XLDataType.Number) { ok = false; return "0"; }
            return cell.Number.ToString("0.##########", CultureInfo.InvariantCulture);
        });
        if (!ok) return null;
        var (value, error) = HrCalculator.Evaluate(expr);
        return error is null ? value : null;
    }

    private static string? Iso(MxValue v) => v.Kind == MxValueKind.Date ? v.Iso : null;

    private static string Json<T>(T value) => System.Text.Json.JsonSerializer.Serialize(value);
}
