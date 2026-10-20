// The HR module's glossary: Bengali searched in English too.
//
// The glossary is an ordinary Library item of the glossary type (the seeded one is
// named "hr-glossary"; at most one is active), which HR edits like any other. Each term is one line,
//
//   <Bengali> | <English> | <other spellings, comma-separated>
//
// under "## " section headings (Separation, Benefits, …). Nothing here knows a
// word of either language: every term comes from that item, so a line HR adds
// is used by the next question.
//
// Two uses:
//   · search — a question or a Rulebook title that holds a Bengali term or one of
//     its other spellings is also searched by the English term (Ask and drafts);
//   · relevance — which statuses, benefits and rules a text names, so a draft's
//     Under review keeps only the doubts that concern its own rules.

using System.Text.RegularExpressions;

namespace RabbitIC.Api.Hr;

/// <param name="Section">The section's English name: "Separation" for "## Separation (…)".</param>
public sealed record HrGlossaryEntry(string Section, string Bengali, string English, IReadOnlyList<string> Others)
{
    public IEnumerable<string> Variants => Others.Prepend(Bengali).Prepend(English).Where(v => v.Length > 0);
}

public sealed class HrGlossary
{
    public static readonly HrGlossary Empty = new(Array.Empty<HrGlossaryEntry>());

    public IReadOnlyList<HrGlossaryEntry> Entries { get; }

    private HrGlossary(IReadOnlyList<HrGlossaryEntry> entries) => Entries = entries;

    /// <summary>
    /// The glossary item's lines. A line counts when it has a "|" between at least
    /// an original and an English term, and no backtick (that is the item's own
    /// description of the format).
    /// </summary>
    public static HrGlossary Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return Empty;
        var entries = new List<HrGlossaryEntry>();
        var section = "";
        foreach (var raw in content.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            var heading = Regex.Match(line, @"^#{2,6}\s+(?<title>.+)$");
            if (heading.Success)
            {
                var title = heading.Groups["title"].Value.Trim();
                var paren = title.IndexOf(" (", StringComparison.Ordinal);
                section = (paren > 0 ? title[..paren] : title).Trim();
                continue;
            }
            if (!line.Contains('|') || line.Contains('`')) continue;
            var cells = line.Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length < 2 || cells[1].Length == 0 || cells[0].Length > 60) continue;
            var others = cells.Length > 2
                ? cells[2].Split(',').Select(o => o.Trim()).Where(o => o.Length > 0).ToList()
                : new List<string>();
            entries.Add(new HrGlossaryEntry(section, cells[0], cells[1], others));
        }
        return new HrGlossary(entries);
    }

    /// <summary>
    /// The English terms to search as well: for every entry whose original or other
    /// spelling the text holds, its English term — unless the text says it already.
    /// </summary>
    public IReadOnlyList<string> EnglishFor(string text)
    {
        var found = new List<string>();
        foreach (var e in Entries)
        {
            if (Mentions(text, e.English)) continue;
            if (e.Others.Prepend(e.Bengali).Any(v => v.Length > 0 && Mentions(text, v)) && !found.Contains(e.English))
                found.Add(e.English);
        }
        return found;
    }

    /// <summary>The English terms of the entries a text names in any spelling, from the given sections only (all when null).</summary>
    public IReadOnlySet<string> TermsIn(string text, IReadOnlySet<string>? sections = null) =>
        Entries.Where(e => sections is null || sections.Contains(e.Section))
            .Where(e => e.Variants.Any(v => Mentions(text, v)))
            .Select(e => e.English)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A Bengali term is found inside a word, so a case suffix does not hide it;
    /// any other term only as a whole word, ignoring case, so "OT" is not found in "TOTAL".
    /// </summary>
    private static bool Mentions(string text, string term)
    {
        if (term.Any(IsBengali)) return text.Contains(term, StringComparison.Ordinal);
        return Regex.IsMatch(text, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(term)}(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase);
    }

    /// <summary>The Bengali block, U+0980 to U+09FF.</summary>
    private static bool IsBengali(char c) => c >= (char)0x0980 && c <= (char)0x09FF;
}
