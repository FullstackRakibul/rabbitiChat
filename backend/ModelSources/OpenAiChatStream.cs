using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace RabbitIC.Api.ModelSources;

/// <summary>
/// Server-sent-events streaming for the OpenAI chat-completions wire format.
///
/// The counterpart to <see cref="OllamaChatStream"/>: same job, different
/// protocol. Ollama streams bare NDJSON objects; OpenAI-compatible providers
/// stream "data: {json}" lines terminated by "data: [DONE]", and split a tool
/// call across many deltas that have to be reassembled before the call is whole.
/// </summary>
internal static class OpenAiChatStream
{
    private const string DataPrefix = "data:";
    private const string DoneMarker = "[DONE]";

    /// <summary>
    /// POSTs to {BaseAddress}/chat/completions and yields deltas as they arrive.
    /// The path is relative so it appends to the configured BaseUrl's version
    /// segment (".../api/v1" + "chat/completions") — unlike the Ollama clients,
    /// whose bare-origin bases take rooted paths.
    /// </summary>
    public static async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
        HttpClient client,
        string model,
        IReadOnlyList<ChatTurn> messages,
        ILogger log,
        [EnumeratorCancellation] CancellationToken ct,
        IReadOnlyList<ToolDefinition>? tools = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(BuildPayload(model, messages, tools)),
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
                var body = await SafeReadAsync(resp, ct);

                // Logged with the resolved URL and status so a stale model name
                // (400/404) is distinguishable from a key problem (401) at a
                // glance. The body never reaches the client — it can echo the key.
                log.LogWarning(
                    "OpenAI-compatible upstream {Url} answered {Status} for model {Model}. Detail: {Detail}",
                    request.RequestUri, (int)resp.StatusCode, model, Truncate(body, 300));

                throw new ChatSourceException(DescribeFailure(resp.StatusCode, model));
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            // Tool calls arrive in fragments keyed by index; assembled here and
            // emitted once the stream finishes, so the loop consuming them sees a
            // complete call rather than a half-built argument string.
            var pendingCalls = new SortedDictionary<int, ToolCallBuilder>();

            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                if (line.Length == 0) continue;                      // SSE frame separator
                if (!line.StartsWith(DataPrefix, StringComparison.Ordinal)) continue;

                var payload = line[DataPrefix.Length..].Trim();
                if (payload.Length == 0) continue;
                if (payload == DoneMarker) break;                    // clean end of stream

