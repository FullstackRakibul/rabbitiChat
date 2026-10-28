// সত্য মন্ত্রণালয় · রেকর্ড বিভাগ (Records Department): what the collector gathers from public
// feeds (docs/TRUTH_PROMPT_P5.md). The weekly issue stays hand-written; these rows feed it.
// Created by SchemaUpgrade.EnsureTablesFromModel(db, "Tr") like every other Tr* table.

namespace RabbitIC.Api.Models;

/// <summary>
/// The collector's memory of one registry source (Truth/Records/SourceRegistry.cs): its
/// conditional-request validators, health and schedule, and an admin's override of its
/// definition or switch. The definition itself lives in backend/config/truth-sources.json.
/// </summary>
public class TrWireSourceState
{
    public int Id { get; set; }
    public string SourceId { get; set; } = "";

    /// <summary>An admin's switch; null follows the registry file.</summary>
    public bool? Enabled { get; set; }

    /// <summary>A whole source definition added or edited in the admin, validated like the file's.</summary>
    public string? OverrideJson { get; set; }

    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    public DateTime? LastAttemptAtUtc { get; set; }
    public DateTime? LastSuccessAtUtc { get; set; }
    public DateTime? LastErrorAtUtc { get; set; }
    public string? LastError { get; set; }

    /// <summary>Failures in a row; 10 stops the source and flags it (§3.1).</summary>
    public int Failures { get; set; }
    public bool Flagged { get; set; }
    public DateTime? NextRunAtUtc { get; set; }
    public int LastDurationMs { get; set; }
}

public static class WireKind
{
    public const string Status = "status", Advisory = "advisory", Release = "release", News = "news";
    public static readonly string[] All = { Status, Advisory, Release, News };
}

public static class WireVisibility
{
    public const string Public = "public", Inbox = "inbox", Hidden = "hidden";
}

public static class WireState
{
    public const string Investigating = "investigating", Identified = "identified", Monitoring = "monitoring",
        Resolved = "resolved", Maintenance = "maintenance", Scheduled = "scheduled";
    public static readonly string[] All = { Investigating, Identified, Monitoring, Resolved, Maintenance, Scheduled };

    /// <summary>States that leave a service "incident" (maintenance is its own word; scheduled is not yet).</summary>
    public static bool IsOpen(string? s) => s is Investigating or Identified or Monitoring;
}

/// <summary>One thing a source published, normalised (§3.2). Unique per (SourceId, ExternalId).</summary>
public class TrWireEntry
{
    public int Id { get; set; }
    public string SourceId { get; set; } = "";
    public string ExternalId { get; set; } = "";
    public string Kind { get; set; } = WireKind.News;

    /// <summary>As published, in the original language.</summary>
    public string Title { get; set; } = "";

    /// <summary>The feed's own description as plain text, at most 300 characters.</summary>
    public string Excerpt { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTime PublishedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string? State { get; set; }
    public string ComponentsJson { get; set; } = "[]";
    public string CveIdsJson { get; set; } = "[]";
    public string TechsJson { get; set; } = "[]";
    public string VersionsJson { get; set; } = "[]";

    /// <summary>The incident's updates as the feed lists them (Statuspage puts them all in each item): [{at, state}].</summary>
    public string UpdatesJson { get; set; } = "[]";

    /// <summary>Numbers a cluster compares across sources (§3.3): {"fixed":[…],"patchTime":[…]}.</summary>
    public string FactsJson { get; set; } = "{}";
    public string? Severity { get; set; }
    public int? ClusterId { get; set; }
    public string Visibility { get; set; } = WireVisibility.Public;

    /// <summary>Of title + excerpt + state: a change is an update, kept in TrWireEntryHistory.</summary>
    public string ContentHash { get; set; } = "";
    public DateTime FirstSeenAtUtc { get; set; }
    public DateTime LastSeenAtUtc { get; set; }

    /// <summary>A URL drop's readable text, for the number check only. Never public.</summary>
    public string? PageText { get; set; }
}

/// <summary>An earlier version of an entry: incident updates add one, never a duplicate entry.</summary>
public class TrWireEntryHistory
{
    public int Id { get; set; }
    public int EntryId { get; set; }
    public DateTime AtUtc { get; set; }
    public string? State { get; set; }
    public string Title { get; set; } = "";
    public string Excerpt { get; set; } = "";
}

/// <summary>Entries about the same thing (§3.3): one shared CVE, or the same tech, close in time, similar titles.</summary>
public class TrWireCluster
{
    public int Id { get; set; }
    public string Key { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>[{field, values:[{value, sources:[sourceId]}]}] — sources that disagree, shown, never hidden.</summary>
    public string ConflictsJson { get; set; } = "[]";
}

/// <summary>Every hide, publish, override and decision in Records, with who and when (§5).</summary>
public class TrWireAudit
{
    public int Id { get; set; }
    public DateTime AtUtc { get; set; }
    public int UserId { get; set; }
    public string Action { get; set; } = "";
    public string Target { get; set; } = "";
    public string? Note { get; set; }
}

/// <summary>What the collector suggests to a person (§3.6). Never an action of its own.</summary>
public class TrWireSuggestion
{
    public int Id { get; set; }

    /// <summary>"urgent" or "versionLine".</summary>
    public string Kind { get; set; } = "";

    /// <summary>One suggestion per reason and subject, e.g. "kev:CVE-0000-0001".</summary>
    public string Key { get; set; } = "";
    public int? ClusterId { get; set; }
    public string? TechSlug { get; set; }
    public string Reason { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";

    /// <summary>open / done / ignored.</summary>
    public string Status { get; set; } = "open";
    public DateTime CreatedAtUtc { get; set; }
    public int? DecidedByUserId { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
}

/// <summary>A model's 2–3 line summary of a cluster (§3.5): a draft until a person makes it an item.</summary>
public class TrWireDigest
{
    public int Id { get; set; }
    public int ClusterId { get; set; }
    public string TextBn { get; set; } = "";
    public string TextEn { get; set; } = "";
    public string Model { get; set; } = "";

    /// <summary>Numbers in the draft no source text has (the number check, rule 4).</summary>
    public string NumberProblemsJson { get; set; } = "[]";

    /// <summary>The draft item made from it, once a person did.</summary>
    public int? ItemId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public int CreatedByUserId { get; set; }
}
