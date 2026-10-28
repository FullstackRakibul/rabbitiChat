# Ministry of Truth, Phase P5: Records Department (automatic collection)

An add-on to the Ministry of Truth prompt (v4). **Stay on .NET 8. Open-source packages only.** This phase replaces the "feed inbox" line in §9 *Later*.

## 0. Goal

A background collector, the **Records Department / রেকর্ড বিভাগ**, gathers programming and tech news, security advisories and service status from **public sources**. It shows them on `/truth` as an **expandable list** in the 1984 theme. The weekly issue stays hand-written; the collector feeds it.

Three kinds of content, each with its own trust level:

| Kind | Example | Shown publicly | Label |
|---|---|---|---|
| **Status** (service up or down, incidents) | OpenAI, Vercel, Node.js status pages | **Automatically**, as the source wrote it: title, state, time, link | `STATUS · স্বয়ংক্রিয়` (auto) |
| **Wire** (advisories, releases, news headlines) | php.net news, the Node.js blog, CISA KEV, news sites | **Automatically** as a headline, the feed's own short description (≤ 300 characters), time and link. No AI text | `AUTO-COLLECTED · NOT YET VERIFIED / স্বয়ংক্রিয় · যাচাই হয়নি` stamp |
| **Verified item** | a weekly-issue item with a summary, guide and references | **Only after a person publishes it** (unchanged) | `VERIFIED / যাচাইকৃত` stamp |

Automation **never**:
- writes a summary that goes public without a person;
- sends an email;
- changes an item's status.

It can **suggest** all of those to the admin.

## 1. What the owner's sources offer (checked 25 Oct 2026)

| Source | Platform | Machine-readable feed | Use |
|---|---|---|---|
| `https://status.openai.com` and `/history` | incident.io | **RSS:** `https://status.openai.com/feed.rss`. A valid RSS 2.0 feed of incidents with status and affected component in the description. Note the item links contain a double slash (`//incidents/…`); normalise them. | `incidentio-rss` adapter |
| `https://www.vercel-status.com/` | Atlassian Statuspage | **RSS / Atom:** `/history.rss`, `/history.atom` (advertised on the page). The `/api/v2/*.json` path is **disallowed by robots.txt**, so don't use it. | `statuspage-feed` adapter |
| `https://status.nodejs.org/` | Atlassian Statuspage | **RSS / Atom:** `/history.rss`, `/history.atom` | `statuspage-feed` adapter |
| `https://digitrans.lu/blog/php2shell-nodejs-vulnerabilities-ai-cybersecurity-response/` | Company blog | **No RSS or Atom found** and no reuse licence stated | `url-drop` (an editor pastes the link; §3.4). Summarise and link only. |

The status pages' feeds give **incidents**, not live component state. Current state comes from the newest open incident: an unresolved incident means "incident", otherwise "operational". Add **"last checked <time>"** next to it. Don't scrape the HTML for component health.

## 2. Source registry (JSON, editable, no code change to add a source)

Define the sources in `backend/config/truth-sources.json` (committed). The admin can switch a source off and press **Test**.

```json
{ "id": "openai-status", "name": "OpenAI", "kind": "status", "adapter": "incidentio-rss",
  "url": "https://status.openai.com/feed.rss", "homepage": "https://status.openai.com",
  "tech": ["openai"], "tier": "official", "everyMinutes": 5, "enabled": true,
  "publish": "auto" }
```

- **`adapter`** is one of `incidentio-rss`, `statuspage-feed`, `rss-atom`, `cisa-kev-json`, `osv-api`, `github-advisories`, `url-drop`.
- **`tier`** is `official` (the vendor or project itself), `authority` (CISA, CERT, NVD), `community` (a reputable blog) or `news`. The tier is shown on each row.
- **`publish`** is `auto` (the row appears on the wire) or `inbox` (the admin sees it first). Defaults: `status` → auto; `official`/`authority` advisories and releases → auto; `news` and `community` → **inbox**.

