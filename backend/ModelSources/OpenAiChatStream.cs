using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace RabbitIC.Api.ModelSources;

/// <summary>
/// How one OpenAI-compatible call is made. A config provider passes only the client (its base
/// address and key are the client's defaults, as before); a database provider passes the base
/// and key here, so they go on each request and never on a shared client.
/// </summary>
internal sealed record OpenAiCall(
    HttpClient Client,
    Uri? BaseUri = null,
    string? ApiKey = null,
    string? ProviderName = null,
    bool Stream = true,
    bool IncludeUsage = false,
    bool RetryTransient = false,
    double? Temperature = null,
    int? MaxTokens = null,
    Action<OpenAiCallOutcome>? OnDone = null);

/// <summary>What one call cost and how it ended: "ok", the HTTP status, "unreachable" or "cancelled".</summary>
public sealed record OpenAiCallOutcome(string Model, string Status, int PromptTokens, int CompletionTokens, string? RequestId);

/// <summary>
/// Server-sent-events streaming for the OpenAI chat-completions wire format.
///
/// The counterpart to <see cref="OllamaChatStream"/>: same job, different
/// protocol. Ollama streams bare NDJSON objects; OpenAI-compatible providers
/// stream "data: {json}" lines terminated by "data: [DONE]", and split a tool
/// call across many deltas that have to be reassembled before the call is whole.
///
/// "stream" is always sent explicitly: some providers (MangoMind) stream by default,
/// so leaving it out would hand an SSE body to a caller expecting one JSON object.
/// Nothing Ollama-only (think, keep_alive, options.num_ctx) is ever sent.
/// </summary>
internal static class OpenAiChatStream
{
    private const string DataPrefix = "data:";
    private const string DoneMarker = "[DONE]";

