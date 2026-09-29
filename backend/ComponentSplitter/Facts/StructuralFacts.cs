// Stage 2a — structural facts. GROUND TRUTH, derived with no AI whatsoever.
//
// Everything in this file is a pure function of the IR. Given the same IR it
// returns byte-identical output on every run, which is the property the
// evaluation checks: no AI variance can reach a binding, a template block, or a
// wiring target, because no AI is reachable from here.
//
// THE DERIVATION RULE, stated once:
//   Walk the candidate's subtree and collect every identifier its expressions
//   reference. Classify each one:
//     - bound by a template variable in scope (`let item`)   → LOCAL, ignored
//     - a method on the component class                      → @Output
//     - anything else the class declares or the template reads → @Input
//   An identifier that is called AND not a class member is unresolvable: it is
//   reported, never guessed at, so Stage 3 can flag it instead of emitting a
//   child that will not compile.

using RabbitIC.Api.ComponentSplitter.Ir;
using RabbitIC.Api.ComponentSplitter.Parsing;

namespace RabbitIC.Api.ComponentSplitter.Facts;

/// <summary>An @Input() the child needs, with the expression the parent will pass.</summary>
/// <param name="Name">Property name on the child.</param>
/// <param name="Type">Declared/inferred type, or "any" when unknown.</param>
/// <param name="ParentExpression">What the parent binds to it.</param>
public record InputFact(string Name, string Type, string ParentExpression);

/// <summary>An @Output() the child emits, and the parent handler it maps to.</summary>
/// <param name="Name">Event name on the child.</param>
/// <param name="PayloadType">Emitted payload type, or "any".</param>
/// <param name="ParentHandler">The parent method invoked when it fires.</param>
/// <param name="HandlerArguments">Arguments the parent's call site passed.</param>
public record OutputFact(
    string Name, string PayloadType, string ParentHandler, IReadOnlyList<string> HandlerArguments);

/// <summary>
/// An identifier the candidate needs that could not be resolved to the parent's
/// class or to a template variable. Carried forward rather than dropped, so a
/// component the AI wants to extract is FLAGGED, not silently emitted broken.
/// </summary>
/// <param name="Identifier">The unresolved name.</param>
/// <param name="Reason">Why it could not be resolved, in reviewer-facing terms.</param>
public record UnresolvedBinding(string Identifier, string Reason);

/// <summary>Why a node was proposed as a split boundary.</summary>
public enum CandidateOrigin
{
    /// <summary>An *ngFor block — the highest-value extraction.</summary>
    RepeatedBlock,

    /// <summary>A large distinct sibling group.</summary>
    SiblingGroup,
}

/// <summary>
/// One candidate boundary plus its authoritative data contract.
///
/// The generator reads THIS for structure. The AI's matching decision supplies
/// only a name; it cannot supply anything in this record.
/// </summary>
/// <param name="CandidateId">Join key with the AI's decisions. The IR node id.</param>
/// <param name="Origin">Why this block was proposed.</param>
/// <param name="Tag">Root element of the block, for display.</param>
/// <param name="TemplateBlock">The block's exact original markup.</param>
/// <param name="Inputs">Required @Input()s — derived, authoritative.</param>
/// <param name="Outputs">Required @Output()s — derived, authoritative.</param>
/// <param name="Unresolved">Identifiers that resolve to nothing; flagged downstream.</param>
/// <param name="Wiring">Where a child must be registered. From the parser.</param>
/// <param name="NodeCount">Size of the block, used to rank candidates.</param>
/// <param name="RepeatVariable">The `let x` name when this is an *ngFor block.</param>
/// <param name="RepeatCollection">The iterated expression when this is an *ngFor block.</param>
public record CandidateFacts(
    int CandidateId,
    CandidateOrigin Origin,
    string Tag,
    string TemplateBlock,
    IReadOnlyList<InputFact> Inputs,
    IReadOnlyList<OutputFact> Outputs,
    IReadOnlyList<UnresolvedBinding> Unresolved,
    WiringKind Wiring,
    int NodeCount,
    string? RepeatVariable,
    string? RepeatCollection);

