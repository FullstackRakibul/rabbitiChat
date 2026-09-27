# BebshaMitra — Phase 0: Discovery

Read-only survey of Mostisko as it exists today, so every later phase reuses real
names instead of inventing parallel machinery.

**Status: BLOCKED on missing reference files.** See §A. The codebase survey below
(D1–D18) is complete and usable; gates G0.2 and G0.5 cannot be met until the
`docs/bebshamitra/` reference bundle is supplied.

---

## A. Blocker: the reference bundle is not in the repo

`docs/bebshamitra/` exists but is **empty** (0 files). Every file §2 of the prompt
names as a contract is absent:

```
$ find docs/ -type f | wc -l
0
```

| Expected file | Found |
|---|---|
| `lift-service-quotation.template.json` | NOT FOUND |
| `sky-view-sample.quotation.json` | NOT FOUND |
| `sample-profile.json` | NOT FOUND |
| `bm-core.js` | NOT FOUND |
| `bm-render.js`, `preview.shell.html`, `build-preview.py` | NOT FOUND |
| `fixtures/calc-vectors.json`, `run-vectors.mjs` | NOT FOUND |
| `eval/rbac-matrix.json` | NOT FOUND |
| `eval/rbac-eval.mjs`, `eval/adapter.mjs`, `eval/mock-server.mjs` | NOT FOUND |
| `sky-view-sample.pdf` | NOT FOUND |
| `BebshaMitra_Module_Details.html` | NOT FOUND |

`git log -- docs/` is empty, so the folder was never committed — the copy step
either did not happen or copied an empty directory.

**Why this blocks rather than slows.** These are not documentation; the prompt
defines them as executable contracts, and §13 forbids editing two of them to make
a gate pass. Specifically:

- **G0.2 cannot run.** It requires `eval/rbac-eval.mjs` and `eval/adapter.mjs`.
- **G0.5's checklist** is required to be *generated from D17*; I can and did
  produce that (§P), but the parity rows it must be checked against in Phase 5
  reference the details HTML that is missing.
- **Phase 1 is fully blocked**: G1.1/G1.2 are "36/36 vectors" against
  `calc-vectors.json`, and `bm-core.js` is the 1:1 port source.
- **Phase 2's non-waivable gate G2.1** is 224/224 against `rbac-matrix.json`.

Writing my own substitutes would defeat the purpose — a matrix I invent cannot
falsify an implementation I also wrote, and §13 explicitly forbids editing them
to pass. **These files must be supplied as-authored.**

---

## B. Discovery table

Every row is a real file path + symbol in this repo.

