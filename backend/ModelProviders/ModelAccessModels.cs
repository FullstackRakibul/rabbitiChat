// Who may run which model in Chat, HR and Jiji (Access → Models).
//
// One row is either a grant (ProviderKey + ModelId, optionally "*" and a multiplier ceiling) or,
// with an empty ProviderKey, a settings row that only carries Locked and the cap. A role's rows
// are the role's rules; a user's rows only ever add to their role's (models, an unlock, a higher cap).

namespace RabbitIC.Api.ModelProviders;

public class ModelAccessRule
{
    public int Id { get; set; }

    /// <summary>The role the rule belongs to, or null for a per-user rule.</summary>
    public string? Role { get; set; }

    /// <summary>The user the rule belongs to, or null for a role rule.</summary>
    public int? UserId { get; set; }

    /// <summary>chat · hr · jiji, or "*" on a settings row that only carries the cap.</summary>
    public string Module { get; set; } = ModelModules.Chat;

    /// <summary>A source key ("local", "cloud", "jiji-mango"), "*" for every Settings provider, or "" for a settings row.</summary>
    public string ProviderKey { get; set; } = "";

    /// <summary>A model id, or "*" for every model the provider offers.</summary>
    public string ModelId { get; set; } = "";

    /// <summary>With "*": only models whose cost multiplier is at most this. Ignored for Ollama.</summary>
    public double? MaxMultiplier { get; set; }

    /// <summary>The module gives this subject one model (its default) and no dropdown.</summary>
    public bool Locked { get; set; }

    /// <summary>Weighted tokens a month across every module; null = no cap. Resets on the 1st, Asia/Dhaka.</summary>
    public long? MonthlyWeightedTokenCap { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public int? UpdatedByUserId { get; set; }
}

/// <summary>A module's default model: what a locked user gets, and what a request without a model uses.</summary>
public class ModelModuleDefault
{
    /// <summary>chat · hr · jiji.</summary>
    public string Module { get; set; } = "";
    public string ProviderKey { get; set; } = "local";
    public string ModelId { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public int? UpdatedByUserId { get; set; }
}

public static class ModelModules
{
    public const string Chat = "chat";
    public const string Hr = "hr";
    public const string Jiji = "jiji";
    public const string Any = "*";
    public static readonly string[] All = { Chat, Hr, Jiji };

    public static bool IsKnown(string? module) => module is not null && All.Contains(module);

    public static string Label(string module) => module switch
    {
        Hr => "HR",
        Jiji => "Jiji",
        _ => "Chat",
    };
}