**Starter set.** The first three entries were checked above. For every other entry, Claude Code must open the URL, confirm it is a public feed or API, check robots.txt and terms, and only then enable it, recording the check in the report. Drop any source that fails.
- **Status:** OpenAI, Vercel, Node.js (checked). Candidates: GitHub, Cloudflare, npm, Railway, OpenRouter, Anthropic.
- **Security authorities:** CISA Known Exploited Vulnerabilities (JSON), the OSV.dev API (by package ecosystem), the GitHub Advisory Database (REST, no token for low volume).
- **Official project news and releases:** php.net news, the Node.js blog (including its security releases), .NET release notes (`releases-index.json`), endoflife.date (version lines and EOL dates, to fill `Tech.VersionLines`).
- **News** (inbox only, headline and link): a few well-known security and tech news feeds the owner picks. Each needs a public RSS feed.

## 3. Collector (backend)

### 3.1 Runtime
- A `BackgroundService` called `RecordsCollector` runs one loop. Each source runs on its own schedule (`everyMinutes`, with a minimum of 5) with ±20% jitter, at most 4 fetches at a time.
- **Polite fetching:**
  - User-Agent `MinistryOfTruthBot/1.0 (+https://mostisko.vercel.app/truth/about)`;
  - conditional requests (`ETag` / `If-Modified-Since`) for 304 responses;
  - a 15 s timeout and a 2 MB cap;
  - backoff on errors (5 min → 1 h);
  - stop and flag a source after 10 failures in a row.
- **Only URLs from the registry are fetched.** Use HTTPS only, block private and LAN IPs, and re-check the IP after redirects (an SSRF guard, the same code as the P4 fetcher).
- **robots.txt** is honoured for anything that isn't an advertised feed or documented API. Never scrape where it is disallowed.
- Respect each source's terms. Keep only the title, link, time, status and the feed's own short description. Don't store full article bodies from news or community sources; only `url-drop` stores fetched text, privately, for the number check.

### 3.2 Normalised record (`WireEntry`)

| Field | Notes |
|---|---|
| Id, SourceId, ExternalId | The feed's `guid`, or a hash of the link if there isn't one; unique per source |
| Kind | status / advisory / release / news |
| Title | as published, in the original language |
| Excerpt | the feed description, stripped to plain text, ≤ 300 characters |
| Url | normalised (fix `//`, drop tracking parameters) |
| PublishedAt, UpdatedAt | UTC |
| State | for status: `investigating` / `identified` / `monitoring` / `resolved` / `maintenance`, parsed from the title or description with a per-adapter map |
| Components | for status, when the feed names them |
| CveIds, Techs, Versions | extracted by regex and a keyword map (`tech-keywords.json`), e.g. `php`, `node`, `vercel`, `openai` |
| Severity | from CVSS when known; `critical` if in CISA KEV; otherwise none |
| ClusterId | entries about the same thing (§3.3) |
| Visibility | public / inbox / hidden (an admin can hide a row; the hiding is logged) |

Incident updates (the same `ExternalId` with new text) **update** the entry and add a row to `WireEntryHistory`. They don't create a duplicate.

### 3.3 Clustering (one story, many sources)
- Entries that share a **CVE ID** go into one cluster.
- Otherwise, entries share a cluster when they have the same tech, fall within 72 hours of each other, and have similar titles (token Jaccard ≥ 0.5).
- A cluster shows its best title (official tier first) and "**3 sources**". Expanding it lists every source with tier and time.
- **Disagreements are shown, not hidden.** When sources in a cluster give different numbers for the same field (a different fixed version, or a different patch time such as 13:30 UTC in one and 14:00 UTC in another), the expanded view shows a small "সূত্রে অমিল / Sources differ" note listing each value with its source. The admin gets a flag.

