using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;

namespace RabbitIC.Api.ModelSources;

/// <summary>
/// Models served by the local Ollama daemon. Private and unmetered: nothing
/// leaves the machine/LAN, so these are the default in the picker.
///
/// Single responsibility: talk to local Ollama. It knows nothing about the
/// cloud source, the registry, or HTTP endpoints.
/// </summary>
public class LocalOllamaSource : IChatModelSource
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<LocalOllamaSource> _log;

    /// <summary>
    /// /api/show costs a round-trip per model, so the answer is cached for the
    /// process lifetime. Capabilities and context length are properties of the
    /// installed blob — they don't change unless the model is re-pulled, at which
    /// point a restart is expected anyway.
    /// </summary>
    private static readonly ConcurrentDictionary<string, ModelDetails> DetailCache = new();

    private sealed record ModelDetails(int? ContextLength, IReadOnlyList<string> Capabilities);

    public LocalOllamaSource(IHttpClientFactory http, ILogger<LocalOllamaSource> log)
    {
        _http = http;
        _log = log;
    }

    public string SourceKey => "local";

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct = default)
    {
        var client = _http.CreateClient("ollama");

        List<string> names;
        try
        {
            // Rooted path against a bare-origin base — see Program.cs.
            var json = await client.GetStringAsync("/api/tags", ct);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("models", out var models))
                return Array.Empty<ModelInfo>();

            names = models.EnumerateArray()
                .Select(m => m.TryGetProperty("name", out var n) ? n.GetString() : null)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .Where(n => !ModelNaming.IsEmbeddingModel(n))   // never offer these for chat
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // A down daemon must not blank the cloud half of the merged list.
            _log.LogWarning("Local Ollama model list unavailable: {Message}", ex.Message);
            return Array.Empty<ModelInfo>();
        }

        // Capabilities decide whether the UI offers Think and Search, so they're
        // read from the daemon rather than assumed. Probed in parallel; a failed
        // probe degrades to "chat" instead of dropping the model.
        var details = await Task.WhenAll(names.Select(n => DescribeAsync(client, n, ct)));

        return names.Zip(details, (name, d) => new ModelInfo(
            Name: name,
            Source: SourceKey,
            ContextLength: d.ContextLength,
            Capabilities: d.Capabilities,
            Metered: false)).ToList();
    }

    public IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(ChatArgs args, CancellationToken ct = default) =>
        OllamaChatStream.StreamAsync(
            _http.CreateClient("ollama"), "/api/chat", args.Model, args.Messages, _log, ct,
            args.Tools, args.Think);

    /// <summary>
    /// Asks /api/show what a model can do. Ollama reports capabilities directly
    /// ("completion", "tools", "thinking", "vision"), so nothing is inferred from
    /// the name — a model gains tool support by being re-pulled, not by us
    /// guessing from a string.
    /// </summary>
    private async Task<ModelDetails> DescribeAsync(HttpClient client, string name, CancellationToken ct)
    {
        if (DetailCache.TryGetValue(name, out var cached)) return cached;

        ModelDetails details;
        try
        {
            using var resp = await client.PostAsJsonAsync("/api/show", new { model = name }, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogDebug("/api/show for {Model} answered {Status}.", name, (int)resp.StatusCode);
                details = new ModelDetails(null, new[] { "chat" });
            }
            else
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                details = new ModelDetails(
                    ReadContextLength(doc.RootElement),
                    ReadCapabilities(doc.RootElement));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Unknown capabilities means the UI hides Think/Search for this model.
            // Hiding a control that might have worked is better than showing one
            // that produces an upstream 400.
            _log.LogDebug("/api/show for {Model} failed: {Message}", name, ex.Message);
            details = new ModelDetails(null, new[] { "chat" });
        }

        DetailCache[name] = details;
        return details;
    }

    /// <summary>
    /// The context window lives under model_info as "&lt;arch&gt;.context_length",
    /// e.g. "llama.context_length" — the architecture prefix varies, so the key is
    /// matched by suffix rather than assumed.
    /// </summary>
    private static int? ReadContextLength(JsonElement root)
    {
        if (!root.TryGetProperty("model_info", out var info) ||
            info.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var prop in info.EnumerateObject())
        {
            if (!prop.Name.EndsWith(".context_length", StringComparison.OrdinalIgnoreCase)) continue;
            if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out var value))
                return value;
        }

        return null;
    }

    /// <summary>
    /// Normalises Ollama's capability list to the tags the UI gates on.
    /// "completion" is renamed "chat" to match what the picker already shows;
    /// everything else passes through, so a capability added upstream reaches the
    /// client without an edit here.
    /// </summary>
    private static IReadOnlyList<string> ReadCapabilities(JsonElement root)
    {
        if (!root.TryGetProperty("capabilities", out var caps) ||
            caps.ValueKind != JsonValueKind.Array)
        {
            return new[] { "chat" };
        }

        var list = caps.EnumerateArray()
            .Where(c => c.ValueKind == JsonValueKind.String)
            .Select(c => c.GetString()!)
            .Select(c => c.Equals("completion", StringComparison.OrdinalIgnoreCase) ? "chat" : c)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!list.Contains("chat", StringComparer.OrdinalIgnoreCase)) list.Insert(0, "chat");
        return list;
    }
}
