# Ministry of Truth: public tech-security bulletin

Build prompt for Claude Code. Repo: `rabbitiChat` (.NET 8 minimal API, Vue 3, Tailwind v4, SQLite). **Stay on .NET 8.** This is version 4 (decisions closed in §13). Version 3 added the owner's answers: Vercel hosting, free open-source mail, Bangla + English with global JSON text, the standalone name, neutral icons, summary-and-link only, and more platforms. **Version 4 adds the visual theme (§3A):** an Orwell *1984* "telescreen" look with a cracked-glass ("shatter") effect, small cute meme banners and an original mascot. The theme has one hard rule: **the jokes are decoration; the safety information is always plain and serious.**

## 0. What we are building

**Ministry of Truth** (Bangla name in the site JSON, default "সত্য মন্ত্রণালয়") is a standalone public module. Anyone can open it from a URL with **no login**. It publishes a weekly issue, by default the series "সাইবার সপ্তাহ / Cyber Week", with security and update news for the technologies people run: PHP, Node.js, Railway, OpenRouter, certificate authorities and others. For each item it shows:
- what happened, when, and how bad it is;
- the current status;
- which versions are affected and which versions fix it;
- **a step-by-step fix guide** for before and after the patch, for each platform and version;
- a **summary in our own words with links and references** to the public sources.

It is mobile first, with a Bangla/English switch. At the end of the page, visitors can **subscribe** by email or use RSS.

**Content rule from the owner:** only public information, summarised, with a link and a reference. No copied articles.

Staff write and publish inside RabbitiChat in a new **Ministry of Truth** admin tab protected by RBAC. The public site only reads published data.

**Hosting:** the frontend is already on Vercel at `https://mostisko.vercel.app/`, and the public module lives under **`https://mostisko.vercel.app/truth`**. The backend stays on the LAN and is reached only through a tunnel for the public `/public/truth/*` paths (§4).

**Reference material:**
1. The user's 7-slide carousel: weekly log, status board, PHP, Node.js, Railway, "3 more this week" and "do it now".
2. A community post about a temporary PHP fix. It shows the shape of a good guide: warning → patch time in UTC and Bangladesh time → backup first → setting with commands for each version (CLI and FPM) → verify with expected output → option for panel users → Cloudflare rule → "install the patch when it is out" → sources.

```
Admin (RabbitiChat, LAN, logged in)            Public (no login)
Ministry of Truth tab ─ draft → review → publish   mostisko.vercel.app/truth          current issue
  ├ Items · Fix guides · Site text (JSON)           /truth/2026-w43                    archive issue
  ├ AI draft assist (Ollama, JSON)                  /truth/guide/:slug                 fix guide
  └ Number check · Command safety check             /truth/s/:kind/:slug               share link (OG + no-JS)
SQLite ── snapshot on publish ──▶ /public/truth/* ◀── tunnel ◀── Vercel rewrites (/truth-api/*, /truth/s/*)
  └─▶ mail queue (MailKit → SMTP relay) → confirmed subscribers
```

## 1. Rules (these apply to every phase)

### 1.1 Truth
1. **Public information only.** Every item and guide is built from public sources and has at least 1 source link with publisher and title. Publishing is blocked without one.
2. **Summarise, link and reference.** Write in our own words and never paste third-party articles. Short quotes (a CVE title, an official sentence) are allowed in quote marks with the link. A guide based on a community post shows "ভিত্তি / Based on: <author>, <site>" with the link.
3. **Confirmed and claimed are different statuses.** A claim made only on the dark web or only by the attacker shows as "নিশ্চিত না / Unconfirmed".
4. **Numbers must come from a source.** Every CVE ID, CVSS score, version, date and time, count or money amount must appear in a source's fetched text. Failures show in red and block publishing until fixed or marked "verified by hand" with a note.
5. **Status changes are logged** (`StatusEvent`) and shown with their time. **Corrections are visible** (সংশোধন / Correction), never silent.
6. **Times are shown in Bangladesh time first** ("সময় বাংলাদেশের / Bangladesh time") and stored as UTC. When a source gives UTC, show both: "রাত ৮টা (14:00 UTC)" (8 PM, 14:00 UTC).
7. **Never publish your own infrastructure:** no company server names, IPs, versions or patch state.
8. **Fix guides must be safe to copy:** they pass the command safety check (§6.4), and temporary mitigations are always labelled temporary.

### 1.2 Open-source only (owner's standing rule)
- **Every new dependency** (NuGet, npm, fonts, icons, CLI tools) must have an **OSI-approved open-source licence**. That includes MIT, BSD, Apache-2.0, ISC, MPL-2.0, LGPL and OFL for fonts. Paid or closed SDKs and libraries that need a paid account are not allowed.
- Record each one in `docs/THIRD_PARTY.md` with its name, version, licence and purpose.
- Add `scripts/license-check.mjs`, which lists every npm and NuGet dependency with its licence and fails on any licence that isn't on the allow-list. It runs in the eval suite.
- External *services* (an SMTP relay, a tunnel) are allowed only when they are free and swappable through configuration. The code that talks to them must be open source and standard (SMTP, HTTP).

**Suggested packages** (all open source; check that each version is at least 2 weeks old):

| Need | Package | Licence |
|---|---|---|
| Sending mail | MailKit (+ MimeKit) | MIT |
| Markdown → HTML (server) | Markdig | BSD-2 |
| HTML sanitising (server) | HtmlSanitizer (mganss) | MIT |
| Version comparison (server) | Semver (NuGet `Semver`) | MIT |
| RSS | System.ServiceModel.Syndication | MIT |
| i18n (frontend) | vue-i18n | MIT |
| Markdown (frontend) | marked + DOMPurify (already in the repo) | MIT / Apache-2.0 or MPL-2.0 |
| Version comparison (frontend) | a small compare function written in the repo (no dependency) | — |
| Fonts | Noto Sans Bengali, Hind Siliguri, JetBrains Mono, Anton or Bebas Neue, Baloo Da 2, Special Elite | OFL (Special Elite: Apache-2.0) |
| Neutral icons | Lucide (already in the repo) for UI; tech icons are our own lettermarks (§3) | ISC |

