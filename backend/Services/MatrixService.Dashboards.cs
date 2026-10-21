// ম্যাট্রিক্স — dashboards and export (docs/MATRIX_PROMPT.md §3.8, §3.10).
//
// Automatic dashboards are made for each report kind as it is first imported (and for
// kinds already there when the list is first read). Every widget is a KPI or a
// MatrixQuery: running a dashboard runs each of them for the caller, with the
// dashboard's filters added where the widget's dataset has the field, and returns
// what ran beside the numbers. Export writes a dataset without its sensitive columns,
// and without personal or salary columns the caller may not see.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using RabbitIC.Api.DTOs.Matrix;
using RabbitIC.Api.Matrix;
using RabbitIC.Api.Models;
using RabbitIC.Api.Rbac;

namespace RabbitIC.Api.Services;

public partial class MatrixService
{
    private static JsonElement El(object value) => JsonSerializer.SerializeToElement(value);

    // ───────────────────────── automatic dashboards ─────────────────────────

    private static string? AutoKey(MxDashboard d) => Layout(d)["auto"]?.ToString();
    private static JsonObject Layout(MxDashboard d) => JsonNode.Parse(string.IsNullOrWhiteSpace(d.LayoutJson) ? "{}" : d.LayoutJson) as JsonObject ?? new JsonObject();

    /// <summary>Creates the automatic dashboards the current datasets' kinds bring, and adds new kinds to Data quality.</summary>
    private async Task EnsureAutoDashboardsAsync(CancellationToken ct)
    {
        var kinds = (await _repo.ListDatasetsAsync(ct)).Where(d => d.IsCurrent).Select(d => d.ReportKind).Distinct().ToList();
        if (kinds.Count == 0) return;
        var all = await _repo.ListDashboardsAsync(ct);
        var byAuto = all.Select(d => (d, auto: AutoKey(d))).Where(x => x.auto is not null).ToDictionary(x => x.auto!, x => x.d);
        foreach (var auto in kinds.SelectMany(DashboardLibrary.AutoFor).Distinct())
        {
            if (byAuto.TryGetValue(auto, out var have))
            {
                if (auto != MxDashboardAuto.DataQuality) continue;
                var layout = Layout(have);
                if (!DashboardLibrary.AddQualityKinds(layout, kinds)) continue;
                have.LayoutJson = layout.ToJsonString();
                have.UpdatedAtUtc = DateTime.UtcNow;
                await _repo.SaveChangesAsync(ct);
                continue;
            }
            var (en, bn, kind) = DashboardLibrary.NameOf(auto);
            var fresh = DashboardLibrary.AutoLayout(auto, kinds);
            fresh["nameBn"] = bn;
            await _repo.AddDashboardAsync(new MxDashboard
            {
                Name = en, ReportKind = kind, LayoutJson = fresh.ToJsonString(), OwnerUserId = 0, Shared = true, UpdatedAtUtc = DateTime.UtcNow,
            }, ct);
        }
    }

    // ───────────────────────── list, read, change ─────────────────────────

    private static bool Visible(MxDashboard d, User user) => d.Shared || d.OwnerUserId == user.Id;

    private async Task<bool> CanEditAsync(User user, MxDashboard d, CancellationToken ct) =>
        Visible(d, user) && await _access.HasAsync(user, PermissionKeys.MatrixDashboardsEdit, ct);

    private async Task<MxDashboardDto> ToDtoAsync(User user, MxDashboard d, CancellationToken ct)
    {
        var layout = Layout(d);
        return new MxDashboardDto(d.Id, d.Name, layout["nameBn"]?.ToString(), d.ReportKind, layout["auto"]?.ToString(), d.Shared, d.OwnerUserId,
            await CanEditAsync(user, d, ct), JsonSerializer.SerializeToElement(layout), d.UpdatedAtUtc);
    }

