// Oddity — the gateway's routes: Ollama's own paths under /oddity/api.
//
// Authenticated by a gateway key (odd_…), never a session token, so these sit
// outside TabAccessMiddleware's /api prefix list; the proxy checks the key's
// owner still holds the "oddity" tab itself. Hidden from Swagger (R11).

using RabbitIC.Api.Oddity;

namespace RabbitIC.Api.MinimalApis
{
    public static class OddityGatewayApis
    {
        public static IEndpointRouteBuilder MapOddityGatewayApis(this IEndpointRouteBuilder app)
        {
            var gw = app.MapGroup("/oddity/api").ExcludeFromDescription();

            foreach (var endpoint in new[] { "chat", "generate", "embed", "embeddings" })
                gw.MapPost("/" + endpoint, (HttpContext ctx, OddityProxy proxy) => proxy.ModelCallAsync(ctx, endpoint));

            gw.MapGet("/tags", (HttpContext ctx, OddityProxy proxy) => proxy.TagsAsync(ctx));
            gw.MapPost("/show", (HttpContext ctx, OddityProxy proxy) => proxy.PassThroughAsync(ctx, HttpMethod.Post, "/api/show"));
            gw.MapGet("/version", (HttpContext ctx, OddityProxy proxy) => proxy.PassThroughAsync(ctx, HttpMethod.Get, "/api/version"));
            gw.MapGet("/ps", (HttpContext ctx, OddityProxy proxy) => proxy.PassThroughAsync(ctx, HttpMethod.Get, "/api/ps"));

            // Model management stays with whoever runs Ollama. Refused for every
            // method and never forwarded, key or no key.
            foreach (var pattern in new[] { "/pull", "/push", "/create", "/copy", "/delete", "/blobs/{**rest}" })
                gw.Map(pattern, (HttpContext ctx) =>
                    OddityProxy.ErrorAsync(ctx, StatusCodes.Status403Forbidden, "model management is closed on this gateway"));

            gw.Map("/{**rest}", (HttpContext ctx) =>
                OddityProxy.ErrorAsync(ctx, StatusCodes.Status404NotFound, "no such endpoint on this gateway"));

            return app;
        }
    }
}