    /// <summary>Waits before retrying a 429, then a 502 (database providers only).</summary>
    private static readonly TimeSpan[] RateLimitWaits = { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15) };
    /// <summary>The same options JsonContent.Create used, so the wire format is unchanged.</summary>
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    private static readonly TimeSpan[] BadGatewayWaits = { TimeSpan.FromSeconds(1) };

    /// <summary>
    /// POSTs to {base}/chat/completions and yields deltas as they arrive (or, with
    /// Stream false, the one reply as the same events). The path is relative so it
    /// appends to the BaseUrl's version segment (".../api/v1" + "chat/completions").
    /// </summary>
    public static async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
        OpenAiCall call,
        string model,
        IReadOnlyList<ChatTurn> messages,
        ILogger log,
        [EnumeratorCancellation] CancellationToken ct,
        IReadOnlyList<ToolDefinition>? tools = null)
    {
        var usage = new UsageHolder();
        var status = "cancelled";
        string? requestId = null;
        try
        {
            HttpResponseMessage resp;
            try
            {
                (resp, requestId) = await SendAsync(call, model, messages, tools, log, ct);
            }
            catch (ChatSourceException ex)
            {
                status = ex.UpstreamStatus?.ToString() ?? "unreachable";
                throw;
            }

            using (resp)
            {
                if (!call.Stream)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    foreach (var e in ParseWhole(body, usage)) yield return e;
                    status = "ok";
                    yield break;
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

                    List<ChatStreamEvent> events;
                    try { events = ParseChunk(payload, pendingCalls, usage); }
                    catch (ChatSourceException) { status = "error"; throw; }
                    foreach (var e in events) yield return e;
                }

                foreach (var pending in pendingCalls.Values)
                {
                    if (pending.TryBuild() is { } built) yield return ChatStreamEvent.Tool(built);
                }
                status = "ok";
            }
        }
        finally
        {
            // Every call is reported, failures and abandoned streams included: a prepaid
            // budget is spent by tokens the provider counted, whether or not we used them.
            call.OnDone?.Invoke(new OpenAiCallOutcome(model, status, usage.Prompt, usage.Completion, requestId));
        }
    }

    /// <summary>Sends, retrying 429 and 502 for a database provider. Returns a successful response or throws.</summary>
    private static async Task<(HttpResponseMessage Response, string? RequestId)> SendAsync(
        OpenAiCall call, string model, IReadOnlyList<ChatTurn> messages, IReadOnlyList<ToolDefinition>? tools,
        ILogger log, CancellationToken ct)
    {
        int rateLimited = 0, badGateway = 0;
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                call.BaseUri is null ? new Uri("chat/completions", UriKind.Relative) : new Uri(call.BaseUri, "chat/completions"))
            {
                // Serialised up front so the body goes with a Content-Length: some gateways refuse a chunked request body.
                Content = new StringContent(JsonSerializer.Serialize(BuildPayload(call, model, messages, tools), WireJson),
                    Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrEmpty(call.ApiKey))
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", call.ApiKey);

            HttpResponseMessage resp;
            try
            {
                resp = await call.Client.SendAsync(request,
                    call.Stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, ct);
            }
            catch (HttpRequestException ex)
            {
                var where = call.ProviderName ?? call.BaseUri?.Host ?? call.Client.BaseAddress?.ToString();
                throw new ChatSourceException($"Cannot reach {where}: {ex.Message}", ex);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new ChatSourceException($"{call.ProviderName ?? "The model provider"} did not answer in time.", ex);
            }

            var requestId = HeaderRequestId(resp);
            if (resp.IsSuccessStatusCode) return (resp, requestId);

            var code = (int)resp.StatusCode;
            var body = await SafeReadAsync(resp, ct);
            requestId ??= BodyRequestId(body);
            resp.Dispose();

            // Logged with the resolved URL, status and the provider's request id, so a stale
            // model (400/404) is told apart from a key problem (401) and the call can be quoted
            // to the provider's support. The body never reaches the client — it can echo the key.
            log.LogWarning(
                "OpenAI-compatible upstream {Url} answered {Status} for model {Model} (request_id {RequestId}). Detail: {Detail}",
                request.RequestUri, code, model, requestId ?? "none", Truncate(body, 300));

            if (call.RetryTransient)
            {
                TimeSpan? wait = code == 429 && rateLimited < RateLimitWaits.Length ? RateLimitWaits[rateLimited++]
                               : code == 502 && badGateway < BadGatewayWaits.Length ? BadGatewayWaits[badGateway++]
                               : null;
                if (wait is { } w)
                {
                    await Task.Delay(w, ct);
                    continue;
                }
            }

            throw new ChatSourceException(DescribeFailure(code, model, call.ProviderName, ErrorMessage(body)))
            {
                UpstreamStatus = code,
            };
        }
    }

    /// <summary>Running token counts from a "usage" object, wherever the provider puts one.</summary>
    private sealed class UsageHolder
    {
        public int Prompt;
        public int Completion;

        public void Read(JsonElement root)
        {
            if (!root.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return;
            if (u.TryGetProperty("prompt_tokens", out var p) && p.TryGetInt32(out var pi)) Prompt = pi;
            if (u.TryGetProperty("completion_tokens", out var c) && c.TryGetInt32(out var ci)) Completion = ci;
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
    private static Dictionary<string, object?> BuildPayload(
        OpenAiCall call, string model, IReadOnlyList<ChatTurn> messages, IReadOnlyList<ToolDefinition>? tools)
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
            // The assistant turn that asked for tools carries its calls, with the
            // same name-as-id the tool results answer to.
            : m.ToolCalls is { Count: > 0 } calls
                ? new
                {
                    role = m.Role,
                    content = m.Content,
                    tool_calls = calls.Select(c => new
                    {
                        id = c.Name,
                        type = "function",
                        function = new { name = c.Name, arguments = c.ArgumentsJson },
                    }),
                }
                : new { role = m.Role, content = m.Content });

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = wireMessages,
            ["stream"] = call.Stream,
        };
        if (call.Stream && call.IncludeUsage) payload["stream_options"] = new { include_usage = true };
        if (call.Temperature is { } t) payload["temperature"] = t;
        if (call.MaxTokens is { } max) payload["max_tokens"] = max;

        if (tools is { Count: > 0 })
        {
            payload["tools"] = tools.Select(t => new
            {
                type = "function",
                function = new
                {
                    name = t.Name,
                    description = t.Description,
                    parameters = JsonDocument.Parse(t.ParametersJson).RootElement,
                },
            });
        }
        return payload;
    }

    /// <summary>A non-streamed reply ("stream": false) as the same events a stream would give.</summary>
    private static List<ChatStreamEvent> ParseWhole(string json, UsageHolder usage)
    {
        var events = new List<ChatStreamEvent>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException)
        {
            // An SSE body here means the provider ignored "stream": false.
            throw new ChatSourceException(json.TrimStart().StartsWith("data:", StringComparison.Ordinal)
                ? "The model provider streamed although a single reply was asked for."
                : "The model provider's reply was not JSON.");
        }

        using (doc)
        {
            var root = doc.RootElement;
            ThrowIfError(root);
            usage.Read(root);
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array) return events;

            foreach (var choice in choices.EnumerateArray())
            {
                if (!choice.TryGetProperty("message", out var message)) continue;
                foreach (var key in new[] { "reasoning", "reasoning_content" })
                {
                    if (message.TryGetProperty(key, out var r) && r.ValueKind == JsonValueKind.String && r.GetString() is { Length: > 0 } rt)
                        events.Add(ChatStreamEvent.Thinking(rt));
                }
                if (message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String && c.GetString() is { Length: > 0 } text)
                    events.Add(ChatStreamEvent.Content(text));
                if (message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
                {
                    var pending = new SortedDictionary<int, ToolCallBuilder>();
                    FoldToolCalls(calls, pending);
                    foreach (var p in pending.Values)
                        if (p.TryBuild() is { } built) events.Add(ChatStreamEvent.Tool(built));
                }
            }
        }
        return events;
    }

    /// <summary>
    /// One SSE data chunk to zero or more deltas. Tool-call fragments are folded
    /// into <paramref name="pendingCalls"/> rather than emitted, because a
    /// fragment is not yet a callable request.
    /// </summary>
    private static List<ChatStreamEvent> ParseChunk(
        string json, SortedDictionary<int, ToolCallBuilder> pendingCalls, UsageHolder usage)
    {
        var events = new List<ChatStreamEvent>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Some providers report a mid-stream failure as a chunk rather than a
            // status code; surface it instead of silently truncating the answer.
            ThrowIfError(root);

            // The final chunk carries the call's usage when the provider sends it.
            usage.Read(root);

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

    private static void ThrowIfError(JsonElement root)
    {
        if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
        {
            var message = err.TryGetProperty("message", out var m) ? m.GetString() : null;
            throw new ChatSourceException(
                string.IsNullOrWhiteSpace(message)
                    ? "The model provider reported an error."
                    : $"The model provider reported: {Truncate(message!, 200)}");
        }
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
    /// A database provider is named, and its 400 shows the provider's own message.
    /// </summary>
    internal static string DescribeFailure(int status, string model, string? provider = null, string? upstreamMessage = null)
    {
        var who = provider ?? "The model provider";
        return status switch
        {
            401 or 403 => provider is null
                ? "The model provider rejected this server's credentials."
                : $"{provider} rejected the API key — paste a new one in Settings → Model providers.",
            402 => $"{provider ?? "The model provider"} credit used up — top up.",
            429 => $"{who} is rate-limiting this account — try again shortly.",
            404 => $"'{model}' is unavailable at this provider — it may have been retired. Pick another model.",
            400 when provider is not null && !string.IsNullOrWhiteSpace(upstreamMessage) =>
                $"{provider} refused the request: {Truncate(upstreamMessage!, 200)}",
            400 => $"'{model}' is unavailable at this provider — it may have been retired. Pick another model.",
            502 => $"{who} could not reach '{model}' upstream (502).",
            _ => $"{who} returned {status}.",
        };
    }

    /// <summary>error.message (or message) from an error body; never the whole body.</summary>
    private static string? ErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("error", out var e))
            {
                if (e.ValueKind == JsonValueKind.String) return Observability.Redactor.Scrub(e.GetString() ?? "");
                if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                    return Observability.Redactor.Scrub(m.GetString() ?? "");
            }
            if (root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
                return Observability.Redactor.Scrub(msg.GetString() ?? "");
        }
        catch (JsonException) { }
        return null;
    }

    private static string? HeaderRequestId(HttpResponseMessage resp)
    {
        foreach (var name in new[] { "x-request-id", "request-id", "x-req-id" })
            if (resp.Headers.TryGetValues(name, out var v) && v.FirstOrDefault() is { Length: > 0 } id) return Truncate(id, 100);
        return null;
    }

    private static string? BodyRequestId(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("request_id", out var r) && r.ValueKind == JsonValueKind.String) return Truncate(r.GetString()!, 100);
            if (root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object
                && e.TryGetProperty("request_id", out var er) && er.ValueKind == JsonValueKind.String) return Truncate(er.GetString()!, 100);
        }
        catch (JsonException) { }
        return null;
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadAsStringAsync(ct); }
        catch { return ""; }
    }

    private static string Truncate(string s, int n) =>
        string.IsNullOrEmpty(s) ? "(empty)" : s.Length <= n ? s : s[..n] + "…";
}
