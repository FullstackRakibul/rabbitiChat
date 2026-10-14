// মানব সম্পদ — the number check (docs/HR_MODULE_PROMPT.md §3.3).
//
// After the answer, every amount of 100 or more in it must be traceable: it
// appears in the user's messages, in the context that was sent, or in a
// `calculate` result of this turn, to within 1. Whatever cannot be traced is
// reported, and the UI shows it under the answer — the model may never state an
// amount HR cannot check without the warning showing.
//
// Bengali digits are read as digits; ৳, "Tk" and thousands separators (including
// the Indian 1,23,456 grouping) are ignored.

using System.Globalization;
using System.Text.RegularExpressions;

namespace RabbitIC.Api.Hr;

public static class HrNumberCheck
{
    public const decimal Threshold = 100m;
    public const decimal Tolerance = 1m;

    private static readonly Regex Amount = new(
        // "12,000" and "1,23,456" whole — also when a comma follows ("12,000, 7 years") —
        // or a plain number. A thousands group is never read as a number of its own.
        @"(?<![\d.,])\d{1,3}(?:,\d{2,3})+(?:\.\d+)?(?!\d)(?!,\d)|(?<![\d.,])\d+(?:\.\d+)?(?!\d)(?!,\d{2,3}(?!\d))",
        RegexOptions.Compiled);

    /// <summary>The reply's amounts of 100 or more that no source accounts for, in the order they appear.</summary>
    public static IReadOnlyList<decimal> Unverified(string reply, IEnumerable<string> sources, IEnumerable<decimal> calculated)
    {
        var known = sources.SelectMany(Numbers).Concat(calculated).ToList();
        return Numbers(reply)
            .Where(a => a >= Threshold)
            .Distinct()
            .Where(a => !known.Any(k => Math.Abs(k - a) <= Tolerance))
            .ToList();
    }

    /// <summary>Every number in the text, digits normalised and separators removed.</summary>
    public static IEnumerable<decimal> Numbers(string? text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        var normalised = Normalise(text);
        foreach (Match m in Amount.Matches(normalised))
        {
            if (decimal.TryParse(m.Value.Replace(",", ""), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v))
                yield return v;
        }
    }

    private static string Normalise(string text)
    {
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (chars[i] is >= '০' and <= '৯') chars[i] = (char)('0' + (chars[i] - '০'));
        return new string(chars).Replace("৳", " ").Replace("Tk.", " ").Replace("Tk", " ");
    }
}