| # | Concern | Finding |
|---|---|---|
| **D1** | Users entity/table, Id type, username, active flag | `backend/Models/Entities.cs:3` `class User` — **`int Id`**, `Username` (unique), `SecretHash`/`SecretSalt` (PBKDF2), `Token`, `Role`, `CreatedAt`. **There is NO active/deactivated flag.** See §C-1. |
| **D2** | Login endpoint, body, token field | `backend/MinimalApis/OllamaApis.cs:45` `POST /api/auth/login`, body `{username, secret}` → `{username, token, created}`. **Auto-registers**: an unknown username creates the account. Each login rotates `User.Token`. |
| **D3** | How a request is authenticated; is active/role re-read per request? | `backend/AuthHelper/Authorization.cs:11` `ResolveUser(HttpContext, AppDbContext)` — reads the `Bearer` header and does `db.Users.FirstOrDefaultAsync(u => u.Token == token)`. **Role is re-read from the DB on every request** (fresh entity, no claims, no cache). There is no active flag to re-read. Called explicitly per endpoint, not as middleware. |
| **D4** | How endpoint code gets the current user | Same `Authorization.ResolveUser(ctx, db)`, called at the top of each handler; `null` → `Results.Unauthorized()`. |
| **D5** | Role model | `backend/Models/Rbac.cs:15` `static class UserRoles` — **strings** `General` / `System` / `SystemAdmin`, stored in `User.Role`. `Normalize()` fails closed to `General`. `IsSystemAdmin(User)` helper. |
| **D6** | Tab registry, `/tabs` endpoint, role defaults | `backend/Rbac/TabCatalog.cs` — `All[]` (canonical keys, ordered), `Labels`, `Aliases`, `GeneralTabs[]`, `SystemExtraTabs[]`, `DefaultTabsFor(role)`, `EffectiveTabs(role, grants)`. Caller-facing endpoint `GET /api/me/tabs` at `MinimalApis/RbacApis.cs:34`. |
| **D7** | Per-user additive grants: storage, check, caching | `backend/Models/Rbac.cs:46` `UserTabPermission(UserId, TabKey)`. Read by `Repositories/RbacRepository.cs:48` `GetGrantsAsync(userId)`. Enforced in `backend/Middleware/TabAccessMiddleware.cs` via a prefix→tab table. **NOT cached** — the middleware queries grants on every gated request, so a revoked grant denies the same token on the next call. |
| **D8** | Admin endpoints | `MinimalApis/RbacApis.cs` group `/api/admin/rbac`: `GET /tabs`, `GET /users`, `GET /users/{id}`, **`PUT /users/{id}/role`**, **`PUT /users/{id}/tabs`** (replaces grants wholesale). **NO create-user endpoint and NO deactivate endpoint.** See §C-1, §C-2. |
| **D9** | Last-admin guardrail (pattern for last-owner) | `backend/Services/RbacService.cs:171-178` in `SetRoleAsync`: demoting a `SystemAdmin` calls `_repo.CountSystemAdminsAsync(ct)`; if `<= 1` → `RbacOutcome.Conflict` → **409** at `RbacApis.cs` `Map()`. Also refuses self-edits (`caller.Id == userId` → Forbidden). **This is the exact shape to copy for `LAST_OWNER`.** |
| **D10** | `SchemaUpgrade` (idempotent DDL) | `backend/Data/SchemaUpgrade.cs` — `Run(AppDbContext)` calls one `EnsureXxxTables(db)` per feature (e.g. `EnsureRbacSchema`, `EnsureIeltsTables`, `EnsureObservabilityTables`). Helpers `TableExists`, `ColumnExists` make each step a no-op when already applied. Project uses `EnsureCreated()` + this, **not EF migrations**. Add `EnsureBebshaMitraTables(db)` here. |
| **D11** | Seeder pattern / SystemAdmin seed | `backend/Rbac/RbacSeeder.cs` — `AdminUsername = "mostisko@admin"`, `AdminSeedSecret` applied **only on creation**; an existing admin keeps its secret and is only promoted. Also `NormalizeExistingRoles(db)`. |
| **D12** | Minimal-API mounting pattern | `backend/Middleware/MiddlewareDependencyInversion.cs` — one `ApplicationXxxApis(this WebApplication)` per module doing `app.MapGroup("/api").WithTags(...)` then `.MapXxxApis()`. Called from `backend/Program.cs` (~line 260+). Endpoints are plain `app.MapGet/MapPost`; auth is per-handler (D4), tab gating is middleware (D7). |
| **D13** | Existing audit/log facility | **Yes, and it is new since the last module.** `backend/Observability/` — `AuditInterceptor.cs`, `ActivityLogQueryService.cs`, `LogQueue.cs`, `RequestLoggingMiddleware.cs`, `Redactor.cs`, `CorrelationMiddleware.cs`; model `backend/Models/ActivityLog.cs`; endpoints `backend/MinimalApis/LogEndpoints.cs`; frontend `components/tabs/LogsTab.vue`. **Currently uncommitted work-in-progress.** `Redactor.cs` is directly relevant to G2.7 (no secrets in logs). |
| **D14** | Frontend http/auth/router | `frontend/src/services/http.js` — base URL from `environments/environment.js`, `Authorization: Bearer` from `session.service.js:getToken()`, **401 → `AuthError`**, plus `http.stream()` for NDJSON. Session in `services/session.service.js` (localStorage). Router `src/app/router.js`: `MODULE_ROUTES[{key, path}]`, `meta.tab`, guard `router.beforeEach` using a resolver installed by `App.vue` via `useRoutePermissions`. Nav/tab list is **hardcoded** in `App.vue:393` `ALL_TABS[]`, then filtered by `/api/me/tabs`. |
| **D15** | Admin UI for grants: backend-driven or hardcoded? | **Backend-driven for the catalogue**: `components/tabs/RbacTab.vue:230` `tabCatalog.value = await listTabs()` (`GET /admin/rbac/tabs`). **But** `RbacTab.vue:105` keeps a *hardcoded mirror* of the role→default-tabs map purely to preview what a role would cover. Adding a tab to `TabCatalog` surfaces it in the admin checklist automatically; the preview mirror must be updated by hand or it silently drifts. |
| **D16** | Data volume path (Docker/Fly) | Fly: `fly.toml:36` `[[mounts]] destination = "/data"`, DB at `/data/rabbitic.db`. Compose: named volume, same rule. Existing file convention: `backend/MinimalApis/DocumentApis.cs:23` `UploadsDir = Path.Combine("data", "uploads")`. BebshaMitra assets → `{DataVolume}/bebshamitra/{businessId}/{sha256}.{ext}`. |
| **D17** | Sibling module map (5Lakhs) | See §D below. |
| **D18** | Reusable helpers in 5Lakhs | See §E below. |

