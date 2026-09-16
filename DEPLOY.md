# Deploying Mostisko

*a creation of Mostisko, the AI*

One Docker image serves the Vue app **and** the API on a single port. SQLite lives on a
mounted volume, so data survives restarts and redeploys.

---

## Why not Vercel (for the backend)

Vercel is serverless: the filesystem is **read-only and ephemeral**, and each invocation
may run on a fresh machine. A SQLite file written there is gone by the next request, and
a `.NET` long-lived process (which this app needs for the in-memory vector index and
streaming chat) has nowhere to live.

**Vercel cannot host this backend.** The supported path is a container host with a
persistent volume. You *can* put the frontend alone on Vercel — see
[Split deployment](#split-deployment-optional) — but the API and its database must run
somewhere that offers a real disk.

---

## Quick start (local Docker)

```bash
cp .env.example .env        # then fill in any keys you have
docker compose up --build
```

Open <http://localhost:8080>. The SPA and the API are same-origin, so there is no CORS
setup and no second host to configure.

First boot creates the database, applies the schema upgrade, and seeds the
administrator. Log in with:

| Username | Secret |
|---|---|
| `mostisko@admin` | `q12wQ!@W` |

**Change that secret immediately after the first login on any machine reachable by
anyone else.** It is a documented bootstrap credential, not a secret.

---

## The volume is the whole point

SQLite is a file. A container's own filesystem is discarded when the container is
replaced, so the database is mounted from outside it:

```yaml
volumes:
  - mostisko-data:/data          # named volume → survives the container
```

```
ConnectionStrings__Default = "Data Source=/data/rabbitic.db"
```

SQLite also writes `rabbitic.db-wal` and `rabbitic.db-shm` beside the database, which is
why the volume holds the **directory** `/data` rather than a single file.

| Command | Effect on data |
|---|---|
| `docker compose restart` | kept |
| `docker compose down` then `up` | kept |
| `docker compose up --build` (new image) | kept |
| `docker compose down -v` | **destroyed** — the `-v` deletes the volume |

Back it up by copying the file out of the volume:

```bash
docker compose exec mostisko sh -c "cat /data/rabbitic.db" > backup-$(date +%F).db
```

Startup is idempotent: `EnsureCreated` → `SchemaUpgrade` → `RbacSeeder` →
`IeltsReadingSeeder` run on **every** boot, each checking before acting. Restarting
never duplicates a seed, and never resets an admin secret that has been changed.

---

## Secrets

Nothing secret is baked into the image. `appsettings.json` ships with **empty** key
fields, and every credential arrives as an environment variable at run time.

```
OLLAMA_CLOUD_API_KEY=…      → OllamaCloud__ApiKey
OPENROUTER_API_KEY=…        → OpenAiCompatible__Providers__0__ApiKey
GROQ_API_KEY=…              → OpenAiCompatible__Providers__1__ApiKey
```

(`__` is .NET's separator for nested config keys, so an env var overrides the matching
entry in `appsettings.json` without the file being edited.)

Two independent locks keep developer keys out of the image:

1. `.dockerignore` excludes `appsettings.Development.json` from the build context.
2. `RabbitIC.Api.csproj` sets `CopyToPublishDirectory="Never"` on it, covering every
   other publish path (CI, IIS, a manual `dotnet publish`).

A missing key is never fatal: the search endpoints answer `503` and an unconfigured
model provider is skipped at startup.

---

## Fly.io

`fly.toml` is included. Fly is a good fit because it offers a real persistent volume.

```bash
fly launch --no-deploy --name mostisko
fly volumes create mostisko_data --size 1 --region sin
fly secrets set OLLAMA_CLOUD_API_KEY=... OPENROUTER_API_KEY=...
fly deploy
```

`fly secrets set` stores values encrypted and injects them at run time, so the image is
byte-identical between local and production.

**Run exactly one machine.** SQLite is a single-writer database and a Fly volume cannot
be shared between machines; two writers would corrupt it. Scale by giving the one
machine more CPU/RAM, not by adding machines. `min_machines_running = 1` is set for this
reason.

### Railway (alternative)

Railway works the same way: deploy from the Dockerfile, attach a volume mounted at
`/data`, and set `ConnectionStrings__Default` plus the key variables in the dashboard.

---

## Ollama

The container needs to reach an Ollama server. `docker-compose.yml` defaults to
`http://host.docker.internal:11434`, which is the Ollama running on your own machine.

- **LAN box** — set `OLLAMA_URL=http://192.168.1.50:11434` in `.env`.
- **Cloud host** — a remote Fly/Railway container cannot see your local Ollama. Either
  expose it over the network (with authentication in front of it) or rely on the
  Ollama Cloud / OpenAI-compatible providers, which are configured by key.

---

## Split deployment (optional)

If you want the frontend on Vercel and the backend on a container host:

```bash
# Build the frontend against the separately hosted API
VITE_API_BASE_URL=https://mostisko.fly.dev/api npm run build
```

or via Docker: `docker build --build-arg VITE_API_BASE_URL=https://… .`

Then add the Vercel origin to the CORS policy in `Program.cs` — the browser is no longer
same-origin, so the backend must name it explicitly. The single-container path avoids
all of this, which is why it is the default.

---

## Routing note

The frontend uses real paths (`/ielts`, `/5lakhs`, `/access`), so a refresh or a deep
link issues a normal `GET` for a path with no file behind it. `Program.cs` maps a
fallback that returns `index.html` for those, while leaving `/api`, `/public` and
`/swagger` to answer as themselves — a genuine API 404 stays a 404 rather than becoming
a page of HTML.
