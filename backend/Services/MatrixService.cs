// ম্যাট্রিক্স — upload, profile, import, quality and the viewer's rows
// (docs/MATRIX_PROMPT.md §3.4, §3.5, §3.10).
//
// The uploaded bytes are held in memory between profiling and import — never on
// disk — and dropped as soon as the import is done (§3.4: the original file is
// not kept; re-import needs a re-upload). Sensitive columns are not imported;
// personal and salary values are masked in the rows for anyone without
// action:matrix.view-personal / view-salary. Every refusal is decided here and
// returned as an outcome the route turns into a status code.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using RabbitIC.Api.DTOs.Matrix;
using RabbitIC.Api.Matrix;
using RabbitIC.Api.Models;
using RabbitIC.Api.Rbac;
using RabbitIC.Api.Repositories;

namespace RabbitIC.Api.Services;

public partial class MatrixService : IMatrixService
{
    public const string Masked = "•••";
    private static readonly string[] Extensions = { ".xlsx", ".xlsm", ".csv" };
    private static readonly TimeSpan HoldBytes = TimeSpan.FromHours(2);
    private const int MaxRows = 2000;

    private readonly IMatrixRepository _repo;
    private readonly TemplateMatcher _matcher;
    private readonly AccessPolicy _access;
    private readonly IMemoryCache _cache;
    private readonly ILogger<MatrixService> _log;
    private readonly ChatService _chat;
    private readonly MatrixModel _models;
    private readonly IHttpClientFactory _http;

    public MatrixService(IMatrixRepository repo, TemplateMatcher matcher, AccessPolicy access, IMemoryCache cache, ILogger<MatrixService> log,
        ChatService chat, MatrixModel models, IHttpClientFactory http)
    {
        _http = http;
        _chat = chat;
        _models = models;
        _repo = repo;
        _matcher = matcher;
        _access = access;
        _cache = cache;
        _log = log;
    }

    private static string BytesKey(int uploadId) => $"matrix:upload-bytes:{uploadId}";
    private static readonly JsonSerializerOptions Json = TemplateMatcher.Json;

    // ───────────────────────── upload and profile ─────────────────────────

    public async Task<MxResult<MxUploadsDto>> UploadAsync(User user, IReadOnlyList<MxIncomingFile> files, CancellationToken ct = default)
    {
        if (files.Count == 0) return MxResult<MxUploadsDto>.Fail(MxOutcome.Invalid, "No file was sent.");
        foreach (var f in files)
        {
            if (!Extensions.Contains(Path.GetExtension(f.FileName).ToLowerInvariant()))
                return MxResult<MxUploadsDto>.Fail(MxOutcome.Invalid, $"{f.FileName}: send .xlsx, .xlsm or .csv files.");
            if (f.Bytes.LongLength > IMatrixService.MaxUploadBytes)
                return MxResult<MxUploadsDto>.Fail(MxOutcome.Invalid, $"{f.FileName} is larger than {IMatrixService.MaxUploadBytes / 1024 / 1024} MB.");
        }

        var templates = await _repo.ListTemplatesAsync(ct);
        var result = new List<MxUploadDto>();
        foreach (var f in files)
        {
            MxProfile profile;
            List<MxTableScan> scans;
            try { (profile, scans) = WorkbookProfiler.Profile(new MemoryStream(f.Bytes), f.FileName); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return MxResult<MxUploadsDto>.Fail(MxOutcome.Invalid, $"{f.FileName} could not be read as a workbook: {ex.Message}");
            }

            TemplateMatcher.ApplyTemplates(profile, templates);
            var note = await _matcher.SuggestAsync(user, scans, ct);
            if (note is not null)
                foreach (var t in scans.Where(s => s.Profile.Columns.Any(c => c.Suggestion is null && c.Sensitivity != MxSensitivity.Sensitive)))
                    t.Profile.Quirks.Add($"no model suggestion for the unmatched columns: {note}");

            var upload = await _repo.AddUploadAsync(new MxUpload
            {
                FileName = Path.GetFileName(f.FileName),
                SizeBytes = f.Bytes.LongLength,
                Sha256 = Convert.ToHexString(SHA256.HashData(f.Bytes)).ToLowerInvariant(),
                UploadedByUserId = user.Id,
                UploadedAtUtc = DateTime.UtcNow,
                ReportDate = profile.ReportDate,
                Status = MxUpload.StatusProfiled,
                ProfileJson = JsonSerializer.Serialize(profile, Json),
            }, ct);
            _cache.Set(BytesKey(upload.Id), f.Bytes, HoldBytes);
            result.Add(ToDto(upload, profile));
        }
        return MxResult<MxUploadsDto>.Success(new MxUploadsDto(result));
    }

    public async Task<List<MxUploadDto>> ListUploadsAsync(CancellationToken ct = default) =>
        (await _repo.ListUploadsAsync(50, ct)).Select(u => ToDto(u, null)).ToList();

    public async Task<MxResult<MxProfile>> GetProfileAsync(int uploadId, CancellationToken ct = default)
    {
        var upload = await _repo.GetUploadAsync(uploadId, ct);
        return upload is null
            ? MxResult<MxProfile>.Fail(MxOutcome.NotFound, "No such upload.")
            : MxResult<MxProfile>.Success(ReadProfile(upload));
    }

    private static MxProfile ReadProfile(MxUpload u) => JsonSerializer.Deserialize<MxProfile>(u.ProfileJson, Json) ?? new MxProfile();

    private static MxUploadDto ToDto(MxUpload u, MxProfile? p) =>
        new(u.Id, u.FileName, u.SizeBytes, u.Sha256, u.Status, u.ReportDate, u.UploadedAtUtc, p);

    // ───────────────────────── import ─────────────────────────

