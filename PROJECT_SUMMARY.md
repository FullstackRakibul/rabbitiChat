# RabbitiChat — Project Summary

RabbitiChat is a self-hosted, ChatGPT-style assistant that has grown into a **garment-merchandiser workspace**: alongside general chat, you can upload purchase-order documents (PDF / XLSX / TXT / CSV), have them chunked and embedded into a local vector index, chat against them with citation-grounded RAG, and extract structured PO JSON. A Vue 3 single-page app talks to a .NET 8 minimal API, which proxies all model calls to a locally running **Ollama** instance and persists everything in SQLite. The browser never talks to Ollama directly.

```
Browser (Vue 3 + Vite + Tailwind/shadcn-style UI, port 5173)
        │  /api/* (Vite dev proxy or VITE_API_BASE_URL)
        ▼
.NET 8 Minimal API (RabbitIC.Api, port 5274)  ── Swagger UI at /swagger
        │  ├─ /api/chat, /api/models …            → Ollama /api/chat (streaming NDJSON)
        │  ├─ /api/documents (ingest/extract/RAG) → Ollama /api/embeddings + /api/chat
        │  └─ in-memory VectorIndex (SIMD cosine search, warmed from SQLite at startup)
        ▼
Ollama (local LLM server, port 11434) — chat models + nomic-embed-text
        │
   SQLite (rabbitic.db) — users, conversations, messages, documents, embedding chunks
```

---

## Backend (`backend/` — RabbitIC.Api, ASP.NET Core, .NET 8)

**Stack:** minimal APIs, EF Core + SQLite, `IHttpClientFactory` for the Ollama client (10-minute timeout for slow first model loads), **PdfPig** (PDF text extraction), **ClosedXML** (XLSX extraction), **System.Numerics.Tensors** (SIMD vector math), Swashbuckle (Swagger UI with bearer-token auth, enabled in all environments).

**Routing** (`Middleware/MiddlewareDependencyInversion.cs`): endpoint groups are mounted via extension methods — Ollama/chat APIs and Document APIs under **`/api`**, health/status APIs under **`/public`**.

**Data model** (`Models/Entities.cs`):
- `User` — username, PBKDF2 secret hash + per-user salt, current bearer token.
- `Conversation` / `Message` — regular chat history, owned by a user; every query is scoped to the owner.
- `Document` — an uploaded PO file: filename, optional **buyer** profile (e.g. "Kohls"), status (`processing` / `ready` / `failed`) and error text.
- `EmbeddingChunk` — one embedded text chunk of a document: owner, filterable metadata (buyer, style number), a `SourceRef` for citations (e.g. `15901248.pdf p2`), the text, and the **L2-normalised float32 vector stored as a raw BLOB**.

The database is created on first run via `EnsureCreated()`, with an idempotent `SchemaUpgrade` step for older database files.

**Retrieval layer** (`Retrieval/VectorStore.cs`):
- `EmbeddingService` — calls Ollama `/api/embeddings` (default model `nomic-embed-text`, configurable) and returns L2-normalised vectors.
- `VectorIndex` — a singleton, in-RAM index (768 dims ≈ 3 KB/chunk) guarded by a `ReaderWriterLockSlim`, warmed from SQLite at startup. Search runs a **metadata gate first** (owner, buyer, style number — a security control, not an optimisation), then SIMD dot-product similarity into a top-K min-heap with a minimum-score cutoff. SQLite is the durable store; RAM is the query path.

**Endpoints:**

| Endpoint | Purpose |
|---|---|
| `POST /api/auth/login` | Login **or auto-register**; constant-time secret compare; fresh bearer token each login. |
| `GET /api/models` | Proxies Ollama's `api/tags`; 502 if Ollama is unreachable. |
| `GET/DELETE /api/conversations…` | List / open / delete the current user's conversations. |
| `POST /api/chat` | Persistent streaming chat (see below). |
| `POST /api/documents` | Upload a PO (≤ 20 MB, pdf/xlsx/txt/csv): extract text → chunk (~800 chars, ~100 overlap, cut on line boundaries) → embed → store + index. Returns chunk count. |
| `GET/DELETE /api/documents…` | List documents (with status + chunk counts); delete removes DB rows by cascade and evicts from the in-memory index. |
| `POST /api/documents/{id}/extract` | Structured PO extraction: feeds **all** of the document's chunks (not retrieval, so no line item is missed) to Ollama with `format:"json"` and temperature 0, against a fixed PO schema (po_number, ship window, line items with prepack/assortment rules, …). |
| `POST /api/documents/workspace-chat` | **Stateless RAG chat**, streamed. Context is either the whole pinned document or a top-6 vector search over the user's chunks. The system prompt orders the model to answer only from the excerpts and cite `[source]` refs. Nothing is saved to Conversations. |
| `GET /public/…` | Simple health/status endpoints. |

Both chat endpoints reject **embedding models** (name heuristics: `*embed*`, `bge-`, `gte-`, `e5-`) with a clear error, since Ollama lists them alongside chat models but they can't chat.

