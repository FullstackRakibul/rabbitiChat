// মানব সম্পদ · নিয়মাবলি — drafting a Rulebook page from the Library (docs/HR_RULEBOOK_PROMPT.md §3.4).
//
// What the model is told, and what is done to what it writes. The model drafts
// from the excerpts only; the server then makes the draft keep the promises the
// template makes, whatever the model did:
//
//   · every template heading, once, in order — a heading the model left empty
//     says "Not in the library", never a guess;
//   · Under review holds only the ⚠ items — the model's and the excerpts' — that
//     share a status, benefit or rule (a glossary term) with the page's rules,
//     at most five;
//   · Sources lists the excerpts the draft actually cites; the others that were
//     sent are listed under a collapsed "Also consulted". Never the model's
//     recollection of what it read.
//
// A draft is only ever a draft: it is saved as a `generated` version and never
// confirmed here (HrRulebook.SaveGeneratedAsync).

using System.Text;
using System.Text.RegularExpressions;
using RabbitIC.Api.Models;

namespace RabbitIC.Api.Hr;

public static class HrRuleDraft
{
    public const string NotInLibrary = "এই বিষয়ে লাইব্রেরিতে তথ্য নেই · Not in the library";
    public const string NothingUnderReview = "পর্যালোচনাধীন কিছু নেই · Nothing under review";

    /// <summary>§3.4's headings, in order.</summary>
    public static readonly string[] Headings =
    {
        "## উদ্দেশ্য · Purpose",
        "## নিয়ম · Rules",
        "## হিসাব · Calculation",
        "## ব্যতিক্রম · Exceptions",
        "## পর্যালোচনাধীন · Under review",
        "## উৎস · Sources",
    };

    private const int Purpose = 0, Rules = 1, Calculation = 2, Exceptions = 3, UnderReview = 4, Sources = 5;

    /// <summary>
    /// The system message's first part. Static, like Ask's persona, so the system
    /// message (this plus the always items) is the same for every draft and Ollama
    /// can reuse its evaluation of it.
    /// </summary>
    public const string Persona = """
        You are মানব সম্পদ's drafting assistant. HR asks you to draft one page of its Rulebook: plain, human-readable rules on one topic, written the way HR would write them on paper. A person in HR reads, edits and confirms every draft; until then it is only a draft.

        Follow these rules:
        1. Use only the HR library below and the excerpts sent with the request. Never use general knowledge. Never invent a rule, a date, an amount, a unit or a condition. Take the rules from the rules document first; use procedure code only for what the rules document does not say.
        2. Copy each rule's numbers, conditions and wording exactly as the source states them. A rule the source gives for another status, employee type or topic does not apply to this page: when the page is about Retrenchment, the Resignation row of a table is not a Retrenchment rule. Leave such rules out.
        3. Use exactly this template: these headings, in this order, and no others.
        # <title>
        **বিভাগ / Category:** <category>   **প্রযোজ্য / Applies to:** <the employee types, categories and statuses the rules apply to>

        ## উদ্দেশ্য · Purpose
        ## নিয়ম · Rules
        ## হিসাব · Calculation
        ## ব্যতিক্রম · Exceptions
        ## পর্যালোচনাধীন · Under review
        ## উৎস · Sources
        4. Under Rules: numbered, one rule per line, each followed by its source in square brackets as the excerpt is labelled, such as [ProcessFinalSettelment.sql §Service Benefit L686-711], or [HR_SETTLEMENT_RULES §5] for a section of a library document.
        5. Under Calculation: the formula in words, then a worked example. Write every amount you work out as a formula in double brackets that starts with =, such as [[= (12000/30)*15*7 ]]; the system works it out. Inside the brackets use only numbers, + - * /, parentheses and round(x, n): never a word or a name. Never write the result yourself.
        6. Under a heading the library does not cover, write exactly: এই বিষয়ে লাইব্রেরিতে তথ্য নেই · Not in the library
        7. Under review: the lines marked ⚠ in the excerpts, still marked ⚠, and the points the rules document lists under HR review, but only those about this page's own statuses, benefits and rules — at most five. Never mark anything else ⚠.
        8. Under Sources, list the labels of the excerpts you used, one per line.
        9. Write every sentence, the Purpose included, in the language the request asks for. Keep the headings exactly as in the template.
        """;

