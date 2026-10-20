// ম্যাট্রিক্স — reading a workbook's shape (docs/MATRIX_PROMPT.md §3.5).
//
// sheets → table blocks → header rows → column types → quirks. A block is a run
// of non-empty rows; its header is the first row of mostly short, distinct texts;
// the lines above it (a merged title, a "(19-May-2026 To 19-May-2026)" range)
// become report details. A second header row under a merged group header is
// joined to it ("Total Machine" + "Hours"). Types come from every value in the
// column, formulas from their cached results. Tables that hold mostly the same
// people (by emp_id) are flagged as copies of one another.
//
// The profile carries header texts, counts and quirks — never a cell value.
// The cell rules here (MxCells) are the importer's too, so a column is typed the
// same way when it is profiled and when it is imported.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using RabbitIC.Api.DTOs.Matrix;

namespace RabbitIC.Api.Matrix;

/// <summary>One cell as read: its cached value (formulas never recalculated) and its formula, if any.</summary>
public sealed class MxRawCell
{
    public XLDataType Type;
    public string? Text;
    public double Number;
    public DateTime Date;
    public bool Bool;
    public string? Formula;

    public bool IsEmpty => Type == XLDataType.Blank || (Type == XLDataType.Text && string.IsNullOrWhiteSpace(Text));
}

/// <summary>A sheet's used cells, 1-based rows and columns, read once.</summary>
public sealed class MxGrid
{
    public string Name = "";
    public int FirstRow, LastRow, FirstCol, LastCol;
    public readonly Dictionary<int, MxRawCell?[]> Rows = new();

    /// <summary>Horizontal merges by top row: (row, firstCol, lastCol).</summary>
    public readonly List<(int Row, int FirstCol, int LastCol, int LastRow)> Merges = new();

    public MxRawCell? Cell(int r, int c) =>
        Rows.TryGetValue(r, out var row) && c >= FirstCol && c <= LastCol ? row[c - FirstCol] : null;

    public bool RowEmpty(int r) => !Rows.TryGetValue(r, out var row) || row.All(c => c is null || c.IsEmpty);

    public static MxGrid Read(IXLWorksheet ws)
    {
        var g = new MxGrid { Name = ws.Name };
        var used = ws.RangeUsed();
        if (used is null) return g;
        g.FirstRow = used.FirstRow().RowNumber();
        g.LastRow = used.LastRow().RowNumber();
        g.FirstCol = used.FirstColumn().ColumnNumber();
        g.LastCol = used.LastColumn().ColumnNumber();
        var width = g.LastCol - g.FirstCol + 1;

        foreach (var cell in ws.CellsUsed(XLCellsUsedOptions.Contents))
        {
            var r = cell.Address.RowNumber;
            var c = cell.Address.ColumnNumber;
            if (c < g.FirstCol || c > g.LastCol) continue;
            if (!g.Rows.TryGetValue(r, out var row)) g.Rows[r] = row = new MxRawCell?[width];

            // Never cell.Value on a formula: that would recalculate. The cached result is what the file says.
            var value = cell.HasFormula ? cell.CachedValue : cell.Value;
            var raw = new MxRawCell { Type = value.Type switch
            {
                XLDataType.Number => XLDataType.Number,
                XLDataType.DateTime => XLDataType.DateTime,
                XLDataType.Boolean => XLDataType.Boolean,
                XLDataType.Text => XLDataType.Text,
                XLDataType.TimeSpan => XLDataType.TimeSpan,
                XLDataType.Blank => XLDataType.Blank,
                _ => XLDataType.Text,
            } };
            switch (value.Type)
            {
                case XLDataType.Number: raw.Number = value.GetNumber(); break;
                case XLDataType.DateTime: raw.Date = value.GetDateTime(); raw.Number = raw.Date.ToOADate(); break;
                case XLDataType.Boolean: raw.Bool = value.GetBoolean(); break;
                case XLDataType.Text: raw.Text = value.GetText(); break;
                case XLDataType.TimeSpan: raw.Text = value.GetTimeSpan().ToString(@"hh\:mm\:ss"); raw.Type = XLDataType.Text; break;
                case XLDataType.Error: raw.Text = value.GetError().ToString(); break;
            }
            if (cell.HasFormula) raw.Formula = cell.FormulaA1;
            row[c - g.FirstCol] = raw;
        }

        foreach (var m in ws.MergedRanges)
            g.Merges.Add((m.FirstRow().RowNumber(), m.FirstColumn().ColumnNumber(), m.LastColumn().ColumnNumber(), m.LastRow().RowNumber()));
        return g;
    }

