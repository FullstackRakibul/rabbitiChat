// Jiji — the script checks (docs/JIJI_PLAN.md, Script JSON).
//
// Normalise first (trim, renumber beats, clean hashtags, take the item's own
// content type and language), then check. A draft may be incomplete; it may not
// be wrong: a code beat whose lines are not in SourceNotes, a fifth code line, a
// title YouTube would refuse. Approval adds the rest: every spoken part filled,
// originalAngle and myTake above all, and a length a Short can carry.

using System.Text.RegularExpressions;
using RabbitIC.Api.Jiji.Dtos;
using RabbitIC.Api.Jiji.Models;

namespace RabbitIC.Api.Jiji.Services;

public static partial class JijiScriptValidator
{
    public const int MaxBeats = 8;
    public const int MaxCodeLines = 4;
    public const int CodeLineWarnChars = 46;   // wider wraps on a 1080-px frame at a readable size
    public const int TitleMax = 100;           // YouTube's limit
    public const int DescriptionMax = 5000;    // YouTube's limit
    public const int TagsTotalMax = 500;       // YouTube's limit, all tags together
    public const int WordsMin = 90, WordsMax = 130;            // 35–55 s, the plan's target
    public const int WordsHardMin = 60, WordsHardMax = 160;    // beyond these approval is refused
    public const double WordsPerSecond = 2.4;

    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
    [GeneratedRegex(@"^[\p{L}\p{N}]{1,30}$")] private static partial Regex HashtagShape();
    [GeneratedRegex(@"[\p{L}\p{N}]+(?:['’\-][\p{L}\p{N}]+)*")] private static partial Regex Word();

    public static void Normalize(JijiScript s, JijiContent c)
    {
        s.ContentType = c.ContentType;
        s.Language = c.Language;
        s.Hook = Clean(s.Hook);
        s.OriginalAngle = Clean(s.OriginalAngle);
        s.MyTake = Clean(s.MyTake);
        s.Cta = Clean(s.Cta);
        s.Title = Clean(s.Title);
        s.Description = (s.Description ?? "").Trim();

        var id = 0;
        foreach (var b in s.Beats)
        {
            b.Id = ++id;
            b.Text = Clean(b.Text);
            b.Visual = (b.Visual ?? "").Trim().ToLowerInvariant();
            if (b.Visual == "") b.Visual = JijiVisual.Text;
            // Code keeps its own indentation; only trailing blanks and blank edge lines go.
            b.Code = b.Visual == JijiVisual.Code
                ? string.Join('\n', (b.Code ?? "").Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd())).Trim('\n')
                : null;
            b.Items = b.Visual == JijiVisual.List
                ? (b.Items ?? new()).Select(Clean).Where(i => i != "").ToList()
                : null;
        }

        s.Hashtags = (s.Hashtags ?? new())
            .Select(h => Clean(h).TrimStart('#').Replace(" ", ""))
            .Where(h => h != "")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static JijiScriptCheck Check(JijiScript s, JijiContent c, bool forApproval)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (s.Beats.Count > MaxBeats) errors.Add($"At most {MaxBeats} beats; this script has {s.Beats.Count}.");

        var notes = Flatten(c.SourceNotes);
        foreach (var b in s.Beats)
        {
            var at = $"Beat {b.Id}";
            if (!JijiVisual.All.Contains(b.Visual)) { errors.Add($"{at}: visual must be text, code or list."); continue; }
            if (forApproval && b.Text == "") errors.Add($"{at}: the spoken text is empty.");

            if (b.Visual == JijiVisual.Code)
            {
                var lines = (b.Code ?? "").Split('\n');
                if (string.IsNullOrWhiteSpace(b.Code)) errors.Add($"{at}: a code beat needs its snippet.");
                else
                {
                    if (lines.Length > MaxCodeLines) errors.Add($"{at}: at most {MaxCodeLines} code lines; it has {lines.Length}.");
                    // Only a snippet you pasted into the notes, never generated code.
                    var foreign = lines.Where(l => l.Trim() != "" && !notes.Contains(Flatten(l), StringComparison.Ordinal)).ToList();
                    if (foreign.Count > 0)
                        errors.Add($"{at}: code must be copied from your notes; not found there: \"{foreign[0].Trim()}\".");
                    if (lines.Any(l => l.Length > CodeLineWarnChars))
                        warnings.Add($"{at}: a code line is over {CodeLineWarnChars} characters and may wrap on screen.");
                }
            }
            else if (b.Visual == JijiVisual.List && (b.Items!.Count < 2 || b.Items.Count > 5))
                errors.Add($"{at}: a list beat shows 2 to 5 items; it has {b.Items.Count}.");
        }

        if (s.Title.Length > TitleMax) errors.Add($"The title is {s.Title.Length} characters; YouTube allows {TitleMax}.");
        if (s.Description.Length > DescriptionMax) errors.Add($"The description is over {DescriptionMax} characters.");
        if (s.Title.IndexOfAny(['<', '>']) >= 0 || s.Description.IndexOfAny(['<', '>']) >= 0)
            errors.Add("YouTube refuses < and > in a title or description.");
        var badTag = s.Hashtags.FirstOrDefault(h => !HashtagShape().IsMatch(h));
        if (badTag is not null) errors.Add($"Hashtag \"{badTag}\": letters and digits only, at most 30.");
        if (s.Hashtags.Sum(h => h.Length + 1) > TagsTotalMax) errors.Add($"The hashtags together pass YouTube's {TagsTotalMax}-character limit.");

        var stats = Stats(s);
        if (forApproval)
        {
            if (s.Hook == "") errors.Add("The hook is empty.");
            if (s.OriginalAngle == "") errors.Add("originalAngle is required: the one idea this video teaches that a generic explainer would not.");
            if (s.MyTake == "") errors.Add("myTake is required: your own real example.");
            if (s.Cta == "") errors.Add("The call to action is empty.");
            if (s.Title == "") errors.Add("The title is empty.");
            if (s.Beats.Count < 2) errors.Add("A script needs at least 2 beats.");
            if (stats.Words < WordsHardMin || stats.Words > WordsHardMax)
                errors.Add($"{stats.Words} spoken words is too far from a Short ({WordsMin}–{WordsMax}).");
        }
        if (stats.Words > 0 && (stats.Words < WordsMin || stats.Words > WordsMax))
            warnings.Add($"{stats.Words} spoken words, about {stats.EstimatedSeconds} s; the target is {WordsMin}–{WordsMax} (35–55 s).");
        if (CountWords(s.Hook) > 15) warnings.Add("The hook is over 15 words; the first second decides the swipe.");

        return new JijiScriptCheck(errors, warnings, stats);
    }

    /// <summary>What is spoken: hook, every beat, myTake, CTA. On-screen code and list items are not read aloud.</summary>
    public static JijiScriptStats Stats(JijiScript s)
    {
        var words = CountWords(s.Hook) + s.Beats.Sum(b => CountWords(b.Text)) + CountWords(s.MyTake) + CountWords(s.Cta);
        return new JijiScriptStats(words, (int)Math.Round(words / WordsPerSecond));
    }

    public static int CountWords(string? text) => string.IsNullOrWhiteSpace(text) ? 0 : Word().Matches(text).Count;

    private static string Clean(string? v) => Spaces().Replace(v ?? "", " ").Trim();

    /// <summary>Whitespace-insensitive form, so a snippet still matches the notes it was copied from after re-indenting.</summary>
    private static string Flatten(string? v) => Spaces().Replace(v ?? "", "");
}