public static class StructuralFactDeriver
{
    /// <summary>
    /// A sibling group must be at least this many nodes to be worth extracting.
    /// Below it, a child component costs more than the duplication it removes.
    /// </summary>
    private const int MinimumSiblingGroupNodes = 4;

    /// <summary>
    /// Candidates, ordered: *ngFor blocks first (highest value), then larger
    /// sibling groups. Deterministic ordering, so two runs produce the same list
    /// in the same order.
    /// </summary>
    public static IReadOnlyList<CandidateFacts> Derive(ComponentIr ir)
    {
        var candidates = new List<CandidateFacts>();
        var claimed = new HashSet<int>();

        // ---- 1. Every *ngFor block ----
        // Highest value: a repeated block is a component by definition, and its
        // data contract is unusually clean because the loop variable names it.
        foreach (var node in ir.AllNodes())
        {
            if (node.IsText) continue;
            if (!node.Bindings.Any(b => b.Kind == BindingKind.StructuralFor)) continue;

            candidates.Add(BuildCandidate(ir, node, CandidateOrigin.RepeatedBlock));
            foreach (var d in node.DescendantsAndSelf()) claimed.Add(d.Id);
        }

        // ---- 2. Large sibling groups that do not overlap an existing candidate ----
        //
        // NON-OVERLAP IS A CORRECTNESS REQUIREMENT, not tidiness. Two candidates
        // that share markup can both be approved in review, and the generator
        // would then move the same block twice — the second splice would land on
        // offsets the first one invalidated.
        //
        // Overlap runs in BOTH directions, so both are excluded:
        //   - the node is inside a claimed block   (claimed contains its id)
        //   - the node CONTAINS a claimed block    (an *ngFor nested inside it)
        // The second is the one that bites: the outermost <div> wrapping an
        // *ngFor has no claimed ancestor, and checking only upward accepts it.
        foreach (var node in ir.AllNodes())
        {
            if (node.IsText || claimed.Contains(node.Id)) continue;

            var subtree = node.DescendantsAndSelf().ToList();
            if (subtree.Count < MinimumSiblingGroupNodes) continue;

            if (subtree.Any(d => claimed.Contains(d.Id))) continue;

            candidates.Add(BuildCandidate(ir, node, CandidateOrigin.SiblingGroup));
            foreach (var d in subtree) claimed.Add(d.Id);
        }

        return candidates
            .OrderBy(c => c.Origin == CandidateOrigin.RepeatedBlock ? 0 : 1)
            .ThenByDescending(c => c.NodeCount)
            .ThenBy(c => c.CandidateId)
            .ToList();
    }

    /// <summary>
    /// The data contract for one candidate. This is the function whose output the
    /// generator trusts absolutely.
    /// </summary>
    private static CandidateFacts BuildCandidate(ComponentIr ir, IrNode node, CandidateOrigin origin)
    {
        var subtree = node.DescendantsAndSelf().ToList();

        // Names bound by template variables anywhere in the block — the loop
        // variable and its aliases. These are LOCAL: passing one as an @Input
        // would be the classic splitter bug.
        var localNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in subtree)
        {
            foreach (var v in n.TemplateVariables) localNames.Add(v);
            foreach (var b in n.Bindings)
                if (b.Kind == BindingKind.StructuralFor)
                    foreach (var v in ExpressionScanner.NgForVariables(b.Expression))
                        localNames.Add(v);
        }

        var methods = ir.Members.Where(m => m.IsMethod)
            .ToDictionary(m => m.Name, m => m, StringComparer.Ordinal);
        var fields = ir.Members.Where(m => !m.IsMethod)
            .ToDictionary(m => m.Name, m => m, StringComparer.Ordinal);

        var inputs = new Dictionary<string, InputFact>(StringComparer.Ordinal);
        var outputs = new Dictionary<string, OutputFact>(StringComparer.Ordinal);
        var unresolved = new Dictionary<string, UnresolvedBinding>(StringComparer.Ordinal);

        var repeatVar = (string?)null;
        var repeatCollection = (string?)null;

