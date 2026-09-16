using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.Data;
using RabbitIC.Api.Models;

namespace RabbitIC.Api.Services;

/// <summary>
/// EF-backed <see cref="IReadingService"/>.
///
/// Two invariants worth keeping:
///
///   Scoring never consults a model. Each supported format reduces to a
///   normalised string comparison, so a mark is reproducible and a disputed one
///   can be traced to a single comparison.
///
///   The candidate never receives the answer key. The take-test path projects to
///   <see cref="ReadingQuestionForAttempt"/>, which has no field that could
///   carry it.
/// </summary>
public class ReadingService : IReadingService
{
    /// <summary>Stated on every result so the band is read as a lookup, not a judgement.</summary>
    private const string BandBasisLabel = "estimated band (raw-score table)";

    private readonly AppDbContext _db;
    private readonly IMistakeService _mistakes;

    public ReadingService(AppDbContext db, IMistakeService mistakes)
    {
        _db = db;
        _mistakes = mistakes;
    }

    // ------------------------------------------------------------------ tests

    public async Task<IReadOnlyList<ReadingTestSummary>> ListTestsAsync(
        int userId, CancellationToken ct = default) =>
        await VisibleTests(userId)
            // Shared demo content first, then the user's own newest-first, so a
            // brand-new account has something to practise with immediately.
            .OrderByDescending(t => t.IsSystem)
            .ThenByDescending(t => t.CreatedAt)
            .Select(t => new ReadingTestSummary(
                t.Id, t.Title, t.Source, t.Questions.Count, t.CreatedAt, t.IsSystem))
            .ToListAsync(ct);

    public async Task<ReadingTestDto?> GetTestAsync(int userId, int testId, CancellationToken ct = default)
    {
        var test = await VisibleTests(userId)
            .Include(t => t.Questions)
            .FirstOrDefaultAsync(t => t.Id == testId, ct);

        return test is null ? null : ProjectTest(test);
    }

    public async Task<ReadingTestDto> CreateTestAsync(
        int userId, ReadingTestInput input, CancellationToken ct = default)
    {
        var test = new ReadingTest
        {
            OwnerUserId = userId,
            Title = (input.Title ?? "").Trim(),
            Passage = input.Passage ?? "",
            Source = string.IsNullOrWhiteSpace(input.Source) ? "self" : input.Source.Trim(),
        };

        _db.ReadingTests.Add(test);
        await _db.SaveChangesAsync(ct);
        return ProjectTest(test);
    }

    public async Task<ReadingWriteResult<ReadingTestDto>> UpdateTestAsync(
        int userId, int testId, ReadingTestInput input, CancellationToken ct = default)
    {
        if (await IsSystemTestAsync(testId, ct)) return ReadingWriteResult<ReadingTestDto>.ReadOnly();

        var test = await OwnedTests(userId)
            .Include(t => t.Questions)
            .FirstOrDefaultAsync(t => t.Id == testId, ct);
        if (test is null) return ReadingWriteResult<ReadingTestDto>.NotFound();

        if (!string.IsNullOrWhiteSpace(input.Title)) test.Title = input.Title.Trim();
        // The passage may legitimately be cleared, so null (absent) and ""
        // (deliberately blank) are treated differently.
        if (input.Passage is not null) test.Passage = input.Passage;
        if (!string.IsNullOrWhiteSpace(input.Source)) test.Source = input.Source.Trim();

        await _db.SaveChangesAsync(ct);
        return ReadingWriteResult<ReadingTestDto>.Ok(ProjectTest(test));
    }

    public async Task<ReadingWriteResult<bool>> DeleteTestAsync(
        int userId, int testId, CancellationToken ct = default)
    {
        if (await IsSystemTestAsync(testId, ct)) return ReadingWriteResult<bool>.ReadOnly();

        var test = await OwnedTests(userId).FirstOrDefaultAsync(t => t.Id == testId, ct);
        if (test is null) return ReadingWriteResult<bool>.NotFound();

        _db.ReadingTests.Remove(test);   // questions cascade
        await _db.SaveChangesAsync(ct);
        return ReadingWriteResult<bool>.Ok(true);
    }

    // -------------------------------------------------------------- questions

