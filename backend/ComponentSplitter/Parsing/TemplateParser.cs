// Stage 1, template half — Angular template → IrNode tree, via AngleSharp.
//
// AngleSharp gives us spec-compliant tree building and malformed-markup recovery.
// What it does NOT give us is Angular: to AngleSharp, `[label]="x"`, `(click)="y"`
// and `*ngFor="…"` are three unremarkable attribute names. Recognising them is
// this file's job, and it is why a generic HTML parser only removes the tokenizer,
// not the Angular layer.
//
// SOURCE SPANS, AND WHY WE RE-FIND THEM:
// The generator MOVES original markup rather than re-serialising the DOM, because
// re-serialisation is how a splitter silently drops an attribute, reorders a class
// list, or rewrites a self-closing tag. AngleSharp's public API does not expose
// reliable end offsets for an element, so each element's exact [start, end) span is
// recovered by scanning the original source with a depth-aware matcher. The parsed
// tree tells us WHICH element; the raw source tells us its exact bytes.

using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using RabbitIC.Api.ComponentSplitter.Ir;

namespace RabbitIC.Api.ComponentSplitter.Parsing;

public sealed class TemplateParser
{
    /// <summary>
    /// Angular attribute prefixes that are structural directives. Matched
    /// case-insensitively because AngleSharp lowercases attribute names, which is
    /// also why the IR stores the ORIGINAL spelling recovered from source.
    /// </summary>
    private const string NgFor = "*ngfor";
    private const string NgIf = "*ngif";

    private readonly HtmlParser _parser = new(new HtmlParserOptions
    {
        // Angular attribute names are camelCase and its binding punctuation is
        // case- and character-sensitive. Without this, AngleSharp lowercases
        // names and `[itemLabel]` becomes `[itemlabel]` — a child that silently
        // never receives its input. This single option is most of why a real
        // HTML parser is usable here at all.
        IsPreservingAttributeNames = true,

        // Keep exact source offsets available on the parsed nodes.
        IsKeepingSourceReferences = true,

        // Angular templates contain no scripts to execute, and comments are not
        // carried into the IR.
        IsScripting = false,
        SkipComments = true,
    });

    private int _nextId;

    /// <summary>
    /// Parses <paramref name="template"/> into root IR nodes.
    /// Offsets in the result index into the string passed in, unchanged.
    /// </summary>
    public IReadOnlyList<IrNode> Parse(string template)
    {
        _nextId = 0;

        // Fragment context: a template is not a document, and parsing it as one
        // would wrap it in html/head/body and move stray text around.
        var context = _parser.ParseDocument("<body></body>").Body!;
        var nodes = _parser.ParseFragment(template, context);

        var cursor = 0;
        var roots = new List<IrNode>();

        foreach (var node in nodes)
        {
            var ir = Convert(node, template, ref cursor, Array.Empty<string>());
            if (ir is not null) roots.Add(ir);
        }

        return roots;
    }

    /// <summary>
    /// One DOM node → one IrNode, recursively.
    ///
    /// <paramref name="cursor"/> walks forward through the original source so each
    /// node's span is found at or after the previous node's span — which is what
    /// makes repeated identical elements (`<li>` in a list) map to distinct spans
    /// instead of all matching the first one.
    /// </summary>
    /// <param name="inheritedVars">
    /// Template variables in scope from ancestors. Passed down so a node inside an
    /// *ngFor knows that `item` is local, not parent state.
    /// </param>
    private IrNode? Convert(INode node, string source, ref int cursor, IReadOnlyList<string> inheritedVars)
    {
        if (node.NodeType == NodeType.Text)
        {
            var text = node.TextContent;
            if (string.IsNullOrWhiteSpace(text)) return null;   // formatting whitespace

            var start = FindText(source, text, cursor);
            var end = start >= 0 ? start + text.Length : cursor;
            if (start >= 0) cursor = end;

            var bindings = ExpressionScanner.Interpolations(text)
                .Select(expr => new IrBinding(
                    BindingKind.Interpolation,
                    Name: "",
                    Expression: expr,
                    Identifiers: ExpressionScanner.FreeIdentifiers(expr)))
                .ToList();

            return new IrNode(
                Id: _nextId++,
                Tag: "#text",
                Bindings: bindings,
                Children: Array.Empty<IrNode>(),
                TemplateVariables: inheritedVars,
                SourceStart: Math.Max(start, 0),
                SourceEnd: end,
                TextContent: text);
        }

        if (node is not IElement element) return null;   // comments, PIs — not carried

        var id = _nextId++;
        var (start2, end2) = FindElementSpan(source, element, cursor);
        cursor = start2 >= 0 ? start2 + 1 : cursor;   // descend; children lie inside

        var bindings2 = ReadBindings(element, source, start2);

        // An *ngFor introduces names that are local to this subtree.
        var ownVars = bindings2
            .Where(b => b.Kind == BindingKind.StructuralFor)
            .SelectMany(b => ExpressionScanner.NgForVariables(b.Expression))
            .ToList();

        var scopeVars = ownVars.Count == 0
            ? inheritedVars
            : inheritedVars.Concat(ownVars).Distinct(StringComparer.Ordinal).ToList();

        var children = new List<IrNode>();
        var childCursor = start2 >= 0 ? start2 : cursor;

        foreach (var child in element.ChildNodes)
        {
            var ir = Convert(child, source, ref childCursor, scopeVars);
            if (ir is not null) children.Add(ir);
        }

        cursor = end2 > 0 ? end2 : childCursor;

        return new IrNode(
            Id: id,
            Tag: element.LocalName,
            Bindings: bindings2,
            Children: children,
            TemplateVariables: ownVars,
            SourceStart: Math.Max(start2, 0),
            SourceEnd: end2);
    }

