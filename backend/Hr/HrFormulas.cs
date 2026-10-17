// মানব সম্পদ — inline formulas (docs/HR_MODULE_PROMPT.md §3.3, a Phase 2 decision).
//
// The model writes every amount it works out as [[= expression ]]; it never does
// the arithmetic. The answer streams through HrFormulaStream, which passes plain
// text on at once, holds back only what may be a formula, and puts the result in
// its place: ৳ with thousands separators, ৳48,000. A formula that cannot be
// calculated — invalid, or cut off by a line break or the end of the answer —
// becomes "⚠ গণনা করা যায়নি" and is listed as unverified.
//
// The evaluator is HrCalculator. Before it sees an expression, the forms a model
// writes without meaning anything different are made plain: Bengali digits, × and
// ÷, ৳ and Tk, and thousands separators (48,000 and 1,20,000).

using System.Text;
using System.Text.RegularExpressions;

namespace RabbitIC.Api.Hr;

/// <summary>One formula as it appeared in the answer: where its text sits, and what it became.</summary>
/// <param name="Offset">Where <paramref name="Display"/> starts in the answer (UTF-16 units, as in JavaScript).</param>
public sealed record HrFormula(string Expression, decimal? Value, string Display, string? Error, int Offset)
{
    public bool Ok => Value is not null;
}

/// <summary>A piece of the answer to send on: plain text, or a formula's result in its place.</summary>
public sealed record HrFormulaPiece(string Text, HrFormula? Formula = null);

public sealed class HrFormulaStream
{
    public const string Open = "[[=";
    public const string Close = "]]";
    public const string CouldNotCalculate = "⚠ গণনা করা যায়নি";

    /// <summary>Longer than this without "]]", it is not a formula that will ever close.</summary>
    public const int MaxFormulaChars = 300;

    private readonly StringBuilder _held = new();
    private bool _inFormula;
    private int _offset;   // characters already handed out
    private readonly List<HrFormula> _formulas = new();

    public IReadOnlyList<HrFormula> Formulas => _formulas;

    /// <summary>Takes the next piece of the model's stream; returns what can be sent now.</summary>
    public List<HrFormulaPiece> Push(string text)
    {
        _held.Append(text);
        var output = new List<HrFormulaPiece>();
        Drain(output, final: false);
        return output;
    }

    /// <summary>The end of the answer: whatever was held goes out, and an open formula is reported as cut off.</summary>
    public List<HrFormulaPiece> Finish()
    {
        var output = new List<HrFormulaPiece>();
        Drain(output, final: true);
        return output;
    }

    private void Drain(List<HrFormulaPiece> output, bool final)
    {
        while (true)
        {
            var buffer = _held.ToString();
            if (!_inFormula)
            {
                var open = buffer.IndexOf(Open, StringComparison.Ordinal);
                if (open >= 0)
                {
                    // The result carries its own ৳; one the model wrote before the formula would double it.
                    Emit(output, StripCurrency(buffer[..open]));
                    _held.Clear().Append(buffer[(open + Open.Length)..]);
                    _inFormula = true;
                    continue;
                }
                // Hold back a tail that may lead into "[[=" ("[", "[[", or a ৳ before them), unless this is the end.
                var keep = final ? 0 : PartialOpenLength(buffer);
                Emit(output, buffer[..^keep]);
                _held.Clear().Append(buffer[^keep..]);
                return;
            }

            var close = buffer.IndexOf(Close, StringComparison.Ordinal);
            var newline = buffer.IndexOf('\n');
            if (close >= 0 && (newline < 0 || close < newline))
            {
                AddFormula(output, buffer[..close], cutOff: false);
                _held.Clear().Append(buffer[(close + Close.Length)..]);
                _inFormula = false;
                continue;
            }
            if (newline >= 0 || buffer.Length > MaxFormulaChars || final)
            {
                // Cut off: by a line break (the line goes on as text), by running too
                // long (what was held is dropped), or by the end of the answer.
                var end = newline >= 0 ? newline : buffer.Length;
                AddFormula(output, buffer[..end], cutOff: true);
                _held.Clear().Append(newline >= 0 ? buffer[newline..] : "");
                _inFormula = false;
                if (newline < 0 && !final) return;
                continue;
            }
            return;   // a formula still arriving
        }
    }

