// এইচআর ওয়ার্কস্পেস · HRIS Workspace — the Reports board (docs/HRIS_WORKSPACE_PROMPT.md §3.2, §3.5).
//
// A front door, not an engine. Every number a pin shows is Matrix's: a chart or KPI pin
// is a dashboard widget run through IMatrixService.RunWidgetsAsync (the same KPIs, the
// same MatrixQuery validator, the same salary and personal checks, for this user); a
// card or summary pin is Matrix's saved card or summary of the current dataset of a
// kind. What lives here is only which pins a board holds, in what order, and the
// board's filters turned into Matrix's scope conditions.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.Data;
using RabbitIC.Api.DTOs.Matrix;
using RabbitIC.Api.Matrix;
using RabbitIC.Api.Models;
using RabbitIC.Api.Rbac;
using RabbitIC.Api.Services;

namespace RabbitIC.Api.Hris;

/// <summary>The board's filter bar (§3.2). From/To: ISO dates. Each reaches the pins whose dataset has the field.</summary>
public sealed record HrisFilters(List<string>? Unit, string? From, string? To, List<string>? Department, List<string>? Section,
    List<string>? LineType, List<int>? Line);

public sealed record HrisPinDto(int Id, string Board, string Kind, string TitleEn, string TitleBn, JsonElement Payload, int Order,
    string Size, bool CanEdit, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record HrisPinInput(string? Board, string? Kind, string? TitleEn, string? TitleBn, JsonElement? Payload, string? Size);

public sealed record HrisOrderInput(string? Board, List<int>? Ids);

public sealed record HrisRenderInput(string? Board, List<int>? Ids, HrisFilters? Filters);

/// <summary>
/// One pin, rendered. Chart and KPI pins carry Matrix's widget results; a card pin the card;
/// a summary pin the summary. Unavailable: the KPI this data cannot give, with the card's
/// reason — and then no number. NotApplicable: the filters this pin's dataset has no field for.
/// </summary>
public sealed record HrisPinRender(int Id, string Kind, string? ReportDate, string? Source, int? DatasetId,
    List<MxWidgetResult>? Widgets, MxReportCardDto? Card, MxSummaryDto? Summary, JsonElement? Answer,
    List<string> NotApplicable, List<HrisUnavailable> Unavailable, string? Error);

public sealed record HrisUnavailable(string Kpi, string Label, string LabelBn, string Reason);

public sealed record HrisRenderResult(List<HrisPinRender> Pins, Dictionary<string, List<object>> FilterOptions);

public sealed record HrisStatusKind(string Kind, int DatasetId, string Name, string? ReportDate, int OpenIssues, List<MxCardUnavailable> Unavailable);

public sealed record HrisStatus(List<HrisStatusKind> Datasets, DateTime? LastRulebookConfirmation, string? LastRulebookPage);

public sealed class HrisService(AppDbContext db, IMatrixService matrix, AccessPolicy access)
{
    public const int MaxPinsPerBoard = 60;
    private static readonly string[] Sizes = { "S", "M", "L" };
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // ───────────────────────── seeding (§3.2) ─────────────────────────

    private sealed record Starter(string Key, string Kind, string En, string Bn, string Size, string Payload);

    private const string M = "current:master";
    private const string A = "current:attendance-daily";
    private const string P = "current:manpower";

    /// <summary>The 8 starter pins, taken from the automatic Matrix dashboards' own widgets.</summary>
    private static readonly Starter[] Starters =
    {
        new("headcount", HrisPinKind.Kpi, "Active headcount", "সক্রিয় জনবল", "S",
            $$$"""{"widgets":[{"id":"headcount","type":"kpi","kpi":"active_headcount","dataset":"{{{M}}}"}]}"""),
        new("by-unit", HrisPinKind.Chart, "Headcount by unit", "ইউনিটভিত্তিক জনবল", "M",
            $$$"""{"widgets":[{"id":"by-unit","type":"bar","kpi":"headcount_by_unit","dataset":"{{{M}}}"}]}"""),
        new("lines", HrisPinKind.Chart, "Sewing lines and operators per line", "সেলাই লাইন ও লাইনপ্রতি অপারেটর", "L",
            $$$"""{"widgets":[{"id":"lines","type":"kpi","kpi":"sewing_lines_total","dataset":"{{{M}}}"},{"id":"operators","type":"bar","kpi":"operators_per_line","dataset":"{{{M}}}","options":{"target":20}}]}"""),
        new("female", HrisPinKind.Kpi, "Female %", "নারী কর্মী %", "S",
            $$$"""{"widgets":[{"id":"female","type":"kpi","kpi":"female_pct","dataset":"{{{M}}}"}]}"""),
        new("attrition", HrisPinKind.Chart, "Attrition trend", "অ্যাট্রিশনের প্রবণতা", "M",
            // The Joiners and separations dashboard's pairing: attrition % (with its change since the previous
            // snapshot) and the headcount line over the snapshots. Matrix's snapshot series runs a KPI on every
            // snapshot, and attrition % has no value on the first one (no snapshot before it), so it is not a line.
            $$$"""{"widgets":[{"id":"attrition","type":"kpi","kpi":"attrition_pct","dataset":"{{{P}}}"},{"id":"trend","type":"line","kpi":"active_headcount","dataset":"{{{P}}}","options":{"series":"snapshots"}}]}"""),
        new("attendance", HrisPinKind.Kpi, "Today's attendance and late %", "আজকের হাজিরা ও লেট %", "M",
            $$$"""{"widgets":[{"id":"att-pct","type":"kpi","kpi":"attendance_pct","dataset":"{{{A}}}"},{"id":"late-pct","type":"kpi","kpi":"late_pct","dataset":"{{{A}}}"}]}"""),
        new("manpower-card", HrisPinKind.Card, "Latest manpower card", "সর্বশেষ জনবল কার্ড", "L",
            """{"dataset":"current:manpower"}"""),
        new("summary", HrisPinKind.Summary, "Latest summary", "সর্বশেষ সারসংক্ষেপ", "L",
            """{"dataset":null,"language":"bn"}"""),
    };

    /// <summary>Gives the shared board its starter pins once. A restart, or HR removing them all, adds nothing.</summary>
    public static void Seed(AppDbContext db, ILogger logger)
    {
        if (db.HrisBoardSeeds.Any(s => s.Board == HrisBoard.Shared)) return;
        var now = DateTime.UtcNow;
        var order = 0;
        foreach (var s in Starters)
            db.HrisPins.Add(new HrisPin
            {
                Board = HrisBoard.Shared, Kind = s.Kind, TitleEn = s.En, TitleBn = s.Bn, PayloadJson = s.Payload,
                Order = order++, Size = s.Size, SeedKey = s.Key, CreatedAt = now, UpdatedAt = now,
            });
        db.HrisBoardSeeds.Add(new HrisBoardSeed { Board = HrisBoard.Shared, SeededAtUtc = now });
        db.SaveChanges();
        logger.LogInformation("HRIS: seeded the shared board with {Count} starter pins.", Starters.Length);
    }

    // ───────────────────────── pins ─────────────────────────

    private IQueryable<HrisPin> BoardOf(User user, string board) => board == HrisBoard.Shared
        ? db.HrisPins.Where(p => p.Board == HrisBoard.Shared)
        : db.HrisPins.Where(p => p.Board == HrisBoard.Personal && p.OwnerUserId == user.Id);

    private async Task<bool> CanEditAsync(User user, string board, CancellationToken ct) =>
        board == HrisBoard.Personal || await access.HasAsync(user, PermissionKeys.HrisPin, ct);

    private static HrisPinDto Dto(HrisPin p, bool canEdit) => new(p.Id, p.Board, p.Kind, p.TitleEn, p.TitleBn,
        JsonDocument.Parse(p.PayloadJson).RootElement.Clone(), p.Order, p.Size, canEdit, p.CreatedAt, p.UpdatedAt);

    public async Task<MxResult<List<HrisPinDto>>> ListAsync(User user, string? board, CancellationToken ct)
    {
        board ??= HrisBoard.Shared;
        if (!HrisBoard.IsKnown(board)) return MxResult<List<HrisPinDto>>.Fail(MxOutcome.Invalid, "board is shared or personal.");
        var canEdit = await CanEditAsync(user, board, ct);
        var pins = await BoardOf(user, board).OrderBy(p => p.Order).ThenBy(p => p.Id).ToListAsync(ct);
        return MxResult<List<HrisPinDto>>.Success(pins.Select(p => Dto(p, canEdit)).ToList());
    }

    /// <summary>Why a pin cannot be saved, or null. A chart or KPI pin's widgets are run for the saving user: a pin holds nothing they could not run.</summary>
    private async Task<string?> ProblemAsync(User user, string kind, JsonElement payload, CancellationToken ct)
    {
        if (payload.ValueKind != JsonValueKind.Object) return "payload is an object.";
        if (payload.GetRawText().Length > 100_000) return "payload is larger than 100 KB.";
        switch (kind)
        {
            case HrisPinKind.Chart or HrisPinKind.Kpi:
                if (!payload.TryGetProperty("widgets", out var ws) || ws.ValueKind != JsonValueKind.Array || ws.GetArrayLength() is < 1 or > 4)
                    return "A chart or KPI pin has 1–4 widgets.";
                var widgets = WidgetsOf(payload);
                foreach (var w in widgets)
                {
                    if (w["type"]?.ToString() is not { } t || !DashboardLibrary.WidgetTypes.Contains(t)) return $"widget type must be one of {string.Join(", ", DashboardLibrary.WidgetTypes)}.";
                    if (w["kpi"] is null == w["query"] is null) return "each widget gives either \"kpi\" or \"query\".";
                    if (w["kpi"]?.ToString() is { } k && KpiLibrary.Find(k) is null) return $"no KPI \"{k}\".";
                }
                // The query widgets through Matrix's own validator, as this user.
                foreach (var w in widgets.Where(w => w["query"] is not null))
                    if (await matrix.QueryAsync(user, JsonSerializer.SerializeToElement(w["query"]), false, ct) is { Outcome: not MxOutcome.Ok } r)
                        return r.Error;
                return null;
            case HrisPinKind.Card or HrisPinKind.Summary:
                var ds = payload.TryGetProperty("dataset", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                if (ds is not null && !(ds.StartsWith("current:", StringComparison.Ordinal) && MxReportKind.All.Contains(ds["current:".Length..])))
                    return "dataset is current:<kind>, or null for the latest.";
                return null;
            case HrisPinKind.Answer:
                return payload.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String ? null : "an answer pin keeps the answer's text.";
            default:
                return $"kind is one of {string.Join(", ", HrisPinKind.All)}.";
        }
    }

    public async Task<MxResult<HrisPinDto>> CreateAsync(User user, HrisPinInput input, CancellationToken ct)
    {
        var board = input.Board ?? HrisBoard.Personal;
        if (!HrisBoard.IsKnown(board)) return MxResult<HrisPinDto>.Fail(MxOutcome.Invalid, "board is shared or personal.");
        if (!await CanEditAsync(user, board, ct))
            return MxResult<HrisPinDto>.Fail(MxOutcome.Forbidden, $"The shared board needs {PermissionKeys.HrisPin}.");
        var kind = input.Kind ?? "";
        if (input.Payload is not { } payload) return MxResult<HrisPinDto>.Fail(MxOutcome.Invalid, "payload is needed.");
        if (await ProblemAsync(user, kind, payload, ct) is { } problem) return MxResult<HrisPinDto>.Fail(MxOutcome.Invalid, problem);
        if (TitleProblem(input.TitleEn, input.TitleBn) is { } tp) return MxResult<HrisPinDto>.Fail(MxOutcome.Invalid, tp);
        var size = input.Size ?? "M";
        if (!Sizes.Contains(size)) return MxResult<HrisPinDto>.Fail(MxOutcome.Invalid, "size is S, M or L.");
        var count = await BoardOf(user, board).CountAsync(ct);
        if (count >= MaxPinsPerBoard) return MxResult<HrisPinDto>.Fail(MxOutcome.Invalid, $"A board holds at most {MaxPinsPerBoard} pins.");

        var now = DateTime.UtcNow;
        var pin = new HrisPin
        {
            Board = board, OwnerUserId = board == HrisBoard.Personal ? user.Id : null, Kind = kind,
            TitleEn = input.TitleEn?.Trim() ?? "", TitleBn = input.TitleBn?.Trim() ?? input.TitleEn?.Trim() ?? "",
            PayloadJson = payload.GetRawText(), Order = count == 0 ? 0 : await BoardOf(user, board).MaxAsync(p => p.Order, ct) + 1,
            Size = size, CreatedBy = user.Id, CreatedAt = now, UpdatedAt = now,
        };
        db.HrisPins.Add(pin);
        await db.SaveChangesAsync(ct);
        return MxResult<HrisPinDto>.Success(Dto(pin, true));
    }

    private static string? TitleProblem(string? en, string? bn) =>
        string.IsNullOrWhiteSpace(en) && string.IsNullOrWhiteSpace(bn) ? "A pin needs a title."
        : (en?.Length ?? 0) > 120 || (bn?.Length ?? 0) > 120 ? "A title is at most 120 characters." : null;

    /// <summary>The pin, if this user may change it: their own personal pin, or a shared one with action:hris.pin.</summary>
    private async Task<(HrisPin? Pin, MxOutcome Outcome, string? Error)> EditableAsync(User user, int id, CancellationToken ct)
    {
        var pin = await db.HrisPins.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pin is null || (pin.Board == HrisBoard.Personal && pin.OwnerUserId != user.Id)) return (null, MxOutcome.NotFound, "No such pin.");
        if (!await CanEditAsync(user, pin.Board, ct)) return (null, MxOutcome.Forbidden, $"The shared board needs {PermissionKeys.HrisPin}.");
        return (pin, MxOutcome.Ok, null);
    }

    public async Task<MxResult<HrisPinDto>> UpdateAsync(User user, int id, HrisPinInput input, CancellationToken ct)
    {
        var (pin, outcome, error) = await EditableAsync(user, id, ct);
        if (pin is null) return MxResult<HrisPinDto>.Fail(outcome, error!);
        if (input.Payload is { } payload)
        {
            if (await ProblemAsync(user, pin.Kind, payload, ct) is { } problem) return MxResult<HrisPinDto>.Fail(MxOutcome.Invalid, problem);
            pin.PayloadJson = payload.GetRawText();
        }
        if (input.TitleEn is not null || input.TitleBn is not null)
        {
            if (TitleProblem(input.TitleEn ?? pin.TitleEn, input.TitleBn ?? pin.TitleBn) is { } tp) return MxResult<HrisPinDto>.Fail(MxOutcome.Invalid, tp);
            pin.TitleEn = input.TitleEn?.Trim() ?? pin.TitleEn;
            pin.TitleBn = input.TitleBn?.Trim() ?? pin.TitleBn;
        }
        if (input.Size is not null)
        {
            if (!Sizes.Contains(input.Size)) return MxResult<HrisPinDto>.Fail(MxOutcome.Invalid, "size is S, M or L.");
            pin.Size = input.Size;
        }
        pin.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return MxResult<HrisPinDto>.Success(Dto(pin, true));
    }

    public async Task<MxResult<bool>> DeleteAsync(User user, int id, CancellationToken ct)
    {
        var (pin, outcome, error) = await EditableAsync(user, id, ct);
        if (pin is null) return MxResult<bool>.Fail(outcome, error!);
        db.HrisPins.Remove(pin);
        await db.SaveChangesAsync(ct);
        return MxResult<bool>.Success(true);
    }

    /// <summary>Puts a board's pins in the order given; every pin of the board must be named once.</summary>
    public async Task<MxResult<List<HrisPinDto>>> OrderAsync(User user, HrisOrderInput input, CancellationToken ct)
    {
        var board = input.Board ?? HrisBoard.Shared;
        if (!HrisBoard.IsKnown(board)) return MxResult<List<HrisPinDto>>.Fail(MxOutcome.Invalid, "board is shared or personal.");
        if (!await CanEditAsync(user, board, ct))
            return MxResult<List<HrisPinDto>>.Fail(MxOutcome.Forbidden, $"The shared board needs {PermissionKeys.HrisPin}.");
        var pins = await BoardOf(user, board).ToListAsync(ct);
        var ids = input.Ids ?? new();
        if (ids.Count != pins.Count || ids.Distinct().Count() != ids.Count || ids.Any(i => pins.All(p => p.Id != i)))
            return MxResult<List<HrisPinDto>>.Fail(MxOutcome.Invalid, "ids lists every pin of the board once.");
        for (var i = 0; i < ids.Count; i++) pins.First(p => p.Id == ids[i]).Order = i;
        await db.SaveChangesAsync(ct);
        return await ListAsync(user, board, ct);
    }

    // ───────────────────────── rendering ─────────────────────────

    /// <summary>The filter bar as Matrix's scope conditions: Matrix's own for the dashboard filters, plus department and line type.</summary>
    public static (List<MxScopeFilter> Scope, string? Error) ScopeOf(HrisFilters? f)
    {
        if (f is null) return (new(), null);
        var (scope, error) = MatrixService.ScopeOf(new MxDashboardFilters(f.Unit, f.Section, f.Line, null, null, null, f.From, f.To));
        if (error is not null) return (scope, error);
        foreach (var (key, values) in new[] { ("department", f.Department), ("line_type", f.LineType) })
        {
            if (values is not { Count: > 0 }) continue;
            if (values.Count > 200) return (scope, $"At most 200 {key} values.");
            scope.Add(new MxScopeFilter(key, new[] { key }, "in", new[] { JsonSerializer.SerializeToElement(values) }));
        }
        return (scope, null);
    }

    private static List<JsonObject> WidgetsOf(JsonElement payload) =>
        (JsonNode.Parse(payload.GetRawText())?["widgets"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Select(w => w.DeepClone().AsObject()).ToList();

    private static string? DatasetRefOf(JsonObject w) => w["dataset"]?.ToString() ?? w["query"]?["dataset"]?.ToString();

    public async Task<MxResult<HrisRenderResult>> RenderAsync(User user, HrisRenderInput input, CancellationToken ct)
    {
        var board = input.Board ?? HrisBoard.Shared;
        if (!HrisBoard.IsKnown(board)) return MxResult<HrisRenderResult>.Fail(MxOutcome.Invalid, "board is shared or personal.");
        var (scope, error) = ScopeOf(input.Filters);
        if (error is not null) return MxResult<HrisRenderResult>.Fail(MxOutcome.Invalid, error);
        var q = BoardOf(user, board);
        if (input.Ids is { Count: > 0 } ids) q = q.Where(p => ids.Contains(p.Id));
        var pins = await q.OrderBy(p => p.Order).ThenBy(p => p.Id).ToListAsync(ct);
        return MxResult<HrisRenderResult>.Success(await RenderPinsAsync(user, pins, scope, ct));
    }

    private async Task<HrisRenderResult> RenderPinsAsync(User user, List<HrisPin> pins, List<MxScopeFilter> scope, CancellationToken ct)
    {
        var asked = scope.Select(s => s.Key).ToList();
        var datasets = await matrix.ListDatasetsAsync(ct);
        MxDatasetDto? Current(string? reference) => reference is not null && reference.StartsWith("current:", StringComparison.Ordinal)
            ? datasets.FirstOrDefault(d => d.IsCurrent && d.Kind == reference["current:".Length..]) : null;
        var cards = new Dictionary<int, MxReportCardDto?>();
        async Task<MxReportCardDto?> CardOf(int id)
        {
            if (!cards.TryGetValue(id, out var c)) cards[id] = c = (await matrix.CardAsync(user, id, ct)).Value;
            return c;
        }

        // Every chart and KPI widget of the board in one Matrix run, so they share one query context.
        var widgetPins = pins.Where(p => p.Kind is HrisPinKind.Chart or HrisPinKind.Kpi).ToList();
        var all = new List<JsonObject>();
        foreach (var p in widgetPins)
            foreach (var w in WidgetsOf(JsonDocument.Parse(p.PayloadJson).RootElement))
            {
                w["id"] = $"p{p.Id}-{w["id"]}";
                all.Add(w);
            }
        var run = all.Count > 0 ? await matrix.RunWidgetsAsync(user, 0, "hris", all, scope, new MxDashboardRunInput(null), ct) : null;
        var results = run?.Widgets.ToDictionary(w => w.Id) ?? new();
        var options = run?.FilterOptions ?? new Dictionary<string, List<object>>();

        var list = new List<HrisPinRender>();
        foreach (var p in pins)
        {
            var payload = JsonDocument.Parse(p.PayloadJson).RootElement;
            switch (p.Kind)
            {
                case HrisPinKind.Chart or HrisPinKind.Kpi:
                {
                    var ws = WidgetsOf(payload);
                    var rs = ws.Select(w => results.GetValueOrDefault($"p{p.Id}-{w["id"]}")).OfType<MxWidgetResult>().ToList();
                    var unavailable = new List<HrisUnavailable>();
                    for (var i = 0; i < ws.Count && i < rs.Count; i++)
                    {
                        var r = rs[i];
                        // A KPI the data cannot give: the card's reason, and never a number in its place.
                        if (r.Kpi is null || r.Value is not null || r.Rows.Count > 0 || r.Trend is { Count: > 0 }) continue;
                        var dsId = r.DatasetId ?? Current(DatasetRefOf(ws[i]))?.Id;
                        var card = dsId is { } id ? await CardOf(id) : null;
                        var na = card?.Unavailable?.FirstOrDefault(u => u.Key == r.Kpi);
                        unavailable.Add(new HrisUnavailable(r.Kpi, na?.Label ?? r.NameEn, na?.LabelBn ?? r.NameBn,
                            na?.Reason ?? r.Error ?? (dsId is null ? "No dataset of this kind has been imported yet." : "This data cannot give it.")));
                    }
                    var first = rs.FirstOrDefault(r => r.DatasetId is not null);
                    // A filter any of the pin's widgets could not take (each widget's own list says which).
                    var ignored = rs.Count == 0 ? asked : asked.Where(k => rs.Any(r => r.FiltersIgnored.Contains(k))).ToList();
                    var source = first?.DatasetId is { } fid ? datasets.FirstOrDefault(d => d.Id == fid)?.Name : null;
                    list.Add(new HrisPinRender(p.Id, p.Kind, first?.ReportDate, source, first?.DatasetId, rs, null, null, null,
                        ignored, unavailable, rs.Count == ws.Count ? null : "Matrix gave no result for this pin."));
                    break;
                }
                case HrisPinKind.Card:
                {
                    var d = Current(payload.TryGetProperty("dataset", out var dr) ? dr.GetString() : null);
                    var card = d is null ? null : await CardOf(d.Id);
                    list.Add(new HrisPinRender(p.Id, p.Kind, card?.ReportDate ?? d?.ReportDate, d?.Name, d?.Id, null, card, null, null, asked,
                        card?.Unavailable?.Select(u => new HrisUnavailable(u.Key, u.Label, u.LabelBn, u.Reason)).ToList() ?? new(),
                        d is null ? "No dataset of this kind has been imported yet." : card is null ? "The report card could not be made." : null));
                    break;
                }
                case HrisPinKind.Summary:
                {
                    var lang = payload.TryGetProperty("language", out var l) && l.GetString() is "en" ? "en" : "bn";
                    var reference = payload.TryGetProperty("dataset", out var dr) && dr.ValueKind == JsonValueKind.String ? dr.GetString() : null;
                    // The named kind's current dataset, or the latest current dataset that has a summary in this language.
                    var candidates = reference is not null ? new[] { Current(reference) }.OfType<MxDatasetDto>()
                        : datasets.Where(d => d.IsCurrent).OrderByDescending(d => d.ReportDate ?? "").ThenByDescending(d => d.Id);
                    MxSummaryDto? summary = null;
                    MxDatasetDto? from = null;
                    foreach (var d in candidates)
                        if ((await matrix.SummaryAsync(user, d.Id, lang, ct)).Value is { } s) { summary = s; from = d; break; }
                    list.Add(new HrisPinRender(p.Id, p.Kind, from?.ReportDate, from?.Name, from?.Id, null, null, summary, null, asked, new(),
                        summary is null ? "No summary has been written yet (Matrix → a dataset's Report page)." : null));
                    break;
                }
                default:
                    // A frozen answer: shown as of its date, never re-run, so no filter reaches it.
                    list.Add(new HrisPinRender(p.Id, p.Kind, payload.TryGetProperty("date", out var dt) ? dt.GetString() : null,
                        null, null, null, null, null, payload.Clone(), asked, new(), null));
                    break;
            }
        }

        await AddOptionsAsync(user, datasets, options, ct);
        return new HrisRenderResult(list, options);
    }

    /// <summary>Department and line-type values for the filter bar, from the current datasets that have them (Matrix gave the rest).</summary>
    private async Task AddOptionsAsync(User user, List<MxDatasetDto> datasets, Dictionary<string, List<object>> options, CancellationToken ct)
    {
        foreach (var field in new[] { "department", "line_type" })
        {
            var values = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in datasets.Where(d => d.IsCurrent && d.Columns.Any(c => c.Key == field)))
            {
                var r = await matrix.QueryAsync(user, JsonSerializer.SerializeToElement(new
                {
                    dataset = d.Id.ToString(CultureInfo.InvariantCulture), filter = new object[] { new object[] { field, "not_null" } },
                    group_by = new[] { field }, measures = new object[] { new object[] { "count", "*", "n" } }, limit = 500,
                }), false, ct);
                foreach (var row in r.Value?.Rows ?? new())
                    if (row.GetValueOrDefault(field)?.ToString() is { Length: > 0 } s) values.Add(s);
            }
            if (values.Count > 0) options[field] = values.Cast<object>().ToList();
        }
    }

    // ───────────────────────── status (§3.4) ─────────────────────────

    public async Task<HrisStatus> StatusAsync(User user, CancellationToken ct)
    {
        var list = new List<HrisStatusKind>();
        foreach (var d in (await matrix.ListDatasetsAsync(ct)).Where(d => d.IsCurrent).OrderBy(d => Array.IndexOf(MxReportKind.All, d.Kind)))
        {
            var card = (await matrix.CardAsync(user, d.Id, ct)).Value;
            list.Add(new HrisStatusKind(d.Kind, d.Id, d.Name, d.ReportDate, d.OpenIssues, card?.Unavailable ?? new()));
        }
        var confirmed = await db.HrRulePageVersions.AsNoTracking().Where(v => v.Kind == HrRulePageVersion.KindConfirmed)
            .OrderByDescending(v => v.AtUtc).Select(v => new { v.AtUtc, v.PageId }).FirstOrDefaultAsync(ct);
        string? page = confirmed is null ? null
            : await db.HrRulePages.AsNoTracking().Where(p => p.Id == confirmed.PageId).Select(p => p.Title).FirstOrDefaultAsync(ct);
        return new HrisStatus(list, confirmed?.AtUtc, page);
    }

    // ───────────────────────── export (§3.2) ─────────────────────────

    /// <summary>
    /// A pin as .xlsx: its figures, and — for someone who may open Matrix's data — the rows of its
    /// dataset from Matrix's own exporter (no sensitive columns; personal and salary only when
    /// permitted). The whole board: one sheet of figures per pin.
    /// </summary>
    public async Task<MxResult<MxExportFile>> ExportAsync(User user, string kind, string? board, int? pinId, HrisFilters? filters, CancellationToken ct)
    {
        var (scope, error) = ScopeOf(filters);
        if (error is not null) return MxResult<MxExportFile>.Fail(MxOutcome.Invalid, error);
        board ??= HrisBoard.Shared;
        if (!HrisBoard.IsKnown(board)) return MxResult<MxExportFile>.Fail(MxOutcome.Invalid, "board is shared or personal.");
        List<HrisPin> pins;
        if (kind == "pin")
        {
            var pin = await db.HrisPins.FirstOrDefaultAsync(p => p.Id == pinId, ct);
            if (pin is null || (pin.Board == HrisBoard.Personal && pin.OwnerUserId != user.Id)) return MxResult<MxExportFile>.Fail(MxOutcome.NotFound, "No such pin.");
            pins = new() { pin };
        }
        else if (kind == "board") pins = await BoardOf(user, board).OrderBy(p => p.Order).ThenBy(p => p.Id).ToListAsync(ct);
        else return MxResult<MxExportFile>.Fail(MxOutcome.Invalid, "Export kind is pin or board.");

        var rendered = await RenderPinsAsync(user, pins, scope, ct);
        using var wb = new XLWorkbook();
        var headers = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (pin, r) in pins.Zip(rendered.Pins))
            headers.AddRange(FiguresSheet(wb, UniqueSheet(names, pin.TitleEn.Length > 0 ? pin.TitleEn : pin.TitleBn), pin, r, filters));

        if (kind == "pin" && rendered.Pins[0].DatasetId is { } dsId && await access.HasAsync(user, PermissionKeys.MatrixData, ct))
        {
            var rows = await matrix.ExportAsync(user, "xlsx", dsId, ct);
            if (rows.Value is { } file)
            {
                using var src = new XLWorkbook(new MemoryStream(file.Bytes));
                src.Worksheet(1).CopyTo(wb, UniqueSheet(names, "Rows"));
                headers.AddRange(file.Headers);
            }
        }

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        var stem = kind == "pin" ? $"hris-pin-{pins[0].Id}" : $"hris-{board}-board";
        return MxResult<MxExportFile>.Success(new MxExportFile(stem + ".xlsx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ms.ToArray(), headers.Distinct().ToList()));
    }

    private static string UniqueSheet(HashSet<string> taken, string title)
    {
        var clean = new string(title.Trim().Where(c => !"[]:*?/\\".Contains(c)).ToArray());
        if (clean.Length == 0) clean = "Pin";
        if (clean.Length > 28) clean = clean[..28];
        var name = clean;
        for (var i = 2; !taken.Add(name); i++) name = $"{clean} {i}";
        return name;
    }

    /// <summary>One pin's figures: what the board shows, as a table. Returns its column headers.</summary>
    private static List<string> FiguresSheet(XLWorkbook wb, string name, HrisPin pin, HrisPinRender r, HrisFilters? filters)
    {
        var ws = wb.AddWorksheet(name);
        var row = 1;
        ws.Cell(row++, 1).Value = $"{pin.TitleBn} · {pin.TitleEn}";
        ws.Cell(row++, 1).Value = $"As of {r.ReportDate ?? "—"} · {r.Source ?? "—"} · Mostisko · HRIS Workspace";
        if (filters is not null) ws.Cell(row++, 1).Value = "Filters: " + JsonSerializer.Serialize(filters, Web);
        row++;
        var headers = new List<string>();
        void Table(List<string> cols, IEnumerable<IEnumerable<object?>> lines)
        {
            for (var i = 0; i < cols.Count; i++) ws.Cell(row, i + 1).Value = cols[i];
            ws.Row(row).Style.Font.Bold = true;
            headers.AddRange(cols);
            row++;
            foreach (var line in lines)
            {
                var c = 1;
                foreach (var v in line)
                {
                    var cell = ws.Cell(row, c++);
                    switch (v)
                    {
                        case null: break;
                        case long l: cell.Value = l; break;
                        case int n: cell.Value = n; break;
                        case double x: cell.Value = x; break;
                        case JsonElement { ValueKind: JsonValueKind.Number } e: cell.Value = e.GetDouble(); break;
                        default: cell.Value = Convert.ToString(v, CultureInfo.InvariantCulture); break;
                    }
                }
                row++;
            }
            row++;
        }

        foreach (var u in r.Unavailable)
            ws.Cell(row++, 1).Value = $"{u.Label}: not available — {u.Reason}";
        foreach (var w in r.Widgets ?? new())
        {
            ws.Cell(row++, 1).Value = w.NameEn;
            if (w.Trend is { Count: > 0 })
                Table(new() { "Report date", w.NameEn }, w.Trend.Select(t => new object?[] { t.ReportDate, t.Value }));
            else if (w.Rows.Count > 0)
            {
                var cols = w.GroupBy.Concat(w.Measures).ToList();
                Table(cols, w.Rows.Select(x => cols.Select(c => x.GetValueOrDefault(c))));
            }
            else if (w.Value is not null) Table(new() { w.NameEn, "Unit" }, new[] { new object?[] { w.Value, w.Unit } });
        }
        if (r.Card is { } card)
            Table(new() { "Item", "বিষয়", "Value", "Unit" }, card.Items.Select(i => new object?[] { i.Label, i.LabelBn, (object?)i.Value ?? i.Text, i.Unit }));
        if (r.Summary is { } s) ws.Cell(row++, 1).Value = s.Text;
        if (r.Answer is { } a && a.TryGetProperty("text", out var text)) ws.Cell(row++, 1).Value = text.GetString();
        if (r.Error is not null) ws.Cell(row, 1).Value = r.Error;
        ws.Column(1).Width = 40;
        return headers;
    }
}
