using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.Data;
using RabbitIC.Api.Models;

namespace RabbitIC.Api.Services;

/// <summary>
/// EF-backed <see cref="IMistakeService"/>.
///
/// Owner scoping is a filter on every query rather than a check afterwards, so
/// "not mine" and "not there" are indistinguishable by construction: the caller
/// gets null either way and answers 404. A 403 would confirm the row exists.
/// </summary>
public class MistakeService : IMistakeService
{
    /// <summary>
    /// The spaced-review ladder, in days. Each completed review moves one rung
    /// further out; past the end, the last interval repeats.
    ///
    /// Deliberately a fixed ladder rather than a scored algorithm: with one user
    /// and a handful of mistakes, SM-2 style scheduling would add tuning
    /// parameters nobody will tune, to solve a problem nobody has yet.
    /// </summary>
    private static readonly int[] ReviewLadderDays = { 2, 5, 11 };

    private readonly AppDbContext _db;

    public MistakeService(AppDbContext db) => _db = db;

    public async Task<MistakeDto> RecordAsync(
        int userId, MistakeSource source, string yourAnswer, string correctAnswer,
        CancellationToken ct = default)
    {
        var module = Normalize(source.Module) ?? IeltsModules.Reading;
        var questionType = Normalize(source.QuestionType) ?? "";
        var skill = Normalize(source.Skill);
        var topic = Normalize(source.Topic);

        // The dedupe key is the *shape* of the error, not the question that
        // produced it — so the same weakness met in three different tests is one
        // row with TimesSeen = 3. Resolved rows are excluded: re-missing
        // something you had closed out is genuinely new information, and should
        // open a fresh row rather than quietly reviving the old one.
        var existing = await Owned(userId)
            .Where(m => !m.Resolved
                     && m.Module == module
                     && m.QuestionType == questionType
                     && m.Skill == skill
                     && m.Topic == topic)
            .OrderBy(m => m.Id)
            .FirstOrDefaultAsync(ct);

        var now = DateTime.Now;

        if (existing is not null)
        {
            existing.TimesSeen++;
            existing.LastSeenAt = now;

            // The latest instance is the useful one to show in review.
            existing.YourAnswer = yourAnswer ?? "";
            existing.CorrectAnswer = correctAnswer ?? "";
            existing.SourceId = source.SourceId;

            // Seeing it again means it is not learned, so pull the review back to
            // the first rung rather than leaving a date that assumed progress.
            existing.NextReviewAt = now.AddDays(ReviewLadderDays[0]);

            await _db.SaveChangesAsync(ct);
            return Project(existing, now);
        }

        var row = new Mistake
        {
            OwnerUserId = userId,
            Module = module,
            SourceType = Normalize(source.SourceType) ?? "reading_question",
            SourceId = source.SourceId,
            QuestionType = questionType,
            Skill = skill,
            Topic = topic,
            YourAnswer = yourAnswer ?? "",
            CorrectAnswer = correctAnswer ?? "",
            Severity = 3,
            FirstSeenAt = now,
            LastSeenAt = now,
            TimesSeen = 1,
            TimesResolved = 0,
            NextReviewAt = now.AddDays(ReviewLadderDays[0]),
            Resolved = false,
        };

        _db.Mistakes.Add(row);
        await _db.SaveChangesAsync(ct);
        return Project(row, now);
    }

    public async Task<IReadOnlyList<MistakeDto>> ListAsync(
        int userId, string? module = null, string? questionType = null, bool dueOnly = false,
        CancellationToken ct = default)
    {
        var now = DateTime.Now;
        var q = Owned(userId);

        if (Normalize(module) is { } m) q = q.Where(x => x.Module == m);
        if (Normalize(questionType) is { } t) q = q.Where(x => x.QuestionType == t);

        if (dueOnly)
        {
            // Due means unresolved and scheduled for now or earlier. A null
            // NextReviewAt on an unresolved row has never been scheduled, so it
            // counts as due rather than disappearing from the queue forever.
            q = q.Where(x => !x.Resolved && (x.NextReviewAt == null || x.NextReviewAt <= now));
        }

        var rows = await q.ToListAsync(ct);

        // Ordered in memory: "due first, then most recent" mixes a computed flag
        // with a column, which SQLite cannot sort as cheaply as it reads.
        return rows
            .OrderByDescending(x => IsDue(x, now))
            .ThenByDescending(x => x.LastSeenAt)
            .Select(x => Project(x, now))
            .ToList();
    }

    public async Task<MistakeDto?> MarkReviewedAsync(
        int userId, int id, bool? resolved = null, CancellationToken ct = default)
    {
        var row = await Owned(userId).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (row is null) return null;

        var now = DateTime.Now;

        row.TimesResolved++;

        if (resolved == true)
        {
            // Closed out: stop scheduling it. The row stays as history.
            row.Resolved = true;
            row.NextReviewAt = null;
        }
        else
        {
            if (resolved == false) row.Resolved = false;

            // One rung further out per completed review, capped at the last.
            var rung = Math.Min(row.TimesResolved, ReviewLadderDays.Length) - 1;
            row.NextReviewAt = now.AddDays(ReviewLadderDays[Math.Max(rung, 0)]);
        }

        await _db.SaveChangesAsync(ct);
        return Project(row, now);
    }

    public async Task<MistakeDto?> TagAsync(
        int userId, int id, string? mistakeType, int? severity, CancellationToken ct = default)
    {
        var row = await Owned(userId).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (row is null) return null;

        // An empty string clears the tag rather than storing a blank.
        var tag = Normalize(mistakeType);
        row.MistakeType = tag;

        if (severity is int s) row.Severity = Math.Clamp(s, 1, 5);

        await _db.SaveChangesAsync(ct);
        return Project(row, DateTime.Now);
    }

    public async Task<IReadOnlyList<MistakeTypeSummary>> SummaryAsync(
        int userId, string? module = null, CancellationToken ct = default)
    {
        var now = DateTime.Now;
        var q = Owned(userId);
        if (Normalize(module) is { } m) q = q.Where(x => x.Module == m);

        var rows = await q.ToListAsync(ct);

        return rows
            .GroupBy(x => x.QuestionType)
            .Select(g => new MistakeTypeSummary(
                QuestionType: g.Key,
                Mistakes: g.Count(),
                TimesSeen: g.Sum(x => x.TimesSeen),
                Unresolved: g.Count(x => !x.Resolved),
                Due: g.Count(x => IsDue(x, now))))
            .OrderByDescending(s => s.TimesSeen)
            .ToList();
    }

    /// <summary>Every read starts here — there is no path to another user's row.</summary>
    private IQueryable<Mistake> Owned(int userId) =>
        _db.Mistakes.Where(m => m.OwnerUserId == userId);

    private static bool IsDue(Mistake m, DateTime now) =>
        !m.Resolved && (m.NextReviewAt == null || m.NextReviewAt <= now);

    /// <summary>Trims, and maps blank to null so "" and null are never two states.</summary>
    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static MistakeDto Project(Mistake m, DateTime now) =>
        new(m.Id, m.Module, m.SourceType, m.SourceId, m.QuestionType, m.Skill, m.Topic,
            m.YourAnswer, m.CorrectAnswer, m.MistakeType, m.Severity,
            m.FirstSeenAt, m.LastSeenAt, m.TimesSeen, m.TimesResolved,
            m.NextReviewAt, m.Resolved, IsDue(m, now));
}
