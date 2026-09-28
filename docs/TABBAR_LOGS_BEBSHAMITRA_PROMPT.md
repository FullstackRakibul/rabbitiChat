# Mostisko — Tab bar redesign · Logs wiring · BebshaMitra tab

### Evaluation-based build prompt for Claude Code

> **How to start.** Tell Claude Code:
>
> *"Follow `docs/TABBAR_LOGS_BEBSHAMITRA_PROMPT.md`. Do **Phase 1** only, then stop and give me the Phase 1 report."*
>
> After each report: *"Continue with Phase 2."* Never let it run several phases in one go.

---

## 0. Rules of engagement

1. **One phase at a time.** Each phase ends with the report in §7. Stop and wait.
2. **A gate is PASS only with evidence** — a command and its output, a file path and line, or a screenshot. "Should work" is not evidence. Fix failures inside the same phase; after three failed attempts on one gate, stop and report the blocker.
3. **Reuse what exists. Never re-create it.** `ModuleTabBar.vue`, `AppShell.vue`, `http.js`, `session.service.js`, `TabCatalog.cs`, `TabAccessMiddleware.cs`, `useShortcuts.js` and the reka-ui `Tabs` primitives already exist. A second tab bar, a second HTTP client or a second permission list is an automatic FAIL.
4. **No new packages.** Everything here is achievable with the installed set (`reka-ui`, `@lucide/vue`, `tailwindcss` v4, `vue-router`, `@vueuse/core`). If you believe otherwise, stop and ask.
5. **Do not touch unrelated modules.** List anything you notice under "Noticed, not changed".
6. **Accessibility is a gate, not a nicety.** The current bar has documented keyboard, focus and reduced-motion behaviour (`ModuleTabBar.vue:1-24`). A redesign that loses any of it is a FAIL.

---

## 1. What we are building

Three separate pieces of work, in dependency order:

| Phase | Work | Why it is first/last |
|---|---|---|
| **1** | Fix the **Logs** tab wiring so it is reachable | It is already written and imported but **unreachable** — a live bug, and the smallest, most verifiable change |
| **2** | Redesign the **module tab bar** (off-white rail, rounded active pill, B&W theme) | Visual change to a shared component; safer once routing is known-good |
| **3** | Add the **BebshaMitra** tab (frontend shell only) | New module surface; uses the corrected wiring from Phase 1 and the new bar from Phase 2 |

---

## 2. Established facts (verified in this repo — do not re-derive, but do re-confirm)

| # | Fact | Evidence |
|---|---|---|
| F1 | `LogsTab.vue` exists (507 lines) and `logs.service.js` exists (167 lines), exported from the barrel | `frontend/src/components/tabs/LogsTab.vue`, `frontend/src/services/logs.service.js`, `services/index.js:43` |
| F2 | `App.vue` imports `LogsTab`, lists `{ id: 'logs' }` in `ALL_TABS`, and renders `<TabsContent value="logs">` | `App.vue:43`, `App.vue:415`, `App.vue:948` |
| F3 | The backend already ships the tab key `"logs"` | `backend/Rbac/TabCatalog.cs` `All[]` |
| F4 | **`logs` is MISSING from `MODULE_ROUTES`** — so `pathForTab('logs')` returns `/dashboard` and the tab can never be opened | `frontend/src/app/router.js` `MODULE_ROUTES` |
| F5 | `bebshamitra` appears nowhere in backend or frontend | `grep -rn "bebshamitra" backend/ frontend/src/` → no matches |
| F6 | The theme is already **pure greyscale**: every token is `0 0%` saturation in both modes | `frontend/src/assets/index.css:13-54` |
| F7 | The tab bar is driven by reka-ui `TabsList`/`TabsTrigger`; the active pill is one absolutely-positioned span animated with `translate3d` + `scaleX`, deliberately to stay on the compositor | `ModuleTabBar.vue:143-149, 174-179` |
| F8 | The bar honours `prefers-reduced-motion`, scroll-snaps horizontally, shows edge fades, and re-measures via `ResizeObserver` | `ModuleTabBar.vue:47-51, 103-136, 196-214` |

**F4 is the actual bug behind "need up and running Logs frontend component".** The component is written; it is the route that is missing. Confirm this before changing anything — if `logs` is present in `MODULE_ROUTES` by the time you read this, report that and re-scope Phase 1.

Verified by set comparison at the time of writing:

```
ALL_TABS  : dashboard chat workspace docs todo search jsonto 5lakhs ielts
            component-splitter sources settings rbac notes logs
ROUTES    : dashboard chat workspace docs todo search jsonto 5lakhs ielts
            component-splitter sources settings rbac notes          ← no 'logs'
BACKEND   : dashboard chat workspace docs todo search jsonto 5lakhs ielts
            component-splitter sources settings notes logs rbac

in ALL_TABS but NOT routable : ['logs']      ← the bug
in routes but not in ALL_TABS: []
in backend but not in ALL_TABS: []
```

