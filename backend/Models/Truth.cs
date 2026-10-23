namespace RabbitIC.Api.Models;

// সত্য মন্ত্রণালয় · Ministry of Truth — docs/TRUTH_PROMPT.md §2.
//
// Staff write issues, their items and each item's advisories, timeline and
// sources here, on the LAN. The public site never reads these tables: publishing
// an issue writes an immutable snapshot (TrSnapshot) built through an allow-list
// (Truth/TruthSnapshot.cs), and /public/truth/* serves only snapshots of issues
// that are published. So a draft, a source's fetched text, a hand-verification
// note or a user id can never reach the public JSON.
//
// Times are stored in UTC (rule 6); the pages show them in Bangladesh time.
// Guides (P2) and subscribers and mail (P3) add their own Tr* tables.

public static class TrStatus
{
    public const string ActiveAttack = "active_attack";
    public const string BreachConfirmed = "breach_confirmed";
    public const string PatchScheduled = "patch_scheduled";
    public const string Ongoing = "ongoing";
    public const string Contained = "contained";
    public const string Unconfirmed = "unconfirmed";
    public const string Patched = "patched";
    public const string Blocked = "blocked";
    public const string Resolved = "resolved";

    /// <summary>Every pill, in the order the admin offers them. Their labels are in the site JSON (status.*).</summary>
    public static readonly string[] All =
        { ActiveAttack, BreachConfirmed, PatchScheduled, Ongoing, Contained, Unconfirmed, Patched, Blocked, Resolved };

    /// <summary>The statuses that raise the urgent banner (§3.1).</summary>
    public static bool IsUrgent(string? status) => status is ActiveAttack or PatchScheduled;
}

public static class TrSeverity
{
    public static readonly string[] All = { "critical", "high", "medium", "low", "info" };
}

public static class TrIssueStatus
{
    public const string Draft = "draft";
    public const string Review = "review";
    public const string Published = "published";
}

public static class TrSourceKind
{
    public const string Claim = "claim";
    public static readonly string[] All = { "official", "vendor", "research", "community", "news", Claim };
}

/// <summary>A technology items are about. Shown with a neutral lettermark, never a vendor logo (§3).</summary>
public class TrTech
{
    public static readonly string[] Categories = { "language", "runtime", "platform", "ai-provider", "ca", "database", "app" };

    public int Id { get; set; }
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "app";

    /// <summary>One or two letters drawn in the lettermark.</summary>
    public string Monogram { get; set; } = "";

    /// <summary>0–359: the lettermark's muted background is made from it.</summary>
    public int Hue { get; set; }
    public string? Homepage { get; set; }

    /// <summary>["3.1","3.2", …] — the version lines the version checker (P2) knows.</summary>
    public string VersionLinesJson { get; set; } = "[]";
}

/// <summary>One weekly issue (slug "2026-w43"). Its items are published together.</summary>
public class TrIssue
{
    public int Id { get; set; }
    public string Slug { get; set; } = "";
    public string TitleBn { get; set; } = "";
    public string? TitleEn { get; set; }
    public string? SubtitleBn { get; set; }
    public string? SubtitleEn { get; set; }

    /// <summary>ISO dates, yyyy-MM-dd.</summary>
    public string WeekStart { get; set; } = "";
    public string WeekEnd { get; set; } = "";

    /// <summary><see cref="TrIssueStatus"/>. Stays published while staff edit; the public copy is the last snapshot.</summary>
    public string Status { get; set; } = TrIssueStatus.Draft;
    public DateTime? FirstPublishedAtUtc { get; set; }
    public DateTime? PublishedAtUtc { get; set; }
    public int? PublishedByUserId { get; set; }
    public int CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>One technology's news in an issue.</summary>
public class TrItem
{
    public int Id { get; set; }
    public int IssueId { get; set; }
    public int TechId { get; set; }

    /// <summary><see cref="TrSeverity.All"/>.</summary>
    public string Severity { get; set; } = "medium";

    /// <summary><see cref="TrStatus"/>. Changed only through a status change, which is logged (rule 5).</summary>
    public string Status { get; set; } = TrStatus.Ongoing;
    public string HeadlineBn { get; set; } = "";
    public string? HeadlineEn { get; set; }
    public string SummaryBn { get; set; } = "";
    public string? SummaryEn { get; set; }
    public string? ImpactBn { get; set; }
    public string? ImpactEn { get; set; }

