# Mostisko — Model policy per role (General users on `qwen3.5:0.8b`)

### Evaluation-based build prompt for Claude Code

> **How to start.** Tell Claude Code:
>
> *"Follow `docs/MODEL_POLICY_PROMPT.md`. Do **Phase 1** only, then stop and give me the Phase 1 report."*
>
> After each report: *"Continue with Phase 2."* Never let it run several phases in one go.

---

## 0. Rules of engagement

1. **One phase at a time.** Each phase ends with the report in §8. Stop and wait.
2. **A gate is PASS only with evidence**: eval output, a command and its output, a file path and line, or a screenshot. "Should work" is not evidence. After three failed attempts on one gate, stop and report the blocker.
3. **Evals first.** Write each phase's checks in `scripts/model-policy-eval.mjs` before the code. Run them once while they fail and paste that line. A check asserts what the fake Ollama received and what the client got, never what the server says it did.
4. **The server is the control.** Hiding a model in the picker is convenience. Every endpoint that runs a model must enforce the policy itself.
5. **One rule, one place.** Write the policy once and call it from every endpoint. A copy of the rule in an endpoint, or a second role list in the frontend, is an automatic FAIL.
6. **No new packages.** Touch only the files in §3.4. List anything else you notice under "Noticed, not changed".
7. **§2 was verified at commit `cc95a82` of `origin/main`. Your working copy is ahead of it.** Re-confirm every fact you use and report what changed before building on it.

---

## 1. What we are building

Users with the **General** role may run only the small model `qwen3.5:0.8b`. Every other role keeps today's behaviour. A SystemAdmin can change the allowed models per role from the Access screen without a redeploy.

| Phase | Work | Why in this order |
|---|---|---|
| **1** | The policy, its table, and enforcement in every endpoint that runs a model | The restriction is the point, and the server is the only place it holds |
| **2** | UI: the model list a user sees, a locked picker for one allowed model, the policy editor in Access | Needs the server answer to show |
| **3** | Live check on the Mac | Proves it with the real models |

**What "normal user" means here:** the stored role `General`.

**General's tabs become exactly five:** `dashboard`, `todo` (Task), `notes`, `sources`, `settings`. Today General also gets `jsonto` and `5lakhs` (F2); this build removes those two from the General baseline. None of the five runs a model, so by default a General user never calls one.

The model policy is the second line of defence. It applies when an admin grants a General user a model tab (for example `chat`) through the existing grant table: that user is still General and may run only `qwen3.5:0.8b`. A promotion to System, GroundControl or SystemAdmin lifts it at once.

**Not in this build:** per-user model overrides, token quotas, rate limits per role, limits for hosted (cloud) models beyond excluding them for General.

---

## 2. Established facts (verified at `cc95a82` — re-confirm, do not assume)

| # | Fact | Evidence |
|---|---|---|
| F1 | Roles are `General`, `System`, `SystemAdmin`; unknown values normalise to `General` | `backend/Models/Rbac.cs:17-33` |
| F2 | General's default tabs today are `dashboard todo jsonto 5lakhs notes sources settings`. They do not include `chat`, `workspace`, `docs` or `component-splitter`, which a General user reaches only through a tab grant. Grants are additive and stored only when they go beyond the role's baseline | `backend/Rbac/TabCatalog.cs:111-124`, `backend/Services/RbacService.cs:204-228` |
| F2b | The Access screen keeps a hand-written copy of each role's tabs, and it is already out of date (it gives General `chat` and `ielts`) | `frontend/src/components/tabs/RbacTab.vue:108-114` |
| F3 | `/api/models` returns every model from every source, the same for every user | `backend/MinimalApis/OllamaApis.cs:25-41` |
| F4 | Chat takes the model name from the request body and validates only that it is not empty | `backend/MinimalApis/ChatEndpoint.cs:44, 52, 106, 162`, `backend/DTOs/OllamaDtos.cs:11-18` |
| F5 | Workspace chat takes `req.Model` from the body; its errors are plain text, not JSON | `backend/MinimalApis/DocumentApis.cs:358-368` |
| F6 | PO extraction takes an optional model; when it is empty, `LlmPoFallback` uses `Ollama:ExtractionModel`, then `Ollama:DefaultModel` | `backend/MinimalApis/DocumentApis.cs:238-263`, `backend/Extraction/LlmPoFallback.cs:64-66` |
| F7 | The component splitter takes `req.Model` and `req.Source` | `backend/MinimalApis/ComponentSplitterApis.cs:100-129` |
| F8 | `ChatService.StreamAsync` is the shared path to a source; it already refuses embedding models | `backend/Services/ChatService.cs:50-58` |
| F9 | The frontend picks `models[0]` when the saved model is not in the list, so a stale choice corrects itself | `frontend/src/App.vue:128-148` |
| F10 | Pickers exist in Chat (grouped by source) and Workspace (a plain `Select`) | `frontend/src/components/tabs/ChatTab.vue:11, 50`, `frontend/src/components/tabs/WorkspaceTab.vue:94-99` |
| F11 | Admin routes answer 403 with a JSON body from a local helper, because `Results.Forbid()` needs an auth scheme this app has none of | `backend/MinimalApis/RbacApis.cs:124-128` |
| F12 | Schema changes go through `EnsureCreated()` plus idempotent `SchemaUpgrade` steps; no migrations | `backend/Program.cs:354-355`, `backend/Data/SchemaUpgrade.cs:22-43` |

