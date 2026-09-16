using RabbitIC.Api.Models;

namespace RabbitIC.Api.Services;

/// <summary>
/// The metadata a wrong answer carries into the mistake log. Passed as one
/// object rather than six loose strings so a caller cannot silently transpose
/// Skill and Topic.
/// </summary>
public record MistakeSource(
    string Module,
    string SourceType,
    int SourceId,
    string QuestionType,
    string? Skill,
    string? Topic);

/// <summary>One mistake as the API and the weakness view see it.</summary>
public record MistakeDto(
    int Id,
    string Module,
    string SourceType,
    int SourceId,
    string QuestionType,
    string? Skill,
    string? Topic,
    string YourAnswer,
    string CorrectAnswer,
    string? MistakeType,
    int Severity,
    DateTime FirstSeenAt,
    DateTime LastSeenAt,
    int TimesSeen,
    int TimesResolved,
    DateTime? NextReviewAt,
    bool Resolved,
    bool IsDue);

/// <summary>Counts for one question type, for the weakness bars.</summary>
public record MistakeTypeSummary(string QuestionType, int Mistakes, int TimesSeen, int Unresolved, int Due);

/// <summary>
/// The mistake spine: what keeps going wrong, and when to revisit it.
///
/// Owner-scoped throughout — a foreign id returns null and the endpoint answers
/// 404, never 403, so probing ids reveals nothing about another user's data.
/// </summary>
public interface IMistakeService
{
    /// <summary>
    /// Logs a wrong answer.
    ///
    /// An unresolved row with the same (Module, QuestionType, Skill, Topic) is
    /// bumped rather than duplicated: missing the same kind of question twice is
    /// one weakness seen twice. That is what makes TimesSeen meaningful and keeps
    /// the weakness view from drowning in near-identical rows.
    /// </summary>
    Task<MistakeDto> RecordAsync(
        int userId, MistakeSource source, string yourAnswer, string correctAnswer,
        CancellationToken ct = default);

    /// <summary>Due first, then most recently seen. Filters are all optional.</summary>
    Task<IReadOnlyList<MistakeDto>> ListAsync(
        int userId, string? module = null, string? questionType = null, bool dueOnly = false,
        CancellationToken ct = default);

    /// <summary>
    /// Records a review. Pushes NextReviewAt out on a widening schedule
    /// (2d → 5d → 11d) and, when <paramref name="resolved"/> is true, closes the
    /// row so it stops surfacing. Null when the id is absent or foreign.
    /// </summary>
    Task<MistakeDto?> MarkReviewedAsync(
        int userId, int id, bool? resolved = null, CancellationToken ct = default);

    /// <summary>Lets the user say *why* it went wrong. Null when absent or foreign.</summary>
    Task<MistakeDto?> TagAsync(
        int userId, int id, string? mistakeType, int? severity, CancellationToken ct = default);

    /// <summary>Per-question-type counts for the weakness view.</summary>
    Task<IReadOnlyList<MistakeTypeSummary>> SummaryAsync(
        int userId, string? module = null, CancellationToken ct = default);
}