    public async Task<ReadingWriteResult<ReadingQuestionDto>> AddQuestionAsync(
        int userId, int testId, ReadingQuestionInput input, CancellationToken ct = default)
    {
        if (await IsSystemTestAsync(testId, ct))
            return ReadingWriteResult<ReadingQuestionDto>.ReadOnly();

        var test = await OwnedTests(userId)
            .Include(t => t.Questions)
            .FirstOrDefaultAsync(t => t.Id == testId, ct);
        if (test is null) return ReadingWriteResult<ReadingQuestionDto>.NotFound();

        var type = ReadingQuestionTypes.Canonical(input.QuestionType)
            ?? throw new ArgumentException(
                $"'{input.QuestionType}' is not a supported question type.");

        var question = new ReadingQuestion
        {
            ReadingTestId = test.Id,
            Order = input.Order ?? (test.Questions.Count == 0 ? 1 : test.Questions.Max(q => q.Order) + 1),
            QuestionType = type,
            Prompt = (input.Prompt ?? "").Trim(),
            OptionsJson = SerializeOptions(input.Options),
            CorrectAnswer = (input.CorrectAnswer ?? "").Trim(),
            Skill = Normalize(input.Skill),
            Topic = Normalize(input.Topic),
            Difficulty = Normalize(input.Difficulty),
        };

        _db.ReadingQuestions.Add(question);
        await _db.SaveChangesAsync(ct);
        return ReadingWriteResult<ReadingQuestionDto>.Ok(ProjectQuestion(question));
    }

    public async Task<ReadingWriteResult<ReadingQuestionDto>> UpdateQuestionAsync(
        int userId, int questionId, ReadingQuestionInput input, CancellationToken ct = default)
    {
        if (await SystemQuestions().AnyAsync(q => q.Id == questionId, ct))
            return ReadingWriteResult<ReadingQuestionDto>.ReadOnly();

        var question = await OwnedQuestions(userId).FirstOrDefaultAsync(q => q.Id == questionId, ct);
        if (question is null) return ReadingWriteResult<ReadingQuestionDto>.NotFound();

        if (!string.IsNullOrWhiteSpace(input.QuestionType))
        {
            question.QuestionType = ReadingQuestionTypes.Canonical(input.QuestionType)
                ?? throw new ArgumentException(
                    $"'{input.QuestionType}' is not a supported question type.");
        }

        if (!string.IsNullOrWhiteSpace(input.Prompt)) question.Prompt = input.Prompt.Trim();
        if (input.Options is not null) question.OptionsJson = SerializeOptions(input.Options);
        if (!string.IsNullOrWhiteSpace(input.CorrectAnswer)) question.CorrectAnswer = input.CorrectAnswer.Trim();
        if (input.Skill is not null) question.Skill = Normalize(input.Skill);
        if (input.Topic is not null) question.Topic = Normalize(input.Topic);
        if (input.Difficulty is not null) question.Difficulty = Normalize(input.Difficulty);
        if (input.Order is int order) question.Order = order;

        await _db.SaveChangesAsync(ct);
        return ReadingWriteResult<ReadingQuestionDto>.Ok(ProjectQuestion(question));
    }

    public async Task<ReadingWriteResult<bool>> DeleteQuestionAsync(
        int userId, int questionId, CancellationToken ct = default)
    {
        if (await SystemQuestions().AnyAsync(q => q.Id == questionId, ct))
            return ReadingWriteResult<bool>.ReadOnly();

        var question = await OwnedQuestions(userId).FirstOrDefaultAsync(q => q.Id == questionId, ct);
        if (question is null) return ReadingWriteResult<bool>.NotFound();

        _db.ReadingQuestions.Remove(question);
        await _db.SaveChangesAsync(ct);
        return ReadingWriteResult<bool>.Ok(true);
    }

    /// <summary>
    /// Is this a seeded test? Asked before the ownership lookup so shared content
    /// reports read-only rather than not-found: it is plainly in the library, and
    /// claiming otherwise would be a lie.
    /// </summary>
    private Task<bool> IsSystemTestAsync(int testId, CancellationToken ct) =>
        _db.ReadingTests.AnyAsync(t => t.Id == testId && t.IsSystem, ct);

    // ---------------------------------------------------------------- attempts

