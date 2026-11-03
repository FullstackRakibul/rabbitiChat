namespace RabbitIC.Api.ModelProviders;

/// <summary>A provider as the admin screen sees it. Never the key: only whether there is one and its last 4.</summary>
public sealed record ModelProviderDto(
    int Id,
    string Key,
    string Name,
    string BaseUrl,
    bool HasKey,
    string? ApiKeyHint,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> AllowedModels,
    IReadOnlyList<string> ToolCapableModels,
    int CatalogCount,
    DateTime? CatalogFetchedAt,
    bool StreamUsage,
    long? MonthlyTokenBudget,
    bool Enabled,
    string Status,
    string? StatusMessage,
    DateTime? LastTestAt,
    ModelProviderMonthUsage Usage,
    bool UsedByJiji,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<string>? Voices = null);

public sealed record ModelProviderMonthUsage(long PromptTokens, long CompletionTokens, long TotalTokens, int Calls, double WeightedTokens = 0);

/// <summary>Create and edit. On edit a blank ApiKey keeps the stored one; ClearApiKey removes it.</summary>
public sealed record ModelProviderInput(
    string? Key,
    string? Name,
    string? BaseUrl,
    string? ApiKey,
    bool? ClearApiKey,
    IReadOnlyList<string>? Capabilities,
    bool? Enabled,
    bool? StreamUsage,
    long? MonthlyTokenBudget);

public sealed record ModelProviderModelsInput(IReadOnlyList<string>? Allowed, IReadOnlyList<string>? ToolCapable);

/// <summary>One model from the provider's GET /models. Prices are shown as the provider gives them.</summary>
public sealed record ModelCatalogEntry(string Id, string? Name, string? InputPrice, string? OutputPrice, string? Kind, double? Multiplier = null);

public sealed record ModelCatalogDto(
    IReadOnlyList<ModelCatalogEntry> Models,
    DateTime? FetchedAt,
    IReadOnlyList<string> Allowed,
    IReadOnlyList<string> ToolCapable,
    IReadOnlyList<string>? Voices = null);

/// <summary>One step of the Test button: what was tried and how it went.</summary>
public sealed record ModelProviderTestStep(string Step, bool Ok, string Detail);

public sealed record ModelProviderTestDto(bool Ok, string Status, string? Message, IReadOnlyList<ModelProviderTestStep> Steps, ModelProviderDto Provider);
