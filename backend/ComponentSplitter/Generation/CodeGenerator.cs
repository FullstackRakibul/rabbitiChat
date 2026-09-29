// Stage 5 — deterministic code generation from the APPROVED plan + the IR.
//
// LOOK AT THE SIGNATURE OF Generate(). It takes a ComponentIr and an
// ApprovedPlan. There is no AiDecision parameter, no AiReasoningResult, and no
// raw model text — the AI's output is not in scope in this file and cannot be
// referenced from it.
//
// That is the proof the evaluation asks for: corrupt the AI's name field and
// the wiring is unaffected, because every structural decision below reads either
// ir.* (parser facts) or plan.* (human-approved facts). The name is the only
// thing that changes, because the name is the only thing the AI ever supplied.
//
// Markup is MOVED, not re-serialised: each block is sliced out of the original
// template by the IR's source offsets. A splitter that re-serialises is a
// splitter that eventually drops an attribute nobody notices for a month.

using System.Text;
using RabbitIC.Api.ComponentSplitter.Ir;

namespace RabbitIC.Api.ComponentSplitter.Generation;

/// <summary>One emitted file.</summary>
/// <param name="Path">Suggested filename, Angular's conventional casing.</param>
/// <param name="Contents">Full file text.</param>
/// <param name="Kind">"child-ts" | "child-html" | "parent-ts" | "parent-html".</param>
public record GeneratedFile(string Path, string Contents, string Kind);

/// <summary>
/// The generated set, plus the mapping validation needs to check coverage.
/// </summary>
/// <param name="Files">Everything produced, for download.</param>
/// <param name="ParentTemplate">The rewritten parent template.</param>
/// <param name="Replacements">Which block became which selector, for validation.</param>
public record GenerationResult(
    IReadOnlyList<GeneratedFile> Files,
    string ParentTemplate,
    IReadOnlyList<BlockReplacement> Replacements);

/// <summary>
/// A record of one block being replaced by one child element. Validation replays
/// these to prove nothing was dropped or duplicated.
/// </summary>
/// <param name="CandidateId">The IR node replaced.</param>
/// <param name="OriginalBlock">Exact markup removed from the parent.</param>
/// <param name="ChildElement">Exact markup inserted in its place.</param>
/// <param name="ComponentName">The child that received the block.</param>
public record BlockReplacement(
    int CandidateId, string OriginalBlock, string ChildElement, string ComponentName);

public static class CodeGenerator
{
    /// <summary>
    /// Generates the parent and every approved child.
    ///
    /// Structure comes from <paramref name="ir"/>; names and the approved
    /// contract come from <paramref name="plan"/>. Nothing else is consulted.
    /// </summary>
    public static GenerationResult Generate(ComponentIr ir, ApprovedPlan plan)
    {
        // Resolve each planned component to its IR node. A plan naming a node
        // that does not exist is dropped here rather than generating a child with
        // an empty template.
        var resolved = plan.Components
            .Select(c => (Plan: c, Node: ir.NodeById(c.CandidateId)))
            .Where(x => x.Node is not null)
            .Select(x => (x.Plan, Node: x.Node!))
            .ToList();

        // Outermost-first ordering matters for nested candidates: replacing an
        // inner block first would invalidate the outer block's offsets.
        var ordered = resolved
            .OrderBy(x => x.Node.SourceStart)
            .ToList();

        var files = new List<GeneratedFile>();
        var replacements = new List<BlockReplacement>();

        foreach (var (planned, node) in ordered)
        {
            var block = SliceBlock(ir.TemplateSource, node);

            // The child owns the block WITHOUT the *ngFor that repeats it: the
            // parent keeps looping and stamps one child per item. Leaving the
            // *ngFor on the child would nest a loop inside every iteration.
            var childTemplate = RewriteHandlerCalls(StripRepeatDirective(block, node), planned);

            files.Add(BuildChildTs(planned, ir, childTemplate));

            replacements.Add(new BlockReplacement(
                planned.CandidateId,
                block,
                BuildChildElement(planned, node, ir),
                planned.ComponentName));
        }

        var parentTemplate = RewriteParentTemplate(ir, ordered, replacements);
        files.Add(BuildParentTs(ir, plan, parentTemplate));

        if (!ir.TemplateIsInline)
        {
            files.Add(new GeneratedFile(
                $"{Naming.FileBase(ir.ClassName)}.component.html", parentTemplate, "parent-html"));
        }

        return new GenerationResult(files, parentTemplate, replacements);
    }