    /// <summary>
    /// Every attribute on an element, classified into Angular binding kinds.
    ///
    /// The attribute NAME is recovered from the original source where possible,
    /// because AngleSharp lowercases names and Angular inputs are camelCase —
    /// emitting `[itemlabel]` instead of `[itemLabel]` would produce a child that
    /// silently never receives its input.
    /// </summary>
    private static List<IrBinding> ReadBindings(IElement element, string source, int elementStart)
    {
        var bindings = new List<IrBinding>();

        foreach (var attr in element.Attributes)
        {
            var rawName = RecoverAttributeCase(source, elementStart, attr.Name);
            var value = attr.Value;

            // *ngFor / *ngIf — structural directives.
            if (attr.Name.Equals(NgFor, StringComparison.OrdinalIgnoreCase))
            {
                var collection = ExpressionScanner.NgForCollection(value);
                bindings.Add(new IrBinding(
                    BindingKind.StructuralFor, rawName, value,
                    ExpressionScanner.FreeIdentifiers(collection)));
                continue;
            }

            if (attr.Name.Equals(NgIf, StringComparison.OrdinalIgnoreCase))
            {
                bindings.Add(new IrBinding(
                    BindingKind.StructuralIf, rawName, value,
                    ExpressionScanner.FreeIdentifiers(value)));
                continue;
            }

            // [(ngModel)] — banana-in-a-box, both directions at once.
            if (rawName.StartsWith("[(") && rawName.EndsWith(")]"))
            {
                bindings.Add(new IrBinding(
                    BindingKind.TwoWay, Trim(rawName, 2, 2), value,
                    ExpressionScanner.FreeIdentifiers(value)));
                continue;
            }

            // [prop]="expr" — data down.
            if (rawName.StartsWith('[') && rawName.EndsWith(']'))
            {
                bindings.Add(new IrBinding(
                    BindingKind.Property, Trim(rawName, 1, 1), value,
                    ExpressionScanner.FreeIdentifiers(value)));
                continue;
            }

            // (event)="handler()" — events up. Identifiers here are the CALLED
            // names, because that is what becomes an @Output on the child.
            if (rawName.StartsWith('(') && rawName.EndsWith(')'))
            {
                var called = ExpressionScanner.CalledIdentifiers(value);
                var read = ExpressionScanner.FreeIdentifiers(value);
                bindings.Add(new IrBinding(
                    BindingKind.Event, Trim(rawName, 1, 1), value,
                    called.Concat(read).Distinct(StringComparer.Ordinal).ToList()));
                continue;
            }

            // Angular's canonical long forms, accepted for completeness.
            if (rawName.StartsWith("bind-", StringComparison.Ordinal))
            {
                bindings.Add(new IrBinding(
                    BindingKind.Property, rawName[5..], value,
                    ExpressionScanner.FreeIdentifiers(value)));
                continue;
            }

            if (rawName.StartsWith("on-", StringComparison.Ordinal))
            {
                bindings.Add(new IrBinding(
                    BindingKind.Event, rawName[3..], value,
                    ExpressionScanner.CalledIdentifiers(value)));
                continue;
            }

            // A static attribute may still interpolate: class="a {{b}}".
            var interpolated = ExpressionScanner.Interpolations(value);
            bindings.Add(new IrBinding(
                BindingKind.Static, rawName, value,
                interpolated.SelectMany(ExpressionScanner.FreeIdentifiers)
                            .Distinct(StringComparer.Ordinal).ToList()));
        }

        return bindings;
    }

    private static string Trim(string s, int fromStart, int fromEnd) =>
        s[fromStart..^fromEnd];

