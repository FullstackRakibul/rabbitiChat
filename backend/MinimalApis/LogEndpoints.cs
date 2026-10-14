// Observability — REST endpoints and SSE live stream.
//
// All endpoints require the caller to be authenticated and to have the "logs"
// tab (enforced by TabAccessMiddleware). The audit endpoints additionally check
// that the user is SystemAdmin (the "ViewAudit" stricter permission).
//
// POST /api/logs/client  — batch client events (authenticated, rate-limited, ≤50)
// GET  /api/logs/activity          — paginated activity log
// GET  /api/logs/activity/{id}     — single entry
// GET  /api/logs/audit             — paginated audit log (SystemAdmin)
// GET  /api/logs/correlation/{id}  — request + audit entries for a correlation id
// GET  /api/logs/system-info       — environment, DB health, queue depth
// GET  /api/logs/stats             — summary stats
// GET  /api/stream/logs            — SSE live tail (authenticated, "logs" tab)

using System.Collections.Concurrent;
using System.Text.Json;
using RabbitIC.Api.AuthHelper;
using RabbitIC.Api.Data;
using RabbitIC.Api.Models;
using RabbitIC.Api.Observability;

namespace RabbitIC.Api.MinimalApis;

public record ClientEventInput(
    string EventName,
    string? Module,
    DateTime? TimestampUtc,
    Dictionary<string, object?>? Properties);

public static class LogEndpoints
{
    // Per-user rate limiter for client events: (userId, windowStart, count)
    private static readonly ConcurrentDictionary<int, (int Count, DateTime WindowStart)> _clientRateLimits = new();