    public async Task<List<MxDashboardSummaryDto>> ListDashboardsAsync(User user, CancellationToken ct = default)
    {
        await EnsureAutoDashboardsAsync(ct);
        var canEdit = await _access.HasAsync(user, PermissionKeys.MatrixDashboardsEdit, ct);
        return (await _repo.ListDashboardsAsync(ct)).Where(d => Visible(d, user)).Select(d =>
        {
            var layout = Layout(d);
            return new MxDashboardSummaryDto(d.Id, d.Name, layout["nameBn"]?.ToString(), d.ReportKind, layout["auto"]?.ToString(), d.Shared, d.OwnerUserId,
                canEdit, (layout["widgets"] as JsonArray)?.Count ?? 0, d.UpdatedAtUtc);
        }).ToList();
    }

    public async Task<MxResult<MxDashboardDto>> GetDashboardAsync(User user, int id, CancellationToken ct = default)
    {
        var d = await _repo.GetDashboardAsync(id, ct);
        if (d is null || !Visible(d, user)) return MxResult<MxDashboardDto>.Fail(MxOutcome.NotFound, "No such dashboard.");
        return MxResult<MxDashboardDto>.Success(await ToDtoAsync(user, d, ct));
    }

    private static string? NameProblem(string? name) =>
        name is null ? null : name.Trim().Length is 0 or > 120 ? "A dashboard's name is 1–120 characters." : null;

    public async Task<MxResult<MxDashboardDto>> CreateDashboardAsync(User user, MxDashboardInput input, CancellationToken ct = default)
    {
        if (NameProblem(input.Name) is { } bad) return MxResult<MxDashboardDto>.Fail(MxOutcome.Invalid, bad);
        JsonObject layout;
        string name;
        if (input.CopyOf is { } from)
        {
            var src = await _repo.GetDashboardAsync(from, ct);
            if (src is null || !Visible(src, user)) return MxResult<MxDashboardDto>.Fail(MxOutcome.NotFound, "No such dashboard to copy.");
            layout = Layout(src);
            layout.Remove("auto");
            layout.Remove("nameBn");   // a copy has a name of its own
            name = input.Name?.Trim() ?? $"Copy of {src.Name}";
        }
        else
        {
            layout = input.Layout is { } l ? JsonNode.Parse(l.GetRawText()) as JsonObject ?? new JsonObject() : new JsonObject { ["widgets"] = new JsonArray() };
            layout.Remove("auto");
            name = input.Name?.Trim() ?? "New dashboard";
        }
        if (DashboardLibrary.Problem(JsonSerializer.SerializeToElement(layout), await QueryContextAsync(user, ct)) is { } problem)
            return MxResult<MxDashboardDto>.Fail(MxOutcome.Invalid, problem);
        if (input.ReportKind is not null && !MxReportKind.All.Contains(input.ReportKind))
            return MxResult<MxDashboardDto>.Fail(MxOutcome.Invalid, $"Report kind must be one of: {string.Join(", ", MxReportKind.All)}.");
        var d = await _repo.AddDashboardAsync(new MxDashboard
        {
            Name = name, ReportKind = input.ReportKind, LayoutJson = layout.ToJsonString(), OwnerUserId = user.Id,
            Shared = input.Shared ?? true, UpdatedAtUtc = DateTime.UtcNow,
        }, ct);
        return MxResult<MxDashboardDto>.Success(await ToDtoAsync(user, d, ct));
    }

