// Observability — EF Core audit interceptor.
//
// Captures Added/Modified/Deleted entity changes in SavingChanges, resolves
// DB-generated keys for Added entities in SavedChanges, and enqueues the audit
// entries. On SaveChangesFailed, discards pending entries (no change = no audit).
//
// Skips ActivityLog and AuditLog themselves (no recursion).
// Applies IRedactor to every property value; also checks [SensitiveData] attribute.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RabbitIC.Api.Models;
using System.Text.Json;

namespace RabbitIC.Api.Observability;

public sealed class AuditInterceptor : SaveChangesInterceptor
{
    private static readonly HashSet<string> SkippedEntities = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(ActivityLog), nameof(AuditLog),
        // A row per gateway call; auditing it would record every call twice.
        nameof(OddityRequest),
    };

    // Per-SaveChanges staging area (thread-safe via async context).
    [ThreadStatic]
    private static List<PendingAudit>? _pending;

    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IRedactor            _redactor;
    private readonly ILogQueue            _queue;
    private readonly IWebHostEnvironment  _env;

    public AuditInterceptor(
        IHttpContextAccessor httpContextAccessor,
        IRedactor            redactor,
        ILogQueue            queue,
        IWebHostEnvironment  env)
    {
        _httpContextAccessor = httpContextAccessor;
        _redactor            = redactor;
        _queue               = queue;
        _env                 = env;
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        Capture(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        Capture(eventData.Context);
        return base.SavingChangesAsync(eventData, result, ct);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Commit(eventData.Context);
        return base.SavedChanges(eventData, result);
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default)
    {
        Commit(eventData.Context);
        return base.SavedChangesAsync(eventData, result, ct);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        _pending = null; // discard — no change happened
        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData, CancellationToken ct = default)
    {
        _pending = null;
        return base.SaveChangesFailedAsync(eventData, ct);
    }

    // ---- Private helpers ----

    private void Capture(DbContext? ctx)
    {
        if (ctx is null) return;
        _pending = new List<PendingAudit>();

        foreach (var entry in ctx.ChangeTracker.Entries())
        {
            if (SkippedEntities.Contains(entry.Entity.GetType().Name)) continue;

            var action = entry.State switch
            {
                EntityState.Added    => "Added",
                EntityState.Modified => "Modified",
                EntityState.Deleted  => "Deleted",
                _                    => null,
            };
            if (action is null) continue;

            var changes = new List<PropertyChange>();

            if (action == "Added")
            {
                // Key may not be set yet (DB-generated); record placeholder.
                foreach (var prop in entry.Properties)
                {
                    var newVal = RedactValue(prop.Metadata.Name, prop.CurrentValue);
                    changes.Add(new PropertyChange(prop.Metadata.Name, null, newVal));
                }
            }
            else if (action == "Modified")
            {
                foreach (var prop in entry.Properties.Where(p => p.IsModified))
                {
                    var oldVal = RedactValue(prop.Metadata.Name, prop.OriginalValue);
                    var newVal = RedactValue(prop.Metadata.Name, prop.CurrentValue);
                    changes.Add(new PropertyChange(prop.Metadata.Name, oldVal, newVal));
                }
            }
            else // Deleted
            {
                foreach (var prop in entry.Properties)
                {
                    var oldVal = RedactValue(prop.Metadata.Name, prop.OriginalValue);
                    changes.Add(new PropertyChange(prop.Metadata.Name, oldVal, null));
                }
            }

            _pending.Add(new PendingAudit
            {
                Entry    = entry,
                Action   = action,
                Changes  = changes,
                EntityName = entry.Entity.GetType().Name,
            });
        }
    }

    private void Commit(DbContext? ctx)
    {
        var batch = _pending;
        _pending = null;
        if (batch is null || batch.Count == 0) return;

        var http = _httpContextAccessor.HttpContext;
        var correlationId = http?.Items[CorrelationMiddleware.ItemKey] as string;
        int? userId = null;
        // Best-effort user id (not worth a DB round-trip here).
        if (http?.Items["ObservabilityUserId"] is int uid) userId = uid;

        foreach (var pending in batch)
        {
            // For Added entities, update the key column now that EF has set it.
            var keyVal = pending.Entry.Properties
                .FirstOrDefault(p => p.Metadata.IsPrimaryKey())?.CurrentValue?.ToString() ?? "?";

            var audit = new AuditLog
            {
                TimestampUtc  = DateTime.UtcNow,
                CorrelationId = correlationId,
                UserId        = userId,
                EntityName    = pending.EntityName,
                EntityKey     = keyVal,
                Action        = pending.Action,
                ChangesJson   = JsonSerializer.Serialize(pending.Changes, AuditJsonOptions.Default),
                Environment   = _env.EnvironmentName,
            };

            _queue.EnqueueAudit(audit);
        }
    }

    private string RedactValue(string propertyName, object? value)
    {
        if (value is null) return "null";
        var s = value.ToString() ?? "null";

        // [SensitiveData] attribute on the property type
        if (_redactor.IsSensitive(propertyName)) return "***";
        return s;
    }
}

internal record PropertyChange(string Property, string? Old, string? New);

internal class PendingAudit
{
    public required Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry Entry { get; init; }
    public required string Action     { get; init; }
    public required string EntityName { get; init; }
    public required List<PropertyChange> Changes { get; init; }
}

internal static class AuditJsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
