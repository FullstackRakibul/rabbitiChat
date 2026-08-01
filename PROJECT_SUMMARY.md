# RabbitiChat — Project Summary

RabbitiChat is a self-hosted, ChatGPT-style assistant that has grown into a **garment-merchandiser workspace**: alongside general chat, you can upload purchase-order documents (PDF / XLSX / TXT / CSV), have them chunked and embedded into a local vector index, chat against them with citation-grounded RAG, and run **structured PO extraction through a human-in-the-loop pipeline** — buyer-template detection, deterministic parsing where a template is known, an LLM fallback otherwise, arithmetic validation of everything, and a side-by-side review screen where a person confirms (or corrects) the result before it's accepted. A Vue 3 single-page app talks to a .NET 8 minimal API, which proxies all model calls to a locally running **Ollama** instance and persists everything in SQLite. The browser never talks to Ollama directly.

```
Browser (Vue 3 + Vite + Tailwind/shadcn-style UI, port 5173)
        │  /api/* (Vite dev proxy or VITE_API_BASE_URL)
        ▼
.NET 8 Minimal API (RabbitIC.Api, port 5274)  ── Swagger UI at /swagger
        │  ├─ /api/chat, /api/models …            → Ollama /api/chat (streaming NDJSON)
        │  ├─ /api/documents (ingest/RAG/extract) → Ollama /api/embeddings + /api/chat
        │  ├─ /api/buyer-profiles                 → template-routing CRUD + detection dry-run
        │  ├─ in-memory VectorIndex (SIMD cosine search, warmed from SQLite at startup)
        │  └─ data/uploads/ — stored original files (deterministic re-parse + review pane)
        ▼
Ollama (local LLM server, port 11434) — chat models + nomic-embed-text
        │
   SQLite (rabbitic.db) — users, conversations, messages, documents, embedding
                          chunks, buyer profiles, extraction runs (audit trail)
```

---

## Backend (`backend/` — RabbitIC.Api, ASP.NET Core, .NET 8)

**Stack:** minimal APIs, EF Core + SQLite, `IHttpClientFactory` for the Ollama client (10-minute timeout for slow first model loads), **PdfPig** (PDF text extraction), **ClosedXML** (XLSX extraction), **System.Numerics.Tensors** (SIMD vector math), Swashbuckle (Swagger UI with bearer-token auth, enabled in all environments).

**Routing** (`Middleware/MiddlewareDependencyInversion.cs`): endpoint groups are mounted via extension methods — Ollama/chat APIs, Document APIs and Buyer-profile APIs under **`/api`**, health/status APIs under **`/public`**.

