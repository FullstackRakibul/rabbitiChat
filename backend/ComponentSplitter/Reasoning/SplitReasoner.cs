// Stage 2b — asking the model which blocks to extract, and what to call them.
//
// Runs in PARALLEL with Stage 2a, from the same IR. It is given the candidate
// list and a summary of each block, and is asked for selection + naming only.
// It is never shown a place to put an @Input, because it is never asked for one.
//
// Streams through the existing ChatService, so every configured model source
// works here with no new transport and no source named in this file.

using System.Text;
using RabbitIC.Api.ComponentSplitter.Facts;
using RabbitIC.Api.ComponentSplitter.Ir;
using RabbitIC.Api.ModelSources;
using RabbitIC.Api.Services;

namespace RabbitIC.Api.ComponentSplitter.Reasoning;

public sealed class SplitReasoner
{
    /// <summary>
    /// The scope limit, stated to the model in the terms the architecture needs.
    /// It says plainly that inputs/outputs are computed elsewhere — a model that
    /// volunteers them anyway is simply ignored by the parser below, but telling
    /// it not to bother keeps the responses short and on-task.
    /// </summary>
    private const string SystemPrompt =
        "You are helping split a large Angular component into smaller reusable ones.\n\n" +
        "Given a parsed Angular component and its candidate blocks, decide WHICH blocks " +
        "should become reusable child components and propose names for them.\n\n" +
        "You are NOT responsible for @Input/@Output lists or module wiring — those are " +
        "computed separately by a deterministic parser and will override anything you say " +
        "about them. Do not emit code. Do not emit templates.\n\n" +
        "For each candidate decide:\n" +
        "  - extract: true if it is worth its own component, false to keep it inline\n" +
        "  - componentName: PascalCase class name ending in 'Component'\n" +
        "  - selector: kebab-case, prefixed 'app-'\n" +
        "  - rationale: ONE short line on why\n\n" +
        "Prefer extracting repeated (*ngFor) blocks. Do not extract trivial wrappers.\n\n" +
        "Output STRICT JSON ONLY — a single array, no prose, no markdown fences:\n" +
        "[{\"candidateId\":0,\"extract\":true,\"componentName\":\"ProductCardComponent\"," +
        "\"selector\":\"app-product-card\",\"rationale\":\"Repeated product row.\"}]";

    private readonly ChatService _chat;
    private readonly ILogger<SplitReasoner> _log;

    public SplitReasoner(ChatService chat, ILogger<SplitReasoner> log)
    {
        _chat = chat;
        _log = log;
    }

    /// <summary>
    /// Asks the model to choose and name components.
    /// </summary>
    /// <param name="onDelta">
    /// Called with each content chunk so the UI can stream the AI stage. Thinking
    /// deltas are passed through separately by the caller's writer.
    /// </param>
    public async Task<AiReasoningResult> ReasonAsync(
        ComponentIr ir,
        IReadOnlyList<CandidateFacts> candidates,
        string model,
        string? source,
        Func<string, Task>? onDelta = null,
        CancellationToken ct = default)
    {
        var prompt = BuildPrompt(ir, candidates);

        var first = await StreamOnceAsync(prompt, model, source, onDelta, ct);
        var parsed = JsonExtraction.TryParseDecisions(first);
        if (parsed.Decisions is not null)
            return new AiReasoningResult(parsed.Decisions, first, null, Retried: false);

        // One retry, with the failure named. A model that emitted prose usually
        // complies when told exactly what was wrong with its last answer.
        _log.LogInformation("Splitter reasoning returned malformed JSON; retrying once.");

        var retryPrompt = prompt +
            "\n\nYour previous response could not be parsed as JSON (" + parsed.Error + "). " +
            "Respond with ONLY the JSON array. No explanation, no markdown fences.";

        var second = await StreamOnceAsync(retryPrompt, model, source, onDelta, ct);
        var reparsed = JsonExtraction.TryParseDecisions(second);

        return reparsed.Decisions is not null
            ? new AiReasoningResult(reparsed.Decisions, second, null, Retried: true)
            // Still malformed: surface the raw text so the UI can offer manual
            // selection rather than pretending there were no candidates.
            : new AiReasoningResult(Array.Empty<AiDecision>(), second, reparsed.Error, Retried: true);
    }

    private async Task<string> StreamOnceAsync(
        string prompt, string model, string? source, Func<string, Task>? onDelta, CancellationToken ct)
    {
        var messages = new List<ChatTurn>
        {
            new("system", SystemPrompt),
            new("user", prompt),
        };

        var sb = new StringBuilder();

        await foreach (var ev in _chat.StreamAsync(source, new ChatArgs(model, messages), ct))
        {
            if (ev.Kind != ChatEventKind.Content) continue;
            sb.Append(ev.Text);
            if (onDelta is not null) await onDelta(ev.Text);
        }

        return sb.ToString();
    }

    /// <summary>
    /// What the model sees: component context and one entry per candidate.
    ///
    /// Each candidate shows its markup (truncated) and its shape. It deliberately
    /// does NOT show the derived inputs/outputs — the model has no use for them
    /// and showing them would invite it to edit them.
    /// </summary>
    private static string BuildPrompt(ComponentIr ir, IReadOnlyList<CandidateFacts> candidates)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"Component: {ir.ClassName} (selector: {ir.Selector})");
        sb.AppendLine($"Wiring: {(ir.Wiring == WiringKind.Standalone ? "standalone" : "NgModule")}");
        if (ir.UiKit is not null) sb.AppendLine($"UI kit: {ir.UiKit}");
        sb.AppendLine($"Template size: {ir.AllNodes().Count()} nodes");
        sb.AppendLine();
        sb.AppendLine($"CANDIDATE BLOCKS ({candidates.Count}):");
        sb.AppendLine();

        foreach (var c in candidates)
        {
            sb.AppendLine($"--- candidateId: {c.CandidateId} ---");
            sb.AppendLine($"root element: <{c.Tag}>");
            sb.AppendLine($"kind: {(c.Origin == CandidateOrigin.RepeatedBlock
                ? "repeated block (*ngFor)" : "sibling group")}");
            sb.AppendLine($"size: {c.NodeCount} nodes");
            if (c.RepeatCollection is not null)
                sb.AppendLine($"iterates: {c.RepeatCollection}");
            sb.AppendLine("markup:");
            sb.AppendLine(Truncate(c.TemplateBlock, 900));
            sb.AppendLine();
        }

        sb.AppendLine("Respond with the JSON array only.");
        return sb.ToString();
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "\n… (truncated)";
}
