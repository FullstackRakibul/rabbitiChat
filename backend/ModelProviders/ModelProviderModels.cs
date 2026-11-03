// Model providers kept in the database (docs: "Model Providers in Settings").
//
// An OpenAI-compatible provider a SystemAdmin adds in Settings instead of appsettings:
// where it lives, its key (encrypted, write-only), and which of its models are offered.
// Config-file providers are untouched; a database row can never reuse their key.

namespace RabbitIC.Api.ModelProviders;

public class ModelProvider
{
    public int Id { get; set; }

    /// <summary>Slug used as the source key, e.g. "mangomind". Unique; never a config or built-in source's key.</summary>
    public string Key { get; set; } = "";

    /// <summary>Group heading in the model picker, e.g. "MangoMind".</summary>
    public string Name { get; set; } = "";

    /// <summary>API root with its version segment, e.g. "https://api.mangomindbd.com/v1".</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>The API key, Data Protection purpose "ModelProviders". Null for a keyless local server.</summary>
    public string? ApiKeyProtected { get; set; }

    /// <summary>Last 4 characters, for display only.</summary>
    public string? ApiKeyHint { get; set; }

    /// <summary>JSON array of "chat", "embeddings", "speech".</summary>
    public string CapabilitiesJson { get; set; } = "[\"chat\"]";

    /// <summary>Models offered in pickers (ids from the catalog).</summary>
    public string AllowedModelsJson { get; set; } = "[]";

    /// <summary>Allowed models that may be sent tools in Chat.</summary>
    public string ToolCapableModelsJson { get; set; } = "[]";

    /// <summary>Last GET /models, normalised: [{ id, name, input, output }].</summary>
    public string CatalogJson { get; set; } = "[]";
    public DateTime? CatalogFetchedAt { get; set; }

    /// <summary>A speech provider's voice ids, from GET /audio/voices (its catalog).</summary>
    public string VoicesJson { get; set; } = "[]";

    /// <summary>Ask for usage in streamed replies (stream_options.include_usage). The Test turns it off if refused.</summary>
    public bool StreamUsage { get; set; } = true;

    /// <summary>Prepaid tokens per month, for the "0.3M of 4.5M" display. Null = not shown.</summary>
    public long? MonthlyTokenBudget { get; set; }

    /// <summary>Off = no source, no models anywhere; the key is kept.</summary>
    public bool Enabled { get; set; } = true;

    public DateTime? LastTestAt { get; set; }

    /// <summary>ok · key_rejected · no_credit · rate_limited · unreachable · error · untested. Also set by real calls.</summary>
    public string LastTestStatus { get; set; } = ModelProviderStatus.Untested;

    /// <summary>The message that went with the status, never a key or a raw body.</summary>
    public string? LastTestMessage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>One model call through a database provider. No foreign key: history outlives the provider.</summary>
public class ModelProviderUsage
{
    public long Id { get; set; }
    public string ProviderKey { get; set; } = "";
    public string Model { get; set; } = "";
    /// <summary>chat · jiji · test · …</summary>
    public string Module { get; set; } = "chat";
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    /// <summary>Who the call was for; null for the admin Test button.</summary>
    public int? UserId { get; set; }
    /// <summary>(prompt + completion) × the model's multiplier: what the monthly caps and the plan counter count.</summary>
    public double WeightedTokens { get; set; }
    /// <summary>ok, or the HTTP status / "cancelled" / "unreachable".</summary>
    public string Status { get; set; } = "ok";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public static class ModelProviderStatus
{
    public const string Ok = "ok";
    public const string Untested = "untested";
    public const string KeyRejected = "key_rejected";
    public const string NoCredit = "no_credit";
    public const string RateLimited = "rate_limited";
    public const string Unreachable = "unreachable";
    public const string Error = "error";
    /// <summary>Not stored: the key ring changed and the stored key no longer decrypts.</summary>
    public const string KeyUnreadable = "key_unreadable";
    /// <summary>Not stored: the row is switched off.</summary>
    public const string Disabled = "disabled";
}

public static class ModelProviderCapabilities
{
    public const string Chat = "chat";
    public const string Embeddings = "embeddings";
    public const string Speech = "speech";
    public static readonly string[] All = { Chat, Embeddings, Speech };
}