If the context engine is applied (`backend/MinimalApis/ContextEngineApis.cs` exists), its `/api/context/preview` and `/api/context/settings` also take a model name. If the Oddity gateway exists, its keys belong to Ground Control users, so it is out of scope; say so in the report.

**Find the full list yourself.** Before Phase 1, run `git grep -n -E "\.Model\b|model\s*=" -- backend/MinimalApis backend/Services backend/Extraction backend/ComponentSplitter` and list every place a model name enters the backend. The gates check against your list, so a missed endpoint is a missed gate.

---

## 3. The design contract

### 3.1 Rule

```
allowed(user) = the policy row for the user's normalised role
                no row, or an empty list  →  no restriction (today's behaviour)

A request that names a model:
    model in allowed            → run it
    model not in allowed        → 403  "Your role (General) can use: qwen3.5:0.8b."
A request that names no model (only PO extraction allows this):
    use the row's DefaultModel; with no row, today's config fallback
```

- Model names compare case-insensitively. A name without a tag equals the same name with `:latest`.
- The source must match too: for a restricted role, only models of the `local` source are allowed, so a metered cloud model can never be reached by naming it.
- The check runs **before** any model call and before anything is saved (no user message stored for a refused chat).

### 3.2 Data

One table, `RoleModelPolicies`:

| Column | Notes |
|---|---|
| `Role` | primary key, a canonical role name |
| `AllowedModels` | comma-separated model names; empty means no restriction |
| `DefaultModel` | must be one of `AllowedModels` when that is not empty |
| `UpdatedAtUtc`, `UpdatedByUserId` | |

Seed once, only when the table is empty: `General` → allowed `qwen3.5:0.8b`, default `qwen3.5:0.8b`. Never overwrite a row an admin changed. Create the table in `SchemaUpgrade` for an existing database.

Cache the rows in memory and clear the cache on save, so a change applies to the next request without a restart.

### 3.3 API

| Route | Who | Purpose |
|---|---|---|
| `GET /api/models` | any signed-in user | Only the models the caller may use. Unchanged for unrestricted roles. Add `"locked": true` to the response when the caller has exactly one allowed model |
| `GET /api/admin/rbac/model-policies` | SystemAdmin | Every role with its allowed models and default, plus the installed local model names to choose from |
| `PUT /api/admin/rbac/model-policies/{role}` | SystemAdmin | Save one role. 400 for an unknown role, a default outside the list, or a model that is not installed. Saving the SystemAdmin role with a restriction is refused (an admin must never lock themselves out of models) |

If changing the `/api/models` response shape would break an existing caller, add the flag in a header (`X-Model-Locked: true`) instead, and say which you chose.

Errors: the policy refusal is 403 with `{ "error": "…" }` on JSON endpoints. On workspace chat, which answers in plain text (F5), use the same message in plain text.

### 3.4 Files