    public async Task<MxResult<MxDashboardDto>> UpdateDashboardAsync(User user, int id, MxDashboardInput input, CancellationToken ct = default)
    {
        var d = await _repo.GetDashboardAsync(id, ct);
        if (d is null || !Visible(d, user)) return MxResult<MxDashboardDto>.Fail(MxOutcome.NotFound, "No such dashboard.");
        if (NameProblem(input.Name) is { } bad) return MxResult<MxDashboardDto>.Fail(MxOutcome.Invalid, bad);
        if (input.Layout is { } l)
        {
            if (DashboardLibrary.Problem(l, await QueryContextAsync(user, ct)) is { } problem)
                return MxResult<MxDashboardDto>.Fail(MxOutcome.Invalid, problem);
            var old = Layout(d);
            var layout = (JsonNode.Parse(l.GetRawText()) as JsonObject)!;
            // An automatic dashboard stays one, and keeps its Bengali name, whatever the editor sends.
            foreach (var keep in new[] { "auto", "nameBn" })
                if (old[keep] is { } v) layout[keep] = v.DeepClone(); else if (keep == "auto") layout.Remove(keep);
            d.LayoutJson = layout.ToJsonString();
        }
        if (!string.IsNullOrWhiteSpace(input.Name) && input.Name.Trim() != d.Name)
        {
            d.Name = input.Name.Trim();
            // A new name replaces both: the Bengali one no longer says the same.
            var renamed = Layout(d);
            renamed.Remove("nameBn");
            d.LayoutJson = renamed.ToJsonString();
        }
        // Only the owner of a dashboard of their own may make it private.
        if (input.Shared is { } shared && d.OwnerUserId == user.Id) d.Shared = shared;
        d.UpdatedAtUtc = DateTime.UtcNow;
        await _repo.SaveChangesAsync(ct);
        return MxResult<MxDashboardDto>.Success(await ToDtoAsync(user, d, ct));
    }

    public async Task<MxResult<bool>> DeleteDashboardAsync(User user, int id, CancellationToken ct = default)
    {
        var d = await _repo.GetDashboardAsync(id, ct);
        if (d is null || !Visible(d, user)) return MxResult<bool>.Fail(MxOutcome.NotFound, "No such dashboard.");
        if (AutoKey(d) is not null) return MxResult<bool>.Fail(MxOutcome.Invalid, "An automatic dashboard can be changed, not deleted.");
        await _repo.DeleteDashboardAsync(d, ct);
        return MxResult<bool>.Success(true);
    }

    // ───────────────────────── running a dashboard ─────────────────────────

    private static readonly Regex IsoDay = new(@"^\d{4}-\d{2}-\d{2}$");

    /// <summary>The filters as scope conditions; null and an error when a value is not acceptable.</summary>
    private static (List<MxScopeFilter> Scope, string? Error) ScopeOf(MxDashboardFilters? f)
    {
        var s = new List<MxScopeFilter>();
        if (f is null) return (s, null);
        string? error = null;
        void In<T>(string key, List<T>? values)
        {
            if (values is not { Count: > 0 }) return;
            if (values.Count > 200) { error = $"At most 200 {key} values."; return; }
            s.Add(new MxScopeFilter(key, DashboardLibrary.FilterFields[key], "in", new[] { El(values) }));
        }
        In("unit", f.Unit);
        In("section", f.Section);
        In("line", f.Line);
        In("emp_type", f.EmpType);
        In("status", f.Status);
        In("shift", f.Shift);
        if (f.From is not null || f.To is not null)
        {
            if ((f.From is not null && !IsoDay.IsMatch(f.From)) || (f.To is not null && !IsoDay.IsMatch(f.To))) return (s, "Dates are YYYY-MM-DD.");
            s.Add(new MxScopeFilter("date", DashboardLibrary.FilterFields["date"], "between", new[] { El(f.From ?? "0001-01-01"), El(f.To ?? "9999-12-31") }));
        }
        return (s, error);
    }

    /// <summary>The snapshot before <paramref name="d"/> of the same kind and layout, or null.</summary>
    private static MxDataset? PreviousOf(MxQueryContext ctx, MxDataset d) => MatrixQuery.PreviousSnapshot(d, ctx.Datasets);

