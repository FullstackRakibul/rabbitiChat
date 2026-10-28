using System.Text.Json.Nodes;

namespace RabbitIC.Api.DTOs.Truth;

// সত্য মন্ত্রণালয় — what the admin API takes (docs/TRUTH_PROMPT.md §2, §8).

public sealed record TechInput(string Slug, string Name, string Category, string Monogram, int Hue, string? Homepage, List<string>? VersionLines, List<string>? EolLines = null);

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
    List<AdvisoryInput>? Advisories, List<TimelineInput>? Timeline, List<SourceInput>? Sources, string? Status = null,
    string? DoNowCommand = null, string? DoNowLanguage = null, List<RotateKeyInput>? RotateKeys = null);

public sealed record StatusInput(string Status, DateTime? PatchScheduledAt, string? TextBn, string? TextEn);

public sealed record CorrectionInput(string TextBn, string? TextEn);

public sealed record VerifyInput(string Token, string Note);

public sealed record SiteTextInput(JsonObject? Text, string? Note);

// ─────────── fix guides (§6) ───────────

public sealed record GuideStepInput(
    string Kind, string? Platform, string? TitleBn, string? TitleEn, string? BodyBn, string? BodyEn,
    string? Code, string? Language, string? ExpectedOutput, string? Condition, string? SideEffectsBn, string? SideEffectsEn);

/// <summary>A whole guide. Saving replaces its steps and sources with these, and resets the grid and the checklist (§6.3).</summary>
public sealed record GuideInput(
    string Slug, int? ItemId, int TechId, string TitleBn, string? TitleEn, string Phase, string? IntroBn, string? IntroEn,
    JsonObject? Variables, List<string>? Platforms, bool BackupFirst, string AuthorName, int? BasedOnSourceOrder,
    List<GuideStepInput>? Steps, List<SourceInput>? Sources);

public sealed record AckInput(string FindingKey, string Note);

public sealed record ChecklistInput(bool Tested, bool Verify, bool Undo, bool Sources);

public sealed record RenderInput(string? Template, Dictionary<string, string>? Values);

public sealed record RotateKeyInput(string Label, string? Url);

// ─── Records Department (P5) ───
public sealed record EnabledInput(bool Enabled);
public sealed record VisibilityInput(string Visibility, string? Note);
public sealed record DecisionInput(string Decision);
public sealed record DropInput(string Url);
public sealed record ClusterItemInput(int IssueId, int? TechId, int? DigestId);
