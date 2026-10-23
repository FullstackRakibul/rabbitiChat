# Ministry of Truth: deployment

How the public bulletin reaches the internet while the backend stays on the LAN
(docs/TRUTH_PROMPT.md §4).

```
visitor ── https://mostisko.vercel.app/truth ──▶ Vercel (static truth.html + assets)
                         /truth-api/*  ── rewrite ──▶ https://<tunnel-host>/public/truth/*
                         /truth/s/*    ── rewrite ──▶ https://<tunnel-host>/public/truth/share/*   (P2)
<machine>.<tailnet>.ts.net ── Tailscale Funnel, /public/truth only ──▶ 127.0.0.1:5275 on the LAN machine
                                                         └─ the backend: /public/truth/* only, everything else 404
```

The browser only ever talks to `mostisko.vercel.app`. It never sees the tunnel
host, so no CORS change is needed.

## 1. The backend: open the tunnel port

Set one value, as an environment variable or with user-secrets. It isn't a
secret, but keep it out of `appsettings*.json` so each machine chooses its own:

```bash
# PowerShell: $env:Truth__Tunnel__Port = "5275"   — not 5274, the admin API's port
Truth__Tunnel__Port=5275
```

On start, Kestrel keeps every address it already had (`--urls`,
`ASPNETCORE_URLS`, `ASPNETCORE_HTTP_PORTS`) and also listens on
`http://127.0.0.1:5275`. That address is loopback only, so nothing on the LAN or
the internet can connect to it except the tunnel daemon on the same machine.

Every request that arrives on that port is a **tunnel request**
(`backend/Truth/TunnelGuard.cs`, the first middleware). A tunnel request may only
`GET`, `HEAD` or `POST` a path under `/public/truth/`. Everything else gets the
same empty `404` before any other code runs: `/api/*`, `/swagger`, the admin SPA,
static files, `/truth-api/*`, and `..` tricks (Kestrel normalises the path first).

Optional settings:

| Key | Default | When |
|---|---|---|
| `Truth:Tunnel:Header` | (none) | A tunnel that can't use its own port: name a header only the tunnel adds, and requests carrying it are treated as tunnel requests on any port. |
| `Truth:Tunnel:ClientIpHeader` | `X-Forwarded-For` | Where the tunnel puts the visitor's IP (Tailscale Funnel: `X-Forwarded-For`). Believed only on tunnel requests. |
| `Truth:RateLimits:GetPerMinute` | `120` | Public GETs per visitor IP per minute (§4). |

Visitor IPs are used in memory for the rate limit only. Request logging stores
no IP and no user agent for public Truth requests.

## 2. The tunnel: Tailscale Funnel (decided, §13.1)

Free, with an open-source client (BSD-3-Clause), and a stable `https://<machine>.<tailnet>.ts.net`
address with no domain needed. Two locks, so a mistake in one still exposes nothing:

1. **Funnel mounts the public path only.** It forwards `/public/truth` and nothing else.
2. **The backend's tunnel port serves the public path only** (section 1), and that port is
   not 5274. The admin API's port 5274 is never funnelled.

Setup, on the LAN machine that runs the backend:

1. Install Tailscale and sign in. On Windows it runs as a service and starts with the machine.
2. In the tailnet's admin console: enable **HTTPS certificates** (DNS → HTTPS Certificates), and
   allow Funnel for **this one machine only** (Access controls → a `funnel` node attribute
   scoped to its tag or name).
3. Mount the public path on the tunnel port, path-based:

   ```bash
   tailscale funnel --bg --set-path /public/truth http://127.0.0.1:5275/public/truth
   ```

   The flags have changed between Tailscale versions. Check `tailscale funnel --help` on
   the installed version, and write the exact command that worked back into this file.
4. `tailscale funnel status` shows the public host and that only `/public/truth` is mounted.
   The configuration is kept by the Tailscale service, so it survives a reboot.
5. Put the host into `frontend/vercel.json` (section 3). It isn't a secret, but don't publish it anywhere else.

Funnel terminates TLS and forwards plain HTTP to `127.0.0.1:5275`, adding `X-Forwarded-For`
(the default `Truth:Tunnel:ClientIpHeader`). Confirm it once from outside: call
`/public/truth/time` 121 times in a minute from one address, and the 121st should be a `429`.

**The machine must stay on.** Set the backend to start with Windows (a service or IIS) as
well as Tailscale, and turn sleep off. While it's off, visitors see their stored copy under
the grey bar (section 5).

`tailscale funnel reset` takes the mount down.

## 3. Vercel

`frontend/vercel.json` holds the rewrites and the security headers. **Replace
`truth-tunnel.invalid`** (a reserved name that never resolves) with the tunnel
host in both rewrites, then deploy. The host is an address, not a secret.
`vercel.json` holds no key, and nothing but `/public/truth/*` is ever sent to it
(eval G1.6 checks both).

`vercel.json` must sit in the Vercel project's **root directory**. It is in
`frontend/` because that is where the build runs. If the project's root
directory is the repository root, move it there.

The build is the existing one (`npm run build` in `frontend/`). It produces
`dist/index.html` (the admin SPA, unchanged) and `dist/truth.html` (the public
shell). Nothing else changes for the admin SPA: `vercel.json` has no rewrite for
its paths.

Headers on `/truth*` (and the backend sends the same on the pages it serves,
`backend/Truth/TruthHeaders.cs`): a strict CSP (`script-src 'self'`, no inline
script or style, `frame-ancestors 'self'` so the admin's site-text preview can frame
it), `X-Content-Type-Options`, `Referrer-Policy`, HSTS and `Permissions-Policy`.

## 4. Live check (G1.6, with the real hosts)

```bash
SITE=https://mostisko.vercel.app
TUNNEL=https://<machine>.<tailnet>.ts.net   

curl -sI  $SITE/truth                         # 200, text/html, Content-Security-Policy present
curl -s   $SITE/truth-api/current | head -c 200   # the snapshot JSON (404 {"error":"not found"} until something is published)
curl -s -o /dev/null -w '%{http_code}\n' $TUNNEL/public/truth/current   # 200 (or 404 before the first publish)
for p in /api/models /api/auth/login /swagger/index.html / /index.html /truth /truth-api/current /public/server/status; do
  printf '%-24s %s\n' "$p" "$(curl -s -o /dev/null -w '%{http_code}' "$TUNNEL$p")"   # every one: 404
done
```

## 5. When the backend is off

The shell is static on Vercel and keeps working. Each visitor's browser keeps
the last snapshot it received (`localStorage`) and shows it under a grey "last
saved information" bar. A first-time visitor gets a friendly error with the RSS
link. The built-in site text is bundled, so the page is readable either way. A
static fallback copy published to Vercel is listed under §9 (later).

## 6. On the LAN

The backend serves the same thing itself: `/truth` (from `wwwroot/truth.html`,
when the built frontend is there, as in the Docker image) and `/truth-api/*`.
Staff can preview at `http://<lan-host>:<port>/truth` exactly as visitors see it.
The staff side is the **সত্য মন্ত্রণালয় · Ministry of Truth** tab
(`/ministry-of-truth`), for SystemAdmin by default (Access → Roles to grant it).
