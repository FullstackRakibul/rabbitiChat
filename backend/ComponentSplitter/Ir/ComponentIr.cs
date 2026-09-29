// The intermediate representation — the single source of structural truth.
//
// WHY THIS FILE IS THE WHOLE ARCHITECTURE:
// The core principle of the splitter is that the model can never override a
// structural fact. That is enforced here by TYPE, not by convention or review
// discipline. Every type in this file is produced exclusively by the parser
// (Stage 1) and the fact deriver (Stage 2a). The AI's output is a different
// type — AiDecision — which has exactly four fields, none of which is a binding,
// a template block, or a wiring target.
//
// So "the AI overrode a fact" is not a bug that can happen and be caught in
// review; it is a sentence that cannot be written in this codebase, because the
// generator's signature takes the IR and there is no field on AiDecision that
// could carry one.

namespace RabbitIC.Api.ComponentSplitter.Ir;

/// <summary>How a component is registered with Angular — decided by the parser.</summary>
public enum WiringKind
{
    /// <summary>`standalone: true` — children go in the parent's `imports` array.</summary>
    Standalone,

    /// <summary>Declared in an NgModule — children go in `declarations`.</summary>
    NgModule,
}

/// <summary>
/// One binding on an element, normalised across Angular's several syntaxes.
///
/// Angular writes the same concept many ways ([x]="y", bind-x="y", x="{{y}}");
/// they are all collapsed to a BindingKind here so downstream stages match on
/// meaning rather than on spelling.
/// </summary>
public enum BindingKind
{
    /// <summary>[prop]="expr" — data flowing down.</summary>
    Property,

    /// <summary>(event)="handler()" — events flowing up.</summary>
    Event,

    /// <summary>{{ expr }} in text content.</summary>
    Interpolation,

    /// <summary>*ngIf="expr".</summary>
    StructuralIf,

    /// <summary>*ngFor="let item of items" — the highest-value split boundary.</summary>
    StructuralFor,

    /// <summary>[(ngModel)]="x" — expands to both an input and an output.</summary>
    TwoWay,

    /// <summary>A plain static attribute; carried so the generator can reproduce markup exactly.</summary>
    Static,
}

/// <summary>
/// A single binding, with the free identifiers of its expression already
/// extracted. <paramref name="Identifiers"/> is what makes the data contract
/// derivable without any AI: an identifier that resolves to the parent's scope
/// is an @Input, one that resolves to a template variable is local.
/// </summary>
/// <param name="Kind">Normalised binding kind.</param>
/// <param name="Name">Property/event/attribute name, without the Angular punctuation.</param>
/// <param name="Expression">The raw right-hand side, as written.</param>
/// <param name="Identifiers">Free identifiers referenced by the expression.</param>
/// <param name="InferredType">Type when the .ts declared one; null when unknown.</param>
public record IrBinding(
    BindingKind Kind,
    string Name,
    string Expression,
    IReadOnlyList<string> Identifiers,
    string? InferredType = null);

/// <summary>
/// One node of the template tree.
///
/// Carries its exact source span so the generator can move the ORIGINAL markup
/// rather than re-serialising it. Re-serialisation is how a splitter silently
/// loses an attribute or reorders a class list; slicing the source cannot.
/// </summary>
/// <param name="Id">Stable id, assigned depth-first by the parser.</param>
/// <param name="Tag">Element name, or "#text" for a text node.</param>
/// <param name="Bindings">Every binding on this element.</param>
/// <param name="Children">Child nodes, in source order.</param>
/// <param name="TemplateVariables">
/// Names this node introduces into scope for its subtree — `let item` from an
/// *ngFor, plus its implicit index/first/last aliases. An identifier bound to
/// one of these is local and must NOT become an @Input.
/// </param>
/// <param name="SourceStart">Index into the original template of this node's first character.</param>
/// <param name="SourceEnd">Exclusive end index, so [Start..End) is the exact markup.</param>
/// <param name="TextContent">Raw text for a "#text" node; null for elements.</param>
public record IrNode(
    int Id,
    string Tag,
    IReadOnlyList<IrBinding> Bindings,
    IReadOnlyList<IrNode> Children,
    IReadOnlyList<string> TemplateVariables,
    int SourceStart,
    int SourceEnd,
    string? TextContent = null)
{
    public bool IsText => Tag == "#text";

    /// <summary>This node and every descendant, depth-first.</summary>
    public IEnumerable<IrNode> DescendantsAndSelf()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var node in child.DescendantsAndSelf())
                yield return node;
    }
}

/// <summary>
/// A member declared on the component class, from the .ts parse. Lets the fact
/// deriver distinguish a field (becomes an @Input) from a method (becomes an
/// @Output handler) — a distinction the template alone cannot make.
/// </summary>
/// <param name="Name">Member name as declared.</param>
/// <param name="IsMethod">True when declared as a method.</param>
/// <param name="DeclaredType">Type annotation when present.</param>
public record IrClassMember(string Name, bool IsMethod, string? DeclaredType);

/// <summary>
/// The parsed component: metadata, class members, and the template tree.
///
/// This is the ONLY input the code generator reads for structure. It is produced
/// before the AI is called and is never mutated afterwards.
/// </summary>
/// <param name="ClassName">The component class name.</param>
/// <param name="Selector">Its selector from the @Component decorator.</param>
/// <param name="Wiring">Standalone vs NgModule — detected, never guessed by AI.</param>
/// <param name="UiKit">Detected UI library ("angular-material", "primeng", …) or null.</param>
/// <param name="ExistingImports">`imports` array entries for a standalone component.</param>
/// <param name="TemplateSource">The exact template text every source span indexes into.</param>
/// <param name="TemplateIsInline">True when the template lived in the .ts as `template:`.</param>
/// <param name="Roots">Top-level template nodes.</param>
/// <param name="Members">Class members from the .ts.</param>
public record ComponentIr(
    string ClassName,
    string Selector,
    WiringKind Wiring,
    string? UiKit,
    IReadOnlyList<string> ExistingImports,
    string TemplateSource,
    bool TemplateIsInline,
    IReadOnlyList<IrNode> Roots,
    IReadOnlyList<IrClassMember> Members)
{
    /// <summary>Every node in the template, depth-first.</summary>
    public IEnumerable<IrNode> AllNodes() => Roots.SelectMany(r => r.DescendantsAndSelf());

    /// <summary>Look up a node by the id the candidate list refers to.</summary>
    public IrNode? NodeById(int id) => AllNodes().FirstOrDefault(n => n.Id == id);
}