---

## D. D17 — where 5Lakhs is wired in

5Lakhs is the closest sibling (printable document + amount in words). Its complete footprint:

| Place | File | Detail |
|---|---|---|
| Frontend feature folder | `frontend/src/components/fivelakhs/` | `CheckGenerator.vue`, `CheckTemplate.vue`, `Dashboard.vue` |
| Composable | `frontend/src/composables/useCheckHistory.js` | history/state |
| Tab key + label | `backend/Rbac/TabCatalog.cs:39,59` | key `"5lakhs"`, label `"5 Lakhs"` |
| Aliases | `backend/Rbac/TabCatalog.cs:84-85` | `"fivelakhs"`, `"5lakh"` → `5lakhs` (**the drift G0.4 warns about**) |
| Role defaults | `backend/Rbac/TabCatalog.cs:103` | in `GeneralTabs` — 5Lakhs **is** a General default |
| Router | `frontend/src/app/router.js:37` | `{ key: '5lakhs', path: '/5lakhs' }` |
| Nav/tab bar | `frontend/src/App.vue:401` | `ALL_TABS` entry, icon `BadgeDollarSign` |
| Tab content | `frontend/src/App.vue` | `<TabsContent value="5lakhs">` → `<CheckGenerator />` |
| Admin preview mirror | `frontend/src/components/tabs/RbacTab.vue:105` | hardcoded role→tabs preview |
| **Backend** | — | **None.** 5Lakhs is frontend-only: no feature folder, no endpoints, no tables, no seed. |

**Consequence for parity:** 5Lakhs is a *weaker* template than the prompt assumes.
It has no backend, so it demonstrates no endpoint-mount, seed, audit, Dashboard-card
or Docs-page pattern. The closest **backend** sibling is **IELTS**
(`backend/Ielts/`, `MinimalApis/IeltsApis.cs`, `Services/ReadingService.cs`,
`EnsureIeltsTables`, gated prefix `/api/ielts`) — I recommend mirroring IELTS for
P1/P2/P12 and 5Lakhs only for P4/P5/P6.

**Docs page — correction.** An earlier pass of this survey concluded there was no
docs page. That was wrong: `frontend/src/components/mostisko-docs/` exists
(committed in `77cd74f`) and has exactly the pattern P9 describes.
`sections-data.js:80-98` is the section registry (`{id, title, icon, group}`,
groups `architecture` / `modules` / `other`), and each module gets a full entry —
see `id: 'ielts'` at line 374 and `id: 'five-lakhs'` at 393. A BebshaMitra entry
is a new row in that registry plus a section object with
`title / summary / steps[] / stack[] / notes[]`, which maps onto the prompt's
Purpose / User guide / Technical. **P9 has a precedent; follow it.**

`/public/status` also exists (`MinimalApis/PublicApis.cs:17`), currently
`Results.Ok(new { server = "running" })` — P12 extends that object.

Still without precedent: **P8 (Dashboard card)** — `DashboardTab.vue` exists but
no module contributes a stat card to it, so its shape is a new decision.

