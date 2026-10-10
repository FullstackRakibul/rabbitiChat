# Mostisko (RabbitiChat) — Project Summary

Mostisko (মস্তিস্ক, "brain") started as a self-hosted, ChatGPT-style client for **Ollama** and has grown into a **multi-module workspace**. It now covers:

- streaming chat across several model providers
- garment-PO document RAG and structured extraction, with a human review step
- Kanban boards, notes, and web search
- a JSON→table converter
- IELTS reading practice
- an Angular component splitter
- role-based access control
- a full observability and log viewer

A Vue 3 SPA talks to an ASP.NET Core 8 minimal API. **The browser never talks to a model provider directly.** Every model call goes through the backend, so API keys stay server-side, and everything persists in SQLite.

```
Browser — Vue 3 SPA (vue-router, Tailwind v4, reka-ui)       dev: port 5173
        │  /api/*  (Vite dev proxy → :5274, or same origin in Docker)
        ▼
ASP.NET Core 8 Minimal API (RabbitIC.Api)                     dev: 5274 · Docker: 8080
        │  middleware: Correlation → RequestLogging → TabAccess (RBAC)
        │  ├─ ModelSourceRegistry ──▶ Ollama local (/api/chat, /api/show, /api/embeddings)
        │  │                      ──▶ Ollama Cloud (metered models + /api/web_search)
        │  │                      ──▶ OpenAI-compatible providers (OpenRouter, Groq, …)
        │  ├─ VectorIndex (in-RAM SIMD cosine search, warmed from SQLite at startup)
        │  ├─ ExtractionDispatcher (detect → parse → validate → gate)
        │  ├─ SplitterService (Angular component splitting pipeline)
        │  └─ Log queue → background writer → SQLite + SSE live tail
        ▼
SQLite (rabbitic.db) — users/roles, chats, documents + vectors, extraction runs,
                       boards, notes, IELTS, context sources, search history, logs
```

---

## Backend (`backend/` — RabbitIC.Api, .NET 8)

**Stack:**
- minimal APIs, with EF Core + SQLite
- `IHttpClientFactory` named clients (`ollama`, `ollama-cloud`, and one per OpenAI-compatible provider)
- **PdfPig** (PDF), **ClosedXML** (XLSX), **System.Numerics.Tensors** (SIMD)
- Swashbuckle: Swagger UI at `/swagger` with bearer auth, enabled in all environments

**Composition** (`Program.cs`, `Middleware/MiddlewareDependencyInversion.cs`):
- Each feature has a `MinimalApis/*Apis.cs` file, mounted under `/api` through an `Application…Apis()` extension. Health routes live under `/public`.
- The pipeline runs `UseCorrelation` → `UseRequestLogging` → `UseTabAccess` ahead of every endpoint.
- In Docker, the built SPA is served from `wwwroot` with an SPA fallback. The fallback never swallows `/api`, `/public` or `/swagger`.

**Schema:**
- `EnsureCreated()` builds the database, then an idempotent hand-written `SchemaUpgrade` runs on every boot. Each step checks `TableExists`/`ColumnExists` first; there are no EF migrations.
- Startup then normalises roles and seeds the admin (`RbacSeeder`).
- It also seeds shared IELTS demo tests (`IeltsReadingSeeder`), warms the vector index, purges notes soft-deleted more than 30 days ago, and writes a startup system log.

### Model sources (`ModelSources/`)
- **`IChatModelSource`**: one narrow abstraction for "list models" and "stream a chat turn". `ModelSourceRegistry` resolves sources by key, so adding a provider means one DI registration and no edits to `ChatService`.
- **`local`**: Ollama on the LAN or the local machine. Capabilities (`tools`, `thinking`, context length) are read from `/api/show`, not guessed.
- **`cloud`**: Ollama Cloud's metered models. The curated list comes from `OllamaCloud:Models`, with a `-cloud` suffix mapping.
- **OpenAI-compatible**: one adapter (`OpenAiCompatibleSource`) serves every provider configured in `OpenAiCompatible:Providers`. A provider with no key is skipped at startup.
- **`echo`**: an opt-in diagnostic source, enabled with `ModelSources:EnableEcho`.

### Chat (`POST /api/chat`, `ChatEndpoint.cs`)
1. Resolves or creates the owner-scoped conversation and saves the user turn.
2. Restores the conversation's `ActiveSourceIds` when the client sends none.
3. Builds the system prompt: the configured persona plus each active **context source**, via `IContextSourceService`.
4. Applies web search, which is opt-in per message:
   - Tool-capable models get a `web_search` tool, capped at 2 tool rounds.
   - Other models get one search prepended up front as a system message.
