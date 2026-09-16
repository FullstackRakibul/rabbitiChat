using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.Data;
using RabbitIC.Api.Models;

namespace RabbitIC.Api.Ielts;

/// <summary>
/// One seeded Reading test, defined in code rather than loaded from a file.
///
/// In code on purpose: the seed is content the application is expected to ship
/// with, so it should travel in the assembly and be reviewable in a diff. A file
/// dependency would add a deployment step and a failure mode (missing/edited
/// file) for no benefit at this size.
/// </summary>
public record SeededTest(
    string SeedKey,
    string Title,
    string Source,
    string Passage,
    IReadOnlyList<SeededQuestion> Questions);

/// <summary>One question within a seeded test. Mirrors <see cref="ReadingQuestion"/>.</summary>
public record SeededQuestion(
    int Order,
    string QuestionType,
    string Prompt,
    string? OptionsJson,
    string CorrectAnswer,
    string? Skill = null,
    string? Topic = null,
    string? Difficulty = null);

/// <summary>
/// Loads the shared demo Reading tests at startup.
///
/// Idempotent by <see cref="SeededTest.SeedKey"/>: a test is inserted once and
/// thereafter updated in place. That is the whole point of the key — correcting
/// a passage or an answer key in code and restarting fixes the stored test,
/// rather than leaving the old copy beside a new one.
///
/// Seeded tests are system-owned and read-only. <see cref="ReadingService"/>
/// refuses every write against them; this seeder is the only writer.
/// </summary>
public static class IeltsReadingSeeder
{
    public static void Run(AppDbContext db, ILogger logger)
    {
        // One transaction for the whole seed: a half-applied catalogue (a test
        // updated but its questions not replaced) would be worse than no change.
        using var tx = db.Database.BeginTransaction();

        var inserted = 0;
        var updated = 0;

        foreach (var seed in SeededTests)
        {
            var existing = db.ReadingTests
                .Include(t => t.Questions)
                .FirstOrDefault(t => t.SeedKey == seed.SeedKey);

            if (existing is null)
            {
                db.ReadingTests.Add(new ReadingTest
                {
                    OwnerUserId = ReadingTest.SystemOwnerId,
                    IsSystem = true,
                    SeedKey = seed.SeedKey,
                    Title = seed.Title,
                    Source = seed.Source,
                    Passage = seed.Passage,
                    Questions = seed.Questions.Select(ToEntity).ToList(),
                });
                inserted++;
                continue;
            }

            existing.Title = seed.Title;
            existing.Source = seed.Source;
            existing.Passage = seed.Passage;

            // Kept true even on an existing row: a test that somehow lost the flag
            // would otherwise become editable, which is exactly what must not happen.
            existing.IsSystem = true;
            existing.OwnerUserId = ReadingTest.SystemOwnerId;

            // Questions are replaced wholesale rather than diffed. Matching them
            // up would need a stable per-question identity the seed does not
            // have, and a wrong match would silently corrupt an answer key.
            // Replacing is unambiguous, and attempts keep their own frozen
            // verdicts, so past results are unaffected.
            db.ReadingQuestions.RemoveRange(existing.Questions);
            existing.Questions = seed.Questions.Select(ToEntity).ToList();
            updated++;
        }

        db.SaveChanges();
        tx.Commit();

        // Counts only — the passages and answer keys never reach the log.
        logger.LogInformation(
            "IELTS reading seed: {Inserted} seeded, {Updated} updated.", inserted, updated);
    }

    private static ReadingQuestion ToEntity(SeededQuestion q) => new()
    {
        Order = q.Order,
        QuestionType = q.QuestionType,
        Prompt = q.Prompt,
        OptionsJson = q.OptionsJson,
        CorrectAnswer = q.CorrectAnswer,
        Skill = q.Skill,
        Topic = q.Topic,
        Difficulty = q.Difficulty,
    };

    // =====================================================================
    // The seed catalogue.
    //
    // Answer keys use the scorer's existing conventions: comparison is
    // case-insensitive and space-collapsed, and a pipe lists acceptable
    // variants ("carbon dioxide|CO2").
    // =====================================================================

