// মানব সম্পদ · নিয়মাবলি — HR's own rule pages (docs/HR_RULEBOOK_PROMPT.md §3.1–§3.3).
//
// A page is written by hand (Phase R1; drafted from the Library in R2), edited,
// and confirmed by an editor with a note saying why. Every save, confirmation,
// retirement and restore is a new row in HrRulePageVersions — the paper trail.
// That table is append-only: AddVersion below is the only code that writes it,
// nothing updates or deletes a row, and SQLite triggers refuse it if anything
// tries (SchemaUpgrade.EnsureHrRulebookSchema).
//
// Confirming copies the page into the Library (HrLibrary.WriteRulebookCopyAsync)
// as a `rules` item in `search` mode, headed by a line naming the version and who
// confirmed it, so Ask can find and cite it. Edits after that are a draft on top:
// the copy, and so Ask, keep the confirmed text until the next confirmation.
// Retiring switches the copy off; the page and its history stay.
//
// Who may do what (§3.2): editors (HrAssistant, SystemAdmin — the Library's
// editors) do everything; anyone else with the `hr` tab reads pages that have
// been confirmed — confirmed or retired — and only their confirmed and retired
// versions. Drafts answer 403 to them. Every rule is checked here, on the server.

using System.Text;
using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.Data;
using RabbitIC.Api.Models;
using RabbitIC.Api.Services;

namespace RabbitIC.Api.Hr;

public sealed record HrRuleCategory(string Key, string Bn, string En);

/// <summary>A page as the list and the page view see it. Version is filled on a single-page read.</summary>
public sealed record HrRulePageDto(
    int Id, string Title, string Category, string Language, string Status,
    int CurrentVersion, int? ConfirmedVersion, string? ConfirmedBy, DateTime? ConfirmedAtUtc, int? LibraryItemId,
    string? CreatedBy, DateTime CreatedAtUtc, DateTime UpdatedAtUtc, bool DraftInProgress, bool CanEdit,
    HrRuleVersionDto? Version = null);

/// <summary>
/// One version. Body, Display, Formulas and Sources only on a single-version read:
/// Display is the body with each [[= … ]] worked out, as Ask shows it; Formulas say
/// where each result sits in Display, for the formula on hover; Sources are the
/// Library places the page was drafted from (its latest `generated` version up to
/// this one), each opening its item at its lines.
/// </summary>
public sealed record HrRuleVersionDto(
    int Version, string Kind, string? ChangeNote, string? Author, DateTime AtUtc,
    string? Body = null, string? Display = null, IReadOnlyList<HrRuleFormulaDto>? Formulas = null,
    IReadOnlyList<HrRuleSourceDto>? Sources = null);

/// <summary>A Library place a draft was written from. LineFrom is null for an always item, sent whole.</summary>
public sealed record HrRuleSourceDto(int ItemId, string Name, string SourceRef, int? LineFrom, int? LineTo);

/// <summary>
/// A draft from the Library (§3.4). PageId: a new version of that page; otherwise a new page from Title, Category, Language.
/// Keywords: English words HR adds to the search (the glossary adds its own).
/// </summary>
public sealed record HrRuleGenerateRequest(
    int? PageId, string? Title, string? Category, string? Language, List<int>? Items, string? Instruction, string? Model,
    string? Keywords = null);

/// <summary>What a draft is about, once the request is checked.</summary>
public sealed record HrRuleDraftTarget(int? PageId, string Title, HrRuleCategory Category, string Language);

/// <summary>The number check before Confirm: amounts no source of the draft accounts for, and formulas that cannot be worked out.</summary>
public sealed record HrRuleCheckDto(int Version, int? GeneratedVersion, IReadOnlyList<decimal> Unverified, IReadOnlyList<string> FailedFormulas);

public sealed record HrRuleFormulaDto(int Offset, int Length, string Expression, string Display, bool Ok, string? Error);

/// <summary>Create (Title, Category, Language, Body) or save (Body; the rest only when changing). BaseVersion: the version the editor started from.</summary>
public sealed record HrRulePageInput(string? Title, string? Category, string? Language, string? Body, string? Note, int? BaseVersion);

public sealed record HrRuleNoteInput(string? Note);

public sealed record HrRuleRestoreInput(int? Version, string? Note);

public enum HrRuleOutcome { Ok, NotFound, Forbidden, Invalid, Conflict }

