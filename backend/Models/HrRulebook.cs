namespace RabbitIC.Api.Models;

// মানব সম্পদ · নিয়মাবলি (Rulebook) — docs/HR_RULEBOOK_PROMPT.md §3.1.
//
// HR's own rule pages. The page row says where a page stands; every word it ever
// held is in HrRulePageVersions, which is append-only: no route, repository
// method or SQL statement updates or deletes a version, and SQLite triggers
// refuse it if anything tries. The Library item a confirmed page feeds is a copy;
// these two tables are the source of truth.

public class HrRulePage
{
    public int Id { get; set; }

    /// <summary>Unique among pages that are not retired (case-insensitive).</summary>
    public string Title { get; set; } = "";

    /// <summary>One of HrRulebook.Categories.</summary>
    public string Category { get; set; } = "other";

    /// <summary>"bn" or "en": the language the page is written in.</summary>
    public string Language { get; set; } = "en";

    /// <summary>"draft", "confirmed" or "retired".</summary>
    public string Status { get; set; } = StatusDraft;

    /// <summary>The latest version number.</summary>
    public int CurrentVersion { get; set; }

    /// <summary>The version the Library and Ask use; null until first confirmed.</summary>
    public int? ConfirmedVersion { get; set; }

    /// <summary>The Library item made on first confirmation; it is updated, never replaced.</summary>
    public int? LibraryItemId { get; set; }

    public int CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public const string StatusDraft = "draft";
    public const string StatusConfirmed = "confirmed";
    public const string StatusRetired = "retired";
}

/// <summary>One saved state of a page. Insert-only; (PageId, Version) is the key.</summary>
public class HrRulePageVersion
{
    public int PageId { get; set; }

    /// <summary>1, 2, 3… with no gaps.</summary>
    public int Version { get; set; }

    /// <summary>written, generated, edited, confirmed, retired or restored.</summary>
    public string Kind { get; set; } = KindWritten;

    /// <summary>The whole page as Markdown.</summary>
    public string Body { get; set; } = "";

    /// <summary>Why — required for confirmed, retired and restored.</summary>
    public string? ChangeNote { get; set; }

    public int AuthorUserId { get; set; }
    public DateTime AtUtc { get; set; }

    /// <summary>Generated versions only (Phase R2): the Library items and SourceRefs used.</summary>
    public string? SourcesJson { get; set; }

    /// <summary>Generated versions only (Phase R2): model, prompt hash, tokens, duration.</summary>
    public string? ModelJson { get; set; }

    public const string KindWritten = "written";
    public const string KindGenerated = "generated";
    public const string KindEdited = "edited";
    public const string KindConfirmed = "confirmed";
    public const string KindRetired = "retired";
    public const string KindRestored = "restored";
}