---

## E. D18 — reusable helpers, and why the PDF path differs

| Helper | Where | Reuse? |
|---|---|---|
| Amount in words | `frontend/src/components/fivelakhs/` (inside the check components) | **Do not reuse.** It is cheque-oriented; BebshaMitra needs lakh/crore per `bm-core.js`, which is the 1:1 port source and must be authoritative in **both** C# and JS. |
| Currency formatting | same | Same reasoning — BD grouping must come from `bm-core.js`. |
| `pdfGenerator.js` / html2canvas + jsPDF | 5Lakhs check export | **Explicitly not reused**, per §9. That path **rasterises** the DOM: the output is an image in a PDF wrapper — no selectable text, no `pdftotext` (which G3.2 requires), blurry at print DPI, and no `@page` margin boxes, so "Page N of M" in a footer margin box is impossible. BebshaMitra uses real print CSS (`browser`) or Chromium `page.PdfAsync` (`chromium`). |
| `http.js`, `session.service.js` | `frontend/src/services/` | **Reuse as-is** (rule 3). |
| `SchemaUpgrade`, `Authorization`, `TabCatalog`, `RbacService` | backend | **Reuse as-is.** |

---

## F. Gate results

| Gate | Result | Evidence |
|---|---|---|
| **G0.1** Every row has path + symbol | **PASS** | §B table; unknowns marked and carried into §C with proposals |
| **G0.2** `rbac-eval.mjs` reaches "owner creates business → 404" | **FAIL — cannot run** | `eval/rbac-eval.mjs` and `eval/adapter.mjs` do not exist (§A). **The flow it needs is verified by hand** (§H): throw-away user via auto-register → admin grant → `GET /api/bebshamitra/businesses` → **404** (route absent), which is the state G0.2 describes |
| **G0.3** Risks from D3/D7 caching | **PASS** | Grants **not** cached (D7) and role re-read per request (D3) → S01 should pass as-is. **S02 cannot pass at all**: there is no active flag (§C-1) |
| **G0.4** `bebshamitra` free + registration points listed | **PASS** | `grep -rn "bebshamitra" backend/ frontend/src/` → no matches. Points listed in §G |
| **G0.5** Parity checklist generated from D17 | **PARTIAL** | Checklist produced (§P), but D17 shows 5Lakhs has no backend/Docs/Dashboard precedent (§D), and the visual target HTML is missing (§A) |

---

## H. Live verification of the adapter's flow (stand-in for G0.2)

`rbac-eval.mjs` is missing, so I exercised the same sequence directly against the
running app. This is what `adapter.mjs` will need to do.

| Step | Call | Result |
|---|---|---|
| Create throw-away user | `POST /api/auth/login {username, secret}` (new name) | **200**, `created: true`, token issued — no admin create-user endpoint needed (§C-2) |
| Admin login | `POST /api/auth/login` (`mostisko@admin`) | **200** |
| Find user id | `GET /api/admin/rbac/users?search=<name>` | `items[].id` |
| Grant tab | `PUT /api/admin/rbac/users/{id}/tabs` `{"tabKeys":["ielts"]}` | **200**, `grantedTabs:['ielts']` |
| Module call, granted | `GET /api/ielts/mistakes` | **200** |
| Revoke, same token | `PUT …/tabs {"tabKeys":[]}` then re-call | **403** — no caching, **S01 will pass** |
| BebshaMitra route | `GET /api/bebshamitra/businesses` | **404** (not yet built) — the expected Phase 0 end state |

---

## C. Conflicts between the prompt and this codebase

### C-1. There is no "deactivated user" concept — S02 cannot pass
`User` (D1) has no `IsActive`/`IsDisabled`/`DeletedAt`. `grep -rniE "isactive|deactivat|suspended" Models/ Data/ Rbac/` finds hits only on `BuyerProfile`, `ContextSource`, `Note`, `SearchHistory` — never on `User`. D8 confirms no deactivate endpoint.

The prompt requires this in three places: §4.1 "A deactivated user gets 401 (S02)", D1, and D3.

