// সত্য মন্ত্রণালয় — share pages and the RSS feed (docs/TRUTH_PROMPT.md §4, §7).
//
// /truth/s/issue/:slug and /truth/s/guide/:slug (Vercel rewrites them to
// /public/truth/share/*): a small, plain HTML page with full Open Graph tags, so a
// link previews in Facebook, WhatsApp and Slack and reads without JavaScript —
// title, summary, references, and a guide's steps for its default platform and
// versions — with a clear link to the interactive page. Every word that isn't
// staff-written comes from the published site text, so these pages, the shell
// and the emails say the same thing. Everything is HTML-encoded; nothing runs.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;

namespace RabbitIC.Api.Truth;

/// <summary>Bangladesh time in the site text's words — the same as frontend/src/truth/lib/time.js.</summary>
public static class TruthTime
{
    private static readonly TimeZoneInfo Dhaka = Find();

    private static TimeZoneInfo Find()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Dhaka"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.CreateCustomTimeZone("Asia/Dhaka", TimeSpan.FromHours(6), "Asia/Dhaka", "Asia/Dhaka"); }
    }

    public static string Digits(string s, string lang) =>
        lang == "bn" ? new string(s.Select(c => c is >= '0' and <= '9' ? (char)('০' + (c - '0')) : c).ToArray()) : s;

    private static string Daypart(int h) => h switch { >= 4 and < 6 => "dawn", >= 6 and < 12 => "morning", >= 12 and < 15 => "noon", >= 15 and < 18 => "afternoon", >= 18 and < 20 => "evening", _ => "night" };

    private static string Date(JsonObject text, string lang, int y, int m, int d) =>
        TruthText.Get(text, lang, "time.date", ("day", Digits(d.ToString(CultureInfo.InvariantCulture), lang)),
            ("month", TruthText.Get(text, lang, $"time.m{m}")), ("year", Digits(y.ToString(CultureInfo.InvariantCulture), lang)));

    public static string Day(JsonObject text, string lang, string ymd)
    {
        var p = ymd.Split('-').Select(int.Parse).ToArray();
        return Date(text, lang, p[0], p[1], p[2]);
    }

    public static string DateTimeText(JsonObject text, string lang, DateTime utc)
    {
        var t = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Dhaka);
        var h12 = t.Hour % 12 == 0 ? 12 : t.Hour % 12;
        var clock = TruthText.Get(text, lang, t.Minute == 0 ? "time.clock" : "time.clockMinutes",
            ("daypart", TruthText.Get(text, lang, $"time.{Daypart(t.Hour)}")), ("hour", Digits(h12.ToString(CultureInfo.InvariantCulture), lang)),
            ("minute", lang == "bn" ? Digits(t.Minute.ToString(CultureInfo.InvariantCulture), lang) : t.Minute.ToString("00", CultureInfo.InvariantCulture)),
            ("ampm", TruthText.Get(text, lang, t.Hour < 12 ? "time.am" : "time.pm")));
        return TruthText.Get(text, lang, "time.dateTime", ("date", Date(text, lang, t.Year, t.Month, t.Day)), ("time", clock));
    }

    /// <summary>"২১ অক্টোবর ২০২৬, রাত ৮টা (14:00 UTC)" — rule 6's both zones.</summary>
    public static string WithUtc(JsonObject text, string lang, DateTime utc) =>
        TruthText.Get(text, lang, "time.withUtc", ("local", DateTimeText(text, lang, utc)), ("utc", utc.ToString("HH:mm", CultureInfo.InvariantCulture)));
}