5. Sends a **Think** request only to models that declare reasoning support.
6. Streams **tagged NDJSON frames** (`content`, `thinking`, `tool`, `error`, `done`) through `ChatStreamWriter`. The conversation id travels back in `X-Conversation-Id`.
7. Persists the reply, including a partial one if the user stops mid-stream. Messages record `WebSearchUsed`; reasoning is never persisted.

The chat endpoints reject embedding models (`*embed*`, `bge-`, `gte-`, `e5-`) with a clear error.

### Documents, RAG and PO extraction (`DocumentApis.cs`, `Retrieval/`, `Extraction/`)

**Upload** (≤ 20 MB, pdf/xlsx/txt/csv):
- The original is saved to `data/uploads/{id}{ext}` before extraction, so a failed ingest is still recoverable and the review screen can show the source PDF.
- The text is then chunked (~800 chars, ~100 overlap), embedded with `nomic-embed-text`, stored as L2-normalised float32 BLOBs, and added to the in-RAM `VectorIndex`.

**Retrieval:**
- The metadata gate runs first: owner, then buyer and style number. It is a security control, not an optimisation.
- Scoring is a SIMD dot product into a top-K heap with a minimum-score cutoff.

**Workspace chat** (`/api/documents/workspace-chat`):
- RAG over local Ollama.
- When a document is pinned, its whole text is the context. Otherwise the context is a top-6 vector search.
- Active **buyer profiles** (context sources of kind `buyer`) are added to the persona, and the model must cite `[source]` refs.
- A pinned document gets its **own persisted conversation thread** (`Document.ConversationId`). Unpinned questions stay ephemeral.

**Extraction** (`POST /documents/{id}/extract`), through `ExtractionDispatcher`:
- **Detect** scores the PO against `BuyerProfile.DetectPattern` markers. A majority match is required.
- **Parse** routes to a deterministic `IPoParser` by `ParserKey`. When none applies, it uses the `LlmPoFallback`, which reads all chunks at temperature 0 with JSON format.
  - No deterministic parsers are registered yet, so every extraction currently takes the LLM path.
- **Validate** runs `ExtractionValidator` arithmetic cross-checks against the printed totals.
- **Gate** applies the key rule: **model output is never `ok`**. Only a deterministic parse that passes every check is clean; everything else lands in `needs_review` (or `failed`, as HTTP 422).
- Every run is recorded in **`ExtractionRun`**. `POST /extraction/confirm` stores the reviewer's approved JSON and edits *beside* the untouched machine output, so the run stays auditable.

**Buyer profiles:** `/api/buyer-profiles` handles CRUD on the detection and routing rules, plus a `detect/{documentId}` dry-run for tuning patterns.

### Context sources (`/api/context-sources`)
- Named, toggleable, ordered text blocks that are added to a system prompt. A long block is trimmed with a visible marker.
- There is one entity with a `Kind` field: `source` blocks feed Chat and `buyer` blocks feed Workspace. Both surfaces share one assembly path, so they cannot drift apart.

### Web search (`/api/search`, `/api/search/fetch`, `/api/search/health`, `/api/search/history`)
- The backend proxies Ollama's hosted `web_search`. The key stays server-side, and calls are rate-limited per user (HTTP 429).
- If the key is missing, the endpoints answer 503 and the rest of the app keeps working.
- **Search history** is deduplicated on the query text, so running a query again bumps `RunCount`. It supports soft delete and restore. Results are never cached: a "reuse" is always a re-run.

### Kanban boards — todorabbit (`/api/todorabbit/...`)
Boards, columns (with colour keys and reordering) and cards (with priority, plus a move to another column and index), using a repository/service layering.

### Notes (`/api/notes`, `/api/public/notes/{token}`)
- Notes are Markdown, and the first line becomes the title. They can be pinned, filed in folders, and searched.
- Optimistic concurrency: an update carries `expectedUpdatedAt`, and a stale edit is refused with 409.
- Delete is soft and restorable, with a permanent delete option. Notes deleted more than 30 days ago are purged at startup.
- **Share links**: a token makes a note readable without auth at `/n/:token` in the SPA. The public endpoint is rate-limited per IP.

