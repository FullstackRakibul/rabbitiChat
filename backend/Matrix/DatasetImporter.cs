// ম্যাট্রিক্স — a profiled table → typed rows for a dataset table (docs/MATRIX_PROMPT.md §3.2, §3.5).
//
// Columns become the catalog field they were mapped to (the field's key is the
// column name), or col_<letter> when kept unmapped. Sensitive columns are not
// imported at all — date of birth is read once for age_band first. Values are
// typed by the same rules the profiler used (MxCells): "NULL" and "-" become
// null, text dates ISO, "07:54:20 AM " 07:54:20, IDs keep their leading zeros.
// Text keeps its spaces: trimming is a fix, logged, never silent. Derived fields
// (line_no, line_type, punch_emp_id, tenure, bands) are added here.

using System.Globalization;
using System.Text.RegularExpressions;
using RabbitIC.Api.DTOs.Matrix;

namespace RabbitIC.Api.Matrix;

/// <summary>One column of a dataset table. <see cref="Key"/> is its SQL name — from the catalog or code, never from a header.</summary>
public sealed record MxDsColumn(
    string Key, string Header, string? Field, string Type, string Sensitivity, string? Unit, bool Derived, int? SourceIndex);

public sealed class MxImportBuild
{
    public List<MxDsColumn> Columns = new();
    public List<object?[]> Rows = new();

    /// <summary>The sheet row each dataset row came from (its _row).</summary>
    public List<int> SourceRows = new();
    public List<MxDroppedDto> Dropped = new();

    /// <summary>The typed source values by dataset column key, row-aligned — what the quality checks read.</summary>
    public Dictionary<string, List<MxValue>> Raw = new();
}

public sealed record MxColumnDecision(int Index, string? Field, string Sensitivity, bool Keep);

public static class DatasetImporter
{
    public static readonly Regex SafeName = new(@"^[a-z_][a-z0-9_]{0,62}$");

