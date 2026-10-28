// সত্য মন্ত্রণালয় — the public API, /public/truth (docs/TRUTH_PROMPT.md §4).
//
//   GET /public/truth/current          the current issue's latest snapshot
//   GET /public/truth/issues           the archive: every published issue, from its snapshot
//   GET /public/truth/issues/{slug}    one published issue's latest snapshot
//   GET /public/truth/site-content     the published site text, memes and glossary (each null: the built-in default)
//   GET /public/truth/time             { serverNow } — never cached, for the countdown
//   GET /public/truth/guides           every published guide (the guides list, the cards' buttons)
//   GET /public/truth/guides/{slug}    one published guide's latest snapshot
//   GET /public/truth/share/issue/{slug|current}?lang=   a plain HTML page with OG tags, no JS (/truth/s/issue/…)
//   GET /public/truth/share/guide/{slug}?lang=           the same for a guide, at its default platform and versions
//   GET /public/truth/feed.xml?lang=bn|en                RSS 2.0: issues and guides
//   GET /public/truth/status           the Records Department's live status chips (P5 §4.1), 60 s
//   GET /public/truth/wire?cursor=&tech=&type=&severity=&tier=&open=   public wire rows, 50 a page (§4.2), 60 s
//   GET /public/truth/wire/{clusterId} one row expanded: every source, history, "Sources differ"
//
// The only routes the tunnel lets through (Truth/TunnelGuard.cs); on Vercel they
// are /truth-api/*, and the backend answers that spelling too (Program.cs), so
// the LAN preview and the eval read exactly what Vercel would. No login, read
// only, and only snapshots: a draft is never here (G1.2). Rate limited per
// visitor IP (truth-public policy, Program.cs).