    /// <summary>A .csv file as one sheet: every cell text, typed later by the same rules as a workbook's text cells.</summary>
    public static MxGrid FromCsv(string name, string text)
    {
        var g = new MxGrid { Name = name, FirstRow = 1, FirstCol = 1 };
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else cell.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (ch == '\n' || ch == '\r')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = new List<string>();
            }
            else cell.Append(ch);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }

        g.LastRow = Math.Max(1, rows.Count);
        g.LastCol = Math.Max(1, rows.Count == 0 ? 1 : rows.Max(r => r.Count));
        for (var r = 0; r < rows.Count; r++)
        {
            var cells = new MxRawCell?[g.LastCol];
            for (var c = 0; c < rows[r].Count; c++)
                if (rows[r][c].Length > 0) cells[c] = new MxRawCell { Type = XLDataType.Text, Text = rows[r][c] };
            g.Rows[r + 1] = cells;
        }
        return g;
    }
}

/// <summary>What one cell is, for typing: shared by the profiler and the importer.</summary>
public enum MxValueKind { Null, NullText, Text, LeadingZeroId, Number, NumberAsDate, Date, Time, Bool }

public readonly record struct MxValue(MxValueKind Kind, string? Text, double Number, string? Iso, string? DateFormat);

public static class MxCells
{
    private static readonly string[] NullTokens = { "NULL", "null", "Null", "-", "--" };
    private static readonly Regex DdMmmYyyy = new(@"^\d{1,2}-[A-Za-z]{3}-\d{4}$");
    private static readonly Regex IsoDateTime = new(@"^\d{4}-\d{2}-\d{2}( \d{2}:\d{2}(:\d{2})?)?$");
    private static readonly Regex SlashDate = new(@"^\d{1,2}[/.]\d{1,2}[/.]\d{4}$");
    private static readonly Regex TimeText = new(@"^(\d{1,2}):(\d{2})(?::(\d{2}))?\s*([AaPp][Mm])?$");
    private static readonly Regex NumberText = new(@"^([+-]?\d{1,3}(?:,\d{3})+|[+-]?\d+)(\.\d+)?\s*([A-Za-z%৳]{1,5})?$");
    private static readonly Regex LeadingZero = new(@"^0\d+$");

