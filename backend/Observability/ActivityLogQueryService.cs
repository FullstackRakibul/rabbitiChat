// Observability — REST query service.
//
// ActivityLogQueryService and AuditLogQueryService implement keyset pagination
// (cursor on Id, max 200 rows) and the /stats and /correlation queries.
// All queries run against a SEPARATE read-only scope — never the request DbContext.

using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.Data;
using RabbitIC.Api.Models;
using System.Text.Json;

namespace RabbitIC.Api.Observability;

// ---- Activity ----

public record ActivityLogFilter(
    string? Category,
    string? Level,
    string? UserId,
    string? Status,          // "2xx" | "4xx" | "5xx"
    string? Path,
    string? Module,
    string? CorrelationId,
    string? Search,
    DateTime? From,
    DateTime? To,
    long? Cursor,
    int PageSize = 50);

public record ActivityLogPage(
    IReadOnlyList<ActivityLog> Items,
    long? NextCursor);

// ---- Audit ----

public record AuditLogFilter(
    string? Entity,
    string? Key,
    string? Action,
    string? UserId,
    DateTime? From,
    DateTime? To,
    long? Cursor,
    int PageSize = 50);

public record AuditLogPage(
    IReadOnlyList<AuditLog> Items,
    long? NextCursor);

// ---- Stats ----

public record LogStats(
    int TotalRequests,
    double ErrorRate,
    double? P95DurationMs,
    IReadOnlyList<PathStat> TopPaths,
    IReadOnlyList<ModuleStat> TopModules);

public record PathStat(string Path, int Count);
public record ModuleStat(string Module, int Count);

// ---- System info ----

public record SystemInfo(
    string Environment,
    string MachineName,
    string AppVersion,
    string DotNetVersion,
    string DbProvider,
    string DatabaseName,
    string DatabaseHost,
    string MigrationStatus,
    bool DbHealthy,
    int QueueDepth,
    long DroppedCount,
    DateTime? LastWriteTime);

// ---- Service ----