using RabbitIC.Api.Truth;
using RabbitIC.Api.Truth.Records;

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

            g.MapGet("/guides", async (HttpContext ctx, GuideService guides, CancellationToken ct) =>
            {
                var json = await guides.IndexJsonAsync(ct);
                return Serve(ctx, json, TruthSnapshot.ETag(json), 300);
            });

            g.MapGet("/guides/{slug}", async (string slug, HttpContext ctx, GuideService guides, CancellationToken ct) =>
                await guides.PublishedSnapshotAsync(slug.ToLowerInvariant(), ct) is { } snap ? Serve(ctx, snap.Json, snap.ETag, 300) : NotFound(ctx));

            g.MapGet("/share/issue/{slug}", async (string slug, string? lang, HttpContext ctx, TruthService svc, IConfiguration config, CancellationToken ct) =>
            {
                var snap = slug == "current" ? await svc.CurrentAsync(ct) : await svc.IssueAsync(slug.ToLowerInvariant(), ct);
                if (snap is null) return SharePageMissing(ctx);
                var l = lang == "en" ? "en" : "bn";
                return Html(ctx, TruthShare.IssuePage(System.Text.Json.Nodes.JsonNode.Parse(snap.Json)!.AsObject(), await SiteTextAsync(svc, ct), l, BaseUrl(config), slug.ToLowerInvariant()));
            });

            g.MapGet("/share/guide/{slug}", async (string slug, string? lang, HttpContext ctx, TruthService svc, GuideService guides, IConfiguration config, CancellationToken ct) =>
            {
                if (await guides.PublishedSnapshotAsync(slug.ToLowerInvariant(), ct) is not { } snap) return SharePageMissing(ctx);
                var l = lang == "en" ? "en" : "bn";
                return Html(ctx, TruthShare.GuidePage(System.Text.Json.Nodes.JsonNode.Parse(snap.Json)!.AsObject(), await SiteTextAsync(svc, ct), l, BaseUrl(config)));
            });

            g.MapGet("/feed.xml", async (string? lang, HttpContext ctx, TruthService svc, GuideService guides, IConfiguration config, CancellationToken ct) =>
            {
                var l = lang == "en" ? "en" : "bn";
                var issues = new List<System.Text.Json.Nodes.JsonObject>();
                foreach (var i in System.Text.Json.Nodes.JsonNode.Parse(await svc.ArchiveJsonAsync(ct))!["issues"]!.AsArray())
                    if (await svc.IssueAsync(i!["slug"]!.GetValue<string>(), ct) is { } s) issues.Add(System.Text.Json.Nodes.JsonNode.Parse(s.Json)!.AsObject());
                var guideDocs = new List<System.Text.Json.Nodes.JsonObject>();
                foreach (var x in System.Text.Json.Nodes.JsonNode.Parse(await guides.IndexJsonAsync(ct))!["guides"]!.AsArray())
                    if (await guides.PublishedSnapshotAsync(x!["slug"]!.GetValue<string>(), ct) is { } s) guideDocs.Add(System.Text.Json.Nodes.JsonNode.Parse(s.Json)!.AsObject());
                Common(ctx);
                ctx.Response.Headers.CacheControl = "public, max-age=300";
                return Results.Text(TruthShare.Feed(issues, guideDocs, await SiteTextAsync(svc, ct), l, BaseUrl(config)), "application/rss+xml; charset=utf-8");
            });

            g.MapGet("/status", async (HttpContext ctx, RecordsService rec, CancellationToken ct) =>
            {
                var json = (await rec.PublicStatusAsync(DateTime.UtcNow, ct)).ToJsonString();
                return Serve(ctx, json, RecordsService.ETagOf(json), 60);
            });
            g.MapGet("/wire", async (string? cursor, string? tech, string? type, string? severity, string? tier, bool? open,
                HttpContext ctx, RecordsService rec, CancellationToken ct) =>
            {
                var json = (await rec.PublicWireAsync(cursor, new RecordsService.WireFilter(tech, type, severity, tier, open ?? false), DateTime.UtcNow, ct)).ToJsonString();
                return Serve(ctx, json, RecordsService.ETagOf(json), 60);
            });
            g.MapGet("/wire/{clusterId:int}", async (int clusterId, HttpContext ctx, RecordsService rec, CancellationToken ct) =>
            {
                if (await rec.PublicClusterAsync(clusterId, ct) is not { } cluster) return NotFound(ctx);
                var json = cluster.ToJsonString();
                return Serve(ctx, json, RecordsService.ETagOf(json), 60);
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

        /// <summary>The published site text over the built-in default — the share pages and the feed say what the page says.</summary>
        private static async Task<System.Text.Json.Nodes.JsonObject> SiteTextAsync(TruthService svc, CancellationToken ct) =>
            TruthText.Merge(await svc.PublishedContentAsync(TruthContent.Site, ct) is { } row ? System.Text.Json.Nodes.JsonNode.Parse(row.Json) as System.Text.Json.Nodes.JsonObject : null);

        private static string BaseUrl(IConfiguration config) => (config["Truth:PublicBaseUrl"] ?? "https://mostisko.vercel.app").TrimEnd('/');

        private static IResult Html(HttpContext ctx, string html)
        {
            TruthHeaders.Apply(ctx.Response);
            ctx.Response.Headers.CacheControl = "public, max-age=300";
            return Results.Content(html, "text/html; charset=utf-8");
        }

        private static IResult SharePageMissing(HttpContext ctx)
        {
            TruthHeaders.Apply(ctx.Response);
            ctx.Response.Headers.CacheControl = "public, max-age=60";
            return Results.Content("<!DOCTYPE html><html lang=\"bn\"><head><meta charset=\"utf-8\"><title>404</title><link rel=\"stylesheet\" href=\"/truth-share.css\"></head><body><main><p><a href=\"/truth\">/truth</a></p></main></body></html>",
                "text/html; charset=utf-8", statusCode: StatusCodes.Status404NotFound);
        }

        private static IResult NotFound(HttpContext ctx)
        {
            Common(ctx);
            ctx.Response.Headers.CacheControl = "public, max-age=60";
            return Results.Json(new { error = "not found" }, statusCode: StatusCodes.Status404NotFound);
        }
    }
}
