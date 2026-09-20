// Stage 3 — merge, joining the two parallel halves by candidateId.
//
// The join is the architecture made concrete: from the AI side we take exactly
// three strings (name, selector, rationale); from the facts side we take the
// template block, the inputs, the outputs and the wiring. Nothing crosses over.
//
// The merge is also where DISAGREEMENT surfaces. When the AI names a component
// whose facts contain an unresolvable binding, that component is FLAGGED and
// carried forward — never silently dropped. Dropping it would hide a real
// finding (the parser could not resolve something) behind an empty proposal,
// leaving the user to wonder why the block they expected never appeared.

using RabbitIC.Api.ComponentSplitter.Facts;
using RabbitIC.Api.ComponentSplitter.Ir;
using RabbitIC.Api.ComponentSplitter.Reasoning;

namespace RabbitIC.Api.ComponentSplitter.Proposal;

/// <summary>Why a proposed component needs a human's attention before generating.</summary>
/// <param name="Severity">"warning" blocks nothing; "blocker" disables Generate for it.</param>
/// <param name="Message">Reviewer-facing explanation.</param>
public record ProposalFlag(string Severity, string Message);

/// <summary>
/// One proposed child: AI naming joined to parser facts.
///
/// Field provenance is deliberately documented per-field, because this is the
/// record a future reader will check when asking "could the model have caused
/// this?". The answer is only ever yes for the three naming fields.
/// </summary>
/// <param name="CandidateId">The join key; also the IR node id.</param>
/// <param name="ComponentName">FROM AI (editable in review).</param>
/// <param name="Selector">FROM AI (editable in review).</param>
/// <param name="Rationale">FROM AI (display only).</param>
/// <param name="TemplateBlock">FROM PARSER — exact original markup.</param>
/// <param name="Inputs">FROM PARSER — authoritative.</param>
/// <param name="Outputs">FROM PARSER — authoritative.</param>
/// <param name="Wiring">FROM PARSER — authoritative.</param>
/// <param name="Origin">FROM PARSER.</param>
/// <param name="RepeatVariable">FROM PARSER.</param>
/// <param name="RepeatCollection">FROM PARSER.</param>
/// <param name="Flags">Disagreements and unresolved bindings.</param>
public record ProposedComponent(
    int CandidateId,
    string ComponentName,
    string Selector,
    string? Rationale,
    string TemplateBlock,
    IReadOnlyList<InputFact> Inputs,
    IReadOnlyList<OutputFact> Outputs,
    WiringKind Wiring,
    CandidateOrigin Origin,
    string? RepeatVariable,
    string? RepeatCollection,
    IReadOnlyList<ProposalFlag> Flags)
{
    /// <summary>True when something must be resolved before this one can generate.</summary>
    public bool HasBlocker => Flags.Any(f => f.Severity == "blocker");
}

/// <summary>
/// A candidate the AI declined to extract. Kept so the review UI can offer it
/// anyway — the human gate can overrule the model in both directions.
/// </summary>
/// <param name="CandidateId">The join key.</param>
/// <param name="Tag">Root element, for display.</param>
/// <param name="Origin">Why it was a candidate.</param>
/// <param name="NodeCount">Block size.</param>
/// <param name="Rationale">The model's reason for declining, when it gave one.</param>
public record DeclinedCandidate(
    int CandidateId, string Tag, CandidateOrigin Origin, int NodeCount, string? Rationale);

/// <summary>The merged Stage 3 output, as the review UI receives it.</summary>
/// <param name="ClassName">Parent component class.</param>
/// <param name="Selector">Parent selector.</param>
/// <param name="Wiring">Parent wiring, which children must match.</param>
/// <param name="UiKit">Detected UI kit, or null.</param>
/// <param name="TemplateIsInline">Whether the parent's template was inline.</param>
/// <param name="Components">Components proposed for extraction.</param>
/// <param name="Declined">Candidates the AI passed on.</param>
/// <param name="AiFailed">True when the AI stage could not be parsed.</param>
/// <param name="AiRawResponse">Raw model text, shown when AiFailed.</param>
/// <param name="AiError">Parse failure reason, shown when AiFailed.</param>
public record MergedProposal(
    string ClassName,
    string Selector,
    WiringKind Wiring,
    string? UiKit,
    bool TemplateIsInline,
    IReadOnlyList<ProposedComponent> Components,
    IReadOnlyList<DeclinedCandidate> Declined,
    bool AiFailed,
    string? AiRawResponse,
    string? AiError);

public static class ProposalMerger
{
    /// <summary>
    /// Joins AI decisions to structural facts by candidateId.
    ///
    /// A decision whose candidateId matches no candidate is ignored: the model
    /// invented an id, and there is no block to extract. A candidate with no
    /// decision is reported as declined rather than assumed.
    /// </summary>
    public static MergedProposal Merge(
        ComponentIr ir,
        IReadOnlyList<CandidateFacts> facts,
        AiReasoningResult ai)
    {
        var byId = ai.Decisions
            .GroupBy(d => d.CandidateId)
            .ToDictionary(g => g.Key, g => g.First());

        var components = new List<ProposedComponent>();
        var declined = new List<DeclinedCandidate>();

        foreach (var fact in facts)
        {
            if (!byId.TryGetValue(fact.CandidateId, out var decision) || !decision.Extract)
            {
                declined.Add(new DeclinedCandidate(
                    fact.CandidateId, fact.Tag, fact.Origin, fact.NodeCount, decision?.Rationale));
                continue;
            }

            components.Add(Join(fact, decision));
        }

        return new MergedProposal(
            ClassName: ir.ClassName,
            Selector: ir.Selector,
            Wiring: ir.Wiring,
            UiKit: ir.UiKit,
            TemplateIsInline: ir.TemplateIsInline,
            Components: components,
            Declined: declined,
            AiFailed: !ai.Succeeded,
            AiRawResponse: ai.Succeeded ? null : ai.RawResponse,
            AiError: ai.ParseError);
    }

