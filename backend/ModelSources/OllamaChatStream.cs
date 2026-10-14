using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace RabbitIC.Api.ModelSources;

/// <summary>
/// Shared NDJSON streaming for both Ollama-protocol sources.
///
/// Local and cloud speak the identical /api/chat wire format, so the parsing
/// lives here once; the sources differ only in which client and model name they
/// hand in. Not an inheritance base — composition keeps each source free to
/// diverge later without dragging the other along.
/// </summary>
internal static class OllamaChatStream
{
    /// <summary>
    /// POSTs to <paramref name="path"/> and yields each delta — visible content,
    /// reasoning, or a tool call.
    /// <paramref name="path"/> is rooted ("/api/chat") and the client's
    /// BaseAddress is a bare origin — see the convention note in Program.cs.
    /// </summary>
    public static async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
        HttpClient client,
        string path,
        string model,
        IReadOnlyList<ChatTurn> messages,
        ILogger log,
        [EnumeratorCancellation] CancellationToken ct,
        IReadOnlyList<ToolDefinition>? tools = null,
        bool? think = null,
        int? numCtx = null,
        int? keepAlive = null,
        double? temperature = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(BuildPayload(model, messages, tools, think, numCtx, keepAlive, temperature)),
        };

        HttpResponseMessage resp;
        try
        {
            resp = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ChatSourceException($"Cannot reach {client.BaseAddress}: {ex.Message}", ex);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                // The status is logged with the resolved URL so a 404 (wrong path)
                // is distinguishable from a 401 (key/plan) at a glance. The body is
                // read for the log only — never returned, in case it echoes a token.
                var body = await SafeReadAsync(resp, ct);
                log.LogWarning(
                    "Chat upstream {Url} answered {Status}. Upstream detail: {Detail}",
                    request.RequestUri, (int)resp.StatusCode, Truncate(body, 300));

                throw new ChatSourceException(
                    $"Model provider returned {(int)resp.StatusCode}.");
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                // Parsing is separated from yielding because C# forbids `yield`
                // inside a try/catch — the JsonException guard has to stay in a
                // plain method.
                var events = ParseLine(line);
                foreach (var e in events) yield return e;
            }
        }
    }

    /// <summary>
    /// The request body. `think` is sent only when the caller asked for it, and
    /// the caller asks only for models that declare a thinking capability —
    /// sending it to a model that cannot reason is an upstream 400.
    /// </summary>
    /// <summary>Ollama takes a call's arguments as an object; anything unreadable goes back as {}.</summary>
    private static JsonElement ArgumentsObject(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : EmptyObject();
        }
        catch (JsonException)
        {
            return EmptyObject();
        }
    }

    private static JsonElement EmptyObject()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }

    private static object BuildPayload(
        string model, IReadOnlyList<ChatTurn> messages,
        IReadOnlyList<ToolDefinition>? tools, bool? think, int? numCtx, int? keepAlive, double? temperature)
    {
        var wireMessages = messages.Select(m => m.Role == "tool"
            // A tool result carries the name of the call it answers.
            ? (object)new { role = m.Role, content = m.Content, tool_name = m.ToolName ?? "" }
            // The assistant turn that asked for tools carries its calls.
            : m.ToolCalls is { Count: > 0 } calls
                ? new
                {
                    role = m.Role,
                    content = m.Content,
                    tool_calls = calls.Select(c => new { function = new { name = c.Name, arguments = ArgumentsObject(c.ArgumentsJson) } }),
                }
                : new { role = m.Role, content = m.Content });

        // Optional fields are left out, never sent as null: a model that cannot
        // reason answers 400 to `think` at all, and an unsent num_ctx keeps the
        // daemon's own window. Insertion order keeps the wire shape as it was.
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = wireMessages,
            ["stream"] = true,
        };
        if (think is bool wantThink) payload["think"] = wantThink;

        if (tools is { Count: > 0 })
        {
            payload["tools"] = tools.Select(t => new
            {
                type = "function",
                function = new
                {
                    name = t.Name,
                    description = t.Description,
                    // The schema is authored as JSON text, so it goes on the wire
                    // as a parsed node rather than a re-escaped string.
                    parameters = JsonDocument.Parse(t.ParametersJson).RootElement,
                },
            });
        }

        // Sent by the HR chat only, with the same values every time, so the model
        // that is already loaded is never reloaded for a different window.
        var options = new Dictionary<string, object>();
        if (numCtx is int ctx) options["num_ctx"] = ctx;
        if (temperature is double t) options["temperature"] = t;
        if (options.Count > 0) payload["options"] = options;
        if (keepAlive is int keep) payload["keep_alive"] = keep;

        return payload;
    }

    /// <summary>One NDJSON line to zero or more deltas. Unparsable lines are skipped.</summary>
    private static List<ChatStreamEvent> ParseLine(string line)
    {
        var events = new List<ChatStreamEvent>();

        try
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("message", out var msg)) return events;

            // Reasoning first: it always precedes the answer it explains.
            if (msg.TryGetProperty("thinking", out var thinking) &&
                thinking.ValueKind == JsonValueKind.String &&
                thinking.GetString() is { Length: > 0 } thinkingText)
            {
                events.Add(ChatStreamEvent.Thinking(thinkingText));
            }

            if (msg.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String &&
                content.GetString() is { Length: > 0 } contentText)
            {
                events.Add(ChatStreamEvent.Content(contentText));
            }

            if (msg.TryGetProperty("tool_calls", out var calls) &&
                calls.ValueKind == JsonValueKind.Array)
            {
                foreach (var call in calls.EnumerateArray())
                {
                    if (!call.TryGetProperty("function", out var fn)) continue;

                    var name = fn.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    // Arguments arrive as an object on Ollama and as a JSON string
                    // on OpenAI-compatible builds; normalise to raw JSON text.
                    var argsJson = "{}";
                    if (fn.TryGetProperty("arguments", out var argEl))
                    {
                        argsJson = argEl.ValueKind == JsonValueKind.String
                            ? argEl.GetString() ?? "{}"
                            : argEl.GetRawText();
                    }

                    events.Add(ChatStreamEvent.Tool(new ToolCallRequest(name!, argsJson)));
                }
            }
        }
        catch (JsonException)
        {
            // Keep-alive or partial line — nothing to emit.
        }

        return events;
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadAsStringAsync(ct); }
        catch { return ""; }
    }

    private static string Truncate(string s, int n) =>
        string.IsNullOrEmpty(s) ? "(empty)" : s.Length <= n ? s : s[..n] + "…";
}
