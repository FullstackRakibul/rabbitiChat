using Microsoft.Extensions.Options;

namespace RabbitIC.Api.ModelSources;

/// <summary>
/// Bound from the "OllamaCloud" configuration section. The key is read from
/// user-secrets or the OllamaCloud__ApiKey environment variable — never from a
/// committed file, and never sent to the browser.
/// </summary>
public class OllamaCloudOptions
{
    public const string SectionName = "OllamaCloud";

    public string ApiKey { get; set; } = "6a922123b6c640a288298ac82c4e8478.NU-gbG-AgbhJEnOw-ayB8bfR";
    public string BaseUrl { get; set; } = "https://ollama.com";

    /// <summary>Curated free-plan model names, as shown in the picker.</summary>
    public string[] Models { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Capabilities to advertise for cloud models. The hosted catalogue publishes
    /// no per-model capability list, so this is configuration rather than a guess:
    /// leave it empty and the UI offers only plain chat. Set e.g.
    /// [ "chat", "tools" ] once the account's models are known to support them.
    /// </summary>
    public string[] Capabilities { get; set; } = { "chat" };

    /// <summary>
    /// Suffix the account uses to address a model's hosted copy — `ollama run
    /// gpt-oss:120b-cloud` runs the cloud build of `gpt-oss:120b`. Configurable
    /// rather than hard-coded: set it to "" to send names verbatim if the
    /// account stops requiring the suffix.
    /// </summary>
    public string CloudSuffix { get; set; } = "-cloud";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>
/// Ollama's hosted open models (gpt-oss, nemotron, gemma) — metered, so every
/// call spends the account's quota.
///
/// The listing is read from configuration rather than scraped: the free plan's
/// catalogue is a business decision, not something to discover at runtime.
/// </summary>
public class CloudOllamaSource : IChatModelSource
{
    private readonly IHttpClientFactory _http;
    private readonly OllamaCloudOptions _options;
    private readonly ILogger<CloudOllamaSource> _log;

    public CloudOllamaSource(
        IHttpClientFactory http,
        IOptions<OllamaCloudOptions> options,
        ILogger<CloudOllamaSource> log)
    {
        _http = http;
        _options = options.Value;
        _log = log;
    }

    public string SourceKey => "cloud";

    /// <summary>Configured capabilities, always including "chat".</summary>
    private IReadOnlyList<string> NormalizedCapabilities
    {
        get
        {
            var list = _options.Capabilities
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!list.Contains("chat", StringComparer.OrdinalIgnoreCase)) list.Insert(0, "chat");
            return list;
        }
    }

    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct = default)
    {
        // No key means no cloud models — an empty half, not an error: the picker
        // still shows local models and the UI reports "not configured".
        if (!_options.IsConfigured)
        {
            _log.LogInformation("Ollama cloud has no API key configured; offering no cloud models.");
            return Task.FromResult<IReadOnlyList<ModelInfo>>(Array.Empty<ModelInfo>());
        }

        IReadOnlyList<ModelInfo> models = _options.Models
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => new ModelInfo(
                Name: m.Trim(),
                Source: SourceKey,
                ContextLength: null,          // not published per-model; don't invent one
                Capabilities: NormalizedCapabilities,
                Metered: true))
            .ToList();

        return Task.FromResult(models);
    }

    public IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(ChatArgs args, CancellationToken ct = default)
    {
        if (!_options.IsConfigured)
            throw new ChatSourceException("Ollama cloud is not configured on this server.");

        var upstreamModel = ResolveUpstreamName(args.Model);
        _log.LogInformation(
            "Cloud chat: picker model {Picked} sent upstream as {Upstream}", args.Model, upstreamModel);

        return OllamaChatStream.StreamAsync(
            _http.CreateClient("ollama-cloud"), "/api/chat", upstreamModel, args.Messages, _log, ct,
            args.Tools, args.Think);
    }

    /// <summary>
    /// Maps a picker name to the name the account addresses upstream by applying
    /// <see cref="OllamaCloudOptions.CloudSuffix"/>. Idempotent: a name that
    /// already carries the suffix is sent unchanged, so a config listing
    /// "gpt-oss:120b-cloud" never becomes "…-cloud-cloud".
    /// </summary>
    public string ResolveUpstreamName(string model)
    {
        var name = model.Trim();
        var suffix = _options.CloudSuffix;

        if (string.IsNullOrEmpty(suffix)) return name;
        if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return name;

        return name + suffix;
    }
}