                foreach (var e in ParseChunk(payload, pendingCalls)) yield return e;
            }

            foreach (var call in pendingCalls.Values)
            {
                if (call.TryBuild() is { } built) yield return ChatStreamEvent.Tool(built);
            }
        }
    }

    /// <summary>Accumulates one tool call across the deltas that carry its pieces.</summary>
    private sealed class ToolCallBuilder
    {
        public string? Name { get; set; }
        public StringBuilder Arguments { get; } = new();

        public ToolCallRequest? TryBuild()
        {
            if (string.IsNullOrWhiteSpace(Name)) return null;

            // A call with no arguments streamed is still a valid call.
            var args = Arguments.Length == 0 ? "{}" : Arguments.ToString();
            return new ToolCallRequest(Name!, args);
        }
    }

    /// <summary>The request body in OpenAI chat-completions shape.</summary>
    private static object BuildPayload(
        string model, IReadOnlyList<ChatTurn> messages, IReadOnlyList<ToolDefinition>? tools)
    {
        // OpenAI matches a tool result to its call by tool_call_id; Ollama uses the
        // name. The shared ChatTurn carries the name, so it is sent as both the
        // role's name field and the id — providers accept the echo, and it keeps
        // one ChatTurn shape serving both protocols.
        var wireMessages = messages.Select(m => m.Role == "tool"
            ? (object)new
            {
                role = m.Role,
                content = m.Content,
                name = m.ToolName ?? "",
                tool_call_id = m.ToolName ?? "",
            }
            : new { role = m.Role, content = m.Content });

        if (tools is null || tools.Count == 0)
            return new { model, messages = wireMessages, stream = true };

        return new
        {
            model,
            messages = wireMessages,
            stream = true,
            tools = tools.Select(t => new
            {
                type = "function",
                function = new
                {
                    name = t.Name,
                    description = t.Description,
                    parameters = JsonDocument.Parse(t.ParametersJson).RootElement,
                },
            }),
        };
    }

    /// <summary>
    /// One SSE data chunk to zero or more deltas. Tool-call fragments are folded
    /// into <paramref name="pendingCalls"/> rather than emitted, because a
    /// fragment is not yet a callable request.
    /// </summary>
    private static List<ChatStreamEvent> ParseChunk(
        string json, SortedDictionary<int, ToolCallBuilder> pendingCalls)
    {
        var events = new List<ChatStreamEvent>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Some providers report a mid-stream failure as a chunk rather than a
            // status code; surface it instead of silently truncating the answer.
            if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
            {
                var message = err.TryGetProperty("message", out var m) ? m.GetString() : null;
                throw new ChatSourceException(
                    string.IsNullOrWhiteSpace(message)
                        ? "The model provider reported an error."
                        : $"The model provider reported: {Truncate(message!, 200)}");
            }

            if (!root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array)
            {
                return events;
            }

            foreach (var choice in choices.EnumerateArray())
            {
                if (!choice.TryGetProperty("delta", out var delta)) continue;

                // Reasoning models expose scratch work under a provider-specific
                // key; both spellings seen in the wild map to the same event.
                foreach (var key in new[] { "reasoning", "reasoning_content" })
                {
                    if (delta.TryGetProperty(key, out var reasoning) &&
                        reasoning.ValueKind == JsonValueKind.String &&
                        reasoning.GetString() is { Length: > 0 } reasoningText)
                    {
                        events.Add(ChatStreamEvent.Thinking(reasoningText));
                    }
                }

                if (delta.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.String &&
                    content.GetString() is { Length: > 0 } contentText)
                {
                    events.Add(ChatStreamEvent.Content(contentText));
                }

                if (delta.TryGetProperty("tool_calls", out var calls) &&
                    calls.ValueKind == JsonValueKind.Array)
                {
                    FoldToolCalls(calls, pendingCalls);
                }
            }
        }
        catch (JsonException)
        {
            // A partial or non-JSON keep-alive line — nothing to emit.
        }

        return events;
    }

    /// <summary>Merges tool-call fragments into their builders, keyed by delta index.</summary>
    private static void FoldToolCalls(
        JsonElement calls, SortedDictionary<int, ToolCallBuilder> pendingCalls)
    {
        var fallbackIndex = 0;

        foreach (var call in calls.EnumerateArray())
        {
            // The index ties fragments of the same call together across chunks.
            var index = call.TryGetProperty("index", out var idx) && idx.TryGetInt32(out var i)
                ? i
                : fallbackIndex;
            fallbackIndex = index + 1;

            if (!pendingCalls.TryGetValue(index, out var builder))
            {
                builder = new ToolCallBuilder();
                pendingCalls[index] = builder;
            }

            if (!call.TryGetProperty("function", out var fn)) continue;

            if (fn.TryGetProperty("name", out var name) &&
                name.ValueKind == JsonValueKind.String &&
                name.GetString() is { Length: > 0 } nameText)
            {
                // The name arrives once, in the first fragment.
                builder.Name ??= nameText;
            }

            if (fn.TryGetProperty("arguments", out var args) &&
                args.ValueKind == JsonValueKind.String &&
                args.GetString() is { Length: > 0 } argsText)
            {
                builder.Arguments.Append(argsText);
            }
        }
    }

    /// <summary>
    /// Client-facing text for an upstream failure. A stale model name is the most
    /// likely cause of a 400/404 here — free tiers rotate — so it is named
    /// explicitly, which lets the user fall back to local instead of guessing.
    /// </summary>
    private static string DescribeFailure(System.Net.HttpStatusCode status, string model) => status switch
    {
        System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden =>
            "The model provider rejected this server's credentials.",
        System.Net.HttpStatusCode.TooManyRequests =>
            "The model provider is rate-limiting this account — try again shortly.",
        System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.BadRequest =>
            $"'{model}' is unavailable at this provider — it may have been retired. Pick another model.",
        _ => $"The model provider returned {(int)status}.",
    };

    private static async Task<string> SafeReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadAsStringAsync(ct); }
        catch { return ""; }
    }

    private static string Truncate(string s, int n) =>
        string.IsNullOrEmpty(s) ? "(empty)" : s.Length <= n ? s : s[..n] + "…";
}
