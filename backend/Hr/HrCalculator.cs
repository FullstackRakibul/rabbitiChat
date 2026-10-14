// মানব সম্পদ — the `calculate` tool (docs/HR_MODULE_PROMPT.md §3.3).
//
// Every amount in an HR answer is computed here rather than by the model, which
// is unreliable at arithmetic. The language is deliberately tiny: numbers,
// + - * /, parentheses and round(x, n). Nothing else parses — no names, no
// functions, no thousands separators — and a refusal says exactly what was found
// and where, so the model can correct its call. Decimal arithmetic, so money
// does not pick up binary-fraction noise.

using System.Globalization;

namespace RabbitIC.Api.Hr;

public static class HrCalculator
{
    public const string ToolName = "calculate";
    public const int MaxLength = 500;

    public static readonly RabbitIC.Api.ModelSources.ToolDefinition Definition = new(
        ToolName,
        "Evaluate arithmetic for an HR amount. Use it for every calculation and show the formula in the answer. "
        + "Allowed: numbers (no thousands separators), + - * /, parentheses and round(x, n).",
        """{ "type": "object", "properties": { "expression": { "type": "string", "description": "e.g. round((12000/30)*15*7, 0)" } }, "required": ["expression"] }""");

    /// <summary>The value, or the reason the expression was refused.</summary>
    public static (decimal? Value, string? Error) Evaluate(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return (null, "The expression is empty.");
        if (expression.Length > MaxLength) return (null, $"The expression is longer than {MaxLength} characters.");

        var p = new Parser(expression);
        try
        {
            var value = p.Expression();
            p.SkipSpace();
            if (!p.AtEnd) throw p.Unexpected();
            return (value, null);
        }
        catch (CalcException e) { return (null, e.Message); }
        catch (OverflowException) { return (null, "The result is too large."); }
    }

    /// <summary>"42000", "8766.67" — no trailing zeros, invariant digits.</summary>
    public static string Format(decimal value) => value.ToString("0.############", CultureInfo.InvariantCulture);

    private sealed class CalcException(string message) : Exception(message);

    private sealed class Parser(string s)
    {
        private int _pos;

        public bool AtEnd => _pos >= s.Length;

        public void SkipSpace() { while (!AtEnd && char.IsWhiteSpace(s[_pos])) _pos++; }

        private char? Peek() { SkipSpace(); return AtEnd ? null : s[_pos]; }

        public CalcException Unexpected() => AtEnd
            ? new CalcException("The expression ended early; a number or '(' is missing at the end.")
            : new CalcException($"Only numbers, + - * /, parentheses and round(x, n) are allowed; found '{s[_pos]}' at position {_pos + 1}.");

        // expression := term (('+' | '-') term)*
        public decimal Expression()
        {
            var value = Term();
            while (Peek() is '+' or '-')
            {
                var op = s[_pos++];
                var right = Term();
                value = op == '+' ? value + right : value - right;
            }
            return value;
        }

        // term := factor (('*' | '/') factor)*
        private decimal Term()
        {
            var value = Factor();
            while (Peek() is '*' or '/')
            {
                var op = s[_pos++];
                var right = Factor();
                if (op == '/' && right == 0) throw new CalcException("Division by zero.");
                value = op == '*' ? value * right : value / right;
            }
            return value;
        }

        // factor := ('+' | '-') factor | number | '(' expression ')' | round '(' expression ',' expression ')'
        private decimal Factor()
        {
            var c = Peek();
            if (c is '+' or '-') { _pos++; var v = Factor(); return c == '-' ? -v : v; }
            if (c == '(')
            {
                _pos++;
                var v = Expression();
                Expect(')');
                return v;
            }
            if (c is >= '0' and <= '9' or '.') return Number();
            if (c is not null && string.Compare(s, _pos, "round", 0, 5, StringComparison.OrdinalIgnoreCase) == 0)
            {
                _pos += 5;
                Expect('(');
                var x = Expression();
                Expect(',');
                var n = Expression();
                Expect(')');
                if (n != decimal.Truncate(n) || n < 0 || n > 10)
                    throw new CalcException("round(x, n) needs n to be a whole number from 0 to 10.");
                return Math.Round(x, (int)n, MidpointRounding.AwayFromZero);
            }
            throw Unexpected();
        }

        private decimal Number()
        {
            var start = _pos;
            while (!AtEnd && (char.IsDigit(s[_pos]) || s[_pos] == '.')) _pos++;
            var text = s[start.._pos];
            if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v))
                throw new CalcException($"'{text}' at position {start + 1} is not a number.");
            if (!AtEnd && s[_pos] == ',' && _pos + 1 < s.Length && char.IsDigit(s[_pos + 1]) && !InsideRoundArguments())
                throw new CalcException($"Write numbers without thousands separators (found ',' at position {_pos + 1}).");
            return v;
        }

        private bool InsideRoundArguments() => s[.._pos].LastIndexOf("round", StringComparison.OrdinalIgnoreCase) >= 0;

        private void Expect(char ch)
        {
            if (Peek() == ch) { _pos++; return; }
            throw AtEnd
                ? new CalcException($"The expression ended early; '{ch}' is missing.")
                : new CalcException($"Expected '{ch}' at position {_pos + 1}, found '{s[_pos]}'.");
        }
    }
}
