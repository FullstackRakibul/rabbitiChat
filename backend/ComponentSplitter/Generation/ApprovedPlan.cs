// Stage 4's output — what the human approved.
//
// The review UI sends this back. It carries the human's EDITS (renames, added or
// removed inputs, merges, rejections), not the AI's proposal: by the time a plan
// exists, the model's involvement is over.
//
// Note what a plan entry does NOT contain: a template block. The generator looks
// the block up in the IR by candidateId. That means a tampered or hand-written
// plan cannot inject markup — the worst it can do is name a candidate that does
// not exist, which the generator rejects.

using System.Text.Json.Serialization;

namespace RabbitIC.Api.ComponentSplitter.Generation;

/// <summary>An input on the approved plan, after any human edit.</summary>
/// <param name="Name">Property name on the child.</param>
/// <param name="Type">Type annotation to emit.</param>
/// <param name="ParentExpression">What the parent binds to it.</param>
public record PlannedInput(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("parentExpression")] string ParentExpression);

/// <summary>An output on the approved plan, after any human edit.</summary>
/// <param name="Name">Event name on the child.</param>
/// <param name="PayloadType">Emitted payload type.</param>
/// <param name="ParentHandler">Parent method to invoke.</param>
/// <param name="HandlerArguments">Arguments at the parent's call site.</param>
public record PlannedOutput(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("payloadType")] string PayloadType,
    [property: JsonPropertyName("parentHandler")] string ParentHandler,
    [property: JsonPropertyName("handlerArguments")] IReadOnlyList<string> HandlerArguments);

/// <summary>
/// One approved child component.
/// </summary>
/// <param name="CandidateId">
/// Which IR node's markup to move. The generator resolves the block from this —
/// the plan never carries markup itself.
/// </param>
/// <param name="ComponentName">Approved class name.</param>
/// <param name="Selector">Approved selector.</param>
/// <param name="Inputs">Approved inputs.</param>
/// <param name="Outputs">Approved outputs.</param>
public record PlannedComponent(
    [property: JsonPropertyName("candidateId")] int CandidateId,
    [property: JsonPropertyName("componentName")] string ComponentName,
    [property: JsonPropertyName("selector")] string Selector,
    [property: JsonPropertyName("inputs")] IReadOnlyList<PlannedInput> Inputs,
    [property: JsonPropertyName("outputs")] IReadOnlyList<PlannedOutput> Outputs);

/// <summary>
/// The full approved plan. Only components listed here are generated; a rejected
/// candidate is simply absent, which is why rejection needs no flag of its own.
/// </summary>
public record ApprovedPlan(
    [property: JsonPropertyName("components")] IReadOnlyList<PlannedComponent> Components);
