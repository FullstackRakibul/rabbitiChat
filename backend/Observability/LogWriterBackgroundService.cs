// Observability — background drain worker.
//
// Drains the ILogQueue in batches (≤200 entries or every 1 s), bulk-inserts
// into a SEPARATE DbContext scope (never the request's context), then
// broadcasts to live SSE subscribers.
//
// Failures:
//   - DB write failure: logged to DynamicLogWriter (file), never rethrown.
//   - Full queue / drops: one "system" warning per minute via DynamicLogWriter.
//
// Retention: a daily timer deletes ActivityLog older than N days and AuditLog
// older than M days, in batches to avoid locking.

using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.Data;
using RabbitIC.Api.Models;

namespace RabbitIC.Api.Observability;

public sealed class LogWriterBackgroundService : BackgroundService
{
    private const int BatchSize        = 200;
    private const int DrainIntervalMs  = 1_000;
    private const int RetentionCheckHr = 24;

    private readonly ILogQueue          _queue;
    private readonly LogStreamBroadcaster _broadcaster;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<LogWriterBackgroundService> _logger;
    private readonly IConfiguration     _config;

    private DateTime _lastDropWarning = DateTime.MinValue;
    private DateTime _lastRetention   = DateTime.MinValue;

    public LogWriterBackgroundService(
        ILogQueue queue,
        LogStreamBroadcaster broadcaster,
        IServiceScopeFactory scopes,
        ILogger<LogWriterBackgroundService> logger,
        IConfiguration config)
    {
        _queue       = queue;
        _broadcaster = broadcaster;
        _scopes      = scopes;
        _logger      = logger;
        _config      = config;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("Observability log writer started.");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(DrainIntervalMs, ct);
                await DrainAsync(ct);
                MaybeWarnDrops();
                await MaybeRunRetentionAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let the background loop die.
                _logger.LogError(ex, "Observability drain loop encountered an unexpected error.");
            }
        }

        // Final drain on graceful shutdown.
        await DrainAsync(CancellationToken.None);
        _logger.LogInformation("Observability log writer stopped.");
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        var activityBatch = new List<ActivityLog>(BatchSize);
        var auditBatch    = new List<AuditLog>(BatchSize / 5);

        _queue.TryDrainActivity(activityBatch, BatchSize);
        _queue.TryDrainAudit(auditBatch, BatchSize / 5);

        if (activityBatch.Count == 0 && auditBatch.Count == 0) return;

        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            if (activityBatch.Count > 0)
            {
                await db.ActivityLogs.AddRangeAsync(activityBatch, ct);
            }
            if (auditBatch.Count > 0)
            {
                await db.AuditLogs.AddRangeAsync(auditBatch, ct);
            }

            await db.SaveChangesAsync(ct);
            _queue.LastWriteTime = DateTime.UtcNow;

            if (activityBatch.Count > 0)
            {
                await _broadcaster.BroadcastAsync(activityBatch);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Observability: DB write failed for {ActivityCount} activity and {AuditCount} audit entries.",
                activityBatch.Count, auditBatch.Count);
            // Entries are silently lost — better than retrying infinitely or
            // blocking the queue with entries that will never write.
        }
    }

    private void MaybeWarnDrops()
    {
        var drops = _queue.DroppedCount;
        if (drops == 0) return;
        if ((DateTime.UtcNow - _lastDropWarning).TotalMinutes < 1) return;

        _lastDropWarning = DateTime.UtcNow;
        _queue.ResetDroppedCount();
        _logger.LogWarning(
            "Observability: log queue full — {DropCount} entries dropped in the last minute.",
            drops);
    }

    private async Task MaybeRunRetentionAsync(CancellationToken ct)
    {
        if ((DateTime.UtcNow - _lastRetention).TotalHours < RetentionCheckHr) return;
        _lastRetention = DateTime.UtcNow;

        var activityDays = _config.GetValue<int>("Observability:ActivityRetentionDays", 30);
        var auditDays    = _config.GetValue<int>("Observability:AuditRetentionDays", 365);

        var activityCutoff = DateTime.UtcNow.AddDays(-activityDays);
        var auditCutoff    = DateTime.UtcNow.AddDays(-auditDays);

        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Delete in batches to avoid a long-running lock.
            int deleted;
            do
            {
                deleted = await db.ActivityLogs
                    .Where(a => a.TimestampUtc < activityCutoff)
                    .Take(500)
                    .ExecuteDeleteAsync(ct);
            } while (deleted > 0);

            do
            {
                deleted = await db.AuditLogs
                    .Where(a => a.TimestampUtc < auditCutoff)
                    .Take(500)
                    .ExecuteDeleteAsync(ct);
            } while (deleted > 0);

            _logger.LogInformation(
                "Observability retention: deleted activity older than {ActivityDays}d, audit older than {AuditDays}d.",
                activityDays, auditDays);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Observability: retention job failed.");
        }
    }
}