**Proposal (needs your decision):** add `User.IsActive bool NOT NULL DEFAULT 1` via a new `EnsureUserActiveColumn` step in `SchemaUpgrade` (D10), have `Authorization.ResolveUser` return `null` when `!IsActive` (one-line change, fixes it for **every** module at once), and add `PUT /api/admin/rbac/users/{id}/active`. This touches shared auth, which rule 3 says to reuse rather than replace — I will not change it without approval.

### C-2. The eval needs admin user-creation that does not exist
`rbac-eval.mjs` "creates throw-away users" through the admin API (§2, rule 5), but D8 shows no create-user endpoint. However, **`POST /api/auth/login` auto-registers** (D2), so throw-away users can be created by logging in with a fresh username + secret — no new endpoint needed. `adapter.mjs` should use that. Flagging it because it is a real difference from what the prompt assumes, and because auto-registration is itself worth knowing about.

### C-3. `PUT /users/{id}/tabs` replaces, it does not add — and the field is `tabKeys`
`RbacService.SetTabsAsync` calls `ReplaceGrantsAsync`: the body carries the **full** grant set and overwrites whatever was there. The adapter must read current grants (`GET /admin/rbac/users/{id}` → `grantedTabs`) and send the **union**, or it will silently revoke unrelated tabs.

**Trap, verified the hard way:** the DTO is `record SetTabsInput(List<string>? TabKeys)` (`DTOs/RbacDtos.cs:49`), so the JSON field is **`tabKeys`**. Sending `{"tabs":[…]}` binds `TabKeys` to null, is treated as "revoke everything", and still answers **200** — a silent no-op that looks like a successful grant. My first probe hit exactly this and produced a misleading 403.

Also note `SetTabsAsync` **drops keys the role baseline already covers**, so granting `ielts` to a System user stores nothing and `grantedTabs` stays `[]` — correct behaviour, but the adapter must assert on `effectiveTabs`, not `grantedTabs`.

Verified live:
```
PUT tabKeys=[ielts,search] -> 200, grantedTabs=['ielts','search']
PUT tabKeys=[ielts]        -> 200, grantedTabs=['ielts']      # 'search' revoked
```

### C-4. Tab-gating is a prefix table, not a per-endpoint attribute
Per D7, `TabAccessMiddleware` maps path prefixes to tabs. Adding `("/api/bebshamitra", "bebshamitra")` there gates the whole surface in one line — but note it returns **403** for a missing tab, which matches §4.4's second rule. The §4.4 ordering (401 → 403 tab → 404 membership → 403 capability) therefore lands in two layers: middleware does 401/403-tab, and the `RequireBusinessCapability` filter does 404/403/409/422. Worth stating explicitly so the order is not accidentally inverted.

### C-5. 5Lakhs is frontend-only — mirror IELTS for the backend
5Lakhs has **no backend at all** (§D): no feature folder, endpoints, tables or seed. It cannot model P1/P2/P12 or the seed/audit rows. The real backend sibling is **IELTS**. Recommend: mirror **IELTS** for backend shape, **5Lakhs** for frontend placement (P4/P5/P6).

Of the parity rows, only **P8 (Dashboard card)** is genuinely without precedent — `DashboardTab.vue` exists but no module feeds it a card, so its shape is a new decision. P9 (docs), P11 (shortcuts) and P12 (status) all have concrete patterns to follow, listed in §P.

---

## G. Where the tab key must be registered (G0.4)

| # | File | Change |
|---|---|---|
| 1 | `backend/Rbac/TabCatalog.cs` `All[]` | add `"bebshamitra"` |
| 2 | `backend/Rbac/TabCatalog.cs` `Labels` | `["bebshamitra"] = "BebshaMitra"` |
| 3 | `backend/Rbac/TabCatalog.cs` `SystemExtraTabs[]` | add (System + SystemAdmin default, **not** General — §4.1) |
| 4 | `backend/Rbac/TabCatalog.cs` `Aliases` | optional `"bebsha"`/`"bm"` — avoids the `fivelakhs`/`5lakhs` drift |
| 5 | `backend/Middleware/TabAccessMiddleware.cs` `GatedPrefixes` | `("/api/bebshamitra", "bebshamitra")` |
| 6 | `backend/Middleware/MiddlewareDependencyInversion.cs` | `ApplicationBebshaMitraApis()` |
| 7 | `backend/Program.cs` | call it beside the other `Application*Apis()` |
| 8 | `frontend/src/app/router.js` `MODULE_ROUTES` | `{ key: 'bebshamitra', path: '/bebshamitra' }` |
| 9 | `frontend/src/App.vue` `ALL_TABS` | entry + icon |
| 10 | `frontend/src/App.vue` | `<TabsContent value="bebshamitra">` |
| 11 | `frontend/src/components/tabs/RbacTab.vue:105` | hardcoded role-preview mirror (**silent drift if missed**) |

