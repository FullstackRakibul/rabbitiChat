using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace RabbitIC.Api.MinimalApis
{
    /// <summary>
    /// Builds the system message that grounds a chat turn on live web results.
    /// Used only by the per-message `webSearch` flag on /api/chat — search is never
    /// enabled implicitly for a profile or conversation.
    /// </summary>
    public static class WebSearchContext
    {
        private const int MaxResults = 5;

        /// <summary>Trim each result body so five results can't crowd out the conversation.</summary>
        private const int MaxContentCharsPerResult = 1500;

        /// <summary>
        /// Runs one search and formats it as a system prompt. Returns null when the
        /// search fails or yields nothing, so the caller can answer without it.
        /// </summary>
        public static async Task<string?> BuildAsync(
            HttpClient client, string query, ILogger log, CancellationToken ct)
        {
            string json;
            try
            {
                var payload = new { query, max_results = MaxResults };
                using var resp = await client.PostAsJsonAsync("/api/web_search", payload, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    log.LogWarning("Chat web search failed upstream: {Status}", (int)resp.StatusCode);
                    return null;
                }
                json = await resp.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                log.LogWarning("Chat web search unavailable: {Message}", ex.Message);
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("results", out var results) ||
                    results.ValueKind != JsonValueKind.Array ||
                    results.GetArrayLength() == 0)
                {
                    return null;
                }

                var sb = new StringBuilder();
                sb.Append("Web results retrieved ")
                  .Append(DateTime.Now.ToString("o"))
                  .AppendLine(". Answer using these results where relevant and cite the source URL inline after each claim, e.g. (ollama.com). If results don't cover the question, say so rather than guessing.")
                  .AppendLine();

                var n = 0;
                foreach (var r in results.EnumerateArray())
                {
                    n++;
                    var title = r.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    var url = r.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                    var content = r.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";

                    if (content.Length > MaxContentCharsPerResult)
                        content = content[..MaxContentCharsPerResult] + "…";

                    sb.AppendLine($"{n}. {title}")
                      .AppendLine($"   URL: {url}")
                      .AppendLine($"   {content}")
                      .AppendLine();
                }

                return sb.ToString();
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