### IELTS (`/api/ielts/...`)
- **Reading tests:** a passage plus questions. Supported formats are T/F/NG, Y/N/NG, multiple choice, matching headings, sentence completion and short answer.
- **Ownership:** user-authored tests are private. Seeded demo tests are shared and read-only (`IsSystem`, `SeedKey`).
- **Attempts:** a sitting is served **without answer keys**. Scoring on submit is **deterministic**: a normalised string comparison with pipe-separated accepted variants, and no model call. Verdicts are frozen per answer, so a later change to the answer key never rewrites history.
- **Band estimate:** comes from a published raw-score table, scaled to 40 questions.
- **Mistake log:** every wrong answer feeds an owner-scoped `Mistake` table keyed by type, skill and topic. A repeat bumps `TimesSeen`. Mistakes support spaced review (`NextReviewAt`), user tagging of *why* a mistake happened, and a weakness summary.

### Angular component splitter (`/api/component-splitter/{parse,facts,analyse,generate}`)
The pipeline is: **Parse → { Facts ‖ Reason } → Merge → human REVIEW → Generate → Validate**.

1. **Parse**: `ComponentTsParser` and `TemplateParser` turn a component's `.ts` file (and its template) into an IR. Non-Angular input gets a 400.
2. **Facts and Reason** run in parallel and never see each other's output:
   - Deterministic `StructuralFacts` derive each candidate block's inputs, outputs and wiring.
   - `SplitReasoner` asks a model for split decisions. It uses `ChatService`, so it works with any model source.
3. **Merge**: the two halves combine into a `SplitProposal`. `/analyse` streams that proposal and can never produce files.
4. **Generate**: `/generate` accepts only an approved plan plus the **original source**. It re-parses that source on the server, because a client-held IR is never trusted. `CodeGenerator` emits the files.
5. **Validate**: `SplitValidator` produces a report computed from the generated output.

Files are returned in the response body and never written to disk.

### RBAC (`/api/me/tabs`, `/api/admin/rbac/...`)
- This is layered on the existing auth. A `User` has a PBKDF2 secret and salt, a bearer token, and a `Role`: `General`, `System` or `SystemAdmin`. Any unknown role fails closed to General.
- **`Rbac/TabCatalog.cs`** holds the tab keys, which are the contract with the SPA, along with aliases and the role baselines:

  | Role | Default tabs |
  |---|---|
  | General | dashboard, todo, jsonto, 5lakhs, notes, sources, settings |
  | System | General + chat, workspace, docs, search, ielts, component-splitter |
  | SystemAdmin | everything, including `logs` and `rbac` (Access) |

- Per-user grants (`UserTabPermission`) are **additive only**: they add to the role baseline and never take a tab away.
- **`TabAccessMiddleware`** is the real enforcement boundary. It maps path prefixes to tabs (longest match wins) and returns 403 when a tab is missing. `/api/auth`, `/api/me/tabs` and `/api/models` stay open to every user.
- Owner scoping everywhere returns **404, not 403**, for other people's rows, so a probe cannot tell whether a row exists.
- Admin guardrails: an admin cannot edit their own row, and the last admin cannot be removed. A built-in admin account is seeded on first run (see README).

### Observability (`Observability/`, `LogEndpoints.cs`)
- **`ActivityLog`** records every request, client event, app log and system log, each tagged with an `X-Correlation-Id`. **`AuditLog`** records each EF entity add, change or delete as an old→new diff, written by `AuditInterceptor`.
- Every log path goes through one default-deny **`Redactor`**. Secrets, tokens, keys and cookies, and any property marked `[SensitiveData]`, are replaced with `***`.
- A bounded `LogQueue` feeds `LogWriterBackgroundService`. It batch-inserts (≤ 200 rows or every 1 s) in its own DbContext, broadcasts to SSE subscribers, and runs daily retention cleanup.
- Endpoints:
  - activity entries (keyset-paginated, with detail view)
  - audit entries (SystemAdmin only)
  - correlation lookup
  - system info and stats
  - `POST /api/logs/client`: batched client events, at most 50 per batch, rate-limited
  - `GET /api/stream/logs`: SSE live tail

### Config (`appsettings.json`)
| Key | Purpose |
|---|---|
| `ConnectionStrings:Default` | SQLite path (`Data/rabbitic.db`). A relative path is anchored to the app folder. |
| `Ollama:Url`, `EmbeddingModel`, `SystemPrompt` | Local Ollama URL (currently a LAN host), the embedding model, and the persona prompt. |
| `OllamaCloud:ApiKey`, `BaseUrl`, `Models`, `CloudSuffix` | Cloud models and web search. |
| `OpenAiCompatible:Providers[]` | Per provider: `Key` (its id), `BaseUrl`, `ApiKey`, `Models`, `ToolCapableModels`, `ExtraHeaders`. |
| `ModelSources:EnableEcho` | Turns on the diagnostic source. |