## 2. Data model (SQLite, EF Core)

| Entity | Fields |
|---|---|
| `Tech` | Id, Slug, Name, Category (language / runtime / platform / AI provider / CA / database / app), Monogram (1–2 letters), Hue (0–359, for the neutral icon), Homepage, VersionLines (JSON) |
| `Issue` | Id, Slug (`2026-w43`), TitleBn, TitleEn, SubtitleBn, SubtitleEn, WeekStart, WeekEnd, Status (draft / review / published), PublishedAt, PublishedBy |
| `Item` | Id, IssueId, TechId, Severity, Status (pills below), HeadlineBn, HeadlineEn, SummaryBn, SummaryEn, ImpactBn, ImpactEn, PatchScheduledAt (UTC), Tags (JSON), AuthorName, Order |
| `Advisory` | Id, ItemId, CveId, Cvss, TitleBn, TitleEn, AffectedRange, FixedVersions (JSON), ExploitedInWild, KevListed |
| `TimelineEvent` | Id, ItemId, OccurredAt, Level (ERR / WARN / FIX / INFO), TextBn, TextEn |
| `Guide` | Id, ItemId (nullable), Slug, TitleBn, TitleEn, Phase (`before_patch` / `after_patch` / `hardening`), IntroBn, IntroEn, Variables (JSON, §6.2), Platforms (list of platform keys, §6.5), BackupFirst, AuthorName, BasedOnSourceId, Status, ReviewedBy, ReviewedAt |
| `GuideStep` | Id, GuideId, Order, Kind (`note` / `command` / `config` / `verify` / `panel` / `waf_rule` / `warning` / `undo`), Platform (key or `any`), TitleBn, TitleEn, BodyBn, BodyEn, Code (template), Language, ExpectedOutput (template), Condition (JSON), SideEffectsBn, SideEffectsEn |
| `SafetyFinding` | Id, GuideStepId, Variant (platform × version), RuleId, Level (block / warn), Message, AcknowledgedBy, AcknowledgedNote |
| `Source` | Id, ItemId or GuideId, Url, Publisher, Author, Title, FetchedText, FetchedAt, Kind (official / vendor / research / community / news / claim) |
| `StatusEvent`, `Correction` | as in v2, with Bn and En text fields |
| `SiteContent` | Id, Version, Json (the global text, §5), SchemaVersion, EditedBy, EditedAt, Status (draft / published) |
| `Subscriber` | Id, Email, Status, Topics (JSON), Lang (bn / en), ConfirmTokenHash, UnsubTokenHash, CreatedAt, ConfirmedAt |
| `MailJob` / `Delivery` | Id, Kind (confirm / weekly / urgent / followup / test), IssueId or ItemId, SubscriberId, Status (queued / sent / failed / deferred), Attempts, NextAttemptAt, SentAt, Error |

**Language fields:** Bangla is required for publishing. English is optional. If it is missing and the visitor chose English, show the Bangla text with a small note: "English version not available yet."

**Status pills.** Their labels live in the site JSON (§5), not in code:
- `active_attack` (red)
- `breach_confirmed` (red)
- `patch_scheduled` (amber + countdown)
- `ongoing` (amber)
- `contained` (amber)
- `unconfirmed` (grey)
- `patched` (green)
- `blocked` (green)
- `resolved` (green)

## 3. Public pages (mobile first)

Routes on Vercel: `/truth`, `/truth/:issueSlug`, `/truth/guide/:slug`, `/truth/guides`, `/truth/glossary` (§3A.7) and `/truth/about` (how we verify). Unknown paths show the Room 101 page (§3A.6). The look and feel of every page follows §3A.

The **language switch** (বাংলা | EN) is always visible in the header. Its default comes from the browser language and falls back to Bangla. The choice is remembered in `localStorage` (try/catch) and in the URL as `?lang=en` so shared links keep the language. Changing it re-renders without reloading the page.

Page sections, in order:
1. **Hero** with week range, headline and last update. Above it, an **urgent banner** appears while any item is `active_attack` or `patch_scheduled`. A sticky chip nav scrolls horizontally.
2. **Status board** with icon, name, one line and pill. `patch_scheduled` rows show a live countdown.
3. **This-week log**, a terminal-style block with date, level, tech and line.
4. **Detail cards** with:
   - header (name, short fact, status box with time);
   - byline (author, date, tags) and share button;
   - advisories with linked CVE IDs, and "fixed in" version tiles;
   - timeline;
   - buttons for the before-patch and after-patch guides;
   - **"সূত্র / References"**, a numbered list of every source with publisher, title, date and link. The summary text cites them as [1], [2];
   - corrections.
5. **Version checker:** safe / update to X / EOL / patch not out yet (links to the guide). Runs in the browser only.
6. **Do now:** version-check commands, key-rotation chips and copy buttons.
7. **Archive** and the **all guides** list (filter by tech and platform).
8. **Subscribe**.

**Neutral icons.** No vendor logos anywhere. Each tech gets a rounded square with its `Monogram` (for example "PH", "N", "RW") on a muted background made from its `Hue`. It meets AA contrast in both themes and is generated as inline SVG. The same lettermark is used in emails.

**Quality budget:**
- Works from 360 px wide with no horizontal page scroll; only the chip nav and code blocks scroll inside themselves, and code blocks have a wrap toggle.
- Tap targets are at least 44 px. Body text is at least 16 px, and Bangla gets a line height of at least 1.6.
- Dark mode by default, light mode available (reuse `useTheme`). Contrast meets WCAG AA, and pills always include their text.
- Fonts are subset and use `font-display: swap`.
- Lighthouse mobile scores: Performance ≥ 90, Accessibility ≥ 95, Best Practices ≥ 95, SEO ≥ 95.
- The `/truth` bundle is at most 90 KB of gzipped JS.

