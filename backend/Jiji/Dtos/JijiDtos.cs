// Jiji — what the API sends. UploadToken and every *Protected column stay server-side.

using System.Text.Json;
using RabbitIC.Api.Jiji.Models;

namespace RabbitIC.Api.Jiji.Dtos;

public sealed record JijiContentSummaryDto(
    int Id, string ContentType, string Subject, string Language, string? Angle, string Status,
    string? Title, DateTime? PublishAtUtc, string? YouTubeVideoId, string? LastError, DateTime UpdatedAt)
{
    public static JijiContentSummaryDto From(JijiContent c) => new(
        c.Id, c.ContentType, c.Subject, c.Language, c.Angle, c.Status,
        c.Title, c.PublishAtUtc, c.YouTubeVideoId, c.LastError, c.UpdatedAt);
}

public sealed record JijiJobDto(
    int Id, string Type, string Status, int ContentRevision, int Attempts, DateTime AvailableAtUtc,
    DateTime? StartedAtUtc, DateTime? CompletedAtUtc, string? LastError)
{
    public static JijiJobDto From(JijiJob j) => new(
        j.Id, j.Type, j.Status, j.ContentRevision, j.Attempts, j.AvailableAtUtc,
        j.StartedAtUtc, j.CompletedAtUtc, j.LastError);
}

public sealed record JijiContentDto(
    int Id, string ContentType, string Subject, string Language, string? Angle, string SourceNotes,
    JsonElement? Script, int Revision, string Status, string? Title, string? Description, string[] Tags,
    DateTime? PublishAtUtc, bool ContainsSyntheticMedia, bool MadeForKids, bool HasVideo,
    string? YouTubeVideoId, string? RejectReason, string? LastError, DateTime CreatedAt, DateTime UpdatedAt,
    IReadOnlyList<JijiJobDto> Jobs, JijiScriptCheck? ApprovalCheck, bool HasNarration)
{
    /// <param name="approvalCheck">The saved script checked as approval would check it; null without a script.</param>
    public static JijiContentDto From(JijiContent c, IReadOnlyList<JijiJob> jobs, JijiScriptCheck? approvalCheck, bool hasNarration) => new(
        c.Id, c.ContentType, c.Subject, c.Language, c.Angle, c.SourceNotes,
        ParseOrNull(c.ScriptJson), c.Revision, c.Status, c.Title, c.Description,
        JsonSerializer.Deserialize<string[]>(c.TagsJson) ?? Array.Empty<string>(),
        c.PublishAtUtc, c.ContainsSyntheticMedia, c.MadeForKids, c.VideoPath is not null,
        c.YouTubeVideoId, c.RejectReason, c.LastError, c.CreatedAt, c.UpdatedAt,
        jobs.Select(JijiJobDto.From).ToList(), approvalCheck, hasNarration);

    private static JsonElement? ParseOrNull(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}

public sealed record JijiStatusDto(
    bool StorageWritable, string? Model, string? ModelSource, bool VoiceFound, JijiSpeechStatusDto Speech, bool YouTubeConfigured,
    bool YouTubeConnected, string? ChannelTitle, JijiProviderStatusDto? Provider = null);

/// <summary>
/// The speech provider in Jiji → Settings, checked live (GET /audio/voices, a few seconds at most):
/// Reachable null = no provider chosen. VoiceListed = the voice is in the provider's stored list.
/// </summary>
public sealed record JijiSpeechStatusDto(
    string? Provider, string? ProviderName, bool? Reachable, string? Message, string Model, string? Voice, bool VoiceListed);

/// <summary>Jiji → Settings: what is set (stored, or the config fallback) and the choices for the speech dropdowns.</summary>
public sealed record JijiSettingsDto(
    string? ScriptModel, string ScriptModelSource, string? SpeechProvider, string SpeechModel, string? Voice,
    IReadOnlyList<JijiSpeechProviderDto> SpeechProviders, bool CanEdit);

public sealed record JijiSpeechProviderDto(string Key, string Name, bool Enabled, string Status, IReadOnlyList<string> Voices);

public sealed record JijiPreviewRequest(string? Provider, string? Voice, string? Text);

/// <summary>Set when Jiji:ModelSource names a provider from Settings: whether it works, and what Jiji spent this month.</summary>
public sealed record JijiProviderStatusDto(
    string Key, string Name, string Status, string? Message, long JijiTokensThisMonth, long TokensThisMonth, long? MonthlyTokenBudget);