### 3.4 URL drop (for blogs without feeds)
- In the admin, the editor pastes a link. The backend fetches it once (SSRF guard, robots.txt check) and extracts the readable text with **SmartReader** (Apache-2.0) or **AngleSharp** (MIT). It stores the text privately for the number check.
- It creates an **inbox** entry with the title and link. The editor may then ask the AI draft assist (P4) for a Bangla/English summary, which becomes a normal draft item with the link in References.
- The page content is never published as is.

### 3.5 AI's role (Ollama, optional)
- **Cluster digest drafts:** on demand, the model writes a 2–3 line Bangla + English summary of a cluster from the stored excerpts and text (schema-forced JSON, temperature 0, number check). It stays a **draft** until a person publishes it as an item.
- **Machine translation** of auto rows' titles into Bangla is optional and off by default. When on, it shows a "যন্ত্র-অনুবাদ / machine-translated" tag with the original one tap away.
- **The model never fetches and never decides visibility.**

### 3.6 Suggestions to the admin (never automatic actions)
- "Possible urgent": a new CISA KEV entry for a tracked tech, a status incident with "major outage" in it, or a cluster with 3+ sources in 24 hours. Each comes with buttons: create item from cluster, create a guide, or ignore.
- "Version line changed": endoflife.date or official releases show a new patched version. Approving updates `Tech.VersionLines`, which the version checker uses.

## 4. Public UI: "Records Department" (expanded list, 1984 theme)

Insert two sections into `/truth` after the status board (themed nicknames in the site JSON):

### 4.1 TELESCREEN · লাইভ অবস্থা (live status strip)
- One chip per status source: lettermark, name, state dot plus a word (চালু / সমস্যা / রক্ষণাবেক্ষণ; Operational / Incident / Maintenance) and "checked 3 min ago".
- Tapping a chip expands its open incident (title, latest update, time in Bangladesh time, "উৎস দেখুন / View on source" link) and the last 5 resolved incidents.
- If a source is stale (no successful check in 30 min), the chip shows "তথ্য পুরনো / data stale", never a guessed state.

### 4.2 RECORDS DEPT. · তারবার্তা / The Wire (expandable list)
- **Rows grouped by day** (Bangladesh time). Each row shows the time, a source lettermark, a **type tag** (STATUS · ADVISORY · RELEASE · NEWS), the title, a severity or state pill if known, the tier badge and the "N sources" count.
- **Tap or click to expand** (one row open at a time on mobile; works with keyboard via Enter/Space and `aria-expanded`):
  - the feed excerpt, in quote style with "সূত্র থেকে / From the source";
  - an update history for status incidents (times + states);
  - every source in the cluster with tier, time and link;
  - the "Sources differ" note when present;
  - extracted chips (CVE IDs link to cve.org, tech, versions);
  - if a verified item exists, a "যাচাইকৃত সংবাদ পড়ুন / Read the verified item" link with the VERIFIED stamp.
- **Filters** (chips, remembered per browser): tech, type, severity, tier, "only unresolved". **Search** runs in the browser over the loaded rows.
- **Paging:** the latest 50 rows, then "আরও লোড করুন / Load more" (cursor paging, not infinite scroll).
- **Theme touches** (decoration only; §3A.2 still applies):
  - the section is drawn as a teleprinter strip with perforated edges;
  - new rows arrive with a small **pneumatic-tube capsule** drop animation (once per new row, ≤ 400 ms, off in serious mode and reduced motion);
  - Little Brother sits on the header in the "reading" pose;
  - auto rows carry the rotated grey stamp `AUTO-COLLECTED · NOT YET VERIFIED`.

  Stamps never replace the pills or the text.
- **Empty or stale state:** "রেকর্ড বিভাগ এখন চুপচাপ / The Records Department is quiet right now", with the last successful collection time.

### 4.3 Public API (read-only, through the funnel)
- `GET /public/truth/status`: the status chips. Cached 60 s.
- `GET /public/truth/wire?cursor=&tech=&type=&severity=&tier=&open=`: public rows only, cached 60 s with ETag.
- `GET /public/truth/wire/{clusterId}`: the expanded detail.
- The response allow-list excludes the stored page text, hidden rows, admin notes and internal error text.
- Add `/truth-api/status` and `/truth-api/wire*` to the existing Vercel rewrite (nothing new to expose; same `/public/truth/*` mount).
- When the backend is down, the shell shows the saved copy, with "last collected <time>".

