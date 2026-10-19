// মানব সম্পদ · প্রসিডিউর বিশ্লেষণ (Procedure Analyzer), Phase A1 —
// docs/HR_PROCEDURE_ANALYZER_PROMPT.md §3.1–§3.4.
//
// An uploaded .sql file is text: it is read, hashed, scanned (HrProcedureScanner)
// and stored, never run. Each version is scanned once, at upload; its facts stay
// with it. What a code value means comes from the lookup registry only; what the
// registry does not know becomes a review question, one per distinct item, with
// every place it occurs. The setup list is made from the facts and the registry,
// by fixed rules, every line with its evidence lines. No model is called here.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.Data;
using RabbitIC.Api.Models;
using RabbitIC.Api.Rbac;

namespace RabbitIC.Api.Hr;

public sealed record HrProcedureDto(
    int Id, string ObjectName, string Kind, int CurrentVersion, string? UploadedBy, DateTime UploadedAtUtc,
    DateTime CreatedAtUtc, string Analysis, int OpenQuestions);

public sealed record HrProcedureVersionDto(int Version, string Sha256, string? UploadedBy, DateTime AtUtc, string? Note, int Lines);

public sealed record HrOccurrenceDto(int ProcedureId, int Version, string ObjectName, string? Section, int Line, string Snippet, bool Current);

public sealed record HrQuestionDto(
    int Id, string Kind, string Key, string? Column, string? Value, string Text, string Status,
    string? AnswerLabel, string? AnswerNote, string? AnsweredBy, DateTime? AnsweredAtUtc, DateTime CreatedAtUtc,
    List<HrOccurrenceDto> Occurrences);

public sealed record HrLookupDto(string Column, string Value, string? Label, string? Note, string? Source, string? UpdatedBy, DateTime UpdatedAtUtc);

public sealed record HrLookupLogDto(int Id, string Column, string Value, string? OldLabel, string? NewLabel, string? OldNote, string? NewNote,
    string Reason, string? ChangedBy, DateTime AtUtc);

/// <summary>One line of the setup list. Group: master, parameters, units, dates or risks.</summary>
public sealed record HrSetupItem(string Group, string Text, List<int> Evidence, List<int> QuestionIds);

public sealed record HrAnswerInput(string? Label, string? Note, bool? NotBusiness);
public sealed record HrLookupInput(string? Column, string? Value, string? Label, string? Note);

public enum HrProcOutcome { Ok, Invalid, Forbidden, NotFound, Conflict }

public sealed record HrProcResult<T>(HrProcOutcome Outcome, T? Value, string? Error = null)
{
    public static HrProcResult<T> Ok(T value) => new(HrProcOutcome.Ok, value);
    public static HrProcResult<T> Fail(HrProcOutcome outcome, string error) => new(outcome, default, error);
}

public sealed class HrProcedures(AppDbContext db, HrLibrary library, AccessPolicy access, IConfiguration config, ILogger<HrProcedures> log)
{
    public const long MaxUploadBytes = 2L * 1024 * 1024;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The setup list's groups, in order, with their headings (Bengali · English).</summary>
    public static readonly (string Key, string Bn, string En)[] Groups =
    {
        ("master", "যে মাস্টার ডেটা লাগবে", "Master data it reads"),
        ("parameters", "যে প্যারামিটারের উপর নির্ভর করে", "Parameters it depends on"),
        ("units", "বিশেষ ইউনিট", "Units with special handling"),
        ("dates", "তারিখের সীমা", "Date cut-offs"),
        ("risks", "ঝুঁকি", "Risks"),
    };

    // ───────────────────────── who may do what (§3.7) ─────────────────────────

    // Each is one key in the Access catalog, seeded with who held it before role
    // presets: upload and the registry for DevOps, answers for DevOps and hr-assistant.
    public Task<bool> CanUploadAsync(User u, CancellationToken ct = default) => access.HasAsync(u, PermissionKeys.HrProceduresUpload, ct);
    public Task<bool> CanAnswerAsync(User u, CancellationToken ct = default) => access.HasAsync(u, PermissionKeys.HrProceduresAnswer, ct);
    public Task<bool> CanEditRegistryAsync(User u, CancellationToken ct = default) => access.HasAsync(u, PermissionKeys.HrLookupsEdit, ct);

    private HrScanSettings Settings => HrScanSettings.From(config["Hr:Analyzer:Columns"], config["Hr:Analyzer:Aliases"]);

    // ───────────────────────── registry seed ─────────────────────────

