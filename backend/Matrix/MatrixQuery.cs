// ম্যাট্রিক্স — the query language (docs/MATRIX_PROMPT.md §3.6): the only way anything
// reads data — dashboards, KPIs, report cards and (later) chat.
//
//   { "dataset": "current:manpower" | "previous:manpower" | "snapshots:attendance-daily" | "<id>",
//     "join":    [{ "dataset": "current:master", "on": "emp_id" }],
//     "filter":  [["section", "=", "Sewing"], ["status", "in", ["Active"]], ["att_date", "between", "2026-05-01", "2026-05-31"]],
//     "group_by": ["unit", "line_no"],
//     "measures": [["count", "*", "headcount"], ["sum", "ot_hours", "ot"],
//                  ["ratio", ["count_if", ["designation", "contains", "Operator"]], ["count_if", ["designation", "contains", "Helper"]], "op_helper_ratio"]],
//     "sort": [["headcount", "desc"]], "limit": 50 }
//
// Validation: datasets, fields and operators come from fixed lists; a field must be a
// column of the dataset or of a join ("master.line_no" names a joined field); salary
// and personal fields follow the caller's permissions; limit ≤ 500, ≤ 2 joins, ≤ 6
// group-by fields. The SQL is built here from the dataset's own column list — table
// and column names from code, every value a parameter ($p0, $p1 …), output aliases
// g0…/m0… made here, never the query's text. MatrixRepository runs it.

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using RabbitIC.Api.Models;

namespace RabbitIC.Api.Matrix;

