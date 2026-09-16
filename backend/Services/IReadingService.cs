using RabbitIC.Api.Models;

namespace RabbitIC.Api.Services;

// ---- Authoring ----

/// <summary>Create/update payload for a test. Null members mean "leave unchanged" on update.</summary>
public record ReadingTestInput(string? Title, string? Passage, string? Source);

/// <summary>Create/update payload for a question.</summary>
public record ReadingQuestionInput(
    string? QuestionType,
    string? Prompt,
    IReadOnlyList<string>? Options,
    string? CorrectAnswer,
    string? Skill,
    string? Topic,
    string? Difficulty,
    int? Order);

// ---- Read models ----

/// <summary>A test in the library list — no passage, no questions, just enough to choose one.</summary>
public record ReadingTestSummary(
    int Id, string Title, string Source, int QuestionCount, DateTime CreatedAt,
    bool IsSystem);

/// <summary>
/// A question as the AUTHOR sees it: answer key included.
/// Never returned from the take-test endpoint — see <see cref="ReadingQuestionForAttempt"/>.
/// </summary>
public record ReadingQuestionDto(
    int Id, int Order, string QuestionType, string Prompt,
    IReadOnlyList<string>? Options, string CorrectAnswer,
    string? Skill, string? Topic, string? Difficulty);

/// <summary>A test with its questions, for the authoring view.</summary>
public record ReadingTestDto(
    int Id, string Title, string Passage, string Source, DateTime CreatedAt,
    IReadOnlyList<ReadingQuestionDto> Questions, bool IsSystem);

/// <summary>
/// A question as the CANDIDATE sees it: deliberately no CorrectAnswer field.
///
/// The omission is structural rather than a matter of remembering to strip it —
/// there is no property here that could carry the key, so a sitting cannot leak
/// the answers even by accident.
/// </summary>
public record ReadingQuestionForAttempt(
    int Id, int Order, string QuestionType, string Prompt, IReadOnlyList<string>? Options);

/// <summary>What starting an attempt hands back.</summary>
public record ReadingAttemptStart(
    int AttemptId, int TestId, string Title, string Passage, DateTime StartedAt,
    IReadOnlyList<ReadingQuestionForAttempt> Questions);

/// <summary>One submitted answer.</summary>
public record ReadingAnswerInput(int QuestionId, string? Given);

/// <summary>Per-question verdict in a result or review.</summary>
public record ReadingAnswerResult(
    int QuestionId, int Order, string QuestionType, string Prompt,
    string GivenAnswer, string CorrectAnswer, bool IsCorrect,
    string? Skill, string? Topic);

/// <summary>
/// The outcome of a sitting.
///
/// <paramref name="BandBasis"/> spells out where the number came from, so the UI
/// can label it honestly rather than presenting a lookup as a verdict.
/// </summary>
public record ReadingAttemptResult(
    int AttemptId, int TestId, string Title,
    int RawScore, int TotalQuestions, double BandEstimate, string BandBasis,
    int DurationSeconds, DateTime? SubmittedAt,
    int MistakesRecorded,
    IReadOnlyList<ReadingAnswerResult> Answers);

/// <summary>One row in the attempt history.</summary>
public record ReadingAttemptSummary(
    int Id, int TestId, string Title, int RawScore, int TotalQuestions,
    double? BandEstimate, DateTime StartedAt, DateTime? SubmittedAt, int DurationSeconds);

/// <summary>
/// Why a write was refused.
///
/// The existing contract maps "no" to 404 so that absent and foreign look alike —
/// which is right for user content, but wrong for a system test: it is plainly
/// visible in the library, so claiming it does not exist would be a lie. These
/// two cases are therefore distinguished rather than both collapsing to null.
/// </summary>
public enum ReadingWriteOutcome
{
    Ok,

    /// <summary>Absent, or owned by someone else. The endpoint answers 404.</summary>
    NotFound,

    /// <summary>Seeded demo content. Visible to all, writable by none. Answers 409.</summary>
    ReadOnly,
}

/// <summary>A write result: the value when it succeeded, or why it did not.</summary>
public record ReadingWriteResult<T>(ReadingWriteOutcome Outcome, T? Value)
{
    public static ReadingWriteResult<T> Ok(T value) => new(ReadingWriteOutcome.Ok, value);
    public static ReadingWriteResult<T> NotFound() => new(ReadingWriteOutcome.NotFound, default);
    public static ReadingWriteResult<T> ReadOnly() => new(ReadingWriteOutcome.ReadOnly, default);
}