    public async Task<ReadingAttemptStart?> StartAttemptAsync(
        int userId, int testId, CancellationToken ct = default)
    {
        // Anyone may sit a shared test. The attempt and any mistakes it produces
        // still belong to whoever took it.
        var test = await VisibleTests(userId)
            .Include(t => t.Questions)
            .FirstOrDefaultAsync(t => t.Id == testId, ct);
        if (test is null) return null;

        var attempt = new ReadingAttempt
        {
            OwnerUserId = userId,
            ReadingTestId = test.Id,
            StartedAt = DateTime.Now,
            TotalQuestions = test.Questions.Count,
        };

        _db.ReadingAttempts.Add(attempt);
        await _db.SaveChangesAsync(ct);

        return new ReadingAttemptStart(
            attempt.Id, test.Id, test.Title, test.Passage, attempt.StartedAt,
            test.Questions
                .OrderBy(q => q.Order).ThenBy(q => q.Id)
                .Select(q => new ReadingQuestionForAttempt(
                    q.Id, q.Order, q.QuestionType, q.Prompt, DeserializeOptions(q.OptionsJson)))
                .ToList());
    }

    public async Task<ReadingAttemptResult?> SubmitAttemptAsync(
        int userId, int attemptId, IReadOnlyList<ReadingAnswerInput> answers,
        int? durationSeconds = null, CancellationToken ct = default)
    {
        var attempt = await OwnedAttempts(userId)
            .Include(a => a.Answers)
            .FirstOrDefaultAsync(a => a.Id == attemptId, ct);
        if (attempt is null) return null;

        var test = await _db.ReadingTests
            .Include(t => t.Questions)
            .FirstOrDefaultAsync(t => t.Id == attempt.ReadingTestId, ct);
        if (test is null) return null;

        var given = (answers ?? Array.Empty<ReadingAnswerInput>())
            .GroupBy(a => a.QuestionId)
            .ToDictionary(g => g.Key, g => g.Last().Given ?? "");

        // Resubmitting replaces the previous verdicts rather than appending a
        // second set, so an attempt always has exactly one answer per question.
        if (attempt.Answers.Count > 0) _db.ReadingAttemptAnswers.RemoveRange(attempt.Answers);

        var questions = test.Questions.OrderBy(q => q.Order).ThenBy(q => q.Id).ToList();
        var results = new List<ReadingAnswerResult>(questions.Count);
        var wrong = new List<ReadingQuestion>();
        var raw = 0;

        foreach (var q in questions)
        {
            var answer = given.TryGetValue(q.Id, out var value) ? value : "";
            var correct = ReadingAnswerKey.Matches(answer, q.CorrectAnswer);

            if (correct) raw++;
            else wrong.Add(q);

            _db.ReadingAttemptAnswers.Add(new ReadingAttemptAnswer
            {
                ReadingAttemptId = attempt.Id,
                ReadingQuestionId = q.Id,
                GivenAnswer = answer,
                IsCorrect = correct,
            });

            results.Add(new ReadingAnswerResult(
                q.Id, q.Order, q.QuestionType, q.Prompt,
                answer, q.CorrectAnswer, correct, q.Skill, q.Topic));
        }

        attempt.SubmittedAt = DateTime.Now;
        attempt.RawScore = raw;
        attempt.TotalQuestions = questions.Count;
        attempt.BandEstimate = ReadingBandTable.Estimate(raw, questions.Count);
        attempt.DurationSeconds = durationSeconds
            ?? (int)Math.Max(0, (attempt.SubmittedAt.Value - attempt.StartedAt).TotalSeconds);

        await _db.SaveChangesAsync(ct);

        // The spine: every wrong answer is logged with the metadata that makes
        // the weakness view meaningful. Done after the save so a failure here
        // cannot cost the user their score.
        foreach (var q in wrong)
        {
            await _mistakes.RecordAsync(
                userId,
                new MistakeSource(
                    IeltsModules.Reading, "reading_question", q.Id, q.QuestionType, q.Skill, q.Topic),
                given.TryGetValue(q.Id, out var v) ? v : "",
                q.CorrectAnswer,
                ct);
        }

        return new ReadingAttemptResult(
            attempt.Id, test.Id, test.Title,
            raw, questions.Count, attempt.BandEstimate ?? 0, BandBasisLabel,
            attempt.DurationSeconds, attempt.SubmittedAt,
            wrong.Count, results);
    }

    public async Task<IReadOnlyList<ReadingAttemptSummary>> ListAttemptsAsync(
        int userId, CancellationToken ct = default)
    {
        var rows = await OwnedAttempts(userId)
            .OrderByDescending(a => a.StartedAt)
            .Join(_db.ReadingTests, a => a.ReadingTestId, t => t.Id, (a, t) => new { a, t.Title })
            .ToListAsync(ct);

        return rows
            .Select(x => new ReadingAttemptSummary(
                x.a.Id, x.a.ReadingTestId, x.Title, x.a.RawScore, x.a.TotalQuestions,
                x.a.BandEstimate, x.a.StartedAt, x.a.SubmittedAt, x.a.DurationSeconds))
            .ToList();
    }

