// Feed adapters (docs/TRUTH_PROMPT_P5.md §2, §3.2): one fetched body in, normalised entries out.
// They keep only what §3.1 allows — title, link, times, state, components and the feed's own short
// description as plain text (at most 300 characters) — never an article body. A body that does not
// parse throws FeedFormatException, which the collector records as the source's error.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using AngleSharp.Html.Parser;
using RabbitIC.Api.Models;

namespace RabbitIC.Api.Truth.Records;

public sealed record WireUpdate(DateTime AtUtc, string? State, string Text);

public sealed record ParsedEntry(
    string ExternalId, string Kind, string Title, string Excerpt, string Url, DateTime PublishedAtUtc, DateTime UpdatedAtUtc,
    string? State = null, IReadOnlyList<string>? Components = null, string? Severity = null,
    IReadOnlyList<string>? CveIds = null, IReadOnlyList<string>? Versions = null, IReadOnlyList<string>? Fixed = null,
    IReadOnlyList<WireUpdate>? Updates = null, IReadOnlyList<string>? Techs = null, string? Line = null);

public sealed class FeedFormatException(string message) : Exception(message);

public static partial class Adapters
{
    public const int ExcerptMax = 300;

    public static List<ParsedEntry> Parse(WireSource source, string body, DateTime now) => source.Adapter switch
    {
        "incidentio-rss" => IncidentIo(body),
        "statuspage-feed" => Statuspage(body),
        "rss-atom" => RssAtom(body, source.Kind),
        "cisa-kev-json" => Kev(body, source.Options, now),
        "github-advisories" => GithubAdvisories(body),
        "endoflife-json" => EndOfLife(body, source.Options),
        "osv-api" => throw new FeedFormatException("The osv-api adapter needs a package list; none is configured (see the report)."),
        _ => throw new FeedFormatException($"{source.Adapter} is not a scheduled adapter."),
    };

    // ─────────────────────────── incident.io RSS (status.openai.com) ───────────────────────────

