// সত্য মন্ত্রণালয় — the public API, /public/truth (docs/TRUTH_PROMPT.md §4).
//
//   GET /public/truth/current          the current issue's latest snapshot
//   GET /public/truth/issues           the archive: every published issue, from its snapshot
//   GET /public/truth/issues/{slug}    one published issue's latest snapshot
//   GET /public/truth/site-content     the published site text, memes and glossary (each null: the built-in default)
//   GET /public/truth/time             { serverNow } — never cached, for the countdown (P2)
//
// The only routes the tunnel lets through (Truth/TunnelGuard.cs); on Vercel they
// are /truth-api/*, and the backend answers that spelling too (Program.cs), so
// the LAN preview and the eval read exactly what Vercel would. No login, read
// only, and only snapshots: a draft is never here (G1.2). Rate limited per
// visitor IP (truth-public policy, Program.cs).

using RabbitIC.Api.Truth;

namespace RabbitIC.Api.MinimalApis
{
    public static class TruthPublicApis
    {
        public const string RatePolicy = "truth-public";

        public static IEndpointRouteBuilder MapTruthPublicApis(this IEndpointRouteBuilder app)
        {
            var g = app.MapGroup("/truth").RequireRateLimiting(RatePolicy);

            g.MapGet("/current", async (HttpContext ctx, TruthService svc, CancellationToken ct) =>
                await svc.CurrentAsync(ct) is { } snap ? Serve(ctx, snap.Json, snap.ETag, snap.Urgent ? 60 : 300) : NotFound(ctx));

            g.MapGet("/issues", async (HttpContext ctx, TruthService svc, CancellationToken ct) =>
            {
                var json = await svc.ArchiveJsonAsync(ct);
                return Serve(ctx, json, TruthSnapshot.ETag(json), 300);
            });

            g.MapGet("/issues/{slug}", async (string slug, HttpContext ctx, TruthService svc, CancellationToken ct) =>
                await svc.IssueAsync(slug.ToLowerInvariant(), ct) is { } snap ? Serve(ctx, snap.Json, snap.ETag, snap.Urgent ? 60 : 300) : NotFound(ctx));

            g.MapGet("/site-content", async (HttpContext ctx, TruthService svc, CancellationToken ct) =>
            {
                // The site text, the memes and the glossary as published — each null while only its built-in default exists.
                var doc = new System.Text.Json.Nodes.JsonObject { ["schemaVersion"] = TruthText.SchemaVersion };
                foreach (var kind in TruthContent.Kinds)
                {
                    var row = await svc.PublishedContentAsync(kind, ct);
                    var name = kind == TruthContent.Site ? "text" : kind;
                    if (kind == TruthContent.Site) doc["version"] = row?.Version ?? 0;
                    doc[name] = row is { } r ? System.Text.Json.Nodes.JsonNode.Parse(r.Json) : null;
                    doc[$"{name}Version"] = row?.Version ?? 0;
                }
                var json = doc.ToJsonString();
                return Serve(ctx, json, TruthSnapshot.ETag(json), 60);
            });

            g.MapGet("/time", (HttpContext ctx) =>
            {
                Common(ctx);
                ctx.Response.Headers.CacheControl = "no-store";
                return Results.Json(new { serverNow = TruthJson.Utc(DateTime.UtcNow) });
            });

            return app;
        }

        /// <summary>The headers every public answer carries; X-Server-Now lets the shell correct a visitor's wrong clock.</summary>
        private static void Common(HttpContext ctx)
        {
            ctx.Response.Headers.XContentTypeOptions = "nosniff";
            ctx.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            ctx.Response.Headers["X-Server-Now"] = TruthJson.Utc(DateTime.UtcNow);
        }

        private static IResult Serve(HttpContext ctx, string json, string etag, int maxAge)
        {
            Common(ctx);
            ctx.Response.Headers.ETag = etag;
            ctx.Response.Headers.CacheControl = $"public, max-age={maxAge}";
            if (ctx.Request.Headers.IfNoneMatch.Any(v => v is not null && v.Split(',').Select(t => t.Trim()).Contains(etag)))
                return Results.StatusCode(StatusCodes.Status304NotModified);
            return Results.Text(json, "application/json; charset=utf-8");
        }

        private static IResult NotFound(HttpContext ctx)
        {
            Common(ctx);
            ctx.Response.Headers.CacheControl = "public, max-age=60";
            return Results.Json(new { error = "not found" }, statusCode: StatusCodes.Status404NotFound);
        }
    }
}
