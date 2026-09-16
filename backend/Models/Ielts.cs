// IELTS — Phase 0 (the mistake spine) and Phase 1 (the Reading engine).
//
// Two ideas hold this module together:
//
//   1. The Mistake table is the point. A practice test you score and forget
//      teaches nothing; the value is in what keeps going wrong. So every wrong
//      answer feeds one owner-scoped table with enough metadata (type, skill,
//      topic) to answer "what am I actually bad at", and repeated instances bump
//      a counter rather than spawning near-duplicate rows.
//
//   2. Reading scoring is deterministic and stays that way. Band estimates come
//      from a published raw-score table, not a judgement — so the number is
//      reproducible, explainable, and honestly labelled as an estimate.

namespace RabbitIC.Api.Models;

/// <summary>
/// Which skill a mistake came from. Only Reading exists in this build; the
/// column is a string so adding Listening later is content, not a migration.
/// </summary>
public static class IeltsModules
{
    public const string Reading = "reading";
    public const string Listening = "listening";
    public const string Writing = "writing";
}

/// <summary>
/// The question formats the Reading engine can score without a human.
///
/// Every one of these is a string comparison against an answer key, which is
/// what makes "no AI in scoring" a property of the design rather than a promise.
/// A format that needs judgement (essay marking) is deliberately absent.
/// </summary>
public static class ReadingQuestionTypes
{
    public const string TrueFalseNotGiven = "true_false_not_given";
    public const string YesNoNotGiven = "yes_no_not_given";
    public const string MultipleChoice = "multiple_choice";
    public const string MatchingHeadings = "matching_headings";
    public const string SentenceCompletion = "sentence_completion";
    public const string ShortAnswer = "short_answer";

    public static readonly string[] All =
    {
        TrueFalseNotGiven, YesNoNotGiven, MultipleChoice,
        MatchingHeadings, SentenceCompletion, ShortAnswer,
    };