    private static readonly Dictionary<string, string> IncidentIoStates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Investigating"] = WireState.Investigating, ["Identified"] = WireState.Identified, ["Monitoring"] = WireState.Monitoring,
        ["Resolved"] = WireState.Resolved, ["Scheduled"] = WireState.Scheduled, ["In progress"] = WireState.Maintenance,
        ["Completed"] = WireState.Resolved, ["Degraded performance"] = WireState.Investigating, ["Full outage"] = WireState.Investigating,
        ["Partial outage"] = WireState.Investigating,
    };

    public static List<ParsedEntry> IncidentIo(string body)
    {
        var list = new List<ParsedEntry>();
        foreach (var item in Items(LoadXml(body)))
        {
            var lines = HtmlLines(item.Html);
            string? state = null;
            var components = new List<string>();
            var text = new List<string>();
            var inComponents = false;
            foreach (var line in lines)
            {
                if (StatusLine().Match(line) is { Success: true } m) { state = IncidentIoStates.GetValueOrDefault(m.Groups[1].Value.Trim()); continue; }
                if (line.StartsWith("Affected components", StringComparison.OrdinalIgnoreCase)) { inComponents = true; continue; }
                if (inComponents) { components.Add(ComponentName().Replace(line, "").Trim()); continue; }
                text.Add(line);
            }
            list.Add(new ParsedEntry(item.Id, WireKind.Status, item.Title, Excerpt(string.Join(" ", text)), NormaliseUrl(item.Link),
                item.Published, item.Updated, state ?? WireState.Investigating, components.Where(c => c.Length > 0).Distinct().ToList()));
        }
        return list;
    }

    [GeneratedRegex(@"^Status:\s*(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex StatusLine();

    [GeneratedRegex(@"\s*\([^)]*\)\s*$")]
    private static partial Regex ComponentName();

    // ─────────────────────────── Atlassian Statuspage RSS / Atom ───────────────────────────

    private static readonly Dictionary<string, string> StatuspageStates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Investigating"] = WireState.Investigating, ["Identified"] = WireState.Identified, ["Monitoring"] = WireState.Monitoring,
        ["Update"] = WireState.Investigating, ["Resolved"] = WireState.Resolved, ["Postmortem"] = WireState.Resolved,
        ["Scheduled"] = WireState.Scheduled, ["In progress"] = WireState.Maintenance, ["Verifying"] = WireState.Monitoring,
        ["Completed"] = WireState.Resolved,
    };

    /// <summary>
    /// Each item's description is the incident's updates, newest first: "&lt;small&gt;Oct 23, 17:32 UTC&lt;/small&gt;
    /// &lt;strong&gt;Resolved&lt;/strong&gt; - text". The newest gives the state; all of them the history.
    /// </summary>
    public static List<ParsedEntry> Statuspage(string body)
    {
        var list = new List<ParsedEntry>();
        var parser = new HtmlParser();
        foreach (var item in Items(LoadXml(body)))
        {
            var doc = parser.ParseDocument("<body>" + item.Html + "</body>");
            var updates = new List<WireUpdate>();
            string? lastStateWord = null;
            foreach (var p in doc.Body!.QuerySelectorAll("p"))
            {
                var stamp = p.QuerySelector("small")?.TextContent;
                var word = p.QuerySelector("strong")?.TextContent.Trim();
                var all = Clean(p.TextContent);
                var dash = all.IndexOf(" - ", StringComparison.Ordinal);
                var text = dash >= 0 ? all[(dash + 3)..] : all;
                updates.Add(new WireUpdate(UpdateTime(stamp, item.Published), word is null ? null : StatuspageStates.GetValueOrDefault(word), text));
                lastStateWord ??= word;
            }
            var latest = updates.FirstOrDefault();
            list.Add(new ParsedEntry(item.Id, WireKind.Status, item.Title, Excerpt(latest?.Text ?? Clean(doc.Body.TextContent)), NormaliseUrl(item.Link),
                updates.LastOrDefault()?.AtUtc is { } first && first < item.Published ? first : item.Published,
                latest?.AtUtc is { } upd && upd > item.Updated ? upd : item.Updated,
                latest?.State ?? WireState.Investigating, Updates: updates));
        }
        return list;
    }

    /// <summary>"Oct 23, 17:32 UTC" has no year: the item's year, or the one before when that would be in its future.</summary>
    private static DateTime UpdateTime(string? stamp, DateTime itemTime)
    {
        if (stamp is null) return itemTime;
        var s = Regex.Replace(Clean(stamp), @"\s+", " ").Replace(" UTC", "");
        foreach (var year in new[] { itemTime.Year, itemTime.Year - 1 })
            if (DateTime.TryParseExact($"{s} {year}", new[] { "MMM d, HH:mm yyyy", "MMM dd, HH:mm yyyy" }, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) && t <= itemTime.AddDays(1))
                return t;
        return itemTime;
    }

    // ─────────────────────────── any RSS 2.0 or Atom feed ───────────────────────────

    public static List<ParsedEntry> RssAtom(string body, string kind) =>
        Items(LoadXml(body)).Select(i =>
        {
            var text = Clean(StripHtml(i.Html));
            return new ParsedEntry(i.Id, kind, i.Title, Excerpt(text), NormaliseUrl(i.Link), i.Published, i.Updated);
        }).ToList();

    // ─────────────────────────── CISA Known Exploited Vulnerabilities (JSON) ───────────────────────────

    /// <summary>Only entries added in the last <c>options.days</c> (30) — the catalogue goes back to 2021.</summary>
    public static List<ParsedEntry> Kev(string body, JsonObject options, DateTime now)
    {
        var days = options["days"]?.GetValue<int>() ?? 30;
        var root = LoadJson(body);
        if (root["vulnerabilities"] is not JsonArray vulns) throw new FeedFormatException("No \"vulnerabilities\" array.");
        var list = new List<ParsedEntry>();
        foreach (var v in vulns.OfType<JsonObject>())
        {
            var cve = Str(v, "cveID");
            if (cve is null || !DateTime.TryParse(Str(v, "dateAdded"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var added)) continue;
            if (added < now.AddDays(-days)) continue;
            var title = $"{cve}: {Str(v, "vulnerabilityName") ?? $"{Str(v, "vendorProject")} {Str(v, "product")}"}";
            list.Add(new ParsedEntry(cve, WireKind.Advisory, Cut(title, 300), Excerpt(Str(v, "shortDescription") ?? ""),
                $"https://www.cve.org/CVERecord?id={Uri.EscapeDataString(cve)}", added, added, Severity: "critical", CveIds: new[] { cve },
                Techs: new[] { Str(v, "product") ?? "", Str(v, "vendorProject") ?? "" }.Where(t => t.Length > 0).ToList()));
        }
        return list;
    }

    // ─────────────────────────── GitHub Advisory Database (REST) ───────────────────────────

    private static readonly Dictionary<string, string> EcosystemTech = new(StringComparer.OrdinalIgnoreCase)
    {
        ["npm"] = "node", ["composer"] = "php", ["nuget"] = "dotnet", ["pip"] = "python", ["go"] = "go", ["maven"] = "java", ["rubygems"] = "ruby",
    };

    public static List<ParsedEntry> GithubAdvisories(string body)
    {
        if (LoadJson(body) is not JsonArray arr) throw new FeedFormatException("Expected a JSON array of advisories.");
        var list = new List<ParsedEntry>();
        foreach (var a in arr.OfType<JsonObject>())
        {
            var id = Str(a, "ghsa_id");
            if (id is null || Str(a, "withdrawn_at") is not null) continue;
            var cve = Str(a, "cve_id");
            var vulns = (a["vulnerabilities"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
            var fixedVersions = vulns.Select(v => Str(v, "first_patched_version")).Where(f => f is not null).Select(f => f!).Distinct().ToList();
            var techs = vulns.Select(v => (v["package"] as JsonObject) is { } p ? EcosystemTech.GetValueOrDefault(Str(p, "ecosystem") ?? "") : null)
                .Where(t => t is not null).Select(t => t!).Distinct().ToList();
            var packages = vulns.Select(v => (v["package"] as JsonObject)?["name"]?.GetValue<string>()).Where(n => n is not null).Select(n => n!).Distinct();
            var published = Date(Str(a, "published_at")) ?? DateTime.UtcNow;
            var severity = Str(a, "severity") is { } s && TrSeverity.All.Contains(s) ? s : null;
            var excerpt = string.Join(" ", packages) is { Length: > 0 } pk ? $"{pk}: {FirstParagraph(Str(a, "description") ?? "")}" : FirstParagraph(Str(a, "description") ?? "");
            list.Add(new ParsedEntry(id, WireKind.Advisory, Cut(Str(a, "summary") ?? id, 300), Excerpt(excerpt), Str(a, "html_url") ?? $"https://github.com/advisories/{id}",
                published, Date(Str(a, "updated_at")) ?? published, Severity: severity, CveIds: cve is null ? null : new[] { cve },
                Versions: fixedVersions, Fixed: fixedVersions, Techs: techs));
        }
        return list;
    }

    // ─────────────────────────── endoflife.date (API v1, one product) ───────────────────────────

    /// <summary>One release entry per maintained line's latest version: a new patch release is a new entry.</summary>
    public static List<ParsedEntry> EndOfLife(string body, JsonObject options)
    {
        var root = LoadJson(body)["result"] as JsonObject ?? throw new FeedFormatException("No \"result\" object.");
        var product = Str(root, "name") ?? throw new FeedFormatException("No product name.");
        var label = Str(root, "label") ?? product;
        var list = new List<ParsedEntry>();
        foreach (var r in (root["releases"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            if (r["isEol"]?.GetValue<bool>() == true || r["latest"] is not JsonObject latest) continue;
            var line = Str(r, "name");
            var version = Str(latest, "name");
            if (line is null || version is null) continue;
            var at = Date(Str(latest, "date")) ?? DateTime.UtcNow.Date;
            var link = Str(latest, "link") is { } l && l.StartsWith("https://", StringComparison.Ordinal) ? l : $"https://endoflife.date/{product}";
            list.Add(new ParsedEntry($"{product}-{version}", WireKind.Release, $"{label} {version}", Excerpt($"{label} {line}: latest release {version}."),
                NormaliseUrl(link), at, at, Versions: new[] { version }, Line: line));
        }
        return list;
    }

    // ─────────────────────────── shared ───────────────────────────

    private sealed record FeedItem(string Id, string Title, string Link, string Html, DateTime Published, DateTime Updated);

    private static XDocument LoadXml(string body)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 20_000_000 };
        try
        {
            using var reader = XmlReader.Create(new StringReader(body), settings);
            return XDocument.Load(reader);
        }
        catch (XmlException e) { throw new FeedFormatException($"Malformed XML: {e.Message}"); }
    }

    private static JsonNode LoadJson(string body)
    {
        try { return JsonNode.Parse(body) ?? throw new FeedFormatException("Empty JSON."); }
        catch (JsonException e) { throw new FeedFormatException($"Malformed JSON: {e.Message}"); }
    }

    /// <summary>RSS 2.0 items or Atom entries, whichever the document is.</summary>
    private static IEnumerable<FeedItem> Items(XDocument doc)
    {
        var root = doc.Root ?? throw new FeedFormatException("Empty document.");
        XNamespace atom = "http://www.w3.org/2005/Atom";
        XNamespace content = "http://purl.org/rss/1.0/modules/content/";
        if (root.Name.LocalName == "rss")
        {
            foreach (var i in root.Element("channel")?.Elements("item") ?? Enumerable.Empty<XElement>())
            {
                var link = i.Element("link")?.Value.Trim() ?? "";
                var title = Clean(i.Element("title")?.Value ?? "");
                var published = Date(i.Element("pubDate")?.Value) ?? Date(i.Element(XName.Get("date", "http://purl.org/dc/elements/1.1/"))?.Value) ?? DateTime.UtcNow;
                var id = i.Element("guid")?.Value.Trim() is { Length: > 0 } g ? g : Hash(link + title);
                var html = i.Element("description")?.Value ?? i.Element(content + "encoded")?.Value ?? "";
                if (title.Length == 0 && link.Length == 0) continue;
                yield return new FeedItem(Cut(id, 400), Cut(title, 300), link, html, published, published);
            }
        }
        else if (root.Name == atom + "feed")
        {
            foreach (var e in root.Elements(atom + "entry"))
            {
                var link = e.Elements(atom + "link").FirstOrDefault(l => (string?)l.Attribute("rel") is null or "alternate")?.Attribute("href")?.Value ?? "";
                var title = Clean(e.Element(atom + "title")?.Value ?? "");
                var updated = Date(e.Element(atom + "updated")?.Value);
                var published = Date(e.Element(atom + "published")?.Value) ?? updated ?? DateTime.UtcNow;
                var id = e.Element(atom + "id")?.Value.Trim() is { Length: > 0 } g ? g : Hash(link + title);
                var html = e.Element(atom + "content")?.Value ?? e.Element(atom + "summary")?.Value ?? "";
                yield return new FeedItem(Cut(id, 400), Cut(title, 300), link, html, published, updated ?? published);
            }
        }
        else throw new FeedFormatException($"Neither RSS nor Atom (root <{root.Name.LocalName}>).");
    }

    private static List<string> HtmlLines(string html) =>
        Clean(StripHtml(LineBreaks().Replace(html, "\n")), keepLines: true).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    [GeneratedRegex(@"<br\s*/?>|</p>|</li>|</div>|<li[^>]*>|</h\d>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreaks();

    /// <summary>Tags out, entities decoded — by a real HTML parser, never by rendering it.</summary>
    public static string StripHtml(string html) =>
        html.Contains('<') || html.Contains('&') ? new HtmlParser().ParseDocument("<body>" + html + "</body>").Body!.TextContent : html;

    public static string Clean(string s, bool keepLines = false) =>
        keepLines ? Regex.Replace(Regex.Replace(s, @"[ \t ]+", " "), @"\n\s*\n+", "\n").Trim() : Regex.Replace(s, @"\s+", " ").Trim();

    /// <summary>Plain text, at most 300 characters, cut at a word.</summary>
    public static string Excerpt(string s)
    {
        s = Clean(StripHtml(s));
        if (s.Length <= ExcerptMax) return s;
        var cut = s[..(ExcerptMax - 1)];
        var space = cut.LastIndexOf(' ');
        return (space > ExcerptMax / 2 ? cut[..space] : cut).TrimEnd(',', ';', ':', ' ') + "…";
    }

    private static string FirstParagraph(string markdown) =>
        markdown.Replace("\r", "").Split("\n\n", StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(p => !p.TrimStart().StartsWith('#')) ?? "";

    private static readonly string[] TrackingParams = { "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content", "fbclid", "gclid", "mc_cid", "mc_eid", "ref" };

    /// <summary>Doubled slashes in the path folded (status.openai.com writes "//incidents/…"), tracking parameters dropped.</summary>
    public static string NormaliseUrl(string link)
    {
        if (!Uri.TryCreate(link.Trim(), UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp)) return "";
        var path = Regex.Replace(u.AbsolutePath, "/{2,}", "/");
        var query = string.Join("&", u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !TrackingParams.Contains(p.Split('=')[0], StringComparer.OrdinalIgnoreCase)));
        var b = new UriBuilder(u) { Path = path, Query = query, Port = u.IsDefaultPort ? -1 : u.Port };
        return b.Uri.ToString();
    }

    private static DateTime? Date(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().Replace(" GMT", " +0000").Replace(" UTC", " +0000");
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.UtcDateTime : null;
    }

    private static string? Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
    private static string Cut(string s, int n) => s.Length <= n ? s : s[..n];
    public static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..32].ToLowerInvariant();
}
