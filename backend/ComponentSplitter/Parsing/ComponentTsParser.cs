// Stage 1, TypeScript half — the @Component decorator and class body.
//
// Answers four questions, all of them GROUND TRUTH that the AI is never asked:
//   1. Is this Angular at all?          (reject clearly if not — v1 is Angular-only)
//   2. standalone or NgModule?          (decides imports[] vs declarations[])
//   3. Which UI kit is in play?         (so generated children import the right modules)
//   4. What members does the class have? (field vs method — an @Input vs an @Output)
//
// A brace-matching scan rather than a TypeScript parser: we need the decorator
// object and the member signatures, not semantics, and a .ts parser for .NET
// would be a far larger dependency than the HTML one.

using System.Text.RegularExpressions;
using RabbitIC.Api.ComponentSplitter.Ir;

namespace RabbitIC.Api.ComponentSplitter.Parsing;

/// <summary>Raised when the input is not an Angular component. v1 is Angular-only.</summary>
public sealed class NotAngularException : Exception
{
    public NotAngularException(string message) : base(message) { }
}

public static partial class ComponentTsParser
{
    [GeneratedRegex(@"@Component\s*\(", RegexOptions.Compiled)]
    private static partial Regex ComponentDecorator();

    [GeneratedRegex(@"export\s+class\s+([A-Za-z_$][\w$]*)", RegexOptions.Compiled)]
    private static partial Regex ClassDeclaration();

    [GeneratedRegex(@"selector\s*:\s*['""`]([^'""`]+)['""`]", RegexOptions.Compiled)]
    private static partial Regex SelectorProperty();

    [GeneratedRegex(@"standalone\s*:\s*(true|false)", RegexOptions.Compiled)]
    private static partial Regex StandaloneProperty();

    [GeneratedRegex(@"templateUrl\s*:\s*['""`]([^'""`]+)['""`]", RegexOptions.Compiled)]
    private static partial Regex TemplateUrlProperty();

    /// <summary>
    /// Class members: optional decorators, optional modifiers, a name, then
    /// either `(` for a method or `:`/`=`/`;` for a field.
    ///
    /// The decorator prefix is not optional polish. `@Input() heading: string`
    /// is the single most common way an Angular component declares a field, and
    /// a pattern that skips it reports `heading` as unknown — which, downstream,
    /// flags a perfectly valid block as having an unresolvable binding and
    /// blocks the split. Decorators may also carry arguments
    /// (`@Input({ required: true })`), so the argument list is matched too.
    ///
    /// Still line-anchored, so an identifier inside a method body is never
    /// mistaken for a member.
    /// </summary>
    [GeneratedRegex(
        @"^\s*(?:@[A-Za-z_$][\w$]*\s*(?:\([^)]*\))?\s*)*" +
        @"(?:(?:public|private|protected|readonly|static|async|override)\s+)*" +
        @"([A-Za-z_$][\w$]*)\s*(?:(\()|(?:[?!]?\s*:\s*([^;=\r\n]+))?\s*[;=])",
        RegexOptions.Compiled | RegexOptions.Multiline)]
    private static partial Regex ClassMember();

    /// <summary>
    /// Markers that identify a UI kit from the component's own imports. Order
    /// matters only for reporting — the first hit wins.
    /// </summary>
    private static readonly (string Marker, string Kit)[] UiKitMarkers =
    {
        ("@angular/material", "angular-material"),
        ("primeng", "primeng"),
        ("@ng-bootstrap", "ng-bootstrap"),
        ("ng-zorro-antd", "ng-zorro"),
        ("@progress/kendo-angular", "kendo"),
        ("@swimlane/ngx", "ngx"),
        ("@syncfusion/ej2-angular", "syncfusion"),
    };