## 3A. Theme: 1984 telescreen, cracked glass and cute memes

### 3A.1 The idea
In Orwell's *1984*, the Ministry of Truth rewrites the news. **Ours does the opposite:** every claim has a source, nothing is silently changed, and corrections stay visible. The theme plays with that irony through propaganda-poster typography, an old CRT "telescreen" with cracked glass, rubber stamps, redaction bars and small meme banners. **Inverted slogans** carry the joke:
- "PATCHING IS FREEDOM" / "প্যাচ করাই স্বাধীনতা";
- "IGNORANCE IS A VULNERABILITY" / "না জানাই দুর্বলতা";
- "2 + 2 = 4. ALWAYS." / "২ + ২ = ৪। সবসময়।";
- "LITTLE BROTHER IS NOT WATCHING YOU" / "ছোট ভাই আপনাকে দেখছে না" (privacy note, §3A.6).

The feel is **cute and clever, not dark or political.**

### 3A.2 Where the theme is allowed, and where it isn't

| Allowed (decoration) | Never themed (must stay plain and serious) |
|---|---|
| Hero decoration, section dividers, the mascot, meme banners, empty states, the 404 and backend-down pages, footer, loading states, section nicknames *next to* plain names, OG image frame | Status pills and their labels, severity, CVE/CVSS/versions, the urgent banner text, every fix-guide step, verify output, "what this may break", warnings, References, corrections, the subscribe form's privacy text, every email's safety content |

- **Serious mode turns on automatically.** While any item is `active_attack`, the hero drops the glitch and the memes, and shows only the plain red urgent banner. The mascot appears in its "worried" pose only. Memes stay only in the footer.
- **A visitor switch** ("সিরিয়াস মোড / Serious mode") turns all memes and effects off. It is remembered in `localStorage` (try/catch). `prefers-reduced-motion` turns off all motion automatically.

### 3A.3 Visual system (CSS variables, two themes)
- **Dark, the default ("Telescreen night"):** near-black ink background `#0E0F0C`, telescreen phosphor grey-green for surfaces, cream text `#EFE6D2`, poster red `#C8102E` for accents, amber `#F2A900` for highlights. Subtle CRT scanlines overlay the hero only (CSS gradient, opacity ≤ 0.06).
- **Light ("Poster paper"):** cream paper `#EFE6D2` with faint halftone dots, ink-black text, poster red accents.
- **Status colours are separate tokens from the poster palette.** Red, amber, grey and green pills keep WCAG AA contrast in both themes, and the poster red must never be mistaken for the "attack" red. Use different hue or lightness values, and pills always include their text.
- **Typography (open-source fonts only):**
  - English display: **Anton** or **Bebas Neue** (OFL), uppercase poster style;
  - Bangla display: **Baloo Da 2** or **Noto Sans Bengali Black** (OFL), heavy and friendly;
  - "Records Department" labels and stamps: **Special Elite** (Apache-2.0) typewriter;
  - body text: Noto Sans Bengali / Hind Siliguri; code: JetBrains Mono.
  - Subset every font and load display fonts only for the hero and banners.
- **Motifs** (all original, drawn as inline SVG or CSS; **no film stills, posters, book covers or Big Brother portraits**):
  - **Cracked-glass ("shatter") overlay:** one static SVG of thin crack lines from a corner of the hero telescreen frame, ≤ 8 KB. On first load it can "crack in" once (300 ms, stroke-dashoffset), and never again in the session.
  - **Glitch headline:** the hero title gets a short RGB-split glitch on load (CSS `clip-path` + `text-shadow`, ≤ 600 ms, plays once). **No looping flicker, and never more than 3 flashes per second** (WCAG 2.3.1).
  - **Telescreen frame:** the terminal blocks (this-week log, "do now") sit in a rounded CRT bezel with a tiny power LED that is green normally and blinks amber slowly (≥ 1 s period) while something is urgent.
  - **Rubber stamps:** "যাচাইকৃত / VERIFIED" on items whose sources are all `official` or `vendor`; "নিশ্চিত না / UNCONFIRMED" (grey) on claims; "সংশোধিত / CORRECTED" next to corrections. Stamps are rotated -6° with a rough edge (SVG filter), and are decorative. The real status is always also in the pill.
  - **Redaction bars:** a decorative effect for meme banners and the 404 page only (black bars that slide away on hover or tap). Never use them on real content; nothing factual is ever hidden.
  - **The eye:** a simple, friendly geometric eye icon used as the brand mark (an original design, round and cute, not menacing).

### 3A.4 Mascot: "ছোট ভাই / Little Brother"
- An **original** cute character: a small round CRT telescreen with stubby legs, one big friendly eye on its screen, and a crack across one corner (it has seen things). It is drawn as inline SVG, ≤ 10 KB, with simple shapes that work at 32 px and 160 px.
- **Poses** (separate SVG states, swapped by status):
  - **calm** (eye half-closed, sipping tea): nothing urgent;
  - **alert** (eye wide, antenna up): `patch_scheduled`, holding a small clock;
  - **worried** (sweat drop, screen flickers once): `active_attack`;
  - **relieved** (eye smiling, thumbs up): everything `patched` or `resolved`;
  - **reading** (glasses): guide pages;
  - **sleeping** (Zzz on screen): backend-down page.
- It appears in the hero corner, on empty states, the 404 and backend-down pages, the subscribe section and email headers (as a static PNG fallback for email). It never covers content, and is `aria-hidden` with no text inside the SVG.

