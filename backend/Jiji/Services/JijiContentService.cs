// Jiji — the queue. Every query is scoped to the caller's own rows; someone else's id reads as absent.
//
// Writes carry the UpdatedAt the editor last saw. UpdatedAt is the row's concurrency
// token, so EF's UPDATE matches on it and a stale write changes nothing (409) —
// atomic, and still through the audit interceptor.

using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RabbitIC.Api.Data;
using RabbitIC.Api.Jiji.Dtos;
using RabbitIC.Api.Jiji.Models;
using RabbitIC.Api.Models;
using RabbitIC.Api.Jiji.Workers;

namespace RabbitIC.Api.Jiji.Services;

public sealed class JijiContentService(AppDbContext db, IOptions<JijiOptions> options, ScriptGenerator scripts, JijiStorage storage,
    ModelProviders.DbModelSourceProvider providers, JijiSettingsService settings, ModelProviders.SpeechClient speech)
{
    public const int SubjectMax = 200;
    public const int AngleMax = 400;
    public const int NotesMax = 20_000;

    private static readonly string[] Languages = { "en", "bn" };
    private static readonly string[] Editable = { JijiStatus.Idea, JijiStatus.ScriptDraft };

    private readonly JijiOptions _opt = options.Value;

    public async Task<IReadOnlyList<JijiContentSummaryDto>> ListAsync(int ownerId, string? status, CancellationToken ct)
    {
        var q = db.JijiContents.AsNoTracking().Where(c => c.OwnerId == ownerId);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(c => c.Status == status);
        var rows = await q.OrderByDescending(c => c.UpdatedAt).ToListAsync(ct);
        return rows.Select(JijiContentSummaryDto.From).ToList();
    }

    public async Task<JijiContentDto?> GetAsync(int ownerId, int id, CancellationToken ct)
    {
        var c = await db.JijiContents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.OwnerId == ownerId, ct);
        return c is null ? null : await DtoAsync(c, ct);
    }

    public async Task<JijiResult<JijiContentDto>> CreateAsync(int ownerId, JijiCreateContentRequest req, CancellationToken ct)
    {
        var type = JijiContentType.All.FirstOrDefault(t => t.Equals(req.ContentType?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (type is null) return JijiResult<JijiContentDto>.Fail(JijiResultKind.Invalid, "contentType is TechExplainer, BuildLog, BookInsight or OriginalThought.");
        var language = string.IsNullOrWhiteSpace(req.Language) ? "en" : req.Language.Trim().ToLowerInvariant();
        if (!Languages.Contains(language)) return JijiResult<JijiContentDto>.Fail(JijiResultKind.Invalid, "language is en or bn.");
        if (Fields(req.Subject, req.Angle, req.SourceNotes) is { } bad) return JijiResult<JijiContentDto>.Fail(JijiResultKind.Invalid, bad);

        var now = DateTime.UtcNow;
        var c = new JijiContent
        {
            OwnerId = ownerId,
            ContentType = type,
            Subject = req.Subject!.Trim(),
            Language = language,
            Angle = NullIfBlank(req.Angle),
            SourceNotes = req.SourceNotes?.Trim() ?? "",
            Status = JijiStatus.Idea,
            UploadToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.JijiContents.Add(c);
        await db.SaveChangesAsync(ct);
        return JijiResult<JijiContentDto>.Ok(await DtoAsync(c, ct));
    }

    public async Task<JijiResult<JijiContentDto>> UpdateAsync(int ownerId, int id, JijiUpdateContentRequest req, CancellationToken ct)
    {
        var (c, refused) = await EditableAsync(ownerId, id, req.ExpectedUpdatedAt, ct);
        if (c is null) return refused!;
        if (Fields(req.Subject, req.Angle, req.SourceNotes) is { } bad) return JijiResult<JijiContentDto>.Fail(JijiResultKind.Invalid, bad);

        c.Subject = req.Subject!.Trim();
        c.Angle = NullIfBlank(req.Angle);
        c.SourceNotes = req.SourceNotes?.Trim() ?? "";
        return await SaveAsync(c, ct);
    }

    /// <summary>The model writes the script; it replaces the draft only if nobody saved meanwhile.</summary>
    public async Task<JijiResult<JijiScriptSaved>> GenerateScriptAsync(User user, int id, JijiGenerateScriptRequest req, CancellationToken ct)
    {
        var (c, refused) = await EditableAsync(user.Id, id, req.ExpectedUpdatedAt, ct);
        if (c is null) return Recast<JijiScriptSaved>(refused!);

        var r = await scripts.GenerateAsync(user, c, req.Model, req.Source, ct);
        if (r.Kind != JijiResultKind.Ok) return JijiResult<JijiScriptSaved>.Fail(r.Kind, r.Error!, r.Issues);

        Apply(c, r.Value!.Script);
        var saved = await SaveAsync(c, ct);
        return saved.Kind == JijiResultKind.Ok
            ? JijiResult<JijiScriptSaved>.Ok(new JijiScriptSaved(saved.Value!, r.Value.Check))
            : Recast<JijiScriptSaved>(saved);
    }

    /// <summary>Your edits. Malformed or rule-breaking JSON is refused (422) and never saved; warnings are returned.</summary>
    public async Task<JijiResult<JijiScriptSaved>> SaveScriptAsync(int ownerId, int id, JijiSaveScriptRequest req, CancellationToken ct)
    {
        var (c, refused) = await EditableAsync(ownerId, id, req.ExpectedUpdatedAt, ct);
        if (c is null) return Recast<JijiScriptSaved>(refused!);

        var script = JijiScript.TryParse(req.Script, out var parseError);
        if (script is null) return JijiResult<JijiScriptSaved>.Fail(JijiResultKind.Invalid, parseError!);
        JijiScriptValidator.Normalize(script, c);
        var check = JijiScriptValidator.Check(script, c, forApproval: false);
        if (!check.Ok) return JijiResult<JijiScriptSaved>.Fail(JijiResultKind.Invalid, "The script was not saved.", check.Errors);

        Apply(c, script);
        var saved = await SaveAsync(c, ct);
        return saved.Kind == JijiResultKind.Ok
            ? JijiResult<JijiScriptSaved>.Ok(new JijiScriptSaved(saved.Value!, check))
            : Recast<JijiScriptSaved>(saved);
    }

    /// <summary>
    /// The saved script, checked in full (originalAngle and myTake above all). Raises
    /// Revision and queues the TTS job for it; Render follows TTS (the worker, day 3).
    /// </summary>
    public async Task<JijiResult<JijiContentDto>> ApproveScriptAsync(int ownerId, int id, JijiApproveScriptRequest req, CancellationToken ct)
    {
        var c = await db.JijiContents.FirstOrDefaultAsync(x => x.Id == id && x.OwnerId == ownerId, ct);
        if (c is null) return JijiResult<JijiContentDto>.NotFound();
        if (c.Status != JijiStatus.ScriptDraft || c.ScriptJson is null)
            return JijiResult<JijiContentDto>.Fail(JijiResultKind.Conflict, $"Only a saved draft script can be approved; this item is {c.Status}.");
        if (c.UpdatedAt != req.ExpectedUpdatedAt) return Stale<JijiContentDto>();

        var script = JijiScript.TryParse(c.ScriptJson, out var parseError);
        if (script is null) return JijiResult<JijiContentDto>.Fail(JijiResultKind.Invalid, parseError!);
        JijiScriptValidator.Normalize(script, c);
        var check = JijiScriptValidator.Check(script, c, forApproval: true);
        if (!check.Ok) return JijiResult<JijiContentDto>.Fail(JijiResultKind.Invalid, "The script cannot be approved yet.", check.Errors);

        c.Revision += 1;
        c.Status = JijiStatus.ScriptApproved;
        c.RejectReason = null;
        c.LastError = null;
        db.JijiJobs.Add(new JijiJob
        {
            ContentId = c.Id,
            ContentRevision = c.Revision,
            Type = JijiJobType.Tts,
            Status = JijiJobStatus.Pending,
            AvailableAtUtc = DateTime.UtcNow,
        });
        return await SaveAsync(c, ct);
    }

    /// <summary>An idea or draft you no longer want. Refused while one of its jobs is pending or running.</summary>
    public async Task<JijiResult<bool>> DeleteAsync(int ownerId, int id, CancellationToken ct)
    {
        var c = await db.JijiContents.FirstOrDefaultAsync(x => x.Id == id && x.OwnerId == ownerId, ct);
        if (c is null) return JijiResult<bool>.NotFound();
        if (!Editable.Contains(c.Status))
            return JijiResult<bool>.Fail(JijiResultKind.Conflict, $"Only an idea or a draft can be deleted; this item is {c.Status}.");
        if (await db.JijiJobs.AnyAsync(j => j.ContentId == id && (j.Status == JijiJobStatus.Pending || j.Status == JijiJobStatus.Running), ct))
            return JijiResult<bool>.Fail(JijiResultKind.Conflict, "A job for this item is still queued or running.");
        db.JijiContents.Remove(c);
        await db.SaveChangesAsync(ct);
        // Narration from an earlier, rejected revision goes with it.
        var dir = storage.Resolve($"{id}");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        return JijiResult<bool>.Ok(true);
    }

    /// <summary>What Settings shows: which pieces are configured. Names only, never a value.</summary>
    public async Task<JijiStatusDto> StatusAsync(int ownerId, CancellationToken ct)
    {
        var account = await db.JijiYouTubeAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.OwnerId == ownerId, ct);
        var s = await settings.GetAsync(ct);
        return new JijiStatusDto(
            StorageWritable(),
            s.ScriptModel,
            s.ScriptModel is null ? null : s.ScriptModelSource,
            await JijiVoice.LoadAsync(db, ownerId, ct) is { Length: > 0 },
            await SpeechStatusAsync(s, ct),
            _opt.YouTube.IsConfigured,
            account?.RefreshTokenProtected is not null,
            account?.ChannelTitle,
            await ProviderStatusAsync(s, ct));
    }

    /// <summary>Is the speech provider answering right now? A short GET /audio/voices; never a synthesis.</summary>
    private async Task<JijiSpeechStatusDto> SpeechStatusAsync(JijiEffectiveSettings s, CancellationToken ct)
    {
        if (s.SpeechProvider is null) return new(null, null, null, "Not chosen", s.SpeechModel, s.Voice, false);
        var rt = providers.Runtime(s.SpeechProvider);
        if (providers.Speech(s.SpeechProvider) is not { } p)
            return new(s.SpeechProvider, rt?.Name, false, rt is null ? "Not in Model providers" : "Switched off or key unreadable", s.SpeechModel, s.Voice, false);

        var listed = s.Voice is not null && p.Voices.Contains(s.Voice);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(4));
            await speech.VoicesAsync(p.Target, limit.Token);
            return new(s.SpeechProvider, p.Target.Name, true, "Connected", s.SpeechModel, s.Voice, listed);
        }
        catch (ModelProviders.SpeechException e)
        {
            return new(s.SpeechProvider, p.Target.Name, false, e.Message, s.SpeechModel, s.Voice, listed);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(s.SpeechProvider, p.Target.Name, false, $"Speech provider not reachable at {p.Target.BaseUri.GetLeftPart(UriPartial.Path).TrimEnd('/')}. Is Kokoro running?", s.SpeechModel, s.Voice, listed);
        }
    }

    /// <summary>Jiji → Settings: the effective values and every speech provider with its voices.</summary>
    public async Task<JijiSettingsDto> SettingsAsync(User user, CancellationToken ct)
    {
        var s = await settings.GetAsync(ct);
        var list = providers.SpeechProviders
            .Select(r => new JijiSpeechProviderDto(r.Key, r.Name, r.Enabled, r.Status, providers.Speech(r.Key)?.Voices ?? Array.Empty<string>()))
            .ToList();
        return new JijiSettingsDto(s.ScriptModel, s.ScriptModelSource, s.SpeechProvider, s.SpeechModel, s.Voice, list,
            Rbac.AccessPolicy.IsSuperUser(user));
    }

    /// <summary>The Settings provider behind Jiji:ModelSource, or null for Ollama and config sources.</summary>
    private async Task<JijiProviderStatusDto?> ProviderStatusAsync(JijiEffectiveSettings s, CancellationToken ct)
    {
        var key = s.ScriptModel is null ? null : s.ScriptModelSource;
        if (string.IsNullOrEmpty(key) || providers.Runtime(key) is not { } rt) return null;
        var row = await db.ModelProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Key == key, ct);
        // Weighted tokens (tokens × the model's multiplier), the unit the plan and the caps count in.
        var start = ModelProviders.ModelAccess.MonthStartUtc();
        var month = db.ModelProviderUsages.AsNoTracking().Where(u => u.ProviderKey == key && u.CreatedAt >= start);
        var all = (long)Math.Round(await month.SumAsync(u => u.WeightedTokens, ct));
        var jiji = (long)Math.Round(await month.Where(u => u.Module == "jiji").SumAsync(u => u.WeightedTokens, ct));
        return new JijiProviderStatusDto(rt.Key, rt.Name, rt.Status, rt.Message, jiji, all, row?.MonthlyTokenBudget);
    }

    /// <summary>
    /// A Failed job (or a Pending one waiting after a failed attempt) back to Pending now, its attempts reset. Refused for UnknownOutcome (an upload
    /// nobody can vouch for: resolve-upload settles it) and for a job of an older revision.
    /// One conditional UPDATE, so two clicks re-queue it once.
    /// </summary>
    public async Task<JijiResult<JijiContentDto>> RetryJobAsync(int ownerId, int jobId, CancellationToken ct)
    {
        var job = await db.JijiJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);
        var c = job is null ? null : await db.JijiContents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == job.ContentId && x.OwnerId == ownerId, ct);
        if (job is null || c is null) return JijiResult<JijiContentDto>.Fail(JijiResultKind.NotFound, "No such job.");
        if (job.Status == JijiJobStatus.UnknownOutcome)
            return JijiResult<JijiContentDto>.Fail(JijiResultKind.Conflict, "Nobody can tell whether this upload reached YouTube. Resolve it instead: paste the video id from Studio, or confirm it never uploaded.");
        if (job.ContentRevision != c.Revision)
            return JijiResult<JijiContentDto>.Fail(JijiResultKind.Conflict, $"This job is for revision {job.ContentRevision}; the item is at {c.Revision}.");

        var now = DateTime.UtcNow;
        var n = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "JijiJobs" SET "Status" = 'Pending', "Attempts" = 0, "AvailableAtUtc" = {now}, "CompletedAtUtc" = NULL,
                "LeaseToken" = NULL, "LeasedBy" = NULL, "LeaseUntilUtc" = NULL
            WHERE "Id" = {jobId} AND ("Status" = 'Failed' OR ("Status" = 'Pending' AND "Attempts" > 0))
            """, ct);
        // A failed job, or one waiting out its back-off after a failed attempt (Retry now: the back-off
        // rules are unchanged, a person only skips the wait — e.g. once the speech server is back).
        if (n != 1) return JijiResult<JijiContentDto>.Fail(JijiResultKind.Conflict, $"Only a failed or waiting job can be retried; this one is {job.Status}.");
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "JijiContents" SET "LastError" = NULL, "UpdatedAt" = {now} WHERE "Id" = {c.Id}
            """, ct);
        return JijiResult<JijiContentDto>.Ok((await GetAsync(ownerId, c.Id, ct))!);
    }

    /// <summary>The current revision's narration.wav, when its timings say it is whole.</summary>
    public async Task<string?> NarrationPathAsync(int ownerId, int id, CancellationToken ct)
    {
        var c = await db.JijiContents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.OwnerId == ownerId, ct);
        return c is null ? null : NarrationPath(c);
    }

    public async Task<JijiNarration?> TimingsAsync(int ownerId, int id, CancellationToken ct)
    {
        var c = await db.JijiContents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.OwnerId == ownerId, ct);
        if (c is null || NarrationPath(c) is null) return null;
        var json = await File.ReadAllTextAsync(storage.Resolve($"{c.Id}/r{c.Revision}/{JijiNarration.FileName}"), ct);
        return System.Text.Json.JsonSerializer.Deserialize<JijiNarration>(json, JijiNarration.Json);
    }

    private string? NarrationPath(JijiContent c)
    {
        if (c.Revision == 0) return null;
        var timings = storage.Resolve($"{c.Id}/r{c.Revision}/{JijiNarration.FileName}");
        var audio = storage.Resolve($"{c.Id}/r{c.Revision}/{JijiNarration.AudioName}");
        return File.Exists(timings) && File.Exists(audio) ? audio : null;
    }

    // ---- helpers ----

    private async Task<(JijiContent?, JijiResult<JijiContentDto>?)> EditableAsync(int ownerId, int id, DateTime expected, CancellationToken ct)
    {
        var c = await db.JijiContents.FirstOrDefaultAsync(x => x.Id == id && x.OwnerId == ownerId, ct);
        if (c is null) return (null, JijiResult<JijiContentDto>.NotFound());
        if (!Editable.Contains(c.Status))
            return (null, JijiResult<JijiContentDto>.Fail(JijiResultKind.Conflict,
                $"The script is {c.Status}; reject it to edit again."));
        if (c.UpdatedAt != expected) return (null, Stale<JijiContentDto>());
        return (c, null);
    }

    /// <summary>Stamps UpdatedAt and saves; the UPDATE matches the UpdatedAt read, so a write in between makes it a 409.</summary>
    private async Task<JijiResult<JijiContentDto>> SaveAsync(JijiContent c, CancellationToken ct)
    {
        c.UpdatedAt = DateTime.UtcNow;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return Stale<JijiContentDto>(); }
        return JijiResult<JijiContentDto>.Ok(await DtoAsync(c, ct));
    }

    private static void Apply(JijiContent c, JijiScript s)
    {
        c.ScriptJson = s.Serialize();
        c.Title = s.Title == "" ? null : s.Title;
        c.Description = s.Description == "" ? null : s.Description;
        c.TagsJson = System.Text.Json.JsonSerializer.Serialize(s.Hashtags);
        c.Status = JijiStatus.ScriptDraft;
    }

    private async Task<JijiContentDto> DtoAsync(JijiContent c, CancellationToken ct)
    {
        var jobs = await db.JijiJobs.AsNoTracking().Where(j => j.ContentId == c.Id).OrderBy(j => j.Id).ToListAsync(ct);
        JijiScriptCheck? check = null;
        if (c.ScriptJson is not null && JijiScript.TryParse(c.ScriptJson, out _) is { } s)
        {
            JijiScriptValidator.Normalize(s, c);
            check = JijiScriptValidator.Check(s, c, forApproval: true);
        }
        return JijiContentDto.From(c, jobs, check, NarrationPath(c) is not null);
    }

    private static string? Fields(string? subject, string? angle, string? notes)
    {
        if (string.IsNullOrWhiteSpace(subject)) return "Write the subject.";
        if (subject.Trim().Length > SubjectMax) return $"The subject is over {SubjectMax} characters.";
        if (angle is not null && angle.Trim().Length > AngleMax) return $"The angle is over {AngleMax} characters.";
        if (notes is not null && notes.Length > NotesMax) return $"The notes are over {NotesMax:N0} characters.";
        return null;
    }

    private static string? NullIfBlank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    private static JijiResult<T> Stale<T>() =>
        JijiResult<T>.Fail(JijiResultKind.Conflict, "This item changed since you opened it. Reload it, then make your change again.");

    private static JijiResult<T> Recast<T>(JijiResult<JijiContentDto> r) => JijiResult<T>.Fail(r.Kind, r.Error!, r.Issues);

    private bool StorageWritable()
    {
        try
        {
            Directory.CreateDirectory(_opt.StorageRoot);
            var probe = Path.Combine(_opt.StorageRoot, ".probe");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