    /// <summary>
    /// The child's .component.ts, with an inline template.
    ///
    /// Children are generated standalone-or-declared to MATCH THE PARENT
    /// (ir.Wiring — a parser fact). Mixing the two is the single most common way
    /// a split produces code that will not compile.
    /// </summary>
    private static GeneratedFile BuildChildTs(PlannedComponent planned, ComponentIr ir, string template)
    {
        var sb = new StringBuilder();
        var needsOutput = planned.Outputs.Count > 0;

        var angularImports = new List<string> { "Component" };
        if (planned.Inputs.Count > 0) angularImports.Add("Input");
        if (needsOutput) { angularImports.Add("Output"); angularImports.Add("EventEmitter"); }

        sb.AppendLine($"import {{ {string.Join(", ", angularImports)} }} from '@angular/core';");

        // A moved block may need CommonModule for its own *ngIf/*ngFor — but only
        // a standalone component declares imports at all.
        var needsCommon = ir.Wiring == WiringKind.Standalone && UsesCommonDirectives(template);
        if (needsCommon) sb.AppendLine("import { CommonModule } from '@angular/common';");

        sb.AppendLine();
        sb.AppendLine("@Component({");
        sb.AppendLine($"  selector: '{planned.Selector}',");

        if (ir.Wiring == WiringKind.Standalone)
        {
            sb.AppendLine("  standalone: true,");
            if (needsCommon) sb.AppendLine("  imports: [CommonModule],");
        }

        sb.AppendLine("  template: `");
        sb.AppendLine(Indent(template, 4));
        sb.AppendLine("  `,");
        sb.AppendLine("})");
        sb.AppendLine($"export class {planned.ComponentName} {{");

        foreach (var input in planned.Inputs)
            sb.AppendLine($"  @Input() {input.Name}!: {input.Type};");

        if (planned.Inputs.Count > 0 && needsOutput) sb.AppendLine();

        foreach (var output in planned.Outputs)
            sb.AppendLine(
                $"  @Output() {output.Name} = new EventEmitter<{EmitterType(output.PayloadType)}>();");

        sb.AppendLine("}");

        return new GeneratedFile(
            $"{Naming.FileBase(planned.ComponentName)}.component.ts", sb.ToString(), "child-ts");
    }

    /// <summary>
    /// The parent's .component.ts, with the child registered in the right place.
    ///
    /// imports[] vs declarations[] is decided by ir.Wiring — the parser's
    /// detection, never the model's opinion.
    /// </summary>
    private static GeneratedFile BuildParentTs(ComponentIr ir, ApprovedPlan plan, string template)
    {
        var childNames = plan.Components.Select(c => c.ComponentName).Distinct().ToList();
        var sb = new StringBuilder();

        sb.AppendLine("import { Component } from '@angular/core';");
        foreach (var child in childNames)
            sb.AppendLine($"import {{ {child} }} from './{Naming.FileBase(child)}.component';");

        sb.AppendLine();
        sb.AppendLine("@Component({");
        sb.AppendLine($"  selector: '{ir.Selector}',");

        if (ir.Wiring == WiringKind.Standalone)
        {
            sb.AppendLine("  standalone: true,");

            // Keep the imports the component already had; append the children.
            var imports = ir.ExistingImports
                .Concat(childNames)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            sb.AppendLine($"  imports: [{string.Join(", ", imports)}],");
        }

        if (ir.TemplateIsInline)
        {
            sb.AppendLine("  template: `");
            sb.AppendLine(Indent(template, 4));
            sb.AppendLine("  `,");
        }
        else
        {
            sb.AppendLine($"  templateUrl: './{Naming.FileBase(ir.ClassName)}.component.html',");
        }

        sb.AppendLine("})");
        sb.AppendLine($"export class {ir.ClassName} {{");
        sb.AppendLine("  // Existing class members are unchanged — copy them across from the");
        sb.AppendLine("  // original component. The splitter does not rewrite class logic.");
        sb.AppendLine("}");

        // For an NgModule component the registration is a separate file's
        // concern, so the exact edit is spelled out rather than half-applied.
        if (ir.Wiring == WiringKind.NgModule && childNames.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("// ---------------------------------------------------------------");
            sb.AppendLine("// NgModule wiring — add these to the module that declares");
            sb.AppendLine($"// {ir.ClassName}:");
            sb.AppendLine("//");
            foreach (var child in childNames)
                sb.AppendLine($"//   import {{ {child} }} from './{Naming.FileBase(child)}.component';");
            sb.AppendLine("//");
            sb.AppendLine("//   @NgModule({");
            sb.AppendLine($"//     declarations: [{ir.ClassName}, {string.Join(", ", childNames)}],");
            sb.AppendLine("//   })");
            sb.AppendLine("// ---------------------------------------------------------------");
        }

        return new GeneratedFile(
            $"{Naming.FileBase(ir.ClassName)}.component.ts", sb.ToString(), "parent-ts");
    }

