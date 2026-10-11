// Oddity — wire contracts for the admin API (/api/oddity/*).
//
// No shape here carries a key hash. The key itself appears in exactly one
// response: the one that creates it.

namespace RabbitIC.Api.DTOs.Oddity;

// ---- Settings ----

public record OdditySettingsDto(
    bool Enabled,
    string DefaultModel,
    List<string> AllowedModels,
    int NumCtx,
    int ReplyReserve,
    string KeepAlive,
    int DefaultRatePerMinute,
    int MaxInlineFileMb,
    string LogBodies,
    int RetentionDays,
    DateTime UpdatedAtUtc,
    int? UpdatedByUserId,
    string PublicBaseUrl);

/// <summary>The whole settings row — PUT replaces it, it does not merge.</summary>
public record OdditySettingsInput(
    bool Enabled,
    string? DefaultModel,
    List<string>? AllowedModels,
    int NumCtx,
    int ReplyReserve,
    string? KeepAlive,
    int DefaultRatePerMinute,
    int MaxInlineFileMb,
    string? LogBodies,
    int RetentionDays,
    string? PublicBaseUrl = null);

// ---- Keys ----

public record OddityKeyDto(
    int Id,
    string Name,
    string? Note,
    string Prefix,
    int? DefaultProfileId,
    int? RatePerMinute,
    bool Enabled,
    DateTime? ExpiresAtUtc,
    DateTime CreatedAtUtc,
    DateTime? LastUsedUtc,
    int Requests24h);

/// <summary>Returned once, by POST /keys. <see cref="Key"/> cannot be read again.</summary>
public record OddityKeyCreatedDto(
    int Id,
    string Name,
    string? Note,
    string Prefix,
    int? RatePerMinute,
    bool Enabled,
    DateTime? ExpiresAtUtc,
    DateTime CreatedAtUtc,
    string Key);

/// <summary>
/// Create or replace a key's editable fields. A null rate uses the settings
/// default; a null expiry means none; a null <see cref="Enabled"/> keeps the
/// current value on PUT and means true on POST.
/// </summary>
public record OddityKeyInput(string? Name, string? Note, int? RatePerMinute, bool? Enabled, DateTime? ExpiresAtUtc);

// ---- Requests ----

public record OddityRequestDto(
    long Id,
    DateTime TimestampUtc,
    string? CorrelationId,
    int KeyId,
    string KeyName,
    string? ClientIp,
    string? UserAgent,
    string Endpoint,
    string? RequestedModel,
    string? Model,
    string? Profile,
    bool Stream,
    int StatusCode,
    string Outcome,
    string? Error,
    string? DoneReason,
    int? PromptTokens,
    int? CachedPromptTokens,
    int? OutputTokens,
    long? LoadMs,
    long? PromptMs,
    long? GenerationMs,
    long? FirstByteMs,
    long TotalMs,
    int? EstimatedPromptTokens,
    int? AddedTokens,
    long RequestBytes,
    long ResponseBytes);

/// <summary>One row in full: the list shape plus the receipt and both previews.</summary>
public record OddityRequestDetailDto(
    OddityRequestDto Request,
    string? ReceiptJson,
    string? RequestPreview,
    string? ResponsePreview);

/// <summary>A keyset page: pass <see cref="NextBefore"/> back as <c>before</c> for the next one.</summary>
public record OddityRequestPageDto(List<OddityRequestDto> Items, long? NextBefore);

public record OddityRequestFilter(
    long? Before, int? Limit, int? KeyId, string? Profile, string? Model,
    string? Status, string? Ip, DateTime? From, DateTime? To, string? Q);