public sealed record HrRuleResult(HrRuleOutcome Outcome, HrRulePageDto? Page = null, string? Error = null)
{
    public static HrRuleResult NotFound => new(HrRuleOutcome.NotFound, Error: "No such Rulebook page.");
    public static HrRuleResult Forbidden(string error) => new(HrRuleOutcome.Forbidden, Error: error);
    public static HrRuleResult Invalid(string error) => new(HrRuleOutcome.Invalid, Error: error);
    public static HrRuleResult Conflict(string error) => new(HrRuleOutcome.Conflict, Error: error);
}

public sealed class HrRulebook(AppDbContext db, HrLibrary library)
{
    /// <summary>§1's six categories, fixed (open question 2's default), in display order.</summary>
    public static readonly IReadOnlyList<HrRuleCategory> Categories = new[]
    {
        new HrRuleCategory("settlement", "চূড়ান্ত নিষ্পত্তি", "Settlement"),
        new HrRuleCategory("salary", "বেতন", "Salary"),
        new HrRuleCategory("company-policy", "কোম্পানি নীতি", "Company Policy"),
        new HrRuleCategory("leave-approval", "ছুটি ও অনুমোদন", "Leave & Approval"),
        new HrRuleCategory("attendance", "হাজিরা", "Attendance"),
        new HrRuleCategory("other", "অন্যান্য", "Other"),
    };

    public static readonly string[] Languages = { "bn", "en" };
    public static readonly string[] Statuses = { HrRulePage.StatusDraft, HrRulePage.StatusConfirmed, HrRulePage.StatusRetired };

    public const int MaxTitleChars = 120;

    public const string EditorsOnly = "Only hr-assistant and SystemAdmin users can change the Rulebook.";
    public const string DraftsHidden = "Drafts are visible to Rulebook editors only.";

    public static bool CanEdit(User user) => ContextSourceService.CanEditShared(user);

    // ───────────────────────── reads ─────────────────────────