    public async Task<ReadingAttemptResult?> GetAttemptAsync(
        int userId, int attemptId, CancellationToken ct = default)
    {
        var attempt = await OwnedAttempts(userId)
            .Include(a => a.Answers)
            .FirstOrDefaultAsync(a => a.Id == attemptId, ct);
        if (attempt is null) return null;

        var test = await _db.ReadingTests
            .Include(t => t.Questions)
            .FirstOrDefaultAsync(t => t.Id == attempt.ReadingTestId, ct);
        if (test is null) return null;

        var byQuestion = attempt.Answers.ToDictionary(a => a.ReadingQuestionId);

        var results = test.Questions
            .OrderBy(q => q.Order).ThenBy(q => q.Id)
            .Select(q =>
            {
                byQuestion.TryGetValue(q.Id, out var a);
                return new ReadingAnswerResult(
                    q.Id, q.Order, q.QuestionType, q.Prompt,
                    a?.GivenAnswer ?? "", q.CorrectAnswer, a?.IsCorrect ?? false,
                    q.Skill, q.Topic);
            })
            .ToList();

        return new ReadingAttemptResult(
            attempt.Id, test.Id, test.Title,
            attempt.RawScore, attempt.TotalQuestions,
            attempt.BandEstimate ?? 0, BandBasisLabel,
            attempt.DurationSeconds, attempt.SubmittedAt,
            results.Count(r => !r.IsCorrect), results);
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>
    /// Tests a user may WRITE: their own, never system content. The read-only
    /// case is checked separately so a seeded test can be told apart from one
    /// that simply belongs to somebody else.
    /// </summary>
    private IQueryable<ReadingTest> OwnedTests(int userId) =>
        _db.ReadingTests.Where(t => t.OwnerUserId == userId && !t.IsSystem);

    /// <summary>
    /// Tests a user may READ: their own, plus the shared seeded ones. The union
    /// lives only here, so visibility cannot drift between listing the library,
    /// opening a test, and starting an attempt.
    /// </summary>
    private IQueryable<ReadingTest> VisibleTests(int userId) =>
        _db.ReadingTests.Where(t => t.OwnerUserId == userId || t.IsSystem);

    private IQueryable<ReadingAttempt> OwnedAttempts(int userId) =>
        _db.ReadingAttempts.Where(a => a.OwnerUserId == userId);

    /// <summary>
    /// Questions are owner-scoped through their test — there is no way to reach
    /// one whose parent belongs to someone else.
    /// </summary>
    private IQueryable<ReadingQuestion> OwnedQuestions(int userId) =>
        _db.ReadingQuestions.Where(q => _db.ReadingTests
            .Any(t => t.Id == q.ReadingTestId && t.OwnerUserId == userId && !t.IsSystem));

    /// <summary>A question whose parent is seeded content: visible, never writable.</summary>
    private IQueryable<ReadingQuestion> SystemQuestions() =>
        _db.ReadingQuestions.Where(q => _db.ReadingTests
            .Any(t => t.Id == q.ReadingTestId && t.IsSystem));

    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static string? SerializeOptions(IReadOnlyList<string>? options)
    {
        if (options is null) return null;
        var cleaned = options.Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.Trim()).ToList();
        return cleaned.Count == 0 ? null : JsonSerializer.Serialize(cleaned);
    }

    private static IReadOnlyList<string>? DeserializeOptions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<List<string>>(json); }
        catch (JsonException) { return null; }   // malformed options must not break a sitting
    }

    private static ReadingQuestionDto ProjectQuestion(ReadingQuestion q) =>
        new(q.Id, q.Order, q.QuestionType, q.Prompt, DeserializeOptions(q.OptionsJson),
            q.CorrectAnswer, q.Skill, q.Topic, q.Difficulty);

    private static ReadingTestDto ProjectTest(ReadingTest t) =>
        new(t.Id, t.Title, t.Passage, t.Source, t.CreatedAt,
            t.Questions.OrderBy(q => q.Order).ThenBy(q => q.Id).Select(ProjectQuestion).ToList(),
            t.IsSystem);
}