CORS covers the local and LAN dev origins and a Vercel deployment, and exposes `X-Conversation-Id`.

---

## Frontend (`frontend/` — Vue 3 + Vite 8)

**Stack:**
- Vue 3 (Composition API) and **vue-router 4** (history mode)
- **Tailwind CSS v4**, with a shadcn-style kit in `src/components/ui/`: badge, button, card, checkbox, dialog, input, kbd, label, select, separator, switch, tabs, textarea. It is built on **reka-ui**, `class-variance-authority`, `tailwind-merge` and `tw-animate-css`.
- `@lucide/vue` icons, `@vueuse/core`
- `marked` + `DOMPurify` for Markdown
- `pdfjs-dist` to view PDFs during extraction review
- `vuedraggable`/`sortablejs` for Kanban drag-and-drop
- `virtua` for virtualised log lists
- `jspdf` + `html2canvas` for cheque PDFs

There is no state library: `App.vue` owns shared state.

### Shell and navigation
- **One URL per module** (`src/app/router.js`): `/dashboard`, `/chat`, `/workspace`, `/documents`, `/todo`, `/search`, `/json-converter`, `/5lakhs`, `/ielts`, `/component-splitter`, `/sources`, `/settings`, `/access`, `/notes` (`/notes/:id`), `/logs`, plus a public `/n/:token`.
  - Routes carry no components. The shell keeps **every module mounted**, so a streaming reply survives navigation. The router only owns which module is active.
- **Permission guard**: the shell supplies the tabs from `/api/me/tabs`. A forbidden route redirects to a tab the user does have. This is for user convenience only; the API enforces access.
- **`AppShell.vue`**: a persistent header with the brand, **`ModuleTabBar.vue`**, a command-palette button, a connection dot and a light/dark toggle (`useTheme`). Below it is an optional sidebar slot.
  - The tab bar is a sliding-pill track. It scrolls sideways when it overflows, with edge fades and arrows, keeps the active tab centred, and shows inactive tabs as icons only on phones.
- **Sidebars per tab**: `ChatSidebar.vue` shows conversations grouped by recency. `WorkspaceSidebar.vue` shows documents and the current session. `AppSidebar.vue` is now dead code.
- **`CommandPalette.vue`** (Ctrl/Cmd+K) and **`useShortcuts`**:
  - Ctrl/Cmd+B toggles the sidebar.
  - Ctrl/Cmd+Enter submits.
  - Esc closes the top overlay.
  - `g` then `c`/`w`/`d`/`t` jumps to a tab.
  - Shortcuts do nothing while you type in a field.
- `LoginGate.vue` shows the login screen. `WorldMapBackdrop.vue` is decorative.

### Modules (`src/components/tabs/`)
| Tab | What it does |
|---|---|
| **Dashboard** | Landing page with quick actions. It shows only real counts from the API, and "—" when a count fails to load (never a fake 0). |
| **Chat** | Persistent streaming chat. `ModelPicker` groups models by source and flags metered ones. `ChatComposer` offers **Think** and **Web search** toggles, shown only when the model supports them, a context-sources popover (`SourcesPopover`), and a context-usage bar. `ThinkingBlock` shows live reasoning and collapses when the answer starts. Replies render as Markdown and can be stopped. |
| **Workspace** | The PO assistant. Pick or pin a document, chat against it with citations in a persisted per-document thread, manage **buyer profiles** (kind-`buyer` context sources), and run extraction. |
| **Documents** | Document library: upload, buyer tag, status, chunk counts, delete. Opens **`ExtractionReview.vue`**: side-by-side PDF view, pass/fail validation checks, an editable copy compared against the machine original, and confirm. |
| **To-Do** | todorabbit Kanban: boards, colour-keyed columns, priority cards, drag-and-drop (`KanbanColumn`, `KanbanCard`). |
| **Search** | Proxied web search with expandable page fetch, a provider-health indicator, and server-backed history (`SearchHistoryPanel`: rename, delete, restore). |
| **JSON to …** | Fully client-side JSON → Excel-like HTML table. Nested arrays become merged (rowspan) cells; includes filters, paging and CSV/HTML export (`lib/jsontable*.js`). |
| **5 Lakhs** | Client-side novelty "joke cheque" generator. The amount is fixed and the NOVELTY watermark is always drawn, including in the PDF. History is kept in `localStorage` only. |
| **IELTS** | Library and authoring, a timed sitting, a review screen, and a weakness summary built from the mistake log. |
| **Component splitter** | Mirrors the backend pipeline (`PipelineStages`, `ProposedComponentCard`). Nothing is generated until **Generate** is clicked, and a failing validation report is shown prominently even when the request succeeded. |
| **Notes** | Markdown notes: pinned/unpinned lists, folders, search, autosave with conflict detection (409), trash and restore, share-link toggle. `PublicNote.vue` renders `/n/:token`. |
| **Sources** | Full editor for context sources (`ContextSourceManager`). The chat popover only toggles them. |
| **Logs** | Observability viewer: system-info strip, SSE live tail with pause and "N new", filtered history with keyset paging, a row-detail drawer (plain text, never `v-html`), stats cards, and audit entries (SystemAdmin). |
| **Access** | RBAC admin (SystemAdmin): paged user search, role selection and a tab checklist. The UI mirrors the server's guardrails but never decides them. |
| **Settings** | Account, connection, API base URL, model default, logout. A **Documentation** sub-view (`mostisko-docs/`) adds in-app docs per module (purpose, user guide, technical notes) with scroll-spy. |