    /// <summary>
    /// The pages the caller may see, filtered by category and status and searched
    /// by title and by the text of the version the caller would open — an editor
    /// the latest, a reader the confirmed one — in category order, then by title.
    /// </summary>
    public async Task<IReadOnlyList<HrRulePageDto>> ListAsync(User caller, string? q, string? category, string? status, CancellationToken ct = default)
    {
        var editor = CanEdit(caller);
        var pages = (await db.HrRulePages.AsNoTracking().ToListAsync(ct))
            .Where(p => editor || ReaderSees(p))
            .Where(p => string.IsNullOrWhiteSpace(category) || p.Category == category.Trim())
            .Where(p => string.IsNullOrWhiteSpace(status) || p.Status == status.Trim())
            .ToList();

        var query = q?.Trim();
        if (!string.IsNullOrEmpty(query) && pages.Count > 0)
        {
            var ids = pages.Select(p => p.Id).ToList();
            var bodies = (await db.HrRulePageVersions.AsNoTracking().Where(v => ids.Contains(v.PageId))
                    .Select(v => new { v.PageId, v.Version, v.Body }).ToListAsync(ct))
                .ToDictionary(v => (v.PageId, v.Version), v => v.Body);
            pages = pages.Where(p =>
                p.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (bodies.TryGetValue((p.Id, ShownVersion(p, editor)), out var body) && body.Contains(query, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        var order = Categories.Select((c, i) => (c.Key, i)).ToDictionary(x => x.Key, x => x.i);
        pages = pages.OrderBy(p => order.GetValueOrDefault(p.Category, order.Count))
            .ThenBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        return await ProjectAsync(pages, editor, ct);
    }

    /// <summary>One page with one version: the one asked for, or the one the caller would open.</summary>
    public async Task<HrRuleResult> GetAsync(User caller, int id, int? version, CancellationToken ct = default)
    {
        var page = await db.HrRulePages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (page is null) return HrRuleResult.NotFound;
        var editor = CanEdit(caller);
        if (!editor && !ReaderSees(page)) return HrRuleResult.Forbidden(DraftsHidden);

        var number = version ?? ShownVersion(page, editor);
        var row = await db.HrRulePageVersions.AsNoTracking().FirstOrDefaultAsync(v => v.PageId == id && v.Version == number, ct);
        if (row is null) return new(HrRuleOutcome.NotFound, Error: $"This page has no version {number}.");
        if (!editor && !ReaderVersion(row.Kind)) return HrRuleResult.Forbidden(DraftsHidden);

        return new(HrRuleOutcome.Ok, await DetailAsync(page, row, editor, ct));
    }

    /// <summary>The history, newest last. A reader sees the confirmed and retired versions only.</summary>
    public async Task<(HrRuleOutcome Outcome, IReadOnlyList<HrRuleVersionDto>? Versions)> VersionsAsync(User caller, int id, CancellationToken ct = default)
    {
        var page = await db.HrRulePages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (page is null) return (HrRuleOutcome.NotFound, null);
        var editor = CanEdit(caller);
        if (!editor && !ReaderSees(page)) return (HrRuleOutcome.Forbidden, null);

        var rows = await db.HrRulePageVersions.AsNoTracking().Where(v => v.PageId == id).OrderBy(v => v.Version)
            .Select(v => new { v.Version, v.Kind, v.ChangeNote, v.AuthorUserId, v.AtUtc }).ToListAsync(ct);
        rows = rows.Where(v => editor || ReaderVersion(v.Kind)).ToList();
        var names = await UsernamesAsync(rows.Select(v => v.AuthorUserId), ct);
        return (HrRuleOutcome.Ok, rows.Select(v => new HrRuleVersionDto(v.Version, v.Kind, v.ChangeNote, names.GetValueOrDefault(v.AuthorUserId), v.AtUtc)).ToList());
    }

    /// <summary>A version as a Markdown file: the header line, a blank line, the body (§3.6).</summary>
    public async Task<(HrRuleResult Result, string? FileName, string? Text)> DownloadAsync(User caller, int id, int? version, CancellationToken ct = default)
    {
        var got = await GetAsync(caller, id, version, ct);
        if (got.Outcome != HrRuleOutcome.Ok) return (got, null, null);
        var page = got.Page!;
        var v = page.Version!;
        var text = $"{HeaderLine(page.Title, v.Version, v.Kind, v.Author, v.AtUtc, page.Category)}\n\n{v.Body}";
        return (got, $"rulebook-{Slug(page.Title)}-v{v.Version}.md", text);
    }

    /// <summary>
    /// "Export all confirmed": every confirmed page's confirmed version — never a
    /// draft, nor a retired page — grouped by category, each under its header line.
    /// </summary>
    public async Task<string> ExportConfirmedAsync(CancellationToken ct = default)
    {
        var pages = await db.HrRulePages.AsNoTracking()
            .Where(p => p.Status == HrRulePage.StatusConfirmed && p.ConfirmedVersion != null).ToListAsync(ct);
        var ids = pages.Select(p => p.Id).ToList();
        var versions = (await db.HrRulePageVersions.AsNoTracking().Where(v => ids.Contains(v.PageId)).ToListAsync(ct))
            .ToDictionary(v => (v.PageId, v.Version));
        var names = await UsernamesAsync(versions.Values.Select(v => v.AuthorUserId), ct);

        var now = DateTime.UtcNow;
        var sb = new StringBuilder("# Mostisko · মানব সম্পদ · নিয়মাবলি (Rulebook)\n\n")
            .Append($"{pages.Count} confirmed page(s), exported {now:yyyy-MM-dd HH:mm} UTC.\n");
        foreach (var category in Categories)
        {
            var inCategory = pages.Where(p => p.Category == category.Key)
                .OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (inCategory.Count == 0) continue;
            sb.Append($"\n## {category.En} · {category.Bn}\n");
            foreach (var p in inCategory)
            {
                var v = versions[(p.Id, p.ConfirmedVersion!.Value)];
                sb.Append("\n---\n\n")
                  .Append(HeaderLine(p.Title, v.Version, v.Kind, names.GetValueOrDefault(v.AuthorUserId), v.AtUtc, p.Category))
                  .Append("\n\n").Append(v.Body.TrimEnd()).Append('\n');
            }
        }
        return sb.ToString();
    }

    // ───────────────────────── writes ─────────────────────────

    public async Task<HrRuleResult> CreateAsync(User editor, HrRulePageInput input, CancellationToken ct = default)
    {
        if (!CanEdit(editor)) return HrRuleResult.Forbidden(EditorsOnly);
        var title = (input.Title ?? "").Trim();
        if (TitleError(title) is { } titleError) return HrRuleResult.Invalid(titleError);
        var category = (input.Category ?? "").Trim().ToLowerInvariant();
        if (Categories.All(c => c.Key != category))
            return HrRuleResult.Invalid($"category must be one of: {string.Join(", ", Categories.Select(c => c.Key))}.");
        var body = HrLibrary.Normalize(input.Body);
        if (string.IsNullOrWhiteSpace(body)) return HrRuleResult.Invalid("The page is empty; write its rules first.");
        var language = string.IsNullOrWhiteSpace(input.Language) ? GuessLanguage(title + body) : input.Language.Trim().ToLowerInvariant();
        if (!Languages.Contains(language)) return HrRuleResult.Invalid("language must be bn or en.");
        if (await TitleTakenAsync(title, null, ct)) return HrRuleResult.Conflict($"A page titled \"{title}\" already exists.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = DateTime.UtcNow;
        var page = new HrRulePage
        {
            Title = title, Category = category, Language = language, Status = HrRulePage.StatusDraft,
            CurrentVersion = 0, CreatedByUserId = editor.Id, CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        db.HrRulePages.Add(page);
        await db.SaveChangesAsync(ct);
        AddVersion(page, HrRulePageVersion.KindWritten, body, input.Note, editor);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ReadBackAsync(editor, page.Id, ct);
    }

    /// <summary>
    /// A save: always a new `edited` version, even of a confirmed page — the
    /// confirmed version stays what the Library and Ask use until the next
    /// confirmation. A retired page is restored first, not edited.
    /// </summary>
    public async Task<HrRuleResult> SaveAsync(User editor, int id, HrRulePageInput input, CancellationToken ct = default)
    {
        if (!CanEdit(editor)) return HrRuleResult.Forbidden(EditorsOnly);
        var page = await db.HrRulePages.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (page is null) return HrRuleResult.NotFound;
        if (page.Status == HrRulePage.StatusRetired)
            return HrRuleResult.Conflict("This page is retired. Restore a version from its history to edit it again.");
        if (input.BaseVersion is int baseVersion && baseVersion != page.CurrentVersion)
            return HrRuleResult.Conflict($"The page changed while you were editing (now v{page.CurrentVersion}, you started from v{baseVersion}). Reload it and apply your change again.");

        var current = await BodyAsync(page.Id, page.CurrentVersion, ct);
        var body = input.Body is null ? current : HrLibrary.Normalize(input.Body);
        if (string.IsNullOrWhiteSpace(body)) return HrRuleResult.Invalid("The page is empty; write its rules first.");

        var title = string.IsNullOrWhiteSpace(input.Title) ? page.Title : input.Title.Trim();
        if (TitleError(title) is { } titleError) return HrRuleResult.Invalid(titleError);
        if (!string.Equals(title, page.Title, StringComparison.Ordinal) && await TitleTakenAsync(title, page.Id, ct))
            return HrRuleResult.Conflict($"A page titled \"{title}\" already exists.");
        var category = string.IsNullOrWhiteSpace(input.Category) ? page.Category : input.Category.Trim().ToLowerInvariant();
        if (Categories.All(c => c.Key != category))
            return HrRuleResult.Invalid($"category must be one of: {string.Join(", ", Categories.Select(c => c.Key))}.");
        var language = string.IsNullOrWhiteSpace(input.Language) ? page.Language : input.Language.Trim().ToLowerInvariant();
        if (!Languages.Contains(language)) return HrRuleResult.Invalid("language must be bn or en.");

        if (body == current && title == page.Title && category == page.Category && language == page.Language)
            return HrRuleResult.Invalid($"Nothing changed since v{page.CurrentVersion}.");

        page.Title = title;
        page.Category = category;
        page.Language = language;
        AddVersion(page, HrRulePageVersion.KindEdited, body, input.Note, editor);
        await db.SaveChangesAsync(ct);
        return await ReadBackAsync(editor, page.Id, ct);
    }

    /// <summary>
    /// Confirms the latest version with a required note: a `confirmed` version
    /// copying its body is added, the page becomes Confirmed vN, and the Library
    /// copy is written (made on the first confirmation, replaced after).
    /// </summary>
    public async Task<HrRuleResult> ConfirmAsync(User editor, int id, string? note, CancellationToken ct = default)
    {
        if (!CanEdit(editor)) return HrRuleResult.Forbidden(EditorsOnly);
        var page = await db.HrRulePages.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (page is null) return HrRuleResult.NotFound;
        if (Required(note) is not { } why) return HrRuleResult.Invalid("A confirmation needs a note saying why.");
        if (page.Status == HrRulePage.StatusRetired)
            return HrRuleResult.Conflict("This page is retired. Restore a version from its history first.");
        if (page.ConfirmedVersion == page.CurrentVersion)
            return HrRuleResult.Invalid($"v{page.CurrentVersion} is already the confirmed version; nothing has changed since.");
        if (await library.RulebookCopyNameErrorAsync(page.Title, page.LibraryItemId, ct) is { } nameError)
            return HrRuleResult.Conflict(nameError);

        var body = await BodyAsync(page.Id, page.CurrentVersion, ct);
        var version = AddVersion(page, HrRulePageVersion.KindConfirmed, body, why, editor);
        page.Status = HrRulePage.StatusConfirmed;
        page.ConfirmedVersion = version.Version;
        await db.SaveChangesAsync(ct);

        var content = $"{HeaderLine(page.Title, version.Version, version.Kind, editor.Username, version.AtUtc, page.Category)}\n\n{body}";
        var (itemId, error) = await library.WriteRulebookCopyAsync(editor, page.LibraryItemId, page.Title, content, editor.Username, version.AtUtc, ct);
        if (error is not null) return HrRuleResult.Conflict($"v{version.Version} is confirmed, but its Library copy was not written: {error}");
        if (page.LibraryItemId != itemId)
        {
            page.LibraryItemId = itemId;
            await db.SaveChangesAsync(ct);
        }
        return await ReadBackAsync(editor, page.Id, ct);
    }

    /// <summary>Retires a page with a required note: a `retired` version, and the Library copy switched off.</summary>
    public async Task<HrRuleResult> RetireAsync(User editor, int id, string? note, CancellationToken ct = default)
    {
        if (!CanEdit(editor)) return HrRuleResult.Forbidden(EditorsOnly);
        var page = await db.HrRulePages.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (page is null) return HrRuleResult.NotFound;
        if (Required(note) is not { } why) return HrRuleResult.Invalid("Retiring a page needs a note saying why.");
        if (page.Status == HrRulePage.StatusRetired) return HrRuleResult.Invalid("This page is already retired.");

        // The retired version records what stopped being the rule: the confirmed
        // text, not a draft that was never confirmed.
        var body = await BodyAsync(page.Id, page.ConfirmedVersion ?? page.CurrentVersion, ct);
        AddVersion(page, HrRulePageVersion.KindRetired, body, why, editor);
        page.Status = HrRulePage.StatusRetired;
        await db.SaveChangesAsync(ct);

        if (page.LibraryItemId is int itemId) await library.DeactivateRulebookCopyAsync(editor, itemId, ct);
        return await ReadBackAsync(editor, page.Id, ct);
    }

    /// <summary>
    /// Restores an old version as a new `restored` draft, with a required note. A
    /// confirmed page stays confirmed (the restored text is a draft on top); a
    /// retired page comes back as a draft.
    /// </summary>
    public async Task<HrRuleResult> RestoreAsync(User editor, int id, int? version, string? note, CancellationToken ct = default)
    {
        if (!CanEdit(editor)) return HrRuleResult.Forbidden(EditorsOnly);
        var page = await db.HrRulePages.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (page is null) return HrRuleResult.NotFound;
        if (Required(note) is not { } why) return HrRuleResult.Invalid("Restoring a version needs a note saying why.");
        if (version is not int number) return HrRuleResult.Invalid("Say which version to restore.");
        var source = await db.HrRulePageVersions.AsNoTracking().FirstOrDefaultAsync(v => v.PageId == id && v.Version == number, ct);
        if (source is null) return new(HrRuleOutcome.NotFound, Error: $"This page has no version {number}.");
        if (page.Status == HrRulePage.StatusRetired && await TitleTakenAsync(page.Title, page.Id, ct))
            return HrRuleResult.Conflict($"Another page is titled \"{page.Title}\" now; it cannot come back under the same title.");

        AddVersion(page, HrRulePageVersion.KindRestored, source.Body, why, editor);
        if (page.Status == HrRulePage.StatusRetired) page.Status = HrRulePage.StatusDraft;
        await db.SaveChangesAsync(ct);
        return await ReadBackAsync(editor, page.Id, ct);
    }

    // ───────────────────────── drafts from the Library (§3.4) ─────────────────────────

    /// <summary>
    /// Checks a draft request before anything is sent to a model: an editor, a page
    /// that can take a new version (not retired), or a new page's title, category
    /// and language as Create checks them.
    /// </summary>
    public async Task<(HrRuleResult? Refusal, HrRuleDraftTarget? Target)> ValidateDraftAsync(User editor, HrRuleGenerateRequest input, CancellationToken ct = default)
    {
        if (!CanEdit(editor)) return (HrRuleResult.Forbidden(EditorsOnly), null);
        if (input.PageId is int id)
        {
            var page = await db.HrRulePages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
            if (page is null) return (HrRuleResult.NotFound, null);
            if (page.Status == HrRulePage.StatusRetired)
                return (HrRuleResult.Conflict("This page is retired. Restore a version from its history first."), null);
            return (null, new HrRuleDraftTarget(page.Id, page.Title, Categories.First(c => c.Key == page.Category), page.Language));
        }

        var title = (input.Title ?? "").Trim();
        if (TitleError(title) is { } titleError) return (HrRuleResult.Invalid(titleError), null);
        var category = Categories.FirstOrDefault(c => c.Key == (input.Category ?? "").Trim().ToLowerInvariant());
        if (category is null) return (HrRuleResult.Invalid($"category must be one of: {string.Join(", ", Categories.Select(c => c.Key))}."), null);
        var language = string.IsNullOrWhiteSpace(input.Language) ? GuessLanguage(title) : input.Language.Trim().ToLowerInvariant();
        if (!Languages.Contains(language)) return (HrRuleResult.Invalid("language must be bn or en."), null);
        if (await TitleTakenAsync(title, null, ct)) return (HrRuleResult.Conflict($"A page titled \"{title}\" already exists."), null);
        return (null, new HrRuleDraftTarget(null, title, category, language));
    }

    /// <summary>
    /// Saves a model's draft as a `generated` version, with its sources and the
    /// model's details: a new page (v1), or a new version of the page — a draft on
    /// top when the page is confirmed. Never confirmed here (§0 rule 5).
    /// </summary>
    public async Task<HrRuleResult> SaveGeneratedAsync(User editor, HrRuleDraftTarget target, string body, string sourcesJson, string modelJson, string note, CancellationToken ct = default)
    {
        if (!CanEdit(editor)) return HrRuleResult.Forbidden(EditorsOnly);
        body = HrLibrary.Normalize(body);
        HrRulePage? page;
        if (target.PageId is int id)
        {
            page = await db.HrRulePages.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (page is null) return HrRuleResult.NotFound;
            if (page.Status == HrRulePage.StatusRetired) return HrRuleResult.Conflict("This page was retired while the draft was written.");
            AddVersion(page, HrRulePageVersion.KindGenerated, body, note, editor, sourcesJson, modelJson);
            await db.SaveChangesAsync(ct);
            return await ReadBackAsync(editor, page.Id, ct);
        }

        if (await TitleTakenAsync(target.Title, null, ct)) return HrRuleResult.Conflict($"A page titled \"{target.Title}\" was made while the draft was written.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = DateTime.UtcNow;
        page = new HrRulePage
        {
            Title = target.Title, Category = target.Category.Key, Language = target.Language, Status = HrRulePage.StatusDraft,
            CurrentVersion = 0, CreatedByUserId = editor.Id, CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        db.HrRulePages.Add(page);
        await db.SaveChangesAsync(ct);
        AddVersion(page, HrRulePageVersion.KindGenerated, body, note, editor, sourcesJson, modelJson);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ReadBackAsync(editor, page.Id, ct);
    }

    /// <summary>
    /// The number check before Confirm (§3.4): the version's amounts against what
    /// the sources of its latest draft hold (kept with the `generated` version),
    /// formula results and an example's own inputs counting as stated. A page
    /// never drafted from the Library has nothing to check against: only its
    /// formulas are checked. Editors only.
    /// </summary>
    public async Task<(HrRuleOutcome Outcome, HrRuleCheckDto? Check)> CheckAsync(User caller, int id, int? version, CancellationToken ct = default)
    {
        if (!CanEdit(caller)) return (HrRuleOutcome.Forbidden, null);
        var page = await db.HrRulePages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (page is null) return (HrRuleOutcome.NotFound, null);
        var number = version ?? page.CurrentVersion;
        var row = await db.HrRulePageVersions.AsNoTracking().FirstOrDefaultAsync(v => v.PageId == id && v.Version == number, ct);
        if (row is null) return (HrRuleOutcome.NotFound, null);

        var generated = await LatestGeneratedAsync(id, number, ct);
        var known = generated is null ? null : ReadSources(generated.SourcesJson)?.KnownAmounts;
        var (unverified, failed) = HrRuleDraft.Check(row.Body, known ?? new List<decimal>());
        return (HrRuleOutcome.Ok, new HrRuleCheckDto(number, generated?.Version, generated is null ? Array.Empty<decimal>() : unverified, failed));
    }

    private Task<HrRulePageVersion?> LatestGeneratedAsync(int pageId, int upTo, CancellationToken ct) =>
        db.HrRulePageVersions.AsNoTracking()
            .Where(v => v.PageId == pageId && v.Version <= upTo && v.Kind == HrRulePageVersion.KindGenerated)
            .OrderByDescending(v => v.Version).FirstOrDefaultAsync(ct);

    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>A generated version's SourcesJson, as written by the draft endpoint.</summary>
    public sealed record DraftSources(
        string? Query, List<int>? Scope, List<DraftAlways>? Always, List<DraftExcerpt>? Excerpts, List<decimal>? KnownAmounts);
    public sealed record DraftAlways(int ItemId, string Name, int Version);
    public sealed record DraftExcerpt(int ChunkId, int ItemId, string Name, int Version, string? SourceRef, int? LineFrom, int? LineTo, int Tokens, double Score);

    public static string WriteSources(DraftSources sources) => System.Text.Json.JsonSerializer.Serialize(sources, Json);

    private static DraftSources? ReadSources(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<DraftSources>(json, Json); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    // ───────────────────────── the paper trail ─────────────────────────

    /// <summary>
    /// The one way a version is written: appended after the page's latest, with
    /// who and when. Nothing in this codebase updates or deletes a version.
    /// </summary>
    private HrRulePageVersion AddVersion(HrRulePage page, string kind, string body, string? note, User author,
        string? sourcesJson = null, string? modelJson = null)
    {
        page.CurrentVersion++;
        page.UpdatedAtUtc = DateTime.UtcNow;
        var version = new HrRulePageVersion
        {
            PageId = page.Id, Version = page.CurrentVersion, Kind = kind, Body = body,
            ChangeNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            AuthorUserId = author.Id, AtUtc = page.UpdatedAtUtc,
            SourcesJson = sourcesJson, ModelJson = modelJson,
        };
        db.HrRulePageVersions.Add(version);
        return version;
    }

    /// <summary>
    /// "Rulebook · &lt;Title&gt; · v&lt;N&gt; · confirmed by &lt;user&gt; on &lt;date&gt; ·
    /// category &lt;Category&gt;" (§3.3). For a version that is not a confirmation the
    /// verb is its kind — "edited by", "retired by" — so a printed draft never
    /// reads as confirmed.
    /// </summary>
    public static string HeaderLine(string title, int version, string kind, string? user, DateTime atUtc, string category) =>
        $"Rulebook · {title} · v{version} · {kind} by {user ?? "unknown"} on {atUtc:yyyy-MM-dd} · category {category}";

    // ───────────────────────── helpers ─────────────────────────

    /// <summary>A reader sees a page once it has been confirmed, while it is confirmed or retired.</summary>
    private static bool ReaderSees(HrRulePage p) =>
        p.ConfirmedVersion is not null && p.Status is HrRulePage.StatusConfirmed or HrRulePage.StatusRetired;

    private static bool ReaderVersion(string kind) =>
        kind is HrRulePageVersion.KindConfirmed or HrRulePageVersion.KindRetired;

    /// <summary>The version a page opens at: an editor's latest; a reader's confirmed one (the retirement record once retired).</summary>
    private static int ShownVersion(HrRulePage p, bool editor) =>
        editor || p.Status == HrRulePage.StatusRetired ? p.CurrentVersion : p.ConfirmedVersion ?? p.CurrentVersion;

    private async Task<HrRuleResult> ReadBackAsync(User editor, int id, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        return await GetAsync(editor, id, null, ct);
    }

    private async Task<string> BodyAsync(int pageId, int version, CancellationToken ct) =>
        await db.HrRulePageVersions.AsNoTracking().Where(v => v.PageId == pageId && v.Version == version)
            .Select(v => v.Body).FirstAsync(ct);

    private Task<bool> TitleTakenAsync(string title, int? exceptId, CancellationToken ct) =>
        // The column is NOCASE, so this compares ignoring case, as the unique index does.
        db.HrRulePages.AnyAsync(p => p.Status != HrRulePage.StatusRetired && p.Id != exceptId && p.Title == title, ct);

    private static string? TitleError(string title) =>
        title.Length == 0 ? "A page needs a title."
        : title.Length > MaxTitleChars ? $"A title is at most {MaxTitleChars} characters."
        : title.Contains('\n') ? "A title is one line."
        : null;

    private static string? Required(string? note) => string.IsNullOrWhiteSpace(note) ? null : note.Trim();

    /// <summary>Bengali letters in the title or text: a Bengali page.</summary>
    private static string GuessLanguage(string text) => text.Any(c => c is >= 'ঀ' and <= '৿') ? "bn" : "en";

    private static string Slug(string title)
    {
        var sb = new StringBuilder();
        foreach (var c in title.Trim().ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(c) || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.NonSpacingMark
                or System.Globalization.UnicodeCategory.SpacingCombiningMark ? c : '-');
        var slug = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "-{2,}", "-").Trim('-');
        return slug.Length == 0 ? "page" : slug;
    }

    private async Task<Dictionary<int, string>> UsernamesAsync(IEnumerable<int> ids, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToList();
        return await db.Users.AsNoTracking().Where(u => wanted.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Username, ct);
    }

    private async Task<IReadOnlyList<HrRulePageDto>> ProjectAsync(IReadOnlyList<HrRulePage> pages, bool editor, CancellationToken ct)
    {
        var ids = pages.Select(p => p.Id).ToList();
        var confirmations = (await db.HrRulePageVersions.AsNoTracking()
                .Where(v => ids.Contains(v.PageId) && v.Kind == HrRulePageVersion.KindConfirmed)
                .Select(v => new { v.PageId, v.Version, v.AuthorUserId, v.AtUtc }).ToListAsync(ct))
            .ToDictionary(v => (v.PageId, v.Version));
        var names = await UsernamesAsync(confirmations.Values.Select(v => v.AuthorUserId).Concat(pages.Select(p => p.CreatedByUserId)), ct);

        return pages.Select(p =>
        {
            var confirmation = p.ConfirmedVersion is int c && confirmations.TryGetValue((p.Id, c), out var row) ? row : null;
            return new HrRulePageDto(
                p.Id, p.Title, p.Category, p.Language, p.Status,
                editor ? p.CurrentVersion : ShownVersion(p, false), p.ConfirmedVersion,
                confirmation is null ? null : names.GetValueOrDefault(confirmation.AuthorUserId), confirmation?.AtUtc,
                p.LibraryItemId, names.GetValueOrDefault(p.CreatedByUserId), p.CreatedAtUtc, p.UpdatedAtUtc,
                // "Confirmed v4 · draft v5 in progress" — an editor's view only.
                editor && p.Status == HrRulePage.StatusConfirmed && p.CurrentVersion > p.ConfirmedVersion,
                editor);
        }).ToList();
    }

    private async Task<HrRulePageDto> DetailAsync(HrRulePage page, HrRulePageVersion row, bool editor, CancellationToken ct)
    {
        var dto = (await ProjectAsync(new[] { page }, editor, ct))[0];
        var author = (await UsernamesAsync(new[] { row.AuthorUserId }, ct)).GetValueOrDefault(row.AuthorUserId);
        var (display, formulas) = Render(row.Body);
        var generated = row.Kind == HrRulePageVersion.KindGenerated ? row : await LatestGeneratedAsync(page.Id, row.Version, ct);
        var drafted = ReadSources(generated?.SourcesJson);
        var sources = drafted is null ? null
            : (drafted.Excerpts ?? new()).Where(e => !string.IsNullOrWhiteSpace(e.SourceRef)).DistinctBy(e => e.SourceRef)
                .Select(e => new HrRuleSourceDto(e.ItemId, e.Name, e.SourceRef!, e.LineFrom, e.LineTo))
                .Concat((drafted.Always ?? new()).Select(a => new HrRuleSourceDto(a.ItemId, a.Name, a.Name, null, null)))
                .ToList();
        return dto with
        {
            Version = new HrRuleVersionDto(row.Version, row.Kind, row.ChangeNote, author, row.AtUtc, row.Body, display, formulas, sources),
        };
    }

    /// <summary>
    /// The page as it is read: each [[= … ]] worked out by the same evaluator Ask
    /// uses (HrFormulaStream over HrCalculator), with where each result sits.
    /// </summary>
    private static (string Display, IReadOnlyList<HrRuleFormulaDto> Formulas) Render(string body)
    {
        var stream = new HrFormulaStream();
        var sb = new StringBuilder();
        foreach (var piece in stream.Push(body)) sb.Append(piece.Text);
        foreach (var piece in stream.Finish()) sb.Append(piece.Text);
        return (sb.ToString(), stream.Formulas
            .Select(f => new HrRuleFormulaDto(f.Offset, f.Display.Length, f.Expression, f.Display, f.Ok, f.Error)).ToList());
    }
}