```
backend/Rbac/TabCatalog.cs               General baseline: the five tabs
backend/Models/RoleModelPolicy.cs        the entity
backend/Rbac/ModelPolicy.cs              the rule: Allowed(user), Check(user, model, source), DefaultFor(user)
backend/Data/AppDbContext.cs, SchemaUpgrade.cs        table + seed
backend/MinimalApis/OllamaApis.cs        filter /api/models
backend/MinimalApis/ChatEndpoint.cs, DocumentApis.cs, ComponentSplitterApis.cs   one call to the check
backend/MinimalApis/ContextEngineApis.cs (if present)                          one call to the check
backend/MinimalApis/RbacApis.cs, backend/DTOs/RbacDtos.cs                      the two admin routes
backend/Program.cs                        DI line
frontend/src/services/rbac.service.js, services/index.js
frontend/src/components/tabs/RbacTab.vue  the policy editor; its role preview must match the server
frontend/src/components/mostisko-docs/MostiskoDocsPage.vue   the roles table
frontend/src/components/tabs/ChatTab.vue, WorkspaceTab.vue, ComponentSplitterTab.vue   locked picker
scripts/model-policy-eval.mjs
```

### 3.5 UI

- **Locked picker.** When `locked` is true, every model picker shows the one model as plain text with a small lock icon and the line "Set by your role". No dropdown. When several models are allowed, the normal picker shows only those.
- **Access screen.** A "Models by role" section, SystemAdmin only: one row per role, a multi-select of installed local models, a default select, Save. Show "No restriction" for a role with an empty list. Validation errors appear beside the field.
- Current theme only: tokens and the `components/ui` kit, no new colours, light and dark, usable at 360 px.

---

## 4. Phase 1 — Policy and enforcement

**Do**
- Re-confirm §2 and paste your list of every model entry point.
- The eval script (§7) and this phase's checks, first.
- Set the General baseline in `TabCatalog` to exactly `dashboard`, `todo`, `notes`, `sources`, `settings`.
- §3.1–§3.3 on the backend. Every entry point from your list calls the one check.

**Gates**

| Gate | Check |
|---|---|
| G1.1 | The entry-point list, and for each one the file and line where it calls the check. Nothing on the list without a call |
| G1.2 | **Baseline.** A brand-new General user's `/api/me/tabs` is exactly `dashboard todo notes sources settings`. With no grants, that user gets 403 (tab) from chat, workspace chat, extraction and the splitter, and 200 from `/api/todorabbit`, `/api/notes` and `/api/context-sources`. A General user who holds an explicit `jsonto` grant keeps that tab |
| G1.3 | **Policy.** For the remaining gates an admin grants that General user `chat`, `workspace`, `docs` and `component-splitter`. The user then gets 403 with the role message when naming `qwen3.8:27b` on chat, workspace chat, extraction and the splitter. The fake Ollama received **no** request for any of them, and no chat message was stored |
| G1.4 | The same user naming `qwen3.5:0.8b` and `QWEN3.5:0.8B` succeeds on all four, and the fake received `qwen3.5:0.8b` |
| G1.5 | The same user naming a cloud model, even one called `qwen3.5:0.8b` from another source, gets 403 |
| G1.6 | PO extraction with no model, by a General user, reaches the fake with `qwen3.5:0.8b`; by a System user, with the config fallback as before |
| G1.7 | `/api/models` for the General user lists only `qwen3.5:0.8b` with `locked: true`; for System and SystemAdmin it is unchanged (compare with the response before the change) |
| G1.8 | Promote the user to System: the very next request with `qwen3.8:27b` succeeds. Demote back: the next one is refused. No restart |
| G1.9 | Admin routes: 403 for non-admins; 400 for a default outside the list, an unknown role, an uninstalled model, and a restriction on SystemAdmin. A saved change applies to the next request |
| G1.10 | Restarting the API keeps an admin's edited General row (the seed did not overwrite it). A fresh database gets the seed |
| G1.11 | `dotnet build` clean. `node scripts/model-policy-eval.mjs --phase 1` summary line, and the red line from before the code |

---

## 5. Phase 2 — UI

**Do**
- The locked picker in every picker the General user can see.
- The "Models by role" section in Access.

**Gates**

