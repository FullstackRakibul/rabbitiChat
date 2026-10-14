// RBAC — API-level tab enforcement.
//
// The frontend hides tabs a user lacks, but hiding is not a control: anyone can
// call the API directly. This middleware is the actual boundary. It maps request
// paths to the tab they belong to and refuses with 403 when the caller's
// effective tabs don't include it — so "the UI slipped" and "someone used curl"
// have the same outcome.
//
// Implemented as middleware rather than a per-endpoint filter on purpose: the
// gated endpoints are spread across several files as bare app.MapGet/MapPost
// calls with no shared group, and a rule that must be remembered at ~20 call
// sites is a rule that will eventually be forgotten at one. One table here
// cannot be forgotten, and adding a tab means adding a line to it.
//
// Requests that match no prefix are untouched, so chat, auth, models, health and
// every other shared route behave exactly as before.

using RabbitIC.Api.Data;
using RabbitIC.Api.Models;
using RabbitIC.Api.Rbac;
using RabbitIC.Api.Repositories;

namespace RabbitIC.Api.Middleware;

public class TabAccessMiddleware
{
    /// <summary>
    /// Path prefix → the tab it belongs to. Longest match wins, so a more
    /// specific prefix can carve an exception out of a broader one.
    ///
    /// Only feature APIs appear here. Deliberately absent: /api/auth (you must be
    /// able to log in), /api/me/tabs (it only describes the caller), and
    /// /api/models (Settings shows the model list to every role).
    ///
    /// /api/chat and /api/conversations ARE gated: chat is no longer part of the
    /// General baseline, so the tab and its API have to move together — hiding
    /// the tab while leaving the endpoint open would be no control at all.
    /// </summary>
    private static readonly (string Prefix, string Tab)[] GatedPrefixes =
    {
        ("/api/chat", "chat"),
        ("/api/conversations", "chat"),
        ("/api/documents", "docs"),
        ("/api/buyer-profiles", "docs"),
        ("/api/po-studio", "docs"),
        ("/api/search", "search"),
        ("/api/context-sources", "sources"),
        ("/api/todorabbit", "todo"),
        ("/api/ielts", "ielts"),
        ("/api/component-splitter", "component-splitter"),
        ("/api/notes", "notes"),
        ("/api/logs", "logs"),
        ("/api/stream/logs", "logs"),
        ("/api/oddity", "oddity"),
        ("/api/hr", "hr"),
    };

    /// <summary>
    /// Workspace lives inside the documents surface rather than under a path of
    /// its own, so it is called out explicitly instead of being swept up as "docs".
    /// </summary>
    private static readonly (string Path, string Tab)[] GatedExactPaths =
    {
        ("/api/documents/workspace-chat", "workspace"),
    };

    private readonly RequestDelegate _next;

    public TabAccessMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx, AppDbContext db, IRbacRepository repo)
    {
        var tab = TabForPath(ctx.Request.Path.Value);

        if (tab is null)
        {
            await _next(ctx);
            return;
        }

        // Identity stays the existing auth's job. An anonymous caller is passed
        // through to the endpoint, which answers its own 401 — this middleware
        // only ever decides authorisation, never authentication.
        var user = await AuthHelper.Authorization.ResolveUser(ctx, db);
        if (user is null)
        {
            await _next(ctx);
            return;
        }

        var grants = await repo.GetGrantsAsync(user.Id, ctx.RequestAborted);
        var effective = TabCatalog.EffectiveTabs(user.Role, grants);

        if (!effective.Contains(tab, StringComparer.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = $"Your role ({UserRoles.Normalize(user.Role)}) does not include the "
                      + $"{TabCatalog.LabelFor(tab)} tab.",
            });
            return;
        }

        await _next(ctx);
    }

    /// <summary>The tab a path belongs to, or null when the path isn't gated.</summary>
    private static string? TabForPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        foreach (var (exact, tab) in GatedExactPaths)
        {
            if (path.Equals(exact, StringComparison.OrdinalIgnoreCase)) return tab;
        }

        string? best = null;
        var bestLength = 0;

        foreach (var (prefix, tab) in GatedPrefixes)
        {
            // Segment-aware: "/api/searchable" must not match the "/api/search" gate.
            var isMatch = path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);

            if (isMatch && prefix.Length > bestLength)
            {
                best = tab;
                bestLength = prefix.Length;
            }
        }

        return best;
    }
}

public static class TabAccessMiddlewareExtensions
{
    /// <summary>
    /// Mount before the endpoints so a refusal happens ahead of any handler work.
    /// </summary>
    public static WebApplication UseTabAccess(this WebApplication app)
    {
        app.UseMiddleware<TabAccessMiddleware>();
        return app;
    }
}
