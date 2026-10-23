// সত্য মন্ত্রণালয় — what the internet may reach (docs/TRUTH_PROMPT.md §4, gate G1.6).
//
// The backend stays on the LAN. A tunnel (Tailscale Funnel or Cloudflare Tunnel,
// docs/truth-deploy.md) forwards public requests to a port of their own,
// Truth:Tunnel:Port, which Kestrel binds on 127.0.0.1 only — so only the tunnel
// daemon on this machine can connect to it. A request that arrives on that port,
// or carries Truth:Tunnel:Header (for a tunnel that shares the LAN port), is a
// tunnel request, and every path except /public/truth/* is refused before
// anything else runs: no admin API, no SPA, no Swagger, no static file.
//
// The tunnel is also the only caller whose forwarded-for header is believed:
// a LAN request's client IP is its own connection's.

using System.Net;
using Microsoft.Extensions.Primitives;

namespace RabbitIC.Api.Truth;

public sealed class TunnelOptions
{
    public const string Section = "Truth:Tunnel";

    /// <summary>The port the tunnel forwards to. Bound on 127.0.0.1 when set.</summary>
    public int? Port { get; set; }

    /// <summary>A header only the tunnel adds (e.g. Tailscale-Funnel-Request, Cf-Connecting-Ip). Optional.</summary>
    public string? Header { get; set; }

    /// <summary>Where the tunnel puts the visitor's IP: X-Forwarded-For (Tailscale Funnel) or Cf-Connecting-Ip (Cloudflare).</summary>
    public string ClientIpHeader { get; set; } = "X-Forwarded-For";
}

public static class TunnelGuard
{
    public const string PublicPrefix = "/public/truth";
    private const string TunnelKey = "truth:tunnel";
    private static readonly string[] Methods = { "GET", "HEAD", "POST" };

    public static bool IsTunnel(HttpContext ctx) => ctx.Items.ContainsKey(TunnelKey);

    /// <summary>/public/truth or below it — segment-aware, so /public/truthx is not.</summary>
    public static bool IsPublicTruthPath(string? path) =>
        path is not null && (path.Equals(PublicPrefix, StringComparison.OrdinalIgnoreCase)
                             || path.StartsWith(PublicPrefix + "/", StringComparison.OrdinalIgnoreCase));

    /// <summary>The visitor's IP: the tunnel's forwarded header for a tunnel request, the connection's otherwise.</summary>
    public static IPAddress? ClientIp(HttpContext ctx, TunnelOptions options)
    {
        if (IsTunnel(ctx) && ctx.Request.Headers.TryGetValue(options.ClientIpHeader, out StringValues values))
        {
            var first = values.ToString().Split(',')[0].Trim();
            if (IPAddress.TryParse(first, out var ip)) return ip;
        }
        return ctx.Connection.RemoteIpAddress;
    }

    /// <summary>Mounted first, ahead of Swagger, static files and the SPA fallback.</summary>
    public static WebApplication UseTruthTunnelGuard(this WebApplication app)
    {
        var options = app.Configuration.GetSection(TunnelOptions.Section).Get<TunnelOptions>() ?? new TunnelOptions();
        app.Use(async (ctx, next) =>
        {
            var tunnel = (options.Port is > 0 && ctx.Connection.LocalPort == options.Port)
                         || (!string.IsNullOrWhiteSpace(options.Header) && ctx.Request.Headers.ContainsKey(options.Header));
            if (tunnel)
            {
                ctx.Items[TunnelKey] = true;
                if (!IsPublicTruthPath(ctx.Request.Path.Value) || !Methods.Contains(ctx.Request.Method, StringComparer.OrdinalIgnoreCase))
                {
                    // Nothing about what is there: the same empty 404 for every refused path.
                    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                    ctx.Response.Headers.CacheControl = "no-store";
                    return;
                }
            }
            await next(ctx);
        });
        return app;
    }

    /// <summary>
    /// The extra Kestrel address for the tunnel, keeping every address the app
    /// would have had (--urls, ASPNETCORE_URLS, ASPNETCORE_HTTP_PORTS, or the default).
    /// </summary>
    public static void AddTunnelPort(WebApplicationBuilder builder)
    {
        var port = builder.Configuration.GetValue<int?>($"{TunnelOptions.Section}:Port");
        if (port is not > 0) return;
        var urls = builder.Configuration["urls"];
        if (string.IsNullOrWhiteSpace(urls) && builder.Configuration["http_ports"] is { Length: > 0 } ports)
            urls = string.Join(';', ports.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => $"http://*:{p.Trim()}"));
        if (string.IsNullOrWhiteSpace(urls)) urls = "http://localhost:5000";
        builder.WebHost.UseUrls($"{urls};http://127.0.0.1:{port}");
    }
}
