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

    // ---- PO Studio: the knowledge the profile carries into the prompt ----

    /// <summary>
    /// The Ollama model for this buyer. Empty means the install default
    /// (Extraction:DefaultModel, then Ollama:ExtractionModel).
    /// </summary>
    public string ExtractionModel { get; set; } = "";

    /// <summary>Buyer-specific instructions, plain text, appended to the base rules.</summary>
    public string Rules { get; set; } = "";

    /// <summary>"term = meaning" lines, one per line.</summary>
    public string Glossary { get; set; } = "";

    /// <summary>
    /// Up to 2 confirmed ExtractionRun ids used as worked examples, comma-separated
    /// and ordered. Only confirmed, non-held-out runs may appear here.
    /// </summary>
    public string ExampleRunIds { get; set; } = "";

    /// <summary>Bumped on every save, so a run can record which revision made it.</summary>
    public int Version { get; set; } = 1;
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

    // ---- PO Studio: how this run was made ----
    // Without these, two runs' accuracy cannot be compared: a change in the
    // model or the profile would be invisible.

    /// <summary>The Ollama model that produced ResultJson, when the model path ran.</summary>
    public string? Model { get; set; }

    /// <summary>BuyerProfile.Version at the time of the run.</summary>
    public int? ProfileVersion { get; set; }

    /// <summary>SHA-256 of the assembled system prompt.</summary>
    public string? PromptHash { get; set; }

    /// <summary>True when the repair round's answer was the one kept.</summary>
    public bool RepairUsed { get; set; }

    /// <summary>The model's first answer, when a repair round followed it.</summary>
    public string? FirstResultJson { get; set; }

    public int? PromptTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? DurationMs { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