/// <summary>A refusal: 400 for a query that is wrong, 403 for one the caller may not run.</summary>
public sealed class MxQueryError(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>Who is asking and what exists — the validator's world.</summary>
public sealed class MxQueryContext
{
    public required IReadOnlyList<MxDataset> Datasets { get; init; }
    public required Func<MxDataset, List<MxDsColumn>> ColumnsOf { get; init; }
    public IReadOnlyList<MxRelation> Relations { get; init; } = Array.Empty<MxRelation>();
    public bool CanSalary { get; init; }
    public bool CanPersonal { get; init; }
}

/// <summary>One dataset in a query: the base (alias b) or a join (j1, j2).</summary>
public sealed class MxQuerySource
{
    public required string Alias { get; init; }
    public required string Kind { get; init; }
    public required List<MxDsColumn> Columns { get; init; }
    public required string From { get; init; }

    /// <summary>The datasets behind it (one, or several for snapshots), for provenance.</summary>
    public required List<MxDataset> Datasets { get; init; }
}

/// <summary>A field as a query names it. <paramref name="Part"/> "month": a date grouped by its month ("joining_date:month" → 2026-05).</summary>
public sealed record MxFieldRef(string Text, MxQuerySource Source, MxDsColumn Column, string? Part = null)
{
    public string Sql => Part == "month" ? $"substr({Source.Alias}.\"{Column.Key}\", 1, 7)" : $"{Source.Alias}.\"{Column.Key}\"";
    public bool OnBase => Source.Alias == "b";
}

public sealed record MxOutColumn(string Key, string Label, string Role, string Type);

/// <summary>One measure as compiled: its output name, SQL alias, and — for count_if — its condition (for a per-cell citation).</summary>
public sealed record MxMeasure(string Name, string Alias, string Sql, JsonElement? Condition, IReadOnlyList<MxFieldRef> Fields);

public sealed class MxCompiled
{
    public required MxQuerySource Base { get; init; }
    public required List<MxQuerySource> Joins { get; init; }
    public required List<(MxQuerySource Join, MxFieldRef On)> JoinKeys { get; init; }

    /// <summary>Per join: how many base rows have a key the joined dataset does not have (no parameters).</summary>
    public required List<string> UnmatchedSql { get; init; }
    public required List<MxFieldRef> GroupBy { get; init; }
    public required List<MxMeasure> Measures { get; init; }
    public required List<JsonElement> Filters { get; init; }
    public required List<MxFieldRef> FilterFields { get; init; }
    public required string Sql { get; init; }
    public required string CountSql { get; init; }
    public required List<object> Args { get; init; }
    public required List<MxOutColumn> Columns { get; init; }
    public required string Plain { get; init; }
    public required string BaseWhere { get; init; }
    public required List<object> BaseArgs { get; init; }
}

public static class MatrixQuery
{
    public const int MaxLimit = 500;
    public const int DefaultLimit = 50;
    public const int MaxJoins = 2;
    public const int MaxGroupBy = 6;

    public static readonly string[] FilterOps =
        { "=", "!=", "in", "not_in", "contains", "contains_any", "starts_with", "between", ">", ">=", "<", "<=", "is_null", "not_null", "in_dataset", "not_in_dataset" };
    public static readonly string[] MeasureOps = { "count", "count_distinct", "sum", "avg", "min", "max", "count_if", "ratio", "pct" };
    private static readonly Regex Name = new(@"^[a-z_][a-z0-9_]{0,40}$");
    private static readonly Regex DatasetRef = new(@"^(current|previous|snapshots):([a-z-]+)$");

    // ───────────────────────── datasets ─────────────────────────

    /// <summary>"current:master", "previous:manpower", "snapshots:attendance-daily" or an id.</summary>
    public static List<MxDataset> Resolve(string? reference, MxQueryContext ctx, out bool snapshots)
    {
        snapshots = false;
        var r = (reference ?? "").Trim();
        if (int.TryParse(r, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            return ctx.Datasets.FirstOrDefault(d => d.Id == id) is { } d ? new() { d }
                : throw new MxQueryError(400, $"No dataset {id}.");
        var m = DatasetRef.Match(r);
        if (!m.Success || !MxReportKind.All.Contains(m.Groups[2].Value))
            throw new MxQueryError(400, $"Unknown dataset \"{r}\": use an id, or current:/previous:/snapshots: with one of {string.Join(", ", MxReportKind.All)}.");
        var kind = m.Groups[2].Value;
        var ofKind = ctx.Datasets.Where(d => d.ReportKind == kind).ToList();
        var current = ofKind.Where(d => d.IsCurrent).OrderBy(d => d.ReportDate ?? "").ThenBy(d => d.Id).LastOrDefault()
            ?? throw new MxQueryError(400, $"No {kind} dataset has been imported.");
        switch (m.Groups[1].Value)
        {
            case "current": return new() { current };
            case "previous":
                return PreviousSnapshot(current, ofKind) is { } p ? new() { p }
                    : throw new MxQueryError(400, $"There is no {kind} snapshot before the current one.");
            default:
                snapshots = true;
                return ofKind.Where(d => d.TemplateId == current.TemplateId).OrderBy(d => d.ReportDate ?? "").ThenBy(d => d.Id).ToList();
        }
    }

    /// <summary>
    /// The snapshot before <paramref name="d"/> — one rule for previous:, the KPIs, the report
    /// card and the dashboards: the same kind and layout (template), an earlier report date,
    /// or the same date and imported earlier; the latest of those.
    /// </summary>
    public static MxDataset? PreviousSnapshot(MxDataset d, IEnumerable<MxDataset> all) =>
        all.Where(x => x.ReportKind == d.ReportKind && x.TemplateId == d.TemplateId && x.Id != d.Id
                && (string.CompareOrdinal(x.ReportDate ?? "", d.ReportDate ?? "") < 0
                    || (string.CompareOrdinal(x.ReportDate ?? "", d.ReportDate ?? "") == 0 && x.Id < d.Id)))
            .OrderBy(x => x.ReportDate ?? "").ThenBy(x => x.Id).LastOrDefault();

    private static string Table(MxDataset d) =>
        DatasetImporter.SafeName.IsMatch(d.TableName) ? $"\"{d.TableName}\"" : throw new InvalidOperationException("Unsafe table name.");

    private static MxQuerySource Source(string alias, List<MxDataset> datasets, bool snapshots, MxQueryContext ctx, List<object> args)
    {
        if (!snapshots)
        {
            var d = datasets[0];
            return new MxQuerySource { Alias = alias, Kind = d.ReportKind, Columns = ctx.ColumnsOf(d), From = Table(d), Datasets = datasets };
        }
        // A series: the columns every snapshot has, plus report_date, one UNION ALL.
        var common = datasets.Select(ctx.ColumnsOf).Aggregate((a, b) => a.Where(c => b.Any(x => x.Key == c.Key)).ToList());
        var cols = string.Join(", ", new[] { "\"_row\"", "\"_sheet\"" }.Concat(common.Select(c => $"\"{c.Key}\"")));
        var parts = datasets.Select(d => { args.Add(d.ReportDate ?? ""); return $"SELECT {cols}, $p{args.Count - 1} AS \"report_date\" FROM {Table(d)}"; });
        common.Add(new MxDsColumn("report_date", "Report date", "report_date", MxType.Date, MxSensitivity.Public, null, true, null));
        return new MxQuerySource { Alias = alias, Kind = datasets[0].ReportKind, Columns = common, From = $"({string.Join(" UNION ALL ", parts)})", Datasets = datasets };
    }

    // ───────────────────────── compile ─────────────────────────

    public static MxCompiled Compile(JsonElement q, MxQueryContext ctx)
    {
        if (q.ValueKind != JsonValueKind.Object) throw new MxQueryError(400, "The query must be a JSON object.");
        var args = new List<object>();
        var baseSets = Resolve(Str(q, "dataset"), ctx, out var snap);
        var b = Source("b", baseSets, snap, ctx, args);

        // ---- joins ----
        var joins = new List<MxQuerySource>();
        var joinKeys = new List<(MxQuerySource, MxFieldRef)>();
        var joinSql = new List<string>();
        var ctes = new List<string>();
        var unmatched = new List<string>();
        if (q.TryGetProperty("join", out var js) && js.ValueKind == JsonValueKind.Array)
        {
            if (js.GetArrayLength() > MaxJoins) throw new MxQueryError(400, $"At most {MaxJoins} joins.");
            var n = 0;
            foreach (var j in js.EnumerateArray())
            {
                n++;
                var sets = Resolve(Str(j, "dataset"), ctx, out var jsnap);
                if (jsnap) throw new MxQueryError(400, "A join takes one dataset, not a series of snapshots.");
                var src = Source($"j{n}", sets, false, ctx, args);
                var on = Str(j, "on") ?? throw new MxQueryError(400, "A join needs \"on\": the field both datasets share.");
                var left = b.Columns.FirstOrDefault(c => c.Key == on) ?? throw new MxQueryError(400, $"Unknown field \"{on}\" in the base dataset.");
                var right = src.Columns.FirstOrDefault(c => c.Key == on) ?? throw new MxQueryError(400, $"Unknown field \"{on}\" in the joined {src.Kind} dataset.");
                var norm = Normaliser(b.Kind, src.Kind, on, ctx);
                var key = Norm(norm, $"\"{right.Key}\"");
                // One row per key on the joined side (its first), so a repeated ID never doubles a count.
                // The joined side is made once (a materialised CTE) with its normalised key as a
                // column, _jk: SQLite then indexes it on the fly, and each base row is one lookup —
                // not a rescan of the joined dataset with the normaliser run on every row.
                ctes.Add($"{src.Alias} AS MATERIALIZED (SELECT *, {key} AS \"_jk\" FROM {src.From} WHERE \"_row\" IN (SELECT min(\"_row\") FROM {src.From} WHERE \"{right.Key}\" IS NOT NULL GROUP BY {key}))");
                joinSql.Add($"LEFT JOIN {src.Alias} ON {Norm(norm, $"b.\"{left.Key}\"")} = {src.Alias}.\"_jk\"");
                unmatched.Add($"SELECT count(*) FROM {b.From} b WHERE b.\"{left.Key}\" IS NOT NULL AND {Norm(norm, $"b.\"{left.Key}\"")} NOT IN (SELECT {key} FROM {src.From} WHERE \"{right.Key}\" IS NOT NULL)");
                joins.Add(src);
                joinKeys.Add((src, new MxFieldRef(on, b, left)));
            }
        }
        var sources = new List<MxQuerySource> { b }.Concat(joins).ToList();

        MxFieldRef Field(string? text, string use)
        {
            var t = (text ?? "").Trim();
            // "<date field>:month" groups a date by its month; only in group_by.
            string? part = null;
            if (use == "group" && t.EndsWith(":month", StringComparison.Ordinal)) { part = "month"; t = t[..^6]; }
            MxFieldRef? found = null;
            var dot = t.IndexOf('.');
            if (dot > 0)
            {
                var qual = t[..dot];
                var key = t[(dot + 1)..];
                var src = sources.FirstOrDefault(s => s.Alias == qual) ?? sources.FirstOrDefault(s => s.Kind == qual);
                var col = src?.Columns.FirstOrDefault(c => c.Key == key);
                if (src is not null && col is not null) found = new MxFieldRef(t, src, col);
            }
            else
                foreach (var s in sources)
                    if (s.Columns.FirstOrDefault(c => c.Key == t) is { } col) { found = new MxFieldRef(t, s, col); break; }
            if (found is null) throw new MxQueryError(400, $"Unknown field \"{t}\": not a column of the dataset{(joins.Count > 0 ? " or its joins" : "")}.");
            if (part is not null)
            {
                if (found.Column.Type != MxType.Date) throw new MxQueryError(400, $"\"{t}:month\": only a date field can be grouped by month.");
                found = found with { Text = t + ":month", Part = part };
            }

            var sens = found.Column.Sensitivity;
            if (sens == MxSensitivity.Salary && !ctx.CanSalary)
                throw new MxQueryError(403, $"\"{t}\" is a salary field: it needs action:matrix.view-salary.");
            if (sens == MxSensitivity.Sensitive)
                throw new MxQueryError(403, $"\"{t}\" is a sensitive field and cannot be queried.");
            if (sens == MxSensitivity.Personal && use != "filter")
                throw new MxQueryError(400, $"\"{t}\" is personal (a name): results name groups, never people, so it cannot be grouped or measured.");
            if (sens == MxSensitivity.Personal && !ctx.CanPersonal)
                throw new MxQueryError(403, $"\"{t}\" is personal: filtering on it needs action:matrix.view-personal.");
            return found;
        }

        // ---- filters ----
        var filters = new List<JsonElement>();
        var filterFields = new List<MxFieldRef>();
        var where = new List<string>();
        if (q.TryGetProperty("filter", out var fs) && fs.ValueKind == JsonValueKind.Array)
            foreach (var f in fs.EnumerateArray())
            {
                var (sqlCond, field) = Condition(f, Field, args, ctx);
                where.Add(sqlCond);
                filters.Add(f);
                filterFields.Add(field);
            }

        // ---- group by ----
        var groups = new List<MxFieldRef>();
        if (q.TryGetProperty("group_by", out var gs) && gs.ValueKind == JsonValueKind.Array)
        {
            if (gs.GetArrayLength() > MaxGroupBy) throw new MxQueryError(400, $"At most {MaxGroupBy} group-by fields.");
            foreach (var g in gs.EnumerateArray()) groups.Add(Field(g.ValueKind == JsonValueKind.String ? g.GetString() : null, "group"));
        }

        // ---- measures ----
        var measures = new List<MxMeasure>();
        if (q.TryGetProperty("measures", out var ms) && ms.ValueKind == JsonValueKind.Array)
            foreach (var m in ms.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Array || m.GetArrayLength() < 2) throw new MxQueryError(400, "A measure is [op, field or condition, name].");
                var name = m[m.GetArrayLength() - 1].ValueKind == JsonValueKind.String ? m[m.GetArrayLength() - 1].GetString()! : "";
                if (!Name.IsMatch(name)) throw new MxQueryError(400, $"A measure's name must be lower_case letters, digits and _ (got \"{name}\").");
                var used = new List<MxFieldRef>();
                var (msql, cond) = Measure(m, m.GetArrayLength() - 1, Field, args, ctx, used);
                measures.Add(new MxMeasure(name, $"m{measures.Count}", msql, cond, used));
            }
        if (measures.Count == 0) measures.Add(new MxMeasure("count", "m0", "count(*)", null, Array.Empty<MxFieldRef>()));
        if (measures.Select(x => x.Name).Distinct().Count() != measures.Count) throw new MxQueryError(400, "Two measures have the same name.");

        // ---- sort and limit ----
        var limit = DefaultLimit;
        if (q.TryGetProperty("limit", out var l))
        {
            if (!l.TryGetInt32(out limit) || limit < 1) throw new MxQueryError(400, "limit must be a whole number from 1.");
            if (limit > MaxLimit) throw new MxQueryError(400, $"limit is at most {MaxLimit}.");
        }
        var order = new List<string>();
        if (q.TryGetProperty("sort", out var ss) && ss.ValueKind == JsonValueKind.Array)
            foreach (var s in ss.EnumerateArray())
            {
                var by = s.ValueKind == JsonValueKind.Array && s.GetArrayLength() > 0 ? s[0].GetString() : null;
                var dir = s.ValueKind == JsonValueKind.Array && s.GetArrayLength() > 1 ? s[1].GetString() : "asc";
                if (dir is not ("asc" or "desc")) throw new MxQueryError(400, "Sort direction is asc or desc.");
                var mi = measures.FindIndex(x => x.Name == by);
                var gi = groups.FindIndex(x => x.Text == by);
                if (mi < 0 && gi < 0) throw new MxQueryError(400, $"Sort by \"{by}\": not a measure or a group-by field of this query.");
                order.Add($"{(mi >= 0 ? measures[mi].Alias : $"g{gi}")} {dir.ToUpperInvariant()}");
            }
        if (order.Count == 0) order.AddRange(groups.Select((_, i) => $"g{i} ASC"));

        // ---- SQL ----
        var select = groups.Select((g, i) => $"{g.Sql} AS g{i}").Concat(measures.Select(m => $"{m.Sql} AS {m.Alias}"))
            .Append("group_concat(b.\"_row\") AS _rows").ToList();
        var with = ctes.Count > 0 ? $"WITH {string.Join(", ", ctes)} " : "";
        var from = $"FROM {b.From} b {string.Join(" ", joinSql)}";
        var whereSql = where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "";
        var groupSql = groups.Count > 0 ? " GROUP BY " + string.Join(", ", groups.Select((_, i) => $"g{i}")) : "";
        args.Add(limit);
        var sql = $"{with}SELECT {string.Join(", ", select)} {from}{whereSql}{groupSql}{(order.Count > 0 ? " ORDER BY " + string.Join(", ", order) : "")} LIMIT $p{args.Count - 1}";
        var countSql = $"{with}SELECT count(*) {from}{whereSql}";

        // The base dataset's own filter, for citations that reopen rows in the viewer.
        var baseArgs = new List<object>();
        var baseWhere = string.Join(" AND ", filters.Zip(filterFields).Where(x => x.Second.OnBase).Select(x => Condition(x.First, (t, _) => Field(t, "filter"), baseArgs, ctx).Sql));

        var columns = groups.Select(g => new MxOutColumn(g.Text, FieldCatalog.Find(g.Column.Field)?.En ?? g.Column.Header, "group", g.Column.Type))
            .Concat(measures.Select(m => new MxOutColumn(m.Name, m.Name, "measure", "real"))).ToList();
        return new MxCompiled
        {
            Base = b, Joins = joins, JoinKeys = joinKeys, UnmatchedSql = unmatched, GroupBy = groups, Measures = measures, Filters = filters, FilterFields = filterFields,
            Sql = sql, CountSql = countSql, Args = args, Columns = columns,
            Plain = Plain(b, joins, joinKeys, filters, groups, measures), BaseWhere = baseWhere, BaseArgs = baseArgs,
        };
    }

    // ───────────────────────── conditions ─────────────────────────

    /// <summary>[field, op, value…] → SQL with parameters. Shared with the rows route, so a citation filters exactly as its query did.</summary>
    public static (string Sql, MxFieldRef Field) Condition(JsonElement f, Func<string?, string, MxFieldRef> fieldOf, List<object> args, MxQueryContext ctx)
    {
        if (f.ValueKind != JsonValueKind.Array || f.GetArrayLength() < 2) throw new MxQueryError(400, "A filter is [field, operator, value…].");
        var field = fieldOf(f[0].ValueKind == JsonValueKind.String ? f[0].GetString() : null, "filter");
        var op = f[1].ValueKind == JsonValueKind.String ? f[1].GetString()! : "";
        if (!FilterOps.Contains(op)) throw new MxQueryError(400, $"\"{op}\" is not an operator: use one of {string.Join(", ", FilterOps)}.");
        var col = field.Sql;
        string P(JsonElement v) { args.Add(Value(v)); return $"$p{args.Count - 1}"; }
        JsonElement Arg(int i) => f.GetArrayLength() > i ? f[i] : throw new MxQueryError(400, $"\"{op}\" needs a value.");
        List<JsonElement> List(int i) => Arg(i).ValueKind == JsonValueKind.Array ? Arg(i).EnumerateArray().ToList() : new List<JsonElement> { Arg(i) };

        return (op switch
        {
            "=" => $"{col} = {P(Arg(2))}",
            "!=" => $"({col} IS NULL OR {col} <> {P(Arg(2))})",
            "in" => $"{col} IN ({string.Join(", ", List(2).Select(P))})",
            "not_in" => $"({col} IS NULL OR {col} NOT IN ({string.Join(", ", List(2).Select(P))}))",
            "contains" => $"instr(lower(CAST({col} AS TEXT)), lower({P(Arg(2))})) > 0",
            "contains_any" => "(" + string.Join(" OR ", List(2).Select(v => $"instr(lower(CAST({col} AS TEXT)), lower({P(v)})) > 0")) + ")",
            "starts_with" => $"substr(lower(CAST({col} AS TEXT)), 1, length({P(Arg(2))})) = lower($p{args.Count - 1})",
            "between" => $"{col} BETWEEN {P(Arg(2))} AND {P(Arg(3))}",
            ">" => $"{col} > {P(Arg(2))}",
            ">=" => $"{col} >= {P(Arg(2))}",
            "<" => $"{col} < {P(Arg(2))}",
            "<=" => $"{col} <= {P(Arg(2))}",
            "is_null" => $"({col} IS NULL OR {col} = '')",
            "not_null" => $"({col} IS NOT NULL AND {col} <> '')",
            _ => InDataset(op, field, f, args, ctx),
        }, field);
    }

    /// <summary>[field, "in_dataset" | "not_in_dataset", datasetRef, otherField?]: membership in another dataset, through the field's normaliser.</summary>
    private static string InDataset(string op, MxFieldRef field, JsonElement f, List<object> args, MxQueryContext ctx)
    {
        var reference = f.GetArrayLength() > 2 && f[2].ValueKind is JsonValueKind.String or JsonValueKind.Number ? f[2].ToString() : null;
        var other = Resolve(reference, ctx, out var snap);
        if (snap) throw new MxQueryError(400, $"\"{op}\" takes one dataset, not a series.");
        var otherKey = f.GetArrayLength() > 3 ? f[3].GetString() : field.Column.Key;
        var otherCol = ctx.ColumnsOf(other[0]).FirstOrDefault(c => c.Key == otherKey)
            ?? throw new MxQueryError(400, $"Unknown field \"{otherKey}\" in dataset {other[0].Id}.");
        var norm = FieldCatalog.Find(field.Column.Field)?.Role == MxRole.Id ? MatrixNormalisers.EmpId6 : "trim";
        var sub = $"SELECT {Norm(norm, $"\"{otherCol.Key}\"")} FROM {Table(other[0])} WHERE \"{otherCol.Key}\" IS NOT NULL";
        return op == "in_dataset" ? $"{Norm(norm, field.Sql)} IN ({sub})" : $"({field.Sql} IS NOT NULL AND {Norm(norm, field.Sql)} NOT IN ({sub}))";
    }

    private static (string Sql, JsonElement? Cond) Measure(JsonElement m, int nameAt, Func<string?, string, MxFieldRef> fieldOf, List<object> args, MxQueryContext ctx, List<MxFieldRef> used)
    {
        var op = m[0].ValueKind == JsonValueKind.String ? m[0].GetString()! : "";
        if (!MeasureOps.Contains(op)) throw new MxQueryError(400, $"\"{op}\" is not a measure: use one of {string.Join(", ", MeasureOps)}.");
        MxFieldRef F(JsonElement e) { var r = fieldOf(e.ValueKind == JsonValueKind.String ? e.GetString() : null, "measure"); used.Add(r); return r; }
        string Inner(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Array) throw new MxQueryError(400, $"\"{op}\" takes two measures, such as [\"count_if\", [\"designation\", \"contains\", \"Operator\"]].");
            return Measure(e, e.GetArrayLength(), fieldOf, args, ctx, used).Sql;
        }
        switch (op)
        {
            case "count":
                return (m[1].ValueKind == JsonValueKind.String && m[1].GetString() == "*" ? "count(*)" : $"count({F(m[1]).Sql})", null);
            case "count_distinct": return ($"count(DISTINCT {F(m[1]).Sql})", null);
            case "sum": return ($"total({Numeric(F(m[1]))})", null);
            case "avg": return ($"avg({Numeric(F(m[1]))})", null);
            case "min": return ($"min({F(m[1]).Sql})", null);
            case "max": return ($"max({F(m[1]).Sql})", null);
            case "count_if":
            {
                var (c, field) = Condition(m[1], (t, _) => { var r = fieldOf(t, "filter"); return r; }, args, ctx);
                used.Add(field);
                return ($"sum(CASE WHEN {c} THEN 1 ELSE 0 END)", m[1].Clone());
            }
            case "ratio":
                if (nameAt < 3) throw new MxQueryError(400, "ratio takes two measures and a name.");
                return ($"(1.0 * ({Inner(m[1])}) / NULLIF(({Inner(m[2])}), 0))", null);
            default: // pct
                if (nameAt < 3) throw new MxQueryError(400, "pct takes two measures and a name.");
                return ($"(100.0 * ({Inner(m[1])}) / NULLIF(({Inner(m[2])}), 0))", null);
        }
    }

    private static string Numeric(MxFieldRef f) =>
        f.Column.Type is MxType.Integer or MxType.Real ? f.Sql : throw new MxQueryError(400, $"\"{f.Text}\" is not a number, so it cannot be summed or averaged.");

    /// <summary>A filter value as a parameter: text or a number. Text that looks like SQL is refused outright — it is never needed in a data value.</summary>
    private static object Value(JsonElement v)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.Number: return v.TryGetInt64(out var l) ? l : v.GetDouble();
            case JsonValueKind.True: return 1L;
            case JsonValueKind.False: return 0L;
            case JsonValueKind.String:
                var s = v.GetString()!;
                if (s.Length > 200) throw new MxQueryError(400, "A filter value is at most 200 characters.");
                if (s.Contains(';') || s.Contains("--") || s.Contains("/*"))
                    throw new MxQueryError(400, "A filter value may not contain ; -- or /* — they are never part of a data value.");
                return s;
            default: throw new MxQueryError(400, "A filter value is text or a number.");
        }
    }

    // ───────────────────────── normalisers ─────────────────────────

    private static string Normaliser(string baseKind, string joinKind, string field, MxQueryContext ctx)
    {
        var rel = ctx.Relations.FirstOrDefault(r => r.LeftField == field && r.RightField == field
            && ((r.LeftKind == baseKind && r.RightKind == joinKind) || (r.LeftKind == joinKind && r.RightKind == baseKind)));
        if (rel is not null) return rel.Normaliser;
        return FieldCatalog.Find(field)?.Role switch { MxRole.Id => MatrixNormalisers.EmpId6, MxRole.Dimension => MatrixNormalisers.TrimLower, _ => "trim" };
    }

    public static string Norm(string normaliser, string x) => normaliser switch
    {
        MatrixNormalisers.EmpId6 => $"(CASE WHEN trim({x}) NOT GLOB '*[^0-9]*' AND length(trim({x})) BETWEEN 1 AND 5 THEN substr('000000' || trim({x}), -6) ELSE trim({x}) END)",
        MatrixNormalisers.TrimLower => $"lower(trim({x}))",
        _ => $"trim({x})",
    };

    // ───────────────────────── words ─────────────────────────

    private static string Plain(MxQuerySource b, List<MxQuerySource> joins, List<(MxQuerySource Join, MxFieldRef On)> keys,
        List<JsonElement> filters, List<MxFieldRef> groups, List<MxMeasure> measures)
    {
        var parts = new List<string> { $"{string.Join(", ", measures.Select(m => m.Name))} from {Describe(b)}" };
        if (joins.Count > 0) parts.Add("joined with " + string.Join(", ", keys.Select(k => $"{Describe(k.Join)} on {k.On.Column.Key}")));
        if (filters.Count > 0) parts.Add("where " + string.Join(" and ", filters.Select(f => string.Join(" ", f.EnumerateArray().Select(x => x.ToString())))));
        if (groups.Count > 0) parts.Add("by " + string.Join(", ", groups.Select(g => g.Text)));
        return string.Join(", ", parts);
    }

    private static string Describe(MxQuerySource s) => s.Datasets.Count == 1
        ? $"{s.Kind} dataset {s.Datasets[0].Id} ({s.Datasets[0].ReportDate})"
        : $"{s.Datasets.Count} {s.Kind} snapshots";

    private static string? Str(JsonElement e, string p) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : null;

    // ───────────────────────── a single dataset's filter (the rows route) ─────────────────────────

    /// <summary>
    /// [[field, op, value…], …] over one dataset's columns (alias b) → a WHERE clause and its
    /// parameters. The rows route and citation tokens use it, so they filter as queries do.
    /// </summary>
    public static (string Where, List<object> Args) WhereFor(MxDataset d, IReadOnlyList<JsonElement> filters, MxQueryContext ctx)
    {
        var src = new MxQuerySource { Alias = "b", Kind = d.ReportKind, Columns = ctx.ColumnsOf(d), From = Table(d), Datasets = new() { d } };
        var args = new List<object>();
        var conds = filters.Select(f => Condition(f, (t, _) =>
        {
            var col = src.Columns.FirstOrDefault(c => c.Key == (t ?? "").Trim())
                ?? throw new MxQueryError(400, $"Unknown field \"{t}\": not a column of this dataset.");
            if (col.Sensitivity == MxSensitivity.Salary && !ctx.CanSalary) throw new MxQueryError(403, $"\"{col.Key}\" is a salary field: filtering on it needs action:matrix.view-salary.");
            if (col.Sensitivity == MxSensitivity.Personal && !ctx.CanPersonal) throw new MxQueryError(403, $"\"{col.Key}\" is personal: filtering on it needs action:matrix.view-personal.");
            return new MxFieldRef(col.Key, src, col);
        }, args, ctx).Sql).ToList();
        return (string.Join(" AND ", conds), args);
    }

    // ───────────────────────── dashboard scope ─────────────────────────

    /// <summary>
    /// A dashboard filter added to every query that can take it (§3.8): the first of
    /// <see cref="MxScopeFilter.Fields"/> the base dataset has, else one a join has (as
    /// "&lt;kind&gt;.&lt;field&gt;"). Returns the keys of the filters applied; the others did not fit.
    /// </summary>
    public static List<string> ApplyScope(System.Text.Json.Nodes.JsonObject node, IReadOnlyList<MxScopeFilter>? scope, MxQueryContext ctx)
    {
        var applied = new List<string>();
        if (scope is null || scope.Count == 0) return applied;
        var baseSets = Resolve(node["dataset"]?.ToString(), ctx, out _);
        var baseKeys = baseSets.Select(ctx.ColumnsOf).Select(cs => cs.Select(c => c.Key).ToHashSet())
            .Aggregate((a, b) => { a.IntersectWith(b); return a; });
        var joins = new List<(string Kind, HashSet<string> Keys)>();
        if (node["join"] is System.Text.Json.Nodes.JsonArray ja)
            foreach (var j in ja)
                if (j?["dataset"]?.ToString() is { } r && Resolve(r, ctx, out _) is [var jd, ..])
                    joins.Add((jd.ReportKind, ctx.ColumnsOf(jd).Select(c => c.Key).ToHashSet()));

        foreach (var f in scope)
        {
            string? field = f.Fields.FirstOrDefault(baseKeys.Contains);
            if (field is null)
                foreach (var (kind, keys) in joins)
                    if (f.Fields.FirstOrDefault(keys.Contains) is { } k) { field = $"{kind}.{k}"; break; }
            if (field is null) continue;
            if (node["filter"] is not System.Text.Json.Nodes.JsonArray fa) node["filter"] = fa = new System.Text.Json.Nodes.JsonArray();
            var cond = new System.Text.Json.Nodes.JsonArray(field, f.Op);
            foreach (var v in f.Values) cond.Add(System.Text.Json.Nodes.JsonNode.Parse(v.GetRawText()));
            fa.Add(cond);
            applied.Add(f.Key);
        }
        return applied;
    }
}

/// <summary>One dashboard filter: its key (unit, section, line, emp_type, status, shift, date), the fields it can sit on, in order, and its condition.</summary>
public sealed record MxScopeFilter(string Key, string[] Fields, string Op, JsonElement[] Values);
