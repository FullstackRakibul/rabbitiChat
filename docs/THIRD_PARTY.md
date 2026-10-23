# Third-party components

The owner's standing rule (docs/TRUTH_PROMPT.md §1.2): every dependency is open
source under an OSI-approved licence (OFL for fonts). Free external services are
allowed only when they speak a standard protocol and can be swapped through
configuration.

`node scripts/license-check.mjs` lists every npm and NuGet package with its licence
and fails on anything not on its allow-list. The Ministry of Truth eval runs it (G1.11).

Each build that adds a dependency adds it here: name, exact version, licence and purpose.

## Ministry of Truth, Phase P1 (2026-10-23)

### npm (frontend)

| Package | Version | Licence | Purpose |
|---|---|---|---|
| @fontsource/noto-sans-bengali | 5.3.0 | OFL-1.1 (font), MIT (package) | Self-hosted Bangla and Latin text face, 400 and 600, split by script with `unicode-range`, `font-display: swap`. |
| @fontsource/jetbrains-mono | 5.3.0 | OFL-1.1 (font), MIT (package) | Self-hosted monospace face for the weekly log and CVE IDs. |
| @fontsource/anton | 5.3.0 | OFL-1.1 (font), MIT (package) | English display face of the telescreen theme (§3A.3): hero headline and banners only; one file per script by `unicode-range`. |
| @fontsource/baloo-da-2 | 5.3.0 | OFL-1.1 (font), MIT (package) | Bangla display face (heavy, friendly): hero headline and banners only; weight 800, one file per script by `unicode-range`. |
| @fontsource/special-elite | 5.3.0 | Apache-2.0 (font), MIT (package) | Typewriter face for "Records Department" nicknames and rubber stamps; loaded after the page (`.fonts-late`), one file per script by `unicode-range`. |

Each version was at least two weeks old when added (every font: 2026-07-19).

vue-i18n 11.4.13 (MIT) was added in the first P1 build and removed again in the v4 rework: the page needs a locale and a lookup only, now `src/truth/lib/i18n.js`, which drops 5 packages and about 70 KB of rendered code from the public bundle.

### Tools (not installed in the project)

| Tool | Version | Licence | Purpose |
|---|---|---|---|
| lighthouse | 13.5.0 (2026-09-18) | Apache-2.0 | Run by `scripts/truth-eval.mjs` (G1.17) through `npx`, against the local stand-in for Vercel. Not in any `package.json`. |

### NuGet (backend)

None. Rate limiting (`Microsoft.AspNetCore.RateLimiting`) and the `/truth-api`
rewrite (`Microsoft.AspNetCore.Rewrite`) are part of the ASP.NET Core 8 shared
framework (MIT).

### Icons

Lucide (`@lucide/vue`, ISC), already in the repository, for the theme toggle. Technology
icons are our own lettermarks (inline SVG), never vendor logos.

### Services (not dependencies; swappable through configuration)

| Service | Client | Licence | Used for |
|---|---|---|---|
| Tailscale Funnel (default) or Cloudflare Tunnel | `tailscale` / `cloudflared` | BSD-3-Clause / Apache-2.0 | Exposing `/public/truth/*` from the LAN backend to Vercel (docs/truth-deploy.md). |
| Vercel | — | — | Hosting the frontend (already in use). |

## Named exception (confirmed by the owner, 2026-10-23)

| Package | Version | Licence | Why |
|---|---|---|---|
| SQLitePCLRaw.lib.e_sqlite3 | 3.53.3 | public domain (SQLite) | The native SQLite build the whole app has always used; not new in this build. Public domain is not an OSI licence because it is no licence at all; the owner confirmed it is fine. It is the only exception: `scripts/license-check.mjs` accepts it by name, and only while the package's licence file says "SQLite is Public Domain". |