        foreach (var n in subtree)
        {
            foreach (var binding in n.Bindings)
            {
                // The *ngFor on the candidate root stays with the PARENT: the
                // parent keeps looping and renders one child per item. Its
                // collection is therefore not an input to the child.
                if (binding.Kind == BindingKind.StructuralFor && n.Id == node.Id)
                {
                    repeatVar = ExpressionScanner.NgForVariables(binding.Expression).FirstOrDefault();
                    repeatCollection = ExpressionScanner.NgForCollection(binding.Expression);
                    continue;
                }

                // An event binding yields an @Output, named for the parent handler.
                if (binding.Kind == BindingKind.Event)
                {
                    foreach (var called in ExpressionScanner.CalledIdentifiers(binding.Expression))
                    {
                        if (localNames.Contains(called)) continue;

                        if (!methods.TryGetValue(called, out var method))
                        {
                            unresolved.TryAdd(called, new UnresolvedBinding(called,
                                $"'{called}()' is called in a ({binding.Name}) binding but is not " +
                                "a method on the component class."));
                            continue;
                        }

                        var args = CallArguments(binding.Expression, called);
                        outputs.TryAdd(called, new OutputFact(
                            Name: EventNameFor(called),
                            PayloadType: PayloadTypeFor(args, localNames, fields, repeatVar),
                            ParentHandler: called,
                            HandlerArguments: args));

                        _ = method;
                    }

                    // An event expression can also READ state (`disabled ? … : …`).
                    foreach (var id in binding.Identifiers)
                        ClassifyRead(id, binding, localNames, fields, methods, inputs, unresolved, repeatVar);

                    continue;
                }

                // Everything else is a read: property, interpolation, *ngIf,
                // two-way, or an interpolated static attribute.
                foreach (var id in binding.Identifiers)
                    ClassifyRead(id, binding, localNames, fields, methods, inputs, unresolved, repeatVar);
            }
        }

        // The loop variable itself becomes the child's primary @Input: each child
        // instance receives one item. Typed from the collection where possible.
        if (repeatVar is not null)
        {
            inputs[repeatVar] = new InputFact(
                Name: repeatVar,
                Type: ItemTypeFor(repeatCollection, fields),
                ParentExpression: repeatVar);
        }