### Service layer (`src/services/`, re-exported from `index.js`)
- **`http.js`** is the only place that knows the base URL. It handles:
  - attaching the bearer header
  - mapping JSON responses and errors (`AuthError` on 401 sends the user back to login; `HttpError` otherwise)
  - multipart uploads
  - `stream()` reads a response as it arrives
- One module per API area: `auth`, `session`, `chat` (NDJSON frame parsing), `model` (grouped list, saved choice, `supportsTools`), `conversation`, `document`, `context-source`, `search`, `todorabbit`, `notes`, `ielts`, `component-splitter` (streamed `analyse`), `rbac`, `logs` (client-event batching, SSE `openLogStream`).
- `environments/environment.js` reads `VITE_API_BASE_URL`, defaulting to `/api`.

---

## Integration contracts

- **Dev proxy:** Vite proxies `/api` → `http://localhost:5274`, so development is same-origin. In Docker the API serves the SPA itself on one port, so there is no CORS at all.
- **Chat streaming:** `/api/chat` returns one JSON frame per line (`content` / `thinking` / `tool` / `error` / `done`) over a plain fetch stream, not SSE. Workspace chat still streams raw `text/plain`. Both return `X-Conversation-Id` when a thread is persisted.
- **Tab keys:** the strings in `TabCatalog.All` and `MODULE_ROUTES` must match exactly. The server enforces them, and the UI only hides tabs.
- **RAG:** vectors are normalised when written, so a query is a bare dot product. Every chunk carries a `SourceRef` for `[file pN]` / `[file [Sheet]]` citations, and retrieval filters by owner before it scores anything.
- **Session:** login returns `{ username, token, created }`. The token lives in `localStorage` and every call sends `Authorization: Bearer <token>`. Any 401 clears the session.
- **Persistence:** the backend owns durable state. Only 5 Lakhs history, theme, model choice and similar UI conveniences are stored locally in the browser.

---

## Running and deploying

- **Dev:**
  - Start Ollama, with at least one chat model plus `ollama pull nomic-embed-text`.
  - In `backend/`, run `dotnet run` (port 5274; Swagger at `/swagger`).
  - In `frontend/`, run `npm run dev` (port 5173).
- **Docker** (`Dockerfile`, `docker-compose.yml`):
  - A multi-stage build (node → dotnet SDK → aspnet runtime) produces one image that serves the SPA and the API on **8080**.
  - SQLite lives on the named volume `mostisko-data` at `/data`.
  - The container runs as non-root, with a healthcheck on `/public/server/status`.
  - Secrets come from environment variables or `.env`, never from the image.
- **Fly.io** (`fly.toml`): a single machine (SQLite has one writer) with a persistent volume at `/data`, 1 GB RAM, region `sin`. Vercel can host only the frontend; see `DEPLOY.md`.

## In progress

- **Module launcher** (`frontend/docs/module-launcher/DISCOVERY.md`): a planned launcher panel to replace `ModuleTabBar`, opened with `g` then `m` and from the command palette. Phase 0 discovery is done, and the reference designs are still pending.