### 3A.5 Meme banners (cute, small, editable)
- **Format:** small sticker-style banners (a tilted ribbon, a stamped card or a mini telescreen tile), at most 2 lines and ≤ 90 characters per language, with an optional mascot pose. They sit between sections and in the footer, **never more than 1 visible per screen height on mobile.**
- **Content lives in JSON** (`frontend/src/truth/content/memes.default.json`, with a global override published from the admin like the site text in §5):

  ```json
  { "id": "patch-freedom", "bn": "প্যাচ করাই স্বাধীনতা।", "en": "PATCHING IS FREEDOM.",
    "mood": "calm|alert|relief", "placement": ["divider","footer","404","empty","backend-down"],
    "style": "ribbon|stamp|telescreen", "pose": "calm", "weight": 1, "active": true }
  ```
- **Rotation:** pick by weight per page view among memes whose `mood` fits the current state (`alert` while anything is urgent, `relief` when everything is patched, `calm` otherwise) and whose `placement` matches the slot. Don't repeat in the same view.
- **Starter set** (the owner can edit; write all of them in both languages):
  - "PATCHING IS FREEDOM." / "প্যাচ করাই স্বাধীনতা।"
  - "IGNORANCE IS A VULNERABILITY." / "না জানাই দুর্বলতা।"
  - "2 + 2 = 4. And 8.3.20 < 8.3.36." / "২ + ২ = ৪। আর 8.3.20 < 8.3.36।"
  - "Rotate your keys. Not your eyes." / "চোখ নয়, key ঘোরান।"
  - "This ministry has a memory. No memory hole here." / "এই মন্ত্রণালয় কিছু ভোলে না। এখানে কোনো মেমোরি হোল নেই।"
  - "Unpatched server? That's a thoughtcrime." / "আনপ্যাচড সার্ভার? এটা চিন্তাপরাধ।"
  - "Backup first. Regret never." / "আগে ব্যাকআপ, পরে শান্তি।"
  - "Room 101 is for servers that skip updates." / "আপডেট না করা সার্ভারের জায়গা রুম ১০১।"
  - "Your .env is not a diary. Don't share it." / "আপনার .env ডায়েরি না, শেয়ার করবেন না।"
- **Meme rules** (the admin editor shows them as a checklist when a meme is added):
  - Joke about **bad practices** (unpatched servers, leaked keys, no backups), never about **people or victims**. No mocking a breached company's users or naming a person.
  - **No real politics:** no parties, leaders, governments, elections or religion. The 1984 references stay literary.
  - No fear-mongering and no fake urgency.
  - Short Orwell words and phrases (Big Brother, Newspeak, thoughtcrime, Room 101, memory hole, 2 + 2) are fine. **No quoted passages from the novel.**
  - Plain text only, never HTML.

### 3A.6 Themed section names (always with the plain name)
Section headers show the plain name large and the themed nickname small above it, typewriter style. The plain name always comes first for screen readers.

| Plain name (main) | Themed nickname (small) |
|---|---|
| Status board / অবস্থা | TELESCREEN · টেলিস্ক্রিন |
| This-week log / এই সপ্তাহ | RECORDS DEPT. · রেকর্ড বিভাগ |
| Fix guides / সমাধান গাইড | MINISTRY OF REPAIR · মেরামত মন্ত্রণালয় |
| Archive / আর্কাইভ | THE ARCHIVE THAT NEVER BURNS · যে আর্কাইভ পোড়ে না |
| Corrections / সংশোধন | NO MEMORY HOLE · কোনো মেমোরি হোল নেই |
| Glossary / শব্দকোষ (new, §3A.7) | NEWSPEAK DICTIONARY · নিউস্পিক অভিধান |
| Subscribe / সাবস্ক্রাইব | TELESCREEN AT HOME · ঘরে বসে টেলিস্ক্রিন |
| 404 page | ROOM 101 · রুম ১০১ |
| Backend down | THE TELESCREEN IS SLEEPING · টেলিস্ক্রিন ঘুমাচ্ছে |
| Privacy note | LITTLE BROTHER IS NOT WATCHING YOU · ছোট ভাই আপনাকে দেখছে না |

All the nicknames come from the site JSON, so they can be edited or blanked.

### 3A.7 Newspeak Dictionary (a useful glossary)
- `/truth/glossary` is a short bilingual glossary of the terms the page uses, such as CVE, CVSS, RCE, EOL, patch, mitigation, WAF, KEV, `.env`, API key and SSRF. Each entry has a one-line plain explanation in both languages.
- Terms in item summaries get a dotted underline and a tap-to-see popover (no hover-only behaviour, so it works on mobile).
- Entries live in JSON (`glossary.default.json` + admin override). The glossary is **plain and correct**; only its nickname is themed.

### 3A.8 Small easter eggs (optional, P4)
- Typing `2+2=5` into the version checker answers "এখানে না। ২ + ২ = ৪, আর আপনার ভার্সন … " ("Not here. 2 + 2 = 4, and your version …") followed by the real version result.
- On the Room 101 (404) page, the mascot stands next to a door. Tapping it shows a random meme and a "back to the telescreen" button.

### 3A.9 Privacy promise (real, not a joke)
The footer note "ছোট ভাই আপনাকে দেখছে না / Little Brother is not watching you" links to the privacy section, and must be true:
- no third-party trackers, analytics cookies or ad pixels on `/truth`;
- fonts are self-hosted (bundled), not loaded from Google Fonts at runtime, so no third-party font requests either.

If analytics are wanted later, they must be self-hosted and open source, collect no personal data, and be listed on the privacy page.

### 3A.10 Theme in share images and emails
- OG images (P4) use the poster frame: a cream or ink background, the headline in the display font, the status pill, the mascot pose, and the eye mark in a corner.
- Emails use a light version: the eye mark and a mascot PNG in the header, plain readable body and tables, and one meme line in the footer (none in urgent alerts).

## 4. Hosting and architecture (Vercel frontend, LAN backend)

