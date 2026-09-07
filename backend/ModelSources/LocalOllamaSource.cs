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

    public LocalOllamaSource(IHttpClientFactory http, ILogger<LocalOllamaSource> log)
    {
        _http = http;
        _log = log;
    }

    public string SourceKey => "local";

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct = default)
    {
        var client = _http.CreateClient("ollama");

        try
        {
            // Rooted path against a bare-origin base — see Program.cs.
            var json = await client.GetStringAsync("/api/tags", ct);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("models", out var models))
                return Array.Empty<ModelInfo>();

            var list = new List<ModelInfo>();
            foreach (var m in models.EnumerateArray())
            {
                var name = m.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (IsEmbeddingModel(name)) continue;      // never offer these for chat

                list.Add(new ModelInfo(
                    Name: name,
                    Source: SourceKey,
                    ContextLength: ReadContextLength(m),
                    Capabilities: new[] { "chat" },
                    Metered: false));
            }

            return list;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // A down daemon must not blank the cloud half of the merged list.
            _log.LogWarning("Local Ollama model list unavailable: {Message}", ex.Message);
            return Array.Empty<ModelInfo>();
        }
    }

    public IAsyncEnumerable<string> StreamChatAsync(ChatArgs args, CancellationToken ct = default) =>
        OllamaChatStream.StreamAsync(
            _http.CreateClient("ollama"), "/api/chat", args.Model, args.Messages, _log, ct);

    /// <summary>Embedding models can't answer chat, so they're filtered server-side.</summary>
    private static bool IsEmbeddingModel(string name) => ModelNaming.IsEmbeddingModel(name);

    /// <summary>/api/tags exposes the context window on some builds only; absence is fine.</summary>
    private static int? ReadContextLength(JsonElement model)
    {
        if (model.TryGetProperty("details", out var details) &&
            details.TryGetProperty("context_length", out var ctx) &&
            ctx.TryGetInt32(out var value))
        {
            return value;
        }
        return null;
    }
}
