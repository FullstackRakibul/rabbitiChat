# RabbitIC — .NET 8 + SQLite + Vue chat over Ollama

A local-first chat app. A Vue SPA talks to an ASP.NET Core API, which is the only thing
that talks to Ollama. Conversations are saved in SQLite, so history survives reloads and is
shared by any device that reaches the API.

```
Vue SPA (5173)  ──/api──▶  ASP.NET Core API (5274)  ──▶  Ollama (11434)
                                     │
                                     ▼
                                rabbitic.db  (SQLite)
```

## Prerequisites

- .NET 8 SDK
- Node.js 18+ (for Vite/Vue)
- Ollama running with at least one model pulled, e.g. `ollama pull llama3`

## Project layout

```
rabbitic/
  backend/        ASP.NET Core minimal API (the "orchestrator")
    Program.cs            endpoints + streaming proxy
    Data/                 EF Core DbContext
    Models/               Conversation + Message entities
    appsettings.json      Ollama URL + system prompt (persona)
  frontend/       Vue 3 + Vite SPA
    src/App.vue          chat UI
    src/lib/api.js       backend calls + stream reader
    vite.config.js       dev proxy /api -> :5274
```

## Run it (two terminals)

### 1. Backend

```bash
cd backend
dotnet restore
dotnet run
```

It starts on http://localhost:5274 and creates `rabbitic.db` on first run.
Quick check: open http://localhost:5274/api/models — you should see your installed models as JSON.

### 2. Frontend

```bash
cd frontend
npm install
npm run dev
```

Open http://localhost:5173. Pick a model, start chatting. New chats appear in the sidebar
and persist in SQLite.

## How the streaming works

1. Vue POSTs `{ conversationId, model, message }` to `/api/chat`.
2. The API saves the user message, loads the full conversation as context, and calls Ollama
   with `stream: true`.
3. Ollama returns newline-delimited JSON; the API extracts each token and flushes it straight
   to the browser, while accumulating the full reply.
4. When the stream ends, the API saves the assistant message.
5. The new conversation id comes back in the `X-Conversation-Id` response header.

## Customizing the assistant (no training needed)

Edit `backend/appsettings.json` → `Ollama:SystemPrompt` to change the persona, tone, or rules.
This is injected as a `system` message on every request.

To use a model you've customized with an Ollama Modelfile, just pull/create it in Ollama and
pick it from the dropdown — no code change.

## API reference

| Method | Route                       | Purpose                                  |
|--------|-----------------------------|------------------------------------------|
| GET    | `/api/models`               | List installed Ollama models             |
| GET    | `/api/conversations`        | List conversations (newest first)        |
| GET    | `/api/conversations/{id}`   | One conversation with its messages        |
| DELETE | `/api/conversations/{id}`   | Delete a conversation                    |
| POST   | `/api/chat`                 | Send a message, stream the reply         |

## Next steps (maps to the bigger architecture)

- **Feedback**: add a `Feedback` table (messageId, rating, correction) + thumbs UI, then a
  report endpoint that aggregates it. This is your SQL-report + "learning" foundation.
- **Memory vs history**: add a `nomic-embed-text` embedding step and a vector store for RAG
  over your docs and best past answers.
- **Tools**: add a `run_report` tool with an allow-list of named, parameterized queries —
  never run model-authored SQL.
- **Portable / from anywhere**: put a Cloudflare Tunnel or Tailscale in front of the API and
  add auth (bearer token at minimum). Then host this Vue build on GitHub Pages pointed at it.

## Notes

- SQLite file lives in `backend/`. Delete `rabbitic.db` to reset everything.
- In production, lock down CORS in `Program.cs` and require auth before exposing the API.