    /// <summary>
    /// The decisions for every column: a mapped field, "keep" (unmapped, personal
    /// unless classified), or left out. Sensitive columns are left out unless an
    /// administrator allowed them for the template.
    /// </summary>
    public static MxImportBuild Build(MxTableScan scan, IReadOnlyList<MxColumnDecision> decisions, IReadOnlySet<int> allowedSensitive, string reportDate)
    {
        var t = scan.Profile;
        var g = scan.Grid;
        var build = new MxImportBuild();
        var used = new HashSet<string>();
        var plans = new List<(MxDsColumn Col, int GridCol)>();
        int? dobCol = null;

        foreach (var col in t.Columns)
        {
            var gridCol = scan.FirstCol + col.Index;
            var decision = decisions.FirstOrDefault(d => d.Index == col.Index);

            if (col.Sensitivity == MxSensitivity.Sensitive && !allowedSensitive.Contains(col.Index))
            {
                if (FieldCatalog.IsDateOfBirth(col.Header)) dobCol = gridCol;
                build.Dropped.Add(new MxDroppedDto(col.Header, col.SensitiveCategory ?? "sensitive"));
                continue;
            }
            if (decision is null || (decision.Field is null && !decision.Keep)) continue;

            var field = FieldCatalog.Find(decision.Field);
            var key = field?.Key ?? "col_" + col.Letter.ToLowerInvariant();
            if (!used.Add(key)) { var n = 2; while (!used.Add($"{key}_{n}")) n++; key = $"{key}_{n}"; }
            if (!SafeName.IsMatch(key)) throw new InvalidOperationException($"Unsafe column name '{key}'.");

            var sensitivity = col.Sensitivity == MxSensitivity.Sensitive ? MxSensitivity.Sensitive
                : field?.Sensitivity ?? (MxSensitivity.All.Contains(decision.Sensitivity) ? decision.Sensitivity : MxSensitivity.Personal);
            var type = field?.Type ?? col.Type;
            plans.Add((new MxDsColumn(key, col.Header, field?.Key, type, sensitivity, col.Unit, false, col.Index), gridCol));
        }

        build.Columns.AddRange(plans.Select(p => p.Col));
        foreach (var p in plans) build.Raw[p.Col.Key] = new List<MxValue>(t.DataRows);

        // ---- derived columns ----
        bool Has(string f) => plans.Any(p => p.Col.Field == f);
        var derived = new List<MxDsColumn>();
        void Derive(string key)
        {
            var f = FieldCatalog.Find(key)!;
            if (used.Add(key)) derived.Add(new MxDsColumn(key, f.En, key, f.Type, f.Sensitivity, null, true, null));
        }
        if (Has("wing") || Has("line_name")) Derive("line_no");
        if (Has("section")) Derive("line_type");
        // PunchNo reaches emp_id only through the normaliser (§3.3).
        if (Has("punch_no")) Derive(Has("emp_id") ? "punch_emp_id" : "emp_id");
        if (Has("joining_date")) { Derive("tenure_years"); Derive("tenure_band"); }
        if (dobCol is not null) Derive("age_band");
        build.Columns.AddRange(derived);

        var refDate = DateTime.TryParse(reportDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var rd) ? rd : DateTime.UtcNow.Date;
        var index = build.Columns.Select((c, i) => (c.Key, i)).ToDictionary(x => x.Key, x => x.i);

        for (var r = t.FirstDataRow; r <= t.LastDataRow; r++)
        {
            if (g.RowEmpty(r)) continue;
            var row = new object?[build.Columns.Count];
            foreach (var (col, gridCol) in plans)
            {
                var v = MxCells.Classify(g.Cell(r, gridCol));
                build.Raw[col.Key].Add(v);
                var stored = Convert(v, col.Type);
                // "3" and "Grade 3" are one grade (QualityChecks counts the cells rewritten).
                row[index[col.Key]] = col.Field == "grade_id" && stored is string s ? MatrixNormalisers.Grade(s) : stored;
            }

            string? Text(string key) => index.TryGetValue(key, out var i) ? row[i] as string : null;
            void Set(string key, object? value) { if (index.TryGetValue(key, out var i) && build.Columns[i].Derived) row[i] = value; }

            var lineSource = Text("wing") ?? Text("line_name");
            Set("line_no", FieldCatalog.LineNumber(lineSource));
            Set("line_type", FieldCatalog.LineType(Text("section")));
            if (Has("punch_no"))
            {
                var padded = MatrixNormalisers.PadEmpId(Text("punch_no"));
                Set(Has("emp_id") ? "punch_emp_id" : "emp_id", padded);
            }
            if (Text("joining_date") is { } joined && DateTime.TryParse(joined, CultureInfo.InvariantCulture, DateTimeStyles.None, out var jd))
            {
                var years = Math.Round((refDate - jd).TotalDays / 365.25, 2);
                Set("tenure_years", years);
                Set("tenure_band", years >= 0 ? FieldCatalog.TenureBand(years) : null);
            }
            if (dobCol is not null && MxCells.Classify(g.Cell(r, dobCol.Value)) is { Kind: MxValueKind.Date, Iso: { } dob }
                && DateTime.TryParse(dob, CultureInfo.InvariantCulture, DateTimeStyles.None, out var born))
            {
                var age = refDate.Year - born.Year - (refDate.DayOfYear < born.DayOfYear ? 1 : 0);
                Set("age_band", FieldCatalog.AgeBand(age));
            }

            build.Rows.Add(row);
            build.SourceRows.Add(r);
        }
        return build;
    }

    /// <summary>A typed value as it is stored: TEXT, INTEGER or REAL; ISO text for dates and times.</summary>
    public static object? Convert(MxValue v, string type)
    {
        if (v.Kind is MxValueKind.Null or MxValueKind.NullText) return null;
        switch (type)
        {
            case MxType.Integer:
            case MxType.Real:
                if (v.Kind is MxValueKind.Number or MxValueKind.Date or MxValueKind.Bool)
                    return type == MxType.Integer && Math.Abs(v.Number - Math.Round(v.Number)) < 1e-9 ? (object)(long)Math.Round(v.Number) : v.Number;
                if (v.Kind == MxValueKind.LeadingZeroId && double.TryParse(v.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return n;
                return null;
            case MxType.Date:
                if (v.Kind == MxValueKind.Date) return v.Iso;
                // A plain serial in a date column: Excel's day count.
                if (v.Kind == MxValueKind.Number && v.Number is > 10000 and < 80000) return DateTime.FromOADate(v.Number).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                return null;
            case MxType.Time:
                return v.Kind == MxValueKind.Time ? v.Iso : null;
            case MxType.Bool:
                if (v.Kind == MxValueKind.Bool) return (long)v.Number;
                return v.Text?.Trim().ToLowerInvariant() switch { "yes" or "true" or "y" or "1" => 1L, "no" or "false" or "n" or "0" => 0L, _ => null };
            default:
                return v.Kind switch
                {
                    MxValueKind.Number => WorkbookProfiler.KeyText(v),
                    MxValueKind.Date or MxValueKind.Time => v.Iso,
                    _ => v.Text,
                };
        }
    }

    public static string SqlType(string type) => type switch
    {
        MxType.Integer or MxType.Bool => "INTEGER",
        MxType.Real => "REAL",
        _ => "TEXT",
    };
}