    /// <summary>
    /// Parses the .ts source. Throws <see cref="NotAngularException"/> when the
    /// file is not an Angular component, which the endpoint maps to a 400 with
    /// the message shown to the user.
    /// </summary>
    /// <param name="ts">The component's TypeScript source.</param>
    /// <param name="separateTemplate">
    /// Template file contents when the component used templateUrl; null when the
    /// template is inline.
    /// </param>
    public static (string ClassName, string Selector, WiringKind Wiring, string? UiKit,
                   IReadOnlyList<string> Imports, string Template, bool IsInline,
                   IReadOnlyList<IrClassMember> Members)
        Parse(string ts, string? separateTemplate)
    {
        if (string.IsNullOrWhiteSpace(ts))
            throw new NotAngularException("No component source was provided.");

        if (!ComponentDecorator().IsMatch(ts))
            throw new NotAngularException(DescribeNonAngular(ts));

        var decorator = ExtractDecoratorBody(ts)
            ?? throw new NotAngularException(
                "Found an @Component decorator but could not read its metadata object. " +
                "Check that the decorator's braces are balanced.");

        var className = ClassDeclaration().Match(ts) is { Success: true } cm
            ? cm.Groups[1].Value
            : "UnnamedComponent";

        var selector = SelectorProperty().Match(decorator) is { Success: true } sm
            ? sm.Groups[1].Value
            : "app-unnamed";

        // Standalone detection. Explicit `standalone:` wins. With no such
        // property, the answer depends on the Angular version: v19+ defaults to
        // standalone, earlier defaults to NgModule. An `imports:` array is only
        // legal on a standalone component, so its presence is decisive.
        var wiring = DetectWiring(decorator);

        var template = ResolveTemplate(ts, decorator, separateTemplate, out var isInline);

        return (className, selector, wiring, DetectUiKit(ts),
                ExtractImportsArray(decorator), template, isInline, ExtractMembers(ts));
    }

    /// <summary>
    /// Why the input is not Angular, in terms the user can act on. A generic
    /// "not an Angular component" leaves someone who pasted a React file with no
    /// idea whether the tool is broken or the input is wrong.
    /// </summary>
    private static string DescribeNonAngular(string source)
    {
        if (source.Contains("<template", StringComparison.OrdinalIgnoreCase)
            || source.Contains("defineComponent", StringComparison.Ordinal)
            || source.Contains("<script setup", StringComparison.Ordinal))
            return "This looks like a Vue component. The splitter is Angular-only in v1 " +
                   "and does not convert between frameworks.";

        if (source.Contains("from 'react'", StringComparison.Ordinal)
            || source.Contains("from \"react\"", StringComparison.Ordinal)
            || Regex.IsMatch(source, @"export\s+default\s+function\s+[A-Z]"))
            return "This looks like a React component. The splitter is Angular-only in v1 " +
                   "and does not convert between frameworks.";

        if (source.Contains("@Injectable", StringComparison.Ordinal))
            return "This is an Angular service (@Injectable), not a component. " +
                   "The splitter works on components with templates.";

        if (source.Contains("@NgModule", StringComparison.Ordinal))
            return "This is an Angular NgModule, not a component. " +
                   "Paste the component you want to split instead.";

        return "No @Component decorator was found. The splitter needs an Angular " +
               "component (.ts with @Component), and is Angular-only in v1.";
    }

    /// <summary>
    /// Standalone vs NgModule, with the reasoning kept explicit because this
    /// single bit decides whether a generated child lands in `imports` or in
    /// `declarations` — a wrong answer produces code that does not compile.
    /// </summary>
    private static WiringKind DetectWiring(string decorator)
    {
        var explicitFlag = StandaloneProperty().Match(decorator);
        if (explicitFlag.Success)
            return explicitFlag.Groups[1].Value == "true" ? WiringKind.Standalone : WiringKind.NgModule;

        // `imports:` is only valid on a standalone component.
        return decorator.Contains("imports", StringComparison.Ordinal)
            ? WiringKind.Standalone
            : WiringKind.NgModule;
    }

    /// <summary>The template text, from templateUrl's file or the inline `template:`.</summary>
    private static string ResolveTemplate(
        string ts, string decorator, string? separateTemplate, out bool isInline)
    {
        if (!string.IsNullOrWhiteSpace(separateTemplate))
        {
            isInline = false;
            return separateTemplate;
        }

        var inline = ExtractInlineTemplate(decorator);
        if (inline is not null)
        {
            isInline = true;
            return inline;
        }

        isInline = false;

        var urlMatch = TemplateUrlProperty().Match(decorator);
        throw new NotAngularException(urlMatch.Success
            ? $"This component uses templateUrl: '{urlMatch.Groups[1].Value}'. " +
              "Paste that template file's contents in the template field as well."
            : "The component has neither an inline `template:` nor a `templateUrl:`. " +
              "There is nothing to split.");
    }

