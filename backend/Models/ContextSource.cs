namespace RabbitIC.Api.Models;

/// <summary>
/// One named, toggleable block of text that can be prepended to a chat's system
/// prompt.
///
/// "Connected sources" and "buyer profiles" are the same mechanism seen from two
/// tabs — they differ only in <see cref="Kind"/>. Keeping them one entity is
/// deliberate: the assembly rule, the owner scoping and the ordering are written
/// once and both surfaces inherit them.
/// </summary>
public class ContextSource
{
    public int Id { get; set; }

    /// <summary>Owner — every query is scoped to this, and a foreign id reads as absent (404).</summary>
    public int OwnerUserId { get; set; }

    public string Name { get; set; } = "";

    /// <summary>"source" (chat tab) | "buyer" (workspace tab). Free-form so a third surface needs no schema change.</summary>
    public string Kind { get; set; } = KindSource;

    /// <summary>The text injected into the system prompt verbatim.</summary>
    public string Content { get; set; } = "";

    /// <summary>Default off: a newly added source must be switched on deliberately.</summary>
    public bool IsActive { get; set; }

    /// <summary>Ascending. Decides the order blocks are concatenated in.</summary>
    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public const string KindSource = "source";
    public const string KindBuyer = "buyer";
}
