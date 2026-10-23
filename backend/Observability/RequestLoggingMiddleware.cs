// Observability — request logging middleware.
//
// Logs every inbound HTTP request as Category "request" after the response
// completes. Never touches the request or response body. Never logs the
// Authorization header. Enqueues only — no DB work on the request path.
//
// Excluded paths (no log rows):
//   /api/logs/*     (would create infinite recursion)
//   /api/stream/logs (SSE endpoint)
//   /swagger*
//   /hubs/*
//   Static files and health endpoints
//   Paths ending in known static extensions (.js, .css, .png, etc.)

using System.Diagnostics;
using RabbitIC.Api.AuthHelper;
using RabbitIC.Api.Data;
using RabbitIC.Api.Models;

namespace RabbitIC.Api.Observability;

public sealed class RequestLoggingMiddleware
{
    private static readonly HashSet<string> ExcludedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".js", ".mjs", ".css", ".png", ".jpg", ".jpeg", ".svg", ".ico",
        ".woff", ".woff2", ".ttf", ".eot", ".map", ".webp", ".gif"
    };

    private readonly RequestDelegate _next;

    public RequestLoggingMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(
        HttpContext ctx,
        ILogQueue queue,
        IRedactor redactor,
        AppDbContext db,
        IWebHostEnvironment env)
    {
        var path = ctx.Request.Path.Value ?? "/";

        if (ShouldExclude(path))
        {
            await _next(ctx);
            return;
        }

        var sw = Stopwatch.StartNew();
        await _next(ctx);
        sw.Stop();

        // Read user lazily — Authorization header is NEVER logged.
        User? user = null;
        try
        {
            user = await Authorization.ResolveUser(ctx, db);
        }
        catch { /* best effort */ }

        var correlationId = ctx.Items[CorrelationMiddleware.ItemKey] as string;
        var publicVisitor = RabbitIC.Api.Truth.TunnelGuard.IsTunnel(ctx)
            || RabbitIC.Api.Truth.TunnelGuard.IsPublicTruthPath(path)
            || path.StartsWith("/truth", StringComparison.OrdinalIgnoreCase);
        var rawQuery = ctx.Request.QueryString.Value;

        var entry = new ActivityLog
        {
            TimestampUtc  = DateTime.UtcNow,
            Level         = ctx.Response.StatusCode >= 500 ? "Error"
                          : ctx.Response.StatusCode >= 400 ? "Warning"
                          : "Information",
            Category      = "request",
            CorrelationId = correlationId,
            UserId        = user?.Id,
            UserName      = user?.Username,
            Method        = ctx.Request.Method,
            Path          = path,
            QueryString   = redactor.RedactQueryString(rawQuery),
            StatusCode    = ctx.Response.StatusCode,
            DurationMs    = sw.ElapsedMilliseconds,
            // The Ministry of Truth's visitors are the public: no IP, no user agent (docs/TRUTH_PROMPT.md §7).
            ClientIp      = publicVisitor ? null : ctx.Connection.RemoteIpAddress?.ToString(),
            UserAgent     = publicVisitor ? null : ctx.Request.Headers.UserAgent.ToString().TruncateTo(512),
            Module        = DeriveModule(path),
            Environment   = env.EnvironmentName,
            MachineName   = System.Net.Dns.GetHostName(),
        };

        queue.Enqueue(entry);
    }

    private static bool ShouldExclude(string path)
    {
        if (path.StartsWith("/api/logs", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith("/api/stream/logs", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith("/hubs", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith("/health", StringComparison.OrdinalIgnoreCase)) return true;

        var ext = Path.GetExtension(path);
        if (!string.IsNullOrEmpty(ext) && ExcludedExtensions.Contains(ext)) return true;

        return false;
    }

    /// <summary>
    /// Derives a human-readable module name from the route path so the viewer
    /// can filter "all Rbac traffic" or "all IELTS traffic" without regex.
    /// </summary>
    private static string? DeriveModule(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return null;

        // /api/<module>/...
        if (!string.Equals(segments[0], "api", StringComparison.OrdinalIgnoreCase)) return null;

        return segments[1] switch
        {
            "chat"               => "Chat",
            "conversations"      => "Chat",
            "documents"          => "Docs",
            "buyer-profiles"     => "Docs",
            "search"             => "Search",
            "context-sources"    => "Sources",
            "todorabbit"         => "TodoRabbit",
            "ielts"              => "Ielts",
            "component-splitter" => "ComponentSplitter",
            "notes"              => "Notes",
            "logs"               => "Observability",
            "rbac"               => "Rbac",
            "me"                 => "Auth",
            "auth"               => "Auth",
            "admin"              => "Rbac",
            _                    => segments[1],
        };
    }
}

internal static class StringExtensionsForLogging
{
    public static string TruncateTo(this string s, int max) =>
        s.Length <= max ? s : s[..max];
}

public static class RequestLoggingMiddlewareExtensions
{
    public static WebApplication UseRequestLogging(this WebApplication app)
    {
        app.UseMiddleware<RequestLoggingMiddleware>();
        return app;
    }
}