**Data model** (`Models/Entities.cs`, `Models/BuyerProfile.cs`):
- `User` — username, PBKDF2 secret hash + per-user salt, current bearer token.
- `Conversation` / `Message` — regular chat history, owned by a user; every query is scoped to the owner.
- `Document` — an uploaded PO file: filename, optional **buyer** tag, status (`processing` / `ready` / `failed`), error text, **`FilePath`** (the stored original under `data/uploads/{id}{ext}` — null for legacy uploads that predate file persistence), a cached **`ExtractedJson`/`ExtractedAt`** (last extraction, re-displayed without re-running the model), and **`ConversationId`** (the document's own workspace thread).
- `EmbeddingChunk` — one embedded text chunk of a document: owner, filterable metadata (buyer, style number), a `SourceRef` for citations (e.g. `15901248.pdf p2`), the text, and the **L2-normalised float32 vector stored as a raw BLOB**.
- `BuyerProfile` — the routing table between an uploaded PO and the parser that understands its layout, held in the DB so adding a buyer is a row, not a deploy: unique name, a pipe-separated **`DetectPattern`** of markers matched against page-1 text + PDF producer, a **`ParserKey`** (`GenericLlm` = model fallback), chunking strategy, active flag. A "Kohls" profile is seeded on first run.
- `ExtractionRun` — the audit trail: every extraction attempt is recorded with parser key, status, the verbatim machine `ResultJson`, failed check names, and — after human sign-off — `ConfirmedJson`, `EditsJson`, who confirmed and when. The machine output is never overwritten; corrected and original stay side by side.

The database is created on first run via `EnsureCreated()`, with an idempotent `SchemaUpgrade` step for older database files (adds the extraction tables/columns and seeds the Kohls profile).

**Retrieval layer** (`Retrieval/VectorStore.cs`):
- `EmbeddingService` — calls Ollama `/api/embeddings` (default model `nomic-embed-text`, configurable) and returns L2-normalised vectors.
- `VectorIndex` — a singleton, in-RAM index (768 dims ≈ 3 KB/chunk) guarded by a `ReaderWriterLockSlim`, warmed from SQLite at startup. Search runs a **metadata gate first** (owner, buyer, style number — a security control, not an optimisation), then SIMD dot-product similarity into a top-K min-heap with a minimum-score cutoff. SQLite is the durable store; RAM is the query path.

**Extraction pipeline** (`Extraction/`) — detect → parse → validate → gate:
- `ExtractionDispatcher` — probes page 1 of the stored PDF (text + producer metadata) against every active `BuyerProfile.DetectPattern`; a **majority of markers is required** before a template is trusted, otherwise it falls through to the model. When a profile matches and a deterministic parser is registered for its `ParserKey`, that parser runs **first** — flattened text destroys table geometry, and no prompt recovers a space that was never in the input. The LLM path covers unknown templates, non-PDF files, legacy documents with no stored original, and template drift (a parser throw falls back with an explanatory note rather than failing).
- `IPoParser` — the contract deterministic buyer-template parsers implement (`Key`, `Detect`, `Parse`); registered implementations are discovered via DI. No concrete parser ships yet — the seeded Kohls profile names `KohlsSps`, which routes to the model with a note until that parser lands.
- `LlmPoFallback` — sends full page text (never chunks — chunk boundaries split line-item tables, input capped at 60 k chars) to Ollama with `format:"json"`, temperature 0 and a strict schema + rules prompt (split concatenated style/UPC cells, prepack quantities count packs, never invent numbers). The JSON is mapped defensively (numbers-as-strings, missing arrays, invented nulls).
- `ExtractionValidator` — runs on **both** paths, so neither bypasses the arithmetic: derives totals from the prepack rows (pieces per pack, total pieces, computed unit price, per-size breakdown), then cross-checks them against the printed numbers — style-number/UPC merge detection (the fingerprint of coordinate-blind text extraction), UPC format, prepack structure, unit-price / line-total / PO-total reconciliation to a 1-cent tolerance. Anything unverifiable is a *failed* check, never a silent skip.
- `ExtractionResult` — the shared shape (header, order line, prepack components, derived totals, validation checks) serialized camelCase straight to the review UI.

**The status rule that matters most:** model output is never `"ok"`. A deterministic parse with every check passing is the only path to a clean status; everything the model produces lands in `needs_review` for a human, even when the numbers reconcile.

**Endpoints:**

| Endpoint | Purpose |
|---|---|
| `POST /api/auth/login` | Login **or auto-register**; constant-time secret compare; fresh bearer token each login. |
| `GET /api/models` | Proxies Ollama's `api/tags`; 502 if Ollama is unreachable. |
| `GET/DELETE /api/conversations…` | List / open / delete the current user's conversations. |
| `POST /api/chat` | Persistent streaming chat (see below). |
| `POST /api/documents` | Upload a PO (≤ 20 MB, pdf/xlsx/txt/csv): **persist the original to `data/uploads/`** (saved before text extraction, so a failed ingest still leaves the file recoverable) → extract text → chunk (~800 chars, ~100 overlap, cut on line boundaries) → embed → store + index. Returns chunk count. |
| `GET/DELETE /api/documents…` | List documents (with status, chunk counts, workspace `conversationId`, `hasExtraction`); delete removes DB rows by cascade, evicts from the in-memory index, and best-effort deletes the stored file. |
| `GET /api/documents/{id}/file` | Streams the stored original inline (the review screen's source pane); 410 for legacy documents whose file was never kept. |
| `POST /api/documents/{id}/extract` | Runs the extraction pipeline above. Optional `?buyerProfileId=` forces a template instead of auto-detect; optional body `model` picks the fallback chat model. Caches the result on the document, records an `ExtractionRun`, and returns `{ runId, status, parser, buyer, detectConfidence, note, checks, failedChecks, data }` — 422 when status is `failed`. |
| `GET /api/documents/{id}/extract` | The stored extraction (204 if none) — re-display without re-running the model. |
| `POST /api/documents/{id}/extraction/confirm` | Human sign-off: stores the reviewed (possibly edited) payload and the list of changed field paths on the latest run, and promotes it to the document's display cache. |
| `GET/POST/PUT/DELETE /api/buyer-profiles…` | CRUD for buyer profiles, plus `GET …/parsers` (registered parser keys for the UI) and `POST …/detect/{documentId}` (detection dry-run for tuning a `DetectPattern` — no parsing, no model call). |
| `POST /api/documents/workspace-chat` | **RAG chat**, streamed. Context is either the whole pinned document or a top-6 vector search over the user's chunks. The system prompt orders the model to answer only from the excerpts and cite `[source]` refs. A pinned document gets its own persisted conversation ("Workspace · {file}", id in `X-Conversation-Id`); unpinned questions stay ephemeral. |
| `GET /public/…` | Simple health/status endpoints. |

Both chat endpoints reject **embedding models** (name heuristics: `*embed*`, `bge-`, `gte-`, `e5-`) with a clear error, since Ollama lists them alongside chat models but they can't chat.

**The `/api/chat` flow:** resolves or creates the conversation (title = first 40 chars), saves the user message, loads the **full message history** as context, optionally prepends a system prompt, then POSTs to Ollama with `stream: true`. The NDJSON stream is parsed line-by-line and each content token is flushed to the browser as plain text; the conversation id travels back in an `X-Conversation-Id` header; the full reply is persisted when the stream completes.

**Auth model:** lightweight bearer tokens stored on the `User` row (no JWT/expiry) — `AuthHelper/Authorization.cs` handles PBKDF2 hashing, token generation, and resolving the user from the `Authorization` header. CORS covers several dev/LAN origins plus a Vercel deployment, exposing `X-Conversation-Id`.

**Config** (`appsettings.json`): Ollama URL (currently a LAN host, `http://192.168.61.41:11434`), `Ollama:EmbeddingModel` (`nomic-embed-text`), `Ollama:ExtractionModel` / `Ollama:DefaultModel` (fallback-extraction model chain, final default `llama3`), SQLite connection string, and a "RabbitiC" persona system prompt. `backend/data/` (uploaded customer POs) is gitignored.

---

## Frontend (`frontend/` — Vue 3 + Vite + Tailwind)

**Stack:** Vue 3 (Composition API), Vite 8, **Tailwind CSS v4** with a shadcn-style component kit (`src/components/ui/` — button, card, dialog, tabs, select, switch, checkbox, badge, input, textarea, label, separator — built on **reka-ui**, `class-variance-authority`, `tailwind-merge`), **@lucide/vue** icons, `marked` + `DOMPurify` for safe Markdown rendering, **pdfjs-dist** (lazy-loaded) to render the source PDF in the review screen. No router, no state library — `App.vue` owns all state via refs/computeds and passes it down through props/emits.

**Service layer** (`src/services/`) — replaced the old single `api.js`:
- `http.js` — the one place for base URL, bearer header, JSON/error mapping (typed `AuthError` on 401 bounces the UI to login, `HttpError` otherwise), multipart uploads, `raw()` for endpoints that may answer 204 or binary, and `stream()` which reads a response body chunk-by-chunk via `ReaderStream` + `TextDecoder`.
- `auth.service.js` / `session.service.js` — login and the `localStorage` session (token + username).
- `chat.service.js` — models list, `streamChat()` (picks the conversation id off `X-Conversation-Id`, supports `AbortSignal`).
- `conversation.service.js` — list/open/delete conversations.
- `document.service.js` — upload / list / delete documents, streaming `workspaceChat()`, and the extraction surface: `extractDocument(id, { model, buyerProfileId })`, `getExtraction()` (stored result or null), `fetchDocumentFile()` (the original as a blob, auth header intact), `confirmExtraction()` (edited payload + changed field paths), `listBuyerProfiles()`.
- `environments/environment.js` — `apiBaseUrl` from `VITE_API_BASE_URL`, defaulting to `/api`.

**UI layout** (`App.vue` + `src/components/`): a login gate (`LoginGate.vue`), a responsive sidebar (`AppSidebar.vue` — mobile drawer, conversation list grouped by recency, sources list, clear-all, logout), a top bar with a connection dot and a **light/dark theme toggle** (`composables/useTheme.js`), and a **six-tab workspace** (tabs stay mounted across switches so streaming replies survive navigation):

| Tab | What it does |
|---|---|
| **Chat** | The classic persistent chat: model picker, context selector, streaming Markdown replies, abort, auto-scroll. |
| **Workspace** | The agentic PO flow: pick a buyer tag (Kohls, Walmart, Target, H&M, Generic — a local list; real profiles live in the backend and drive the review screen), upload a PO (inline event log: uploading → chunked/embedded → ready), then **RAG-chat against the pinned document** or hit **Extract**, which opens the review overlay. Pinning an already-ingested document **restores its saved workspace thread** from the document's conversation. |
| **Documents** | Upload/manage the ingested document library: buyer tag, status, chunk counts, delete — and jump straight into extraction review. |
| **To-Do** | Local-only task list with all/active/completed filters. |
| **Sources** | Local-only toggleable source list with search (placeholder for future integrations). |
| **Settings** | Account info, connection status, API base URL, model + context defaults, logout. |

`NewChatDialog.vue` wraps new-chat creation (pick context + sources before starting).

**`ExtractionReview.vue`** — the human-in-the-loop gate, a full-screen overlay opened from Workspace or Documents:
- **Left pane:** the stored original rendered with pdf.js (page nav + zoom; the pdf.js document is deliberately kept out of Vue's reactive state). Legacy documents without a stored file get the 410 explanation instead.
- **Right pane:** the extraction outcome — status badge, parser, detect confidence, buyer-profile selector ("Auto-detect" or force a template) — over an **editable field registry** (header, order line, prepack component table). Every field tracks dirty state against the untouched machine output, with per-field and reset-all reverts; derived totals re-compute live, and client-side checks re-run as the reviewer types.
- **Confirm** requires all live checks to pass, or an explicit override with a written reason; it posts the edited payload plus the list of changed field paths to the confirm endpoint, so machine vs. human stays auditable.

---

## Integration

- **Dev proxy instead of CORS:** the frontend calls everything under `/api`; Vite proxies `/api` → `http://localhost:5274`, keeping the browser same-origin in development. The backend's CORS policy covers non-proxied/deployed origins. `VITE_API_BASE_URL` can point a build at any backend.
- **Streaming contract:** Ollama streams NDJSON → the backend extracts each `message.content` delta and re-streams it as raw `text/plain` → the frontend appends each decoded chunk to the in-progress assistant bubble. Conversation identity travels out-of-band in the `X-Conversation-Id` header — for `/api/chat` always, for workspace chat when a document is pinned (unpinned questions have no id and are ephemeral).
- **RAG contract:** ingestion normalises vectors at write time so query-time similarity is a bare SIMD dot product; every chunk carries a `SourceRef` so answers can cite `[file pN]` / `[file [Sheet]]`; retrieval always filters by owner before scoring.
- **Extraction contract:** `status: "ok"` can only come from a deterministic parser with every validation check passing; model-derived results are always `needs_review`. Every run is an `ExtractionRun` audit row; human confirmation stores the corrected payload *beside* the machine output, never over it. The document's `ExtractedJson` cache means re-opening a result never re-runs the model.
- **Session contract:** login returns `{ username, token, created }`; the token lives in `localStorage` and every call sends `Authorization: Bearer <token>`. Any 401 clears the session client-side.
- **Persistence contract:** the backend owns all durable state — chat context is rebuilt server-side from the database each turn; the vector index is rebuilt from SQLite at startup; uploaded originals live in `backend/data/uploads/` beside the DB; each document's workspace thread is a real conversation. To-Do, Sources and context selection are UI-local for now.

**Running it:** start Ollama (`ollama serve`, with at least one chat model plus `ollama pull nomic-embed-text`), run the backend (`dotnet run` in `backend/`, port 5274 — Swagger at `/swagger`), run the frontend (`npm run dev` in `frontend/`, port 5173).
