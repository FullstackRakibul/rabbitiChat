// Oddity — the Ollama-compatible gateway Ground Control operates.
//
// Three tables in this phase. Keys and requests are owner-scoped; the settings
// row is install-wide because there is one Ollama and one GPU behind it.
//
// Every timestamp is UTC and its column ends in "Utc".

namespace RabbitIC.Api.Models;

/// <summary>
/// A gateway key. Only the public prefix and a SHA-256 of the whole key are kept:
/// the key itself is shown once, at creation, and can never be read back.
/// </summary>
public class OddityKey
{
    public int Id { get; set; }

    public int OwnerUserId { get; set; }

    public string Name { get; set; } = "";

    public string? Note { get; set; }

    /// <summary>The 8 lowercase hex characters after "odd_". Unique; the lookup key.</summary>
    public string Prefix { get; set; } = "";

    /// <summary>SHA-256 of the whole key, lowercase hex. Redacted in the audit log.</summary>
    public string KeyHash { get; set; } = "";

    /// <summary>Profile used when a call names none. Profiles arrive in Phase 3.</summary>
    public int? DefaultProfileId { get; set; }

    /// <summary>Calls a minute. Null uses the settings default; 0 means no limit.</summary>
    public int? RatePerMinute { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTime? ExpiresAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>The one install-wide settings row. Read through a small cache cleared on save.</summary>
public class OdditySettings
{
    public int Id { get; set; }

    /// <summary>Off until Ground Control turns it on; a disabled gateway answers 503.</summary>
    public bool Enabled { get; set; }

    /// <summary>Model used when a call names none. Empty leaves the call as sent.</summary>
    public string DefaultModel { get; set; } = "";

    /// <summary>Comma-separated model names. Empty means any model.</summary>
    public string AllowedModels { get; set; } = "";

    /// <summary>The context window every chat and generate call runs at (2048–262144).</summary>
    public int NumCtx { get; set; } = 16384;

    /// <summary>Tokens kept free for the reply. At most half of NumCtx.</summary>
    public int ReplyReserve { get; set; } = 4096;

    /// <summary>
    /// A plain number ("-1", "0", "3600") is sent as a JSON number, anything else
    /// ("30m", "2h") as a string — Ollama refuses a bare "-1" string.
    /// </summary>
    public string KeepAlive { get; set; } = "-1";

    /// <summary>Calls a minute for keys without their own rate. 0 means no limit.</summary>
    public int DefaultRatePerMinute { get; set; } = 30;

    public int MaxInlineFileMb { get; set; } = 10;

    /// <summary>"off" | "preview" | "full".</summary>
    public string LogBodies { get; set; } = "preview";

    public int RetentionDays { get; set; } = 30;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public int? UpdatedByUserId { get; set; }

    /// <summary>
    /// The gateway's address as callers outside this network use it, e.g.
    /// "https://ai.example.com/oddity". Empty shows only the address the page
    /// was opened on. Declared last so a fresh database and an upgraded one
    /// (which appends the column) have the same column order.
    /// </summary>
    public string PublicBaseUrl { get; set; } = "";
}

/// <summary>
/// One gateway call that presented a valid key. Deliberately no foreign key to the
/// key or its owner: history must outlive a revoked key. Skipped by the audit
/// interceptor, or every call would be recorded twice.
/// </summary>
public class OddityRequest
{
    public long Id { get; set; }

    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    public string? CorrelationId { get; set; }

    public int OwnerUserId { get; set; }

    public int KeyId { get; set; }

    /// <summary>Copied at call time, so the row still reads after the key is gone.</summary>
    public string KeyName { get; set; } = "";

    public string? ClientIp { get; set; }

    public string? UserAgent { get; set; }

    /// <summary>"chat" | "generate" | "embed" | "embeddings".</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>What the caller asked for.</summary>
    public string? RequestedModel { get; set; }

    /// <summary>What was sent upstream.</summary>
    public string? Model { get; set; }

    public string? Profile { get; set; }

    public bool Stream { get; set; }

    public int StatusCode { get; set; }

    /// <summary>"ok" | "rejected" | "upstream_error" | "client_abort".</summary>
    public string Outcome { get; set; } = "";

    public string? Error { get; set; }

    public string? DoneReason { get; set; }

    public int? PromptTokens { get; set; }

    public int? CachedPromptTokens { get; set; }

    public int? OutputTokens { get; set; }

    public long? LoadMs { get; set; }

    public long? PromptMs { get; set; }

    public long? GenerationMs { get; set; }

    public long? FirstByteMs { get; set; }

    public long TotalMs { get; set; }

    public int? EstimatedPromptTokens { get; set; }

    public int? AddedTokens { get; set; }

    public string? ReceiptJson { get; set; }

    public string? RequestPreview { get; set; }

    public string? ResponsePreview { get; set; }

    public long RequestBytes { get; set; }

    public long ResponseBytes { get; set; }
}
