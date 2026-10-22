// One schema-forced /api/chat call, shared by every feature that asks a model
// for JSON of a fixed shape: PO Studio's extraction (LlmPoFallback) and Matrix's
// mapping suggestions. Moved here from LlmPoFallback unchanged, so there is one
// way to make such a call (docs/MATRIX_PROMPT.md §0 rule 8).
//
//   · `format` carries the JSON schema OBJECT, so Ollama constrains the output;
//   · thinking off, temperature 0;
//   · streamed on the "ollama-extract" client, which has no fixed timeout, so a
//     slow local model is not cut off and the caller's token still cancels it.

using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace RabbitIC.Api.ModelSources;

public sealed record SchemaJsonMessage(string Role, string Content);

public sealed record SchemaJsonAnswer(string? Content, int PromptTokens, int OutputTokens, string? Error);

public static class SchemaJsonChat
{
    /// <param name="keepAlive">How long Ollama keeps the model loaded, in seconds; null sends nothing (its default).</param>
    public static async Task<SchemaJsonAnswer> CallAsync(
        IHttpClientFactory http, string model, object schema, IEnumerable<SchemaJsonMessage> messages, int numCtx,
        CancellationToken ct, int? keepAlive = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["stream"] = true,
            ["format"] = schema,
            ["think"] = false,
            ["options"] = new { temperature = 0, num_ctx = numCtx },
            ["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
        };
        if (keepAlive is not null) payload["keep_alive"] = keepAlive;

        try
        {
            var client = http.CreateClient("ollama-extract");
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
            {
                Content = JsonContent.Create(payload),
            };
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
                return new(null, 0, 0, $"Ollama answered {(int)resp.StatusCode} for model '{model}'.");

            var content = new StringBuilder();
            int promptTokens = 0, outputTokens = 0;
            string? streamError = null;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonDocument frame;
                try { frame = JsonDocument.Parse(line); } catch { continue; }
                using (frame)
                {
                    var root = frame.RootElement;
                    if (root.TryGetProperty("error", out var err))
                    {
                        streamError = err.GetString();
                        break;
                    }
                    if (root.TryGetProperty("message", out var msg)
                        && msg.TryGetProperty("content", out var c)
                        && c.ValueKind == JsonValueKind.String)
                        content.Append(c.GetString());
                    if (root.TryGetProperty("prompt_eval_count", out var pe) && pe.TryGetInt32(out var p))
                        promptTokens = p;
                    if (root.TryGetProperty("eval_count", out var ec) && ec.TryGetInt32(out var o))
                        outputTokens = o;
                }
            }

            if (streamError is not null) return new(null, promptTokens, outputTokens, streamError);
            var text = content.ToString();
            return string.IsNullOrWhiteSpace(text)
                ? new(null, promptTokens, outputTokens, "The model returned an empty answer.")
                : new(text, promptTokens, outputTokens, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new(null, 0, 0, $"Could not reach the model: {ex.Message}");
        }
    }

    /// <summary>
    /// Has Ollama read a system message into its prompt cache now, so the call that later
    /// starts with the same message only reads what follows: one token is generated
    /// (num_predict 1), with the model, num_ctx, keep_alive and thinking the real call will use.
    /// Returns null, or why it did not happen.
    /// </summary>
    public static async Task<string?> WarmAsync(IHttpClientFactory http, string model, string system, int numCtx, int? keepAlive, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["stream"] = false,
            ["think"] = false,
            ["options"] = new { temperature = 0, num_ctx = numCtx, num_predict = 1 },
            ["messages"] = new[] { new { role = "system", content = system } },
        };
        if (keepAlive is not null) payload["keep_alive"] = keepAlive;
        try
        {
            var client = http.CreateClient("ollama-extract");
            using var resp = await client.PostAsJsonAsync("/api/chat", payload, ct);
            return resp.IsSuccessStatusCode ? null : $"Ollama answered {(int)resp.StatusCode} for model '{model}'.";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return $"Could not reach the model: {ex.Message}"; }
    }
}