    /// <summary>
    /// One join. Naming comes from the decision but is SANITISED — a model may
    /// return a selector with spaces or a lowercase class name, and a malformed
    /// identifier would produce code that does not parse. Sanitising a name is
    /// not overriding a fact: names are the AI's half, and this only makes them
    /// legal, never different in meaning.
    /// </summary>
    private static ProposedComponent Join(CandidateFacts fact, AiDecision decision)
    {
        var flags = new List<ProposalFlag>();

        // Unresolvable bindings are blockers: generating would produce a child
        // referencing something that exists nowhere.
        foreach (var u in fact.Unresolved)
            flags.Add(new ProposalFlag("blocker",
                $"{u.Reason} Resolve it in the original component, or remove this block " +
                "from the split."));

        var name = Naming.SanitiseClassName(decision.ComponentName, fact);
        if (!string.Equals(name, decision.ComponentName, StringComparison.Ordinal))
            flags.Add(new ProposalFlag("warning",
                $"The model proposed '{decision.ComponentName}', which is not a valid " +
                $"Angular class name. Using '{name}'."));

        var selector = Naming.SanitiseSelector(decision.Selector, name);
        if (decision.Selector is not null
            && !string.Equals(selector, decision.Selector, StringComparison.Ordinal))
            flags.Add(new ProposalFlag("warning",
                $"The model proposed selector '{decision.Selector}', which is not valid. " +
                $"Using '{selector}'."));

        return new ProposedComponent(
            CandidateId: fact.CandidateId,
            ComponentName: name,
            Selector: selector,
            Rationale: decision.Rationale,
            TemplateBlock: fact.TemplateBlock,
            Inputs: fact.Inputs,
            Outputs: fact.Outputs,
            Wiring: fact.Wiring,
            Origin: fact.Origin,
            RepeatVariable: fact.RepeatVariable,
            RepeatCollection: fact.RepeatCollection,
            Flags: flags);
    }
}

/// <summary>
/// Name legalisation. Shared with the review endpoint so a human's edit is held
/// to the same rules as the model's proposal.
/// </summary>
public static class Naming
{
    /// <summary>
    /// A legal PascalCase component class name. Falls back to a name derived
    /// from the block's own tag when nothing usable was proposed — derived from
    /// the FACTS, so the fallback never depends on model output.
    /// </summary>
    public static string SanitiseClassName(string? proposed, CandidateFacts fact)
    {
        var cleaned = new string((proposed ?? string.Empty)
            .Where(c => char.IsLetterOrDigit(c) || c == '_')
            .ToArray());

        if (cleaned.Length == 0 || char.IsDigit(cleaned[0]))
            cleaned = FallbackName(fact);

        cleaned = char.ToUpperInvariant(cleaned[0]) + cleaned[1..];

        return cleaned.EndsWith("Component", StringComparison.Ordinal)
            ? cleaned
            : cleaned + "Component";
    }

    /// <summary>A name from the block itself: `<li>` in an *ngFor → "ItemComponent".</summary>
    private static string FallbackName(CandidateFacts fact)
    {
        var basis = fact.RepeatVariable ?? fact.Tag;
        var letters = new string(basis.Where(char.IsLetter).ToArray());
        if (letters.Length == 0) letters = "Extracted";
        return char.ToUpperInvariant(letters[0]) + letters[1..];
    }

    /// <summary>
    /// A legal kebab-case selector. Derived from the class name when the proposed
    /// one is unusable, so a component always has a selector that matches its name.
    /// </summary>
    public static string SanitiseSelector(string? proposed, string className)
    {
        var candidate = (proposed ?? string.Empty).Trim().ToLowerInvariant();
        var cleaned = new string(candidate
            .Where(c => char.IsLetterOrDigit(c) || c == '-')
            .ToArray())
            .Trim('-');

        while (cleaned.Contains("--", StringComparison.Ordinal))
            cleaned = cleaned.Replace("--", "-");

        if (cleaned.Length == 0 || !char.IsLetter(cleaned[0]))
            cleaned = KebabFromClassName(className);

        return cleaned;
    }

    /// <summary>`ProductCardComponent` → `app-product-card`.</summary>
    public static string KebabFromClassName(string className)
    {
        var name = className.EndsWith("Component", StringComparison.Ordinal)
            ? className[..^"Component".Length]
            : className;

        var sb = new System.Text.StringBuilder("app");
        foreach (var c in name)
        {
            if (char.IsUpper(c)) sb.Append('-').Append(char.ToLowerInvariant(c));
            else if (char.IsLetterOrDigit(c)) sb.Append(c);
        }

        var result = sb.ToString().Trim('-');
        return result.Length <= 3 ? "app-extracted" : result;
    }
}