public static class TruthShare
{
    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static DateTime Utc(string iso) => DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);

    /// <summary>A Bn/En pair in the page's language: the English when there is one; the Bangla, marked, otherwise.</summary>
    private static (string Text, string? Lang) Pick(JsonNode? obj, string field, string lang)
    {
        var bn = obj?[$"{field}Bn"]?.GetValue<string>();
        var en = obj?[$"{field}En"]?.GetValue<string>();
        return lang == "en" && !string.IsNullOrWhiteSpace(en) ? (en!, null) : (bn ?? "", lang == "en" && !string.IsNullOrWhiteSpace(bn) ? "bn" : null);
    }

    private static string Tag(string tag, (string Text, string? Lang) v, string? cls = null) =>
        $"<{tag}{(cls is null ? "" : $" class=\"{cls}\"")}{(v.Lang is null ? "" : $" lang=\"{v.Lang}\"")}>{E(v.Text)}</{tag}>";

    private static string Head(JsonObject text, string lang, string title, string description, string shareUrl, string pageUrl, string? publishedAt, string baseUrl)
    {
        var site = TruthText.Get(text, lang, "site.name");
        var other = lang == "bn" ? "en" : "bn";
        return $"""
            <!DOCTYPE html>
            <html lang="{lang}">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{E(title)} · {E(site)}</title>
            <meta name="description" content="{E(description)}">
            <link rel="canonical" href="{E(baseUrl + pageUrl)}">
            <link rel="alternate" hreflang="{other}" href="{E(baseUrl + shareUrl.Replace($"lang={lang}", $"lang={other}"))}">
            <meta property="og:type" content="article">
            <meta property="og:site_name" content="{E(site)}">
            <meta property="og:title" content="{E(title)}">
            <meta property="og:description" content="{E(description)}">
            <meta property="og:url" content="{E(baseUrl + shareUrl)}">
            <meta property="og:locale" content="{(lang == "bn" ? "bn_BD" : "en_US")}">
            <meta property="og:locale:alternate" content="{(lang == "bn" ? "en_US" : "bn_BD")}">
            {(publishedAt is null ? "" : $"<meta property=\"article:published_time\" content=\"{E(publishedAt)}\">")}
            <meta name="twitter:card" content="summary">
            <meta name="twitter:title" content="{E(title)}">
            <meta name="twitter:description" content="{E(description)}">
            <link rel="stylesheet" href="/truth-share.css">
            </head>
            <body>
            <main>
            <p class="site">{E(site)} · {E(TruthText.Get(text, lang, "site.series"))}</p>
            """;
    }

    private static string Foot(JsonObject text, string lang) =>
        $"""
        <footer><p>{E(TruthText.Get(text, lang, "footer.note"))}</p></footer>
        </main>
        </body>
        </html>
        """;

    private static string References(JsonObject text, string lang, JsonArray? sources)
    {
        var sb = new StringBuilder($"<h3>{E(TruthText.Get(text, lang, "refs.title"))}</h3><ol class=\"refs\">");
        foreach (var s in sources ?? new JsonArray())
        {
            var who = s!["author"]?.GetValue<string>() is { } a ? $"{a}, {s["publisher"]!.GetValue<string>()}" : s["publisher"]!.GetValue<string>();
            var date = s["publishedOn"]?.GetValue<string>() is { } d ? " · " + TruthTime.Day(text, lang, d) : "";
            sb.Append($"<li><a href=\"{E(s["url"]!.GetValue<string>())}\" rel=\"noopener noreferrer\">{E(s["title"]!.GetValue<string>())}</a> <span>{E(who)}{E(date)} · {E(TruthText.Get(text, lang, $"refs.{s["kind"]!.GetValue<string>()}"))}</span></li>");
        }
        return sb.Append("</ol>").ToString();
    }

    public static string IssuePage(JsonObject snapshot, JsonObject text, string lang, string baseUrl, string shareSlug)
    {
        var issue = snapshot["issue"]!;
        var slug = issue["slug"]!.GetValue<string>();
        var title = Pick(issue, "title", lang);
        var subtitle = Pick(issue, "subtitle", lang);
        var items = snapshot["items"]!.AsArray();
        var description = !string.IsNullOrWhiteSpace(subtitle.Text) ? subtitle.Text
            : string.Join(" · ", items.Take(3).Select(i => Pick(i, "headline", lang).Text));
        var sb = new StringBuilder(Head(text, lang, title.Text, description, $"/truth/s/issue/{shareSlug}?lang={lang}", $"/truth/{slug}?lang={lang}", issue["publishedAt"]?.GetValue<string>(), baseUrl));
        sb.Append(Tag("h1", title));
        if (!string.IsNullOrWhiteSpace(subtitle.Text)) sb.Append(Tag("p", subtitle, "lead"));
        var range = TruthText.Get(text, lang, "time.range", ("start", TruthTime.Day(text, lang, issue["weekStart"]!.GetValue<string>())), ("end", TruthTime.Day(text, lang, issue["weekEnd"]!.GetValue<string>())));
        var updated = TruthText.Get(text, lang, "hero.updated", ("time", TruthTime.DateTimeText(text, lang, Utc(issue["publishedAt"]!.GetValue<string>()))));
        sb.Append($"<p class=\"muted\">{E(range)} · {E(updated)} · {E(TruthText.Get(text, lang, "time.zoneNote"))}</p>");
        sb.Append($"<p><a class=\"open\" href=\"/truth/{E(slug)}?lang={lang}\">{E(TruthText.Get(text, lang, "share.open"))}</a></p>");
        foreach (var item in items)
        {
            var status = item!["status"]!.GetValue<string>();
            sb.Append("<section class=\"item\">");
            sb.Append($"<p class=\"tech\">{E(item["tech"]!["name"]!.GetValue<string>())} · <span class=\"pill\">{E(TruthText.Get(text, lang, $"status.{status}"))}</span> · {E(TruthText.Get(text, lang, "severity.label"))}: {E(TruthText.Get(text, lang, $"severity.{item["severity"]!.GetValue<string>()}"))}</p>");
            sb.Append(Tag("h2", Pick(item, "headline", lang)));
            if (item["patchScheduledAt"]?.GetValue<string>() is { } patch)
                sb.Append($"<p class=\"patch\">{E(TruthText.Get(text, lang, "item.patchAt", ("time", TruthTime.WithUtc(text, lang, Utc(patch)))))} · {E(TruthText.Get(text, lang, "time.zoneNote"))}</p>");
            sb.Append(Tag("p", Pick(item, "summary", lang)));
            var todo = Pick(item, "whatToDo", lang);
            if (!string.IsNullOrWhiteSpace(todo.Text)) sb.Append($"<h3>{E(TruthText.Get(text, lang, "item.whatToDo"))}</h3>").Append(Tag("p", todo));
            sb.Append(References(text, lang, item["sources"]?.AsArray()));
            foreach (var c in item["corrections"]?.AsArray() ?? new JsonArray())
            {
                var at = TruthText.Get(text, lang, "item.correctionAt", ("time", TruthTime.DateTimeText(text, lang, Utc(c!["at"]!.GetValue<string>()))));
                sb.Append($"<p class=\"correction\">{E(at)}: ").Append(Tag("span", Pick(c, "text", lang))).Append("</p>");
            }
            sb.Append("</section>");
        }
        return sb.Append(Foot(text, lang)).ToString();
    }

    /// <summary>A guide for its default platform and versions — the steps as a reader would copy them, rendered by the same template renderer.</summary>
    public static string GuidePage(JsonObject snapshot, JsonObject text, string lang, string baseUrl)
    {
        var guide = snapshot["guide"]!;
        var slug = guide["slug"]!.GetValue<string>();
        var phase = guide["phase"]!.GetValue<string>();
        var title = Pick(guide, "title", lang);
        var intro = Pick(guide, "intro", lang);
        var values = new Dictionary<string, string> { ["platform"] = snapshot["platforms"]!.AsArray().FirstOrDefault()?["key"]?.GetValue<string>() ?? "any" };
        foreach (var (name, node) in snapshot["variables"] as JsonObject ?? new JsonObject())
            values[name] = node?["default"]?.GetValue<string>() ?? node?["options"]?.AsArray().FirstOrDefault()?.GetValue<string>() ?? "";
        var platform = snapshot["platforms"]!.AsArray().FirstOrDefault();
        var description = string.IsNullOrWhiteSpace(intro.Text) ? TruthText.Get(text, lang, $"phase.{phase}") : intro.Text;

        var sb = new StringBuilder(Head(text, lang, title.Text, description, $"/truth/s/guide/{slug}?lang={lang}", $"/truth/guide/{slug}?lang={lang}", guide["publishedAt"]?.GetValue<string>(), baseUrl));
        sb.Append($"<p class=\"phase\">{E(TruthText.Get(text, lang, $"phase.{phase}"))}</p>").Append(Tag("h1", title));
        if (!string.IsNullOrWhiteSpace(intro.Text)) sb.Append(Tag("p", intro, "lead"));
        sb.Append($"<p><a class=\"open\" href=\"/truth/guide/{E(slug)}?lang={lang}\">{E(TruthText.Get(text, lang, "share.openGuide"))}</a></p>");
        if (platform is not null)
        {
            var versions = string.Join(", ", values.Where(v => v.Key != "platform").Select(v => v.Key + " " + v.Value));
            var shownFor = TruthText.Get(text, lang, "guide.shownFor", ("platform", platform[lang == "en" ? "nameEn" : "nameBn"]!.GetValue<string>()), ("versions", versions));
            sb.Append($"<p class=\"muted\">{E(shownFor)}</p>");
        }
        if (guide["backupFirst"]?.GetValue<bool>() == true) sb.Append($"<p class=\"backup\">{E(TruthText.Get(text, lang, "guide.backup"))}</p>");
        sb.Append("<ol class=\"steps\">");
        foreach (var step in snapshot["steps"]!.AsArray())
        {
            var p = step!["platform"]!.GetValue<string>();
            if (p != "any" && p != values["platform"]) continue;
            if (step["condition"]?.GetValue<string>() is { } cond && !GuideTemplate.EvalCondition(cond, values)) continue;
            var kind = step["kind"]!.GetValue<string>();
            sb.Append($"<li class=\"step {E(kind)}\"><p class=\"kind\">{E(TruthText.Get(text, lang, $"kind.{kind}"))}</p>");
            var stepTitle = Pick(step, "title", lang);
            if (!string.IsNullOrWhiteSpace(stepTitle.Text)) sb.Append(Tag("h2", stepTitle));
            var body = Pick(step, "body", lang);
            if (!string.IsNullOrWhiteSpace(body.Text)) sb.Append(Tag("p", body));
            if (step["code"]?.GetValue<string>() is { } code) sb.Append($"<pre><code>{E(GuideTemplate.Render(code, values).Text)}</code></pre>");
            if (step["expected"]?.GetValue<string>() is { } expected)
                sb.Append($"<p class=\"muted\">{E(TruthText.Get(text, lang, "guide.expected"))}</p><pre><code>{E(GuideTemplate.Render(expected, values).Text)}</code></pre>");
            var breaks = Pick(step, "sideEffects", lang);
            if (!string.IsNullOrWhiteSpace(breaks.Text)) sb.Append($"<p class=\"breaks\"><strong>{E(TruthText.Get(text, lang, "guide.mayBreak"))}:</strong> ").Append(Tag("span", breaks)).Append("</p>");
            sb.Append("</li>");
        }
        sb.Append("</ol>");
        sb.Append(References(text, lang, snapshot["sources"]?.AsArray()));
        return sb.Append(Foot(text, lang)).ToString();
    }

    /// <summary>RSS 2.0 of the published issues and guides, in one language.</summary>
    public static string Feed(IEnumerable<JsonObject> issues, IEnumerable<JsonObject> guides, JsonObject text, string lang, string baseUrl)
    {
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8, OmitXmlDeclaration = false }))
        {
            w.WriteStartDocument();
            w.WriteStartElement("rss");
            w.WriteAttributeString("version", "2.0");
            w.WriteStartElement("channel");
            w.WriteElementString("title", $"{TruthText.Get(text, lang, "site.name")} · {TruthText.Get(text, lang, "site.series")}");
            w.WriteElementString("link", $"{baseUrl}/truth?lang={lang}");
            w.WriteElementString("description", TruthText.Get(text, lang, "site.description"));
            w.WriteElementString("language", lang == "bn" ? "bn-BD" : "en");
            w.WriteElementString("lastBuildDate", DateTime.UtcNow.ToString("R", CultureInfo.InvariantCulture));
            void Item(string title, string link, string guid, string description, string? date)
            {
                w.WriteStartElement("item");
                w.WriteElementString("title", title);
                w.WriteElementString("link", link);
                w.WriteStartElement("guid"); w.WriteAttributeString("isPermaLink", "false"); w.WriteString(guid); w.WriteEndElement();
                w.WriteElementString("description", description);
                if (date is not null) w.WriteElementString("pubDate", DateTime.Parse(date, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal).ToString("R", CultureInfo.InvariantCulture));
                w.WriteEndElement();
            }
            foreach (var s in issues)
            {
                var issue = s["issue"]!;
                var slug = issue["slug"]!.GetValue<string>();
                var heads = string.Join(" · ", s["items"]!.AsArray().Select(i => Pick(i, "headline", lang).Text));
                Item(Pick(issue, "title", lang).Text, $"{baseUrl}/truth/{slug}?lang={lang}", $"truth-issue-{slug}", heads, issue["publishedAt"]?.GetValue<string>());
            }
            foreach (var s in guides)
            {
                var guide = s["guide"]!;
                var slug = guide["slug"]!.GetValue<string>();
                Item(Pick(guide, "title", lang).Text, $"{baseUrl}/truth/guide/{slug}?lang={lang}", $"truth-guide-{slug}",
                    $"{TruthText.Get(text, lang, $"phase.{guide["phase"]!.GetValue<string>()}")} · {Pick(guide, "intro", lang).Text}", guide["publishedAt"]?.GetValue<string>());
            }
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndDocument();
        }
        // XmlWriter over a StringBuilder declares UTF-16; the response is UTF-8.
        return sb.ToString().Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");
    }
}