    /// <summary>The system message: the drafting persona, then each always item whole.</summary>
    public static string System(IEnumerable<ContextSource> alwaysItems)
    {
        var sb = new StringBuilder(Persona);
        var any = false;
        foreach (var item in alwaysItems)
        {
            if (!any) { sb.Append("\n\n# HR library (always included)\n"); any = true; }
            sb.Append("\n## ").Append(item.Name).Append(" (v").Append(item.Version).Append(")\n\n").Append(item.Content.Trim()).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>The excerpts block: each labelled by its SourceRef alone, so the model cites it as labelled.</summary>
    public static string Excerpts(IReadOnlyList<HrExcerpt> excerpts)
    {
        if (excerpts.Count == 0) return $"{HrPrompt.ContextTitle}\n\n(No excerpt in the library matched this topic.)";
        var sb = new StringBuilder("Excerpts from the HR library for this page\n\n");
        foreach (var e in excerpts) sb.Append('[').Append(e.SourceRef).Append("]\n").Append(e.Text.Trim()).Append("\n\n");
        return sb.ToString().TrimEnd();
    }

    /// <summary>The request: the excerpts, then what to draft.</summary>
    public static string Request(IReadOnlyList<HrExcerpt> excerpts, string title, HrRuleCategory category, string language, string? instruction)
    {
        var sb = new StringBuilder(Excerpts(excerpts)).Append("\n\n");
        sb.Append("Draft the Rulebook page \"").Append(title).Append("\".\n");
        sb.Append("Category: ").Append(category.Bn).Append(" · ").Append(category.En).Append('\n');
        sb.Append(language == "bn"
            ? "Language: write the page in Bengali (বাংলা); keep the headings exactly as in the template.\n"
            : "Language: write the page in English; keep the headings exactly as in the template.\n");
        if (!string.IsNullOrWhiteSpace(instruction)) sb.Append("HR's instruction: ").Append(instruction.Trim()).Append('\n');
        return sb.ToString().TrimEnd();
    }

    /// <summary>The words a draft is searched for in the Library: the title, HR's instruction and English keywords.</summary>
    public static string Query(string title, string? instruction, string? keywords = null) =>
        string.Join(". ", new[] { title, instruction, keywords }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()));

    // ───────────────────────── keeping the template's promises ─────────────────────────

    private static readonly Regex[] HeadingWords =
    {
        new(@"উদ্দেশ্য|\bpurpose\b", RegexOptions.IgnoreCase),
        new(@"নিয়ম|\brules?\b", RegexOptions.IgnoreCase),
        new(@"হিসাব|\bcalculation", RegexOptions.IgnoreCase),
        new(@"ব্যতিক্রম|\bexception", RegexOptions.IgnoreCase),
        new(@"পর্যালোচনা|\breview\b", RegexOptions.IgnoreCase),
        new(@"উৎস|\bsources?\b", RegexOptions.IgnoreCase),
    };

    /// <summary>Which template heading a "## …" line is; the order of the tests keeps "Under review" from reading as Rules.</summary>
    private static int? HeadingOf(string line)
    {
        var text = line.TrimStart('#').Trim();
        foreach (var i in new[] { UnderReview, Purpose, Calculation, Exceptions, Sources, Rules })
            if (HeadingWords[i].IsMatch(text)) return i;
        return null;
    }

    /// <summary>
    /// The glossary sections whose terms are statuses, benefits and rules — what
    /// makes a point under review related to a page (Hr:ReviewTermSections). Salary
    /// and people terms ("basic", "Management") would make every point related.
    /// </summary>
    public const string DefaultReviewSections = "Separation,Benefits,Leave and attendance";

    /// <summary>At most this many points under review on a page.</summary>
    public const int MaxUnderReview = 5;

    public const string NoSourceCited = "কোনো উৎস উদ্ধৃত হয়নি · No source cited";
    public const string AlsoConsulted = "আরও দেখা হয়েছে · Also consulted";

    /// <summary>
    /// The page as it is saved: the title, the category line, and every template
    /// heading in order with what the model wrote under it — "Not in the library"
    /// where it wrote nothing; under Under review only the related ⚠ items; under
    /// Sources what the draft cites, and the rest as "Also consulted".
    /// </summary>
    /// <param name="reviewSections">The glossary sections whose terms make a doubt related (statuses, benefits, rules).</param>
    public static string Shape(string modelText, string title, HrRuleCategory category,
        IReadOnlyList<HrExcerpt> excerpts, IEnumerable<ContextSource> alwaysItems,
        HrGlossary? glossary = null, IReadOnlySet<string>? reviewSections = null)
    {
        glossary ??= HrGlossary.Empty;
        var always = alwaysItems.ToList();
        var lines = Unfence(modelText.Replace("\r\n", "\n").Replace('\r', '\n')).Split('\n');
        var parts = Enumerable.Range(0, Headings.Length).Select(_ => new StringBuilder()).ToArray();
        string? categoryLine = null;
        int? current = null;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (Regex.IsMatch(line, @"^\s{0,3}#\s+\S")) continue;   // the model's title: the page's own is used
            if (Regex.IsMatch(line, @"^\s{0,3}##\s+\S"))
            {
                if (HeadingOf(line) is int h) { current = h; continue; }
                // A heading of the model's own: kept, one level down, inside the section it is in.
                parts[current ?? Purpose].Append("###").Append(line.TrimStart().TrimStart('#')).Append('\n');
                continue;
            }
            if (current is null)
            {
                if (categoryLine is null && Regex.IsMatch(line, "বিভাগ|Category", RegexOptions.IgnoreCase)) categoryLine = line.Trim();
                else if (line.Trim().Length > 0) parts[Purpose].Append(line).Append('\n');
                continue;
            }
            parts[current.Value].Append(line).Append('\n');
        }

        var body = parts.Select(p => p.ToString().Trim()).ToArray();

        body[UnderReview] = Related(body[UnderReview], excerpts, $"{title}\n{body[Rules]}", glossary, reviewSections);

        // Sources: what the draft cites, then the rest of what was sent, collapsed.
        var cited = Labels(string.Join("\n", body.Take(Sources)));
        var sent = excerpts.Where(e => !string.IsNullOrWhiteSpace(e.SourceRef)).DistinctBy(e => e.SourceRef).ToList();
        var used = sent.Where(e => Cites(cited, e)).Select(e => e.SourceRef!)
            .Concat(always.Where(i => cited.Any(l => l.StartsWith(i.Name, StringComparison.Ordinal))).Select(i => i.Name)).ToList();
        var rest = sent.Select(e => e.SourceRef!).Concat(always.Select(i => i.Name)).Where(r => !used.Contains(r)).ToList();
        var sources = new StringBuilder(used.Count > 0 ? string.Join("\n", used.Select(r => $"- [{r}]")) : NoSourceCited);
        if (rest.Count > 0)
            sources.Append("\n\n<details>\n<summary>").Append(AlsoConsulted).Append($" ({rest.Count})</summary>\n\n")
                .Append(string.Join("\n", rest.Select(r => $"- [{r}]"))).Append("\n\n</details>");
        body[Sources] = sources.ToString();

        for (var i = 0; i < body.Length; i++)
            if (IsEmpty(body[i])) body[i] = i == UnderReview ? NothingUnderReview : NotInLibrary;

        var sb = new StringBuilder("# ").Append(title.Trim()).Append('\n')
            .Append(categoryLine ?? $"**বিভাগ / Category:** {category.Bn} · {category.En}   **প্রযোজ্য / Applies to:** {NotInLibrary}")
            .Append("\n\n");
        for (var i = 0; i < Headings.Length; i++) sb.Append(Headings[i]).Append('\n').Append(body[i]).Append("\n\n");
        return sb.ToString().TrimEnd() + "\n";
    }

