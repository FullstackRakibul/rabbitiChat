# Mostisko — Model policy per role (General users on `gemma4:12b`)

### Evaluation-based build prompt for Claude Code

> **Before you start.** Commit the PO Studio work on a branch named `po-studio` (it is loose changes on `main` right now). This build starts from that branch.
>
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
5. **One rule, one place.** Write the policy once, in `backend/Rbac/ModelPolicy.cs`, and call it from every endpoint. A copy of the rule in an endpoint, or a role or model list in the frontend, is an automatic FAIL.
6. **No new packages.** Do not push, merge to `main` or open a pull request. Work on a branch named `model-policy`, created from `po-studio`.
7. **Do not change PO Studio or Oddity behaviour** beyond adding the policy check to their model entry points. Their eval suites (`scripts/po-eval.mjs`, `scripts/oddity-eval.mjs`) must still pass at the end of every phase.
8. **§2 was verified at commit `cc95a82`. The repository has moved on** (Oddity at `a0dcb17`, then PO Studio on `po-studio`). Line numbers have shifted. Re-confirm every fact you use and report what changed before building on it.

---

## 1. What we are building

Every user type (role) gets its own list of allowed models, which a SystemAdmin edits in the Access screen without a redeploy. The starting values: **General** may run only `gemma4:12b`; every other role may run every local model, as today.

| Phase | Work | Why in this order |
|---|---|---|
| **1** | The policy, its table, and enforcement in every endpoint that runs a model | The restriction is the point, and the server is the only place it holds |
| **2** | UI: the model list a user sees, a locked picker for one allowed model, the policy editor in Access | Needs the server answer to show |
| **3** | Live check on the Mac | Proves it with the real models |

**What "normal user" means here:** the stored role `General`.

**General's tabs become exactly six:** `dashboard`, `todo` (Task), `notes`, `sources`, `settings`, `5lakhs`. Today General also gets `jsonto` (F2); this build removes only that one from the General baseline. None of the six runs a model, so by default a General user never calls one.

The model policy is the second line of defence. It applies when an admin grants a General user a model tab (for example `chat` or `workspace`) through the existing grant table: that user is still General and may run only `gemma4:12b`. A promotion to System, GroundControl or SystemAdmin lifts it at once.

**Not in this build:** per-user model overrides, token quotas, rate limits per role, limits for hosted (cloud) models beyond excluding them for restricted roles, Oddity gateway keys (they belong to Ground Control users and have their own model rules).

---

## 2. Established facts (verified at `cc95a82` unless marked — re-confirm, do not assume)

