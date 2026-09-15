namespace RabbitIC.Api.Models;

/// <summary>
/// Routing table between an uploaded PO and the parser that understands its
/// layout. Held in the database rather than code so adding a buyer is a row, not
/// a deploy.
/// </summary>
public class BuyerProfile
{
    public int Id { get; set; }

    /// <summary>Display name, e.g. "Kohls". Unique.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Pipe-separated, case-insensitive markers matched against page-1 text and
    /// the PDF producer, e.g. "SPS Commerce|Kohls|Order #:". Scored as a fraction
    /// matched; a majority is required before a template is trusted.
    /// </summary>
    public string DetectPattern { get; set; } = "";

    /// <summary>An IPoParser.Key, or "GenericLlm" for the fallback path.</summary>
    public string ParserKey { get; set; } = "GenericLlm";

    /// <summary>"none" for structured POs (never chunk), "hierarchical" for prose.</summary>
    public string ChunkingStrategy { get; set; } = "none";

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>Audit trail — every extraction run is recorded, clean or not.</summary>
public class ExtractionRun
{
    public int Id { get; set; }
    public int DocumentId { get; set; }
    public int OwnerUserId { get; set; }
    public int? BuyerProfileId { get; set; }
    public string ParserKey { get; set; } = "";

    /// <summary>"ok" | "needs_review" | "failed"</summary>
    public string Status { get; set; } = "";

    public string ResultJson { get; set; } = "";
    public string FailedChecks { get; set; } = "";

    // Human sign-off. ResultJson above is never overwritten — the machine output
    // stays auditable beside the corrected version.
    public string? ConfirmedJson { get; set; }
    public int? ConfirmedByUserId { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    public string? EditsJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