    /// <summary>
    /// Adds the rows of docs/hr/hr-lookups.csv the registry does not have yet
    /// (Column, Value). A row already there — seeded, edited or answered — is
    /// never touched, so a restart keeps every answer. The seed is not a change,
    /// so it writes no log rows.
    /// </summary>
    public async Task<int> SeedLookupsAsync(string seedDir, CancellationToken ct = default)
    {
        var file = Path.Combine(seedDir, "hr-lookups.csv");
        if (!File.Exists(file)) return 0;
        var rows = ParseCsv(HrLibrary.Normalize(await File.ReadAllTextAsync(file, ct)));
        if (rows.Count < 2) return 0;

        var head = rows[0].Select(h => h.Trim()).ToList();
        int Col(string name) => head.FindIndex(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));
        int cColumn = Col("Column"), cValue = Col("Value"), cLabel = Col("Label"), cSource = Col("Source");
        if (cColumn < 0 || cValue < 0) return 0;

        var have = (await db.HrLookups.AsNoTracking().Select(l => new { l.Column, l.Value }).ToListAsync(ct))
            .Select(l => Key(l.Column, l.Value)).ToHashSet();
        var added = 0;
        foreach (var r in rows.Skip(1))
        {
            string Cell(int i) => i >= 0 && i < r.Count ? r[i].Trim() : "";
            var column = Cell(cColumn);
            var value = Cell(cValue);
            if (column.Length == 0 || value.Length == 0 || !have.Add(Key(column, value))) continue;
            db.HrLookups.Add(new HrLookup
            {
                Column = column, Value = value,
                Label = Cell(cLabel) is { Length: > 0 } label ? label : null,
                Source = Cell(cSource) is { Length: > 0 } source ? source : "hr-lookups.csv",
                UpdatedAtUtc = DateTime.UtcNow,
            });
            added++;
        }
        await db.SaveChangesAsync(ct);
        return added;
    }

    /// <summary>RFC 4180 rows: quoted cells may hold commas, quotes ("") and line breaks.</summary>
    public static List<List<string>> ParseCsv(string text)
    {
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
                continue;
            }
            switch (ch)
            {
                case '"': quoted = true; break;
                case ',': row.Add(cell.ToString()); cell.Clear(); break;
                case '\n':
                    row.Add(cell.ToString()); cell.Clear();
                    if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
                    row = new List<string>();
                    break;
                default: cell.Append(ch); break;
            }
        }
        row.Add(cell.ToString());
        if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
        return rows;
    }

    private static string Key(string column, string value) => $"{column.ToLowerInvariant()}\u0001{value.ToLowerInvariant()}";

    // ───────────────────────── upload (§3.1) ─────────────────────────

    /// <summary>
    /// Reads, checks, scans and stores an upload as the object's next version.
    /// Refused: not .sql, over 2 MB, no CREATE/ALTER PROCEDURE|FUNCTION, or the
    /// same text as the current version ("no change").
    /// </summary>
    public async Task<HrProcResult<(HrProcedureDto Procedure, HrProcedureVersionDto Version)>> UploadAsync(
        User user, IFormFile file, string? note, CancellationToken ct = default)
    {
        if (!await CanUploadAsync(user, ct)) return Fail<(HrProcedureDto, HrProcedureVersionDto)>(HrProcOutcome.Forbidden, "Only DevOps and SystemAdmin users can upload procedures.");
        if (!string.Equals(Path.GetExtension(file.FileName), ".sql", StringComparison.OrdinalIgnoreCase))
            return Fail<(HrProcedureDto, HrProcedureVersionDto)>(HrProcOutcome.Invalid, "Only .sql files can be uploaded here.");
        if (file.Length > MaxUploadBytes)
            return Fail<(HrProcedureDto, HrProcedureVersionDto)>(HrProcOutcome.Invalid, "The file is larger than 2 MB.");

        var (raw, readError) = await HrLibrary.ReadUploadAsync(file, ct);
        if (readError is not null) return Fail<(HrProcedureDto, HrProcedureVersionDto)>(HrProcOutcome.Invalid, readError);
        var sql = HrLibrary.Normalize(raw);

        if (HrProcedureScanner.Header(SqlTokenizer.Tokenize(sql)) is not { } header)
            return Fail<(HrProcedureDto, HrProcedureVersionDto)>(HrProcOutcome.Invalid,
                "No CREATE or ALTER PROCEDURE|FUNCTION was found in the file; only a procedure or a function can be uploaded.");

        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql))).ToLowerInvariant();
        var procedure = await db.HrProcedures.FirstOrDefaultAsync(p => p.ObjectName == header.Name, ct);
        if (procedure is not null)
        {
            var current = await db.HrProcedureVersions.AsNoTracking()
                .FirstOrDefaultAsync(v => v.ProcedureId == procedure.Id && v.Version == procedure.CurrentVersion, ct);
            if (current?.Sha256 == sha)
                return Fail<(HrProcedureDto, HrProcedureVersionDto)>(HrProcOutcome.Conflict, $"No change: this file is the same as v{procedure.CurrentVersion}.");
        }

        var facts = HrProcedureScanner.Scan(sql, Settings);
        var now = DateTime.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (procedure is null)
        {
            procedure = new HrProcedure { ObjectName = header.Name, Kind = header.Kind, CreatedAtUtc = now };
            db.HrProcedures.Add(procedure);
            await db.SaveChangesAsync(ct);
        }
        procedure.CurrentVersion++;
        procedure.Kind = header.Kind;
        var version = new HrProcedureVersion
        {
            ProcedureId = procedure.Id, Version = procedure.CurrentVersion, Sql = sql, Sha256 = sha,
            UploadedByUserId = user.Id, AtUtc = now, Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            FactsJson = JsonSerializer.Serialize(facts, Json),
        };
        db.HrProcedureVersions.Add(version);
        await db.SaveChangesAsync(ct);
        await SyncQuestionsAsync(procedure, version.Version, facts, sql, ct);
        await tx.CommitAsync(ct);

        // The Library copy (§3.1): the newest code, so Ask cites it.
        try { await library.WriteProcedureCopyAsync(user, procedure.ObjectName, sql, ct); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "HR procedures: {Object} v{Version} saved, but its Library copy could not be written.", procedure.ObjectName, version.Version);
        }

        log.LogInformation("HR procedures: {User} uploaded {Object} v{Version}.", user.Username, procedure.ObjectName, version.Version);
        return HrProcResult<(HrProcedureDto, HrProcedureVersionDto)>.Ok(
            ((await ListAsync(ct)).First(p => p.Id == procedure.Id), ToDto(version, user.Username)));
    }

    private static HrProcResult<T> Fail<T>(HrProcOutcome outcome, string error) => HrProcResult<T>.Fail(outcome, error);

    private static HrProcedureVersionDto ToDto(HrProcedureVersion v, string? by) =>
        new(v.Version, v.Sha256, by, v.AtUtc, v.Note, v.Sql.Count(c => c == '\n') + 1);

    // ───────────────────────── reads ─────────────────────────

    public async Task<List<HrProcedureDto>> ListAsync(CancellationToken ct = default)
    {
        var procedures = await db.HrProcedures.AsNoTracking().OrderBy(p => p.ObjectName).ToListAsync(ct);
        var ids = procedures.Select(p => p.Id).ToList();
        var current = (await db.HrProcedureVersions.AsNoTracking()
                .Where(v => ids.Contains(v.ProcedureId))
                .Select(v => new { v.ProcedureId, v.Version, v.UploadedByUserId, v.AtUtc }).ToListAsync(ct))
            .Where(v => procedures.Any(p => p.Id == v.ProcedureId && p.CurrentVersion == v.Version))
            .ToDictionary(v => v.ProcedureId);
        var names = await UsernamesAsync(current.Values.Select(v => v.UploadedByUserId), ct);

        var open = await db.HrQuestionOccurrences.AsNoTracking()
            .Join(db.HrReviewQuestions.Where(q => q.Status == HrReviewQuestion.StatusOpen), o => o.QuestionId, q => q.Id, (o, q) => new { o.ProcedureId, o.Version, o.QuestionId })
            .ToListAsync(ct);

        return procedures.Select(p =>
        {
            var v = current.GetValueOrDefault(p.Id);
            var openCount = open.Where(o => o.ProcedureId == p.Id && o.Version == p.CurrentVersion).Select(o => o.QuestionId).Distinct().Count();
            return new HrProcedureDto(p.Id, p.ObjectName, p.Kind, p.CurrentVersion,
                v is null ? null : names.GetValueOrDefault(v.UploadedByUserId), v?.AtUtc ?? p.CreatedAtUtc, p.CreatedAtUtc,
                "facts", openCount);
        }).ToList();
    }

    /// <summary>A procedure at a version (default: its current one): the version's text, facts and the version list.</summary>
    public async Task<object?> GetAsync(int id, int? version, CancellationToken ct = default)
    {
        var p = await db.HrProcedures.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return null;
        var versions = await db.HrProcedureVersions.AsNoTracking().Where(v => v.ProcedureId == id).OrderByDescending(v => v.Version).ToListAsync(ct);
        var shown = versions.FirstOrDefault(v => v.Version == (version ?? p.CurrentVersion));
        if (shown is null) return null;
        var names = await UsernamesAsync(versions.Select(v => v.UploadedByUserId), ct);
        var summary = (await ListAsync(ct)).First(x => x.Id == id);
        return new
        {
            summary.Id, summary.ObjectName, summary.Kind, summary.CurrentVersion, summary.OpenQuestions,
            version = ToDto(shown, names.GetValueOrDefault(shown.UploadedByUserId)),
            versions = versions.Select(v => ToDto(v, names.GetValueOrDefault(v.UploadedByUserId))).ToList(),
            facts = FactsOf(shown),
            sql = shown.Sql,
        };
    }

    private static HrProcFacts FactsOf(HrProcedureVersion v) => JsonSerializer.Deserialize<HrProcFacts>(v.FactsJson, Json)!;

    // ───────────────────────── review questions (§3.3) ─────────────────────────

    private sealed record Item(string Kind, string Key, string? Column, string? Value, string Text, List<int> Lines);

    /// <summary>
    /// The questions a version raises: an unlabelled code value, a hard-coded
    /// employee, GETDATE() in a business condition, a table written that no
    /// confirmed Rulebook page names. A question already asked — open or closed —
    /// only gains this version's places; a closed one stays closed.
    /// </summary>
    private async Task SyncQuestionsAsync(HrProcedure procedure, int version, HrProcFacts facts, string sql, CancellationToken ct)
    {
        var lines = sql.Split('\n');
        var labels = await LabelsAsync(ct);
        var items = new List<Item>();

        foreach (var c in facts.Conditions)
            foreach (var v in c.Values)
                if (!labels.ContainsKey(Key(c.Column, v)))
                    items.Add(new Item("lookup", $"lookup:{c.Column.ToLowerInvariant()}:{v.ToLowerInvariant()}", c.Column, v,
                        $"What does {c.Column} {v} mean?", c.Lines));
        foreach (var e in facts.Employees)
            items.Add(new Item("employee", $"employee:{e.Value}", null, e.Value, $"Why is employee {e.Value} set by hand ({e.Form})?", e.Lines));
        foreach (var r in facts.RunDate.Where(r => r.Business))
            items.Add(new Item("rundate", $"rundate:{procedure.ObjectName.ToLowerInvariant()}:{ShortHash(Squash(r.Snippet))}", null, r.Function,
                $"Should this rule{(string.IsNullOrEmpty(r.Section) ? "" : $" in \"{r.Section}\"")} depend on the run date ({r.Function}) or on the separation date?",
                new List<int> { r.Line }));
        var ruled = await RulebookTextAsync(ct);
        foreach (var w in facts.TablesWritten.GroupBy(w => w.Table, StringComparer.OrdinalIgnoreCase))
        {
            if (ruled.Contains(w.Key, StringComparison.OrdinalIgnoreCase)) continue;
            items.Add(new Item("table", $"table:{w.Key.ToLowerInvariant()}", null, w.First().Table,
                $"No Rulebook page covers what is written to {w.First().Table} ({string.Join(", ", w.Select(x => x.Operation).Distinct())}). Which rule is it?",
                w.SelectMany(x => x.Lines).Distinct().OrderBy(l => l).ToList()));
        }

        // Several items can share a key (one value in two conditions): one question, all their lines.
        var merged = items.GroupBy(i => i.Key).Select(g => g.First() with { Lines = g.SelectMany(i => i.Lines).Distinct().OrderBy(l => l).ToList() }).ToList();
        if (merged.Count == 0) return;

        var keys = merged.Select(i => i.Key).ToList();
        var existing = await db.HrReviewQuestions.Where(q => keys.Contains(q.Key)).ToDictionaryAsync(q => q.Key, ct);
        var now = DateTime.UtcNow;
        foreach (var i in merged.Where(i => !existing.ContainsKey(i.Key)))
        {
            var q = new HrReviewQuestion { Kind = i.Kind, Key = i.Key, Column = i.Column, Value = i.Value, Text = i.Text, Status = HrReviewQuestion.StatusOpen, CreatedAtUtc = now };
            db.HrReviewQuestions.Add(q);
            existing[i.Key] = q;
        }
        await db.SaveChangesAsync(ct);

        string? Section(int line) => facts.Sections.LastOrDefault(s => s.LineFrom <= line && line <= s.LineTo)?.Name;
        foreach (var i in merged)
            foreach (var line in i.Lines)
                db.HrQuestionOccurrences.Add(new HrQuestionOccurrence
                {
                    QuestionId = existing[i.Key].Id, ProcedureId = procedure.Id, Version = version, ObjectName = procedure.ObjectName,
                    Section = Section(line), Line = line, Snippet = Snip(line <= lines.Length ? lines[line - 1] : ""),
                });
        await db.SaveChangesAsync(ct);
    }

    private static string Squash(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
    private static string ShortHash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..12].ToLowerInvariant();
    private static string Snip(string line) { var t = line.Trim(); return t.Length <= 240 ? t : t[..240] + "…"; }

    /// <summary>The confirmed text of every confirmed Rulebook page, as one string to search for a table name.</summary>
    private async Task<string> RulebookTextAsync(CancellationToken ct)
    {
        var pages = await db.HrRulePages.AsNoTracking()
            .Where(p => p.Status == HrRulePage.StatusConfirmed && p.ConfirmedVersion != null)
            .Select(p => new { p.Id, p.ConfirmedVersion }).ToListAsync(ct);
        var sb = new StringBuilder();
        foreach (var p in pages)
            sb.Append(await db.HrRulePageVersions.AsNoTracking().Where(v => v.PageId == p.Id && v.Version == p.ConfirmedVersion)
                .Select(v => v.Body).FirstOrDefaultAsync(ct)).Append('\n');
        return sb.ToString();
    }

    /// <summary>Registry labels by (column, value), ignoring case; rows with no label are not known.</summary>
    private async Task<Dictionary<string, string>> LabelsAsync(CancellationToken ct) =>
        (await db.HrLookups.AsNoTracking().Where(l => l.Label != null && l.Label != "").ToListAsync(ct))
            .GroupBy(l => Key(l.Column, l.Value)).ToDictionary(g => g.Key, g => g.First().Label!);

    /// <summary>The questions, open first; occurrences in each procedure's current version marked Current.</summary>
    public async Task<List<HrQuestionDto>> QuestionsAsync(string? status, int? procedureId, CancellationToken ct = default)
    {
        var query = db.HrReviewQuestions.AsNoTracking();
        status = string.IsNullOrWhiteSpace(status) ? HrReviewQuestion.StatusOpen : status.Trim().ToLowerInvariant();
        if (status != "all") query = query.Where(q => q.Status == status);
        var questions = await query.ToListAsync(ct);
        var ids = questions.Select(q => q.Id).ToList();
        var occurrences = (await db.HrQuestionOccurrences.AsNoTracking().Where(o => ids.Contains(o.QuestionId)).ToListAsync(ct))
            .GroupBy(o => o.QuestionId).ToDictionary(g => g.Key, g => g.ToList());
        var currentVersions = await db.HrProcedures.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.CurrentVersion, ct);
        var names = await UsernamesAsync(questions.Where(q => q.AnsweredByUserId != null).Select(q => q.AnsweredByUserId!.Value), ct);

        return questions
            .Select(q => new HrQuestionDto(q.Id, q.Kind, q.Key, q.Column, q.Value, q.Text, q.Status, q.AnswerLabel, q.AnswerNote,
                q.AnsweredByUserId is int by ? names.GetValueOrDefault(by) : null, q.AnsweredAtUtc, q.CreatedAtUtc,
                occurrences.GetValueOrDefault(q.Id, new())
                    .OrderBy(o => o.ObjectName, StringComparer.OrdinalIgnoreCase).ThenByDescending(o => o.Version).ThenBy(o => o.Line)
                    .Select(o => new HrOccurrenceDto(o.ProcedureId, o.Version, o.ObjectName, o.Section, o.Line, o.Snippet,
                        currentVersions.GetValueOrDefault(o.ProcedureId) == o.Version)).ToList()))
            .Where(q => procedureId is null || q.Occurrences.Any(o => o.ProcedureId == procedureId && o.Current))
            .OrderBy(q => q.Status == HrReviewQuestion.StatusOpen ? 0 : 1).ThenBy(q => KindOrder(q.Kind)).ThenBy(q => q.Column).ThenBy(q => q.Id)
            .ToList();
    }

    private static int KindOrder(string kind) => kind switch { "lookup" => 0, "employee" => 1, "rundate" => 2, _ => 3 };

    /// <summary>
    /// Answers an open question. A code value needs its label (or "not a business
    /// value"): the label goes into the registry, logged. The other questions take
    /// a note. Either way the question closes everywhere and is never asked again.
    /// </summary>
    public async Task<HrProcResult<HrQuestionDto>> AnswerAsync(User user, int id, HrAnswerInput input, CancellationToken ct = default)
    {
        if (!await CanAnswerAsync(user, ct)) return Fail<HrQuestionDto>(HrProcOutcome.Forbidden, "Only DevOps, hr-assistant and SystemAdmin users can answer questions.");
        var q = await db.HrReviewQuestions.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (q is null) return Fail<HrQuestionDto>(HrProcOutcome.NotFound, "No such question.");
        if (q.Status != HrReviewQuestion.StatusOpen) return Fail<HrQuestionDto>(HrProcOutcome.Conflict, "This question is already closed.");

        var label = input.Label?.Trim();
        var note = input.Note?.Trim();
        var now = DateTime.UtcNow;
        if (input.NotBusiness == true)
        {
            q.Status = HrReviewQuestion.StatusNotBusiness;
            q.AnswerNote = string.IsNullOrEmpty(note) ? null : note;
        }
        else if (q.Kind == "lookup")
        {
            if (string.IsNullOrEmpty(label)) return Fail<HrQuestionDto>(HrProcOutcome.Invalid, "Give the value's label, or mark it as not a business value.");
            await WriteLookupAsync(user, q.Column!, q.Value!, label, string.IsNullOrEmpty(note) ? null : note, $"answer to question #{q.Id}", now, ct, keepNote: true);
            q.Status = HrReviewQuestion.StatusAnswered;
            q.AnswerLabel = label;
            q.AnswerNote = string.IsNullOrEmpty(note) ? null : note;
        }
        else
        {
            if (string.IsNullOrEmpty(note)) return Fail<HrQuestionDto>(HrProcOutcome.Invalid, "Write the answer as a note, or mark it as not a business value.");
            q.Status = HrReviewQuestion.StatusAnswered;
            q.AnswerNote = note;
        }
        q.AnsweredByUserId = user.Id;
        q.AnsweredAtUtc = now;
        await db.SaveChangesAsync(ct);
        return HrProcResult<HrQuestionDto>.Ok((await QuestionsAsync("all", null, ct)).First(x => x.Id == id));
    }

    // ───────────────────────── registry ─────────────────────────

    public async Task<List<HrLookupDto>> LookupsAsync(string? q, string? column, CancellationToken ct = default)
    {
        var rows = await db.HrLookups.AsNoTracking().ToListAsync(ct);
        var names = await UsernamesAsync(rows.Where(r => r.UpdatedByUserId != null).Select(r => r.UpdatedByUserId!.Value), ct);
        var term = q?.Trim();
        return rows
            .Where(r => string.IsNullOrEmpty(column) || string.Equals(r.Column, column, StringComparison.OrdinalIgnoreCase))
            .Where(r => string.IsNullOrEmpty(term)
                || r.Value.Contains(term, StringComparison.OrdinalIgnoreCase) || (r.Label ?? "").Contains(term, StringComparison.OrdinalIgnoreCase)
                || (r.Note ?? "").Contains(term, StringComparison.OrdinalIgnoreCase) || r.Column.Contains(term, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Column, StringComparer.OrdinalIgnoreCase).ThenBy(r => NumericOrder(r.Value)).ThenBy(r => r.Value, StringComparer.OrdinalIgnoreCase)
            .Select(r => new HrLookupDto(r.Column, r.Value, r.Label, r.Note, r.Source, r.UpdatedByUserId is int by ? names.GetValueOrDefault(by) : null, r.UpdatedAtUtc))
            .ToList();
    }

    private static long NumericOrder(string v) => long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : long.MaxValue;

    /// <summary>The registry columns: the scan's parameter columns plus any the registry holds.</summary>
    public async Task<List<string>> ColumnsAsync(CancellationToken ct = default) =>
        Settings.Columns.Concat(await db.HrLookups.AsNoTracking().Select(l => l.Column).Distinct().ToListAsync(ct))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Adds or changes one registry row, logged; a label closes the open question for that value.</summary>
    public async Task<HrProcResult<HrLookupDto>> EditLookupAsync(User user, HrLookupInput input, CancellationToken ct = default)
    {
        if (!await CanEditRegistryAsync(user, ct)) return Fail<HrLookupDto>(HrProcOutcome.Forbidden, "Only DevOps and SystemAdmin users can edit the registry.");
        var column = input.Column?.Trim() ?? "";
        var value = input.Value?.Trim() ?? "";
        if (column.Length == 0 || value.Length == 0) return Fail<HrLookupDto>(HrProcOutcome.Invalid, "Column and value are required.");
        if (column.Length > 100 || value.Length > 200) return Fail<HrLookupDto>(HrProcOutcome.Invalid, "Column or value is too long.");
        var canonical = Settings.Column(column) ?? column;
        var label = string.IsNullOrWhiteSpace(input.Label) ? null : input.Label.Trim();
        var note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim();

        var now = DateTime.UtcNow;
        var changed = await WriteLookupAsync(user, canonical, value, label, note, "edit", now, ct);
        if (!changed) return Fail<HrLookupDto>(HrProcOutcome.Conflict, "No change.");

        if (label is not null)
        {
            var key = $"lookup:{canonical.ToLowerInvariant()}:{value.ToLowerInvariant()}";
            var q = await db.HrReviewQuestions.FirstOrDefaultAsync(x => x.Key == key && x.Status == HrReviewQuestion.StatusOpen, ct);
            if (q is not null)
            {
                q.Status = HrReviewQuestion.StatusAnswered;
                q.AnswerLabel = label;
                q.AnsweredByUserId = user.Id;
                q.AnsweredAtUtc = now;
            }
        }
        await db.SaveChangesAsync(ct);
        return HrProcResult<HrLookupDto>.Ok((await LookupsAsync(null, canonical, ct)).First(r => string.Equals(r.Value, value, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Writes a row and its log line (not saved yet when nothing changed). False when the row already says this.</summary>
    /// <param name="keepNote">An answer without a note keeps the row's note; an edit writes the note it was given.</param>
    private async Task<bool> WriteLookupAsync(User user, string column, string value, string? label, string? note, string reason, DateTime now,
        CancellationToken ct, bool keepNote = false)
    {
        var row = await db.HrLookups.FirstOrDefaultAsync(l => l.Column == column && l.Value == value, ct);
        var (oldLabel, oldNote) = (row?.Label, row?.Note);
        if (keepNote) note ??= oldNote;
        if (row is not null && oldLabel == label && oldNote == note) return false;
        if (row is null)
        {
            row = new HrLookup { Column = column, Value = value, Source = "registry" };
            db.HrLookups.Add(row);
        }
        row.Label = label;
        row.Note = note;
        row.UpdatedByUserId = user.Id;
        row.UpdatedAtUtc = now;
        db.HrLookupLog.Add(new HrLookupLog
        {
            Column = row.Column, Value = row.Value, OldLabel = oldLabel, NewLabel = label, OldNote = oldNote, NewNote = note,
            Reason = reason, ChangedByUserId = user.Id, AtUtc = now,
        });
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<List<HrLookupLogDto>> LookupLogAsync(string? column, string? value, CancellationToken ct = default)
    {
        var query = db.HrLookupLog.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(column)) query = query.Where(l => l.Column == column);
        if (!string.IsNullOrWhiteSpace(value)) query = query.Where(l => l.Value == value);
        var rows = await query.OrderByDescending(l => l.Id).Take(500).ToListAsync(ct);
        var names = await UsernamesAsync(rows.Select(r => r.ChangedByUserId), ct);
        return rows.Select(r => new HrLookupLogDto(r.Id, r.Column, r.Value, r.OldLabel, r.NewLabel, r.OldNote, r.NewNote, r.Reason,
            names.GetValueOrDefault(r.ChangedByUserId), r.AtUtc)).ToList();
    }

    // ───────────────────────── setup list (§3.4) ─────────────────────────

    /// <summary>The setup list of a procedure's current version; null when there is no such procedure.</summary>
    public async Task<(HrProcedure Procedure, HrProcedureVersion Version, string? UploadedBy, List<HrSetupItem> Items)?> SetupAsync(int id, CancellationToken ct = default)
    {
        var p = await db.HrProcedures.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return null;
        var v = await db.HrProcedureVersions.AsNoTracking().FirstAsync(x => x.ProcedureId == id && x.Version == p.CurrentVersion, ct);
        var facts = FactsOf(v);
        var labels = await LabelsAsync(ct);
        var questions = await db.HrReviewQuestions.AsNoTracking().Select(q => new { q.Id, q.Key, q.Status }).ToDictionaryAsync(q => q.Key, ct);
        var uploaded = await db.HrProcedures.AsNoTracking().Select(x => new { x.ObjectName, x.CurrentVersion }).ToListAsync(ct);
        var by = (await UsernamesAsync(new[] { v.UploadedByUserId }, ct)).GetValueOrDefault(v.UploadedByUserId);

        var items = new List<HrSetupItem>();
        static List<int> Ev(IEnumerable<int> lines) => lines.Distinct().OrderBy(l => l).ToList();

        // A value as the list writes it: "3 (Earned leave)", or unknown with its question.
        (string Text, int? Question) Value(string column, string value, bool nameFirst = false)
        {
            if (labels.TryGetValue(Key(column, value), out var label)) return (nameFirst ? $"{value} {label}" : $"{value} ({label})", null);
            var q = questions.GetValueOrDefault($"lookup:{column.ToLowerInvariant()}:{value.ToLowerInvariant()}");
            if (q?.Status == HrReviewQuestion.StatusNotBusiness) return ($"{value} (not a business value)", null);
            return q is null ? ($"{value} (unknown)", null) : ($"{value} (unknown — see question #{q.Id})", q.Id);
        }

        // ---- master data ----
        var filtered = facts.Filters.GroupBy(f => f.Table, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var t in facts.TablesRead)
        {
            if (!filtered.TryGetValue(t.Table, out var filters))
            {
                items.Add(new HrSetupItem("master", $"Must be maintained: {t.Table}", Ev(t.Lines), new()));
                continue;
            }
            foreach (var f in filters)
            {
                var each = f.Terms.Where(x => x.Values is null).Select(x => x.Column).Distinct().ToList();
                var qids = new List<int>();
                var with = f.Terms.Where(x => x.Values is not null).Select(x =>
                {
                    var vals = x.Values!.Select(val => { var (text, q) = Value(x.Column, val); if (q is int n) qids.Add(n); return text; });
                    return $"{x.Column} {(x.Operator == "=" ? "" : x.Operator + " ")}{string.Join(", ", vals)}";
                }).ToList();
                var text = new StringBuilder($"{t.Table}: ");
                text.Append(each.Count > 0 ? $"a row for each {string.Join(" × ", each)}" : "rows");
                if (with.Count > 0) text.Append(" with ").Append(string.Join("; ", with));
                if (f.LatestBy is not null) text.Append($", latest by {f.LatestBy}");
                items.Add(new HrSetupItem("master", text.ToString(), Ev(f.Lines), qids.Distinct().ToList()));
            }
        }

        // ---- parameters ----
        foreach (var g in facts.Conditions.GroupBy(c => c.Column))
        {
            var qids = new List<int>();
            var values = g.SelectMany(c => c.Values).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(val => { var (text, q) = Value(g.Key, val); if (q is int n) qids.Add(n); return text; }).ToList();
            items.Add(new HrSetupItem("parameters", $"{g.Key}: {string.Join(", ", values)}", Ev(g.SelectMany(c => c.Lines)), qids.Distinct().ToList()));
        }

        // ---- units ----
        foreach (var c in facts.Conditions.Where(c => c.Column == "UnitId"))
        {
            var qids = new List<int>();
            var names = c.Values.Select(val => { var (text, q) = Value("UnitId", val, nameFirst: true); if (q is int n) qids.Add(n); return text; }).ToList();
            var where = c.Sections.Where(s => s.Length > 0).ToList();
            items.Add(new HrSetupItem("units",
                $"UnitId {c.Operator} ({string.Join(", ", c.Values)}): {string.Join("; ", names)}{(where.Count > 0 ? $" — in {string.Join(", ", where)}" : "")}",
                Ev(c.Lines), qids));
        }

        // ---- dates ----
        foreach (var d in facts.Dates.OrderBy(d => d.Date, StringComparer.Ordinal))
        {
            var where = d.Sections.Where(s => s.Length > 0).ToList();
            items.Add(new HrSetupItem("dates", $"{d.Date} ({string.Join(", ", d.Operators)}){(where.Count > 0 ? $" — {string.Join(", ", where)}" : "")}", Ev(d.Lines), new()));
        }

        // ---- risks ----
        foreach (var e in facts.Employees)
        {
            var q = questions.GetValueOrDefault($"employee:{e.Value}");
            items.Add(new HrSetupItem("risks", $"Hard-coded employee {e.Value} ({e.Form}){(q is null ? "" : $" — question #{q.Id}")}", Ev(e.Lines), q is null ? new() : new() { q.Id }));
        }
        foreach (var r in facts.RunDate.Where(r => r.Business))
        {
            var q = questions.GetValueOrDefault($"rundate:{p.ObjectName.ToLowerInvariant()}:{ShortHash(Squash(r.Snippet))}");
            items.Add(new HrSetupItem("risks",
                $"Depends on the run date: {r.Function}{(string.IsNullOrEmpty(r.Section) ? "" : $" in {r.Section}")}{(q is null ? "" : $" — question #{q.Id}")}",
                new() { r.Line }, q is null ? new() : new() { q.Id }));
        }
        foreach (var w in facts.TablesWritten.GroupBy(w => w.Table, StringComparer.OrdinalIgnoreCase))
            items.Add(new HrSetupItem("risks", $"Writes {w.First().Table}: {string.Join(", ", w.Select(x => $"{x.Operation} ×{x.Lines.Count}"))}",
                Ev(w.SelectMany(x => x.Lines)), new()));
        foreach (var c in facts.Calls.Where(c => !string.Equals(c.Name, p.ObjectName, StringComparison.OrdinalIgnoreCase)))
        {
            var up = uploaded.FirstOrDefault(x => string.Equals(x.ObjectName, c.Name, StringComparison.OrdinalIgnoreCase));
            items.Add(new HrSetupItem("risks", $"Calls {c.Name} ({c.Kind}) — {(up is null ? "not uploaded" : $"uploaded v{up.CurrentVersion}")}", Ev(c.Lines), new()));
        }

        return (p, v, by, items);
    }

    /// <summary>The setup list as Markdown, under the Rulebook's header and footer: one "- " line per item, in screen order.</summary>
    public static string SetupMarkdown(HrProcedure p, HrProcedureVersion v, string? uploadedBy, List<HrSetupItem> items, DateTime nowUtc)
    {
        var sb = new StringBuilder("# Mostisko · মানব সম্পদ · প্রসিডিউর (Procedures)\n\n")
            .Append($"Procedures · {p.ObjectName} · v{v.Version} · uploaded by {uploadedBy ?? "unknown"} on {v.AtUtc:yyyy-MM-dd} · setup list\n");
        foreach (var (key, bn, en) in Groups)
        {
            var group = items.Where(i => i.Group == key).ToList();
            if (group.Count == 0) continue;
            sb.Append($"\n## {en} · {bn}\n\n");
            foreach (var i in group)
                sb.Append("- ").Append(i.Text).Append($" [{p.ObjectName}.sql {string.Join(", ", i.Evidence.Select(l => $"L{l}"))}]\n");
        }
        sb.Append("\n---\n\n").Append($"Mostisko · মানব সম্পদ · exported {nowUtc:yyyy-MM-dd HH:mm} UTC\n");
        return sb.ToString();
    }

    private async Task<Dictionary<int, string>> UsernamesAsync(IEnumerable<int> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return list.Count == 0 ? new() : await db.Users.AsNoTracking().Where(u => list.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Username, ct);
    }
}
