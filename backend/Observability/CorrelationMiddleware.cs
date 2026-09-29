// Observability — correlation middleware.
//
// Reads X-Correlation-Id from the incoming request (or generates a new UUID),
// stores it in HttpContext.Items["CorrelationId"], and echoes it back in the
// response header. All log entries read from HttpContext.Items so they all
// carry the same id for a given request.

namespace RabbitIC.Api.Observability;

public sealed class CorrelationMiddleware
{
    public const string ItemKey    = "CorrelationId";
    public const string HeaderName = "X-Correlation-Id";

    private readonly RequestDelegate _next;

    public CorrelationMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx)
    {
        var correlationId = ctx.Request.Headers[HeaderName].FirstOrDefault()
            ?? Guid.NewGuid().ToString("N");

        ctx.Items[ItemKey] = correlationId;
        ctx.Response.Headers[HeaderName] = correlationId;

        await _next(ctx);
    }
}

public static class CorrelationMiddlewareExtensions
{
    public static WebApplication UseCorrelation(this WebApplication app)
    {
        app.UseMiddleware<CorrelationMiddleware>();
        return app;
    }
}
