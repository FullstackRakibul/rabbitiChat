# Mostisko (RabbitiChat)

A self-hosted workspace that started as a chat client for [Ollama](https://ollama.com) and
grew into a set of tools around it: document RAG, purchase-order extraction, a Kanban board,
web search, a JSON→table generator, IELTS reading practice, and role-based access control.

A Vue 3 SPA talks to an ASP.NET Core minimal API. **The browser never talks to a model
provider directly** — every model call goes through the backend, so API keys stay server-side.
Everything persists in SQLite.

```
Vue 3 SPA  ──/api──▶  ASP.NET Core 8 API  ──▶  Ollama (local, 11434)
                              │             ──▶  Ollama Cloud
                              │             ──▶  OpenAI-compatible providers
                              ▼
                        rabbitic.db (SQLite)
```

---

## Prerequisites

| | |
|---|---|
| .NET SDK | 8.0 |
| Node.js | 18+ |
| Ollama | optional — only for local models (`ollama pull llama3`) |

Cloud providers work without a local Ollama; local models work without any API key.

---

## Running it

### Development (two terminals)

```bash
# terminal 1 — API on http://localhost:5274
cd backend
dotnet restore
dotnet run

# terminal 2 — SPA on http://localhost:5173
cd frontend
npm install
npm run dev
```

Open <http://localhost:5173>.

> **Note on the dev proxy.** `frontend/vite.config.js` currently forwards `/api` to
> **`http://localhost:8080`** (the Docker port), not to `dotnet run`'s `5274`. If you run the
> two halves separately, switch that line to `http://localhost:5274` — the alternatives are
> already there, commented out.

### Docker (single port)

One image serves the SPA and the API together on **8080**, same-origin, no CORS. See
[DEPLOY.md](DEPLOY.md).

### First run

The database is created automatically and a built-in administrator is seeded:

| Username | Secret |
|---|---|
| `mostisko@admin` | `q12wQ!@W` |

**Change this before exposing the app to anything.** The seeder only sets the secret when it
creates the account — if you change the password, restarts leave it alone.

Two demo IELTS reading tests are also seeded, shared and read-only.

---

## Configuration

### Backend — `backend/appsettings.json`

```jsonc
{
  "ConnectionStrings": { "Default": "Data Source=Data/rabbitic.db" },

  "Ollama": {
    "Url": "http://localhost:11434",
    "EmbeddingModel": "nomic-embed-text",
    "SystemPrompt": "…the assistant's persona…"
  },

  "OllamaCloud":       { "ApiKey": "", "Models": [ … ] },
  "OpenAiCompatible":  { "Providers": [ { "Key": "openrouter", "BaseUrl": "…", "ApiKey": "" } ] }
}
```

**Never commit a real API key.** Use user-secrets or environment variables:

```bash
dotnet user-secrets set "OllamaCloud:ApiKey" "…"
dotnet user-secrets set "OpenAiCompatible:Providers:0:ApiKey" "…"
```

A provider with an empty key is skipped at startup rather than failing — the picker simply
shows fewer models.

### Frontend — `frontend/.env`

```bash
VITE_API_BASE_URL=/api      # same-origin; override for a split deployment
```

---

## What's in it

| Tab | What it does |
|---|---|
| **Dashboard** | Landing view and quick actions |
| **Chat** | Streaming chat with model picker, per-message web search, reasoning ("Think") for capable models, and context sources |
| **Workspace** | RAG chat grounded on an uploaded purchase order, with citations |
| **Documents** | Upload PDF/XLSX/TXT/CSV → chunk → embed; structured PO extraction with human review |
| **To-Do** | Kanban boards (todorabbit) |
| **Search** | Web search proxied server-side, with a searchable history |
| **JSON to …** | JSON → HTML table with merged (rowspan) nested arrays, filters, and CSV/HTML export |
| **5Lakhs** | A novelty joke-cheque generator — permanently watermarked, no monetary value |
| **IELTS** | Reading practice: author tests, sit them on a timer, deterministic scoring, and a mistake log that tracks what keeps going wrong |
| **Sources** | Named blocks of text prepended to a chat's system prompt |
| **Access** | RBAC admin — roles and per-tab grants (SystemAdmin only) |
| **Settings** | Connection status, model default, theme |

### Model sources

Registered behind one `IChatModelSource` abstraction, so adding a provider is a registration
rather than an edit to the chat pipeline:

- **`local`** — Ollama on your machine or LAN. Capabilities (`tools`, `thinking`, context
  length) are read from `/api/show`, not guessed.
- **`cloud`** — Ollama's hosted models (metered).
- **OpenAI-compatible** — any provider speaking `/chat/completions`, configured per block.
- **`echo`** — a diagnostic source, enabled with `ModelSources:EnableEcho`.

---

## Access control

Three roles, each a superset of the last, so a promotion can never remove a tab:

| Role | Tabs |
|---|---|
| **General** | dashboard, todo, jsonto, 5lakhs, sources, settings |
| **System** | General **+ chat, workspace, docs, search, ielts** |
| **SystemAdmin** | everything, including the Access tab |

Explicit per-user grants add to the role baseline; they never subtract. Enforcement is
server-side in `TabAccessMiddleware` — hiding a tab in the UI is presentation, not a control.

Note that **chat is not in the General baseline**: `/api/chat` and `/api/conversations` are
gated behind the `chat` tab, so a plain account needs either the System role or an explicit
grant before it can hold a conversation.

The role tiers live in `backend/Rbac/TabCatalog.cs` and the gated path prefixes in
`TabAccessMiddleware.cs`; both are one-line edits.

**Ownership.** Conversations, documents, boards, sources, search history, IELTS tests,
attempts and mistakes are all owner-scoped. A request for someone else's id returns **404,
not 403** — a 403 would confirm the row exists.

---

## API reference

All routes are under `/api`. Everything except `/auth/login` and `/public/*` needs
`Authorization: Bearer <token>`.

<details>
<summary><b>Auth, chat and models</b></summary>

| Method | Route | Purpose |
|---|---|---|
| POST | `/auth/login` | Log in, or auto-register a new username |
| GET | `/models` | Every model from every configured source |
| GET | `/me/tabs` | The caller's role and effective tabs |
| POST | `/chat` | Send a message; streams NDJSON frames (`content`, `thinking`, `tool`, `done`) |
| GET | `/conversations` | List (newest first) |
| GET · DELETE | `/conversations/{id}` | One conversation with messages · delete |
</details>

<details>
<summary><b>Documents and extraction</b></summary>

| Method | Route | Purpose |
|---|---|---|
| GET · POST | `/documents` | List · upload (chunks + embeds) |
| DELETE | `/documents/{id}` | Delete a document and its chunks |
| GET | `/documents/{id}/file` | The original upload |
| POST · GET | `/documents/{id}/extract` | Run extraction · read the last result |
| POST | `/documents/{id}/extraction/confirm` | Human sign-off |
| POST | `/documents/workspace-chat` | RAG chat grounded on chunks |
| GET · POST · PUT · DELETE | `/buyer-profiles[/{id}]` | Parser routing rules |
| POST | `/buyer-profiles/detect/{documentId}` | Detection dry-run |
</details>

<details>
<summary><b>Search, sources and boards</b></summary>

| Method | Route | Purpose |
|---|---|---|
| POST | `/search` · `/search/fetch` | Proxied web search · single-page fetch |
| GET | `/search/health` | Is search usable right now |
| GET · PUT · DELETE | `/search/history[/{id}]` | History (soft delete) |
| POST | `/search/history/{id}/restore` | Undo a delete |
| GET · POST · PUT · DELETE | `/context-sources[/{id}]` | Prompt context blocks |
| PUT | `/context-sources/reorder` | Reorder |
| GET · POST · PUT · DELETE | `/boards[/{id}]` | Kanban boards |
| POST · PUT · DELETE | `/columns`, `/cards` | Columns and cards, incl. `/cards/{id}/move` |
</details>

<details>
<summary><b>IELTS</b></summary>

| Method | Route | Purpose |
|---|---|---|
| GET · POST · PUT · DELETE | `/ielts/reading/tests[/{id}]` | Author reading tests |
| POST · PUT · DELETE | `/ielts/reading/…/questions` | Manage questions |
| POST | `/ielts/reading/tests/{id}/attempts` | Start a sitting (no answer keys returned) |
| POST | `/ielts/reading/attempts/{id}/submit` | Score deterministically |
| GET | `/ielts/reading/attempts[/{id}]` | History · full review |
| GET | `/ielts/mistakes` · `/ielts/mistakes/summary` | Mistake log · weakness breakdown |
| POST | `/ielts/mistakes/{id}/reviewed` · `/tag` | Spaced review · diagnose |
</details>

<details>
<summary><b>Admin (SystemAdmin only)</b></summary>

| Method | Route | Purpose |
|---|---|---|
| GET | `/admin/rbac/tabs` | Known tab keys |
| GET | `/admin/rbac/users[/{id}]` | Users and their grants |
| PUT | `/admin/rbac/users/{id}/role` · `/tabs` | Set role · replace grants |
</details>

---

## How chat streaming works

1. The SPA POSTs `{ conversationId, source, model, message, webSearch, think, activeSourceIds }`.
2. The API saves the user turn, assembles the system prompt from the active context sources,
   and calls the chosen provider with `stream: true`.
3. Each delta is written back as **one tagged NDJSON frame per line** — `content`, `thinking`,
   `tool`, `error`, `done` — so reasoning and tool progress stay distinguishable from the answer.
4. Tool-capable models may call `web_search`; the backend runs it and feeds the results back.
5. The finished reply is saved. The conversation id returns in `X-Conversation-Id`.

Stopping mid-stream keeps the partial reply, on both the client and the server.

---

## Project layout

```
backend/                  ASP.NET Core 8 minimal API
  Program.cs              composition root: DI, middleware, endpoint groups
  MinimalApis/            one file per route group
  Services/               business logic behind interfaces
  Models/                 EF Core entities
  Data/                   AppDbContext + SchemaUpgrade
  ModelSources/           Ollama / cloud / OpenAI-compatible adapters
  Rbac/                   tab catalogue, role baselines, admin seeder
  Ielts/                  IELTS reading seeder
  Retrieval/              embeddings + in-memory vector index
  Extraction/             PO parsers and the LLM fallback

frontend/                 Vue 3 + Vite
  src/App.vue             shell: tabs, permissions, chat state
  src/components/tabs/    one component per tab
  src/components/ui/      reka-ui primitives (shadcn-style)
  src/services/           one module per API area, all through http.js
  src/lib/                markdown, JSON→table, date formatting
```

### Schema migrations

The project uses `EnsureCreated()` plus a hand-written, idempotent `SchemaUpgrade` rather than
EF migrations. Every step checks before it acts (`TableExists`, `ColumnExists`), so it is safe
on every startup and upgrades an existing database in place without data loss.

---

## Notes

- Delete `backend/rabbitic.db` to reset everything.
- CORS origins are listed in `Program.cs`; the Docker build avoids the issue by being same-origin.
- The JSON→table and 5Lakhs tabs are **fully client-side** — nothing they process is uploaded.
- Reading scoring never calls a model: it is a normalised string comparison, so a mark is
  reproducible and a disputed one can be traced to a single comparison.

## See also

- [DEPLOY.md](DEPLOY.md) — Docker build and deployment
- [PROJECT_SUMMARY.md](PROJECT_SUMMARY.md) — architecture narrative
- [SECURITY.md](SECURITY.md) — reporting vulnerabilities