- **A separate Vite entry**, `frontend/truth.html` → `src/truth/main.js`. It imports no admin, auth or session code, and a build check enforces that. It is deployed with the existing Vercel project.
- **Vercel rewrites** in `vercel.json`:
  - `/truth` and `/truth/:path*` serve `truth.html` (the client-side app shell);
  - `/truth-api/:path*` → `https://<tunnel-host>/public/truth/:path*`. The browser stays same-origin and never sees the tunnel host, so no CORS change is needed;
  - `/truth/s/:path*` → `https://<tunnel-host>/public/truth/share/:path*` (share pages, below).
- **The tunnel is Tailscale Funnel** (decided). It is free, the client is open source (BSD-3), and it gives a stable `https://<machine>.<tailnet>.ts.net` URL with no domain needed. It exposes only `/public/truth/*` from the LAN backend; admin `/api/*` is never reachable from the internet.
  - **Mount only the public path** (path-based serve), so the funnel forwards `/public/truth` to `http://localhost:5274/public/truth` and nothing else. Check the exact command against the installed Tailscale version's `tailscale funnel --help`, and record it in the docs.
  - **Defence in depth:** have the backend listen for funnel traffic on a **separate local port** (for example 5275) that serves only the `/public/truth/*` endpoints. Any other path on that port returns 404. The admin API stays on 5274, which is never funnelled.
  - The tailnet policy must allow Funnel for that one machine only, with HTTPS certificates enabled.
  - Put the `ts.net` host in `vercel.json` rewrites. It isn't a secret, but don't publish it anywhere else.
  - **The machine must stay on.** If it sleeps, the page falls back to the stored snapshot (below).
  - Document the setup and start-up (Tailscale runs as a service; funnel config persists across reboots) in `docs/truth-deploy.md`.
- **Snapshot on publish.** Publishing writes an immutable JSON snapshot (content + site JSON version + `serverNow`). It is served with `ETag` and `Cache-Control: public, max-age=300` (60 while anything is urgent).
- **When the backend is down.** The app shell keeps the last snapshot it received in `localStorage` (try/catch). If `/truth-api` fails, it shows that snapshot under a grey bar: "সর্বশেষ সংরক্ষিত তথ্য · <time> · সার্ভারে এখন পৌঁছানো যাচ্ছে না" ("last saved information · <time> · the server can't be reached right now"). With no stored copy, it shows a friendly error with the RSS link. Built-in site text (§5) keeps the shell readable in either case.
- **Share pages, OG tags and no-JS.** The share button links to `/truth/s/issue/:slug` or `/truth/s/guide/:slug`. The backend renders a small, plain, readable HTML page for these (title, summary, references, guide steps with default variables) with full OG tags, and a clear link to the interactive page. This gives Facebook, WhatsApp and Slack previews and a page that works without JS. The main shell has `<noscript>` pointing to the matching share page.
- **Rate limits** (built-in ASP.NET Core limiter): GETs 120/min per IP; subscribe 5/hour per IP and 3/day per email; confirm and unsubscribe 30/hour per IP. IPs come from the tunnel's forwarded header, trusted only from the tunnel.
- **Headers on Vercel** (`vercel.json`): a strict CSP for `/truth*`, `X-Content-Type-Options`, `Referrer-Policy` and HSTS. The backend share pages send the same headers.

## 5. Global text in JSON (Bangla + English)

All fixed text lives in **JSON**, not in components. That covers the site name, tagline, nav labels, status pill labels, banner text, countdown phrases, version-checker answers, guide headings ("সবার আগে ব্যাকআপ নিন", "আউটপুটে যা দেখবেন", "যা ভাঙতে পারে"…), the subscribe form, privacy note, email templates' fixed text, footer and the how-we-verify page.

- **Structure:**
  - `frontend/src/truth/content/site.default.json` holds both languages: `{ "bn": {...}, "en": {...} }`;
  - `site.schema.json` is a JSON Schema listing every key, its type and the placeholders it allows (`{time}`, `{version}`, `{count}`).
- **Two layers:**
  1. **Built-in default:** the default JSON is bundled with the app. It always works, even when the backend is down.
  2. **Global override:** an admin edits the site text in the Ministry of Truth tab → validated against the schema → published as a new `SiteContent` version → served at `/public/truth/site-content` and included in snapshots. The shell merges the override over the default at load, so a change is visible to everyone **without redeploying Vercel**.
- **The admin editor** shows the two languages side by side per key, with a search box, "missing in English" filters, schema validation (unknown keys, missing keys, wrong placeholders and HTML are refused), a preview at 360 px, version history and one-click rollback.
- **Safety:** JSON text is plain text with placeholders only. It is never rendered as HTML, and URLs in it must be `https`.
- **Consistency:** the same JSON feeds vue-i18n (frontend), the share pages and the emails (backend reads the published version), so one change updates all three.
- **CI check:** every key used in code exists in both `bn` and `en` of the default file, and the schema matches the default.

## 6. Fix guides (playbooks)

As in v2, with the platform list extended. Summary of the structure:
- the title, phase badge (temporary / after patch / hardening) and byline;
- why, and which versions are affected;
- the backup box;
- **platform and version selectors**;
- numbered steps with copy buttons;
- the **verify** step with expected output and a paste-to-compare box (browser only);
- the **no-terminal / panel** steps;
- **edge / WAF rules** with a "what this may break" box;
- **undo**;
- a link to the after-patch guide;
- the **References** list.

### 6.1 Guide page
The guide page is the full structure above, with `/truth/guide/:slug` for interactive use and `/truth/s/guide/:slug` for sharing.

### 6.2 Templated commands
- `Variables` defines the selectors, for example `{"php": {"options": ["8.2","8.3","8.4","8.5"], "default": "8.4"}, "platform": {...}}`.
- Placeholders such as `{{php}}`, with conditional lines like `{{#if php>=8.5}}…{{/if}}`, and a step `Condition` to hide steps that don't apply.
- Use a tiny logic-limited renderer written in the repo: no `eval`, plain-text output only.
- A **variant grid** in the admin shows every platform × version.

