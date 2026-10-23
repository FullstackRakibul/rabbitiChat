namespace RabbitIC.Api.Truth;

/// <summary>
/// The security headers of the public pages (docs/TRUTH_PROMPT.md §4) — the same
/// ones frontend/vercel.json sets for /truth*, for when the backend serves a page
/// itself (truth.html on the LAN; the share pages in P2). A change to one is a
/// change to both.
/// </summary>
public static class TruthHeaders
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; connect-src 'self'; "
        + "object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'self'; upgrade-insecure-requests";

    public static void Apply(HttpResponse response)
    {
        response.Headers.ContentSecurityPolicy = ContentSecurityPolicy;
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), interest-cohort=()";
        if (response.HttpContext.Request.IsHttps)
            response.Headers.StrictTransportSecurity = "max-age=63072000; includeSubDomains";
    }
}
