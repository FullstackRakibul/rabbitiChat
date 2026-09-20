// Recovering a JSON array from a model response.
//
// Separated from SplitReasoner so it depends on nothing but System.Text.Json:
// this is the part of the AI stage whose behaviour must be verifiable without a
// model, a network call, or the chat infrastructure behind it.

using System.Text.Json;

namespace RabbitIC.Api.ComponentSplitter.Reasoning;

/// <summary>
/// Pulling a JSON array out of a model response.
///
/// Models wrap JSON in markdown fences, prepend "Here is the JSON:", or emit a
/// single object instead of an array. All three are recoverable without relaxing
/// what we accept INTO the pipeline — every recovered decision is still validated
/// field by field.
/// </summary>
public static class JsonExtraction
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static (IReadOnlyList<AiDecision>? Decisions, string? Error) TryParseDecisions(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return (null, "the model returned an empty response");

        var json = Isolate(raw);
        if (json is null)
            return (null, "no JSON array was found in the response");

        try
        {
            var decisions = JsonSerializer.Deserialize<List<AiDecision>>(json, Options);
            if (decisions is null)
                return (null, "the JSON array was null");

            // Keep only well-formed decisions. A decision that says "extract" but
            // names nothing is not usable, and inventing a name here would put a
            // naming decision in the parser — the wrong half of the architecture.
            var valid = decisions
                .Where(d => !d.Extract || !string.IsNullOrWhiteSpace(d.ComponentName))
                .ToList();

            return (valid, null);
        }
        catch (JsonException ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>The outermost JSON array in the text, fences and prose stripped.</summary>
    private static string? Isolate(string raw)
    {
        var text = raw.Trim();

        // ```json … ``` fences.
        var fence = text.IndexOf("```", StringComparison.Ordinal);
        if (fence >= 0)
        {
            var start = text.IndexOf('\n', fence);
            var end = text.IndexOf("```", fence + 3, StringComparison.Ordinal);
            if (start > 0 && end > start) text = text[start..end].Trim();
        }

        var open = text.IndexOf('[');
        var close = text.LastIndexOf(']');

        if (open >= 0 && close > open) return text[open..(close + 1)];

        // A bare object — wrap it, so a model that emitted one decision still works.
        var objOpen = text.IndexOf('{');
        var objClose = text.LastIndexOf('}');
        if (objOpen >= 0 && objClose > objOpen) return "[" + text[objOpen..(objClose + 1)] + "]";

        return null;
    }
}