    public async Task<MxResult<MxImportResult>> ImportAsync(User user, int uploadId, MxImportInput input, CancellationToken ct = default)
    {
        var upload = await _repo.GetUploadAsync(uploadId, ct);
        if (upload is null) return MxResult<MxImportResult>.Fail(MxOutcome.NotFound, "No such upload.");
        if (!_cache.TryGetValue(BytesKey(uploadId), out byte[]? bytes) || bytes is null)
        {
            if (upload.Status == MxUpload.StatusProfiled) { upload.Status = MxUpload.StatusExpired; await _repo.SaveChangesAsync(ct); }
            return MxResult<MxImportResult>.Fail(MxOutcome.Gone, "The uploaded file is no longer held (it is never stored). Upload it again to import.");
        }

        // The grids again — the bytes are profiled the same way — with the suggestions the stored profile holds.
        var (profile, scans) = WorkbookProfiler.Profile(new MemoryStream(bytes), upload.FileName);
        var templates = await _repo.ListTemplatesAsync(ct);
        TemplateMatcher.ApplyTemplates(profile, templates);
        var stored = ReadProfile(upload);
        foreach (var scan in scans)
        {
            var was = stored.Sheets.SelectMany(s => s.Tables).FirstOrDefault(t => t.Id == scan.Profile.Id);
            if (was is null) continue;
            foreach (var col in scan.Profile.Columns.Where(c => c.Suggestion is null))
                col.Suggestion = was.Columns.FirstOrDefault(c => c.Index == col.Index)?.Suggestion;
        }

        // Which tables: as asked, or every selected table that has a template ("no questions").
        var requested = input.Tables is { Count: > 0 }
            ? input.Tables
            : scans.Where(s => s.Profile.Selected).Select(s => new MxImportTable(s.Profile.Id, null, null, null, null, null, null)).ToList();
        var plan = new List<(MxTableScan Scan, MxImportTable Ask)>();
        foreach (var ask in requested)
        {
            var scan = scans.FirstOrDefault(s => s.Profile.Id == ask.Table);
            if (scan is null) return MxResult<MxImportResult>.Fail(MxOutcome.Invalid, $"No table '{ask.Table}' in this upload.");
            if (ask.Mapping is null && ask.Confirm != "suggestions" && scan.Profile.Template is null)
                return MxResult<MxImportResult>.Fail(MxOutcome.Invalid, $"Confirm the column mapping for '{scan.Profile.Sheet.Trim()}' first.");
            if (ask.Kind is not null && !MxReportKind.All.Contains(ask.Kind))
                return MxResult<MxImportResult>.Fail(MxOutcome.Invalid, $"Report kind must be one of: {string.Join(", ", MxReportKind.All)}.");
            if (ask.ReportDate is not null && !DateTime.TryParseExact(ask.ReportDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                return MxResult<MxImportResult>.Fail(MxOutcome.Invalid, "Report date must be yyyy-MM-dd.");
            if (ask.AllowSensitive is { Count: > 0 })
            {
                if (!await _access.HasAsync(user, PermissionKeys.MatrixTemplates, ct))
                    return MxResult<MxImportResult>.Fail(MxOutcome.Forbidden, "Keeping a sensitive column needs action:matrix.templates.");
                if (ask.AllowSensitive.Any(a => string.IsNullOrWhiteSpace(a.Note)))
                    return MxResult<MxImportResult>.Fail(MxOutcome.Invalid, "Keeping a sensitive column needs a note saying why.");
            }
            foreach (var m in ask.Mapping ?? new())
                if (m.Field is not null and not "keep" && FieldCatalog.Find(m.Field) is not { Derived: false })
                    return MxResult<MxImportResult>.Fail(MxOutcome.Invalid, $"'{m.Field}' is not a field in the catalog.");
            plan.Add((scan, ask));
        }

        // The designation lookup first, so the other tables are checked against it.
        plan = plan.OrderBy(p => (p.Ask.Kind ?? p.Scan.Profile.Kind) == MxReportKind.DesignationLookup ? 0 : 1).ToList();

        var datasets = new List<MxDatasetDto>();
        var usedTemplates = new List<MxTemplateDto>();
        var allFromTemplates = true;
        foreach (var (scan, ask) in plan)
        {
            var t = scan.Profile;
            var kind = ask.Kind ?? t.Kind;
            var reportDate = ask.ReportDate ?? t.ReportDate ?? upload.ReportDate ?? DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var fromTemplate = ask.Mapping is null && ask.Confirm != "suggestions" && t.Template is not null;
            allFromTemplates &= fromTemplate;
            var decisions = Decide(t, ask);
            var allowed = (ask.AllowSensitive ?? new()).Select(a => a.Index).ToHashSet();

            var build = DatasetImporter.Build(scan, decisions, allowed, reportDate);
            var lookup = await LookupAsync(ct);
            var issues = QualityChecks.Run(scan, build, new MxCheckContext(
                scans.Where(s => s != scan && s.EmpIds.Count > 0).Select(s => (s.Profile.Sheet, s.EmpIds)).ToList(),
                kind == MxReportKind.DesignationLookup ? null : lookup));

            var template = await UpsertTemplateAsync(user, t, kind, decisions, ask.AllowSensitive, ct);
            var dataset = await _repo.CreateDatasetAsync(new MxDataset
            {
                UploadId = upload.Id,
                TemplateId = template.Id,
                Name = $"{kind} · {t.Sheet.Trim()} · {reportDate}",
                ReportKind = kind,
                Sheet = t.Sheet,
                TableRange = t.Range,
                RowCount = build.Rows.Count,
                ColumnsJson = JsonSerializer.Serialize(build.Columns, Json),
                DroppedJson = JsonSerializer.Serialize(build.Dropped, Json),
                ReportDate = reportDate,
                CreatedByUserId = user.Id,
                CreatedAtUtc = DateTime.UtcNow,
            }, build.Columns, build.Rows, build.SourceRows, ct);
            foreach (var i in issues) i.DatasetId = dataset.Id;
            await _repo.AddIssuesAsync(issues, ct);
            await _repo.SetCurrentAsync(dataset, ct);
            foreach (var a in ask.AllowSensitive ?? new())
                await _repo.AddFixLogAsync(new MxFixLog
                {
                    DatasetId = dataset.Id, Field = t.Columns.FirstOrDefault(c => c.Index == a.Index)?.Header ?? $"#{a.Index}",
                    Kind = "allow-sensitive", ByUserId = user.Id, AtUtc = DateTime.UtcNow, Note = a.Note,
                }, ct);

            datasets.Add(ToDto(dataset, upload, issues.Count));
            usedTemplates.Add(ToDto(template));

            // A newer designation lookup: the datasets already imported are checked against it again.
            if (kind == MxReportKind.DesignationLookup && (await _repo.GetDatasetAsync(dataset.Id, ct))?.IsCurrent == true)
                await RecheckDesignationsAsync(user, dataset.Id, ct);

            // The report card, made by code at import (§3.8).
            await BuildCardAsync(user, (await _repo.GetDatasetAsync(dataset.Id, ct))!, ct);
        }

        upload.Status = MxUpload.StatusImported;
        await _repo.SaveChangesAsync(ct);
        _cache.Remove(BytesKey(upload.Id));   // §3.4: the file is not kept after import

        // Each kind brings its automatic dashboards (§3.8).
        await EnsureAutoDashboardsAsync(ct);

        var names = string.Join(", ", usedTemplates.Select(t => $"{t.Name} v{t.Version}").Distinct());
        var message = allFromTemplates
            ? $"Imported with template {names}"
            : $"Imported {datasets.Count} table(s); template {names} saved for the next file of this layout";
        _log.LogInformation("Matrix: upload {Upload} imported as {Count} dataset(s).", upload.Id, datasets.Count);
        return MxResult<MxImportResult>.Success(new MxImportResult(datasets, usedTemplates, message));
    }

    /// <summary>Each column's field: the request's mapping, then the template's or the suggestion, else kept unmapped.</summary>
    private static List<MxColumnDecision> Decide(MxTableProfile t, MxImportTable ask)
    {
        var acceptModel = ask.Confirm == "suggestions";
        return t.Columns.Select(c =>
        {
            var m = ask.Mapping?.FirstOrDefault(x => x.Index == c.Index);
            if (m is not null)
                return new MxColumnDecision(c.Index, m.Field == "keep" ? null : m.Field, m.Sensitivity ?? c.Sensitivity, m.Field == "keep");
            var s = c.Suggestion;
            var field = s is { Source: "synonym" or "template" } || (acceptModel && s?.Source == "model") ? s?.Field : null;
            // Unmapped columns are kept, personal until a person classifies them (§3.4) — the profile
            // says personal for them, or what the template recorded. A template's "left out" stays out.
            return new MxColumnDecision(c.Index, field, c.Sensitivity, field is null && s?.Source != "template");
        }).ToList();
    }

    private async Task<MxTemplate> UpsertTemplateAsync(User user, MxTableProfile t, string kind, List<MxColumnDecision> decisions,
        List<MxAllowSensitive>? allowSensitive, CancellationToken ct)
    {
        var mapping = JsonSerializer.Serialize(t.Columns.Select(c =>
        {
            var d = decisions.First(x => x.Index == c.Index);
            return new MxTemplateColumn(c.Index, c.Header, d.Keep ? "keep" : d.Field, d.Field is null ? d.Sensitivity : FieldCatalog.Find(d.Field)!.Sensitivity);
        }).ToList(), Json);
        var allowed = JsonSerializer.Serialize((allowSensitive ?? new()).Select(a => new { a.Index, a.Note, byUserId = user.Id, atUtc = DateTime.UtcNow }), Json);

        var latest = (await _repo.ListTemplatesAsync(ct)).Where(x => x.Fingerprint == t.Fingerprint).OrderByDescending(x => x.Version).FirstOrDefault();
        if (latest is not null && latest.MappingJson == mapping && latest.ReportKind == kind && (allowSensitive is null || allowSensitive.Count == 0))
            return latest;

        return await _repo.AddTemplateAsync(new MxTemplate
        {
            Name = latest?.Name ?? $"{kind} · {t.Sheet.Trim()}",
            ReportKind = kind,
            Fingerprint = t.Fingerprint,
            SheetName = t.Sheet,
            MappingJson = mapping,
            AllowedSensitiveJson = allowed,
            Version = (latest?.Version ?? 0) + 1,
            CreatedByUserId = latest?.CreatedByUserId ?? user.Id,
            CreatedAtUtc = latest?.CreatedAtUtc ?? DateTime.UtcNow,
            UpdatedByUserId = user.Id,
            UpdatedAtUtc = DateTime.UtcNow,
        }, ct);
    }

    /// <summary>The designations of the current designation lookup, trimmed and lower-cased; null when none is imported.</summary>
    private async Task<IReadOnlySet<string>?> LookupAsync(CancellationToken ct)
    {
        var lookup = (await _repo.ListDatasetsAsync(ct)).FirstOrDefault(d => d.ReportKind == MxReportKind.DesignationLookup && d.IsCurrent);
        if (lookup is null || !Columns(lookup).Any(c => c.Key == "designation")) return null;
        return await _repo.DistinctAsync(lookup, "designation", ct);
    }

    /// <summary>
    /// Re-runs "designation not in lookup" on every current dataset that has a designation
    /// and an employee type, against the lookup just imported — the same check the import
    /// runs (QualityChecks.DesignationLookupIssue), over the rows already stored.
    /// </summary>
    /// <summary>
    /// Checks the current datasets against a newer lookup. A dataset whose designation issue
    /// changes gets its report card rebuilt, so the card never shows the old count.
    /// </summary>
    private async Task RecheckDesignationsAsync(User user, int lookupId, CancellationToken ct)
    {
        var lookup = await LookupAsync(ct);
        if (lookup is null) return;
        foreach (var d in (await _repo.ListDatasetsAsync(ct)).Where(d => d.IsCurrent && d.Id != lookupId && d.ReportKind != MxReportKind.DesignationLookup))
        {
            var cols = Columns(d);
            var des = cols.FirstOrDefault(c => c.Key == "designation");
            if (des?.SourceIndex is null || !cols.Any(c => c.Key == "emp_type")) continue;
            var keys = new List<string> { "designation", "emp_type" };
            if (cols.Any(c => c.Key == "grade_id")) keys.Add("grade_id");
            var (_, rows) = await _repo.RowsAsync(d, keys, "", Array.Empty<object>(), null, 0, Math.Max(1, d.RowCount), ct);
            var people = rows.Select(r => new MxDesignationRow(
                Convert.ToInt32(r["_row"]), r["designation"] as string, r["emp_type"] as string,
                r.TryGetValue("grade_id", out var g) ? g?.ToString() : null)).ToList();
            var firstCol = ClosedXML.Excel.XLHelper.GetColumnNumberFromLetter(new string(d.TableRange.TakeWhile(char.IsAsciiLetter).ToArray()));
            string RefOf(int row) => $"{d.Sheet}!{MxCells.Letter(firstCol + des.SourceIndex.Value)}{row}";
            var before = (await _repo.ListIssuesAsync(d.Id, ct)).FirstOrDefault(i => i.Check == MxCheck.DesignationNotInLookup);
            var after = QualityChecks.DesignationLookupIssue(people, lookup, RefOf, des);
            var same = before is null ? after is null
                : after is not null && before.Count == after.Count && before.Detail == after.Detail && before.SampleRefsJson == after.SampleRefsJson;
            if (same) continue;
            await _repo.ReplaceIssuesAsync(d.Id, MxCheck.DesignationNotInLookup, after, ct);
            await BuildCardAsync(user, d, ct);
        }
    }

    // ───────────────────────── templates ─────────────────────────

    public async Task<List<MxTemplateDto>> ListTemplatesAsync(CancellationToken ct = default) =>
        (await _repo.ListTemplatesAsync(ct)).Select(ToDto).ToList();

    public async Task<MxResult<MxTemplateDto>> UpdateTemplateAsync(int id, MxTemplateInput input, User user, CancellationToken ct = default)
    {
        var t = await _repo.GetTemplateAsync(id, ct);
        if (t is null) return MxResult<MxTemplateDto>.Fail(MxOutcome.NotFound, "No such template.");
        if (input.ReportKind is not null && !MxReportKind.All.Contains(input.ReportKind))
            return MxResult<MxTemplateDto>.Fail(MxOutcome.Invalid, $"Report kind must be one of: {string.Join(", ", MxReportKind.All)}.");
        if (!string.IsNullOrWhiteSpace(input.Name)) t.Name = input.Name.Trim();
        if (input.ReportKind is not null) t.ReportKind = input.ReportKind;
        t.UpdatedByUserId = user.Id;
        t.UpdatedAtUtc = DateTime.UtcNow;
        await _repo.SaveChangesAsync(ct);
        return MxResult<MxTemplateDto>.Success(ToDto(t));
    }

    private static MxTemplateDto ToDto(MxTemplate t) =>
        new(t.Id, t.Name, t.ReportKind, t.SheetName, t.Version, TemplateMatcher.Mapping(t).Count, t.UpdatedAtUtc);

    // ───────────────────────── datasets and rows ─────────────────────────

    private static List<MxDsColumn> Columns(MxDataset d) => JsonSerializer.Deserialize<List<MxDsColumn>>(d.ColumnsJson, Json) ?? new();

    private static MxDatasetDto ToDto(MxDataset d, MxUpload? upload, int openIssues) => new(
        d.Id, d.Name, d.ReportKind, d.Sheet, d.TableRange, d.RowCount, d.ReportDate, d.IsCurrent, d.UploadId, upload?.FileName ?? "",
        d.TemplateId, d.CreatedAtUtc,
        Columns(d).Select(c => { var f = FieldCatalog.Find(c.Field); return new MxColumnDto(c.Key, c.Header, c.Field, c.Type, c.Sensitivity, c.Unit, c.Derived, f?.Bn, f?.En); }).ToList(),
        JsonSerializer.Deserialize<List<MxDroppedDto>>(d.DroppedJson, Json) ?? new(),
        openIssues);

    public async Task<List<MxDatasetDto>> ListDatasetsAsync(CancellationToken ct = default)
    {
        var list = new List<MxDatasetDto>();
        foreach (var d in await _repo.ListDatasetsAsync(ct))
            list.Add(ToDto(d, await _repo.GetUploadAsync(d.UploadId, ct), (await _repo.ListIssuesAsync(d.Id, ct)).Count(i => i.State == MxQualityIssue.Open)));
        return list;
    }

    public async Task<MxResult<MxDatasetDto>> GetDatasetAsync(int id, CancellationToken ct = default)
    {
        var d = await _repo.GetDatasetAsync(id, ct);
        if (d is null) return MxResult<MxDatasetDto>.Fail(MxOutcome.NotFound, "No such dataset.");
        var open = (await _repo.ListIssuesAsync(id, ct)).Count(i => i.State == MxQualityIssue.Open);
        return MxResult<MxDatasetDto>.Success(ToDto(d, await _repo.GetUploadAsync(d.UploadId, ct), open));
    }

    /// <summary>The validator's world for this caller: the datasets, how kinds join, and what the caller may see.</summary>
    private async Task<MxQueryContext> QueryContextAsync(User user, CancellationToken ct)
    {
        var access = await _access.GetAsync(user, ct);
        return new MxQueryContext
        {
            Datasets = await _repo.ListDatasetsAsync(ct),
            ColumnsOf = Columns,
            Relations = await _repo.ListRelationsAsync(ct),
            CanSalary = access.Has(PermissionKeys.MatrixViewSalary),
            CanPersonal = access.Has(PermissionKeys.MatrixViewPersonal),
        };
    }

    private static MxOutcome OutcomeOf(MxQueryError e) => e.Status == 403 ? MxOutcome.Forbidden : MxOutcome.Invalid;

    public async Task<MxResult<MxRowsDto>> RowsAsync(User user, int datasetId, int offset, int limit, string? filter, string? highlight,
        bool onlyHighlighted, CancellationToken ct = default)
    {
        var d = await _repo.GetDatasetAsync(datasetId, ct);
        if (d is null) return MxResult<MxRowsDto>.Fail(MxOutcome.NotFound, "No such dataset.");
        var cols = Columns(d);
        var keys = cols.Select(c => c.Key).ToHashSet();
        var ctx = await QueryContextAsync(user, ct);

        var filters = new List<JsonElement>();
        if (!string.IsNullOrWhiteSpace(filter))
        {
            try
            {
                using var doc = JsonDocument.Parse(filter);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException();
                filters.AddRange(doc.RootElement.EnumerateArray().Select(e => e.Clone()));
            }
            catch (JsonException) { return MxResult<MxRowsDto>.Fail(MxOutcome.Invalid, "filter must be a JSON list of [column, operator, value…]."); }
        }

        MxHighlight? hl = null;
        List<JsonElement>? tokenFilters = null;
        if (!string.IsNullOrWhiteSpace(highlight))
        {
            var decoded = MxHighlightToken.Decode(highlight, datasetId, keys);
            if (decoded is null) return MxResult<MxRowsDto>.Fail(MxOutcome.Invalid, "The highlight token does not belong to this dataset.");
            (hl, tokenFilters) = decoded.Value;
        }

        limit = Math.Clamp(limit, 1, MaxRows);
        offset = Math.Max(0, offset);
        int total;
        List<Dictionary<string, object?>> rows;
        try
        {
            // A token with a filter (a query's group) filters like its query did; one with rows names them.
            var all = onlyHighlighted && tokenFilters is not null ? filters.Concat(tokenFilters).ToList() : filters;
            var (where, args) = MatrixQuery.WhereFor(d, all, ctx);
            (total, rows) = await _repo.RowsAsync(d, cols.Select(c => c.Key).ToList(), where, args,
                onlyHighlighted && tokenFilters is null ? hl?.Rows : null, offset, limit, ct);
            if (hl is not null && tokenFilters is not null)
            {
                var (tw, ta) = MatrixQuery.WhereFor(d, tokenFilters, ctx);
                var (_, marked) = await _repo.RowsAsync(d, Array.Empty<string>(), tw, ta, null, 0, 5000, ct);
                hl = new MxHighlight(marked.Select(r => Convert.ToInt32(r["_row"])).ToList(), hl.Cells);
            }
        }
        catch (MxQueryError e) { return MxResult<MxRowsDto>.Fail(OutcomeOf(e), e.Message); }

        var canSensitive = await _access.HasAsync(user, PermissionKeys.MatrixTemplates, ct);
        bool MaskedFor(MxDsColumn c) => c.Sensitivity switch
        {
            MxSensitivity.Personal => !ctx.CanPersonal,
            MxSensitivity.Salary => !ctx.CanSalary,
            MxSensitivity.Sensitive => !canSensitive,
            _ => false,
        };
        var masked = new HashSet<string>();
        foreach (var c in cols) if (MaskedFor(c)) masked.Add(c.Key);
        foreach (var row in rows)
            foreach (var k in masked)
                if (row[k] is not null) row[k] = Masked;

        var outCols = cols.Select(c => { var f = FieldCatalog.Find(c.Field); return new MxRowsColumn(c.Key, c.Header, c.Type, c.Sensitivity, masked.Contains(c.Key), f?.Bn, f?.En); }).ToList();
        return MxResult<MxRowsDto>.Success(new MxRowsDto(d.Id, total, offset, outCols, rows, hl));
    }

    // ───────────────────────── queries (§3.6) ─────────────────────────

    public async Task<MxResult<MxQueryResult>> QueryAsync(User user, JsonElement query, bool explain, CancellationToken ct = default)
    {
        try { return MxResult<MxQueryResult>.Success(await RunQueryAsync(await QueryContextAsync(user, ct), query, explain, ct)); }
        catch (MxQueryError e) { return MxResult<MxQueryResult>.Fail(OutcomeOf(e), e.Message); }
    }

    /// <summary>Compiles, runs, and attaches provenance and a citation token to every output row.</summary>
    private async Task<MxQueryResult> RunQueryAsync(MxQueryContext ctx, JsonElement query, bool explain, CancellationToken ct)
    {
        var c = MatrixQuery.Compile(query, ctx);
        var raw = await _repo.QueryAsync(c.Sql, c.Args, ct);
        var matched = await _repo.ScalarAsync(c.CountSql, c.Args, ct);
        var joins = new List<MxQueryJoin>();
        for (var i = 0; i < c.Joins.Count; i++)
            joins.Add(new MxQueryJoin(c.Joins[i].Datasets[0].Id, c.Joins[i].Kind, c.JoinKeys[i].On.Column.Key, await _repo.ScalarAsync(c.UnmatchedSql[i], c.Args, ct)));

        var single = c.Base.Datasets.Count == 1 ? c.Base.Datasets[0] : null;
        // A group reopens in the viewer by the query's own filter plus its group values, when all of
        // them are the base dataset's fields; otherwise by the base rows the group holds.
        var byFilter = single is not null && c.GroupBy.All(g => g.OnBase) && c.FilterFields.All(f => f.OnBase);
        var cells = c.Measures.SelectMany(m => m.Fields).Concat(c.GroupBy).Where(f => f.OnBase).Select(f => f.Column.Key).Distinct().ToList();

        var rows = new List<Dictionary<string, object?>>();
        foreach (var r in raw)
        {
            var row = new Dictionary<string, object?>();
            var groupFilters = new List<JsonElement>();
            for (var i = 0; i < c.GroupBy.Count; i++)
            {
                var v = r[$"g{i}"];
                row[c.GroupBy[i].Text] = v;
                groupFilters.Add(JsonSerializer.SerializeToElement(v is null
                    ? new object[] { c.GroupBy[i].Column.Key, "is_null" }
                    : c.GroupBy[i].Part == "month" ? new object[] { c.GroupBy[i].Column.Key, "starts_with", v }
                    : new object[] { c.GroupBy[i].Column.Key, "=", v }));
            }
            foreach (var m in c.Measures) row[m.Name] = r[m.Alias];

            if (single is not null)
            {
                if (byFilter)
                {
                    var f = c.Filters.Concat(groupFilters).ToList();
                    row["_cite"] = MxHighlightToken.Encode(single.Id, null, f, cells);
                    var cites = new Dictionary<string, string>();
                    foreach (var m in c.Measures.Where(m => m.Condition is not null && m.Fields.All(x => x.OnBase)))
                        cites[m.Name] = MxHighlightToken.Encode(single.Id, null, f.Append(m.Condition!.Value).ToList(), cells);
                    if (cites.Count > 0) row["_cites"] = cites;
                }
                else
                {
                    var ids = (r["_rows"] as string ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Take(5000)
                        .Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToList();
                    row["_cite"] = MxHighlightToken.Encode(single.Id, ids, null, cells);
                }
            }
            rows.Add(row);
        }

        var sets = c.Base.Datasets.Concat(c.Joins.SelectMany(j => j.Datasets))
            .Select(d => new MxQueryDataset(d.Id, d.Name, d.ReportKind, d.ReportDate, d.RowCount)).ToList();
        return new MxQueryResult(
            c.Columns.Select(x => new MxQueryColumn(x.Key, x.Label, x.Role, x.Type)).ToList(), rows,
            new MxProvenance(c.Plain, sets, matched, joins, explain ? c.Sql : null, explain ? c.Args : null));
    }

    // ───────────────────────── KPIs (§3.7) ─────────────────────────

    public async Task<List<MxKpiDto>> ListKpisAsync(CancellationToken ct = default)
    {
        var defs = (await _repo.ListKpiDefinitionsAsync(ct)).ToDictionary(d => d.Key);
        return KpiLibrary.All.Select(k =>
        {
            defs.TryGetValue(k.Key, out var d);
            return new MxKpiDto(k.Key, k.Area, d?.NameEn ?? k.En, d?.NameBn ?? k.Bn, d?.FormulaWords ?? k.Words, k.Unit, k.Shape,
                k.Query.Length > 0 ? k.Query : null, d is null ? null : JsonDocument.Parse(d.ThresholdsJson).RootElement.Clone(), k.Kinds);
        }).ToList();
    }

    public async Task<MxResult<MxKpiDto>> UpdateKpiAsync(string key, MxKpiInput input, CancellationToken ct = default)
    {
        var d = await _repo.GetKpiDefinitionAsync(key, ct);
        if (d is null) return MxResult<MxKpiDto>.Fail(MxOutcome.NotFound, "No such KPI.");
        if (!string.IsNullOrWhiteSpace(input.NameEn)) d.NameEn = input.NameEn.Trim();
        if (!string.IsNullOrWhiteSpace(input.NameBn)) d.NameBn = input.NameBn.Trim();
        if (!string.IsNullOrWhiteSpace(input.Words)) d.FormulaWords = input.Words.Trim();
        if (input.Thresholds is { } t)
        {
            if (KpiLibrary.ThresholdProblem(t) is { } problem) return MxResult<MxKpiDto>.Fail(MxOutcome.Invalid, "Thresholds: " + problem);
            d.ThresholdsJson = t.GetRawText();
        }
        d.UpdatedAtUtc = DateTime.UtcNow;
        await _repo.SaveChangesAsync(ct);
        return MxResult<MxKpiDto>.Success((await ListKpisAsync(ct)).First(k => k.Key == key));
    }

    public async Task<MxResult<MxKpiResult>> RunKpiAsync(User user, string key, MxKpiRunInput input, CancellationToken ct = default)
    {
        try { return MxResult<MxKpiResult>.Success(await RunKpiCoreAsync(await QueryContextAsync(user, ct), key, input, ct)); }
        catch (MxQueryError e) { return MxResult<MxKpiResult>.Fail(OutcomeOf(e), e.Message); }
    }

    private static string? RefText(JsonElement? e) => e is { ValueKind: JsonValueKind.Number or JsonValueKind.String } v ? v.ToString() : null;

    /// <summary>Runs a KPI; its result says which dashboard filters (input.Scope) it could apply.</summary>
    private async Task<MxKpiResult> RunKpiCoreAsync(MxQueryContext ctx, string key, MxKpiRunInput input, CancellationToken ct)
    {
        var applied = new HashSet<string>();
        var r = await RunKpiShapeAsync(ctx, key, input, applied, ct);
        return r with { ScopeApplied = applied.Union(r.ScopeApplied).Distinct().ToList() };
    }

    private async Task<MxKpiResult> RunKpiShapeAsync(MxQueryContext ctx, string key, MxKpiRunInput input, HashSet<string> applied, CancellationToken ct)
    {
        var kpi = KpiLibrary.Find(key) ?? throw new MxQueryError(400, $"No KPI \"{key}\".");
        var def = await _repo.GetKpiDefinitionAsync(key, ct);
        var en = def?.NameEn ?? kpi.En;
        var bn = def?.NameBn ?? kpi.Bn;

        // The dataset: as asked, else the current one of the first kind this KPI reads.
        var reference = RefText(input.Dataset)
            ?? kpi.Kinds?.Select(k => ctx.Datasets.Any(d => d.ReportKind == k && d.IsCurrent) ? $"current:{k}" : null).FirstOrDefault(r => r is not null)
            ?? throw new MxQueryError(400, $"No dataset to run \"{key}\" on; pass one.");
        var d = MatrixQuery.Resolve(reference, ctx, out _)[0];
        MxDataset Previous()
        {
            if (RefText(input.Previous) is { } p) return MatrixQuery.Resolve(p, ctx, out _)[0];
            return MatrixQuery.PreviousSnapshot(d, ctx.Datasets)
                ?? throw new MxQueryError(400, $"There is no snapshot before dataset {d.Id}.");
        }
        string Param(string name, string fallback) => input.Params is not null && input.Params.TryGetValue(name, out var v) ? v.ToString() : fallback;

        async Task<MxQueryResult> Run(MxKpi k, MxDataset on, MxDataset? prev = null)
        {
            var report = DateTime.TryParse(on.ReportDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var rd) ? rd : DateTime.UtcNow.Date;
            var text = k.Query.Replace("{d}", on.Id.ToString(CultureInfo.InvariantCulture))
                .Replace("{prev}", prev?.Id.ToString(CultureInfo.InvariantCulture) ?? "")
                .Replace("{from}", Param("from", report.AddDays(-30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))
                .Replace("{to}", Param("to", report.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
            var node = System.Text.Json.Nodes.JsonNode.Parse(text)!.AsObject();
            if (k.ActiveOnly && Columns(on).Any(c => c.Key == "status"))
            {
                if (node["filter"] is not System.Text.Json.Nodes.JsonArray fa) node["filter"] = fa = new System.Text.Json.Nodes.JsonArray();
                fa.Add(System.Text.Json.Nodes.JsonNode.Parse("[\"status\", \"in\", [\"Active\"]]"));
            }
            applied.UnionWith(MatrixQuery.ApplyScope(node, input.Scope, ctx));
            return await RunQueryAsync(ctx, JsonSerializer.SerializeToElement(node), false, ct);
        }
        static double? Num(object? v) => v switch { null => null, long l => l, double x => x, int i => i, _ => double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : null };
        double? Round(double? v) => v is null ? null : kpi.Unit switch { "%" or "hours" or "tk" => Math.Round(v.Value, 2), "ratio" => Math.Round(v.Value, 3), _ => v };
        string? CiteOf(Dictionary<string, object?> row) => row.TryGetValue("_cites", out var cs) && cs is Dictionary<string, string> m && m.TryGetValue("value", out var t) ? t : row.GetValueOrDefault("_cite") as string;
        List<Dictionary<string, object?>> Shaped(MxQueryResult r, string field) => r.Rows.Select(x => new Dictionary<string, object?>
        {
            [field] = x.GetValueOrDefault(field), ["value"] = Round(Num(x["value"])), ["_cite"] = CiteOf(x),
        }).ToList();

        switch (kpi.Shape)
        {
            case MxKpiShape.Value:
            {
                var against = kpi.Query.Contains("{prev}") ? Previous() : null;
                var r = await Run(kpi, d, against);
                var row = r.Rows.FirstOrDefault();
                var v = row?["value"];
                // A snapshot KPI says which snapshot it was measured against.
                var detail = against is null ? null : $"against the snapshot of {against.ReportDate ?? "no date"} (dataset {against.Id})";
                return new MxKpiResult(key, en, bn, kpi.Unit, Round(Num(v)), v is string s ? s : null, null, new(), null, detail, row is null ? null : CiteOf(row), r.Provenance);
            }
            case MxKpiShape.Rows:
            case MxKpiShape.PerLine:
            {
                var r = await Run(kpi, d);
                var rows = Shaped(r, kpi.GroupField!);
                MxKpiSummary? summary = null;
                if (kpi.Shape == MxKpiShape.PerLine)
                {
                    var vs = rows.Select(x => x["value"] as double?).Where(x => x is not null).Select(x => x!.Value).ToList();
                    summary = new MxKpiSummary(vs.Count > 0 ? vs.Min() : null, KpiLibrary.Median(vs), vs.Count > 0 ? vs.Max() : null, rows.Count);
                }
                return new MxKpiResult(key, en, bn, kpi.Unit, null, null, kpi.GroupField, rows, summary, null, null, r.Provenance);
            }
            case MxKpiShape.LinesBelow:
            {
                var r = await Run(KpiLibrary.Find("operators_per_line")!, d);
                var threshold = double.TryParse(Param("threshold", "20"), NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : 20;
                var below = Shaped(r, "line_no").Where(x => (x["value"] as double? ?? 0) < threshold).ToList();
                return new MxKpiResult(key, en, bn, kpi.Unit, below.Count, null, "line_no", below, null, $"lines with fewer than {threshold} operators", null, r.Provenance);
            }
            case MxKpiShape.Attrition:
            {
                var prev = Previous();
                var sep = await RunKpiCoreAsync(ctx, "separations_since_previous", input with { Dataset = JsonSerializer.SerializeToElement(d.Id), Previous = JsonSerializer.SerializeToElement(prev.Id) }, ct);
                var a = await RunKpiCoreAsync(ctx, "active_headcount", input with { Dataset = JsonSerializer.SerializeToElement(prev.Id) }, ct);
                var b = await RunKpiCoreAsync(ctx, "active_headcount", input with { Dataset = JsonSerializer.SerializeToElement(d.Id) }, ct);
                applied.UnionWith(sep.ScopeApplied.Intersect(a.ScopeApplied).Intersect(b.ScopeApplied));
                var avg = ((a.Value ?? 0) + (b.Value ?? 0)) / 2;
                var value = avg > 0 ? Math.Round(100 * (sep.Value ?? 0) / avg, 2) : (double?)null;
                return new MxKpiResult(key, en, bn, kpi.Unit, value, null, null, new(), null,
                    $"{sep.Value} separations ÷ average active headcount {avg} ({a.Value} on {prev.ReportDate}, {b.Value} on {d.ReportDate}) × 100", sep.Cite, sep.Provenance);
            }
            case MxKpiShape.AttendancePct:
            {
                var against = RefText(input.Against) is { } ag ? MatrixQuery.Resolve(ag, ctx, out _)[0]
                    : ctx.Datasets.Where(x => x.IsCurrent && x.ReportKind is MxReportKind.Master or MxReportKind.Manpower).OrderBy(x => x.ReportKind == MxReportKind.Master ? 0 : 1).FirstOrDefault()
                      ?? throw new MxQueryError(400, "Attendance % needs a master or manpower list to measure against.");
                var kind = against.ReportKind;
                var presentNode = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new
                {
                    dataset = d.Id.ToString(CultureInfo.InvariantCulture),
                    join = new[] { new { dataset = against.Id.ToString(CultureInfo.InvariantCulture), on = "emp_id" } },
                    group_by = new[] { $"{kind}.unit" },
                    measures = new object[] { new object[] { "count_if", new object[] { "att_status", "in", new[] { "P", "LT" } }, "present" } },
                    limit = 500,
                }))!.AsObject();
                var presentScope = MatrixQuery.ApplyScope(presentNode, input.Scope, ctx);
                var present = await RunQueryAsync(ctx, JsonSerializer.SerializeToElement(presentNode), false, ct);
                var activeFilter = Columns(against).Any(c => c.Key == "status") ? new object[] { new object[] { "status", "in", new[] { "Active" } } } : Array.Empty<object>();
                var activeNode = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new
                {
                    dataset = against.Id.ToString(CultureInfo.InvariantCulture), filter = activeFilter, group_by = new[] { "unit" },
                    measures = new object[] { new object[] { "count", "*", "active" } }, limit = 500,
                }))!.AsObject();
                // The people side takes the filters the master has (unit, section …); attendance-only ones (shift, date) narrow the present side.
                MatrixQuery.ApplyScope(activeNode, input.Scope?.Where(s => s.Key is not ("shift" or "date") && !(s.Key == "status")).ToList(), ctx);
                applied.UnionWith(presentScope);
                var active = await RunQueryAsync(ctx, JsonSerializer.SerializeToElement(activeNode), false, ct);
                var activeByUnit = active.Rows.ToDictionary(x => x["unit"]?.ToString() ?? "", x => Num(x["active"]) ?? 0);
                var rows = present.Rows.Where(x => x[$"{kind}.unit"] is not null).Select(x =>
                {
                    var unit = x[$"{kind}.unit"]!.ToString()!;
                    var p = Num(x["present"]) ?? 0;
                    var act = activeByUnit.GetValueOrDefault(unit);
                    return new Dictionary<string, object?> { ["unit"] = unit, ["present"] = p, ["active"] = act, ["value"] = act > 0 ? Math.Round(100 * p / act, 2) : null, ["_cite"] = x.GetValueOrDefault("_cite") };
                }).ToList();
                var sumP = rows.Sum(x => (double)x["present"]!);
                var sumA = rows.Sum(x => (double)x["active"]!);
                var unmatched = present.Provenance.Joins.FirstOrDefault()?.Unmatched ?? 0;
                return new MxKpiResult(key, en, bn, kpi.Unit, sumA > 0 ? Math.Round(100 * sumP / sumA, 2) : null, null, "unit", rows, null,
                    $"{sumP} present of {sumA} active in {rows.Count} unit(s); {unmatched} people in the attendance are not in the {kind} list",
                    null, present.Provenance);
            }
            default: // open issues
            {
                var issues = (await _repo.ListIssuesAsync(d.Id, ct)).Where(i => i.State == MxQualityIssue.Open)
                    .GroupBy(i => i.Check).Select(g => new Dictionary<string, object?> { ["check"] = g.Key, ["value"] = (double)g.Sum(i => i.Count) }).ToList();
                return new MxKpiResult(key, en, bn, kpi.Unit, issues.Sum(x => (double)x["value"]!), null, "check", issues, null, null, null, null);
            }
        }
    }

    // ───────────────────────── report cards (§3.8) ─────────────────────────

    public async Task<MxResult<MxReportCardDto>> CardAsync(User user, int datasetId, CancellationToken ct = default)
    {
        var d = await _repo.GetDatasetAsync(datasetId, ct);
        if (d is null) return MxResult<MxReportCardDto>.Fail(MxOutcome.NotFound, "No such dataset.");
        if (await _repo.GetCardAsync(datasetId, ct) is { } stored
            && JsonSerializer.Deserialize<MxReportCardDto>(stored.Json, Json) is { Version: >= ReportCardBuilder.Version } card)
            return MxResult<MxReportCardDto>.Success(card);
        return MxResult<MxReportCardDto>.Success(await BuildCardAsync(user, d, ct));
    }

    private async Task<MxReportCardDto> BuildCardAsync(User user, MxDataset d, CancellationToken ct)
    {
        var ctx = await QueryContextAsync(user, ct);
        var card = await ReportCardBuilder.BuildAsync(d, ctx, Columns(d),
            (key, ds, prev) => RunKpiCoreAsync(ctx, key, new MxKpiRunInput(JsonSerializer.SerializeToElement(ds.Id), prev is null ? null : JsonSerializer.SerializeToElement(prev.Id), null, null), ct),
            await _repo.ListIssuesAsync(d.Id, ct));
        await _repo.SaveCardAsync(d.Id, JsonSerializer.Serialize(card, Json), ct);
        return card;
    }

    // ───────────────────────── quality ─────────────────────────

    public async Task<MxResult<MxIssuesDto>> IssuesAsync(int datasetId, CancellationToken ct = default)
    {
        var d = await _repo.GetDatasetAsync(datasetId, ct);
        if (d is null) return MxResult<MxIssuesDto>.Fail(MxOutcome.NotFound, "No such dataset.");
        var issues = (await _repo.ListIssuesAsync(datasetId, ct)).Select(i =>
        {
            var rows = JsonSerializer.Deserialize<List<int>>(i.SampleRowsJson) ?? new();
            return new MxIssueDto(i.Id, i.Check, i.Severity, i.Count, i.Field, JsonSerializer.Deserialize<List<string>>(i.SampleRefsJson) ?? new(),
                rows, i.Detail, i.SuggestedFix, i.FixKind, i.State,
                MxHighlightToken.Encode(datasetId, rows, null, i.Field is null ? new List<string>() : new List<string> { i.Field }));
        }).ToList();
        return MxResult<MxIssuesDto>.Success(new MxIssuesDto(datasetId, issues));
    }

    public async Task<MxResult<MxFixResult>> FixAsync(User user, int issueId, MxFixInput input, CancellationToken ct = default)
    {
        var issue = await _repo.GetIssueAsync(issueId, ct);
        if (issue is null) return MxResult<MxFixResult>.Fail(MxOutcome.NotFound, "No such issue.");
        if (issue.FixKind is null || issue.Field is null)
            return MxResult<MxFixResult>.Fail(MxOutcome.Invalid, "This issue has no automatic fix; it needs a person (see the suggested fix).");
        if (issue.State == MxQualityIssue.Fixed) return MxResult<MxFixResult>.Fail(MxOutcome.Invalid, "This issue is already fixed.");
        var d = await _repo.GetDatasetAsync(issue.DatasetId, ct);
        if (d is null) return MxResult<MxFixResult>.Fail(MxOutcome.NotFound, "No such dataset.");
        if (!Columns(d).Any(c => c.Key == issue.Field)) return MxResult<MxFixResult>.Fail(MxOutcome.Invalid, "The issue's column is not in the dataset.");

        var changed = await _repo.ApplyFixAsync(d, issue.Field, issue.FixKind, ct);
        issue.State = MxQualityIssue.Fixed;
        await _repo.SaveChangesAsync(ct);
        await _repo.AddFixLogAsync(new MxFixLog
        {
            DatasetId = d.Id, IssueId = issue.Id, Field = issue.Field, Kind = issue.FixKind, RowsChanged = changed,
            ByUserId = user.Id, AtUtc = DateTime.UtcNow, Note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim(),
        }, ct);
        // The fix changed values and closed an issue: the card is made again from what is there now.
        await BuildCardAsync(user, d, ct);
        return MxResult<MxFixResult>.Success(new MxFixResult(issue.Id, issue.Field, "_raw_" + issue.Field, changed, issue.FixKind));
    }

    public IReadOnlyList<MxFieldDto> Catalog() =>
        FieldCatalog.All.Select(f => new MxFieldDto(f.Key, f.Group, f.Role, f.Type, f.Sensitivity, f.En, f.Bn, f.Derived)).ToList();
}

/// <summary>
/// A citation token: which rows of which dataset to outline and which cells to fill.
/// Either the rows by number (R), or the filter that selects them (F, the query's own
/// filter plus a group's values). Base64url JSON; it carries no data, and is checked
/// against the dataset before use — the rows route still masks and still applies the
/// caller's permissions to the filter.
/// </summary>
public static class MxHighlightToken
{
    private sealed record Body(int D, List<int>? R, List<JsonElement>? F, List<string> C);

    public static string Encode(int datasetId, List<int>? rows, List<JsonElement>? filters, List<string> cells)
    {
        var json = JsonSerializer.Serialize(new Body(datasetId, rows, filters, cells), TemplateMatcher.Json);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>The rows (empty when a filter selects them) and cells, and the filter if it has one.</summary>
    public static (MxHighlight Highlight, List<JsonElement>? Filters)? Decode(string token, int datasetId, IReadOnlySet<string> keys)
    {
        try
        {
            var b64 = token.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            var body = JsonSerializer.Deserialize<Body>(Encoding.UTF8.GetString(Convert.FromBase64String(b64)), TemplateMatcher.Json);
            if (body is null || body.D != datasetId || (body.R?.Count ?? 0) > 5000 || (body.F?.Count ?? 0) > 20 || body.C.Any(c => !keys.Contains(c))) return null;
            return (new MxHighlight(body.R ?? new List<int>(), body.C), body.F);
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { return null; }
    }
}