    /// <summary>The cell as a typed value. Text keeps its spaces; trimming is a fix, never silent.</summary>
    public static MxValue Classify(MxRawCell? cell)
    {
        if (cell is null || cell.IsEmpty) return new(MxValueKind.Null, null, 0, null, null);
        switch (cell.Type)
        {
            case XLDataType.Number:
                return new(MxValueKind.Number, null, cell.Number, null, null);
            case XLDataType.DateTime:
                return new(MxValueKind.Date, null, cell.Number, cell.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "excel");
            case XLDataType.Boolean:
                return new(MxValueKind.Bool, cell.Bool ? "true" : "false", cell.Bool ? 1 : 0, null, null);
        }

        var text = cell.Text ?? "";
        var t = text.Trim();
        if (NullTokens.Contains(t)) return new(MxValueKind.NullText, text, 0, null, null);

        if (DdMmmYyyy.IsMatch(t) && DateTime.TryParseExact(t, new[] { "d-MMM-yyyy", "dd-MMM-yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d1))
            return new(MxValueKind.Date, text, 0, d1.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "dd-MMM-yyyy");
        if (IsoDateTime.IsMatch(t) && DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d2))
            return new(MxValueKind.Date, text, 0, d2.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), t.Length > 10 ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd");
        if (SlashDate.IsMatch(t) && DateTime.TryParseExact(t, new[] { "d/M/yyyy", "dd/MM/yyyy", "d.M.yyyy", "dd.MM.yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d3))
            return new(MxValueKind.Date, text, 0, d3.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "dd/MM/yyyy");

        var tm = TimeText.Match(t);
        if (tm.Success)
        {
            var h = int.Parse(tm.Groups[1].Value, CultureInfo.InvariantCulture);
            var m = int.Parse(tm.Groups[2].Value, CultureInfo.InvariantCulture);
            var s = tm.Groups[3].Success ? int.Parse(tm.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
            var ap = tm.Groups[4].Value.ToUpperInvariant();
            if (ap == "PM" && h < 12) h += 12;
            if (ap == "AM" && h == 12) h = 0;
            if (h < 24 && m < 60 && s < 60)
                return new(MxValueKind.Time, text, 0, $"{h:00}:{m:00}:{s:00}", null);
        }

        if (LeadingZero.IsMatch(t)) return new(MxValueKind.LeadingZeroId, text, 0, null, null);
        var nm = NumberText.Match(t);
        if (nm.Success && double.TryParse(nm.Groups[1].Value.Replace(",", "") + nm.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            return new(MxValueKind.Number, nm.Groups[3].Success ? nm.Groups[3].Value : null, n, null, null);

        return new(MxValueKind.Text, text, 0, null, null);
    }

    /// <summary>The column's type from all its values (≥ 90% agreeing; ID-like text stays text).</summary>
    public static (string Type, string? Unit) Decide(IReadOnlyCollection<MxValue> values)
    {
        var real = values.Where(v => v.Kind is not (MxValueKind.Null or MxValueKind.NullText)).ToList();
        if (real.Count == 0) return (MxType.Text, null);
        int Count(MxValueKind k) => real.Count(v => v.Kind == k);
        var dates = Count(MxValueKind.Date);
        var numbers = Count(MxValueKind.Number);
        double share(int n) => (double)n / real.Count;

        if (share(Count(MxValueKind.Time)) >= 0.9) return (MxType.Time, null);
        if (share(Count(MxValueKind.Bool)) >= 0.9) return (MxType.Bool, null);
        if (Count(MxValueKind.LeadingZeroId) > 0) return (MxType.Text, null);
        // A mostly numeric column keeps its few date-formatted numbers as numbers.
        if (numbers > 0 && share(numbers + dates) >= 0.999 && numbers >= dates * 4)
        {
            var units = real.Where(v => v.Kind == MxValueKind.Number && v.Text is not null).Select(v => v.Text!).Distinct().ToList();
            var whole = real.Where(v => v.Kind == MxValueKind.Number).All(v => Math.Abs(v.Number - Math.Round(v.Number)) < 1e-9);
            return (whole ? MxType.Integer : MxType.Real, units.Count == 1 ? units[0] : null);
        }
        if (share(dates) >= 0.9) return (MxType.Date, null);
        return (MxType.Text, null);
    }

    public static string Letter(int column) => XLHelper.GetColumnLetterFromNumber(column);
}

/// <summary>What the profiler learns beside the profile: never serialised, never shown.</summary>
public sealed class MxTableScan
{
    public required MxTableProfile Profile;
    public required MxGrid Grid;
    public required int FirstCol;
    public required int LastCol;

    /// <summary>emp_id values (trimmed text), for copies and the cross-sheet check.</summary>
    public HashSet<string> EmpIds = new();

    /// <summary>Up to five raw values per unmatched column, for the model — masked before they leave.</summary>
    public Dictionary<int, List<string>> Samples = new();
}

public static class WorkbookProfiler
{
    private static readonly Regex RangeLine = new(
        @"(\d{1,2}-[A-Za-z]{3}-\d{4})\s*(?:to|-|–)\s*(\d{1,2}-[A-Za-z]{3}-\d{4})", RegexOptions.IgnoreCase);
    private static readonly Regex DateLine = new(@"(?:date|dated|as on|for)\s*[:\-]?\s*(\d{1,2}-[A-Za-z]{3}-\d{4})", RegexOptions.IgnoreCase);
    private static readonly Regex CopyName = new(@"\(\d+\)\s*$|\bcopy\b|correct|corrcet|\bold\b|backup|\btemp\b", RegexOptions.IgnoreCase);

    public const double CopyThresholdPct = 80;

    /// <summary>Profiles a workbook. The scans hold the grids, for the importer and the checks; the profile holds no values.</summary>
    public static (MxProfile Profile, List<MxTableScan> Scans) Profile(Stream file, string fileName)
    {
        IEnumerable<MxGrid> grids;
        XLWorkbook? wb = null;
        if (Path.GetExtension(fileName).Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            using var reader = new StreamReader(file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            grids = new[] { MxGrid.FromCsv(Path.GetFileNameWithoutExtension(fileName), reader.ReadToEnd()) };
        }
        else
        {
            wb = new XLWorkbook(file);
            grids = wb.Worksheets.Select(MxGrid.Read);
        }
        using var dispose = wb;
        var profile = new MxProfile { FileName = fileName };
        var scans = new List<MxTableScan>();

        var sheetIndex = 0;
        foreach (var grid in grids)
        {
            var sheet = new MxSheetProfile
            {
                Index = sheetIndex,
                Name = grid.Name,
                UsedRange = grid.Rows.Count == 0 ? null : $"{MxCells.Letter(grid.FirstCol)}{grid.FirstRow}:{MxCells.Letter(grid.LastCol)}{grid.LastRow}",
                Rows = grid.Rows.Count == 0 ? 0 : grid.LastRow - grid.FirstRow + 1,
                Cols = grid.Rows.Count == 0 ? 0 : grid.LastCol - grid.FirstCol + 1,
            };
            var tableIndex = 0;
            foreach (var block in Blocks(grid))
            {
                var scan = ProfileBlock(grid, block.First, block.Last, $"{sheetIndex}.{tableIndex}");
                if (scan is null) continue;
                sheet.Tables.Add(scan.Profile);
                scans.Add(scan);
                tableIndex++;
            }
            profile.Sheets.Add(sheet);
            sheetIndex++;
        }

        FlagCopies(scans);
        profile.ReportDate = scans.Select(s => s.Profile.ReportDate).FirstOrDefault(d => d is not null);
        return (profile, scans);
    }

    /// <summary>Runs of non-empty rows.</summary>
    private static IEnumerable<(int First, int Last)> Blocks(MxGrid g)
    {
        if (g.Rows.Count == 0) yield break;
        int? start = null;
        for (var r = g.FirstRow; r <= g.LastRow + 1; r++)
        {
            var empty = r > g.LastRow || g.RowEmpty(r);
            if (!empty && start is null) start = r;
            if (empty && start is not null)
            {
                yield return (start.Value, r - 1);
                start = null;
            }
        }
    }

    private static MxTableScan? ProfileBlock(MxGrid g, int first, int last, string id)
    {
        // ---- the header row ----
        int? header = null;
        for (var r = first; r <= Math.Min(last, first + 15) && header is null; r++)
            if (IsHeaderRow(g, r)) header = r;
        if (header is null) return null;
        var h = header.Value;

        var cols = Enumerable.Range(g.FirstCol, g.LastCol - g.FirstCol + 1)
            .Where(c => !(g.Cell(h, c)?.IsEmpty ?? true) || InMergeFrom(g, h, c) is not null).ToList();
        var firstCol = cols.Min();
        var lastCol = cols.Max();

        // ---- a second header row under a merged group header ----
        var headerRows = new List<int> { h };
        var texts = new Dictionary<int, string>();
        for (var c = firstCol; c <= lastCol; c++)
            texts[c] = (InMergeFrom(g, h, c) is { } top ? g.Cell(h, top)?.Text : g.Cell(h, c)?.Text)?.Trim() ?? "";
        if (h + 1 <= last && IsSubHeader(g, h, firstCol, lastCol))
        {
            headerRows.Add(h + 1);
            for (var c = firstCol; c <= lastCol; c++)
            {
                var sub = g.Cell(h + 1, c)?.Text?.Trim();
                if (!string.IsNullOrEmpty(sub)) texts[c] = string.IsNullOrEmpty(texts[c]) ? sub : $"{texts[c]} {sub}";
            }
        }

        var firstData = headerRows[^1] + 1;
        if (firstData > last) return null;

        // ---- details above the header: title, date range ----
        var details = new List<string>();
        for (var r = first; r < h; r++)
        {
            var line = string.Join(" ", Enumerable.Range(g.FirstCol, g.LastCol - g.FirstCol + 1)
                .Select(c => g.Cell(r, c)).Where(x => x is not null && !x.IsEmpty).Select(x => x!.Text?.Trim() ?? x.Number.ToString(CultureInfo.InvariantCulture)));
            if (line.Length > 0) details.Add(line);
        }

        var headers = texts.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
        var kind = FieldCatalog.DetectKind(headers);
        var table = new MxTableProfile
        {
            Id = id,
            Sheet = g.Name,
            Range = $"{MxCells.Letter(firstCol)}{h}:{MxCells.Letter(lastCol)}{last}",
            HeaderRows = headerRows,
            FirstDataRow = firstData,
            LastDataRow = last,
            DataRows = last - firstData + 1,
            Details = details,
            ReportDate = ReportDateFrom(details),
            Kind = kind,
            Fingerprint = Fingerprint(g.Name, headers),
        };
        var scan = new MxTableScan { Profile = table, Grid = g, FirstCol = firstCol, LastCol = lastCol };

        // ---- columns ----
        var usedFields = new HashSet<string>();
        for (var c = firstCol; c <= lastCol; c++)
        {
            var values = new List<MxValue>(table.DataRows);
            for (var r = firstData; r <= last; r++) values.Add(MxCells.Classify(g.Cell(r, c)));
            var col = ProfileColumn(g, c, c - firstCol, texts[c], values, firstData, last, kind, usedFields);
            table.Columns.Add(col);

            if (col.Suggestion?.Field == "emp_id")
                foreach (var v in values)
                    if (KeyText(v) is { } k) scan.EmpIds.Add(k);
            if (col.Suggestion is null && col.Sensitivity != MxSensitivity.Sensitive)
                scan.Samples[col.Index] = values.Where(v => v.Kind != MxValueKind.Null).Take(5).Select(v => v.Text ?? v.Iso ?? v.Number.ToString(CultureInfo.InvariantCulture)).ToList();
        }

        if (details.Count > 0) table.Quirks.Add($"{details.Count} line(s) above the header kept as report details");
        if (headerRows.Count > 1) table.Quirks.Add("two header rows, joined");
        if (table.Columns.Any(x => x.Formula is not null)) table.Quirks.Add("formulas: cached values used");
        return scan;
    }

    /// <summary>Text of an ID-like value, trimmed, numbers without a fraction.</summary>
    public static string? KeyText(MxValue v) => v.Kind switch
    {
        MxValueKind.Number => v.Number.ToString("0.##########", CultureInfo.InvariantCulture),
        MxValueKind.Text or MxValueKind.LeadingZeroId => v.Text?.Trim() is { Length: > 0 } t ? t : null,
        _ => null,
    };

    private static MxColumnProfile ProfileColumn(
        MxGrid g, int c, int index, string header, List<MxValue> values, int firstData, int last, string kind, HashSet<string> usedFields)
    {
        var (type, unit) = MxCells.Decide(values);
        var col = new MxColumnProfile
        {
            Index = index,
            Letter = MxCells.Letter(c),
            Header = header,
            Type = type,
            Unit = unit,
            Nulls = values.Count(v => v.Kind is MxValueKind.Null or MxValueKind.NullText),
            NullText = values.Count(v => v.Kind == MxValueKind.NullText),
            Distinct = values.Where(v => v.Kind != MxValueKind.Null).Select(v => v.Text ?? v.Iso ?? v.Number.ToString(CultureInfo.InvariantCulture)).Distinct().Count(),
        };

        // Formulas: the first one as written, and how many cells hold one.
        for (var r = firstData; r <= last; r++)
            if (g.Cell(r, c)?.Formula is { } f) { col.Formula ??= f; col.FormulaCount++; }

        // Sensitivity and mapping.
        var sensitive = FieldCatalog.SensitiveCategory(header);
        var field = sensitive is null ? FieldCatalog.MatchSynonym(header, kind) : null;
        if (field is not null && field.Derived) field = null;
        if (field is not null && !usedFields.Add(field.Key)) field = null;   // a second column for the same field stays unmapped
        if (sensitive is not null)
        {
            col.Sensitivity = MxSensitivity.Sensitive;
            col.SensitiveCategory = sensitive;
        }
        else if (field is not null)
        {
            col.Sensitivity = field.Sensitivity;
            col.Suggestion = new MxSuggestion { Field = field.Key, Source = "synonym", Confidence = 1, Confirmed = true };
            col.Synonym = field.Key;
        }
        else col.Sensitivity = MxSensitivity.Personal;   // unmapped: personal until a person classifies it (§3.4)

        // Quirks, as counts.
        var dateFormats = values.Where(v => v.Kind == MxValueKind.Date).GroupBy(v => v.DateFormat).ToDictionary(x => x.Key ?? "?", x => x.Count());
        if (type == MxType.Date && dateFormats.Keys.Any(k => k != "excel")) col.Quirks.Add("dates as text");
        if (dateFormats.Count > 1) col.Quirks.Add($"mixed date formats ({string.Join(", ", dateFormats.Select(kv => $"{kv.Key} ×{kv.Value}"))})");
        var lead = values.Count(v => v.Kind == MxValueKind.LeadingZeroId);
        if (lead > 0) col.Quirks.Add($"IDs with leading zeros ×{lead}");
        if (col.NullText > 0) col.Quirks.Add($"\"NULL\" text ×{col.NullText}");
        var trailing = values.Count(v => v.Kind is MxValueKind.Text or MxValueKind.Time && v.Text is { } t && t != t.Trim());
        if (trailing > 0) col.Quirks.Add($"leading or trailing spaces ×{trailing}");
        if (type == MxType.Time && values.Any(v => v.Kind == MxValueKind.Time && v.Text is not null)) col.Quirks.Add("times as text");
        var asDates = (type is MxType.Integer or MxType.Real) ? values.Count(v => v.Kind == MxValueKind.Date) : 0;
        if (asDates > 0) col.Quirks.Add($"numbers formatted as dates ×{asDates}");
        if (unit is not null) col.Quirks.Add($"unit \"{unit}\"");
        if (col.Formula is not null) col.Quirks.Add($"formula {col.Formula} ×{col.FormulaCount}");
        if (FieldCatalog.LineNumber(values.FirstOrDefault(v => v.Text is not null).Text) is not null) col.Quirks.Add("line names (LINE - n)");
        return col;
    }

    private static bool IsHeaderRow(MxGrid g, int r)
    {
        var width = g.LastCol - g.FirstCol + 1;
        var cells = Enumerable.Range(g.FirstCol, width).Select(c => g.Cell(r, c)).Where(x => x is not null && !x.IsEmpty).ToList();
        if (cells.Count < Math.Max(2, Math.Ceiling(width * 0.5)) && !(cells.Count >= 2 && HasGroupMerge(g, r))) return false;
        if (cells.Any(x => x!.Type != XLDataType.Text)) return false;
        var texts = cells.Select(x => x!.Text!.Trim()).ToList();
        if (texts.Any(t => t.Length > 60)) return false;
        if (texts.Distinct(StringComparer.OrdinalIgnoreCase).Count() != texts.Count) return false;
        // Header texts are names, not data: dates, times and IDs do not head columns.
        return texts.Count(t => MxCells.Classify(new MxRawCell { Type = XLDataType.Text, Text = t }).Kind != MxValueKind.Text) <= texts.Count / 4;
    }

    private static bool HasGroupMerge(MxGrid g, int r) => g.Merges.Any(m => m.Row == r && m.LastCol > m.FirstCol);

    /// <summary>The column a horizontal or vertical merge covering (r, c) starts at, when c is not that start.</summary>
    private static int? InMergeFrom(MxGrid g, int r, int c)
    {
        foreach (var m in g.Merges)
            if (m.Row <= r && r <= m.LastRow && m.FirstCol <= c && c <= m.LastCol && (m.FirstCol != c || m.Row != r))
                return m.FirstCol;
        return null;
    }

    /// <summary>A second header row: text only, under a horizontal group merge, with data below it.</summary>
    private static bool IsSubHeader(MxGrid g, int h, int firstCol, int lastCol)
    {
        if (!g.Merges.Any(m => m.Row == h && m.LastCol > m.FirstCol)) return false;
        var cells = Enumerable.Range(firstCol, lastCol - firstCol + 1).Select(c => g.Cell(h + 1, c)).Where(x => x is not null && !x.IsEmpty).ToList();
        if (cells.Count == 0 || cells.Any(x => x!.Type != XLDataType.Text)) return false;
        var below = Enumerable.Range(firstCol, lastCol - firstCol + 1).Select(c => g.Cell(h + 2, c)).Where(x => x is not null && !x.IsEmpty).ToList();
        return below.Any(x => x!.Type != XLDataType.Text);
    }

    /// <summary>"(19-May-2026 To 19-May-2026)" → 2026-05-19, the range's end; "Date: 19-May-2026" the same.</summary>
    public static string? ReportDateFrom(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var m = RangeLine.Match(line);
            var text = m.Success ? m.Groups[2].Value : DateLine.Match(line) is { Success: true } d ? d.Groups[1].Value : null;
            if (text is not null && DateTime.TryParseExact(text, new[] { "d-MMM-yyyy", "dd-MMM-yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        return null;
    }

    public static string Fingerprint(string sheet, IEnumerable<string> headers)
    {
        var text = FieldCatalog.Normalise(sheet) + "|" + string.Join("|", headers.Select(FieldCatalog.Normalise));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    /// <summary>
    /// Tables holding mostly the same people (≥ 80% of the larger by emp_id) are
    /// copies. One of each group is the default: not named like a copy
    /// ("HRM (2)", "For correction"), then the most rows.
    /// </summary>
    private static void FlagCopies(List<MxTableScan> scans)
    {
        var withIds = scans.Where(s => s.EmpIds.Count > 0).ToList();
        var groups = new List<List<MxTableScan>>();
        foreach (var s in withIds)
        {
            var group = groups.FirstOrDefault(gr => gr.Any(o => OverlapPct(o, s) >= CopyThresholdPct));
            if (group is null) groups.Add(new List<MxTableScan> { s });
            else group.Add(s);
        }

        foreach (var group in groups.Where(gr => gr.Count > 1))
        {
            var keep = group.OrderBy(s => CopyName.IsMatch(s.Profile.Sheet) ? 1 : 0).ThenByDescending(s => s.Profile.DataRows).First();
            var keepHeaders = keep.Profile.Columns.Select(c => FieldCatalog.Normalise(c.Header)).ToHashSet();
            foreach (var s in group.Where(s => s != keep))
            {
                var headers = s.Profile.Columns.Select(c => FieldCatalog.Normalise(c.Header)).ToList();
                var extra = headers.Count(x => !keepHeaders.Contains(x));
                var missing = keepHeaders.Count(x => !headers.Contains(x));
                s.Profile.CopyOf = new MxCopyInfo
                {
                    Sheet = keep.Profile.Sheet,
                    OverlapPct = Math.Round(OverlapPct(keep, s), 1),
                    Near = extra + missing > 0,
                    ExtraColumns = extra,
                };
                s.Profile.Selected = false;
                s.Profile.Quirks.Add($"possible copy of '{keep.Profile.Sheet}'");
            }
        }
    }

    public static double OverlapPct(MxTableScan a, MxTableScan b)
    {
        if (a.EmpIds.Count == 0 || b.EmpIds.Count == 0) return 0;
        var (small, large) = a.EmpIds.Count <= b.EmpIds.Count ? (a.EmpIds, b.EmpIds) : (b.EmpIds, a.EmpIds);
        var both = small.Count(large.Contains);
        return 100.0 * both / Math.Max(a.EmpIds.Count, b.EmpIds.Count);
    }
}