    /// <summary>The "what to do" note an urgent item needs when it has no guide (§8 checklist).</summary>
    public string? WhatToDoBn { get; set; }
    public string? WhatToDoEn { get; set; }
    public DateTime? PatchScheduledAtUtc { get; set; }
    public string TagsJson { get; set; } = "[]";
    public string AuthorName { get; set; } = "";
    public int Order { get; set; }

    /// <summary>
    /// Numbers verified by hand (rule 4): [{ token, note, byUserId, atUtc }]. Staff
    /// only — the snapshot never carries it.
    /// </summary>
    public string HandVerifiedJson { get; set; } = "[]";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public class TrAdvisory
{
    public int Id { get; set; }
    public int ItemId { get; set; }
    public int Order { get; set; }
    public string? CveId { get; set; }
    public double? Cvss { get; set; }
    public string? TitleBn { get; set; }
    public string? TitleEn { get; set; }

    /// <summary>As the source writes it, e.g. "3.1.0 – 3.1.6".</summary>
    public string? AffectedRange { get; set; }

    /// <summary>["3.1.7","3.2.4"].</summary>
    public string FixedVersionsJson { get; set; } = "[]";
    public bool ExploitedInWild { get; set; }
    public bool KevListed { get; set; }
}

public class TrTimelineEvent
{
    public static readonly string[] Levels = { "ERR", "WARN", "FIX", "INFO" };

    public int Id { get; set; }
    public int ItemId { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public string Level { get; set; } = "INFO";
    public string TextBn { get; set; } = "";
    public string? TextEn { get; set; }
}

/// <summary>
/// A public source (rule 1). <see cref="FetchedText"/> is what the number check
/// reads (rule 4); it is third-party text and is never published.
/// </summary>
public class TrSource
{
    public int Id { get; set; }
    public int? ItemId { get; set; }
    public int Order { get; set; }
    public string Url { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string? Author { get; set; }
    public string Title { get; set; } = "";

    /// <summary>The source's own date, yyyy-MM-dd, when it gives one.</summary>
    public string? PublishedOn { get; set; }

    /// <summary><see cref="TrSourceKind.All"/>. Only "claim" sources: the item must be "unconfirmed" (rule 3).</summary>
    public string Kind { get; set; } = "official";
    public string FetchedText { get; set; } = "";
    public DateTime? FetchedAtUtc { get; set; }
}

/// <summary>One status change of an item, append-only (rule 5).</summary>
public class TrStatusEvent
{
    public int Id { get; set; }
    public int ItemId { get; set; }
    public string? FromStatus { get; set; }
    public string ToStatus { get; set; } = "";
    public DateTime AtUtc { get; set; }
    public string? TextBn { get; set; }
    public string? TextEn { get; set; }
    public int ByUserId { get; set; }
}

/// <summary>A visible correction (সংশোধন, rule 5), append-only.</summary>
public class TrCorrection
{
    public int Id { get; set; }
    public int ItemId { get; set; }
    public DateTime AtUtc { get; set; }
    public string TextBn { get; set; } = "";
    public string? TextEn { get; set; }
    public int ByUserId { get; set; }
}

/// <summary>A published issue as the public saw it: immutable JSON, one row per publish.</summary>
public class TrSnapshot
{
    public int Id { get; set; }
    public int IssueId { get; set; }
    public string IssueSlug { get; set; } = "";

    /// <summary>Built by TruthSnapshot through its allow-list — nothing else is ever written here.</summary>
    public string Json { get; set; } = "";
    public string ETag { get; set; } = "";
    public int SiteContentVersion { get; set; }
    public bool Urgent { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public int PublishedByUserId { get; set; }
}

/// <summary>
/// A version of an editable content document: the site text (§5), the memes (§3A.5)
/// or the glossary (§3A.7), by <see cref="Kind"/>. Published rows are history;
/// rollback publishes an old one again.
/// </summary>
public class TrSiteContent
{
    public const string StatusDraft = "draft";
    public const string StatusPublished = "published";

    public int Id { get; set; }

    /// <summary>site, memes or glossary (Truth/TruthContent.cs).</summary>
    public string Kind { get; set; } = "site";

    /// <summary>1, 2, 3 … for published rows; a draft has 0.</summary>
    public int Version { get; set; }
    public string Json { get; set; } = "";
    public int SchemaVersion { get; set; }
    public string Status { get; set; } = StatusDraft;
    public string? Note { get; set; }
    public int EditedByUserId { get; set; }
    public DateTime EditedAtUtc { get; set; }
}
