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
    /// POSTs to <paramref name="path"/> and yields each content delta.
    /// <paramref name="path"/> is rooted ("/api/chat") and the client's
    /// BaseAddress is a bare origin — see the convention note in Program.cs.
    /// </summary>
    public static async IAsyncEnumerable<string> StreamAsync(
        HttpClient client,
        string path,
        string model,
        IReadOnlyList<ChatTurn> messages,
        ILogger log,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var payload = new
        {
            model,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }),
            stream = true,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(payload),
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

                string? token = null;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("message", out var msg) &&
                        msg.TryGetProperty("content", out var content))
                    {
                        token = content.GetString();
                    }
                }
                catch (JsonException) { continue; }   // keep-alive or partial line

                if (!string.IsNullOrEmpty(token)) yield return token;
            }
        }
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadAsStringAsync(ct); }
        catch { return ""; }
    }

    private static string Truncate(string s, int n) =>
        string.IsNullOrEmpty(s) ? "(empty)" : s.Length <= n ? s : s[..n] + "…";
}