    /// <summary>
    /// The original spelling of an attribute, found in the element's own source
    /// text. Falls back to AngleSharp's lowercased name when the source cannot be
    /// matched — a degraded name is better than a crash, and the validator will
    /// catch a binding that stops matching.
    /// </summary>
    private static string RecoverAttributeCase(string source, int elementStart, string lowerName)
    {
        if (elementStart < 0 || elementStart >= source.Length) return lowerName;

        var tagEnd = source.IndexOf('>', elementStart);
        if (tagEnd < 0) return lowerName;

        var openTag = source[elementStart..tagEnd];
        var idx = openTag.IndexOf(lowerName, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return lowerName;

        var original = openTag.Substring(idx, lowerName.Length);

        // Angular punctuation sits just outside the name AngleSharp reports for
        // some syntaxes; re-attach it by widening to the surrounding token.
        var left = idx;
        while (left > 0 && openTag[left - 1] is '[' or '(' or '*' or '#') left--;
        var right = idx + lowerName.Length;
        while (right < openTag.Length && openTag[right] is ']' or ')') right++;

        return left < idx || right > idx + lowerName.Length
            ? openTag[left..right]
            : original;
    }

    /// <summary>
    /// The exact [start, end) span of an element in the original source.
    ///
    /// Depth-aware: it counts nested open/close tags of the same name so a
    /// `<div>` containing `<div>`s ends at its OWN closing tag. Self-closing and
    /// void elements end at the first '>'.
    /// </summary>
    private static (int Start, int End) FindElementSpan(string source, IElement element, int from)
    {
        var tag = element.LocalName;
        var start = FindOpenTag(source, tag, from);
        if (start < 0) return (-1, -1);

        var openEnd = source.IndexOf('>', start);
        if (openEnd < 0) return (start, source.Length);

        // Self-closing (`<x />`) or a void element — no closing tag to find.
        if (source[openEnd - 1] == '/' || IsVoid(tag))
            return (start, openEnd + 1);

        var depth = 1;
        var i = openEnd + 1;

        while (i < source.Length && depth > 0)
        {
            var next = source.IndexOf('<', i);
            if (next < 0) break;

            if (IsTagAt(source, next, tag, closing: true))
            {
                depth--;
                if (depth == 0)
                {
                    var close = source.IndexOf('>', next);
                    return (start, close < 0 ? source.Length : close + 1);
                }
                i = next + 1;
            }
            else if (IsTagAt(source, next, tag, closing: false))
            {
                // A nested same-name element, unless it closes itself.
                var nestedEnd = source.IndexOf('>', next);
                if (nestedEnd > 0 && source[nestedEnd - 1] != '/') depth++;
                i = next + 1;
            }
            else i = next + 1;
        }

        return (start, source.Length);
    }

    /// <summary>Index of the next `<tag` at or after <paramref name="from"/>.</summary>
    private static int FindOpenTag(string source, string tag, int from)
    {
        var i = Math.Max(from, 0);
        while (i < source.Length)
        {
            var idx = source.IndexOf('<', i);
            if (idx < 0) return -1;
            if (IsTagAt(source, idx, tag, closing: false)) return idx;
            i = idx + 1;
        }
        return -1;
    }

    /// <summary>
    /// Whether position <paramref name="at"/> begins a tag named
    /// <paramref name="tag"/>. Requires a delimiter after the name so `<div`
    /// does not match `<divider`.
    /// </summary>
    private static bool IsTagAt(string source, int at, string tag, bool closing)
    {
        var offset = at + 1;
        if (closing)
        {
            if (offset >= source.Length || source[offset] != '/') return false;
            offset++;
        }

        if (offset + tag.Length > source.Length) return false;
        if (string.Compare(source, offset, tag, 0, tag.Length, StringComparison.OrdinalIgnoreCase) != 0)
            return false;

        var after = offset + tag.Length;
        if (after >= source.Length) return true;
        var c = source[after];
        return char.IsWhiteSpace(c) || c == '>' || c == '/';
    }

    /// <summary>Text nodes are found literally; whitespace differences are tolerated.</summary>
    private static int FindText(string source, string text, int from)
    {
        var idx = source.IndexOf(text, Math.Max(from, 0), StringComparison.Ordinal);
        if (idx >= 0) return idx;

        // AngleSharp may have decoded entities; fall back to the trimmed run.
        var trimmed = text.Trim();
        return trimmed.Length == 0
            ? -1
            : source.IndexOf(trimmed, Math.Max(from, 0), StringComparison.Ordinal);
    }

    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input",
        "link", "meta", "param", "source", "track", "wbr",
    };

    private static bool IsVoid(string tag) => VoidElements.Contains(tag);
}
