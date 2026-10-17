// The HR module's citations: which [labels] in a text name a place that was sent.
//
// One rule for Ask answers, Rulebook drafts and the check before Confirm. A label
// names a sent place when it is
//   · the excerpt's whole SourceRef;
//   · its item and a section: "[HR_SETTLEMENT_RULES §8.2]" names the chunk whose
//     section, or one of whose headings, starts with "8.2" (small sections share a
//     chunk, so the heading may be inside it);
//   · an always item and anything after its name (the item was sent whole);
//   · a Rulebook page's own label, "Rulebook: <Title> v<N>".
// Anything else is an unknown source, marked and listed as unverified amounts are.

using System.Text.RegularExpressions;

namespace RabbitIC.Api.Hr;

/// <summary>A place that was sent: an excerpt (with the headings inside it) or, Whole, an always item.</summary>
public sealed record HrCitable(string ItemName, string? SourceRef, IReadOnlyList<string> Headings, string? Cite = null, bool Whole = false)
{
    public static HrCitable Of(HrExcerpt e) => new(e.ItemName, e.SourceRef, HrCitations.HeadingsOf(e.SourceRef, e.Text), e.Cite);
}

public static class HrCitations
{
    private static readonly Regex Label = new(@"\[([^\[\]\n]{2,200})\](?!\()", RegexOptions.Compiled);
    private static readonly Regex MarkdownHeading = new(@"^\s{0,3}#{1,6}\s+(?<title>.+?)\s*#*\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// The [labels] a text cites. Not labels: a formula's inside ("[= 12000*5 ]" of
    /// "[[= … ]]"), and "calculate", the old tool's mark.
    /// </summary>
    public static List<string> Labels(string text) =>
        Label.Matches(text).Select(m => m.Groups[1].Value.Trim())
            .Where(l => l.Length > 1 && !l.StartsWith('=') && l != "calculate")
            .Distinct().ToList();

    /// <summary>A chunk's section (from its SourceRef) and every Markdown heading inside its text.</summary>
    public static List<string> HeadingsOf(string? sourceRef, string? text)
    {
        var headings = new List<string>();
        var section = sourceRef is null ? null : HrSplitter.SourceRefPattern.Match(sourceRef).Groups["section"].Value;
        if (!string.IsNullOrEmpty(section)) headings.Add(section);
        if (!string.IsNullOrEmpty(text))
            headings.AddRange(MarkdownHeading.Matches(text).Select(m => m.Groups["title"].Value.Trim()));
        return headings.Distinct().ToList();
    }

    public static bool Cites(string label, HrCitable c)
    {
        if (c.Cite is not null && label == c.Cite) return true;
        if (c.SourceRef is not null && label == c.SourceRef) return true;
        if (!label.StartsWith(c.ItemName, StringComparison.Ordinal)) return false;
        var rest = label[c.ItemName.Length..];
        if (rest.Length > 0 && !char.IsWhiteSpace(rest[0]) && rest[0] != '§') return false;   // "X" does not name "X2"
        if (c.Whole) return true;
        rest = Regex.Replace(rest.Trim().TrimStart('§').Trim(), @"\s*L\d+-\d+$", "");
        if (rest.Length == 0) return true;
        return c.Headings.Any(h => h.StartsWith(rest, StringComparison.OrdinalIgnoreCase)
            && (h.Length == rest.Length || !char.IsLetterOrDigit(h[rest.Length])));
    }

    /// <summary>The labels in a text that name no place that was sent, in the order they appear.</summary>
    public static IReadOnlyList<string> Unknown(string text, IReadOnlyList<HrCitable> sent) =>
        Labels(text).Where(l => !sent.Any(c => Cites(l, c))).ToList();
}