    public static IEndpointRouteBuilder MapLogApis(this IEndpointRouteBuilder app)
    {
        // ---- Client events ----

        app.MapPost("/api/logs/client", async (
            List<ClientEventInput> batch,
            HttpContext ctx,
            AppDbContext db,
            ILogQueue queue,
            IRedactor redactor,
            IWebHostEnvironment env) =>
        {
            var user = await Authorization.ResolveUser(ctx, db);
            if (user is null) return Results.Unauthorized();

            // Rate limit: 100 events / minute per user
            var now = DateTime.UtcNow;
            var rl = _clientRateLimits.AddOrUpdate(user.Id,
                _ => (batch.Count, now),
                (_, old) => (now - old.WindowStart).TotalMinutes > 1
                    ? (batch.Count, now)
                    : (old.Count + batch.Count, old.WindowStart));

            if (rl.Count > 100) return Results.StatusCode(429);

            // Batch size cap
            if (batch.Count > 50)
                return Results.BadRequest(new { error = "Batch must not exceed 50 events." });

            var correlationId = ctx.Items[CorrelationMiddleware.ItemKey] as string;

            foreach (var item in batch)
            {
                var propsJson = item.Properties is not null
                    ? redactor.RedactJson(JsonSerializer.Serialize(item.Properties))
                    : null;

                queue.Enqueue(new ActivityLog
                {
                    TimestampUtc  = item.TimestampUtc?.ToUniversalTime() ?? DateTime.UtcNow,
                    Level         = "Information",   // clients cannot exceed Warning
                    Category      = "client",         // always "client", clients cannot override
                    CorrelationId = correlationId,
                    UserId        = user.Id,
                    UserName      = user.Username,
                    EventName     = item.EventName?.Length > 200 ? item.EventName[..200] : item.EventName,
                    Module        = item.Module?.Length > 100 ? item.Module[..100] : item.Module,
                    PropertiesJson = propsJson,
                    Environment   = env.EnvironmentName,
                    MachineName   = System.Net.Dns.GetHostName(),
                });
            }

            return Results.NoContent();
        });

        // ---- Activity log ----

        app.MapGet("/api/logs/activity", async (
            string? category, string? level, string? userId, string? status,
            string? path, string? module, string? correlationId, string? search,
            DateTime? from, DateTime? to, long? cursor, int? pageSize,
            HttpContext ctx, AppDbContext db,
            ActivityLogQueryService svc, CancellationToken ct) =>
        {
            var user = await Authorization.ResolveUser(ctx, db);
            if (user is null) return Results.Unauthorized();

            var filter = new ActivityLogFilter(
                category, level, userId, status, path, module, correlationId, search,
                from, to, cursor, pageSize ?? 50);

            var page = await svc.QueryAsync(filter, ct);
            return Results.Ok(page);
        });

        app.MapGet("/api/logs/activity/{id:long}", async (
            long id,
            HttpContext ctx, AppDbContext db,
            ActivityLogQueryService svc, CancellationToken ct) =>
        {
            var user = await Authorization.ResolveUser(ctx, db);
            if (user is null) return Results.Unauthorized();

            var entry = await svc.GetByIdAsync(id, ct);
            return entry is null ? Results.NotFound() : Results.Ok(entry);
        });

        // ---- Audit log (SystemAdmin only) ----

        app.MapGet("/api/logs/audit", async (
            string? entity, string? key, string? action, string? userId,
            DateTime? from, DateTime? to, long? cursor, int? pageSize,
            HttpContext ctx, AppDbContext db,
            ActivityLogQueryService svc, CancellationToken ct) =>
        {
            var user = await Authorization.ResolveUser(ctx, db);
            if (user is null) return Results.Unauthorized();
            if (!RabbitIC.Api.Models.UserRoles.IsSystemAdmin(user))
                // Not Results.Forbid(): it challenges an authentication scheme, and
                // none is registered here, so it throws a 500 instead of refusing.
                // Same shape the RBAC routes return.
                return Results.Json(
                    new { error = "SystemAdmin role required." },
                    statusCode: StatusCodes.Status403Forbidden);

            var filter = new AuditLogFilter(entity, key, action, userId, from, to, cursor, pageSize ?? 50);
            var page   = await svc.QueryAuditAsync(filter, ct);
            return Results.Ok(page);
        });

        // ---- Correlation drill-down ----

        app.MapGet("/api/logs/correlation/{correlationId}", async (
            string correlationId,
            HttpContext ctx, AppDbContext db,
            ActivityLogQueryService svc, CancellationToken ct) =>
        {
            var user = await Authorization.ResolveUser(ctx, db);
            if (user is null) return Results.Unauthorized();

            var result = await svc.GetCorrelationAsync(correlationId, ct);
            return Results.Ok(result);
        });

        // ---- System info ----

        app.MapGet("/api/logs/system-info", async (
            HttpContext ctx, AppDbContext db,
            ActivityLogQueryService svc, CancellationToken ct) =>
        {
            var user = await Authorization.ResolveUser(ctx, db);
            if (user is null) return Results.Unauthorized();

            var info = await svc.GetSystemInfoAsync(db, ct);
            return Results.Ok(info);
        });

        // ---- Stats ----

        app.MapGet("/api/logs/stats", async (
            DateTime? from, DateTime? to,
            HttpContext ctx, AppDbContext db,
            ActivityLogQueryService svc, CancellationToken ct) =>
        {
            var user = await Authorization.ResolveUser(ctx, db);
            if (user is null) return Results.Unauthorized();

            var stats = await svc.GetStatsAsync(from, to, ct);
            return Results.Ok(stats);
        });

        // ---- SSE live stream ----

        app.MapGet("/api/stream/logs", async (
            string? category, string? minLevel,
            HttpContext ctx, AppDbContext db,
            LogStreamBroadcaster broadcaster) =>
        {
            var user = await Authorization.ResolveUser(ctx, db);
            if (user is null)
            {
                ctx.Response.StatusCode = 401;
                return;
            }

            ctx.Response.Headers["Content-Type"]  = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            var clientId = Guid.NewGuid().ToString("N");
            var client   = new SseClient(ctx.Response)
            {
                CategoryFilter = category,
                MinLevelFilter = minLevel,
            };

            broadcaster.AddClient(clientId, client);

            try
            {
                // Keep the connection alive until the client disconnects.
                await Task.Delay(Timeout.Infinite, ctx.RequestAborted);
            }
            catch (OperationCanceledException) { }
            finally
            {
                broadcaster.RemoveClient(clientId);
            }
        });

        return app;
    }
}