    private static readonly string[] Leads = { "৳ [[", "৳[[", "৳ [", "৳[", "৳ ", "৳", "[[", "[" };

    private static int PartialOpenLength(string buffer)
    {
        foreach (var lead in Leads)
            if (buffer.EndsWith(lead, StringComparison.Ordinal)) return lead.Length;
        return 0;
    }

    private static string StripCurrency(string text) =>
        text.EndsWith("৳ ", StringComparison.Ordinal) ? text[..^2]
        : text.EndsWith("৳", StringComparison.Ordinal) ? text[..^1]
        : text;

    private void Emit(List<HrFormulaPiece> output, string text)
    {
        if (text.Length == 0) return;
        output.Add(new HrFormulaPiece(text));
        _offset += text.Length;
    }

    private void AddFormula(List<HrFormulaPiece> output, string expression, bool cutOff)
    {
        expression = expression.Trim();
        var (value, display, error) = cutOff
            ? (null, CouldNotCalculate, "The formula was not closed with ]].")
            : HrFormulas.EvaluateInline(expression);
        var formula = new HrFormula(expression, value, display, error, _offset);
        _formulas.Add(formula);
        output.Add(new HrFormulaPiece(display, formula));
        _offset += display.Length;
    }
}

public static class HrFormulas
{
    private static readonly Regex Thousands = new(@"(?<=\d),(?=\d{3}(?!\d))|(?<=\d),(?=\d{2},)", RegexOptions.Compiled);
    private static readonly Regex Currency = new(@"৳|\bTk\.?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The expression as the evaluator reads it: ASCII digits, * and /, no currency, no thousands separators.</summary>
    public static string Normalize(string expression)
    {
        var sb = new StringBuilder(expression.Length);
        foreach (var c in expression)
            sb.Append(c switch
            {
                >= '০' and <= '৯' => (char)('0' + (c - '০')),
                '×' => '*',
                '÷' => '/',
                '−' => '-',
                _ => c,
            });
        return Thousands.Replace(Currency.Replace(sb.ToString(), ""), "");
    }

    /// <summary>
    /// An inline [[= … ]] formula worked out: made plain (Normalize), evaluated by
    /// HrCalculator, shown as ৳ — or "⚠ গণনা করা যায়নি" with the reason. The one way a
    /// formula is worked out, for an answer as it streams, a Rulebook page, and the
    /// Rulebook editor's preview (/api/hr/calculate with inline).
    /// </summary>
    public static (decimal? Value, string Display, string? Error) EvaluateInline(string? expression)
    {
        var (value, error) = HrCalculator.Evaluate(Normalize((expression ?? "").Trim()));
        return (value, value is decimal v ? Taka(v) : HrFormulaStream.CouldNotCalculate, error);
    }

    /// <summary>The amounts written inside a formula: an example's inputs, stated by the example itself.</summary>
    public static IEnumerable<decimal> Operands(string expression) => HrNumberCheck.Numbers(Normalize(expression));

    /// <summary>৳ with the separators HR in Bangladesh reads (৳48,000; ৳1,20,000), at most two decimals.</summary>
    public static string Taka(decimal value)
    {
        var rounded = Math.Round(Math.Abs(value), 2, MidpointRounding.AwayFromZero);
        var whole = decimal.Truncate(rounded).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var fraction = rounded - decimal.Truncate(rounded);

        // Lakh grouping: the last three digits, then groups of two.
        var grouped = whole.Length <= 3 ? whole : Group(whole[..^3]) + "," + whole[^3..];
        var text = fraction == 0
            ? grouped
            : grouped + fraction.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)[1..].TrimEnd('0');
        return (value < 0 ? "-৳" : "৳") + text;

        static string Group(string head)
        {
            var parts = new List<string>();
            for (var end = head.Length; end > 0; end -= 2) parts.Insert(0, head[Math.Max(0, end - 2)..end]);
            return string.Join(",", parts);
        }
    }
}