    public async Task<MxResult<MxDashboardRun>> RunDashboardAsync(User user, int id, MxDashboardRunInput input, CancellationToken ct = default)
    {
        var dash = await _repo.GetDashboardAsync(id, ct);
        if (dash is null || !Visible(dash, user)) return MxResult<MxDashboardRun>.Fail(MxOutcome.NotFound, "No such dashboard.");
        var (scope, error) = ScopeOf(input.Filters);
        if (error is not null) return MxResult<MxDashboardRun>.Fail(MxOutcome.Invalid, error);

        var widgets = (Layout(dash)["widgets"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
        return MxResult<MxDashboardRun>.Success(await RunWidgetsAsync(user, dash.Id, dash.Name, widgets, scope, input, ct));
    }

    /// <summary>A dataset's Report page: the automatic charts of its kind, run on this dataset.</summary>
    public async Task<MxResult<MxDashboardRun>> RunReportAsync(User user, int datasetId, MxDashboardRunInput input, CancellationToken ct = default)
    {
        var d = await _repo.GetDatasetAsync(datasetId, ct);
        if (d is null) return MxResult<MxDashboardRun>.Fail(MxOutcome.NotFound, "No such dataset.");
        var (scope, error) = ScopeOf(input.Filters);
        if (error is not null) return MxResult<MxDashboardRun>.Fail(MxOutcome.Invalid, error);
        return MxResult<MxDashboardRun>.Success(await RunWidgetsAsync(user, d.Id, d.Name, DashboardLibrary.ReportWidgets(d), scope, input, ct));
    }

    private async Task<MxDashboardRun> RunWidgetsAsync(User user, int id, string name, List<JsonObject> widgets, List<MxScopeFilter> scope,
        MxDashboardRunInput input, CancellationToken ct)
    {
        var ctx = await QueryContextAsync(user, ct);
        var defs = (await _repo.ListKpiDefinitionsAsync(ct)).ToDictionary(k => k.Key);
        var used = new Dictionary<int, MxQueryDataset>();
        var results = new List<MxWidgetResult>();
        foreach (var w in widgets)
            results.Add(await RunWidgetAsync(ctx, w, scope, input, defs, used, ct));
        return new MxDashboardRun(id, name, results, await FilterOptionsAsync(ctx, widgets, ct), used.Values.ToList()) { Layout = widgets.Select(w => JsonSerializer.SerializeToElement(w)).ToList() };
    }

    private static JsonElement? ThresholdsOf(Dictionary<string, MxKpiDefinition> defs, string? key)
    {
        if (key is null || !defs.TryGetValue(key, out var d) || string.IsNullOrWhiteSpace(d.ThresholdsJson) || d.ThresholdsJson == "{}") return null;
        var t = JsonDocument.Parse(d.ThresholdsJson).RootElement.Clone();
        return t.TryGetProperty("direction", out _) ? t : null;
    }

    private async Task<MxWidgetResult> RunWidgetAsync(MxQueryContext ctx, JsonObject w, List<MxScopeFilter> scope, MxDashboardRunInput input,
        Dictionary<string, MxKpiDefinition> defs, Dictionary<int, MxQueryDataset> used, CancellationToken ct)
    {
        var id = w["id"]?.ToString() ?? "";
        var type = w["type"]?.ToString() ?? "table";
        var options = w["options"] as JsonObject ?? new JsonObject();
        var titleEn = w["title"]?["en"]?.ToString();
        var titleBn = w["title"]?["bn"]?.ToString();
        var asked = scope.Select(s => s.Key).ToList();
        void Use(MxProvenance? p) { foreach (var x in p?.Datasets ?? new()) used[x.Id] = x; }

        try
        {
            if (w["kpi"]?.ToString() is { } key)
            {
                var kpi = KpiLibrary.Find(key) ?? throw new MxQueryError(400, $"No KPI \"{key}\".");
                var dsRef = w["dataset"]?.ToString();
                var runInput = new MxKpiRunInput(dsRef is null ? null : El(dsRef), null, null, null, scope);
                var r = await RunKpiCoreAsync(ctx, key, runInput, ct);
                Use(r.Provenance);
                var ds = dsRef is not null ? MatrixQuery.Resolve(dsRef, ctx, out _)[0]
                    : r.Provenance?.Datasets.FirstOrDefault() is { } pd ? ctx.Datasets.First(x => x.Id == pd.Id) : null;
                var thresholds = ThresholdsOf(defs, key);

                // The snapshot series, for a trend widget.
                List<MxTrendPoint>? trend = null;
                if (options["series"]?.ToString() == "snapshots" && ds is not null)
                {
                    trend = new();
                    var from = input.Filters?.From ?? "";
                    var to = input.Filters?.To ?? "9999";
                    // A snapshot without a report date has no place on the time axis.
                    foreach (var x in ctx.Datasets.Where(x => x.ReportKind == ds.ReportKind && x.TemplateId == ds.TemplateId && x.ReportDate is not null
                                     && string.CompareOrdinal(x.ReportDate, from) >= 0 && string.CompareOrdinal(x.ReportDate, to) <= 0)
                                 .OrderBy(x => x.ReportDate ?? "").ThenBy(x => x.Id).TakeLast(24))
                    {
                        var p = await RunKpiCoreAsync(ctx, key, runInput with { Dataset = El(x.Id.ToString(CultureInfo.InvariantCulture)) }, ct);
                        trend.Add(new MxTrendPoint(x.Id, x.ReportDate, p.Value, p.Cite));
                    }
                }

                // Against the previous snapshot: a value's change always (the tile's arrow); rows when asked.
                MxWidgetCompare? compare = null;
                if (ds is not null && PreviousOf(ctx, ds) is { } prev && (r.Value is not null || input.Compare))
                {
                    try
                    {
                        var pr = await RunKpiCoreAsync(ctx, key, runInput with { Dataset = El(prev.Id.ToString(CultureInfo.InvariantCulture)) }, ct);
                        compare = new MxWidgetCompare(prev.Id, prev.ReportDate, pr.Value, r.Value,
                            r.Value is { } a && pr.Value is { } b ? Math.Round(a - b, 2) : null, input.Compare && pr.Rows.Count > 0 ? pr.Rows : null);
                    }
                    catch (MxQueryError) { /* the previous snapshot cannot run this KPI: no comparison */ }
                }

                // Per-row state, where the KPI's thresholds speak of each row (operators per line).
                if (thresholds is not null)
                    foreach (var row in r.Rows)
                        row["_state"] = KpiLibrary.StateOf(row.GetValueOrDefault("value") as double?, thresholds);

                var suffix = options["titleSuffix"]?.ToString() is { } sfx ? $" — {sfx}" : "";
                return new MxWidgetResult(id, type, (titleEn ?? r.NameEn) + suffix, (titleBn ?? r.NameBn) + suffix, key, null, runInput,
                    r.Unit, r.Value, r.Text, r.GroupBy is null ? new() : new() { r.GroupBy }, new() { "value" }, r.Rows, r.Summary, r.Detail, r.Cite,
                    KpiLibrary.StateOf(r.Value, thresholds), thresholds, compare, trend, r.ScopeApplied, asked.Except(r.ScopeApplied).ToList(),
                    ds?.Id, ds?.ReportDate, null);
            }

            var q = (w["query"] as JsonObject ?? throw new MxQueryError(400, "The widget has neither a KPI nor a query.")).DeepClone().AsObject();
            var applied = MatrixQuery.ApplyScope(q, scope, ctx);
            var query = JsonSerializer.SerializeToElement(q);
            var res = await RunQueryAsync(ctx, query, false, ct);
            Use(res.Provenance);
            var groups = res.Columns.Where(c => c.Role == "group").Select(c => c.Key).ToList();
            var measures = res.Columns.Where(c => c.Role == "measure").Select(c => c.Key).ToList();
            var rowThresholds = ThresholdsOf(defs, options["thresholdsOf"]?.ToString());
            if (rowThresholds is not null && options["status"]?.ToString() is { } statusOf)
                foreach (var row in res.Rows)
                    row["_state"] = KpiLibrary.StateOf(row.GetValueOrDefault(statusOf) switch { long l => l, double x => x, _ => null }, rowThresholds);

            var baseSet = res.Provenance.Datasets.FirstOrDefault();
            MxWidgetCompare? cmp = null;
            if (input.Compare && baseSet is not null && res.Provenance.Datasets.Count(x => x.Kind == baseSet.Kind) == 1
                && ctx.Datasets.First(x => x.Id == baseSet.Id) is var bd && PreviousOf(ctx, bd) is { } prevSet)
            {
                var pq = q.DeepClone().AsObject();
                pq["dataset"] = prevSet.Id.ToString(CultureInfo.InvariantCulture);
                try { cmp = new MxWidgetCompare(prevSet.Id, prevSet.ReportDate, null, null, null, (await RunQueryAsync(ctx, JsonSerializer.SerializeToElement(pq), false, ct)).Rows); }
                catch (MxQueryError) { /* not comparable */ }
            }
            return new MxWidgetResult(id, type, titleEn ?? id, titleBn ?? titleEn ?? id, null, query, null,
                options["unit"]?.ToString() ?? "people", null, null, groups, measures, res.Rows, null, res.Provenance.Plain, null,
                null, rowThresholds, cmp, null, applied, asked.Except(applied).ToList(), baseSet?.Id, baseSet?.ReportDate, null);
        }
        catch (MxQueryError e)
        {
            return new MxWidgetResult(id, type, titleEn ?? w["kpi"]?.ToString() ?? id, titleBn ?? titleEn ?? id, w["kpi"]?.ToString(), null, null,
                null, null, null, new(), new(), new(), null, null, null, null, null, null, null, new(), asked, null, null, e.Message);
        }
    }

    /// <summary>The values each filter can take, from the datasets the widgets read (dimension fields; never personal).</summary>
    private async Task<Dictionary<string, List<object>>> FilterOptionsAsync(MxQueryContext ctx, List<JsonObject> widgets, CancellationToken ct)
    {
        var refs = widgets.Select(w => w["dataset"]?.ToString() ?? (w["query"] as JsonObject)?["dataset"]?.ToString()).Where(r => r is not null).Distinct();
        var sets = new List<MxDataset>();
        foreach (var r in refs)
            try { sets.AddRange(MatrixQuery.Resolve(r, ctx, out _).Take(1)); } catch (MxQueryError) { /* not imported yet */ }
        var options = new Dictionary<string, List<object>>();
        foreach (var (key, fields) in DashboardLibrary.FilterFields.Where(f => f.Key != "date"))
        {
            var values = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var numbers = new SortedSet<long>();
            foreach (var d in sets.DistinctBy(d => d.Id))
            {
                var field = fields.FirstOrDefault(f => ctx.ColumnsOf(d).Any(c => c.Key == f));
                if (field is null) continue;
                var res = await RunQueryAsync(ctx, El(new
                {
                    dataset = d.Id.ToString(CultureInfo.InvariantCulture), filter = new object[] { new object[] { field, "not_null" } },
                    group_by = new[] { field }, measures = new object[] { new object[] { "count", "*", "n" } }, limit = 500,
                }), false, ct);
                foreach (var row in res.Rows)
                    if (row[field] is long l) numbers.Add(l);
                    else if (row[field]?.ToString() is { Length: > 0 } s) values.Add(s);
            }
            if (numbers.Count > 0) options[key] = numbers.Cast<object>().ToList();
            else if (values.Count > 0) options[key] = values.Cast<object>().ToList();
        }
        return options;
    }

    // ───────────────────────── export ─────────────────────────

    private static readonly Regex FileSafe = new(@"[^A-Za-z0-9._-]+");

    /// <summary>
    /// A dataset as .xlsx or .csv — corrected values, the source row of each, no sensitive
    /// columns, and no personal or salary columns the caller may not see — or a dashboard's layout as .json.
    /// </summary>
    public async Task<MxResult<MxExportFile>> ExportAsync(User user, string kind, int id, CancellationToken ct = default)
    {
        if (kind == "dashboard")
        {
            var dash = await _repo.GetDashboardAsync(id, ct);
            if (dash is null || !Visible(dash, user)) return MxResult<MxExportFile>.Fail(MxOutcome.NotFound, "No such dashboard.");
            var json = Encoding.UTF8.GetBytes(new JsonObject { ["name"] = dash.Name, ["reportKind"] = dash.ReportKind, ["layout"] = Layout(dash) }
                .ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return MxResult<MxExportFile>.Success(new MxExportFile($"{FileSafe.Replace(dash.Name, "-")}.dashboard.json", "application/json", json, new()));
        }
        if (kind is not ("xlsx" or "csv")) return MxResult<MxExportFile>.Fail(MxOutcome.Invalid, "Export kind is xlsx, csv or dashboard.");

        var d = await _repo.GetDatasetAsync(id, ct);
        if (d is null) return MxResult<MxExportFile>.Fail(MxOutcome.NotFound, "No such dataset.");
        var ctx = await QueryContextAsync(user, ct);
        var cols = Columns(d).Where(c => c.Sensitivity switch
        {
            MxSensitivity.Sensitive => false,
            MxSensitivity.Personal => ctx.CanPersonal,
            MxSensitivity.Salary => ctx.CanSalary,
            _ => true,
        }).ToList();
        var (_, rows) = await _repo.RowsAsync(d, cols.Select(c => c.Key).ToList(), "", Array.Empty<object>(), null, 0, Math.Max(1, d.RowCount), ct);
        var headers = new List<string> { "Source row" }.Concat(cols.Select(c => c.Derived ? FieldCatalog.Find(c.Field)?.En ?? c.Key : c.Header.Trim())).ToList();
        // The dataset's name already says file, sheet and report date.
        var stem = FileSafe.Replace(d.Name, "-").Trim('-');

        if (kind == "csv")
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", headers.Select(Csv)));
            foreach (var r in rows)
                sb.AppendLine(string.Join(",", new[] { r["_row"] }.Concat(cols.Select(c => r[c.Key])).Select(v => Csv(v switch
                {
                    null => "",
                    double x => x.ToString(CultureInfo.InvariantCulture),
                    _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
                }))));
            // With a byte-order mark, so Excel reads Bengali text as UTF-8.
            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
            return MxResult<MxExportFile>.Success(new MxExportFile(stem + ".csv", "text/csv; charset=utf-8", bytes, headers));
        }

        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet(SheetName(d.Sheet));
        for (var i = 0; i < headers.Count; i++) ws.Cell(1, i + 1).Value = headers[i];
        ws.Row(1).Style.Font.Bold = true;
        var n = 2;
        foreach (var r in rows)
        {
            ws.Cell(n, 1).Value = Convert.ToInt64(r["_row"], CultureInfo.InvariantCulture);
            for (var i = 0; i < cols.Count; i++)
            {
                var cell = ws.Cell(n, i + 2);
                switch (r[cols[i].Key])
                {
                    case null: break;
                    case long l: cell.Value = l; break;
                    case double x: cell.Value = x; break;
                    case string s when cols[i].Type == MxType.Date && DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day):
                        cell.Value = day;
                        cell.Style.DateFormat.Format = "yyyy-mm-dd";
                        break;
                    // Text stays text: an ID's leading zeros are kept.
                    case var v: cell.Value = Convert.ToString(v, CultureInfo.InvariantCulture); break;
                }
            }
            n++;
        }
        ws.SheetView.FreezeRows(1);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return MxResult<MxExportFile>.Success(new MxExportFile(stem + ".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ms.ToArray(), headers));
    }

    /// <summary>A CSV field: quoted when needed; text a spreadsheet would run as a formula is prefixed with an apostrophe.</summary>
    private static string Csv(string s)
    {
        if (s.Length > 0 && "=+-@\t\r".Contains(s[0]) && !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) s = "'" + s;
        return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
    }

    private static string SheetName(string sheet)
    {
        var clean = new string(sheet.Trim().Where(c => !"[]:*?/\\".Contains(c)).ToArray());
        return clean.Length == 0 ? "Data" : clean.Length > 31 ? clean[..31] : clean;
    }
}