**The `/api/chat` flow:** resolves or creates the conversation (title = first 40 chars), saves the user message, loads the **full message history** as context, optionally prepends a system prompt, then POSTs to Ollama with `stream: true`. The NDJSON stream is parsed line-by-line and each content token is flushed to the browser as plain text; the conversation id travels back in an `X-Conversation-Id` header; the full reply is persisted when the stream completes.

**Auth model:** lightweight bearer tokens stored on the `User` row (no JWT/expiry) — `AuthHelper/Authorization.cs` handles PBKDF2 hashing, token generation, and resolving the user from the `Authorization` header. CORS covers several dev/LAN origins plus a Vercel deployment, exposing `X-Conversation-Id`.

**Config** (`appsettings.json`): Ollama URL (currently a LAN host, `http://192.168.61.41:11434`), `Ollama:EmbeddingModel` (`nomic-embed-text`), SQLite connection string, and a "RabbitiC" persona system prompt.

---

## Frontend (`frontend/` — Vue 3 + Vite + Tailwind)

**Stack:** Vue 3 (Composition API), Vite 8, **Tailwind CSS v4** with a shadcn-style component kit (`src/components/ui/` — button, card, dialog, tabs, select, switch, checkbox, badge, input, textarea, label, separator — built on **reka-ui**, `class-variance-authority`, `tailwind-merge`), **@lucide/vue** icons, `marked` + `DOMPurify` for safe Markdown rendering. No router, no state library — `App.vue` owns all state via refs/computeds and passes it down through props/emits.

**Service layer** (`src/services/`) — replaced the old single `api.js`:
- `http.js` — the one place for base URL, bearer header, JSON/error mapping (typed `AuthError` on 401 bounces the UI to login, `HttpError` otherwise), multipart uploads, and `stream()` which reads a response body chunk-by-chunk via `ReaderStream` + `TextDecoder`.
- `auth.service.js` / `session.service.js` — login and the `localStorage` session (token + username).
- `chat.service.js` — models list, `streamChat()` (picks the conversation id off `X-Conversation-Id`, supports `AbortSignal`).
- `conversation.service.js` — list/open/delete conversations.
- `document.service.js` — upload / list / delete documents, `extractDocument()`, and streaming `workspaceChat()`.
- `environments/environment.js` — `apiBaseUrl` from `VITE_API_BASE_URL`, defaulting to `/api`.

**UI layout** (`App.vue` + `src/components/`): a login gate (`LoginGate.vue`), a responsive sidebar (`AppSidebar.vue` — mobile drawer, conversation list grouped by recency, sources list, clear-all, logout), a top bar with a connection dot and a **light/dark theme toggle** (`composables/useTheme.js`), and a **six-tab workspace** (tabs stay mounted across switches so streaming replies survive navigation):

| Tab | What it does |
|---|---|
| **Chat** | The classic persistent chat: model picker, context selector, streaming Markdown replies, abort, auto-scroll. |
| **Workspace** | The agentic PO flow: pick a buyer profile (Kohls, Walmart, Target, H&M, Generic — local-only for now), upload a PO (inline event log: uploading → chunked/embedded → ready), then **RAG-chat against the pinned document** or run one-click **Extract JSON** (rendered as a fenced code block). Stateless — never saved to conversation history. |
| **Documents** | Upload/manage the ingested document library: buyer tag, status, chunk counts, delete. |
| **To-Do** | Local-only task list with all/active/completed filters. |
| **Sources** | Local-only toggleable source list with search (placeholder for future integrations). |
| **Settings** | Account info, connection status, API base URL, model + context defaults, logout. |

`NewChatDialog.vue` wraps new-chat creation (pick context + sources before starting).

---

## Integration

- **Dev proxy instead of CORS:** the frontend calls everything under `/api`; Vite proxies `/api` → `http://localhost:5274`, keeping the browser same-origin in development. The backend's CORS policy covers non-proxied/deployed origins. `VITE_API_BASE_URL` can point a build at any backend.
- **Streaming contract:** Ollama streams NDJSON → the backend extracts each `message.content` delta and re-streams it as raw `text/plain` → the frontend appends each decoded chunk to the in-progress assistant bubble. For `/api/chat`, conversation identity is out-of-band in the `X-Conversation-Id` header; workspace chat is stateless and has no id.
- **RAG contract:** ingestion normalises vectors at write time so query-time similarity is a bare SIMD dot product; every chunk carries a `SourceRef` so answers can cite `[file pN]` / `[file [Sheet]]`; retrieval always filters by owner before scoring.
- **Session contract:** login returns `{ username, token, created }`; the token lives in `localStorage` and every call sends `Authorization: Bearer <token>`. Any 401 clears the session client-side.
- **Persistence contract:** the backend owns all durable state — chat context is rebuilt server-side from the database each turn; the vector index is rebuilt from SQLite at startup. To-Do, Sources, buyer profiles and context selection are UI-local for now.

**Running it:** start Ollama (`ollama serve`, with at least one chat model plus `ollama pull nomic-embed-text`), run the backend (`dotnet run` in `backend/`, port 5274 — Swagger at `/swagger`), run the frontend (`npm run dev` in `frontend/`, port 5173).