        return new CandidateFacts(
            CandidateId: node.Id,
            Origin: origin,
            Tag: node.Tag,
            TemplateBlock: Slice(ir.TemplateSource, node),
            Inputs: inputs.Values.OrderBy(i => i.Name, StringComparer.Ordinal).ToList(),
            Outputs: outputs.Values.OrderBy(o => o.Name, StringComparer.Ordinal).ToList(),
            Unresolved: unresolved.Values.OrderBy(u => u.Identifier, StringComparer.Ordinal).ToList(),
            Wiring: ir.Wiring,
            NodeCount: subtree.Count,
            RepeatVariable: repeatVar,
            RepeatCollection: repeatCollection);
    }

    /// <summary>
    /// Classify one read identifier: local (ignored), class member (@Input), or
    /// unresolvable (flagged). Never guesses — an unknown name is reported.
    /// </summary>
    private static void ClassifyRead(
        string id,
        IrBinding binding,
        HashSet<string> localNames,
        IReadOnlyDictionary<string, IrClassMember> fields,
        IReadOnlyDictionary<string, IrClassMember> methods,
        IDictionary<string, InputFact> inputs,
        IDictionary<string, UnresolvedBinding> unresolved,
        string? repeatVar)
    {
        if (localNames.Contains(id)) return;          // template variable — stays local
        if (id == repeatVar) return;                  // added explicitly as the item input

        if (fields.TryGetValue(id, out var field))
        {
            inputs.TryAdd(id, new InputFact(
                Name: id,
                Type: field.DeclaredType ?? "any",
                ParentExpression: id));
            return;
        }

        // A method read without being called (passed as a reference) is still the
        // parent's behaviour, not data — treat as an output-shaped dependency.
        if (methods.ContainsKey(id)) return;

        unresolved.TryAdd(id, new UnresolvedBinding(id,
            $"'{id}' is used in a [{binding.Name}] binding but is neither a class member " +
            "nor a template variable in scope."));
    }

    /// <summary>
    /// Arguments at a call site, e.g. `remove(item, $event)` → ["item", "$event"].
    /// Used to type the @Output payload and to rebuild the parent's binding.
    /// </summary>
    private static IReadOnlyList<string> CallArguments(string expression, string method)
    {
        var idx = expression.IndexOf(method + "(", StringComparison.Ordinal);
        if (idx < 0)
        {
            idx = expression.IndexOf(method, StringComparison.Ordinal);
            if (idx < 0) return Array.Empty<string>();
            idx = expression.IndexOf('(', idx);
            if (idx < 0) return Array.Empty<string>();
        }
        else idx += method.Length;

        var open = expression.IndexOf('(', idx - 1);
        if (open < 0) return Array.Empty<string>();

        var depth = 0;
        for (var i = open; i < expression.Length; i++)
        {
            if (expression[i] == '(') depth++;
            else if (expression[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    var inner = expression[(open + 1)..i].Trim();
                    return inner.Length == 0
                        ? Array.Empty<string>()
                        : SplitArguments(inner);
                }
            }
        }

        return Array.Empty<string>();
    }

    /// <summary>Splits on top-level commas only, so `f(a, g(b, c))` yields two.</summary>
    private static IReadOnlyList<string> SplitArguments(string inner)
    {
        var args = new List<string>();
        var depth = 0;
        var start = 0;

        for (var i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == ',' && depth == 0)
            {
                args.Add(inner[start..i].Trim());
                start = i + 1;
            }
        }

        var last = inner[start..].Trim();
        if (last.Length > 0) args.Add(last);
        return args;
    }

    /// <summary>
    /// The @Output name for a parent handler: `removeItem` → "removeItem", but
    /// `onRemove` → "remove", since an output named `onRemove` would be bound as
    /// `(onRemove)` which reads wrong in Angular.
    /// </summary>
    private static string EventNameFor(string handler) =>
        handler.StartsWith("on", StringComparison.Ordinal)
            && handler.Length > 2
            && char.IsUpper(handler[2])
                ? char.ToLowerInvariant(handler[2]) + handler[3..]
                : handler;

    /// <summary>
    /// Payload type of an output, from its first argument. The loop variable's
    /// item type when it is passed, else the declared field type, else "any" —
    /// never a guess dressed up as knowledge.
    /// </summary>
    private static string PayloadTypeFor(
        IReadOnlyList<string> args,
        HashSet<string> localNames,
        IReadOnlyDictionary<string, IrClassMember> fields,
        string? repeatVar)
    {
        var first = args.FirstOrDefault(a => a != "$event");
        if (first is null) return args.Contains("$event") ? "Event" : "void";

        if (first == repeatVar || localNames.Contains(first)) return "any";
        return fields.TryGetValue(first, out var f) ? f.DeclaredType ?? "any" : "any";
    }

    /// <summary>
    /// The element type of an iterated collection: `items: Product[]` → "Product".
    /// Falls back to "any", which compiles and is honest about not knowing.
    /// </summary>
    private static string ItemTypeFor(string? collection, IReadOnlyDictionary<string, IrClassMember> fields)
    {
        if (collection is null) return "any";

        var root = ExpressionScanner.FreeIdentifiers(collection).FirstOrDefault();
        if (root is null || !fields.TryGetValue(root, out var field) || field.DeclaredType is null)
            return "any";

        var type = field.DeclaredType.Trim();
        if (type.EndsWith("[]", StringComparison.Ordinal)) return type[..^2].Trim();

        // Array<T> / Observable<T[]> — take the innermost type argument.
        var open = type.IndexOf('<');
        var close = type.LastIndexOf('>');
        if (open > 0 && close > open)
        {
            var arg = type[(open + 1)..close].Trim();
            return arg.EndsWith("[]", StringComparison.Ordinal) ? arg[..^2].Trim() : arg;
        }

        return "any";
    }

    /// <summary>The block's exact original markup — sliced, never re-serialised.</summary>
    private static string Slice(string source, IrNode node)
    {
        if (node.SourceStart < 0 || node.SourceEnd <= node.SourceStart
            || node.SourceEnd > source.Length)
            return string.Empty;

        return source[node.SourceStart..node.SourceEnd];
    }
}