    /// <summary>
    /// Under review, kept to what concerns this page: each point the model wrote
    /// and each ⚠ line of the excerpts is kept when it names a status, benefit or
    /// rule (a glossary term from <paramref name="sections"/>) that the title or the
    /// rules name too; the ones sharing the most come first, at most five. Without
    /// a glossary there is nothing to judge by, and the first five are kept.
    /// </summary>
    private static string Related(string modelReview, IReadOnlyList<HrExcerpt> excerpts, string rules,
        HrGlossary glossary, IReadOnlySet<string>? sections)
    {
        // A table row is not a point: "| 11 Retrenchment | … ⚠ |" reads as noise, and the
        // document lists the same doubt in words under HR review.
        var points = modelReview.Split('\n').Select(l => l.Trim())
            .Where(l => l.Length > 0 && !IsEmpty(l) && !l.Contains(NotInLibrary) && !l.Contains(NothingUnderReview))
            .Concat(excerpts.SelectMany(e => e.Text.Split('\n')).Select(l => l.Trim()).Where(l => l.Contains('⚠')))
            .Where(l => !Plain(l).StartsWith('|'))
            .ToList();
        var unique = new List<string>();
        foreach (var p in points)
            if (unique.All(u => Squash(Plain(u)) != Squash(Plain(p)))) unique.Add(p);

        IEnumerable<string> kept;
        if (glossary.Entries.Count == 0) kept = unique;
        else
        {
            var pageTerms = glossary.TermsIn(rules, sections);
            kept = unique.Select((p, i) => (p, i, shared: glossary.TermsIn(p, sections).Count(pageTerms.Contains)))
                .Where(x => x.shared > 0).OrderByDescending(x => x.shared).ThenBy(x => x.i).Select(x => x.p);
        }
        var list = kept.Take(MaxUnderReview).Select(p => p.StartsWith('-') || p.StartsWith('*') || Regex.IsMatch(p, @"^\d+[.)]") ? p : "- " + p).ToList();
        return list.Count > 0 ? string.Join("\n", list) : NothingUnderReview;
    }