---

## P. Module parity checklist (G0.5)

| # | Place | Precedent | BebshaMitra file | Status |
|---|---|---|---|---|
| P1 | Backend feature folder | **IELTS** (`backend/Ielts/`) — 5Lakhs has none | `backend/Features/BebshaMitra/{Domain,Calculation,Access,Persistence,Endpoints,Seed,Pdf}` | to create |
| P2 | Endpoint mount | `MiddlewareDependencyInversion.cs` + `Program.cs` | `ApplicationBebshaMitraApis()` | to create |
| P3 | Tab registry + defaults | `TabCatalog.cs` | rows 1–4 in §G | to edit |
| P4 | Frontend feature folder | `components/fivelakhs/` | `frontend/src/features/bebshamitra/` | to create |
| P5 | Router + guard | `app/router.js` | `/bebshamitra/*`, `meta.tab` | to edit |
| P6 | Nav/tab bar | `App.vue` `ALL_TABS` | entry + `TabsContent` | to edit |
| P7 | Admin grant UI | `RbacTab.vue` | automatic via `listTabs()`; **preview mirror line 105 is manual** | to edit |
| P8 | Dashboard card | **no precedent** — `DashboardTab.vue` takes no module cards | `GET /api/bebshamitra/dashboard` + card | needs decision |
| P9 | Docs page entry | **`mostisko-docs/sections-data.js`** — registry line 80-98, module entry e.g. `id:'ielts'` L374, `id:'five-lakhs'` L393 | new registry row + section object (`summary`/`steps[]`/`stack[]`/`notes[]`) | to edit |
| P10 | Settings section | `components/tabs/SettingsTab.vue` | BebshaMitra section | to create |
| P11 | Keyboard help | `composables/useShortcuts.js` (Cmd/Ctrl+K, +B, +Enter today) | Ctrl+S/P/Enter, editor-scoped | to edit |
| P12 | Health/status | `MinimalApis/PublicApis.cs:17` `GET /public/status` → `{server:"running"}` | add `bebshamitra:{enabled,templates,pdfEngine}` | to edit |
| P13 | Not a General default | `TabCatalog.GeneralTabs` | omit from `GeneralTabs` | to edit |
| — | SchemaUpgrade step | `EnsureIeltsTables` | `EnsureBebshaMitraTables` | to create |
| — | Seeder | `RbacSeeder.cs` | `BebshaMitraSeeder` (upsert by key+version) | to create |
| — | Audit | `backend/Observability/` (D13) | reuse, or `BmAuditEvents` per §4.5 | needs decision |

---

## Noticed, not changed

- `backend/Observability/`, `Models/ActivityLog.cs`, `MinimalApis/LogEndpoints.cs`, `frontend/.../LogsTab.vue` are **uncommitted work in progress** on this branch. D13/§4.5 overlap with it — worth deciding whether BebshaMitra audits through it or keeps its own `BmAuditEvents`.
- `POST /api/auth/login` **auto-registers unknown usernames** (D2). That is how the eval can create throw-away users (§C-2), but it is also a notable property of the platform in its own right.
- `RbacTab.vue:105` duplicates the backend role→tabs map with no test binding them. Adding a tab and forgetting this line makes the admin preview quietly wrong.
- `Authorization.ResolveUser` does a `Users` table scan on `Token` per gated request; there is no index on `Token` (`AppDbContext.cs`). Fine at current scale, worth an index later.