    private static readonly SeededTest UrbanFarming = new(
        SeedKey: "reading.urban-farming",
        Title: "Urban Farming",
        Source: "Mostisko demo",
        Passage:
            "Urban farming has grown rapidly in the last decade. Rooftop gardens now supply "
            + "a measurable share of fresh produce in several cities. Critics argue the yields "
            + "remain small relative to demand, though proponents note the reduction in "
            + "transport emissions. Carbon dioxide savings are frequently cited.",
        Questions: new SeededQuestion[]
        {
            new(1, ReadingQuestionTypes.TrueFalseNotGiven,
                "Rooftop gardens supply some produce.",
                null, "TRUE", "scanning", "environment", "easy"),

            new(2, ReadingQuestionTypes.TrueFalseNotGiven,
                "Urban farming has declined.",
                null, "FALSE", "scanning", "environment", "easy"),

            new(3, ReadingQuestionTypes.MultipleChoice,
                "What do proponents note?",
                """["Lower transport emissions","Higher yields","Cheaper land"]""",
                "Lower transport emissions", "inference", "environment", "medium"),

            new(4, ReadingQuestionTypes.SentenceCompletion,
                "Savings in ______ are frequently cited.",
                null, "carbon dioxide|CO2", "detail", "environment", "medium"),

            new(5, ReadingQuestionTypes.ShortAnswer,
                "What kind of gardens are mentioned?",
                null, "rooftop", "scanning", "environment", "easy"),
        });

    private static readonly SeededTest PrintingPress = new(
        SeedKey: "reading.printing-press",
        Title: "The Printing Press",
        Source: "Mostisko demo",
        Passage:
            "Before movable type reached Europe, books were copied by hand, a process that "
            + "could take a scribe more than a year for a single volume. Johannes Gutenberg's "
            + "press, developed around 1440, reduced that labour dramatically. Within fifty "
            + "years printing houses had opened in most major European cities, and the price "
            + "of a book fell sharply.\n\n"
            + "The consequences reached well beyond cost. Standardised texts meant that two "
            + "readers in different countries could, for the first time, be confident they "
            + "were reading identical words. Historians often link this uniformity to the "
            + "spread of scientific ideas, since an experiment described in one city could "
            + "be repeated accurately in another. Not everyone welcomed the change: some "
            + "scholars complained that cheap books encouraged careless reading.",
        Questions: new SeededQuestion[]
        {
            new(1, ReadingQuestionTypes.TrueFalseNotGiven,
                "Hand-copying a single book could take over a year.",
                null, "TRUE", "scanning", "history", "easy"),

            new(2, ReadingQuestionTypes.TrueFalseNotGiven,
                "Gutenberg's press was developed in the sixteenth century.",
                null, "FALSE", "detail", "history", "easy"),

            new(3, ReadingQuestionTypes.TrueFalseNotGiven,
                "Gutenberg earned a large fortune from his invention.",
                null, "NOT GIVEN", "inference", "history", "medium"),

            new(4, ReadingQuestionTypes.YesNoNotGiven,
                "The writer suggests standardised texts helped science spread.",
                null, "YES", "inference", "history", "medium"),

            new(5, ReadingQuestionTypes.MultipleChoice,
                "According to the passage, what did some scholars complain about?",
                """["Careless reading","Poor ink quality","Slow printing speeds","Rising paper costs"]""",
                "Careless reading", "detail", "history", "medium"),

            new(6, ReadingQuestionTypes.SentenceCompletion,
                "Within fifty years, printing houses had opened in most major ______ cities.",
                null, "European|european", "scanning", "history", "easy"),

            new(7, ReadingQuestionTypes.ShortAnswer,
                "In roughly which year was the press developed?",
                null, "1440", "scanning", "history", "easy"),
        });

    /// <summary>
    /// Every test the application ships with.
    /// </summary>
    private static readonly SeededTest[] SeededTests =
    {
        UrbanFarming,
        PrintingPress,

        // add more seeded tests below
    };
}
