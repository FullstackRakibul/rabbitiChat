using System.Text.RegularExpressions;

namespace RabbitIC.Api.ModelSources;

/// <summary>
/// One adapter for every OpenAI-shaped provider — OpenRouter, Groq, Together,
/// anything else speaking /chat/completions.
///
/// There is deliberately no per-provider subclass: providers differ in base URL,
/// credentials, headers and catalogue, all of which are data. A new provider is
/// an <see cref="OpenAiProviderOptions"/> entry plus its key, and nothing in this
/// file, ChatService, or the chat endpoint changes.
/// </summary>
public class OpenAiCompatibleSource : IChatModelSource
{
    private readonly OpenAiProviderOptions _provider;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<OpenAiCompatibleSource> _log;

    public OpenAiCompatibleSource(
        OpenAiProviderOptions provider,
        IHttpClientFactory http,
        ILogger<OpenAiCompatibleSource> log)
    {
        _provider = provider;
        _http = http;
        _log = log;
    }

    public string SourceKey => _provider.Key;

    /// <summary>Group heading for the picker — the registry exposes it alongside the key.</summary>
    public string DisplayName =>
        string.IsNullOrWhiteSpace(_provider.DisplayName) ? _provider.Key : _provider.DisplayName;

    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct = default)
    {
        // Catalogue comes from config, never from the provider: their /models
        // endpoint lists hundreds of entries and changes weekly, which would make
        // the picker unusable and the free-tier subset unknowable.
        IReadOnlyList<ModelInfo> models = _provider.Models
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Trim())
            .Select(m => new ModelInfo(
                Name: m,
                Source: SourceKey,
                ContextLength: null,            // not published per-model; don't invent one
                Capabilities: CapabilitiesFor(m),
                Metered: _provider.Metered))
            .ToList();

        return Task.FromResult(models);
    }

    public IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(ChatArgs args, CancellationToken ct = default)
    {
        // Tools are forwarded only when this model is declared tool-capable. A
        // provider that receives a tools array for a model without support answers
        // 400, so the gate is enforced here as well as in the UI.
        var tools = SupportsTools(args.Model) ? args.Tools : null;

        if (args.Tools is { Count: > 0 } && tools is null)
        {
            _log.LogInformation(
                "{Provider}: dropping tools for {Model} — not listed in ToolCapableModels.",
                SourceKey, args.Model);
        }

        return OpenAiChatStream.StreamAsync(
            _http.CreateClient(_provider.HttpClientName),
            args.Model, args.Messages, _log, ct, tools);
    }

    /// <summary>Every model can chat; "tools" is added only for a configured match.</summary>
    private IReadOnlyList<string> CapabilitiesFor(string model) =>
        SupportsTools(model) ? new[] { "chat", "tools" } : new[] { "chat" };

    /// <summary>True when the model matches one of the provider's glob patterns.</summary>
    private bool SupportsTools(string model) =>
        _provider.ToolCapableModels.Any(pattern => GlobMatch(pattern, model));

    /// <summary>
    /// Case-insensitive glob over model names: "*" matches any run of characters,
    /// "?" matches one. Implemented by translating to a regex so a pattern like
    /// "llama-3.3*" treats its dots literally rather than as wildcards.
    /// </summary>
    internal static bool GlobMatch(string pattern, string value)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;

        var regex = "^" + Regex.Escape(pattern.Trim())
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";

        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase);
    }
}