## 5. Admin: "Records" sub-tab
- **Sources:** each source's health (last success, last error, entries in 24 hours, next run), an on/off switch, **Test now** (fetch + parse preview, no save), and add/edit with schema validation of `truth-sources.json` (or an override stored in the database).
- **Inbox:** news, community and flagged items with buttons: publish to wire, hide, create item from cluster, ask for an AI draft.
- **Suggestions:** the possible-urgent and version-line flags from §3.6.
- **Audit:** every hide, publish or override, with who and when.

## 6. Gates (evals in `scripts/truth-eval.mjs`; live network never used in evals)

Evals use a local fake feed server that serves **synthetic** fixture files: an incident.io-style RSS feed, a Statuspage Atom feed, a KEV JSON file and a blog HTML page. No real feeds are copied into fixtures.

- **G5.1 Adapters:** each adapter parses its fixture correctly (titles, times in UTC, links normalised including the `//` case, states mapped). Malformed XML or JSON gives a logged source error, not a crash.
- **G5.2 Dedupe and updates:** the same guid fetched twice gives 1 entry. An updated incident gives 1 entry plus 1 history row, with the state moving from investigating to resolved.
- **G5.3 Clustering:** 3 fixtures sharing a CVE give 1 cluster with "3 sources". Planted conflicting fixed versions and patch times show the "Sources differ" note with each value.
- **G5.4 Politeness:** a second fetch sends `If-None-Match`/`If-Modified-Since` and handles 304. The timeout, size cap, backoff and 10-failure flag all work. The minimum interval of 5 min is enforced.
- **G5.5 Safety:** the fetcher refuses URLs not in the registry, `http://`, private IPs and redirects to private IPs. `url-drop` honours a fixture robots.txt that disallows the path.
- **G5.6 Trust rules:** news and community sources default to inbox; nothing auto-published contains AI text; no email is ever queued by the collector; auto rows always carry the auto stamp; a cluster digest stays a draft until published.
- **G5.7 Stale state:** with a source failing for over 30 min, its chip shows "data stale", never "operational".
- **G5.8 Public allow-list:** the wire and status JSON contain no hidden rows, stored page text, admin notes or error text.
- **G5.9 UI:** at 360 px, rows expand and collapse by tap and keyboard with `aria-expanded`, there is no horizontal scroll, filters and search make no network request, and "load more" uses the cursor. Animations are off in serious mode and reduced motion. Screenshots in both themes and both languages.
- **G5.10 Live smoke test (manual, reported):** with the backend running, the 3 checked sources (OpenAI, Vercel, Node.js) each complete one real fetch, and their chips appear on the live `/truth` through Vercel. Report entries per source and the time taken. Vercel's JSON API is not called.
- **G5.11 Licence check:** SmartReader or AngleSharp and any other new package pass `license-check.mjs`.

## 7. Do not
- Do not scrape HTML where an RSS, Atom or JSON feed exists, or where robots.txt disallows it. **Do not call `vercel-status.com/api/v2/*`.**
- Do not store or republish full article text from news or community sites. Use the title, a short feed excerpt and the link only.
- Do not let automation publish summaries, change statuses, send emails or edit `VersionLines` without a person.
- Do not guess a service's state. When data is old, say it is stale.
- Do not fetch any URL that isn't in the registry, except through the editor's explicit URL drop.
- Do not poll more often than every 5 minutes per source.
- Do not use real feed content in fixtures.
- Do not upgrade the framework. Stay on .NET 8.

## 8. Report format
As in the main prompt, plus:
- a source check table (URL, feed type, robots and terms checked, enabled yes/no, reason);
- the live smoke-test numbers;
- screenshots of the Telescreen strip and the expanded Wire list.