    /// <summary>Unknown types are rejected rather than stored, so scoring never meets a format it cannot handle.</summary>
    public static bool IsKnown(string? type) =>
        All.Any(t => string.Equals(t, type?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string? Canonical(string? type) =>
        All.FirstOrDefault(t => string.Equals(t, type?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// One recorded error, and the whole reason this module exists.
///
/// A row is keyed conceptually by (Module, QuestionType, Skill, Topic) rather
/// than by the question: getting the same *kind* of question wrong twice is one
/// weakness seen twice, not two weaknesses. <see cref="TimesSeen"/> carries that
/// repetition, which is what makes the weakness view meaningful.
/// </summary>
public class Mistake
{
    public int Id { get; set; }

    /// <summary>Owner — every query is scoped to this, and a foreign id reads as absent (404).</summary>
    public int OwnerUserId { get; set; }

    /// <summary>See <see cref="IeltsModules"/>.</summary>
    public string Module { get; set; } = IeltsModules.Reading;

    /// <summary>What produced this row, e.g. "reading_question".</summary>
    public string SourceType { get; set; } = "reading_question";

    /// <summary>Id within <see cref="SourceType"/> — the question that was missed.</summary>
    public int SourceId { get; set; }

    public string QuestionType { get; set; } = "";

    /// <summary>What the question exercises, e.g. "scanning", "inference". Author-supplied.</summary>
    public string? Skill { get; set; }

    /// <summary>Free text, e.g. "environment". Groups mistakes across tests by subject matter.</summary>
    public string? Topic { get; set; }

    public string YourAnswer { get; set; } = "";
    public string CorrectAnswer { get; set; } = "";

    /// <summary>
    /// Why it went wrong, e.g. "inference_error", "keyword_trap". Null until the
    /// user tags it: the system records *that* something was missed, but only a
    /// person can say why, and guessing would make the weakness view fiction.
    /// </summary>
    public string? MistakeType { get; set; }

    /// <summary>1..5, default 3. Reserved for user weighting.</summary>
    public int Severity { get; set; } = 3;

    public DateTime FirstSeenAt { get; set; } = DateTime.Now;
    public DateTime LastSeenAt { get; set; } = DateTime.Now;

    public int TimesSeen { get; set; } = 1;
    public int TimesResolved { get; set; }

    /// <summary>When this should resurface. Null once resolved.</summary>
    public DateTime? NextReviewAt { get; set; }

    public bool Resolved { get; set; }
}

/// <summary>
/// A Reading passage plus its questions.
///
/// Two kinds of row share this table. A user-authored test is owned by exactly
/// one person and is invisible to everyone else. A seeded demo test is shared:
/// <see cref="IsSystem"/> is true, it appears in every library, and only the
/// seeder may write it.
/// </summary>
public class ReadingTest
{
    public int Id { get; set; }

    /// <summary>
    /// Owner of a user-authored test. For system content this is
    /// <see cref="SystemOwnerId"/> — a sentinel rather than a real account.
    ///
    /// Kept non-nullable deliberately: SQLite cannot relax a NOT NULL column
    /// without rebuilding the table and copying every row, and a sentinel no
    /// user can ever hold achieves the same thing with a plain ADD COLUMN.
    /// </summary>
    public int OwnerUserId { get; set; }

    /// <summary>
    /// Reserved owner id for seeded content. Unreachable by any real account:
    /// SQLite AUTOINCREMENT starts at 1, so no user is ever id 0.
    /// </summary>
    public const int SystemOwnerId = 0;

    /// <summary>True for shared demo content. Read-only to every user, including admins.</summary>
    public bool IsSystem { get; set; }

    /// <summary>
    /// Stable identity of a seeded test, e.g. "reading.urban-farming". Null for
    /// user-authored tests. Unique when present, which is what lets re-seeding
    /// update a test in place instead of inserting a second copy.
    /// </summary>
    public string? SeedKey { get; set; }

    public string Title { get; set; } = "";

    /// <summary>The passage itself. Long, multi-line; blank lines are significant and preserved.</summary>
    public string Passage { get; set; } = "";

    /// <summary>Free text provenance, e.g. "Cambridge 18 Test 2" or "self".</summary>
    public string Source { get; set; } = "self";

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public List<ReadingQuestion> Questions { get; set; } = new();
}

/// <summary>One question against a passage.</summary>
public class ReadingQuestion
{
    public int Id { get; set; }
    public int ReadingTestId { get; set; }

    /// <summary>Display position. Normalised to 1,2,3,… so the paper reads in order.</summary>
    public int Order { get; set; }

    /// <summary>One of <see cref="ReadingQuestionTypes"/>.</summary>
    public string QuestionType { get; set; } = "";

    public string Prompt { get; set; } = "";

    /// <summary>JSON array of choices for MCQ/matching; null for the free-text formats.</summary>
    public string? OptionsJson { get; set; }

    /// <summary>
    /// The answer key. For completion and short-answer it may list acceptable
    /// variants pipe-separated ("carbon dioxide|CO2"), because a correct answer
    /// with different wording is still correct.
    /// </summary>
    public string CorrectAnswer { get; set; } = "";

    public string? Skill { get; set; }
    public string? Topic { get; set; }
    public string? Difficulty { get; set; }

    public ReadingTest? Test { get; set; }
}

/// <summary>One sitting of one test.</summary>
public class ReadingAttempt
{
    public int Id { get; set; }
    public int OwnerUserId { get; set; }
    public int ReadingTestId { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.Now;

    /// <summary>Null while the attempt is still open.</summary>
    public DateTime? SubmittedAt { get; set; }

    public int DurationSeconds { get; set; }

    public int RawScore { get; set; }
    public int TotalQuestions { get; set; }

    /// <summary>
    /// From the raw-score conversion table, never a judgement. Null until submitted.
    /// </summary>
    public double? BandEstimate { get; set; }

    public List<ReadingAttemptAnswer> Answers { get; set; } = new();
}

/// <summary>One answer within an attempt, with the verdict frozen at submit time.</summary>
public class ReadingAttemptAnswer
{
    public int Id { get; set; }
    public int ReadingAttemptId { get; set; }
    public int ReadingQuestionId { get; set; }

    public string GivenAnswer { get; set; } = "";

    /// <summary>
    /// Stored rather than recomputed on read: editing a test's answer key later
    /// must not silently rewrite the history of an attempt already taken.
    /// </summary>
    public bool IsCorrect { get; set; }

    public ReadingAttempt? Attempt { get; set; }
}

/// <summary>
/// Raw score (out of 40) → Academic Reading band.
///
/// A published conversion table, reproduced as a lookup. It is an estimate and
/// the UI says so: real papers vary slightly, and this cannot know which paper
/// the questions came from. Presenting it as anything firmer would be dishonest.
/// </summary>
public static class ReadingBandTable
{
    /// <summary>Lower bound of each band, highest first. The first threshold met wins.</summary>
    private static readonly (int MinRaw, double Band)[] Table =
    {
        (39, 9.0), (37, 8.5), (35, 8.0), (33, 7.5), (30, 7.0),
        (27, 6.5), (23, 6.0), (19, 5.5), (15, 5.0), (13, 4.5),
        (10, 4.0), (8, 3.5), (6, 3.0), (4, 2.5),
    };

    /// <summary>
    /// The band for a raw score. Tests shorter than 40 questions are scaled to
    /// the 40-question table first, so a 10-question practice set still lands on
    /// a comparable number instead of reading as a near-zero band.
    /// </summary>
    public static double Estimate(int rawScore, int totalQuestions)
    {
        if (totalQuestions <= 0) return 0;

        var scaled = (int)Math.Round(rawScore / (double)totalQuestions * 40, MidpointRounding.AwayFromZero);
        scaled = Math.Clamp(scaled, 0, 40);

        foreach (var (minRaw, band) in Table)
        {
            if (scaled >= minRaw) return band;
        }

        return 2.0;
    }
}