    /// <summary>
    /// The inline template's contents. Handles the backtick form (overwhelmingly
    /// the common one) and both quote forms, tracking escapes so a quote inside
    /// the markup does not end the string early.
    /// </summary>
    private static string? ExtractInlineTemplate(string decorator)
    {
        var idx = decorator.IndexOf("template", StringComparison.Ordinal);
        while (idx >= 0)
        {
            // Must be the `template:` key, not `templateUrl:`.
            var after = idx + "template".Length;
            var rest = decorator[after..].TrimStart();

            if (rest.StartsWith(':'))
            {
                var colonPos = decorator.IndexOf(':', after);
                var valueStart = colonPos + 1;
                while (valueStart < decorator.Length && char.IsWhiteSpace(decorator[valueStart])) valueStart++;

                if (valueStart < decorator.Length && decorator[valueStart] is '`' or '\'' or '"')
                {
                    var quote = decorator[valueStart];
                    var i = valueStart + 1;
                    var sb = new System.Text.StringBuilder();

                    while (i < decorator.Length)
                    {
                        if (decorator[i] == '\\' && i + 1 < decorator.Length)
                        {
                            sb.Append(decorator[i + 1]);
                            i += 2;
                            continue;
                        }
                        if (decorator[i] == quote) return sb.ToString();
                        sb.Append(decorator[i]);
                        i++;
                    }
                }
            }

            idx = decorator.IndexOf("template", idx + 1, StringComparison.Ordinal);
        }

        return null;
    }

    /// <summary>
    /// The text inside `@Component({ … })`, by brace matching. Skips braces that
    /// appear inside strings, so a `{` in a template literal cannot unbalance it.
    /// </summary>
    private static string? ExtractDecoratorBody(string ts)
    {
        var match = ComponentDecorator().Match(ts);
        if (!match.Success) return null;

        var open = ts.IndexOf('{', match.Index + match.Length);
        if (open < 0) return null;

        var depth = 0;
        var i = open;

        while (i < ts.Length)
        {
            var c = ts[i];

            if (c is '`' or '\'' or '"')
            {
                var quote = c;
                i++;
                while (i < ts.Length)
                {
                    if (ts[i] == '\\') { i += 2; continue; }
                    if (ts[i] == quote) break;
                    i++;
                }
                i++;
                continue;
            }

            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return ts[(open + 1)..i];
            }

            i++;
        }

        return null;
    }

    /// <summary>
    /// Entries of the decorator's `imports: [...]` array. Needed so a generated
    /// standalone parent keeps the imports it already had and the new child is
    /// appended rather than replacing them.
    /// </summary>
    private static IReadOnlyList<string> ExtractImportsArray(string decorator)
    {
        var idx = decorator.IndexOf("imports", StringComparison.Ordinal);
        if (idx < 0) return Array.Empty<string>();

        var open = decorator.IndexOf('[', idx);
        if (open < 0) return Array.Empty<string>();

        var close = decorator.IndexOf(']', open);
        if (close < 0) return Array.Empty<string>();

        return decorator[(open + 1)..close]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .ToList();
    }

    /// <summary>The UI kit, from the component's import statements, or null.</summary>
    private static string? DetectUiKit(string ts)
    {
        foreach (var (marker, kit) in UiKitMarkers)
            if (ts.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return kit;

        return null;
    }

    /// <summary>
    /// Class members, so the fact deriver can tell a field from a method.
    ///
    /// Scans only the class body and skips nested braces, because a local
    /// variable inside a method is not a member and must not be offered as an
    /// @Input.
    /// </summary>
    private static IReadOnlyList<IrClassMember> ExtractMembers(string ts)
    {
        var classMatch = ClassDeclaration().Match(ts);
        if (!classMatch.Success) return Array.Empty<IrClassMember>();

        var open = ts.IndexOf('{', classMatch.Index);
        if (open < 0) return Array.Empty<IrClassMember>();

        // Keep only depth-1 text: the class body with method bodies removed.
        var body = new System.Text.StringBuilder();
        var depth = 0;
        var i = open;

        while (i < ts.Length)
        {
            var c = ts[i];

            if (c is '`' or '\'' or '"')
            {
                var quote = c;
                i++;
                while (i < ts.Length)
                {
                    if (ts[i] == '\\') { i += 2; continue; }
                    if (ts[i] == quote) break;
                    i++;
                }
                i++;
                continue;
            }

            if (c == '{')
            {
                depth++;
                if (depth > 1) { i++; continue; }
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0) break;
                // Closing a method body — emit a separator so the next member
                // still starts on its own line for the line-anchored regex.
                if (depth == 1) body.Append('\n');
                i++;
                continue;
            }

            if (depth == 1) body.Append(c);
            i++;
        }

        var members = new List<IrClassMember>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match m in ClassMember().Matches(body.ToString()))
        {
            var name = m.Groups[1].Value;
            if (name is "constructor" or "return" or "if" or "for" or "while") continue;
            if (!seen.Add(name)) continue;

            var isMethod = m.Groups[2].Success;
            var type = m.Groups[3].Success ? m.Groups[3].Value.Trim() : null;
            members.Add(new IrClassMember(name, isMethod, type));
        }

        return members;
    }
}
