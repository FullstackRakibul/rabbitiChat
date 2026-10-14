// The rules PO Studio shares between the profile API, the extraction path, the
// accuracy board and the dataset export. They live in ONE place deliberately: a
// held-out run that leaked into a worked example or a training file would make
// every A/B result afterwards meaningless, and that mistake is easy to make
// twice if the rule is written twice.

using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.Data;
using RabbitIC.Api.Models;

namespace RabbitIC.Api.Extraction;

public static class PoStudio
{
    /// <summary>At most this many worked examples reach a prompt (§3.3).</summary>
    public const int MaxExamples = 2;

    /// <summary>
    /// The held-out share: one document in five (§3.6, §11 Q4). Keyed on the
    /// DOCUMENT, not the run, so re-extracting the same PO can never move it
    /// between the train and test sides.
    /// </summary>
    public const int HeldOutModulus = 5;

    /// <summary>True when this document's runs belong to the test set.</summary>
    public static bool IsHeldOut(int documentId) => documentId % HeldOutModulus == 0;

    /// <summary>"1,4" → [1, 4]. Tolerates spaces, blanks and duplicates.</summary>
    public static List<int> ParseRunIds(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return new List<int>();
        return csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var i) ? i : 0)
            .Where(i => i > 0)
            .Distinct()
            .ToList();
    }

    public static string FormatRunIds(IEnumerable<int> ids) => string.Join(",", ids);

    /// <summary>
    /// Why a run may not be a worked example, or null when it may. Two rules, and
    /// both matter: an unconfirmed run would teach the model its own mistakes, and
    /// a held-out run would make the A/B comparison score its own training data.
    /// </summary>
    public static async Task<string?> ExampleRefusalAsync(AppDbContext db, int runId, CancellationToken ct)
    {
        var run = await db.ExtractionRuns
            .Where(r => r.Id == runId)
            .Select(r => new { r.Id, r.DocumentId, r.ConfirmedJson })
            .FirstOrDefaultAsync(ct);

        if (run is null)
            return $"Run {runId} does not exist.";
        if (string.IsNullOrWhiteSpace(run.ConfirmedJson))
            return $"Run {runId} has not been confirmed — only a confirmed extraction can be a worked example.";
        if (IsHeldOut(run.DocumentId))
            return $"Run {runId} is on document {run.DocumentId}, which is in the held-out test set "
                 + $"(document id divisible by {HeldOutModulus}). A held-out run can never be a worked "
                 + "example, or the A/B evaluation would be scoring its own examples.";
        return null;
    }

    /// <summary>
    /// Loads a profile's worked examples, in the order the profile lists them, as
    /// the prompt builder wants them. A run that has since lost its confirmation,
    /// or whose text is gone, is skipped rather than failing the extraction.
    /// </summary>
    public static async Task<List<PoWorkedExample>> LoadExamplesAsync(
        AppDbContext db, BuyerProfile? profile, Func<int, Task<string>> documentText, CancellationToken ct)
    {
        var examples = new List<PoWorkedExample>();
        if (profile is null) return examples;

        foreach (var runId in ParseRunIds(profile.ExampleRunIds).Take(MaxExamples))
        {
            var run = await db.ExtractionRuns
                .Where(r => r.Id == runId && r.ConfirmedJson != null)
                .Select(r => new { r.Id, r.DocumentId, r.ConfirmedJson })
                .FirstOrDefaultAsync(ct);
            if (run is null || IsHeldOut(run.DocumentId)) continue;

            var text = await documentText(run.DocumentId);
            if (string.IsNullOrWhiteSpace(text)) continue;

            examples.Add(new PoWorkedExample(run.Id, text, run.ConfirmedJson!));
        }
        return examples;
    }

    /// <summary>The profile's contribution to a prompt, in the builder's pure shape.</summary>
    public static PoPromptProfile ToPromptProfile(BuyerProfile? profile, IReadOnlyList<PoWorkedExample> examples)
        => new(profile?.Name,
               profile?.Rules ?? "",
               profile?.Glossary ?? "",
               profile?.Version ?? 1,
               examples);
}