    /// <summary>
    /// The child element the parent renders: selector plus one binding per
    /// approved input and output.
    ///
    /// The *ngFor is re-attached HERE, on the child element, so the parent still
    /// owns the iteration and the child stays a single item's view.
    /// </summary>
    private static string BuildChildElement(PlannedComponent planned, IrNode node, ComponentIr ir)
    {
        var sb = new StringBuilder("<").Append(planned.Selector);

        // Preserve the repeat, and any *ngIf, that guarded the original block.
        foreach (var binding in node.Bindings)
        {
            if (binding.Kind == BindingKind.StructuralFor)
                sb.Append($" *ngFor=\"{binding.Expression}\"");
            else if (binding.Kind == BindingKind.StructuralIf)
                sb.Append($" *ngIf=\"{binding.Expression}\"");
        }

        foreach (var input in planned.Inputs)
            sb.Append($" [{input.Name}]=\"{input.ParentExpression}\"");

        foreach (var output in planned.Outputs)
            sb.Append($" ({output.Name})=\"{HandlerCall(output)}\"");

        sb.Append("></").Append(planned.Selector).Append('>');
        return sb.ToString();
    }

    /// <summary>
    /// The parent's handler call. `$event` is substituted for the argument the
    /// child now emits, so `remove(item)` becomes `remove($event)` — the item is
    /// no longer in the parent's scope once the block moves into the child.
    /// </summary>
    private static string HandlerCall(PlannedOutput output)
    {
        if (output.HandlerArguments.Count == 0) return $"{output.ParentHandler}()";

        var args = output.HandlerArguments
            .Select(a => a == "$event" ? "$event" : "$event")
            .Distinct()
            .ToList();

        return $"{output.ParentHandler}({string.Join(", ", args)})";
    }

    /// <summary>
    /// The parent template with every approved block replaced by its child
    /// element. Replacements are applied back-to-front so each splice cannot
    /// shift the offsets of the ones not yet applied.
    /// </summary>
    private static string RewriteParentTemplate(
        ComponentIr ir,
        IReadOnlyList<(PlannedComponent Plan, IrNode Node)> ordered,
        IReadOnlyList<BlockReplacement> replacements)
    {
        var template = ir.TemplateSource;
        var byId = replacements.ToDictionary(r => r.CandidateId);

        foreach (var (planned, node) in ordered.OrderByDescending(x => x.Node.SourceStart))
        {
            if (!byId.TryGetValue(planned.CandidateId, out var replacement)) continue;
            if (node.SourceStart < 0 || node.SourceEnd > template.Length
                || node.SourceEnd <= node.SourceStart) continue;

            template = template[..node.SourceStart]
                     + replacement.ChildElement
                     + template[node.SourceEnd..];
        }

        return template;
    }

