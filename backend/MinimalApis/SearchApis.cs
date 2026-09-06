using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using RabbitIC.Api.AuthHelper;
using RabbitIC.Api.Data;
using RabbitIC.Api.DTOs;

namespace RabbitIC.Api.MinimalApis
{
    /// <summary>
    /// Backend proxy for Ollama's hosted web search API.
    ///
    /// The browser never sees the upstream key — it lives only in configuration
    /// ("OllamaCloud:ApiKey") and is attached to the named "ollama-cloud" client
    /// in Program.cs. Every call here is authenticated as a RabbitiC user and
    /// rate limited, because the upstream is metered and paid.
    /// </summary>
    public static class SearchApis
    {
        /// <summary>Upstream page dumps can be enormous; cap what we hand back to the UI.</summary>
        private const int MaxFetchContentChars = 8000;

        public static IEndpointRouteBuilder MapSearchApis(this IEndpointRouteBuilder app)
        {
            // POST /api/search — proxy a query to the upstream web_search endpoint.
            app.MapPost("/search", async (
                SearchRequest req,
                HttpContext ctx,
                AppDbContext db,
                IHttpClientFactory http,
                IConfiguration config,
                ILoggerFactory logs) =>
            {
                var log = logs.CreateLogger("SearchApis");

                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                if (!IsConfigured(config)) return NotConfigured();

                if (string.IsNullOrWhiteSpace(req.Query))
                    return Results.BadRequest(new { error = "query is required." });

                if (!SearchRateLimiter.TryTake(user.Id))
                    return Results.Json(
                        new { error = "Slow down — search is rate-limited." }, statusCode: 429);

                // Clamp to the upstream's documented ceiling.
                var maxResults = Math.Clamp(req.MaxResults ?? 5, 1, 10);

                // Usage auditing: the query only, never the results.
                log.LogInformation(
                    "Web search by user {UserId}: {Query} (maxResults {MaxResults})",
                    user.Id, req.Query, maxResults);

                var client = http.CreateClient("ollama-cloud");
                var payload = new { query = req.Query, max_results = maxResults };

                return await ProxyAsync(client, "/api/web_search", payload, ctx.RequestAborted,
                    transform: json => Results.Content(json, "application/json"));
            });

            // POST /api/search/fetch — proxy a single page fetch, truncated for the UI.
            app.MapPost("/search/fetch", async (
                FetchPageRequest req,
                HttpContext ctx,
                AppDbContext db,
                IHttpClientFactory http,
                IConfiguration config,
                ILoggerFactory logs) =>
            {
                var log = logs.CreateLogger("SearchApis");

                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                if (!IsConfigured(config)) return NotConfigured();

                if (string.IsNullOrWhiteSpace(req.Url) ||
                    !Uri.TryCreate(req.Url, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    return Results.BadRequest(new { error = "An absolute http(s) url is required." });
                }

                if (!SearchRateLimiter.TryTake(user.Id))
                    return Results.Json(
                        new { error = "Slow down — search is rate-limited." }, statusCode: 429);

                log.LogInformation("Web fetch by user {UserId}: {Url}", user.Id, uri);

                var client = http.CreateClient("ollama-cloud");
                var payload = new { url = uri.ToString() };

                return await ProxyAsync(client, "/api/web_fetch", payload, ctx.RequestAborted,
                    transform: json => Results.Json(Truncate(json)));
            });

            return app;
        }

        /// <summary>True when an upstream key is present. Absence is a runtime 503, never a startup crash.</summary>
        public static bool IsConfigured(IConfiguration config) =>
            !string.IsNullOrWhiteSpace(config["OllamaCloud:ApiKey"]);

        private static IResult NotConfigured() =>
            Results.Json(new { error = "Web search is not configured." }, statusCode: 503);

        /// <summary>
        /// POST to the upstream and map failures to our own status codes. Upstream
        /// auth failures collapse to a generic 502 so key problems never leak outward.
        /// </summary>
        private static async Task<IResult> ProxyAsync(
            HttpClient client,
            string path,
            object payload,
            CancellationToken ct,
            Func<string, IResult> transform)
        {
            try
            {
                using var resp = await client.PostAsJsonAsync(path, payload, ct);

                if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized
                                    or System.Net.HttpStatusCode.Forbidden)
                    return Results.Json(
                        new { error = "Search provider rejected the key" }, statusCode: 502);

                if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    return Results.Json(
                        new { error = "Slow down — search is rate-limited." }, statusCode: 429);

                if (!resp.IsSuccessStatusCode)
                    return Results.Json(
                        new { error = "Search provider error." }, statusCode: 502);

                var json = await resp.Content.ReadAsStringAsync(ct);
                return transform(json);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                // HttpClient surfaces its own timeout as a cancellation.
                return Results.Json(new { error = "Search provider timed out." }, statusCode: 504);
            }
            catch (HttpRequestException)
            {
                return Results.Json(new { error = "Cannot reach the search provider." }, statusCode: 502);
            }
        }

        /// <summary>
        /// Re-shape a web_fetch response with its content capped. Returns a plain
        /// object so the JSON is re-serialized with the shorter body.
        /// </summary>
        private static object Truncate(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var content = root.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                var truncated = content.Length > MaxFetchContentChars;

                return new
                {
                    title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                    content = truncated ? content[..MaxFetchContentChars] + "\n\n…(truncated)" : content,
                    truncated,
                };
            }
            catch (JsonException)
            {
                return new { title = "", content = "", truncated = false };
            }
        }
    }

    /// <summary>
    /// Fixed-window, in-memory, per-user limiter. Deliberately simple: this guards
    /// a paid upstream on a single-instance internal tool, not a distributed cluster.
    /// </summary>
    internal static class SearchRateLimiter
    {
        private const int MaxCallsPerWindow = 10;
        private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

        private static readonly ConcurrentDictionary<int, Counter> Buckets = new();

        private sealed class Counter
        {
            public DateTime WindowStart;
            public int Count;
        }

        /// <summary>Consumes one call for the user; false once the window's budget is spent.</summary>
        public static bool TryTake(int userId)
        {
            var now = DateTime.UtcNow;
            var bucket = Buckets.GetOrAdd(userId, _ => new Counter { WindowStart = now });

            lock (bucket)
            {
                if (now - bucket.WindowStart >= Window)
                {
                    bucket.WindowStart = now;
                    bucket.Count = 0;
                }

                if (bucket.Count >= MaxCallsPerWindow) return false;

                bucket.Count++;
                return true;
            }
        }
    }
}