### 6.3 Workflow
**Simple workflow (owner's choice: no over-engineering):** draft → published, with no separate reviewer role. One person can write and publish. Safety comes from the tool, not extra people. Before a guide can be published, the publisher must:
1. open the **variant grid** at least once since the last edit (the app records this);
2. have **no block-level** safety findings, and acknowledge each warning with a short note;
3. tick a 4-item checklist: "commands tested on at least one real or test server", "verify step matches", "undo step works", "sources linked".

If a second account with publish rights exists later, nothing changes; they simply use the same checklist. Changes after publishing go through a correction.

### 6.4 Command safety check
This is unchanged from v2 and runs on **every rendered variant**:
- **block:** `curl|sh`, `chmod 777`, root-like `rm -rf`, disabled TLS verification, inline secrets;
- **warn:** `memory_limit = -1`, block-all-multipart WAF rules, `tee` overwrites, a restart with no verify step, unknown php.ini directives for the selected version.

Add one platform-specific rule: `docker.exec_ephemeral` (warn). Editing a file with `docker exec` inside a running container is lost when the container is recreated, so tell readers to change the image or a mounted config instead.

### 6.5 Platforms (configured in `platforms.json`, extendable without code changes)

Each platform has a key, a Bangla and English name, a group, a default shell or language for its code blocks, and a short "how to know you're on this" hint.

| Group | Platforms (keys) | Notes for guide writers |
|---|---|---|
| Linux servers | `debian-ubuntu`, `rhel` (RHEL / Rocky / AlmaLinux, including Remi PHP), `alpine` | Package manager, config paths and service names differ per platform and per PHP source. The reviewer checks paths for each one. |
| Containers | `docker` (official images), `docker-compose`, `kubernetes` (ConfigMap / Helm values) | Change the image or mounted config, then rebuild or roll out. Never only `docker exec` (rule above). |
| Windows | `windows-server` (IIS + PHP, Node as a service) | PowerShell code blocks, php.ini path, IIS or app-pool restart. |
| Developer machines | `macos-homebrew`, `nvm` (Node version manager) | For people testing locally. |
| Hosting panels | `cpanel-whm` (MultiPHP INI Editor), `plesk`, `cyberpanel`, `aapanel`, `shared-hosting` (`.user.ini` / host's panel) | No-terminal steps in plain words, with "ask your host" when the setting isn't editable. |
| Web servers | `nginx`, `apache` | Edge rules at the web server, such as limiting request bodies or blocking a path. |
| Edge / CDN | `cloudflare` (WAF custom rule expressions) | Always with "what this may break". |
| Runtimes' own update channels | `nodesource`, `official-installer` | For the after-patch "how to update" steps. |

The first guides cover **Debian/Ubuntu, RHEL, Alpine, Docker, Docker Compose, Kubernetes, Windows Server, cPanel, shared hosting, Nginx, Apache and Cloudflare**. The rest can be added as needed. A guide declares which platforms it supports, and the selector shows only those.

### 6.6 AI draft assist (P4)
Schema-forced JSON at temperature 0 from pasted public source text. It produces Bangla and English fields, steps, variables and expected output, then goes through the number check, the safety check and human review. The model never fetches URLs, and its output is written in our own words with the source added to References.

## 7. Subscription and free open-source mail

**Software (all open source):** MailKit + MimeKit for SMTP, our own subscriber tables and queue (§2), and email bodies built from the published site JSON plus the snapshot. No newsletter SaaS SDK.

**Mail settings file (owner's choice).** The sender details go in **`backend/config/truth-mail.json`**, which the owner fills in later:

```json
{ "fromAddress": "", "fromName": "সত্য মন্ত্রণালয় · Ministry of Truth", "replyTo": "",
  "smtpHost": "smtp.gmail.com", "smtpPort": 587, "useStartTls": true,
  "dailyCap": 400, "perMinute": 20 }
```

- Commit only `truth-mail.example.json`; add the real `truth-mail.json` to `.gitignore`.
- **The Gmail app password is never in this file.** It comes only from user-secrets or the environment variable `Truth__Mail__Password`.
- While `fromAddress` is empty, the app starts normally. The admin's Mail tab shows "মেইল সেটআপ বাকি / Mail not set up yet", subscribe requests are saved, and confirmation emails wait in the queue until the file is filled in. They are sent then, oldest first, as long as they are less than 48 hours old; older ones get a fresh token.

**Sending route:** the free part is the relay:
- **Start with a free Gmail account and an app password** (`smtp.gmail.com:587`, STARTTLS). It costs nothing, and the code stays standard SMTP. Gmail caps how many recipients one account can send to per day (roughly 500 for a normal account; confirm the current limit), so the queue enforces a configurable **daily cap** (default 400).
- **Later, fully self-hosted:** an open-source mail server such as **Postal** (MIT) or Postfix on a VPS with SPF, DKIM and DMARC. Only the configuration changes. Don't try to send directly from the office or LAN IP: home and office IPs are usually blocked or land in spam.
- **Development and evals:** `FileMailSender` writes `.eml` files to a gitignored folder.

**Queue behaviour:**
- One background worker, sending at most N per minute (configurable, default 20).
- Respects the daily cap and defers the rest to the next day (Bangladesh midnight), oldest first.
- Retries failures with backoff, 3 attempts.
- Confirmation emails always go first.
- Urgent alerts go before the weekly digest.
- The admin sees queued, sent, deferred and failed counts.

**Subscription flow** (unchanged): a form at the end of the page and on guide pages with email, topics (weekly; urgent alerts; tech filter), **language (bn / en, preselected from the current switch)** and a honeypot. Double opt-in with a hashed single-use token valid for 48 hours. The same response whether or not the email exists. One-click unsubscribe plus the `List-Unsubscribe` and `List-Unsubscribe-Post` headers.

**Emails:**
- Written in the subscriber's language, using fixed text from the site JSON.
- Neutral lettermark icons and simple table HTML that works in Gmail mobile, plus a text version.
- Every email ends with the References and the unsubscribe link.
- The urgent alert includes the patch time in both time zones and the first 3 steps of the before-patch guide.
- Limits (owner's choice): weekly digest plus urgent alerts, with **at most 13 urgent emails per week** (Monday 00:00 to Sunday 23:59 Bangladesh time; follow-ups count toward the 13). Within that, at most 1 urgent alert per item per 24 hours, plus 1 "patch is out" follow-up per item. When the cap is reached, further urgent news waits for the next weekly digest, and the admin sees "urgent limit reached (13/13)".

**Privacy:** store the email, topics, language and timestamps only, plus per-day IP hashes for rate limits. No subscriber emails in logs, public JSON, evals or reports.

**RSS** at `/truth-api/feed.xml?lang=bn|en` covers issues and guides.

## 8. Admin: Ministry of Truth tab (RabbitiChat, LAN)

- **RBAC keys (kept minimal):**
  - `tab:truth`: open the tab and edit drafts;
  - `action:truth.publish`: publish items and guides, send email, edit site text and memes.

  Both go to the admin preset by default. No separate reviewer or sender roles.
- **Sub-tabs:**
  - Issues & Items;
  - Fix Guides (variant grid, safety panel);
  - Site Text (§5 JSON editor);
  - Subscribers & Mail (counts, queue, test send; email addresses shown masked, like `ra***@gmail.com`);
  - Sources inbox (later).
- **Pre-publish checklist:**
  - sources present;
  - number check clean;
  - no block-level findings, and every warning acknowledged;
  - urgent items have a guide or a "what to do" note;
  - `patch_scheduled` items have a time;
  - guides that change servers have verify and undo steps;
  - Bangla fields filled in.
- **Reminders:** a patch time has passed with no status update; a before-patch guide is still live 72 hours after its item was patched; or the mail queue has deferred items.

## 9. Later (not in this build)
- Feed inbox (CISA KEV, Node.js and PHP releases, GitHub Security Advisories, endoflife.date) → drafts only.
- Web push.
- A static fallback copy of the latest snapshot published to Vercel on each publish, so the page stays up while the backend is off.
- An internal "our servers" page behind login, never public.

## 10. Phases and gates

Write the evals first in `scripts/truth-eval.mjs`, with the shared fake Ollama and `FileMailSender`; they start red, then go green. **Invented fixtures only:** "ExampleLang 3.1–3.4", "DemoHost", `CVE-0000-0001`, the directive `demo.upload_feature` and `example.com` emails.

### P1: Data, admin, public read page, Vercel and tunnel, site JSON
Gates:
- **G1.1** Publishing without a source is refused, and so is publishing with an empty Bangla field.
- **G1.2** Draft content is never served by `/public/truth/*`.
- **G1.3** The public JSON passes a schema allow-list (no emails, notes or acknowledgements).
- **G1.4** Status changes are logged and shown in Asia/Dhaka time.
- **G1.5** The `/truth` bundle has no admin or auth code, and gzipped JS is ≤ 90 KB.
- **G1.6** Through Vercel, `/truth` loads, `/truth-api/current` returns the snapshot, and **a request through the tunnel for `/api/models` or any non-`/public/truth` path is refused** (curl evidence).
- **G1.7** With the backend stopped, `/truth` shows the stored snapshot and the grey bar, or the friendly error on a first visit.
- **G1.8** Site text: editing a label in the admin and publishing changes it on the live page without a Vercel redeploy. A JSON edit with a missing key, wrong placeholder or HTML is refused. Rollback works.
- **G1.9** The language switch changes every visible string. `?lang=en` survives sharing. Missing English falls back to Bangla with the note. The CI key check passes.
- **G1.10** Screenshots at 360, 390, 768 and 1280 px, in both themes and both languages, show no horizontal page scroll.
- **G1.11** `license-check.mjs` passes, and `docs/THIRD_PARTY.md` lists every new package.
- **G1.12 Theme boundaries:** a DOM check confirms that no meme, nickname-only text, stamp-only status or effect class appears inside status pills, advisories, guide steps, verify blocks, warnings, References or the urgent banner.
- **G1.13 Serious mode:** with an `active_attack` fixture, the hero shows no glitch and no meme, and the mascot is "worried". The visitor switch and `prefers-reduced-motion` turn off all animation (Playwright checks computed `animation-name: none`).
- **G1.14 Flashing:** no element animates more than 3 times per second, and the glitch and crack animations run once only.
- **G1.15 Meme JSON:** the schema refuses memes over 90 characters, missing a language, containing HTML, or using an unknown placement. Rotation never shows the same meme twice in one view, and respects the mood.
- **G1.16 No third parties:** a Playwright network log of `/truth` shows only same-origin requests (no Google Fonts, analytics or CDNs).
- **G1.17 Asset weight:** the mascot SVG states total ≤ 10 KB, the crack overlay ≤ 8 KB, and display fonts are subset and loaded only where used. The Lighthouse targets still pass with the theme on.

### P2: Fix guides, platforms, safety check, countdown, version checker, share pages
Gates:
- **G2.1** The template renderer passes at least 15 cases, including an attempt to inject `<script>`.
- **G2.2** The variant grid covers every platform × version that a guide declares, and `platforms.json` adds a platform without code changes (eval adds a fake one).
- **G2.3** The safety check finds every rule, including `docker.exec_ephemeral`, in a planted fixture and nothing in a clean one, on every variant.
- **G2.4** A guide can't be published until the variant grid has been opened since the last edit, every warning is acknowledged and the 4 checklist items are ticked. Editing a step resets the "grid opened" flag.
- **G2.5** Paste-to-compare and the version checker make zero network requests (Playwright).
- **G2.6** The countdown uses `serverNow` and is correct with a visitor clock 2 hours wrong.
- **G2.7** `/truth/s/issue/:slug` and `/truth/s/guide/:slug` return readable HTML with OG tags in the requested language, with no JS needed.
- **G2.8** References are numbered, and every [n] in the summary points to an existing source.
- **G2.9** Corrections keep history, and RSS is valid in both languages.

### P3: Subscription and mail
Gates:
- **G3.1** Subscribe → confirm → confirmed; an expired or reused token is refused.
- **G3.2** The response is identical for new and existing emails.
- **G3.3** A filled honeypot is silently ignored.
- **G3.4** The rate limits trigger at the stated numbers.
- **G3.5** One-click unsubscribe works, and no mail goes out afterwards.
- **G3.6** The queue respects the per-minute rate and the daily cap (eval with cap = 5 and 12 recipients: 5 sent, 7 deferred to the next Bangladesh day), confirmations go first, and urgent goes before weekly. **The weekly urgent cap holds:** the 14th urgent alert in a Bangladesh week is refused with "13/13", and the count resets on Monday 00:00 Bangladesh time.
- **G3.6b** With `truth-mail.json` missing or `fromAddress` empty, the app starts, subscribe requests are saved, confirmation emails wait in the queue, and the admin shows "Mail not set up yet". After the file is filled in, the queued emails go out.
- **G3.7** Emails are in the subscriber's language, use the published site JSON, contain the References and unsubscribe link, and include the patch time in both time zones for urgent alerts.
- **G3.8** A real send works through the SMTP relay to the owner's own test address (screenshot of the received email on mobile).
- **G3.9** No SMTP secret is in tracked files, and no subscriber email appears in the logs.

### P4: Draft assist, OG images, polish
Gates:
- **G4.1** The draft assist returns valid bn and en JSON for 5 fixtures. The number check and safety check catch the planted errors.
- **G4.2** The source fetcher refuses `http://`, private or LAN IPs, `localhost` and files over 2 MB.
- **G4.3** OG images are generated for each issue and guide.
- **G4.4** Lighthouse meets the targets in both languages; attach the reports.

## 11. Do not
- Do not upgrade the framework. Stay on .NET 8.
- **Do not add any dependency that isn't open source** (§1.2), or any paid or closed mail SDK.
- Do not copy third-party articles. Summarise, link and reference only.
- Do not use vendor logos. Use neutral lettermarks only.
- Do not put real incidents, CVEs, emails or copied posts in fixtures, seeds, tests or commits.
- Do not publish anything automatically. A person always presses publish, after the guide checklist (§6.3).
- Do not add extra roles, approval chains or workflow states beyond §6.3 and §8. Keep it simple.
- Do not funnel port 5274 or any path other than `/public/truth/*`.
- Do not commit `truth-mail.json`, and never put the Gmail app password in it.
- Do not publish guides with block-level findings or hide acknowledged warnings from readers.
- Do not label a temporary mitigation as a fix.
- Do not expose admin `/api/*` through the tunnel or Vercel rewrites.
- Do not show the company's own servers, IPs, versions or patch state.
- Do not put SMTP or tunnel secrets in `appsettings*.json`, `vercel.json` or commits.
- Do not send mail directly from the office or LAN IP.
- Do not render site JSON or guide templates as HTML. No `eval`, `v-html` or `innerHTML`.
- Do not hardcode user-facing text in components. It goes in the site JSON.
- Do not send what users type or paste (versions, command output) to the server.
- Do not let the model fetch URLs.
- Do not reveal whether an email is subscribed, and do not log emails or raw IPs.
- Do not put jokes, memes, stamps-only meaning or effects on safety information (§3A.2).
- Do not use film stills, book covers, the 1984 film's imagery, the famous 1984 TV advert, or any existing Big Brother portrait. Every visual is an original drawing.
- Do not quote passages from the novel. Short words and phrases only.
- Do not make political jokes (parties, leaders, governments, elections, religion), and do not joke about breach victims.
- Do not use looping flicker or anything that flashes more than 3 times per second.
- Do not load third-party trackers, analytics, fonts or CDNs on `/truth`.
- Do not push the `context-engine` branch.

## 12. Report format (after each phase)
1. What was built (files, endpoints, entities, `vercel.json` changes).
2. Gate table: gate, pass or fail, evidence.
3. `node scripts/truth-eval.mjs` summary, all other suites still green, and the `license-check.mjs` output.
4. Screenshots at 360 px in both themes and both languages: the page, a guide, the site-text editor and the variant grid. Also include the page in serious mode during an `active_attack` fixture, the 404 (Room 101) page, the backend-down page, and every mascot pose.
5. Bundle size, the no-JS share-page checks, and the backend-down check.
6. New dependencies with their licences.
7. Deviations and why.
8. Open questions.

## 13. Decisions (closed)
1. **Tunnel:** Tailscale Funnel, public path only, on a separate local port (§4).
2. **Mail sender:** a Gmail address set in `backend/config/truth-mail.json`, which the owner fills in later; the app password goes only in user-secrets or the environment (§7). Use a separate Gmail account for this, not a personal one.
3. **People:** one person can write and publish. No reviewer role; the guide checklist and safety check do the job (§6.3, §8).
4. **Cadence:** weekly digest plus urgent alerts, **at most 13 urgent emails per week** (§7).
5. **Bangla name:** সত্য মন্ত্রণালয়.
6. **Theme:** decoration on by default; **serious mode switches on automatically during attacks**; visitors can also turn it on themselves (§3A.2).
7. **Mascot:** **Little Brother** (ছোট ভাই in Bangla text).

Claude Code: if anything is still unclear, choose the simpler option, record it under "Deviations" in the report, and keep going.
