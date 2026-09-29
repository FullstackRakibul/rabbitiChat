// Free-identifier extraction from an Angular template expression.
//
// This is what makes the data contract derivable with no AI at all: given
// `[label]="user.name"` we need "user", and given `(click)="remove(item)"` we
// need "remove" and "item". An identifier that resolves to a template variable
// (`let item` from an *ngFor) is LOCAL and must never become an @Input; one that
// resolves to the component class is parent state and must.
//
// Deliberately a small scanner rather than a real TypeScript expression parser:
// Angular template expressions are a restricted subset (no function bodies, no
// arrow bodies worth walking), and we only need the *set of roots*, not a tree.
// What it must get right is what it must NOT report: string contents, numbers,
// property names after a dot, and the template-expression keywords.

using System.Text;

namespace RabbitIC.Api.ComponentSplitter.Parsing;

public static class ExpressionScanner
{
    /// <summary>
    /// Words that look like identifiers but never refer to component state.
    /// `$event` is excluded separately (it starts with '$' and is Angular's own).
    /// </summary>
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "true", "false", "null", "undefined", "let", "of", "as",
        "this", "typeof", "in", "new", "void",
        // Angular's *ngFor implicit context — local to the loop, never an @Input.
        "index", "first", "last", "even", "odd", "count", "trackBy",
    };

    /// <summary>
    /// Root identifiers referenced by <paramref name="expression"/>, in first-seen
    /// order and de-duplicated.
    ///
    /// "Root" means the head of a member chain: `user.profile.name` yields only
    /// `user`, because passing `user` down is what satisfies the child's needs.
    /// </summary>
    public static IReadOnlyList<string> FreeIdentifiers(string? expression)
    {
        var found = new List<string>();
        if (string.IsNullOrWhiteSpace(expression)) return found;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var i = 0;

        while (i < expression.Length)
        {
            var c = expression[i];

            // Skip string literals whole — their contents are data, not identifiers.
            if (c is '\'' or '"' or '`')
            {
                i = SkipString(expression, i);
                continue;
            }

            // A numeric literal can contain letters (1e5, 0x1f); consume it so its
            // suffix is never mistaken for a name.
            if (char.IsDigit(c))
            {
                while (i < expression.Length && (char.IsLetterOrDigit(expression[i]) || expression[i] == '.'))
                    i++;
                continue;
            }

            if (!IsIdentifierStart(c)) { i++; continue; }

            var start = i;
            while (i < expression.Length && IsIdentifierPart(expression[i])) i++;
            var word = expression[start..i];

            // Property access (`.name`) and object keys (`name:`) are not free
            // identifiers — only the head of the chain is.
            var precededByDot = PreviousNonSpace(expression, start - 1) == '.';
            if (precededByDot) continue;

            // A pipe NAME is not component state: in `price | currency`,
            // `currency` names a pipe that Angular resolves from the module/
            // imports, not a value the parent owns. Treating it as one would
            // invent an @Input that can never be satisfied — and, worse, would
            // flag the component as having an unresolvable binding and block a
            // perfectly valid split. Pipe ARGUMENTS (after a ':') are still
            // scanned, because those genuinely can be component state.
            if (IsPipeName(expression, start)) continue;

            if (Keywords.Contains(word)) continue;
            if (word.StartsWith('$')) continue;      // $event, $any — Angular's own

            if (seen.Add(word)) found.Add(word);
        }

        return found;
    }

    /// <summary>
    /// Identifiers that are *called* in the expression — `remove(item)` yields
    /// "remove". These are the handler names that become @Output()s, which is why
    /// they are collected separately from plain reads.
    /// </summary>
    public static IReadOnlyList<string> CalledIdentifiers(string? expression)
    {
        var found = new List<string>();
        if (string.IsNullOrWhiteSpace(expression)) return found;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var i = 0;

        while (i < expression.Length)
        {
            var c = expression[i];

            if (c is '\'' or '"' or '`') { i = SkipString(expression, i); continue; }
            if (!IsIdentifierStart(c)) { i++; continue; }

            var start = i;
            while (i < expression.Length && IsIdentifierPart(expression[i])) i++;
            var word = expression[start..i];

            // A call is an identifier followed by '(' — whitespace allowed between.
            var next = NextNonSpace(expression, i);
            if (next != '(') continue;
            if (Keywords.Contains(word) || word.StartsWith('$')) continue;

            if (seen.Add(word)) found.Add(word);
        }

        return found;
    }

    /// <summary>
    /// Template variables introduced by an *ngFor microsyntax, e.g.
    /// `let item of items; let i = index` → ["item", "i"].
    ///
    /// These names are local to the loop body. Reporting them is how the fact
    /// deriver avoids the classic bug of turning a loop variable into an @Input.
    /// </summary>
    public static IReadOnlyList<string> NgForVariables(string? microsyntax)
    {
        var vars = new List<string>();
        if (string.IsNullOrWhiteSpace(microsyntax)) return vars;

        var i = 0;
        while (i < microsyntax.Length)
        {
            // Find the next `let` that stands as its own word.
            var idx = microsyntax.IndexOf("let", i, StringComparison.Ordinal);
            if (idx < 0) break;

            var before = idx == 0 ? ' ' : microsyntax[idx - 1];
            var afterIdx = idx + 3;
            var after = afterIdx < microsyntax.Length ? microsyntax[afterIdx] : ' ';

            if (!char.IsLetterOrDigit(before) && before != '_' && (char.IsWhiteSpace(after) || after == '\t'))
            {
                var j = afterIdx;
                while (j < microsyntax.Length && char.IsWhiteSpace(microsyntax[j])) j++;
                var start = j;
                while (j < microsyntax.Length && IsIdentifierPart(microsyntax[j])) j++;
                if (j > start) vars.Add(microsyntax[start..j]);
                i = j;
            }
            else
            {
                i = idx + 3;
            }
        }

        return vars;
    }

    /// <summary>
    /// The collection expression of an *ngFor — `let item of items` → "items".
    /// This is the parent-owned value the loop reads, so it is the input that a
    /// list-wrapper split depends on.
    /// </summary>
    public static string? NgForCollection(string? microsyntax)
    {
        if (string.IsNullOrWhiteSpace(microsyntax)) return null;

        var ofIdx = FindWord(microsyntax, "of");
        if (ofIdx < 0) return null;

        var rest = microsyntax[(ofIdx + 2)..];
        var end = rest.IndexOf(';');
        if (end >= 0) rest = rest[..end];

        return rest.Trim() is { Length: > 0 } s ? s : null;
    }

    /// <summary>Index of a standalone word, or -1. Avoids matching inside a name.</summary>
    private static int FindWord(string text, string word)
    {
        var i = 0;
        while (i < text.Length)
        {
            var idx = text.IndexOf(word, i, StringComparison.Ordinal);
            if (idx < 0) return -1;

            var before = idx == 0 ? ' ' : text[idx - 1];
            var afterIdx = idx + word.Length;
            var after = afterIdx < text.Length ? text[afterIdx] : ' ';

            if (!IsIdentifierPart(before) && !IsIdentifierPart(after)) return idx;
            i = idx + word.Length;
        }
        return -1;
    }

    /// <summary>Index just past the string literal starting at <paramref name="i"/>.</summary>
    private static int SkipString(string text, int i)
    {
        var quote = text[i];
        i++;
        while (i < text.Length)
        {
            if (text[i] == '\\') { i += 2; continue; }   // escaped char
            if (text[i] == quote) return i + 1;
            i++;
        }
        return i;
    }

    /// <summary>
    /// Whether the identifier starting at <paramref name="start"/> is a pipe
    /// name — preceded by a single '|' that is not part of the '||' operator.
    ///
    /// The distinction matters: in `a | currency` the word is a pipe, while in
    /// `a || fallback` it is genuine component state.
    /// </summary>
    private static bool IsPipeName(string text, int start)
    {
        var i = start - 1;
        while (i >= 0 && char.IsWhiteSpace(text[i])) i--;

        if (i < 0 || text[i] != '|') return false;

        // `||` — a logical or, so what follows is an operand, not a pipe.
        return i == 0 || text[i - 1] != '|';
    }

    private static char PreviousNonSpace(string text, int from)
    {
        for (var i = from; i >= 0; i--)
            if (!char.IsWhiteSpace(text[i])) return text[i];
        return '\0';
    }

    private static char NextNonSpace(string text, int from)
    {
        for (var i = from; i < text.Length; i++)
            if (!char.IsWhiteSpace(text[i])) return text[i];
        return '\0';
    }

    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_' || c == '$';
    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';

    /// <summary>
    /// Every `{{ … }}` expression in a text run. Returned as raw expression
    /// strings so the caller can scan each for identifiers.
    /// </summary>
    public static IReadOnlyList<string> Interpolations(string text)
    {
        var results = new List<string>();
        var i = 0;

        while (i < text.Length - 1)
        {
            if (text[i] == '{' && text[i + 1] == '{')
            {
                var end = text.IndexOf("}}", i + 2, StringComparison.Ordinal);
                if (end < 0) break;
                var expr = text[(i + 2)..end].Trim();
                if (expr.Length > 0) results.Add(expr);
                i = end + 2;
            }
            else i++;
        }

        return results;
    }

    /// <summary>Strips `{{ … }}` runs, leaving the literal text. Used for text-node checks.</summary>
    public static string StripInterpolations(string text)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            if (i < text.Length - 1 && text[i] == '{' && text[i + 1] == '{')
            {
                var end = text.IndexOf("}}", i + 2, StringComparison.Ordinal);
                if (end < 0) break;
                i = end + 2;
            }
            else sb.Append(text[i++]);
        }
        return sb.ToString();
    }
}