So `logs` is the **only** genuine drift, and the backend and `App.vue` already agree about it. Phase 1 is a one-line fix plus proof that nothing else has drifted.

---

## 3. Phase 1 — Make the Logs tab reachable

**Goal:** a user with the `logs` tab can navigate to it, deep-link it, refresh on it, and reach it from the command palette. No redesign in this phase.

**Do**
- Add `{ key: 'logs', path: '/logs' }` to `MODULE_ROUTES` in `frontend/src/app/router.js`, positioned to match its order in `App.vue`'s `ALL_TABS`.
- Verify nothing else is missing: **every** `ALL_TABS` id must have a `MODULE_ROUTES` entry, and every `MODULE_ROUTES` key must exist in the backend's `TabCatalog.All`. Report any other drift you find rather than silently fixing unrelated keys.
- Confirm `LogsTab.vue` actually renders against the live API (it calls `logs.service.js`; check it handles an empty list, a 403 and a network failure without throwing).

**Gates**

| Gate | Check |
|---|---|
| G1.1 | `grep -n "logs" frontend/src/app/router.js` shows the route. Navigating to `/logs` renders `LogsTab`, and a hard refresh on `/logs` stays there (SPA fallback) |
| G1.2 | **Drift check**: a script proving `ALL_TABS` ids ⊆ `MODULE_ROUTES` keys ⊆ `TabCatalog.All`. Paste the output. Any mismatch is named. ⚠ Scope the `ALL_TABS` match to the array itself — a naïve `id: '…'` regex over the whole of `App.vue` also catches the `action:new-chat`-style command-palette rows and reports them as phantom drift |
| G1.3 | The command palette (`Cmd/Ctrl+K`) lists **Logs** for a permitted user and opens it |
| G1.4 | RBAC still holds: a user **without** the `logs` grant does not see the tab, and a direct `GET /api/logs…` (whatever `logs.service.js` calls) returns **403**. Show both |
| G1.5 | `npm run build` succeeds with no new warnings |
| G1.6 | `LogsTab` renders without console errors on: empty data, a 403, and a failed request. Screenshot or console output for each |

---

## 4. Phase 2 — Tab bar redesign

**Goal:** the header tab row becomes an **off-white rail** containing a **rounded active pill** in the theme's foreground colour, matching the existing black-and-white system.

### 4.1 Design intent

Per F6 the palette is already greyscale, so "theme color" here means **the existing `--primary` / `--foreground` token, not a new hue**. Do not introduce a colour.

```
┌──────────────────────────────────────────────────────────┐
│  ▟▀▀▀▀▀▀▀▙                                               │   ← rail: off-white (muted),
│  ▐ ● Chat ▌  Workspace   Documents   Logs   Settings      │     rounded, inset padding
│  ▜▄▄▄▄▄▄▄▛                                               │   ← pill: rounded, filled,
└──────────────────────────────────────────────────────────┘     inverted text
```

- **Rail**: the strip behind all tabs — off-white in light mode, the dark-mode equivalent in dark. Use `--muted` / `--secondary`; do not hardcode a hex.
- **Pill**: the active tab, fully rounded, filled with the foreground colour, label inverted (`--primary` / `--primary-foreground`). It keeps the existing slide animation.
- **Inactive tabs**: muted foreground, no fill; hover raises contrast only.
- **Both themes** must be legible and pass contrast (§4.3).

> **⚠ The reference image did not arrive in the conversation.** Build to this written spec, and flag in the report that the visual target was unavailable. If the image is supplied later, treat it as authoritative over this ASCII sketch.

### 4.2 Constraints (regressions that make this a FAIL)

Everything in F7/F8 must survive:
- The pill stays **one compositor-animated element** (`transform` only). Animating `left`/`width` is a FAIL.
- `prefers-reduced-motion` still drops the transition.
- Horizontal scroll + snap, edge fades, and `ResizeObserver` re-measurement still work.
- reka-ui keyboard behaviour (arrows, Home/End, roving tabindex, `role="tab"`, `aria-selected`) is untouched.
- **Labels remain visible** — icons must not become the sole affordance at any width (`ModuleTabBar.vue:20-23` explains why).
- Visible focus rings remain, and remain visible **against the new pill**.

### 4.3 Gates

