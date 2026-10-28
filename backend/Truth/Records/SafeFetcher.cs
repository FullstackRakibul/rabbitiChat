// The one way the Records Department reaches the internet (docs/TRUTH_PROMPT_P5.md §3.1, §3.4).
//
//   · https only — the first URL and every redirect;
//   · every address a host resolves to is checked when the socket connects, so a name that
//     resolves (or re-resolves) to loopback, the LAN, link-local or CGNAT space is refused —
//     no DNS-rebinding gap between a check and the connection;
//   · redirects (at most 5, followed by hand) stay on the host that was asked for: a different
//     host is not in the registry;
//   · a 15 s timeout, a size cap read from the stream (not trusted from Content-Length),
//     ETag / If-Modified-Since so an unchanged feed costs a 304;
//   · our own User-Agent, and robots.txt honoured for pages that are not advertised feeds.
//
// Evals only (ASPNETCORE_ENVIRONMENT=Eval): Truth:Records:TestHosts maps invented host names to the
// fake feed server on loopback, and Truth:Records:TestCertFile is the one extra certificate trusted.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace RabbitIC.Api.Truth.Records;

public sealed record FetchResult(int Status, byte[]? Body, string? ContentType, string? ETag, string? LastModified, string? Error, int Ms)
{
    public bool NotModified => Status == 304 && Error is null;
    public bool Ok => Status == 200 && Error is null && Body is not null;
    public string Text => Body is null ? "" : Encoding.UTF8.GetString(Body);
}

public sealed class SafeFetcher : IDisposable
{
    public const string UserAgent = "MinistryOfTruthBot/1.0 (+https://mostisko.vercel.app/truth/about)";
    public const string RobotsToken = "MinistryOfTruthBot";
    private const int MaxRedirects = 5;

    private readonly HttpClient http;
    private readonly Dictionary<string, IPEndPoint> testHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? testThumbprint;
    private readonly TimeSpan timeout;