/// <summary>
/// The Reading engine: authoring, sittings, and deterministic scoring.
///
/// Scoring never consults a model. Every supported question type reduces to a
/// normalised string comparison against an answer key, which is what makes a
/// score reproducible and explainable — the same answers always produce the same
/// band, and a disputed mark can be traced to one comparison.
///
/// Owner-scoped throughout: a foreign id returns null and the endpoint answers
/// 404, never 403.
/// </summary>
public interface IReadingService
{
    // ---- Tests ----
    Task<IReadOnlyList<ReadingTestSummary>> ListTestsAsync(int userId, CancellationToken ct = default);
    Task<ReadingTestDto?> GetTestAsync(int userId, int testId, CancellationToken ct = default);
    Task<ReadingTestDto> CreateTestAsync(int userId, ReadingTestInput input, CancellationToken ct = default);
    Task<ReadingWriteResult<ReadingTestDto>> UpdateTestAsync(int userId, int testId, ReadingTestInput input, CancellationToken ct = default);
    Task<ReadingWriteResult<bool>> DeleteTestAsync(int userId, int testId, CancellationToken ct = default);

    // ---- Questions ----

    /// <summary>Null when the test is absent or foreign; throws <see cref="ArgumentException"/> on an unknown type.</summary>
    Task<ReadingWriteResult<ReadingQuestionDto>> AddQuestionAsync(
        int userId, int testId, ReadingQuestionInput input, CancellationToken ct = default);

    Task<ReadingWriteResult<ReadingQuestionDto>> UpdateQuestionAsync(
        int userId, int questionId, ReadingQuestionInput input, CancellationToken ct = default);

    Task<ReadingWriteResult<bool>> DeleteQuestionAsync(int userId, int questionId, CancellationToken ct = default);

    // ---- Attempts ----

    /// <summary>Opens a sitting. The questions come back without their answer keys.</summary>
    Task<ReadingAttemptStart?> StartAttemptAsync(int userId, int testId, CancellationToken ct = default);

    /// <summary>
    /// Scores a sitting deterministically, stores the per-question verdicts, and
    /// logs every wrong answer to the mistake spine.
    /// </summary>
    Task<ReadingAttemptResult?> SubmitAttemptAsync(
        int userId, int attemptId, IReadOnlyList<ReadingAnswerInput> answers,
        int? durationSeconds = null, CancellationToken ct = default);

    Task<IReadOnlyList<ReadingAttemptSummary>> ListAttemptsAsync(int userId, CancellationToken ct = default);

    /// <summary>Full review: your answer against the key, per question.</summary>
    Task<ReadingAttemptResult?> GetAttemptAsync(int userId, int attemptId, CancellationToken ct = default);
}

/// <summary>
/// Answer comparison for Reading.
///
/// Separated from the service so the rules can be read — and tested — on their
/// own. Every rule here exists because a human marker would accept the variation:
/// trailing spaces, capitalisation and an article are not comprehension errors.
/// </summary>
public static class ReadingAnswerKey
{
    /// <summary>
    /// Case-folded, space-collapsed, punctuation-trimmed. Deliberately conservative:
    /// it forgives typography, never spelling.
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        var collapsed = System.Text.RegularExpressions.Regex
            .Replace(value.Trim(), @"\s+", " ")
            .ToLowerInvariant();

        // Strip only surrounding punctuation — interior hyphens and apostrophes
        // are part of the word ("well-being", "don't").
        return collapsed.Trim('.', ',', ';', ':', '!', '?', '"', '\'', '(', ')');
    }

    /// <summary>
    /// Does the given answer match the key?
    ///
    /// The key may list acceptable variants pipe-separated ("carbon dioxide|CO2"),
    /// because the same correct answer can be written more than one way.
    /// </summary>
    public static bool Matches(string? given, string? correctAnswer)
    {
        var normalizedGiven = Normalize(given);
        if (normalizedGiven.Length == 0) return false;

        foreach (var variant in (correctAnswer ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Normalize(variant) == normalizedGiven) return true;
        }

        return false;
    }
}
