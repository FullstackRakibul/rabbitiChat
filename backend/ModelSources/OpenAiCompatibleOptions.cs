namespace RabbitIC.Api.ModelSources;

/// <summary>
/// Bound from "OpenAiCompatible". Holds every configured provider; the adapter
/// class is instantiated once per entry, so adding Together or Fireworks later
/// is a config block rather than a new type.
/// </summary>
public class OpenAiCompatibleOptions
{
    public const string SectionName = "OpenAiCompatible";

    public List<OpenAiProviderOptions> Providers { get; set; } = new();
}

/// <summary>One OpenAI-shaped provider: where it lives, how to authenticate, what to offer.</summary>
public class OpenAiProviderOptions
{
    /// <summary>Source key used in DTOs and in a chat request's `source` field, e.g. "openrouter".</summary>
    public string Key { get; set; } = "";

    /// <summary>Group heading in the model picker, e.g. "OpenRouter (free)".</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>API root including its version segment, e.g. "https://openrouter.ai/api/v1".</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>Set via user-secrets or an environment variable. Empty means the provider is skipped.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// Models to offer. Curated rather than fetched: a provider's /models lists
    /// hundreds and free tiers rotate, so the catalogue is a decision, not a
    /// discovery. A name that has gone stale surfaces as "unavailable" at call
    /// time instead of breaking the list.
    /// </summary>
    public string[] Models { get; set; } = Array.Empty<string>();

    /// <summary>These cost money or consume a quota, so the picker badges them.</summary>
    public bool Metered { get; set; } = true;

    /// <summary>
    /// Glob patterns ("llama-3.3*") for models known to support tool calling.
    /// Empty means none — the UI then hides the agentic controls for this
    /// provider rather than offering a capability that would 400 upstream.
    /// </summary>
    public string[] ToolCapableModels { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Provider-specific headers sent on every request — OpenRouter asks for
    /// HTTP-Referer and X-Title to attribute traffic.
    /// </summary>
    public Dictionary<string, string> ExtraHeaders { get; set; } = new();

    /// <summary>A provider with no key is inert: skipped at registration, never an error.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ApiKey) &&
        !string.IsNullOrWhiteSpace(Key) &&
        !string.IsNullOrWhiteSpace(BaseUrl);

    /// <summary>Name of the typed HttpClient registered for this provider.</summary>
    public string HttpClientName => $"openai-compatible:{Key}";
}