    public SafeFetcher(IConfiguration config, IWebHostEnvironment env)
    {
        timeout = TimeSpan.FromSeconds(Math.Clamp(config.GetValue<int?>("Truth:Records:TimeoutSeconds") ?? 15, 1, 60));
        if (env.EnvironmentName == "Eval")
        {
            foreach (var pair in (config["Truth:Records:TestHosts"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (pair.Split('=') is [var host, var ep] && IPEndPoint.TryParse(ep, out var endPoint)) testHosts[host] = endPoint;
            if (config["Truth:Records:TestCertFile"] is { Length: > 0 } certFile && File.Exists(certFile))
                testThumbprint = X509Certificate2.CreateFromPem(File.ReadAllText(certFile)).Thumbprint;
        }
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = timeout,
            ConnectCallback = ConnectAsync,
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = ValidateCertificate },
        };
        http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public void Dispose() => http.Dispose();

    /// <summary>
    /// GET one URL under every rule above. Never throws for the network's sake: a refusal, a timeout or
    /// an oversize body comes back as <see cref="FetchResult.Error"/>.
    /// </summary>
    public async Task<FetchResult> GetAsync(Uri url, int maxBytes, string? etag = null, string? lastModified = null, CancellationToken ct = default)
    {
        var started = Environment.TickCount64;
        int Ms() => (int)(Environment.TickCount64 - started);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var current = url;
        try
        {
            for (var hop = 0; ; hop++)
            {
                if (Refuse(current, url) is { } refused) return new FetchResult(0, null, null, null, null, refused, Ms());
                using var req = new HttpRequestMessage(HttpMethod.Get, current);
                req.Headers.UserAgent.ParseAdd(UserAgent);
                req.Headers.Accept.ParseAdd("*/*");
                if (etag is not null) req.Headers.TryAddWithoutValidation("If-None-Match", etag);
                if (lastModified is not null) req.Headers.TryAddWithoutValidation("If-Modified-Since", lastModified);
                using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                var code = (int)res.StatusCode;
                if (code is 301 or 302 or 303 or 307 or 308)
                {
                    if (hop >= MaxRedirects) return new FetchResult(code, null, null, null, null, "Too many redirects.", Ms());
                    if (res.Headers.Location is not { } next) return new FetchResult(code, null, null, null, null, "A redirect with no Location.", Ms());
                    current = next.IsAbsoluteUri ? next : new Uri(current, next);
                    continue;
                }
                var newEtag = res.Headers.ETag?.ToString();
                var newModified = res.Content.Headers.LastModified?.ToString("R");
                if (code == 304) return new FetchResult(304, null, null, etag ?? newEtag, lastModified ?? newModified, null, Ms());
                if (code != 200) return new FetchResult(code, null, null, null, null, $"HTTP {code}.", Ms());
                if (res.Content.Headers.ContentLength > maxBytes)
                    return new FetchResult(code, null, null, null, null, $"Larger than the {maxBytes / 1024} KB cap.", Ms());
                await using var stream = await res.Content.ReadAsStreamAsync(cts.Token);
                using var buffer = new MemoryStream();
                var chunk = new byte[16 * 1024];
                int read;
                while ((read = await stream.ReadAsync(chunk, cts.Token)) > 0)
                {
                    buffer.Write(chunk, 0, read);
                    if (buffer.Length > maxBytes) return new FetchResult(code, null, null, null, null, $"Larger than the {maxBytes / 1024} KB cap.", Ms());
                }
                return new FetchResult(200, buffer.ToArray(), res.Content.Headers.ContentType?.MediaType, newEtag, newModified, null, Ms());
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new FetchResult(0, null, null, null, null, $"Timed out after {timeout.TotalSeconds:0} s.", Ms());
        }
        catch (HttpRequestException e) when (e.InnerException is BlockedAddressException blocked)
        {
            return new FetchResult(0, null, null, null, null, blocked.Message, Ms());
        }
        catch (HttpRequestException e)
        {
            return new FetchResult(0, null, null, null, null, $"Could not connect: {e.HttpRequestError}.", Ms());
        }
    }

    /// <summary>The checks made before every hop: https, no private literal address, and the asked-for host.</summary>
    private static string? Refuse(Uri current, Uri first)
    {
        if (current.Scheme != Uri.UriSchemeHttps) return $"Only https is fetched ({current.Scheme}:// refused).";
        if (IPAddress.TryParse(current.IdnHost.Trim('[', ']'), out var literal) && IsPrivate(literal))
            return "Refused: a private or local address.";
        if (!string.Equals(current.IdnHost, first.IdnHost, StringComparison.OrdinalIgnoreCase))
            return $"Refused: a redirect to {current.IdnHost}, a host that is not in the registry.";
        return null;
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        var host = ctx.DnsEndPoint.Host;
        var port = ctx.DnsEndPoint.Port;
        IPAddress[] addresses;
        if (testHosts.TryGetValue(host, out var mapped)) { addresses = new[] { mapped.Address }; port = mapped.Port; }
        else
        {
            addresses = IPAddress.TryParse(host, out var literal) ? new[] { literal } : await Dns.GetHostAddressesAsync(host, ct);
            if (addresses.Length == 0) throw new BlockedAddressException($"{host} did not resolve.");
            if (addresses.Any(IsPrivate)) throw new BlockedAddressException("Refused: a private or local address.");
        }
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }

    private bool ValidateCertificate(object sender, X509Certificate? cert, X509Chain? chain, SslPolicyErrors errors) =>
        errors == SslPolicyErrors.None
        || (testThumbprint is not null && cert is not null && string.Equals(new X509Certificate2(cert).Thumbprint, testThumbprint, StringComparison.OrdinalIgnoreCase));

    /// <summary>Loopback, private (RFC 1918, ULA), link-local, CGNAT, unspecified, multicast and documentation-free reserved ranges.</summary>
    public static bool IsPrivate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6None)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10 || b[0] == 0 || b[0] >= 224
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] is >= 64 and <= 127)
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                || (b[0] == 198 && b[1] is 18 or 19);
        }
        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return true;
        var first = ip.GetAddressBytes()[0];
        return (first & 0xFE) == 0xFC;   // fc00::/7, unique local
    }

    /// <summary>
    /// robots.txt (RFC 9309) for one URL: our token's group, else "*"; the longest matching rule wins,
    /// Allow on a tie. No file (404) allows; an unreachable or failing one disallows.
    /// </summary>
    public async Task<(bool Allowed, string Why)> RobotsAsync(Uri url, CancellationToken ct)
    {
        var robots = new Uri($"https://{url.Authority}/robots.txt");
        var r = await GetAsync(robots, 512 * 1024, ct: ct);
        if (r.Status is 404 or 410) return (true, "no robots.txt");
        if (!r.Ok) return (false, $"robots.txt could not be read ({r.Error})");
        var allowed = RobotsTxt.Allows(r.Text, RobotsToken, url.PathAndQuery);
        return (allowed, allowed ? "allowed by robots.txt" : "disallowed by robots.txt");
    }
}

public sealed class BlockedAddressException(string message) : Exception(message);

public static class RobotsTxt
{
    public static bool Allows(string text, string token, string path)
    {
        var groups = new List<(List<string> Agents, List<(bool Allow, string Rule)> Rules)>();
        (List<string> Agents, List<(bool, string)> Rules)? current = null;
        var lastWasAgent = false;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Split('#')[0].Trim();
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            if (key == "user-agent")
            {
                if (current is null || !lastWasAgent) { current = (new List<string>(), new List<(bool, string)>()); groups.Add(current.Value); }
                current.Value.Agents.Add(value.ToLowerInvariant());
                lastWasAgent = true;
            }
            else if (key is "allow" or "disallow" && current is not null)
            {
                lastWasAgent = false;
                if (value.Length > 0) current.Value.Rules.Add((key == "allow", value));
            }
            else lastWasAgent = false;
        }
        var mine = groups.Where(g => g.Agents.Contains(token.ToLowerInvariant())).ToList();
        if (mine.Count == 0) mine = groups.Where(g => g.Agents.Contains("*")).ToList();
        var best = (Allow: true, Length: -1);
        foreach (var (allow, rule) in mine.SelectMany(g => g.Rules))
            if (Matches(rule, path) && (rule.Length > best.Length || (rule.Length == best.Length && allow)))
                best = (allow, rule.Length);
        return best.Allow;
    }

    private static bool Matches(string rule, string path)
    {
        var anchored = rule.EndsWith('$');
        var pattern = "^" + string.Concat((anchored ? rule[..^1] : rule).Select(c => c == '*' ? ".*" : System.Text.RegularExpressions.Regex.Escape(c.ToString())));
        if (anchored) pattern += "$";
        return System.Text.RegularExpressions.Regex.IsMatch(path, pattern);
    }
}