| # | Fact | Evidence |
|---|---|---|
| F1 | Roles are `General`, `System`, `SystemAdmin`, and `GroundControl` since Oddity; unknown values normalise to `General` | `backend/Models/Rbac.cs` |
| F2 | General's default tabs at `cc95a82` were `dashboard todo jsonto 5lakhs notes sources settings`. They do not include `chat`, `workspace`, `docs` or `component-splitter`, which a General user reaches only through a tab grant. Grants are additive and stored only when they go beyond the role's baseline | `backend/Rbac/TabCatalog.cs`, `backend/Services/RbacService.cs` |
| F2b | The Access screen keeps a hand-written copy of each role's tabs, and at `cc95a82` it was out of date (it gave General `chat` and `ielts`) | `frontend/src/components/tabs/RbacTab.vue` |
| F3 | `/api/models` returns every model from every source, the same for every user | `backend/MinimalApis/OllamaApis.cs` |
| F4 | Chat takes the model name from the request body and validates only that it is not empty | `backend/MinimalApis/ChatEndpoint.cs`, `backend/DTOs/OllamaDtos.cs` |
| F5 | Workspace (document) chat takes `req.Model` from the body; its errors are plain text, not JSON | `backend/MinimalApis/DocumentApis.cs` |
| F6 | **Changed by PO Studio.** Extraction resolves its model as: the request's model, else the buyer profile's `ExtractionModel`, else `Extraction:DefaultModel` (`gemma4:12b`), else the old `Ollama:ExtractionModel` / `Ollama:DefaultModel` keys | `backend/Extraction/PoStudio.cs`, `backend/Extraction/LlmPoFallback.cs`, `backend/appsettings.json` |
| F7 | The component splitter takes `req.Model` and `req.Source` | `backend/MinimalApis/ComponentSplitterApis.cs` |
| F8 | `ChatService.StreamAsync` is the shared path to a source; it already refuses embedding models | `backend/Services/ChatService.cs` |
| F9 | The frontend picks `models[0]` when the saved model is not in the list, so a stale choice corrects itself | `frontend/src/App.vue` |
| F10 | **Changed by PO Studio.** `WorkspaceTab.vue` and `DocumentsTab.vue` are deleted; the document chat and the extraction model choice live in `PoStudioTab.vue` and `frontend/src/components/po-studio/`. Chat keeps its picker in `ChatTab.vue` | `po-studio` branch |
| F11 | Admin routes answer 403 with a JSON body from a local helper, because `Results.Forbid()` needs an auth scheme this app has none of | `backend/MinimalApis/RbacApis.cs` |
| F12 | Schema changes go through `EnsureCreated()` plus idempotent `SchemaUpgrade` steps; no migrations. PO Studio added its columns to both `CREATE TABLE` and `ALTER TABLE`, because on a fresh database `EnsureCreated()` builds from the EF model and a raw seed `INSERT` then hit a `NOT NULL` | `backend/Program.cs`, `backend/Data/SchemaUpgrade.cs` |
| F13 | **New with PO Studio.** `backend/MinimalApis/PoStudioApis.cs` holds the preview and extraction routes, gated by tab `docs`. The preview takes `numCtxOverride` | `po-studio` branch |
| F14 | Test models: `qwen3.5:0.8b`, `qwen3.5:9b` and the 4-bit `qwen3.8:27b` are being removed from the Mac. Live models are `gemma4:12b`, `qwen3.8-27b-q3` and `nomic-embed-text` | KAKAROT's setup |

If the context engine is applied (`backend/MinimalApis/ContextEngineApis.cs` exists), its `/api/context/preview` and `/api/context/settings` also take a model name and must call the check.

**Find the full list yourself.** Before Phase 1, run
`git grep -n -E "\.Model\b|ExtractionModel|model\s*=" -- backend/MinimalApis backend/Services backend/Extraction backend/ComponentSplitter`
and list every place a model name enters the backend, including PO Studio's routes and the profile's `ExtractionModel`. The gates check against your list, so a missed endpoint is a missed gate.

---

## 3. The design contract

### 3.1 Rule

```
allowed(user) = the policy row for the user's normalised role
                no row, or an empty list  →  no restriction (today's behaviour)

The model a request will run (after any fallback):
    model in allowed            → run it
    model not in allowed        → 403  "Your role (General) can use: gemma4:12b."
A request that names no model (extraction only):
    resolve it as today (F6), then apply the same check to the result.
    If no model was named at all and nothing resolves, use the row's DefaultModel.
```

- **Check the resolved model, not just the named one.** For extraction the check runs on the model PO Studio actually picks (request, then profile, then default). A buyer profile set to `qwen3.8-27b-q3` therefore refuses a General user with: "This buyer profile uses qwen3.8-27b-q3. Your role (General) can use: gemma4:12b."
- Model names compare case-insensitively. A name without a tag equals the same name with `:latest`.
- The source must match too: for a restricted role, only models of the `local` source are allowed, so a metered cloud model can never be reached by naming it.
- The check runs **before** any model call and before anything is saved (no user message stored for a refused chat, no `ExtractionRuns` row for a refused extraction).
- Saving a buyer profile still accepts any installed model as `ExtractionModel`; profiles are install-wide and the check happens when a run starts.

### 3.2 Data

One table, `RoleModelPolicies`:

| Column | Notes |
|---|---|
| `Role` | primary key, a canonical role name |
| `AllowedModels` | comma-separated model names; empty means no restriction |
| `DefaultModel` | must be one of `AllowedModels` when that is not empty |
| `UpdatedAtUtc`, `UpdatedByUserId` | |