    /// <summary>A point without its list marker.</summary>
    private static string Plain(string point) => Regex.Replace(point, @"^\s*([-*]|\d+[.)])\s*", "");

    /// <summary>The [labels] a text cites.</summary>
    private static List<string> Labels(string text) =>
        Regex.Matches(text, @"\[([^\[\]\n]{2,200})\](?!\()").Select(m => m.Groups[1].Value.Trim()).Distinct().ToList();

    /// <summary>
    /// Whether the draft cites an excerpt: by its whole SourceRef, or by its item
    /// and a section — "[HR_SETTLEMENT_RULES §8.2]" cites the chunk whose section, or
    /// one of whose headings, starts with "8.2" (small sections share a chunk).
    /// </summary>
    private static bool Cites(IEnumerable<string> labels, HrExcerpt e)
    {
        foreach (var label in labels)
        {
            if (label == e.SourceRef) return true;
            if (!label.StartsWith(e.ItemName, StringComparison.Ordinal)) continue;
            var rest = Regex.Replace(label[e.ItemName.Length..].Trim().TrimStart('§').Trim(), @"\s*L\d+-\d+$", "");
            if (rest.Length == 0) return true;
            var section = e.SourceRef is null ? null : HrSplitter.SourceRefPattern.Match(e.SourceRef).Groups["section"].Value;
            if (!string.IsNullOrEmpty(section) && section.StartsWith(rest, StringComparison.OrdinalIgnoreCase)) return true;
            var heading = new Regex(@"^\s{0,3}#{1,6}\s+" + Regex.Escape(rest) + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.Multiline);
            if (heading.IsMatch(e.Text)) return true;
        }
        return false;
    }

    /// <summary>Nothing said: blank, or only dashes, dots or "none"-like filler.</summary>
    private static bool IsEmpty(string text) => Regex.IsMatch(text, @"^[\s\-–—•*.…]*$");

    /// <summary>A model that wraps its answer in ```markdown fences: the fences go.</summary>
    private static string Unfence(string text)
    {
        var t = text.Trim();
        if (!t.StartsWith("```", StringComparison.Ordinal)) return text;
        var lines = t.Split('\n').ToList();
        lines.RemoveAt(0);
        if (lines.Count > 0 && lines[^1].Trim().StartsWith("```", StringComparison.Ordinal)) lines.RemoveAt(lines.Count - 1);
        return string.Join('\n', lines);
    }

    private static string Squash(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    // ───────────────────────── the number check ─────────────────────────

    /// <summary>
    /// The amounts (100 or more) the draft's sources hold: the excerpts as sent
    /// (their labels included), the always items, the title and HR's instruction.
    /// Kept with the generated version, so the check before Confirm needs nothing else.
    /// </summary>
    public static List<decimal> KnownAmounts(IEnumerable<string> sources) =>
        sources.SelectMany(HrNumberCheck.Numbers).Where(n => n >= HrNumberCheck.Threshold).Distinct().OrderBy(n => n).ToList();

    /// <summary>
    /// The amounts in a page that no source accounts for, as Ask's number check
    /// counts them: a formula's result counts as worked out, and the numbers
    /// written inside a formula as the inputs its example states. Also the
    /// formulas that could not be worked out.
    /// </summary>
    public static (IReadOnlyList<decimal> Unverified, IReadOnlyList<string> FailedFormulas) Check(string body, IEnumerable<decimal> known)
    {
        var stream = new HrFormulaStream();
        var shown = new StringBuilder();
        foreach (var piece in stream.Push(body)) shown.Append(piece.Text);
        foreach (var piece in stream.Finish()) shown.Append(piece.Text);
        var worked = stream.Formulas.Where(f => f.Ok).Select(f => f.Value!.Value)
            .Concat(stream.Formulas.SelectMany(f => HrFormulas.Operands(f.Expression)));
        // The page as read, with each result in place, and without its Sources list (line numbers, not amounts).
        var text = shown.ToString();
        var at = text.IndexOf(Headings[Sources], StringComparison.Ordinal);
        if (at >= 0) text = text[..at];
        var unverified = HrNumberCheck.Unverified(text, Array.Empty<string>(), known.Concat(worked));
        return (unverified, stream.Formulas.Where(f => !f.Ok).Select(f => f.Expression).ToList());
    }
}
