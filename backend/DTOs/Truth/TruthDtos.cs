using System.Text.Json.Nodes;

namespace RabbitIC.Api.DTOs.Truth;

// সত্য মন্ত্রণালয় — what the admin API takes (docs/TRUTH_PROMPT.md §2, §8).

public sealed record TechInput(string Slug, string Name, string Category, string Monogram, int Hue, string? Homepage, List<string>? VersionLines);

public sealed record IssueInput(string Slug, string TitleBn, string? TitleEn, string? SubtitleBn, string? SubtitleEn, string WeekStart, string WeekEnd);

public sealed record AdvisoryInput(
    string? CveId, double? Cvss, string? TitleBn, string? TitleEn, string? AffectedRange,
    List<string>? FixedVersions, bool ExploitedInWild, bool KevListed);

public sealed record TimelineInput(DateTime OccurredAt, string Level, string TextBn, string? TextEn);

public sealed record SourceInput(
    string Url, string Publisher, string? Author, string Title, string? PublishedOn, string Kind, string? FetchedText);

/// <summary>
/// A whole item. Saving replaces its advisories, timeline and sources with these.
/// <see cref="Status"/> is read only when the item is created; after that a
/// status changes through StatusInput, which is logged.
/// </summary>
public sealed record ItemInput(
    int TechId, string Severity, string HeadlineBn, string? HeadlineEn, string SummaryBn, string? SummaryEn,
    string? ImpactBn, string? ImpactEn, string? WhatToDoBn, string? WhatToDoEn, DateTime? PatchScheduledAt,
    List<string>? Tags, string AuthorName, int Order,
    List<AdvisoryInput>? Advisories, List<TimelineInput>? Timeline, List<SourceInput>? Sources, string? Status = null);

public sealed record StatusInput(string Status, DateTime? PatchScheduledAt, string? TextBn, string? TextEn);

public sealed record CorrectionInput(string TextBn, string? TextEn);

public sealed record VerifyInput(string Token, string Note);

public sealed record SiteTextInput(JsonObject? Text, string? Note);
