using System.Text;
using System.Text.Json;
using RabbitIC.Api.MinimalApis;
using RabbitIC.Api.ModelSources;

namespace RabbitIC.Api.Services;

/// <summary>
/// The `web_search` tool as offered to models that support tool calling.
///
/// Execution goes through <see cref="WebSearchRunner"/>, the same helper the
/// /api/search proxy path uses, so there is exactly one place that knows how to
/// talk to the upstream search API and one place that caps what comes back.
/// </summary>
public static class WebSearchTool
{
    public const string Name = "web_search";

    /// <summary>Enough results to answer with, few enough to leave room for the conversation.</summary>
    public const int MaxResults = 5;

    public static readonly ToolDefinition Definition = new(
        Name,
        "Search the public web for current information. Use it when the answer depends on "
        + "recent events, live data, or anything outside your training data. Always cite the "
        + "source URL inline after each claim drawn from a result.",
        """
        {
          "type": "object",
          "properties": {
            "query": {
              "type": "string",
              "description": "The search query, phrased as you would type it into a search engine."
            }
          },
          "required": ["query"]
        }
        """);

    /// <summary>Pulls the query out of whatever the model produced. Empty when it sent nothing usable.</summary>
    public static string ReadQuery(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("query", out var q) &&
                q.ValueKind == JsonValueKind.String)
            {
                return q.GetString()?.Trim() ?? "";
            }
        }
        catch (JsonException) { /* the model produced non-JSON arguments */ }

        return "";
    }
}

/// <summary>
/// Runs a web search and formats the results, for both the tool-calling path and
/// the prepend-context path. Wraps <see cref="WebSearchContext"/> so neither
/// caller re-implements the upstream call or the result cap.
/// </summary>
public class WebSearchRunner
{
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _config;
    private readonly ILogger<WebSearchRunner> _log;

    public WebSearchRunner(IHttpClientFactory http, IConfiguration config, ILogger<WebSearchRunner> log)
    {
        _http = http;
        _config = config;
        _log = log;
    }

    /// <summary>False when no upstream key is configured — callers degrade instead of failing.</summary>
    public bool IsConfigured => SearchApis.IsConfigured(_config);

    /// <summary>
    /// The formatted results block, or null when search is unconfigured, failed,
    /// or returned nothing. Used verbatim as a system message (prepend path) or as
    /// a tool result (tool-calling path).
    /// </summary>
    public async Task<string?> RunAsync(string query, CancellationToken ct)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(query)) return null;

        return await WebSearchContext.BuildAsync(
            _http.CreateClient("ollama-cloud"), query, _log, ct);
    }
}