public sealed class ActivityLogQueryService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogQueue            _queue;
    private readonly IConfiguration       _config;

    public ActivityLogQueryService(
        IServiceScopeFactory scopes,
        ILogQueue queue,
        IConfiguration config)
    {
        _scopes = scopes;
        _queue  = queue;
        _config = config;
    }

    public async Task<ActivityLogPage> QueryAsync(ActivityLogFilter f, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var q = db.ActivityLogs.AsNoTracking().AsQueryable();

        if (f.Cursor.HasValue) q = q.Where(a => a.Id < f.Cursor.Value);
        if (f.Category is not null) q = q.Where(a => a.Category == f.Category);
        if (f.Level is not null) q = q.Where(a => a.Level == f.Level);
        if (f.Module is not null) q = q.Where(a => a.Module == f.Module);
        if (f.CorrelationId is not null) q = q.Where(a => a.CorrelationId == f.CorrelationId);
        if (f.From.HasValue) q = q.Where(a => a.TimestampUtc >= f.From.Value);
        if (f.To.HasValue) q = q.Where(a => a.TimestampUtc <= f.To.Value);

        if (f.UserId is not null && int.TryParse(f.UserId, out var uid))
            q = q.Where(a => a.UserId == uid);

        if (f.Status is not null)
        {
            q = f.Status switch
            {
                "2xx" => q.Where(a => a.StatusCode >= 200 && a.StatusCode < 300),
                "4xx" => q.Where(a => a.StatusCode >= 400 && a.StatusCode < 500),
                "5xx" => q.Where(a => a.StatusCode >= 500),
                _     => q,
            };
        }

        if (f.Path is not null)
            q = q.Where(a => a.Path != null && EF.Functions.Like(a.Path, $"%{f.Path}%"));

        if (f.Search is not null)
            q = q.Where(a => (a.Message != null && EF.Functions.Like(a.Message, $"%{f.Search}%"))
                            || (a.Path   != null && EF.Functions.Like(a.Path,    $"%{f.Search}%")));

        var pageSize = Math.Clamp(f.PageSize, 1, 200);
        var items    = await q.OrderByDescending(a => a.Id).Take(pageSize).ToListAsync(ct);

        long? nextCursor = items.Count == pageSize ? items[^1].Id : null;
        return new ActivityLogPage(items, nextCursor);
    }

    public async Task<ActivityLog?> GetByIdAsync(long id, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ActivityLogs.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
    }

    public async Task<AuditLogPage> QueryAuditAsync(AuditLogFilter f, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var q = db.AuditLogs.AsNoTracking().AsQueryable();

        if (f.Cursor.HasValue) q = q.Where(a => a.Id < f.Cursor.Value);
        if (f.Entity is not null) q = q.Where(a => a.EntityName == f.Entity);
        if (f.Key is not null) q = q.Where(a => a.EntityKey == f.Key);
        if (f.Action is not null) q = q.Where(a => a.Action == f.Action);
        if (f.From.HasValue) q = q.Where(a => a.TimestampUtc >= f.From.Value);
        if (f.To.HasValue) q = q.Where(a => a.TimestampUtc <= f.To.Value);

        if (f.UserId is not null && int.TryParse(f.UserId, out var uid))
            q = q.Where(a => a.UserId == uid);

        var pageSize = Math.Clamp(f.PageSize, 1, 200);
        var items    = await q.OrderByDescending(a => a.Id).Take(pageSize).ToListAsync(ct);
        long? nextCursor = items.Count == pageSize ? items[^1].Id : null;
        return new AuditLogPage(items, nextCursor);
    }

    public async Task<object> GetCorrelationAsync(string correlationId, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var activities = await db.ActivityLogs.AsNoTracking()
            .Where(a => a.CorrelationId == correlationId)
            .OrderBy(a => a.TimestampUtc)
            .ToListAsync(ct);

        var audits = await db.AuditLogs.AsNoTracking()
            .Where(a => a.CorrelationId == correlationId)
            .OrderBy(a => a.TimestampUtc)
            .ToListAsync(ct);

        return new { activities, audits };
    }

    public async Task<LogStats> GetStatsAsync(DateTime? from, DateTime? to, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var q = db.ActivityLogs.AsNoTracking()
            .Where(a => a.Category == "request");

        if (from.HasValue) q = q.Where(a => a.TimestampUtc >= from.Value);
        if (to.HasValue)   q = q.Where(a => a.TimestampUtc <= to.Value);

        var all = await q
            .Select(a => new { a.StatusCode, a.DurationMs, a.Path, a.Module })
            .ToListAsync(ct);

        var total     = all.Count;
        var errors    = all.Count(a => a.StatusCode >= 400);
        var errorRate = total > 0 ? (double)errors / total : 0;

        var durations = all
            .Where(a => a.DurationMs.HasValue)
            .Select(a => a.DurationMs!.Value)
            .OrderBy(d => d)
            .ToList();
        double? p95 = durations.Count > 0
            ? durations[(int)(durations.Count * 0.95)]
            : null;

        var topPaths = all
            .Where(a => a.Path is not null)
            .GroupBy(a => a.Path!)
            .OrderByDescending(g => g.Count())
            .Take(10)
            .Select(g => new PathStat(g.Key, g.Count()))
            .ToList();

        var topModules = all
            .Where(a => a.Module is not null)
            .GroupBy(a => a.Module!)
            .OrderByDescending(g => g.Count())
            .Take(10)
            .Select(g => new ModuleStat(g.Key, g.Count()))
            .ToList();

        return new LogStats(total, errorRate, p95, topPaths, topModules);
    }

    public async Task<SystemInfo> GetSystemInfoAsync(AppDbContext requestDb, CancellationToken ct)
    {
        var dbHealthy = true;
        var migrationStatus = "EnsureCreated (no migrations)";
        var dbConnStr = requestDb.Database.GetConnectionString() ?? "";

        // Parse "Data Source=path/to/file.db" — expose only filename, never credentials.
        var dbName = "";
        var dbHost = "localhost (SQLite)";
        try
        {
            var parts = dbConnStr.Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var kv = part.Trim().Split('=', 2);
                if (kv.Length == 2 && kv[0].Trim().Equals("Data Source", StringComparison.OrdinalIgnoreCase))
                {
                    dbName = Path.GetFileName(kv[1].Trim());
                    dbHost = "localhost (SQLite file)";
                    break;
                }
            }
        }
        catch { /* best effort */ }

        try
        {
            await requestDb.Database.CanConnectAsync(ct);
        }
        catch
        {
            dbHealthy = false;
        }

        var asm  = System.Reflection.Assembly.GetExecutingAssembly();
        var ver  = asm.GetName().Version?.ToString() ?? "0.0.0.0";

        return new SystemInfo(
            Environment: System.Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production",
            MachineName: System.Net.Dns.GetHostName(),
            AppVersion:  ver,
            DotNetVersion: System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            DbProvider:  "SQLite",
            DatabaseName: dbName,
            DatabaseHost: dbHost,
            MigrationStatus: migrationStatus,
            DbHealthy:   dbHealthy,
            QueueDepth:  _queue.ActivityQueueDepth,
            DroppedCount: _queue.DroppedCount,
            LastWriteTime: _queue.LastWriteTime);
    }
}
