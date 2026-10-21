// ম্যাট্রিক্স — the summary (docs/MATRIX_PROMPT.md §3.8): the model writes it from the
// report card; code checks it.
//
// 5–8 short points in Bengali or English. Every number is copied from the card or
// written as [[= … ]] (HrFormulaStream works it out); every point ends with the card
// labels it uses, [C1] [C4]. After the answer: the exact number check (every number,
// at the precision written — HrNumberCheck.UnverifiedExact), points without a label,
// and labels the card does not have ("unknown source").

using System.Text.RegularExpressions;
using RabbitIC.Api.Hr;

namespace RabbitIC.Api.Matrix;

public static partial class MatrixSummary
{
    public static readonly string[] Languages = { "bn", "en" };
    public const int MinPoints = 5;
    public const int MaxPoints = 8;

    public static string System(string language) =>
        "You write the summary of one report from a garment factory (RMG) for its managers.\n"
        + "Rules:\n"
        + $"- Write {MinPoints} to {MaxPoints} short points, each on its own line starting with \"- \".\n"
        + (language == "bn"
            ? "- Write in Bengali (বাংলা). Numbers may be in Bengali digits.\n"
            : "- Write in English.\n")
        + "- Use only what the report card below says. Copy every number exactly as the card writes it.\n"
        + "- When a point needs a number the card does not give — a difference, a share — write the calculation as [[= expression ]] "
        + "with numbers from the card, for example [[= 676 - 652 ]]. Never work it out yourself.\n"
        + "- End every point with the labels of the card lines it uses, such as [C1] or [C2][C5].\n"
        + "- Never name a person. Do not guess causes the card does not show.\n"
        + "Answer with the points only.";

    public static string User(string card, string language) =>
        $"{card}\nWrite the summary in {(language == "bn" ? "Bengali" : "English")}.";

    /// <summary>A formula's result as Matrix writes numbers: lakh grouping, at most two decimals, no ৳.</summary>
    public static string Number(decimal value) => HrFormulas.Taka(value).Replace("৳", "");

    [GeneratedRegex(@"\[(C\d+)\]")]
    private static partial Regex CardLabel();

    [GeneratedRegex(@"\[(?:C|R)\d+\]|\[KPI:[^\]]*\]")]
    private static partial Regex AnyLabel();

    [GeneratedRegex(@"^\s*(?:[-•*]|\d+[.)]|[০-৯]+[.)])\s+")]
    private static partial Regex Bullet();

    /// <summary>The text without its labels — "[C12]" is no number of the summary's own.</summary>
    public static string WithoutLabels(string text) => AnyLabel().Replace(text, " ");

    /// <summary>The points; the 1-based numbers of those that cite no card line; labels the card does not have.</summary>
    public static (List<string> Points, List<int> Uncited, List<string> Unknown) Check(string text, IReadOnlyCollection<string> labels)
    {
        var points = text.Split('\n').Where(l => Bullet().IsMatch(l)).Select(l => l.Trim()).ToList();
        var uncited = points.Select((p, i) => (p, i)).Where(x => !CardLabel().IsMatch(x.p)).Select(x => x.i + 1).ToList();
        var unknown = CardLabel().Matches(text).Select(m => m.Groups[1].Value).Where(l => !labels.Contains(l)).Distinct().ToList();
        return (points, uncited, unknown);
    }
}