| Gate | Check |
|---|---|
| G2.1 | General user: Chat and Workspace show `qwen3.5:0.8b` with the lock and "Set by your role", no dropdown. A message sends and streams. Screenshots, light and dark |
| G2.2 | A General user whose browser had `qwen3.8:27b` saved from before opens the app: the picker shows `qwen3.5:0.8b` and the first message succeeds (F9) |
| G2.3 | System user: pickers unchanged. Screenshot |
| G2.4 | Access, as SystemAdmin: set General to allow `qwen3.5:0.8b` and `gemma3:270m`; the General user's picker becomes a normal two-item dropdown after a reload. Set it back. Before and after screenshots |
| G2.5 | The editor shows the server's validation errors beside the field; Save is disabled while nothing changed |
| G2.6 | The section is not rendered for a non-admin, and its routes return 403 for them (show both) |
| G2.7 | 360 px wide: no horizontal scroll. No hard-coded colours in changed files: `grep -nE -e "#[0-9a-fA-F]{3,8}\b" -e "rgb\(" <changed .vue files>` → no matches |
| G2.8 | A fresh General user sees exactly five tabs in the tab bar and the command palette (Dashboard, To-Do, Notes, Sources, Settings); `/chat` and `/json-converter` redirect away. The Access screen's role preview and the in-app docs roles table show the same five. Screenshot |
| G2.9 | `npm run build` clean. `--phase 2` summary line |

---

## 6. Phase 3 — Live check on the Mac

Ollama running with `qwen3.5:0.8b` and the 27B both pulled.

| Gate | Check |
|---|---|
| G3.1 | `ollama list` shows both models |
| G3.2 | As a General user in the real UI: send "Reply with exactly: OK". Time to the first word under 2 seconds. `ollama ps` shows `qwen3.5:0.8b` loaded |
| G3.3 | As the same user, force `qwen3.8:27b` with curl against `/api/chat`: 403, and `ollama ps` shows the 27B was not loaded by this request |
| G3.4 | As a System user, use the 27B, then as the General user use the 0.8B, three times each in turns. `ollama ps` keeps both loaded; if the 27B unloads each time, report it (it needs `OLLAMA_MAX_LOADED_MODELS=2`) |

---

## 7. Eval harness

`scripts/model-policy-eval.mjs`, Node 18+, no dependencies. If `scripts/oddity-eval.mjs` exists, reuse its fake Ollama and API start-up rather than writing a second copy.

- A **fake Ollama** that records every request and answers `/api/tags` (listing `qwen3.5:0.8b`, `qwen3.8:27b`, `gemma3:270m`, `nomic-embed-text`), `/api/show`, `/api/chat` (a short NDJSON stream) and `/api/generate`.
- Starts the API against it with a throwaway SQLite file. Never the dev database.
- Signs in as the seeded admin, with the name and secret from environment variables, never written in the script. Creates users by logging in, sets roles and tab grants through the existing admin API.
- Prints `id | check | PASS/FAIL | detail` and `N/M passed`, exits non-zero on any FAIL. `--phase N` and `--only <id>`.

---

## 8. Report format (end of every phase)

```
## Phase N report — <title>
Summary: <2–3 lines>
Eval: <N/M passed>   first run before the code: <N/M passed>

| Gate | Check | Result | Evidence |
|------|-------|--------|----------|

Facts from §2 that changed: <list, or "none">
Model entry points found: <list with file:line>   (Phase 1 only)
Files changed: <list>
Decisions made (and why): <list>
Noticed, not changed: <list>
Needs KAKAROT: <questions, or "none">
```

A phase with any FAIL is not complete. Say so plainly.

---

## 9. Do not

- Do not enforce only in the frontend, or only in `/api/models`.
- Do not substitute the allowed model silently when a forbidden one is requested. Refuse with the message, so the caller knows.
- Do not store a refused chat message or call Ollama for it.
- Do not let a restricted role reach a cloud model by name.
- Do not put a role list or a model list in the frontend.
- Do not use `Results.Forbid()` (F11) or EF migrations (F12).
- Do not change unrelated behaviour; report it instead.

---

## 10. Open questions for KAKAROT

Each has a default the build follows until you say otherwise.

1. **Existing General users lose JSON → Table and 5Lakhs.** Default: they lose them, as requested. Grant either tab to one user in Access to keep it for them.
2. **Refuse or replace?** Default: refuse a forbidden model with 403. The other choice is to quietly run the allowed model instead.
3. **Embeddings.** Document search uses `nomic-embed-text` for everyone. Default: not covered by the policy.
4. **Think toggle.** `qwen3.5:0.8b` supports thinking, which makes it slower. Default: unchanged, the user's toggle decides.
