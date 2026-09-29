// Stage 2b — the AI's entire vocabulary.
//
// READ THIS BEFORE ADDING A FIELD.
//
// AiDecision has exactly four fields and none of them is structural. There is no
// Inputs, no Outputs, no TemplateBlock, no Wiring, and no Code. That is not an
// oversight to be filled in later — it is the mechanism by which "the model can
// never override a structural fact" is enforced.
//
// The generator's signature takes CandidateFacts for structure and AiDecision for
// naming. If a future change wants the model to influence a binding, it cannot be
// done by loosening a prompt or a review step: it would require adding a field
// here and then threading it into the generator, which is a visible, reviewable
// architectural change rather than a silent regression.
//
// So: adding a structural field to this record is the one edit that breaks the
// core guarantee of this module. Don't.

using System.Text.Json.Serialization;

namespace RabbitIC.Api.ComponentSplitter.Reasoning;

/// <summary>
/// One decision from the model: whether to extract a candidate, and what to call
/// it. Naming and selection only — see the file header.
/// </summary>
/// <param name="CandidateId">Join key back to <c>CandidateFacts.CandidateId</c>.</param>
/// <param name="Extract">Whether this block should become its own component.</param>
/// <param name="ComponentName">Proposed PascalCase class name.</param>
/// <param name="Selector">Proposed kebab-case selector.</param>
/// <param name="Rationale">One line explaining the choice, shown in review.</param>
public record AiDecision(
    [property: JsonPropertyName("candidateId")] int CandidateId,
    [property: JsonPropertyName("extract")] bool Extract,
    [property: JsonPropertyName("componentName")] string? ComponentName,
    [property: JsonPropertyName("selector")] string? Selector,
    [property: JsonPropertyName("rationale")] string? Rationale);

/// <summary>
/// The outcome of the AI stage, including the failure modes the UI must show.
/// A malformed response is surfaced rather than swallowed, so the user can pick
/// components manually instead of being handed a silently empty proposal.
/// </summary>
/// <param name="Decisions">Parsed decisions; empty when parsing failed.</param>
/// <param name="RawResponse">Exactly what the model emitted, for manual fallback.</param>
/// <param name="ParseError">Why parsing failed, or null on success.</param>
/// <param name="Retried">Whether the one permitted retry was used.</param>
public record AiReasoningResult(
    IReadOnlyList<AiDecision> Decisions,
    string RawResponse,
    string? ParseError,
    bool Retried)
{
    public bool Succeeded => ParseError is null;
}