| Gate | Check |
|---|---|
| G2.1 | Side-by-side screenshots, light **and** dark: rail is off-white/dark, active tab is a rounded filled pill with inverted text |
| G2.2 | No hardcoded colours: `grep -nE "#[0-9a-fA-F]{3,6}\|rgb\(" ModuleTabBar.vue` → no matches. Every colour is a token |
| G2.3 | Animation is still compositor-only: the animated style is `transform`/`opacity` exclusively (show the computed style or the code) |
| G2.4 | With `prefers-reduced-motion: reduce` forced in devtools, switching tabs shows no slide |
| G2.5 | At 320px the row scrolls, snaps, shows edge fades, and labels are still readable. Screenshot |
| G2.6 | Keyboard: Tab into the bar, then ←/→/Home/End move selection; the focus ring is clearly visible on **both** the active pill and an inactive tab. Screenshot of each |
| G2.7 | Contrast: active pill text ≥ **4.5:1** against the pill, and inactive label ≥ 4.5:1 against the rail, in both themes. Give the measured ratios |
| G2.8 | `ResizeObserver` still corrects the pill: collapse the sidebar (`Cmd/Ctrl+B`) and confirm the pill stays aligned |
| G2.9 | `npm run build` clean |

---

## 5. Phase 3 — BebshaMitra tab (frontend shell)

**Goal:** a registered, permission-gated, reachable BebshaMitra tab rendering a placeholder shell. **No business logic** — that belongs to the separate BebshaMitra build (`docs/bebshamitra/`), which is currently blocked on its reference bundle.

**Scope: frontend + the one backend line that registers the tab key.** Do not create tables, endpoints or services.

**Do**
- Backend: add `"bebshamitra"` to `TabCatalog.All`, a label `"BebshaMitra"` in `Labels`, and add it to `SystemExtraTabs` (System + SystemAdmin default — **not** a General default).
- Frontend: `{ key: 'bebshamitra', path: '/bebshamitra' }` in `MODULE_ROUTES`; an `ALL_TABS` entry with a suitable existing `@lucide/vue` icon (document-with-currency if one exists, else the nearest — state which and why); a `<TabsContent value="bebshamitra">`; and `components/tabs/BebshaMitraTab.vue` rendering a placeholder that names the module and says it is not yet built.
- **`frontend/src/components/tabs/RbacTab.vue`** keeps a hand-written mirror of the role→default-tabs map used only to preview what a role covers. Update it, or state explicitly that it does not need updating and why.

**Gates**

| Gate | Check |
|---|---|
| G3.1 | A SystemAdmin sees the tab; `/bebshamitra` renders the placeholder and survives a refresh |
| G3.2 | A fresh **General** user does **not** see it, and `pathForTab`/the guard redirects them away from `/bebshamitra` |
| G3.3 | Granting `bebshamitra` to that General user via the existing admin UI makes it appear **without a redeploy**. Show before/after |
| G3.4 | The tab key is identical in all five places — `TabCatalog.All`, `Labels`, `SystemExtraTabs`, `MODULE_ROUTES`, `ALL_TABS`. Paste a grep proving it. **No `fivelakhs`/`5lakhs`-style drift** |
| G3.5 | The Phase-1 drift check (G1.2) still passes with the new key included |
| G3.6 | The new tab looks correct in the Phase-2 bar, both themes, including as the active pill. Screenshot |
| G3.7 | `npm run build` and `dotnet build` both clean |

---

## 6. Do not

- Do not add a colour to a greyscale theme (F6). "Theme color" = the existing foreground/primary token.
- Do not hardcode hex/rgb values in the tab bar.
- Do not animate layout properties (`left`, `width`, `margin`) on the pill.
- Do not reduce the bar to icons-only at any breakpoint.
- Do not build a second tab bar component — modify `ModuleTabBar.vue`.
- Do not implement BebshaMitra business logic in Phase 3.
- Do not rely on the frontend to enforce permissions; the server is the authority.
- Do not "fix" unrelated drift you discover — report it.

---

## 7. Report format (end of every phase)

```
## Phase N report — <title>
Summary: <2–3 lines>

| Gate | Check | Result | Evidence |
|------|-------|--------|----------|
| GN.1 | …     | PASS   | `command` → output / path:line / screenshot |

Files changed: <list with paths>
Decisions made (and why): <list>
Noticed, not changed: <list>
Needs KAKAROT: <questions, or "none">
```

A phase whose table contains any FAIL is not complete. Say so plainly.

---

## 8. Open questions for KAKAROT

1. **The reference image never arrived** in the conversation, so §4.1 is a written reconstruction of "off-white bg, theme color active tab, round, black and white". If you have the mockup, send it — it overrides §4.1.
2. **Pill fill**: solid foreground with inverted text (as specced), or a subtler raised "card" pill on the off-white rail? The sketch assumes the former.
3. **Rail width**: should the rail wrap only the tabs, or span the full header row between the brand and the right-hand controls?
4. **BebshaMitra icon**: `@lucide/vue` has no document-with-৳ glyph. Preference — `FileText`, `ReceiptText`, `Landmark`, or something else?
