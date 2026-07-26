# RabbitiChat — Project Summary

RabbitiChat is a self-hosted ChatGPT-style chat application. A Vue 3 single-page app talks to a .NET minimal API, which proxies chat requests to a locally running **Ollama** instance and persists users, conversations, and messages in SQLite. Everything runs on your own machine — the browser never talks to Ollama directly.

```
Browser (Vue 3 + Vite, port 5173)
        │  /api/* (Vite dev proxy)
        ▼
.NET Minimal API (RabbitIC.Api, port 5274)
        │  HTTP (streaming NDJSON)
        ▼
Ollama (local LLM server, port 11434)
        │
   SQLite (rabbitic.db) — users, conversations, messages
```

---

## Backend (`backend/` — RabbitIC.Api, ASP.NET Core)

**Stack:** .NET minimal APIs, Entity Framework Core with SQLite, `IHttpClientFactory` for the Ollama client (10-minute timeout for slow first model loads).

**Data model** (`Models/Entities.cs`):
- `User` — username, PBKDF2 secret hash + per-user salt, current bearer token.
- `Conversation` — owned by a user; every query is scoped to the owner.
- `Message` — role (`user` / `assistant` / `system`), content, timestamp.

The database is created on first run via `EnsureCreated()`, with an idempotent `SchemaUpgrade` step for older database files.

**Endpoints** (`MinimalApis/OllamaApis.cs`, `MinimalApis/PublicApis.cs`):

| Endpoint | Purpose |
|---|---|
| `POST /auth/login` | Login **or auto-register**: a new username creates an account; an existing one verifies the secret (constant-time compare). Issues a fresh bearer token each login. |
| `GET /models` | Proxies Ollama's `api/tags` to list installed models; returns 502 if Ollama is unreachable. |
| `GET /conversations` | Current user's conversations, newest first. |
| `GET /conversations/{id}` | One conversation with its ordered messages. |
| `DELETE /conversations/{id}` | Delete a conversation (user-scoped). |
| `POST /chat` | The core endpoint — see below. |
| `GET /`, `/server/status`, `/backend/products` | Simple health/status endpoints. |

**The `/chat` flow:** resolves or creates the conversation (title = first 40 chars of the message), saves the user message, loads the **full message history** as context (this is the chat "memory"), optionally prepends a system prompt, then POSTs to Ollama with `stream: true`. Ollama's newline-delimited JSON stream is parsed line-by-line and each content token is flushed to the browser immediately as plain text. The conversation id travels back in an `X-Conversation-Id` response header. When the stream completes, the full assistant reply is persisted.

**Auth model:** lightweight bearer tokens stored on the `User` row (no JWT/expiry) — `AuthHelper/Authorization.cs` handles PBKDF2 hashing, token generation, and resolving the user from the `Authorization` header. CORS is configured for several dev origins plus a Vercel deployment, exposing the `X-Conversation-Id` header.

**Config** (`appsettings.json`): Ollama URL (default `http://localhost:11434`), SQLite connection string, and a "RabbitiC" persona system prompt.

---

## Frontend (`frontend/` — Vue 3 + Vite)

**Stack:** Vue 3 (Composition API, single `App.vue`), Vite 8, `marked` + `DOMPurify` for safe Markdown rendering of replies. No router, no state library — refs and computeds only.

- **`src/lib/api.js`** — the entire backend client. Manages the session in `localStorage` (token + username), attaches the bearer token to requests, and throws a typed `AuthError` on 401 so the UI can bounce to the login gate. `streamChat()` reads the response body with a `ReaderStream` + `TextDecoder`, invoking an `onToken` callback per chunk, and picks the conversation id off the `X-Conversation-Id` header. Supports aborting via `AbortSignal`.
- **`src/App.vue`** — the whole UI: login gate (username + secret), sidebar of conversations (open/delete), model picker fed from `/models`, a connection status indicator, and the chat pane where assistant replies stream in token-by-token and render as sanitized Markdown.

---

## Integration

- **Dev proxy instead of CORS:** the frontend calls everything under `/api`; `vite.config.js` proxies `/api` → `http://localhost:5274`, keeping the browser same-origin in development. The backend's CORS policy covers non-proxied/deployed origins.
- **Streaming contract:** Ollama streams NDJSON → the backend extracts each `message.content` delta and re-streams it as raw `text/plain` → the frontend appends each decoded chunk to the in-progress assistant bubble. Metadata (which conversation) is out-of-band in the `X-Conversation-Id` header, which CORS explicitly exposes.
- **Session contract:** login returns `{ username, token, created }`; the token lives in `localStorage` and every authenticated call sends `Authorization: Bearer <token>`. Any 401 clears the session client-side.
- **Persistence contract:** the backend owns all state — the frontend never stores messages; it re-fetches a conversation to open it, and chat context/memory is rebuilt server-side from the database on each turn.

**Running it:** start Ollama (`ollama serve`, with at least one model pulled), run the backend (`dotnet run` in `backend/`, port 5274), run the frontend (`npm run dev` in `frontend/`, port 5173).