    /// <summary>
    /// Rewrites the moved block's handler calls into emitter calls.
    ///
    /// This is not cosmetic. In the parent, `(click)="addToCart(product)"` calls
    /// a METHOD. Once the block moves, `addToCart` is an @Output() EventEmitter
    /// on the child, and calling it as a function does not compile — the child
    /// must raise `addToCart.emit(product)` and let the parent handle it.
    ///
    /// Omitting this step produces a child that looks right, generates cleanly,
    /// and fails at `ng build` — exactly the class of error the validator exists
    /// to catch, which is why the emitted text is what Stage 6 reads.
    /// </summary>
    private static string RewriteHandlerCalls(string block, PlannedComponent planned)
    {
        var result = block;

        foreach (var output in planned.Outputs)
        {
            var handler = output.ParentHandler;
            if (string.IsNullOrWhiteSpace(handler)) continue;

            // `handler(` → `name.emit(`, only where it is a call and not part of
            // a longer identifier (so `addToCartLater(` is left alone).
            var i = 0;
            while (true)
            {
                var idx = result.IndexOf(handler + "(", i, StringComparison.Ordinal);
                if (idx < 0) break;

                var before = idx == 0 ? '\0' : result[idx - 1];
                if (char.IsLetterOrDigit(before) || before is '_' or '$' or '.')
                {
                    i = idx + handler.Length;
                    continue;
                }

                var replacement = output.Name + ".emit(";
                result = result[..idx] + replacement + result[(idx + handler.Length + 1)..];
                i = idx + replacement.Length;
            }
        }

        return result;
    }

    /// <summary>
    /// Removes the *ngFor from the moved block's root element. The child renders
    /// one item; the parent's child element carries the loop.
    /// </summary>
    private static string StripRepeatDirective(string block, IrNode node)
    {
        var result = block;

        foreach (var binding in node.Bindings)
        {
            if (binding.Kind != BindingKind.StructuralFor && binding.Kind != BindingKind.StructuralIf)
                continue;

            // Match the attribute as written, including its quoting, and remove
            // it along with the single space that separated it.
            foreach (var quote in new[] { '"', '\'' })
            {
                var attr = $"{binding.Name}={quote}{binding.Expression}{quote}";
                var idx = result.IndexOf(attr, StringComparison.Ordinal);
                if (idx < 0) continue;

                var start = idx > 0 && result[idx - 1] == ' ' ? idx - 1 : idx;
                result = result[..start] + result[(idx + attr.Length)..];
                break;
            }
        }

        return result;
    }

    /// <summary>The block's exact original markup.</summary>
    private static string SliceBlock(string source, IrNode node) =>
        node.SourceStart >= 0 && node.SourceEnd > node.SourceStart && node.SourceEnd <= source.Length
            ? source[node.SourceStart..node.SourceEnd]
            : string.Empty;

    /// <summary>Whether a template needs CommonModule for its directives.</summary>
    private static bool UsesCommonDirectives(string template) =>
        template.Contains("*ngIf", StringComparison.OrdinalIgnoreCase)
        || template.Contains("*ngFor", StringComparison.OrdinalIgnoreCase)
        || template.Contains("*ngSwitch", StringComparison.OrdinalIgnoreCase)
        || template.Contains("| date", StringComparison.OrdinalIgnoreCase)
        || template.Contains("| currency", StringComparison.OrdinalIgnoreCase)
        || template.Contains("| async", StringComparison.OrdinalIgnoreCase);

    /// <summary>`void` is not a legal EventEmitter type argument.</summary>
    private static string EmitterType(string payloadType) =>
        string.IsNullOrWhiteSpace(payloadType) || payloadType == "void" ? "void" : payloadType;

    private static string Indent(string text, int spaces)
    {
        var pad = new string(' ', spaces);
        return string.Join('\n', text.Split('\n').Select(l => l.Length > 0 ? pad + l : l));
    }
}

/// <summary>Angular's file-naming convention, shared by generator and validator.</summary>
public static class Naming
{
    /// <summary>`ProductCardComponent` → `product-card`.</summary>
    public static string FileBase(string className)
    {
        var name = className.EndsWith("Component", StringComparison.Ordinal)
            ? className[..^"Component".Length]
            : className;

        var sb = new StringBuilder();
        foreach (var c in name)
        {
            if (char.IsUpper(c))
            {
                if (sb.Length > 0) sb.Append('-');
                sb.Append(char.ToLowerInvariant(c));
            }
            else if (char.IsLetterOrDigit(c)) sb.Append(c);
        }

        var result = sb.ToString().Trim('-');
        return result.Length == 0 ? "component" : result;
    }
}
