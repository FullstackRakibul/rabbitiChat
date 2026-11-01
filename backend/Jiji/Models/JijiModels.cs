// Jiji — notes, books and thoughts into English YouTube Shorts (docs/JIJI_PLAN.md).
//
// Three tables, no foreign keys to other modules: ownership is the user id only.
// Every column the week-one plan needs is here from day one, so later days add
// behaviour, not ALTERs. Times are UTC (marked so on the way out, AppDbContext).

namespace RabbitIC.Api.Jiji.Models;

public static class JijiContentType
{
    public const string TechExplainer = "TechExplainer";
    public const string BuildLog = "BuildLog";
    public const string BookInsight = "BookInsight";
    public const string OriginalThought = "OriginalThought";

    public static readonly string[] All = { TechExplainer, BuildLog, BookInsight, OriginalThought };
}

/// <summary>
/// idea → script_draft → script_approved → voiced → rendered → video_approved → scheduled → published.
/// A failure sets LastError and keeps the last good status; a rejection goes back to script_draft.
/// Only the publication check sets published, never the upload.
/// </summary>
public static class JijiStatus
{
    public const string Idea = "idea";
    public const string ScriptDraft = "script_draft";
    public const string ScriptApproved = "script_approved";
    public const string Voiced = "voiced";
    public const string Rendered = "rendered";
    public const string VideoApproved = "video_approved";
    public const string Scheduled = "scheduled";
    public const string Published = "published";

    public static readonly string[] All =
        { Idea, ScriptDraft, ScriptApproved, Voiced, Rendered, VideoApproved, Scheduled, Published };
}

public static class JijiJobType
{
    public const string Tts = "Tts";
    public const string Render = "Render";
    public const string Upload = "Upload";
}

public static class JijiJobStatus
{
    public const string Pending = "Pending";
    public const string Running = "Running";
    public const string Done = "Done";
    public const string Failed = "Failed";

    /// <summary>An upload whose result nobody can tell. Never retried automatically: resolve-upload settles it.</summary>
    public const string UnknownOutcome = "UnknownOutcome";
}

public class JijiContent
{
    public int Id { get; set; }
    public int OwnerId { get; set; }

    /// <summary>One of <see cref="JijiContentType"/>.</summary>
    public string ContentType { get; set; } = JijiContentType.TechExplainer;
    public string Subject { get; set; } = "";
    public string Language { get; set; } = "en";
    public string? Angle { get; set; }

    /// <summary>What the script may build from. The prompt must not invent beyond it.</summary>
    public string SourceNotes { get; set; } = "";

    /// <summary>The script (hook, originalAngle, beats, myTake, cta …). The only home of myTake.</summary>
    public string? ScriptJson { get; set; }

    /// <summary>Raised on every script approval; jobs carry the revision they were made for.</summary>
    public int Revision { get; set; }

    /// <summary>One of <see cref="JijiStatus"/>.</summary>
    public string Status { get; set; } = JijiStatus.Idea;

    public string? Title { get; set; }
    public string? Description { get; set; }

    /// <summary>JSON array of topic tags.</summary>
    public string TagsJson { get; set; } = "[]";
    public DateTime? PublishAtUtc { get; set; }
    public bool ContainsSyntheticMedia { get; set; }
    public bool MadeForKids { get; set; }

    /// <summary>final.mp4, relative to Jiji:StoragePath.</summary>
    public string? VideoPath { get; set; }

    /// <summary>Internal only (database and logs); never in public tags or the description.</summary>
    public string UploadToken { get; set; } = "";
    public string? YouTubeVideoId { get; set; }
    public string? RejectReason { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class JijiJob
{
    public int Id { get; set; }
    public int ContentId { get; set; }
    public int ContentRevision { get; set; }

    /// <summary>One of <see cref="JijiJobType"/>. Unique with ContentId and ContentRevision.</summary>
    public string Type { get; set; } = JijiJobType.Tts;

    /// <summary>One of <see cref="JijiJobStatus"/>.</summary>
    public string Status { get; set; } = JijiJobStatus.Pending;
    public int Attempts { get; set; }
    public DateTime AvailableAtUtc { get; set; }
    public DateTime? LeaseUntilUtc { get; set; }
    public string? LeasedBy { get; set; }

    /// <summary>New for every claim; heartbeat, complete and fail match on it, so a reclaimed job's old result is dropped.</summary>
    public string? LeaseToken { get; set; }

    /// <summary>The resumable upload session URI, Data Protection (Jiji.YouTube). Saved before any byte is sent.</summary>
    public string? UploadSessionUriProtected { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? LastError { get; set; }
}

/// <summary>One connected channel per user. The OAuth state columns hold a one-time, 10-minute value.</summary>
public class JijiYouTubeAccount
{
    public int OwnerId { get; set; }
    public string? ChannelId { get; set; }
    public string? ChannelTitle { get; set; }
    public string? RefreshTokenProtected { get; set; }
    public DateTime? ConnectedAt { get; set; }
    public string? OAuthStateHash { get; set; }
    public DateTime? OAuthStateExpiresAtUtc { get; set; }
    public string? OAuthCodeVerifierProtected { get; set; }
}
