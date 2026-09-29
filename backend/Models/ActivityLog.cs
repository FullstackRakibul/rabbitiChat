// Observability — activity and audit log entities.
//
// ActivityLog: every inbound HTTP request, client-submitted event, and
// app/system log entry. High-volume; keyset-paginated; retained N days.
//
// AuditLog: EF Core entity mutations (Added/Modified/Deleted) with old→new
// diffs. Retained M days (default 365, longer than activity).

using System.ComponentModel.DataAnnotations;

namespace RabbitIC.Api.Models;

/// <summary>
/// A single observable event. Category drives how the row was produced;
/// Level mirrors ILogger's LogLevel so the viewer can filter uniformly.
/// </summary>
public class ActivityLog
{
    public long   Id             { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Trace | Debug | Information | Warning | Error | Critical</summary>
    public string Level          { get; set; } = "Information";

    /// <summary>request | client | app | system</summary>
    public string Category       { get; set; } = "request";

    public string? CorrelationId { get; set; }
    public int?    UserId        { get; set; }
    public string? UserName      { get; set; }

    // HTTP request fields (null for non-request categories)
    public string? Method        { get; set; }
    public string? Path          { get; set; }

    /// <summary>Query string with sensitive params replaced by ***.</summary>
    public string? QueryString   { get; set; }
    public int?    StatusCode    { get; set; }
    public long?   DurationMs    { get; set; }
    public string? ClientIp      { get; set; }
    public string? UserAgent     { get; set; }

    /// <summary>Route-derived module, e.g. "Rbac", "Notes", "IELTS".</summary>
    public string? Module        { get; set; }

    // App / client event fields
    public string? EventName     { get; set; }

    [MaxLength(4000)]
    public string? Message       { get; set; }

    /// <summary>JSON blob with sensitive keys replaced by ***.</summary>
    public string? PropertiesJson { get; set; }

    public string  Environment   { get; set; } = "";
    public string  MachineName   { get; set; } = "";
}

/// <summary>
/// Records an EF Core entity mutation produced by the SaveChangesInterceptor.
/// ChangesJson holds [{property, old, new}] with sensitive properties as ***.
/// </summary>
public class AuditLog
{
    public long     Id            { get; set; }
    public DateTime TimestampUtc  { get; set; } = DateTime.UtcNow;
    public string?  CorrelationId { get; set; }
    public int?     UserId        { get; set; }
    public string   EntityName    { get; set; } = "";
    public string   EntityKey     { get; set; } = "";

    /// <summary>Added | Modified | Deleted</summary>
    public string   Action        { get; set; } = "";

    /// <summary>JSON array of {property, old, new} — sensitive values are ***.</summary>
    public string?  ChangesJson   { get; set; }

    public string   Environment   { get; set; } = "";
}

/// <summary>
/// Marks an entity property as always-redacted in audit ChangesJson.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public class SensitiveDataAttribute : Attribute { }
