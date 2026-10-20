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

public class MatrixService : IMatrixService
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

    public MatrixService(IMatrixRepository repo, TemplateMatcher matcher, AccessPolicy access, IMemoryCache cache, ILogger<MatrixService> log)
    {
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
                await RecheckDesignationsAsync(dataset.Id, ct);
        }

        upload.Status = MxUpload.StatusImported;
        await _repo.SaveChangesAsync(ct);
        _cache.Remove(BytesKey(upload.Id));   // §3.4: the file is not kept after import

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
    private async Task RecheckDesignationsAsync(int lookupId, CancellationToken ct)
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
            var (_, rows) = await _repo.RowsAsync(d, keys, Array.Empty<MxRowFilter>(), null, 0, Math.Max(1, d.RowCount), ct);
            var people = rows.Select(r => new MxDesignationRow(
                Convert.ToInt32(r["_row"]), r["designation"] as string, r["emp_type"] as string,
                r.TryGetValue("grade_id", out var g) ? g?.ToString() : null)).ToList();
            var firstCol = ClosedXML.Excel.XLHelper.GetColumnNumberFromLetter(new string(d.TableRange.TakeWhile(char.IsAsciiLetter).ToArray()));
            string RefOf(int row) => $"{d.Sheet}!{MxCells.Letter(firstCol + des.SourceIndex.Value)}{row}";
            await _repo.ReplaceIssuesAsync(d.Id, MxCheck.DesignationNotInLookup, QualityChecks.DesignationLookupIssue(people, lookup, RefOf, des), ct);
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

    private static readonly string[] FilterOps = { "=", "contains", "in", "empty" };

    public async Task<MxResult<MxRowsDto>> RowsAsync(User user, int datasetId, int offset, int limit, string? filter, string? highlight,
        bool onlyHighlighted, CancellationToken ct = default)
    {
        var d = await _repo.GetDatasetAsync(datasetId, ct);
        if (d is null) return MxResult<MxRowsDto>.Fail(MxOutcome.NotFound, "No such dataset.");
        var cols = Columns(d);
        var keys = cols.Select(c => c.Key).ToHashSet();

        var filters = new List<MxRowFilter>();
        if (!string.IsNullOrWhiteSpace(filter))
        {
            try
            {
                foreach (var f in JsonSerializer.Deserialize<List<JsonElement>>(filter) ?? new())
                {
                    var key = f[0].GetString() ?? "";
                    var op = f[1].GetString() ?? "";
                    if (!keys.Contains(key)) return MxResult<MxRowsDto>.Fail(MxOutcome.Invalid, $"'{key}' is not a column of this dataset.");
                    if (!FilterOps.Contains(op)) return MxResult<MxRowsDto>.Fail(MxOutcome.Invalid, $"'{op}' is not a filter operator.");
                    var values = f.GetArrayLength() < 3 ? new List<string>()
                        : f[2].ValueKind == JsonValueKind.Array ? f[2].EnumerateArray().Select(v => v.ToString()).ToList()
                        : new List<string> { f[2].ToString() };
                    filters.Add(new MxRowFilter(key, op, values));
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or IndexOutOfRangeException)
            {
                return MxResult<MxRowsDto>.Fail(MxOutcome.Invalid, "filter must be a JSON list of [column, operator, value].");
            }
        }

        MxHighlight? hl = null;
        if (!string.IsNullOrWhiteSpace(highlight))
        {
            hl = MxHighlightToken.Decode(highlight, datasetId, keys);
            if (hl is null) return MxResult<MxRowsDto>.Fail(MxOutcome.Invalid, "The highlight token does not belong to this dataset.");
        }

        limit = Math.Clamp(limit, 1, MaxRows);
        offset = Math.Max(0, offset);
        var (total, rows) = await _repo.RowsAsync(d, cols.Select(c => c.Key).ToList(), filters,
            onlyHighlighted ? hl?.Rows : null, offset, limit, ct);

        var access = await _access.GetAsync(user, ct);
        var canPersonal = access.Has(PermissionKeys.MatrixViewPersonal);
        var canSalary = access.Has(PermissionKeys.MatrixViewSalary);
        var canSensitive = access.Has(PermissionKeys.MatrixTemplates);
        bool MaskedFor(MxDsColumn c) => c.Sensitivity switch
        {
            MxSensitivity.Personal => !canPersonal,
            MxSensitivity.Salary => !canSalary,
            MxSensitivity.Sensitive => !canSensitive,
            _ => false,
        };
        var masked = cols.Where(MaskedFor).Select(c => c.Key).ToHashSet();
        foreach (var row in rows)
            foreach (var k in masked)
                if (row[k] is not null) row[k] = Masked;

        var outCols = cols.Select(c => { var f = FieldCatalog.Find(c.Field); return new MxRowsColumn(c.Key, c.Header, c.Type, c.Sensitivity, masked.Contains(c.Key), f?.Bn, f?.En); }).ToList();
        return MxResult<MxRowsDto>.Success(new MxRowsDto(d.Id, total, offset, outCols, rows, hl));
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
                MxHighlightToken.Encode(datasetId, rows, i.Field is null ? new List<string>() : new List<string> { i.Field }));
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
        return MxResult<MxFixResult>.Success(new MxFixResult(issue.Id, issue.Field, "_raw_" + issue.Field, changed, issue.FixKind));
    }

    public IReadOnlyList<MxFieldDto> Catalog() =>
        FieldCatalog.All.Select(f => new MxFieldDto(f.Key, f.Group, f.Role, f.Type, f.Sensitivity, f.En, f.Bn, f.Derived)).ToList();
}

/// <summary>
/// A highlight token: which rows of which dataset to outline and which cells to
/// fill. Base64url JSON; it carries no data, only row numbers and column keys,
/// and is checked against the dataset before use — the rows route still masks.
/// </summary>
public static class MxHighlightToken
{
    private sealed record Body(int D, List<int> R, List<string> C);

    public static string Encode(int datasetId, List<int> rows, List<string> cells)
    {
        var json = JsonSerializer.Serialize(new Body(datasetId, rows, cells), TemplateMatcher.Json);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static MxHighlight? Decode(string token, int datasetId, IReadOnlySet<string> keys)
    {
        try
        {
            var b64 = token.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            var body = JsonSerializer.Deserialize<Body>(Encoding.UTF8.GetString(Convert.FromBase64String(b64)), TemplateMatcher.Json);
            if (body is null || body.D != datasetId || body.R.Count > 5000 || body.C.Any(c => !keys.Contains(c))) return null;
            return new MxHighlight(body.R, body.C);
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { return null; }
    }
}