Seed one row per role, only for roles that have no row yet, so a role added later gets its row on the next start:

| Role | Allowed models | Default |
|---|---|---|
| `General` | `gemma4:12b` | `gemma4:12b` |
| `System` | empty (all local models) | empty |
| `GroundControl` | empty (all local models) | empty |
| `SystemAdmin` | empty (all local models), and it cannot be restricted | empty |

Never overwrite a row an admin changed. Create the table in `SchemaUpgrade` for an existing database, and in the EF model for a fresh one (F12).

Cache the rows in memory and clear the cache on save, so a change applies to the next request without a restart.

### 3.3 API

| Route | Who | Purpose |
|---|---|---|
| `GET /api/models` | any signed-in user | Only the models the caller may use. Unchanged for unrestricted roles. Add `"locked": true` when the caller has exactly one allowed model |
| `GET /api/admin/rbac/model-policies` | SystemAdmin | Every role with its allowed models and default, plus the installed local model names to choose from |
| `PUT /api/admin/rbac/model-policies/{role}` | SystemAdmin | Save one role. 400 for an unknown role, a default outside the list, or a model that is not installed. Saving the SystemAdmin role with a restriction is refused (an admin must never lock themselves out of models) |

If changing the `/api/models` response shape would break an existing caller (Oddity's `/api/tags` passthrough included), add the flag in a header (`X-Model-Locked: true`) instead, and say which you chose.

Errors: the policy refusal is 403 with `{ "error": "…" }` on JSON endpoints. On document chat, which answers in plain text (F5), use the same message in plain text.

### 3.4 Files

```
backend/Rbac/TabCatalog.cs               General baseline: the six tabs
backend/Models/RoleModelPolicy.cs        the entity
backend/Rbac/ModelPolicy.cs              the rule: Allowed(user), Check(user, model, source), DefaultFor(user)
backend/Data/AppDbContext.cs, SchemaUpgrade.cs        table + seed
backend/MinimalApis/OllamaApis.cs        filter /api/models
backend/MinimalApis/ChatEndpoint.cs, DocumentApis.cs, ComponentSplitterApis.cs, PoStudioApis.cs   one call to the check
backend/Extraction/PoStudio.cs           the check on the resolved model, before the run row is written
backend/MinimalApis/ContextEngineApis.cs (if present)                          one call to the check
backend/MinimalApis/RbacApis.cs, backend/DTOs/RbacDtos.cs                      the two admin routes
backend/Program.cs                        DI line
frontend/src/services/rbac.service.js, services/index.js
frontend/src/components/tabs/RbacTab.vue  the policy editor; its role preview must come from the server
frontend/src/components/mostisko-docs/MostiskoDocsPage.vue   the roles table
frontend/src/components/tabs/ChatTab.vue, PoStudioTab.vue, po-studio/*, ComponentSplitterTab.vue   locked picker
scripts/model-policy-eval.mjs
```

### 3.5 UI

- **Locked picker.** When `locked` is true, every model picker shows the one model as plain text with a small lock icon and the line "Set by your role". No dropdown. When several models are allowed, the normal picker shows only those.
- **PO Studio.** A restricted user who picks a buyer profile whose model they cannot run sees the refusal message next to the Extract button before pressing it, and the button is disabled. The server still refuses (rule 4).
- **Access screen.** A "Models by role" section, SystemAdmin only: one row for **every** role, each with a multi-select of installed local models, a default select and Save. A role with an empty list shows "All local models". The SystemAdmin row is read-only and says why. Validation errors appear beside the field.
- Current theme only: tokens and the `components/ui` kit, no new colours, light and dark, usable at 360 px.

---

## 4. Phase 1 — Policy and enforcement

**Do**
- Branch `model-policy` from `po-studio`. Run `po-eval` and `oddity-eval` once and paste both summary lines as the baseline.
- Re-confirm §2 and paste your list of every model entry point.
- The eval script (§7) and this phase's checks, first.
- Set the General baseline in `TabCatalog` to exactly `dashboard`, `todo`, `notes`, `sources`, `settings`, `5lakhs`.
- §3.1–§3.3 on the backend. Every entry point from your list calls the one check.

**Gates**

| Gate | Check |
|---|---|
| G1.1 | The entry-point list, and for each one the file and line where it calls the check. Nothing on the list without a call |
| G1.2 | **Baseline.** A brand-new General user's `/api/me/tabs` is exactly `dashboard todo notes sources settings 5lakhs`. With no grants, that user gets 403 (tab) from chat, document chat, extraction and the splitter, and 200 from `/api/todorabbit`, `/api/notes` and `/api/context-sources`. A General user who holds an explicit `jsonto` grant keeps that tab |
| G1.3 | **Policy.** For the remaining gates an admin grants that General user `chat`, `workspace`, `docs` and `component-splitter`. The user then gets 403 with the role message when naming `qwen3.8-27b-q3` on chat, document chat, extraction and the splitter. The fake Ollama received **no** request for any of them, no chat message was stored, and no `ExtractionRuns` row was written |
| G1.4 | The same user naming `gemma4:12b` and `GEMMA4:12B` succeeds on all four, and the fake received `gemma4:12b` |
| G1.5 | The same user naming a cloud model, even one called `gemma4:12b` from another source, gets 403 |
| G1.6 | **Extraction resolution.** General user, no model named: a profile with an empty `ExtractionModel` reaches the fake with `gemma4:12b`; a profile set to `qwen3.8-27b-q3` gets 403 with the profile message and no request at the fake. A System user with the same profile reaches the fake with `qwen3.8-27b-q3` |
| G1.7 | `/api/models` for the General user lists only `gemma4:12b` with the lock flag; for System and SystemAdmin it is unchanged (compare with the response before the change) |
| G1.8 | Promote the user to System: the very next request with `qwen3.8-27b-q3` succeeds. Demote back: the next one is refused. No restart |
| G1.9 | Admin routes: 403 for non-admins; 400 for a default outside the list, an unknown role, an uninstalled model, and a restriction on SystemAdmin. A saved change applies to the next request |
| G1.10 | **Rows per role.** A fresh database and an existing-database copy both have one row for each role with the values in §3.2. Restarting keeps an admin's edited rows. Restrict **System** to `gemma3:270m`: a System user then gets 403 for `qwen3.8-27b-q3` and succeeds with `gemma3:270m`; clear it and the 27B works again. No restart |
| G1.11 | **Regressions.** `po-eval` and `oddity-eval` pass with the same counts as the baseline (paste both lines). If a check in them assumed General had `jsonto` or an unrestricted model, list it and how you updated it |
| G1.12 | `dotnet build` clean (0 warnings). `node scripts/model-policy-eval.mjs --phase 1` summary line, and the red line from before the code |

---

## 5. Phase 2 — UI

**Do**
- The locked picker in every picker a General user can see.
- The PO Studio profile warning (§3.5).
- The "Models by role" section in Access.

**Gates**

| Gate | Check |
|---|---|
| G2.1 | General user with grants: Chat and PO Studio show `gemma4:12b` with the lock and "Set by your role", no dropdown. A message sends and streams. Screenshots, light and dark |
| G2.2 | A General user whose browser had `qwen3.8-27b-q3` saved from before opens the app: the picker shows `gemma4:12b` and the first message succeeds (F9) |
| G2.3 | PO Studio: choosing a profile set to `qwen3.8-27b-q3` shows the refusal message by the Extract button and disables it. Screenshot |
| G2.4 | System user: pickers unchanged. Screenshot |
| G2.5 | Access, as SystemAdmin: set General to allow `gemma4:12b` and `qwen3.8-27b-q3`; the General user's picker becomes a normal two-item dropdown after a reload. Set it back. Before and after screenshots |
| G2.6 | The editor shows the server's validation errors beside the field; Save is disabled while nothing changed |
| G2.7 | The section is not rendered for a non-admin, and its routes return 403 for them (show both) |
| G2.8 | 360 px wide: no horizontal scroll. No hard-coded colours in changed files: `grep -nE -e "#[0-9a-fA-F]{3,8}\b" -e "rgb\(" <changed .vue files>` → no matches |
| G2.9 | A fresh General user sees exactly six tabs in the tab bar and the command palette (Dashboard, To-Do, Notes, Sources, Settings, 5Lakhs); `/chat` and `/json-converter` redirect away. The Access screen's role preview and the in-app docs roles table show the same six, read from the server. The "Models by role" section lists every role. Screenshot |
| G2.10 | `npm run build` clean. `--phase 2` summary line; `po-eval` and `oddity-eval` still pass |

---

## 6. Phase 3 — Live check on the Mac

Ollama running with `gemma4:12b` and `qwen3.8-27b-q3`.

| Gate | Check |
|---|---|
| G3.1 | `ollama list` shows both models |
| G3.2 | As a General user with the `chat` grant, in the real UI: send "Reply with exactly: OK". It answers OK. `ollama ps` shows `gemma4:12b` loaded |
| G3.3 | As the same user, force `qwen3.8-27b-q3` with curl against `/api/chat`: 403, and `ollama ps` shows the 27B was not loaded by this request |
| G3.4 | As a System user, extract one real PO with a profile set to `qwen3.8-27b-q3`; as the General user, extract with a profile that uses the default. Both runs show their model in the run history |

---

## 7. Eval harness

`scripts/model-policy-eval.mjs`, Node 18+, no dependencies. Reuse the fake Ollama and API start-up from `scripts/po-eval.mjs` or `scripts/oddity-eval.mjs`; do not write a third copy. If they differ, move the shared part into one helper both import, and show their suites still pass.

- The **fake Ollama** records every request and answers `/api/tags` (listing `gemma4:12b`, `qwen3.8-27b-q3`, `gemma3:270m`, `nomic-embed-text`), `/api/show`, `/api/chat` (a short NDJSON stream), `/api/generate` and `/api/embeddings`.
- Starts the API against it with a throwaway SQLite file. Never the dev database.
- Signs in as the seeded admin, with the name and secret from environment variables, never written in the script. Creates users by logging in, sets roles and tab grants through the existing admin API.
- Prints `id | check | PASS/FAIL | detail` and `N/M passed`, exits non-zero on any FAIL. `--phase N` and `--only <id>`.

---

## 8. Report format (end of every phase)

```
## Phase N report — <title>
Summary: <2–3 lines>
Eval: <N/M passed>   first run before the code: <N/M passed>
Other suites: po-eval <N/M>, oddity-eval <N/M>

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

At the end of each phase, commit on `model-policy` with a message naming the phase, so the work cannot be lost again.

---

## 9. Do not

- Do not enforce only in the frontend, or only in `/api/models`.
- Do not substitute the allowed model silently when a forbidden one is requested or resolved. Refuse with the message, so the caller knows.
- Do not store a refused chat message, write a run row, or call Ollama for a refused request.
- Do not let a restricted role reach a cloud model by name.
- Do not put a role list or a model list in the frontend.
- Do not use `Results.Forbid()` (F11) or EF migrations (F12). `LogEndpoints.cs` still uses `Results.Forbid()`; report it, do not fix it here.
- Do not change PO Studio extraction prompts, budgets or Oddity behaviour.
- Do not push, merge to `main` or open a pull request.

---

## 10. Open questions for KAKAROT

Each has a default the build follows until you say otherwise.

1. **Existing General users lose JSON → Table.** Default: they lose it; 5Lakhs stays. Grant `jsonto` to one user in Access to keep it for them.
2. **Refuse or replace?** Default: refuse a forbidden model with 403, including a profile's model in PO Studio. The other choice is to quietly run the allowed model instead.
3. **Who edits buyer profiles.** Default: unchanged (anyone with the `docs` tab), so a General user with that grant can set a profile's model to one they cannot run themselves. Say if profile editing should be System and above.
4. **Embeddings.** Document search uses `nomic-embed-text` for everyone. Default: not covered by the policy.
5. **Think toggle.** Default: unchanged, the user's toggle decides.
