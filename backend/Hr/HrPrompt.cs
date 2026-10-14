// মানব সম্পদ — what the model is told (docs/HR_MODULE_PROMPT.md §3.3).
//
// Two parts, and the split is what makes the first word fast:
//
//   · The system message: the persona and every active `always` item, in
//     SortOrder. It depends on nothing about the question, the user or the time,
//     so it is byte-identical for every question and Ollama reuses its cached
//     evaluation of it instead of reading ~4,000 tokens again.
//   · The last user message: the excerpts retrieved for this question, titled
//     "HR context for this question", then the question. Excerpts never go into
//     the system message — that would change it every time.

using RabbitIC.Api.Models;

namespace RabbitIC.Api.Hr;

public sealed record HrExcerpt(int ChunkId, int ItemId, string ItemName, int Version, string? SourceRef, int? LineFrom, int? LineTo, string Text, int Tokens, double Score);

public static class HrPrompt
{
    public const string ContextTitle = "HR context for this question";

    public const string Persona = """
        You are মানব সম্পদ, the company's HR assistant. HR staff ask you about final settlement, earned leave and the rules around them.

        Follow these rules in every answer:
        1. Answer only from the HR context: the library below and the excerpts sent with the question. Never answer from general knowledge or guess company policy.
        2. After each statement, cite its source in square brackets as the context labels it: [HR_SETTLEMENT_RULES §5] for a section of a library document, or an excerpt's own label, such as [ProcessFinalSettelment.sql §Service Benefit L686-711].
        3. When the rules document and a procedure disagree, give the rules document's answer and say that the code does something else.
        4. When the context does not cover the question, say so in one line and name the item HR would need to add. Do not answer it anyway.
        5. For one employee's amount, use only figures the user gave. If one is missing (basic, service length, status, employee type, separation date), ask for it instead of assuming.
        6. Do every calculation with the calculate tool and show the formula in the answer.
        7. Where §11 of the rules document lists a doubt about a rule you use, add: "HR is reviewing this rule".
        8. Answer in the user's language, Bengali or English. Write amounts in digits with ৳, for example ৳42,000.
        """;

    /// <summary>The system message: persona, then each always item whole. Nothing per-question goes in here.</summary>
    public static string System(IEnumerable<ContextSource> alwaysItems)
    {
        var sb = new System.Text.StringBuilder(Persona);
        var any = false;
        foreach (var item in alwaysItems)
        {
            if (!any) { sb.Append("\n\n# HR library (always included)\n"); any = true; }
            sb.Append("\n## ").Append(item.Name).Append(" (v").Append(item.Version);
            if (!string.IsNullOrWhiteSpace(item.ApprovedBy)) sb.Append(", approved by ").Append(item.ApprovedBy);
            sb.Append(")\n\n").Append(item.Content.Trim()).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>The excerpts block alone, as it appears in the last user message.</summary>
    public static string Excerpts(IReadOnlyList<HrExcerpt> excerpts)
    {
        if (excerpts.Count == 0) return $"{ContextTitle}\n\n(No excerpt in the library matched this question.)";
        var sb = new System.Text.StringBuilder(ContextTitle).Append("\n\n");
        foreach (var e in excerpts)
            sb.Append('[').Append(e.SourceRef).Append("] (").Append(e.ItemName).Append(", v").Append(e.Version).Append(")\n")
              .Append(e.Text.Trim()).Append("\n\n");
        return sb.ToString().TrimEnd();
    }

    /// <summary>The last user message: the excerpts, then the question.</summary>
    public static string Question(IReadOnlyList<HrExcerpt> excerpts, string question) =>
        $"{Excerpts(excerpts)}\n\nQuestion: {question.Trim()}";
}
