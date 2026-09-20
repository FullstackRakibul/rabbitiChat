// The pipeline conductor.
//
// Its job is sequencing and nothing else: every rule lives in the stage that
// owns it. The one thing this file is responsible for architecturally is that
// Stage 2a and Stage 2b are started TOGETHER from the same IR (see AnalyseAsync)
// — facts do not wait on the model, and the model never sees the facts.

using RabbitIC.Api.ComponentSplitter.Facts;
using RabbitIC.Api.ComponentSplitter.Generation;
using RabbitIC.Api.ComponentSplitter.Ir;
using RabbitIC.Api.ComponentSplitter.Parsing;
using RabbitIC.Api.ComponentSplitter.Proposal;
using RabbitIC.Api.ComponentSplitter.Reasoning;
using RabbitIC.Api.ComponentSplitter.Validation;

namespace RabbitIC.Api.ComponentSplitter;

public sealed class SplitterService
{
    private readonly SplitReasoner _reasoner;
    private readonly TemplateParser _templateParser = new();

    public SplitterService(SplitReasoner reasoner) => _reasoner = reasoner;

    /// <summary>
    /// Stage 1 — parse to IR. Throws <see cref="NotAngularException"/> for
    /// non-Angular input, which the endpoint turns into a 400.
    /// </summary>
    public ComponentIr Parse(string componentTs, string? templateHtml)
    {
        var (className, selector, wiring, uiKit, imports, template, isInline, members) =
            ComponentTsParser.Parse(componentTs, templateHtml);

        var roots = _templateParser.Parse(template);

        return new ComponentIr(
            ClassName: className,
            Selector: selector,
            Wiring: wiring,
            UiKit: uiKit,
            ExistingImports: imports,
            TemplateSource: template,
            TemplateIsInline: isInline,
            Roots: roots,
            Members: members);
    }

    /// <summary>Stage 2a alone — used by the endpoint that proves determinism.</summary>
    public static IReadOnlyList<CandidateFacts> DeriveFacts(ComponentIr ir) =>
        StructuralFactDeriver.Derive(ir);

    /// <summary>
    /// Stages 2a ‖ 2b, then 3.
    ///
    /// WHAT "PARALLEL" MEANS HERE, PRECISELY.
    /// Candidate ENUMERATION (which blocks exist, and their ids) has to happen
    /// before the model can be asked about them — the model answers per
    /// candidateId, so it needs the list. What runs in parallel is the expensive,
    /// architecturally significant half: DERIVING THE DATA CONTRACT. The
    /// inputs/outputs/wiring for every candidate are computed while the model
    /// streams, from the IR, on this thread's own task.
    ///
    /// The independence guarantee is therefore not "they start at the same
    /// microsecond" — it is that the model's prompt is built from the candidate
    /// list ALONE (see SplitReasoner.BuildPrompt, which is handed ids, markup and
    /// sizes, and never touches Inputs/Outputs/Wiring), and that the facts task
    /// receives nothing from the model. Neither half can read the other's output,
    /// which is the property the merge and the generator depend on.
    /// </summary>
    public async Task<MergedProposal> AnalyseAsync(
        ComponentIr ir,
        string model,
        string? source,
        Func<string, Task>? onAiDelta = null,
        CancellationToken ct = default)
    {
        // Enumerate boundaries. Cheap, pure, and the only thing the model needs.
        var facts = StructuralFactDeriver.Derive(ir);

        // Now the two halves run concurrently: the model reasons about selection
        // and naming while the contracts are re-derived independently of it.
        var factsTask = Task.Run(() => StructuralFactDeriver.Derive(ir), ct);
        var aiTask = _reasoner.ReasonAsync(ir, facts, model, source, onAiDelta, ct);

        await Task.WhenAll(factsTask, aiTask);

        // The merge uses the INDEPENDENTLY derived facts, not the list that was
        // shown to the model — so even the candidate summary the prompt saw
        // cannot be a path by which model output reaches the generator.
        return ProposalMerger.Merge(ir, await factsTask, await aiTask);
    }

    /// <summary>
    /// Stages 5 and 6. Generation and validation are separate calls on purpose —
    /// a caller cannot get a generated result without also getting the report,
    /// and the report is computed from the output rather than asserted by it.
    /// </summary>
    public static (GenerationResult Generated, ValidationReport Report) GenerateAndValidate(
        ComponentIr ir, ApprovedPlan plan)
    {
        var generated = CodeGenerator.Generate(ir, plan);
        var report = SplitValidator.Validate(ir, generated);
        return (generated, report);
    }
}
