#!/usr/bin/env node
// Oddity eval — Tier A contract checks, no model. See docs/ODDITY_PROMPT.md §3.10.
//
//   node scripts/oddity-eval.mjs                 every check
//   node scripts/oddity-eval.mjs --phase 1       checks up to phase 1
//   node scripts/oddity-eval.mjs --only G1.3     the checks of one gate (--only DOC: every DOC.n)
//   node scripts/oddity-eval.mjs --no-build      reuse the last eval build of the API
//   node scripts/oddity-eval.mjs --db-copy <f>   start from a copy of an existing database
//
// The checks that read the database directly use node:sqlite (Node 22.13+).
//
// It starts a fake Ollama and the API on ports of their own, against a throwaway
// SQLite file in the temp folder, so the dev database and a running dev server
// are never touched. The seeded administrator's name and secret come from
// ODDITY_EVAL_ADMIN_USER and ODDITY_EVAL_ADMIN_SECRET; they are never written here.
//
// A check asserts against what the fake recorded and what the client received,
// or against the source files themselves — never against the API's own account
// of what it did. Node 18+, no dependencies.

import http from 'node:http'
import net from 'node:net'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import crypto from 'node:crypto'
import { spawn, spawnSync } from 'node:child_process'
import { fileURLToPath, pathToFileURL } from 'node:url'

const REPO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const PROJECT = path.join(REPO, 'backend', 'RabbitIC.Api.csproj')
const EVAL_ROOT = path.join(os.tmpdir(), 'oddity-eval')
const BIN_DIR = path.join(EVAL_ROOT, 'api-bin')

// ---- Expected values, written by hand ----
// These mirror the design contract (§3.1), not the code: a check that read them
// from TabCatalog.cs would only prove the code agrees with itself.
// jsonto and 5lakhs left General's baseline when the model policy landed
// (MODEL_POLICY_PROMPT section 1) — both are grantable, and both now start at
// System, so the tiers above General still hold everything they held before.
const GENERAL_TABS = ['dashboard', 'todo', 'notes', 'sources', 'settings']
const SYSTEM_EXTRA = ['chat', 'workspace', 'docs', 'search', 'ielts', 'component-splitter',
                      'jsonto', '5lakhs']
const ROLE_KEYS = ['General', 'System', 'GroundControl', 'SystemAdmin']

// ─────────────────────────────── fake Ollama ───────────────────────────────

/** Ollama's final-frame usage fields, canned so later phases can compare them exactly. */
export const FAKE_USAGE = {
  total_duration: 1_234_000_000,
  load_duration: 5_000_000,
  prompt_eval_count: 42,
  prompt_eval_cached_count: 30,
  prompt_eval_duration: 100_000_000,
  eval_count: 3,
  eval_duration: 300_000_000,
}

export const FAKE_MODELS = ['fake-ok', 'fake-slow', 'fake-404', 'fake-500', 'fake-midstream-error', 'fake-nothink']
const EMBED_DIMS = 768

/**
 * Bag-of-words vector: each word adds 1 to a hashed dimension. Deterministic, and
 * texts that share words score higher, so retrieval can be checked without a model.
 */
export function fakeEmbedding(text) {
  const v = new Array(EMBED_DIMS).fill(0)
  for (const word of String(text).toLowerCase().match(/[a-z0-9]+/g) ?? []) {
    const h = crypto.createHash('sha256').update(word).digest()
    v[h.readUInt32BE(0) % EMBED_DIMS] += 1
  }
  if (v.every((x) => x === 0)) v[0] = 1
  return v
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

/**
 * Starts the fake. `requests` holds every request it received:
 * { method, path, headers, body (parsed JSON or raw text), sentSha256, closedEarly,
 *   firstWriteAt, lastWriteAt }. `stop()` and `restart()` keep the same port, so
 * the API (which knows only that URL) can be shown an Ollama that has gone away.
 */
export async function startFakeOllama({ models = FAKE_MODELS } = {}) {
  const requests = []

  const server = http.createServer(async (req, res) => {
    const chunks = []
    for await (const c of req) chunks.push(c)
    const raw = Buffer.concat(chunks).toString('utf8')
    let body = raw
    try { body = raw ? JSON.parse(raw) : null } catch { /* keep the raw text */ }

    const url = new URL(req.url, 'http://fake')
    const rec = {
      method: req.method, path: url.pathname, headers: req.headers, body,
      sentSha256: null, closedEarly: false, firstWriteAt: null, lastWriteAt: null,
    }
    requests.push(rec)

    const sent = []
    const write = (s) => {
      rec.lastWriteAt = Date.now()
      rec.firstWriteAt ??= rec.lastWriteAt
      sent.push(Buffer.from(s))
      res.write(s)
    }
    const finish = () => {
      rec.sentSha256 = crypto.createHash('sha256').update(Buffer.concat(sent)).digest('hex')
      res.end()
    }
    const json = (status, obj) => {
      res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8' })
      write(JSON.stringify(obj))
      finish()
    }
    res.on('close', () => { if (!res.writableFinished) rec.closedEarly = true })

    const model = body?.model ?? body?.name ?? ''
    const key = `${req.method} ${url.pathname}`

    switch (key) {
      case 'GET /api/version':
        return json(200, { version: '0.0.0-fake' })

      case 'GET /api/tags':
        return json(200, {
          models: [...models, 'nomic-embed-text'].map((name, i) => ({
            name, model: name, modified_at: '2026-01-01T00:00:00Z',
            size: 1000 + i, digest: `sha256:${String(i).padStart(64, '0')}`,
            details: { format: 'gguf', family: 'fake', parameter_size: '1B', quantization_level: 'Q4' },
          })),
        })

      case 'GET /api/ps':
        return json(200, {
          models: [{
            name: 'fake-ok', model: 'fake-ok', size: 2000, size_vram: 1500,
            context_length: 16384, expires_at: '2099-01-01T00:00:00Z',
          }],
        })

      case 'POST /api/show':
        if (model === 'fake-404') return json(404, { error: `model '${model}' not found` })
        return json(200, {
          capabilities: model === 'fake-nothink' ? ['completion'] : ['completion', 'thinking', 'tools'],
          model_info: { 'fake.context_length': 32768 },
        })

      case 'POST /api/embed': {
        const input = Array.isArray(body?.input) ? body.input : [body?.input ?? '']
        return json(200, { model, embeddings: input.map(fakeEmbedding) })
      }

      case 'POST /api/embeddings':
        return json(200, { embedding: fakeEmbedding(body?.prompt ?? '') })

      case 'POST /api/chat':
      case 'POST /api/generate': {
        if (model === 'fake-404') return json(404, { error: `model '${model}' not found` })
        if (model === 'fake-500') return json(500, { error: 'fake internal error' })

        const isChat = url.pathname === '/api/chat'
        const piece = (text) => isChat
          ? { model, created_at: '2026-01-01T00:00:00Z', message: { role: 'assistant', content: text }, done: false }
          : { model, created_at: '2026-01-01T00:00:00Z', response: text, done: false }
        const last = {
          ...piece(''), done: true, done_reason: 'stop', ...FAKE_USAGE,
        }
        const words = ['Hello', ' from', ' fake.']

        if (body?.stream === false) {
          const whole = isChat
            ? { ...last, message: { role: 'assistant', content: words.join('') } }
            : { ...last, response: words.join('') }
          return json(200, whole)
        }

        res.writeHead(200, { 'Content-Type': 'application/x-ndjson' })
        const gap = model === 'fake-slow' ? 300 : 0
        for (const w of words) {
          if (res.destroyed) break
          write(JSON.stringify(piece(w)) + '\n')
          if (model === 'fake-midstream-error') {
            write(JSON.stringify({ error: 'fake failure after the stream started' }) + '\n')
            return finish()
          }
          if (gap) await sleep(gap)
        }
        if (!res.destroyed) write(JSON.stringify(last) + '\n')
        return finish()
      }

      default:
        return json(404, { error: 'fake: not found' })
    }
  })

  const port = await listen(server)
  return {
    url: `http://127.0.0.1:${port}`,
    requests,
    since: (n) => requests.slice(n),
    stop: () => new Promise((r) => { server.closeAllConnections(); server.close(() => r()) }),
    restart: () => listen(server, port),
  }
}

function listen(server, port = 0) {
  return new Promise((resolve, reject) => {
    server.once('error', reject)
    server.listen(port, '127.0.0.1', () => resolve(server.address().port))
  })
}

async function freePort() {
  const s = net.createServer()
  const port = await listen(s)
  await new Promise((r) => s.close(r))
  return port
}

/** True when something already accepts connections on 127.0.0.1:port. */
function portAnswers(port) {
  return new Promise((resolve) => {
    const socket = net.connect({ host: '127.0.0.1', port })
    const done = (answered) => { socket.destroy(); resolve(answered) }
    socket.setTimeout(500, () => done(false))
    socket.once('connect', () => done(true))
    socket.once('error', () => done(false))
  })
}

// ─────────────────────────────── the API ───────────────────────────────

export function buildApi() {
  const r = spawnSync('dotnet', ['build', PROJECT, '-o', BIN_DIR, '-nologo', '-v', 'q'], {
    encoding: 'utf8', shell: process.platform === 'win32',
  })
  if (r.status !== 0) throw new Error(`dotnet build failed:\n${r.stdout}\n${r.stderr}`)
}

/**
 * Starts the built API against the fake. `runDir` holds the throwaway database and
 * is the working directory, so uploads land there too. Reuse `runDir` to restart
 * on the same database.
 */
export async function startApi({ fakeUrl, runDir, port }) {
  fs.mkdirSync(runDir, { recursive: true })
  // A port someone else holds would answer the readiness check, and every call
  // after it would reach THEIR server and database. Refuse rather than guess.
  if (port !== undefined && await portAnswers(port))
    throw new Error(`port ${port} is already in use (a dev server?) — refusing to start the eval API there`)
  port ??= await freePort()
  const dbPath = path.join(runDir, 'rabbitic.db')
  const logPath = path.join(runDir, 'api.log')
  const log = fs.createWriteStream(logPath, { flags: 'a' })

  const env = {
    ...process.env,
    // Not "Development": that would load user-secrets, and with them real keys.
    ASPNETCORE_ENVIRONMENT: 'Eval',
    Ollama__Url: fakeUrl,
    ConnectionStrings__Default: `Data Source=${dbPath}`,
    // appsettings.json carries a cloud key. Whitespace reads as "not configured",
    // and the cloud client is pointed at the fake in case anything still calls it.
    OllamaCloud__ApiKey: ' ',
    OllamaCloud__BaseUrl: fakeUrl,
    Logging__LogLevel__Default: 'Warning',
    Logging__LogLevel__Microsoft__AspNetCore: 'Warning',
  }

  const proc = spawn('dotnet', [
    path.join(BIN_DIR, 'RabbitIC.Api.dll'),
    '--urls', `http://127.0.0.1:${port}`,
    '--contentRoot', BIN_DIR,
  ], { cwd: runDir, env })
  proc.stdout.pipe(log)
  proc.stderr.pipe(log)

  const url = `http://127.0.0.1:${port}`
  const deadline = Date.now() + 90_000
  while (Date.now() < deadline) {
    if (proc.exitCode !== null) break
    try {
      const r = await fetch(`${url}/public/server/status`)
      // Answered, but only trust it while our own process is alive: if it died
      // (say, failing to bind), the answer came from another server.
      if (r.ok && proc.exitCode !== null) break
      if (r.ok) {
        return {
          url, dbPath, logPath, runDir, port, proc,
          stop: () => new Promise((resolve) => {
            if (proc.exitCode !== null) return resolve()
            proc.once('exit', () => resolve())
            proc.kill()
          }),
        }
      }
    } catch { /* not up yet */ }
    await sleep(300)
  }
  proc.kill()
  const tail = fs.existsSync(logPath) ? fs.readFileSync(logPath, 'utf8').slice(-3000) : ''
  throw new Error(`API did not become ready on ${url}.\n--- api.log (tail) ---\n${tail}`)
}

/** One HTTP call; never throws on a status. */
export async function call(base, method, p, { token, body, headers = {} } = {}) {
  const h = { ...headers }
  if (token) h.Authorization = `Bearer ${token}`
  if (body !== undefined) h['Content-Type'] ??= 'application/json'
  const res = await fetch(base + p, {
    method, headers: h, body: body === undefined ? undefined : JSON.stringify(body),
  })
  const text = await res.text()
  let json = null
  try { json = text ? JSON.parse(text) : null } catch { /* not JSON */ }
  return { status: res.status, headers: res.headers, text, json }
}

// ─────────────────────────────── checks ───────────────────────────────

const checks = []
/** Registers a check. `id` is its gate id; several checks may share one. */
function check(id, phase, name, fn) { checks.push({ id, phase, name, fn }) }

const sameSet = (a, b) => a.length === b.length && [...a].sort().join(',') === [...b].sort().join(',')
const show = (v) => (Array.isArray(v) ? `[${v.join(',')}]` : JSON.stringify(v))
const ok = (pass, detail) => ({ pass: !!pass, detail })
const read = (rel) => fs.readFileSync(path.join(REPO, rel), 'utf8')

/** Shared state, built lazily so --only can run a single gate. */
const ctx = {}

async function login(username, secret) {
  const r = await call(ctx.api.url, 'POST', '/api/auth/login', { body: { username, secret } })
  if (r.status !== 200 || !r.json?.token) throw new Error(`login ${username} → ${r.status} ${r.text}`)
  const me = await call(ctx.api.url, 'GET', '/api/me/tabs', { token: r.json.token })
  return { username, token: r.json.token, id: me.json?.id, me: me.json }
}

async function me(user) {
  return (await call(ctx.api.url, 'GET', '/api/me/tabs', { token: user.token })).json
}

function setRole(id, role) {
  return call(ctx.api.url, 'PUT', `/api/admin/rbac/users/${id}/role`, { token: ctx.admin.token, body: { role } })
}

function setGrants(id, tabKeys) {
  return call(ctx.api.url, 'PUT', `/api/admin/rbac/users/${id}/tabs`, { token: ctx.admin.token, body: { tabKeys } })
}

/** Creates the eval users once: one per role, plus spares for the input-spelling checks. */
async function users() {
  if (ctx.users) return ctx.users
  const secret = 'eval-' + crypto.randomBytes(6).toString('hex')
  const u = {
    gen: await login('eval-general', secret),
    sys: await login('eval-system', secret),
    gc: await login('eval-ground', secret),
    gc2: await login('eval-ground-2', secret),
    gc3: await login('eval-ground-3', secret),
  }
  const r = await setRole(u.sys.id, 'System')
  if (r.status !== 200) throw new Error(`could not make eval-system a System user: ${r.status} ${r.text}`)
  ctx.users = u
  return u
}

// ---- G1.2 the harness itself ----

check('G1.2', 1, 'fake Ollama answers on its own port', async () => {
  const before = ctx.fake.requests.length
  const r = await fetch(`${ctx.fake.url}/api/version`).then((x) => x.json())
  return ok(r.version === '0.0.0-fake' && ctx.fake.requests.length === before + 1, `${ctx.fake.url} → ${show(r)}`)
})

check('G1.2', 1, 'API runs on a throwaway database', async () => {
  const devDb = path.join(REPO, 'backend', 'Data', 'rabbitic.db')
  const usesRunDb = fs.existsSync(ctx.api.dbPath) && path.resolve(ctx.api.dbPath) !== path.resolve(devDb)
  return ok(usesRunDb && ctx.api.dbPath.startsWith(os.tmpdir()), ctx.api.dbPath)
})

check('G1.2', 1, 'seeded admin signs in from the environment', async () => {
  return ok(ctx.admin.me?.role === 'SystemAdmin' && ctx.admin.me?.isSystemAdmin === true,
    `${ctx.admin.username} → role ${ctx.admin.me?.role}`)
})

check('G1.2', 1, 'API reaches the fake, not a real Ollama', async () => {
  const before = ctx.fake.requests.length
  const r = await call(ctx.api.url, 'GET', '/api/models', { token: ctx.admin.token })
  const seen = ctx.fake.since(before).some((q) => q.method === 'GET' && q.path === '/api/tags')
  const names = (r.json ?? []).map?.((m) => m.name) ?? []
  return ok(r.status === 200 && seen && names.includes('fake-ok'),
    `/api/models ${r.status}; fake saw GET /api/tags: ${seen}; models include fake-ok: ${names.includes('fake-ok')}`)
})

// ---- G1.3 the role and its tabs ----

check('G1.3', 1, "role 'ground-control' is accepted and stored as GroundControl", async () => {
  const { gc } = await users()
  const r = await setRole(gc.id, 'ground-control')
  return ok(r.status === 200 && r.json?.role === 'GroundControl', `PUT role → ${r.status} ${r.json?.role ?? r.text}`)
})

check('G1.3', 1, "'ground control' and 'Ground_Control' are accepted too", async () => {
  const { gc2, gc3 } = await users()
  const a = await setRole(gc2.id, 'ground control')
  const b = await setRole(gc3.id, 'Ground_Control')
  return ok(a.json?.role === 'GroundControl' && b.json?.role === 'GroundControl',
    `'ground control' → ${a.status} ${a.json?.role ?? a.text}; 'Ground_Control' → ${b.status} ${b.json?.role ?? b.text}`)
})

check('G1.3', 1, 'only the three Ground Control spellings are loose; other roles match exactly', async () => {
  const { gen } = await users()
  const refused = []
  for (const role of ['system admin', 'System-Admin', 'system_admin', 'ground.control', 'groundcontrol-x']) {
    const r = await setRole(gen.id, role)
    if (r.status !== 400) refused.push(`${role} → ${r.status} ${r.json?.role ?? ''}`)
  }
  const still = (await me(gen))?.role
  // A wrong acceptance must not leak into later checks (an extra admin breaks G1.4).
  if (still !== 'General') await setRole(gen.id, 'General')
  return ok(refused.length === 0 && still === 'General', refused.length ? `accepted ${refused.join(', ')}` : 'all five refused with 400')
})

check('G1.3', 1, 'Ground Control gets every System tab plus oddity', async () => {
  const { gc } = await users()
  const tabs = (await me(gc))?.tabs ?? []
  const want = [...GENERAL_TABS, ...SYSTEM_EXTRA, 'oddity']
  return ok(sameSet(tabs, want), `got ${show(tabs)}`)
})

check('G1.3', 1, "Ground Control's tabs equal a System user's tabs plus oddity", async () => {
  const { gc, sys } = await users()
  const g = (await me(gc))?.tabs ?? []
  const s = (await me(sys))?.tabs ?? []
  return ok(sameSet(g, [...s, 'oddity']), `system ${show(s)}`)
})

check('G1.3', 1, 'a System user does not get oddity', async () => {
  const { sys } = await users()
  const tabs = (await me(sys))?.tabs ?? []
  return ok(sameSet(tabs, [...GENERAL_TABS, ...SYSTEM_EXTRA]) && !tabs.includes('oddity'), `got ${show(tabs)}`)
})

check('G1.3', 1, 'a SystemAdmin gets oddity', async () => {
  const tabs = (await me(ctx.admin))?.tabs ?? []
  return ok(tabs.includes('oddity') && tabs.includes('rbac') && tabs.includes('logs'), `got ${show(tabs)}`)
})

check('G1.3', 1, '/api/oddity is gated by the oddity tab', async () => {
  const { gc, sys, gen } = await users()
  const s = await call(ctx.api.url, 'GET', '/api/oddity/ping', { token: sys.token })
  const g = await call(ctx.api.url, 'GET', '/api/oddity/ping', { token: gen.token })
  const c = await call(ctx.api.url, 'GET', '/api/oddity/ping', { token: gc.token })
  const a = await call(ctx.api.url, 'GET', '/api/oddity/ping')
  // No /api/oddity route exists yet, so "let through" reads as 404 and "refused" as 403.
  return ok(s.status === 403 && /oddity/i.test(s.json?.error ?? '') && g.status === 403
    && c.status === 404 && a.status !== 403,
  `System ${s.status} · General ${g.status} · GroundControl ${c.status} · anonymous ${a.status}`)
})

check('G1.3', 1, 'GroundControl survives a restart on the same database (F2)', async () => {
  const { gc } = await users()
  await ctx.api.stop()
  ctx.api = await startApi({ fakeUrl: ctx.fake.url, runDir: ctx.api.runDir, port: ctx.api.port })
  const after = await me(gc)
  return ok(after?.role === 'GroundControl' && after.tabs.includes('oddity'), `after restart: ${after?.role}`)
})

// ---- G1.4 the existing guards ----

check('G1.4', 1, 'an unknown role is a 400 that lists the four roles', async () => {
  const { gen } = await users()
  const r = await setRole(gen.id, 'Pilot')
  const msg = r.json?.error ?? ''
  const listed = msg.includes(ROLE_KEYS.join(', '))
  return ok(r.status === 400 && listed, `${r.status} "${msg}"`)
})

check('G1.4', 1, 'changing your own role is refused', async () => {
  const r = await setRole(ctx.admin.id, 'System')
  const still = (await me(ctx.admin))?.role
  return ok(r.status === 403 && still === 'SystemAdmin', `${r.status} "${r.json?.error}"; still ${still}`)
})

check('G1.4', 1, 'the only SystemAdmin cannot be demoted by any route', async () => {
  const list = await call(ctx.api.url, 'GET', '/api/admin/rbac/users?pageSize=100', { token: ctx.admin.token })
  const admins = (list.json?.items ?? []).filter((u) => u.role === 'SystemAdmin')
  // The only caller allowed to set roles is a SystemAdmin; with one admin, that is
  // the target itself, which the self-rule refuses.
  const r = await setRole(admins[0]?.id, 'General')
  return ok(admins.length === 1 && r.status === 403, `${admins.length} admin(s); demote → ${r.status} "${r.json?.error}"`)
})

// ---- G1.6 roles come from the server ----

check('G1.6', 1, 'RbacTab.vue holds no role list or role → tabs copy', async () => {
  const src = read('frontend/src/components/tabs/RbacTab.vue')
  const hits = ['predictBaseline', 'const ROLES'].filter((s) => src.includes(s))
  return ok(hits.length === 0, hits.length ? `found ${show(hits)}` : 'no matches')
})

check('G1.6', 1, 'Ground Control badge is secondary with a Satellite icon, never destructive', async () => {
  const src = read('frontend/src/components/tabs/RbacTab.vue')
  const variant = src.match(/function roleVariant[\s\S]*?\n\}/)?.[0] ?? ''
  const red = variant.includes('destructive')
  const gcSecondary = /'GroundControl'\)\s*return 'secondary'/.test(variant)
  const icon = /<Satellite\b[^>]*v-if="[^"]*GroundControl/.test(src)
  return ok(!red && gcSecondary && icon, `destructive: ${red} · GroundControl → secondary: ${gcSecondary} · Satellite icon: ${icon}`)
})

check('G1.6', 1, 'GET /api/admin/rbac/roles lists four roles in order, with labels', async () => {
  const r = await call(ctx.api.url, 'GET', '/api/admin/rbac/roles', { token: ctx.admin.token })
  const keys = (r.json ?? []).map?.((x) => x.key) ?? []
  const gcLabel = (r.json ?? []).find?.((x) => x.key === 'GroundControl')?.label
  return ok(r.status === 200 && keys.join(',') === ROLE_KEYS.join(',') && gcLabel === 'Ground Control',
    `${r.status} ${show(keys)} label "${gcLabel}"`)
})

check('G1.6', 1, "each role's default tabs equal what /api/me/tabs gives that role", async () => {
  const { gen, sys, gc } = await users()
  const r = await call(ctx.api.url, 'GET', '/api/admin/rbac/roles', { token: ctx.admin.token })
  const byKey = Object.fromEntries((r.json ?? []).map?.((x) => [x.key, x.defaultTabs]) ?? [])
  const pairs = [['General', gen], ['System', sys], ['GroundControl', gc], ['SystemAdmin', ctx.admin]]
  const bad = []
  for (const [role, user] of pairs) {
    const tabs = (await me(user))?.tabs ?? []
    if (!byKey[role] || !sameSet(byKey[role], tabs)) bad.push(`${role}: route ${show(byKey[role])} vs me ${show(tabs)}`)
  }
  return ok(r.status === 200 && bad.length === 0, bad.join(' · ') || 'all four match')
})

check('G1.6', 1, 'the roles route is SystemAdmin only', async () => {
  const { gc } = await users()
  const g = await call(ctx.api.url, 'GET', '/api/admin/rbac/roles', { token: gc.token })
  const a = await call(ctx.api.url, 'GET', '/api/admin/rbac/roles')
  return ok(g.status === 403 && a.status === 401, `GroundControl ${g.status} · anonymous ${a.status}`)
})

// ---- G1.7 a grant opens the tab without a redeploy ----

check('G1.7', 1, 'granting oddity to a System user opens it; removing it closes it', async () => {
  const { sys } = await users()
  const before = (await me(sys))?.tabs ?? []
  const b403 = (await call(ctx.api.url, 'GET', '/api/oddity/ping', { token: sys.token })).status
  const g = await setGrants(sys.id, ['oddity'])
  const during = (await me(sys))?.tabs ?? []
  const d404 = (await call(ctx.api.url, 'GET', '/api/oddity/ping', { token: sys.token })).status
  await setGrants(sys.id, [])
  const after = (await me(sys))?.tabs ?? []
  const a403 = (await call(ctx.api.url, 'GET', '/api/oddity/ping', { token: sys.token })).status
  return ok(!before.includes('oddity') && b403 === 403 && g.status === 200
    && during.includes('oddity') && d404 === 404 && !after.includes('oddity') && a403 === 403,
  `before: tab ${before.includes('oddity')}, api ${b403} · granted (${g.status}): tab ${during.includes('oddity')}, api ${d404} · revoked: tab ${after.includes('oddity')}, api ${a403}`)
})

// ---- G1.5 and G1.8 the key is spelled the same everywhere ----

/** Pulls the string list out of a source block, so the check reads the files, not the API. */
function extract(rel, blockRe, itemRe, constants = {}) {
  const block = read(rel).match(blockRe)?.[1] ?? ''
  const items = []
  for (const m of block.matchAll(itemRe)) items.push(m[1] ?? constants[m[2]])
  return items
}

const sources = () => ({
  catalog: extract('backend/Rbac/TabCatalog.cs',
    /public static readonly string\[\] All =\s*\{([\s\S]*?)\};/, /"([^"]+)"|\b(RbacTab)\b/g, { RbacTab: 'rbac' }),
  routes: extract('frontend/src/app/router.js',
    /export const MODULE_ROUTES = \[([\s\S]*?)\n\]/, /key: '([^']+)'/g),
  tabs: extract('frontend/src/App.vue', /const ALL_TABS = \[([\s\S]*?)\n\]/, /id: '([^']+)'/g),
})

check('G1.5', 1, "the key 'oddity' appears in all seven places", async () => {
  const places = [
    ['TabCatalog.All', sources().catalog.includes('oddity')],
    ['TabCatalog.Labels', /\["oddity"\]\s*=\s*"Oddity"/.test(read('backend/Rbac/TabCatalog.cs'))],
    ['role baseline', /GroundControl\w*Tabs\s*=\s*\{[^}]*"oddity"/.test(read('backend/Rbac/TabCatalog.cs'))],
    ['TabAccessMiddleware', /\("\/api\/oddity",\s*"oddity"\)/.test(read('backend/Middleware/TabAccessMiddleware.cs'))],
    ['MODULE_ROUTES', sources().routes.includes('oddity')],
    ['ALL_TABS', sources().tabs.includes('oddity')],
    ['<TabsContent>', /<TabsContent[^>]*value="oddity"/.test(read('frontend/src/App.vue'))],
  ]
  const missing = places.filter(([, found]) => !found).map(([p]) => p)
  return ok(missing.length === 0, missing.length ? `missing in ${show(missing)}` : 'all seven')
})

check('G1.8', 1, 'ALL_TABS ⊆ MODULE_ROUTES ⊆ TabCatalog.All, with oddity', async () => {
  const { catalog, routes, tabs } = sources()
  const a = tabs.filter((t) => !routes.includes(t))
  const b = routes.filter((r) => !catalog.includes(r))
  const has = tabs.includes('oddity') && routes.includes('oddity') && catalog.includes('oddity')
  return ok(a.length === 0 && b.length === 0 && has,
    `ALL_TABS ${tabs.length} · MODULE_ROUTES ${routes.length} · TabCatalog.All ${catalog.length}`
    + (a.length ? ` · not routed ${show(a)}` : '') + (b.length ? ` · not in catalog ${show(b)}` : '')
    + (has ? '' : ' · oddity missing'))
})

check('G1.8', 1, 'TabCatalog.All in source equals the server tab list', async () => {
  const r = await call(ctx.api.url, 'GET', '/api/admin/rbac/tabs', { token: ctx.admin.token })
  const server = (r.json ?? []).map?.((t) => t.key) ?? []
  return ok(server.join(',') === sources().catalog.join(','), `server ${show(server)}`)
})

// ─────────────────────────────── Phase 2 — gateway core ───────────────────────────────

/** Settings the Phase 2 checks run under. Rates are 0 (no limit) except where a check sets one. */
const P2_SETTINGS = {
  enabled: true, defaultModel: 'fake-ok', allowedModels: [], numCtx: 8192, replyReserve: 2048,
  keepAlive: '-1', defaultRatePerMinute: 30, maxInlineFileMb: 10, logBodies: 'preview', retentionDays: 30,
  publicBaseUrl: '',
}
const KEY_RE = /^odd_[0-9a-f]{8}_[A-Za-z0-9_-]{43}$/
const EVAL_UA = 'oddity-eval/1'

const sha256 = (b) => crypto.createHash('sha256').update(b).digest('hex')
const clone = (v) => JSON.parse(JSON.stringify(v))

/** Structural equality, key order ignored — the gateway edits a JSON tree, not text. */
function deepEqual(a, b) {
  if (a === b) return true
  if (typeof a !== typeof b || a === null || b === null || typeof a !== 'object') return false
  if (Array.isArray(a) !== Array.isArray(b)) return false
  if (Array.isArray(a)) return a.length === b.length && a.every((x, i) => deepEqual(x, b[i]))
  const ka = Object.keys(a).sort()
  const kb = Object.keys(b).sort()
  return deepEqual(ka, kb) && ka.every((k) => deepEqual(a[k], b[k]))
}

/** Read-only query of the throwaway database. node:sqlite needs Node 22.13+. */
async function sql(query, ...params) {
  const emit = process.emitWarning
  process.emitWarning = (w, ...a) => (String(w).includes('SQLite') ? undefined : emit.call(process, w, ...a))
  const { DatabaseSync } = await import('node:sqlite')
  const d = new DatabaseSync(ctx.api.dbPath, { readOnly: true })
  try { return d.prepare(query).all(...params) } finally { d.close() }
}

/** A gateway call. Reads the body as bytes and notes when the first chunk arrived. */
async function gw(p, { key, body, raw, contentType = 'application/json', method = 'POST', headers = {}, signal, onFirstChunk } = {}) {
  const h = { 'User-Agent': EVAL_UA, ...headers }
  if (key) h.Authorization = `Bearer ${key}`
  const payload = raw ?? (body === undefined ? undefined : JSON.stringify(body))
  if (payload !== undefined && contentType) h['Content-Type'] = contentType
  const startedAt = Date.now()
  const res = await fetch(`${ctx.api.url}/oddity${p}`, { method, headers: h, body: payload, signal })
  const chunks = []
  let firstChunkAt = null
  const reader = res.body?.getReader() ?? { read: async () => ({ done: true }) }   // HEAD has no body
  for (;;) {
    const { value, done } = await reader.read()
    if (done) break
    if (firstChunkAt === null) { firstChunkAt = Date.now(); await onFirstChunk?.(res) }
    chunks.push(Buffer.from(value))
  }
  const buf = Buffer.concat(chunks)
  const text = buf.toString('utf8')
  let json = null
  try { json = JSON.parse(text) } catch { /* NDJSON or empty */ }
  return {
    status: res.status, headers: res.headers, contentType: res.headers.get('content-type'),
    correlationId: res.headers.get('x-correlation-id'), buf, text, json, sha: sha256(buf), startedAt, firstChunkAt,
  }
}

const adminApi = (user, method, p, body) => call(ctx.api.url, method, `/api/oddity${p}`, { token: user.token, body })

async function putSettings(patch) {
  const cur = (await adminApi(ctx.admin, 'GET', '/settings')).json ?? {}
  const r = await adminApi(ctx.admin, 'PUT', '/settings', { ...cur, ...patch })
  if (r.status !== 200) throw new Error(`PUT settings → ${r.status} ${r.text}`)
  return r.json
}

async function createKey(user, fields = {}) {
  const r = await adminApi(user, 'POST', '/keys', { name: 'eval key', ratePerMinute: 0, ...fields })
  if (r.status !== 200 || !r.json?.key) throw new Error(`POST keys → ${r.status} ${r.text}`)
  return r.json
}

/** The request rows the owner can see, newest first, optionally narrowed by a query string. */
async function rows(user, query = '') {
  const r = await adminApi(user, 'GET', `/requests?limit=200${query ? '&' + query : ''}`)
  return r.json?.items ?? []
}

/** Polls until the row for a correlation id exists (the row is written after the response). */
async function rowFor(user, correlationId, want = () => true) {
  for (let i = 0; i < 40; i++) {
    const hit = (await rows(user, `q=${encodeURIComponent(correlationId)}`)).find((x) => x.correlationId === correlationId)
    if (hit && want(hit)) return hit
    await sleep(100)
  }
  return null
}

/** Phase 2 setup: two Ground Control users, a System user, settings on, a key each. */
async function gateway() {
  if (ctx.gw) return ctx.gw
  const u = await users()
  for (const x of [u.gc, u.gc2]) {
    const r = await setRole(x.id, 'GroundControl')
    if (r.status !== 200) throw new Error(`could not make ${x.username} Ground Control: ${r.status}`)
  }
  await putSettings(P2_SETTINGS)
  ctx.gw = { a: u.gc, b: u.gc2, sys: u.sys, keyA: await createKey(u.gc, { name: 'key A' }), keyB: await createKey(u.gc2, { name: 'key B' }) }
  return ctx.gw
}

const chatBody = (model = 'fake-ok', extra = {}) => ({ model, messages: [{ role: 'user', content: 'hello' }], ...extra })

// ---- G2.1 ----

check('G2.1', 2, 'admin keys route: 401 without a token, 403 for System, 200 for Ground Control', async () => {
  const g = await gateway()
  const none = await call(ctx.api.url, 'GET', '/api/oddity/keys')
  const sys = await adminApi(g.sys, 'GET', '/keys')
  const gc = await adminApi(g.a, 'GET', '/keys')
  return ok(none.status === 401 && sys.status === 403 && gc.status === 200, `none ${none.status} · System ${sys.status} · GroundControl ${gc.status}`)
})

// ---- G2.2 ----

check('G2.2', 2, 'a new key is returned once, in the odd_<8 hex>_<43 base64url> format', async () => {
  const g = await gateway()
  const k = g.keyA
  return ok(KEY_RE.test(k.key) && k.key.startsWith(`odd_${k.prefix}_`), `${k.key.slice(0, 13)}… prefix ${k.prefix}`)
})

check('G2.2', 2, 'listing keys never returns the key', async () => {
  const g = await gateway()
  const r = await adminApi(g.a, 'GET', '/keys')
  const secret = g.keyA.key.split('_').pop()
  const listed = (r.json ?? []).find((k) => k.id === g.keyA.id)
  return ok(listed && !r.text.includes(secret) && !('key' in listed), listed ? `fields ${show(Object.keys(listed))}` : 'key missing from list')
})

check('G2.2', 2, 'OddityKeys stores only the prefix and the SHA-256 of the whole key', async () => {
  const g = await gateway()
  const [row] = await sql('select * from OddityKeys where Id = ?', g.keyA.id)
  const secret = g.keyA.key.split('_').pop()
  const leaks = Object.entries(row ?? {}).filter(([, v]) => typeof v === 'string' && (v.includes(secret) || v === g.keyA.key))
  return ok(row && row.Prefix === g.keyA.prefix && row.KeyHash === sha256(g.keyA.key) && leaks.length === 0,
    row ? `columns ${show(Object.keys(row))}; hash matches: ${row.KeyHash === sha256(g.keyA.key)}; leaks ${show(leaks.map(([k]) => k))}` : 'no row')
})

check('G2.2', 2, "the AuditLogs row for the new key shows *** for KeyHash", async () => {
  const g = await gateway()
  let audit
  for (let i = 0; i < 40 && !audit; i++) {
    ;[audit] = await sql("select ChangesJson from AuditLogs where EntityName = 'OddityKey' and EntityKey = ? and Action = 'Added'", String(g.keyA.id))
    if (!audit) await sleep(100)
  }
  const changes = JSON.parse(audit?.ChangesJson ?? '[]')
  const hash = changes.find((c) => c.property === 'KeyHash')
  return ok(hash?.new === '***' && !audit.ChangesJson.includes(sha256(g.keyA.key)), audit ? `KeyHash → ${hash?.new}` : 'no audit row')
})

// ---- G2.3 ----

check('G2.3', 2, 'missing, wrong, disabled, expired and deleted keys get one identical 401, and nothing is forwarded', async () => {
  const g = await gateway()
  const disabled = await createKey(g.a, { name: 'disabled' })
  await adminApi(g.a, 'PUT', `/keys/${disabled.id}`, { name: 'disabled', ratePerMinute: 0, enabled: false })
  const expired = await createKey(g.a, { name: 'expired' })
  await adminApi(g.a, 'PUT', `/keys/${expired.id}`, { name: 'expired', ratePerMinute: 0, enabled: true, expiresAtUtc: '2020-01-01T00:00:00Z' })
  const deleted = await createKey(g.a, { name: 'deleted' })
  await adminApi(g.a, 'DELETE', `/keys/${deleted.id}`)
  const wrong = g.keyA.key.slice(0, -1) + (g.keyA.key.endsWith('A') ? 'B' : 'A')

  const before = ctx.fake.requests.length
  const cases = { missing: undefined, wrong, disabled: disabled.key, expired: expired.key, deleted: deleted.key }
  const out = {}
  for (const [name, key] of Object.entries(cases)) out[name] = await gw('/api/chat', { key, body: chatBody() })
  const bodies = new Set(Object.values(out).map((r) => r.text))
  const statuses = Object.entries(out).map(([n, r]) => `${n} ${r.status}`)
  const forwarded = ctx.fake.requests.length - before
  return ok(Object.values(out).every((r) => r.status === 401) && bodies.size === 1 && forwarded === 0
    && out.missing.json?.error?.startsWith('oddity:'),
  `${statuses.join(' · ')} · distinct bodies ${bodies.size} · forwarded ${forwarded} · ${out.missing.text}`)
})

check('G2.3', 2, 'a session token is not a gateway key, and a gateway key is not a session token', async () => {
  const g = await gateway()
  const viaSession = await gw('/api/chat', { key: g.a.token, body: chatBody() })
  const keyOnAdmin = await call(ctx.api.url, 'GET', '/api/oddity/keys', { token: g.keyA.key })
  const keyOnMe = await call(ctx.api.url, 'GET', '/api/me/tabs', { token: g.keyA.key })
  return ok(viaSession.status === 401 && keyOnAdmin.status === 401 && keyOnMe.status === 401,
    `session token on gateway ${viaSession.status} · key on /api/oddity/keys ${keyOnAdmin.status} · key on /api/me/tabs ${keyOnMe.status}`)
})

check('G2.3', 2, 'a key whose owner lost the oddity tab gets 403, nothing is forwarded, and a rejected row is written', async () => {
  await gateway()
  const u = await login('eval-ground-lapsed', 'eval-' + crypto.randomBytes(6).toString('hex'))
  await setRole(u.id, 'GroundControl')
  const key = await createKey(u, { name: 'lapsed' })
  await setRole(u.id, 'System')
  const before = ctx.fake.requests.length
  const r = await gw('/api/chat', { key: key.key, body: chatBody() })
  let row
  for (let i = 0; i < 30 && !row; i++) {
    ;[row] = await sql('select Outcome, StatusCode from OddityRequests where KeyId = ?', key.id)
    if (!row) await sleep(100)
  }
  return ok(r.status === 403 && ctx.fake.requests.length === before && row?.Outcome === 'rejected' && row?.StatusCode === 403,
    `${r.status} ${r.text} · forwarded ${ctx.fake.requests.length - before} · row ${show(row)}`)
})

// ---- G2.4 ----

check('G2.4', 2, 'streaming fidelity: same bytes, same status and content type, first frame before the last is sent', async () => {
  const g = await gateway()
  const before = ctx.fake.requests.length
  const r = await gw('/api/chat', { key: g.keyA.key, body: chatBody('fake-slow') })
  const up = ctx.fake.since(before).find((q) => q.path === '/api/chat')
  return ok(r.status === 200 && up && r.sha === up.sentSha256 && r.contentType === 'application/x-ndjson'
    && r.firstChunkAt < up.lastWriteAt,
  `status ${r.status} · ${r.contentType} · sha match ${r.sha === up?.sentSha256} · first chunk ${up ? up.lastWriteAt - r.firstChunkAt : '?'} ms before the fake's last write`)
})

// ---- G2.5 ----

const FIDELITY_BODY = {
  model: 'fake-ok',
  messages: [{ role: 'user', content: 'describe', images: ['aGVsbG8='] }],
  tools: [{ type: 'function', function: { name: 'lookup', description: 'x', parameters: { type: 'object', properties: { q: { type: 'string' } } } } }],
  format: { type: 'object', properties: { answer: { type: 'string' } }, required: ['answer'] },
  options: { temperature: 0.3, seed: 7, num_ctx: 999 },
  keep_alive: '5m',
  stream: false,
  foo: { bar: [1, 2, { baz: null }] },
  oddity: {},
}

function expectedUpstream(sent, settings = P2_SETTINGS) {
  const e = clone(sent)
  delete e.oddity
  e.options = { ...(e.options ?? {}), num_ctx: settings.numCtx }
  e.keep_alive = /^-?\d+$/.test(settings.keepAlive) ? Number(settings.keepAlive) : settings.keepAlive
  return e
}

check('G2.5', 2, 'the fake receives every field, minus oddity, with num_ctx and keep_alive from settings', async () => {
  const g = await gateway()
  const before = ctx.fake.requests.length
  const r = await gw('/api/chat', { key: g.keyA.key, body: FIDELITY_BODY })
  const up = ctx.fake.since(before).find((q) => q.path === '/api/chat')
  return ok(r.status === 200 && deepEqual(up?.body, expectedUpstream(FIDELITY_BODY)), `status ${r.status} · upstream ${JSON.stringify(up?.body)}`)
})

check('G2.5', 2, 'generate keeps system, images and unknown fields too', async () => {
  const g = await gateway()
  const sent = { model: 'fake-ok', prompt: 'hi', system: 'be brief', images: ['aGVsbG8='], raw: false, stream: false, zap: 1, oddity: {} }
  const before = ctx.fake.requests.length
  const r = await gw('/api/generate', { key: g.keyA.key, body: sent })
  const up = ctx.fake.since(before).find((q) => q.path === '/api/generate')
  return ok(r.status === 200 && deepEqual(up?.body, expectedUpstream(sent)), `status ${r.status} · upstream ${JSON.stringify(up?.body)}`)
})

check('G2.5', 2, 'only Content-Type and Accept travel upstream (plus transport headers); nothing of the caller\'s leaks', async () => {
  const g = await gateway()
  const before = ctx.fake.requests.length
  await gw('/api/chat', {
    key: g.keyA.key, body: chatBody(),
    headers: {
      Cookie: 'session=abc', 'X-Eval-Private': 'nope', Accept: 'application/x-ndjson',
      traceparent: '00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01',
    },
  })
  const up = ctx.fake.since(before).find((q) => q.path === '/api/chat')
  const h = up?.headers ?? {}
  // R3 allowlist. host, content-length, connection and transfer-encoding are HTTP framing, not the caller's.
  const allowed = ['content-type', 'accept', 'host', 'content-length', 'connection', 'transfer-encoding']
  const extra = Object.keys(h).filter((k) => !allowed.includes(k))
  return ok(up && extra.length === 0 && h['content-type']?.startsWith('application/json') && h.accept === 'application/x-ndjson',
    `headers ${show(Object.keys(h))} · not allowed ${show(extra)} · accept ${h.accept}`)
})

check('G2.5', 2, 'a body posted like `curl -d` (form content type) arrives the same', async () => {
  const g = await gateway()
  const before = ctx.fake.requests.length
  const r = await gw('/api/chat', { key: g.keyA.key, raw: JSON.stringify(FIDELITY_BODY), contentType: 'application/x-www-form-urlencoded' })
  const up = ctx.fake.since(before).find((q) => q.path === '/api/chat')
  return ok(r.status === 200 && deepEqual(up?.body, expectedUpstream(FIDELITY_BODY)), `status ${r.status}`)
})

/**
 * POSTs a body and resolves with the first response, even one that arrives while
 * the body is still uploading — the way curl reports an early 413. fetch() drops
 * such a response when the server then closes the half-sent upload.
 */
function postEarly(p, key, payload) {
  return new Promise((resolve, reject) => {
    const url = new URL(`${ctx.api.url}/oddity${p}`)
    const req = http.request(url, {
      method: 'POST',
      headers: { Authorization: `Bearer ${key}`, 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(payload) },
    }, (res) => {
      let text = ''
      res.on('data', (d) => { text += d })
      res.on('end', () => resolve({ status: res.statusCode, text }))
      res.on('error', () => resolve({ status: res.statusCode, text }))
    })
    req.on('error', (e) => { if (!req.res) reject(e) })   // the close after an early answer is expected
    req.end(payload)
  })
}

check('G2.5', 2, 'a body that is not JSON is a 400; one over the size limit is a 413; neither is forwarded', async () => {
  const g = await gateway()
  const before = ctx.fake.requests.length
  const bad = await gw('/api/chat', { key: g.keyA.key, raw: 'model=fake-ok&messages=hi', contentType: 'application/x-www-form-urlencoded' })
  const big = await postEarly('/api/chat', g.keyA.key, JSON.stringify({ model: 'fake-ok', pad: 'x'.repeat(31_000_000) }))
  return ok(bad.status === 400 && bad.json?.error?.startsWith('oddity:') && big.status === 413 && ctx.fake.requests.length === before,
    `not JSON ${bad.status} ${bad.text} · too large ${big.status} ${big.text.slice(0, 80)} · forwarded ${ctx.fake.requests.length - before}`)
})

// ---- G2.6 ----

check('G2.6', 2, 'saving a new NumCtx changes the very next request', async () => {
  const g = await gateway()
  await putSettings({ numCtx: 4096 })
  const before = ctx.fake.requests.length
  await gw('/api/chat', { key: g.keyA.key, body: chatBody('fake-ok', { options: { num_ctx: 131072 } }) })
  const up = ctx.fake.since(before).find((q) => q.path === '/api/chat')
  await putSettings({ numCtx: P2_SETTINGS.numCtx })
  return ok(up?.body?.options?.num_ctx === 4096, `caller asked 131072, fake got ${up?.body?.options?.num_ctx}`)
})

check('G2.6', 2, "KeepAlive '-1' arrives as the number -1, '30m' as a string", async () => {
  const g = await gateway()
  const seen = {}
  for (const v of ['-1', '30m']) {
    await putSettings({ keepAlive: v })
    const before = ctx.fake.requests.length
    await gw('/api/generate', { key: g.keyA.key, body: { model: 'fake-ok', prompt: 'x', stream: false, keep_alive: 0 } })
    seen[v] = ctx.fake.since(before).find((q) => q.path === '/api/generate')?.body?.keep_alive
  }
  await putSettings({ keepAlive: P2_SETTINGS.keepAlive })
  return ok(seen['-1'] === -1 && seen['30m'] === '30m', `'-1' → ${JSON.stringify(seen['-1'])} · '30m' → ${JSON.stringify(seen['30m'])}`)
})

check('G2.6', 2, 'turning the gateway off answers 503 on the next call, and nothing is forwarded', async () => {
  const g = await gateway()
  await putSettings({ enabled: false })
  const before = ctx.fake.requests.length
  const r = await gw('/api/chat', { key: g.keyA.key, body: chatBody() })
  await putSettings({ enabled: true })
  return ok(r.status === 503 && ctx.fake.requests.length === before, `${r.status} ${r.text}`)
})

check('G2.6', 2, 'invalid settings are refused field by field', async () => {
  await gateway()
  const cur = (await adminApi(ctx.admin, 'GET', '/settings')).json
  const r = await adminApi(ctx.admin, 'PUT', '/settings', { ...cur, numCtx: 1000, replyReserve: 9000, keepAlive: 'soon', logBodies: 'all' })
  const fields = Object.keys(r.json?.errors ?? {}).map((f) => f.toLowerCase())
  const want = ['numctx', 'replyreserve', 'keepalive', 'logbodies']
  return ok(r.status === 400 && want.every((f) => fields.includes(f)), `${r.status} fields ${show(fields)}`)
})

// ---- G2.7 ----

check('G2.7', 2, 'fake-404 and fake-500 come back with Ollama\'s own status and bytes', async () => {
  const g = await gateway()
  const out = []
  for (const model of ['fake-404', 'fake-500']) {
    const before = ctx.fake.requests.length
    const r = await gw('/api/chat', { key: g.keyA.key, body: chatBody(model) })
    const up = ctx.fake.since(before).find((q) => q.path === '/api/chat')
    out.push({ model, status: r.status, same: r.sha === up?.sentSha256, text: r.text })
  }
  return ok(out[0].status === 404 && out[1].status === 500 && out.every((o) => o.same),
    out.map((o) => `${o.model} ${o.status} same bytes ${o.same} ${o.text}`).join(' · '))
})

check('G2.7', 2, 'a stopped Ollama gives 502', async () => {
  const g = await gateway()
  await ctx.fake.stop()
  let r
  try { r = await gw('/api/chat', { key: g.keyA.key, body: chatBody() }) } finally { await ctx.fake.restart() }
  return ok(r.status === 502 && r.json?.error?.startsWith('oddity:'), `${r.status} ${r.text}`)
})

check('G2.7', 2, 'a mid-stream error line passes through and the row says upstream_error', async () => {
  const g = await gateway()
  const before = ctx.fake.requests.length
  const r = await gw('/api/chat', { key: g.keyA.key, body: chatBody('fake-midstream-error') })
  const up = ctx.fake.since(before).find((q) => q.path === '/api/chat')
  const row = await rowFor(g.a, r.correlationId)
  return ok(r.status === 200 && r.sha === up?.sentSha256 && r.text.includes('"error"') && row?.outcome === 'upstream_error',
    `status ${r.status} · same bytes ${r.sha === up?.sentSha256} · row ${row?.outcome} "${row?.error}"`)
})

check('G2.7', 2, 'model management paths are 403 and never forwarded', async () => {
  const g = await gateway()
  const before = ctx.fake.requests.length
  const paths = [['POST', '/api/pull'], ['POST', '/api/push'], ['POST', '/api/create'], ['POST', '/api/copy'], ['DELETE', '/api/delete'], ['HEAD', '/api/blobs/sha256:abc'], ['POST', '/api/blobs/sha256:abc']]
  const out = []
  for (const [method, p] of paths) {
    const r = await gw(p, { key: g.keyA.key, method, body: method === 'HEAD' ? undefined : { model: 'fake-ok' } })
    out.push(`${method} ${p} ${r.status}`)
  }
  return ok(out.every((s) => s.endsWith(' 403')) && ctx.fake.requests.length === before, `${out.join(' · ')} · forwarded ${ctx.fake.requests.length - before}`)
})

check('G2.7', 2, 'an unknown gateway path is a JSON 404', async () => {
  const g = await gateway()
  const r = await gw('/api/nope', { key: g.keyA.key, method: 'GET' })
  return ok(r.status === 404 && r.json?.error?.startsWith('oddity:'), `${r.status} ${r.text}`)
})

check('G2.7', 2, 'a key limited to 3 a minute gets 429 on its fourth call', async () => {
  const g = await gateway()
  const key = await createKey(g.a, { name: 'three a minute', ratePerMinute: 3 })
  const before = ctx.fake.requests.length
  const statuses = []
  for (let i = 0; i < 4; i++) statuses.push((await gw('/api/chat', { key: key.key, body: chatBody() })).status)
  const forwarded = ctx.fake.since(before).filter((q) => q.path === '/api/chat').length
  return ok(statuses.join(',') === '200,200,200,429' && forwarded === 3, `${statuses.join(',')} · forwarded ${forwarded}`)
})

check('G2.7', 2, 'a model outside the allowed list is a 403; /api/tags lists only allowed models', async () => {
  const g = await gateway()
  await putSettings({ allowedModels: ['fake-ok', 'fake-slow'] })
  const before = ctx.fake.requests.length
  const r = await gw('/api/chat', { key: g.keyA.key, body: chatBody('fake-nothink') })
  const forwarded = ctx.fake.since(before).filter((q) => q.path === '/api/chat').length
  const tags = await gw('/api/tags', { key: g.keyA.key, method: 'GET' })
  await putSettings({ allowedModels: [] })
  const names = (tags.json?.models ?? []).map((m) => m.name)
  return ok(r.status === 403 && forwarded === 0 && sameSet(names, ['fake-ok', 'fake-slow']),
    `chat fake-nothink ${r.status} · forwarded ${forwarded} · tags ${show(names)}`)
})

// ---- G2.8 ----

check('G2.8', 2, "the row holds caller, address, key, model and the fake's exact usage", async () => {
  const g = await gateway()
  const r = await gw('/api/chat', { key: g.keyA.key, body: chatBody() })
  const row = await rowFor(g.a, r.correlationId)
  const want = {
    clientIp: '127.0.0.1', userAgent: EVAL_UA, keyId: g.keyA.id, keyName: 'key A', endpoint: 'chat',
    requestedModel: 'fake-ok', model: 'fake-ok', stream: true, statusCode: 200, outcome: 'ok', doneReason: 'stop',
    promptTokens: FAKE_USAGE.prompt_eval_count, cachedPromptTokens: FAKE_USAGE.prompt_eval_cached_count,
    outputTokens: FAKE_USAGE.eval_count, loadMs: FAKE_USAGE.load_duration / 1e6,
    promptMs: FAKE_USAGE.prompt_eval_duration / 1e6, generationMs: FAKE_USAGE.eval_duration / 1e6,
    correlationId: r.correlationId,
  }
  const bad = Object.entries(want).filter(([k, v]) => row?.[k] !== v).map(([k, v]) => `${k}: ${JSON.stringify(row?.[k])} ≠ ${JSON.stringify(v)}`)
  return ok(row && bad.length === 0, bad.join(' · ') || `all ${Object.keys(want).length} fields match`)
})

check('G2.8', 2, 'first-byte and total time are measured, and total covers the fake\'s delay', async () => {
  const g = await gateway()
  const r = await gw('/api/chat', { key: g.keyA.key, body: chatBody('fake-slow') })
  const row = await rowFor(g.a, r.correlationId)
  // fake-slow pauses 300 ms after each of its three frames.
  return ok(row && row.totalMs >= 900 && row.firstByteMs >= 0 && row.firstByteMs < 300 && row.firstByteMs <= row.totalMs,
    `firstByteMs ${row?.firstByteMs} · totalMs ${row?.totalMs}`)
})

check('G2.8', 2, 'exactly one row per chat, generate, embed and embeddings call — none for tags, show, version or ps', async () => {
  await gateway()
  const u = await users()
  const key = await createKey(u.gc2, { name: 'one row each' })
  const k = key.key
  await gw('/api/chat', { key: k, body: chatBody('fake-ok', { stream: false }) })
  await gw('/api/generate', { key: k, body: { model: 'fake-ok', prompt: 'x' } })
  await gw('/api/embed', { key: k, body: { model: 'fake-ok', input: ['a', 'b'] } })
  await gw('/api/embeddings', { key: k, body: { model: 'fake-ok', prompt: 'a' } })
  await gw('/api/tags', { key: k, method: 'GET' })
  await gw('/api/show', { key: k, body: { model: 'fake-ok' } })
  await gw('/api/version', { key: k, method: 'GET' })
  await gw('/api/ps', { key: k, method: 'GET' })
  await sleep(300)
  const mine = (await rows(u.gc2, `keyId=${key.id}`)).map((x) => x.endpoint).sort()
  return ok(mine.join(',') === 'chat,embed,embeddings,generate', `rows ${show(mine)}`)
})

// ---- G2.9 ----

check('G2.9', 2, 'a client that disconnects mid-stream leaves a client_abort row, and the fake saw the close', async () => {
  const g = await gateway()
  const before = ctx.fake.requests.length
  const ac = new AbortController()
  let correlationId = null
  await gw('/api/chat', {
    key: g.keyA.key, body: chatBody('fake-slow'), signal: ac.signal,
    onFirstChunk: (res) => { correlationId = res.headers.get('x-correlation-id'); ac.abort() },
  }).catch(() => null)
  const row = correlationId ? await rowFor(g.a, correlationId) : null
  const up = ctx.fake.since(before).find((q) => q.path === '/api/chat')
  for (let i = 0; i < 20 && up && !up.closedEarly; i++) await sleep(100)
  return ok(row?.outcome === 'client_abort' && up?.closedEarly === true, `row ${row?.outcome} · fake saw close ${up?.closedEarly}`)
})

// ---- G2.10 ----

check('G2.10', 2, "user B cannot list, read, change or delete user A's keys or requests (404)", async () => {
  const g = await gateway()
  const r = await gw('/api/chat', { key: g.keyA.key, body: chatBody() })
  const rowA = await rowFor(g.a, r.correlationId)
  const keysB = (await adminApi(g.b, 'GET', '/keys')).json ?? []
  const reqsB = await rows(g.b)
  const res = {
    listKeys: keysB.some((k) => k.id === g.keyA.id),
    listRequests: reqsB.some((x) => x.id === rowA?.id),
    readRequest: (await adminApi(g.b, 'GET', `/requests/${rowA?.id}`)).status,
    putKey: (await adminApi(g.b, 'PUT', `/keys/${g.keyA.id}`, { name: 'stolen', ratePerMinute: 0, enabled: true })).status,
    deleteKey: (await adminApi(g.b, 'DELETE', `/keys/${g.keyA.id}`)).status,
  }
  const stillA = ((await adminApi(g.a, 'GET', '/keys')).json ?? []).find((k) => k.id === g.keyA.id)
  return ok(rowA && !res.listKeys && !res.listRequests && res.readRequest === 404 && res.putKey === 404 && res.deleteKey === 404 && stillA?.name === 'key A',
    `${JSON.stringify(res)} · A's key name "${stillA?.name}"`)
})

check('G2.10', 2, 'OddityRequests has no foreign key, and rows survive deleting the key', async () => {
  const g = await gateway()
  const key = await createKey(g.a, { name: 'short-lived' })
  const r = await gw('/api/chat', { key: key.key, body: chatBody() })
  await rowFor(g.a, r.correlationId)
  const del = await adminApi(g.a, 'DELETE', `/keys/${key.id}`)
  const left = await rows(g.a, `keyId=${key.id}`)
  const fks = await sql("select * from pragma_foreign_key_list('OddityRequests')")
  return ok(del.status === 204 && left.length === 1 && left[0].keyName === 'short-lived' && fks.length === 0,
    `delete ${del.status} · rows after ${left.length} · foreign keys ${fks.length}`)
})

// ---- G2.11 ----

const CLIENTS = path.join(EVAL_ROOT, 'clients')

/**
 * Runs a child process without blocking the event loop. The fake Ollama lives in
 * this process, so anything that calls the gateway must never be a spawnSync.
 */
function runAsync(cmd, args, { cwd, env, timeoutMs = 60_000 } = {}) {
  return new Promise((resolve) => {
    const p = spawn(cmd, args, { cwd, env: { ...process.env, ...env } })
    let stdout = ''
    let stderr = ''
    p.stdout.on('data', (d) => { stdout += d })
    p.stderr.on('data', (d) => { stderr += d })
    const timer = setTimeout(() => { stderr += `\n[timed out after ${timeoutMs} ms]`; p.kill() }, timeoutMs)
    p.on('error', (e) => { clearTimeout(timer); resolve({ status: -1, stdout, stderr: String(e) }) })
    p.on('close', (status) => { clearTimeout(timer); resolve({ status, stdout, stderr }) })
  })
}

/** The Python client in a venv under the temp folder, or null when Python is not installed. */
function ensurePyClient() {
  const py = spawnSync('python', ['--version'], { encoding: 'utf8', shell: process.platform === 'win32' })
  if (py.status !== 0) return null
  const venv = path.join(CLIENTS, 'py')
  const exe = path.join(venv, process.platform === 'win32' ? 'Scripts/python.exe' : 'bin/python')
  if (!fs.existsSync(exe)) {
    const mk = spawnSync('python', ['-m', 'venv', venv], { encoding: 'utf8', shell: process.platform === 'win32' })
    if (mk.status !== 0) throw new Error(`venv failed: ${mk.stderr}`)
    const pip = spawnSync(exe, ['-m', 'pip', 'install', '--quiet', 'ollama==0.6.3'], { encoding: 'utf8' })
    if (pip.status !== 0) throw new Error(`pip install ollama failed: ${pip.stderr}`)
  }
  return exe
}

/** Installs a client once, in the temp folder — never in the repo. */
function ensureJsClient() {
  const dir = path.join(CLIENTS, 'js')
  if (!fs.existsSync(path.join(dir, 'node_modules', 'ollama', 'package.json'))) {
    fs.mkdirSync(dir, { recursive: true })
    const r = spawnSync('npm', ['install', '--prefix', dir, '--no-save', '--no-audit', '--no-fund', 'ollama@0.6.4'], { encoding: 'utf8', shell: process.platform === 'win32' })
    if (r.status !== 0) throw new Error(`npm install ollama failed: ${r.stderr}`)
  }
  return path.join(dir, 'node_modules', 'ollama', 'dist', 'index.mjs')
}

check('G2.11', 2, 'the ollama npm client completes a streamed chat, a non-streamed chat and a list', async () => {
  const g = await gateway()
  const { Ollama } = await import(pathToFileURL(ensureJsClient()).href)
  const client = new Ollama({ host: `${ctx.api.url}/oddity`, headers: { Authorization: `Bearer ${g.keyA.key}` } })
  const before = ctx.fake.requests.length
  let streamed = ''
  for await (const part of await client.chat({ model: 'fake-ok', messages: [{ role: 'user', content: 'hi' }], stream: true })) streamed += part.message.content
  const whole = await client.chat({ model: 'fake-ok', messages: [{ role: 'user', content: 'hi' }], stream: false })
  const list = await client.list()
  const seen = ctx.fake.since(before).map((q) => `${q.method} ${q.path}`)
  return ok(streamed === 'Hello from fake.' && whole.message.content === 'Hello from fake.' && whole.eval_count === FAKE_USAGE.eval_count
    && list.models.some((m) => m.name === 'fake-ok') && seen.filter((s) => s === 'POST /api/chat').length === 2 && seen.includes('GET /api/tags'),
  `streamed "${streamed}" · whole "${whole.message.content}" · list ${list.models.length} models · fake saw ${show(seen)}`)
})

check('G2.11', 2, 'the ollama Python client completes a streamed chat and a list', async () => {
  const g = await gateway()
  const exe = ensurePyClient()
  if (!exe) return { pass: false, skip: true, detail: 'SKIPPED: Python is not available' }
  const script = [
    'import os, ollama',
    "c = ollama.Client(host=os.environ['ODDITY_BASE'], headers={'Authorization': 'Bearer ' + os.environ['ODDITY_KEY']})",
    "text = ''.join(p.message.content for p in c.chat(model='fake-ok', messages=[{'role': 'user', 'content': 'hi'}], stream=True))",
    "names = [m.model for m in c.list().models]",
    "print(text + '|' + ','.join(names))",
  ].join('\n')
  const scriptDir = path.join(EVAL_ROOT, 'py-scripts')
  fs.mkdirSync(scriptDir, { recursive: true })
  const file = path.join(scriptDir, 'g2_11.py')
  fs.writeFileSync(file, script)
  const run = await runAsync(exe, ['-I', file], { env: { ODDITY_BASE: `${ctx.api.url}/oddity`, ODDITY_KEY: g.keyA.key } })
  const [text, names] = (run.stdout ?? '').trim().split('|')
  return ok(run.status === 0 && text === 'Hello from fake.' && (names ?? '').split(',').includes('fake-ok'),
    run.status === 0 ? `streamed "${text}" · list ${names}` : `exit ${run.status}: ${run.stderr.slice(-300)}`)
})

// ---- G2.12 ----

check('G2.12', 2, 'the three Oddity tables exist with the expected columns', async () => {
  await gateway()
  const want = {
    // PublicBaseUrl (the docs round) is last in OdditySettings: an upgrade appends it there.
    OddityKeys: ['Id', 'OwnerUserId', 'Name', 'Note', 'Prefix', 'KeyHash', 'DefaultProfileId', 'RatePerMinute', 'Enabled', 'ExpiresAtUtc', 'CreatedAtUtc'],
    OdditySettings: ['Id', 'Enabled', 'DefaultModel', 'AllowedModels', 'NumCtx', 'ReplyReserve', 'KeepAlive', 'DefaultRatePerMinute', 'MaxInlineFileMb', 'LogBodies', 'RetentionDays', 'UpdatedAtUtc', 'UpdatedByUserId', 'PublicBaseUrl'],
    OddityRequests: ['Id', 'TimestampUtc', 'CorrelationId', 'OwnerUserId', 'KeyId', 'KeyName', 'ClientIp', 'UserAgent', 'Endpoint', 'RequestedModel', 'Model', 'Profile', 'Stream', 'StatusCode', 'Outcome', 'Error', 'DoneReason', 'PromptTokens', 'CachedPromptTokens', 'OutputTokens', 'LoadMs', 'PromptMs', 'GenerationMs', 'FirstByteMs', 'TotalMs', 'EstimatedPromptTokens', 'AddedTokens', 'ReceiptJson', 'RequestPreview', 'ResponsePreview', 'RequestBytes', 'ResponseBytes'],
  }
  const bad = []
  for (const [table, cols] of Object.entries(want)) {
    const have = (await sql(`select name from pragma_table_info('${table}')`)).map((c) => c.name)
    if (!sameSet(have, cols)) bad.push(`${table}: missing ${show(cols.filter((c) => !have.includes(c)))} extra ${show(have.filter((c) => !cols.includes(c)))}`)
  }
  const idx = (await sql("select name from sqlite_master where type = 'index' and tbl_name = 'OddityRequests'")).map((i) => i.name)
  const hasIdx = idx.some((n) => /OwnerUserId_TimestampUtc/.test(n)) && idx.some((n) => /KeyId_TimestampUtc/.test(n))
  return ok(bad.length === 0 && hasIdx, (bad.join(' · ') || 'columns match') + ` · indexes ${show(idx)}`)
})

// ---- G2.13 ----

check('G2.13', 2, 'the app\'s own /api/models, /api/chat and document upload still work against the fake', async () => {
  await gateway()
  const models = await call(ctx.api.url, 'GET', '/api/models', { token: ctx.admin.token })
  const before = ctx.fake.requests.length
  const chat = await call(ctx.api.url, 'POST', '/api/chat', { token: ctx.admin.token, body: { source: 'local', model: 'fake-ok', message: 'hello' } })
  const sawChat = ctx.fake.since(before).some((q) => q.path === '/api/chat')
  const form = new FormData()
  form.append('file', new Blob(['PO 1234\nShip window: 2026-11-01 to 2026-11-15\n'], { type: 'text/plain' }), 'po-1234.txt')
  const up = await fetch(`${ctx.api.url}/api/documents`, { method: 'POST', headers: { Authorization: `Bearer ${ctx.admin.token}` }, body: form })
  const doc = await up.json().catch(() => null)
  const sawEmbed = ctx.fake.since(before).some((q) => q.path === '/api/embeddings' || q.path === '/api/embed')
  return ok(models.status === 200 && chat.status === 200 && chat.text.includes('Hello') && sawChat
    && up.status === 200 && doc?.status === 'ready' && doc?.chunks >= 1 && sawEmbed,
  `models ${models.status} · chat ${chat.status} (fake saw it ${sawChat}) · upload ${up.status} ${doc?.status} ${doc?.chunks} chunk(s), embedded ${sawEmbed}`)
})

check('G2.13', 2, '/oddity and /oddity/docs get the same answer as /dashboard; /oddity/api/nope is the gateway\'s 404', async () => {
  await gateway()
  const get = (p) => fetch(ctx.api.url + p).then(async (r) => ({ status: r.status, type: r.headers.get('content-type'), body: await r.text() }))
  const [dash, odd, docs, nope] = await Promise.all(['/dashboard', '/oddity', '/oddity/docs', '/oddity/api/nope'].map(get))
  const same = (x) => x.status === dash.status && x.body === dash.body
  return ok(dash.status === 200 && dash.type?.includes('text/html') && same(odd) && same(docs) && nope.status === 404 && nope.type?.includes('json'),
    `/dashboard ${dash.status} ${dash.type} · /oddity same ${same(odd)} · /oddity/docs same ${same(docs)} · /oddity/api/nope ${nope.status} ${nope.type}`)
})

// ─────────────────────────────── Docs round (built in Phase 2) ───────────────────────────────
//
// The Docs board and the public /oddity/docs page, brought forward from Phase 5 on
// KAKAROT's call, plus the public gateway URL setting. These checks hold the docs
// to the code: every route documented and nothing invented, fields equal to the
// server's own OpenAPI file, and every example run unchanged against the fake.

const lib = (name) => import(pathToFileURL(path.join(REPO, 'frontend', 'src', 'lib', name)).href)

/** Gateway routes as OddityGatewayApis.cs maps them: open ("METHOD /api/x") and closed ("/api/x"). */
function gatewayRoutesInSource() {
  const src = read('backend/MinimalApis/OddityGatewayApis.cs')
  const open = []
  const loop = src.match(/foreach \(var endpoint in new\[\] \{([\s\S]*?)\}\)\s*gw\.MapPost/)?.[1] ?? ''
  for (const m of loop.matchAll(/"([^"]+)"/g)) open.push(`POST /api/${m[1]}`)
  for (const m of src.matchAll(/gw\.Map(Get|Post|Put|Delete)\("(\/[^"]+)"/g)) open.push(`${m[1].toUpperCase()} /api${m[2]}`)
  const closedBlock = src.match(/foreach \(var pattern in new\[\] \{([\s\S]*?)\}\)/)?.[1] ?? ''
  const closed = [...closedBlock.matchAll(/"([^"]+)"/g)].map((m) => `/api${m[1].replace('/{**rest}', '/*')}`)
  return { open, closed }
}

async function swagger() {
  ctx.swagger ??= await (await fetch(`${ctx.api.url}/swagger/v1/swagger.json`)).json()
  return ctx.swagger
}

const refName = (schema) => (schema?.$ref ?? schema?.items?.$ref ?? '').split('/').pop() + (schema?.items ? '[]' : '')
const lower = (xs) => xs.map((x) => x.toLowerCase())

check('DOC.1', 2, 'the public gateway URL saves normalised, refuses a non-http(s) address, and clears', async () => {
  await gateway()
  const saved = await putSettings({ publicBaseUrl: 'https://AI.Example.com/oddity/' })
  const cur = (await adminApi(ctx.admin, 'GET', '/settings')).json
  const refused = []
  for (const bad of ['ftp://files.example.com/oddity', 'not a url', 'https://ai.example.com/oddity?x=1']) {
    const r = await adminApi(ctx.admin, 'PUT', '/settings', { ...cur, publicBaseUrl: bad })
    refused.push(`${bad} → ${r.status}${'publicBaseUrl' in (r.json?.errors ?? {}) ? ' (field named)' : ''}`)
  }
  const cleared = await putSettings({ publicBaseUrl: '' })
  return ok(saved.publicBaseUrl === 'https://ai.example.com/oddity' && refused.every((s) => s.endsWith('400 (field named)')) && cleared.publicBaseUrl === '',
    `saved ${JSON.stringify(saved.publicBaseUrl)} · ${refused.join(' · ')} · cleared ${JSON.stringify(cleared.publicBaseUrl)}`)
})

check('DOC.2', 2, 'the docs list exactly the gateway routes and error statuses the code has', async () => {
  const docs = await lib('oddity-docs.js')
  const src = gatewayRoutesInSource()
  const open = docs.GATEWAY_ENDPOINTS.map((e) => `${e.method} ${e.path}`)
  const statuses = new Set()
  for (const f of ['backend/Oddity/OddityProxy.cs', 'backend/MinimalApis/OddityGatewayApis.cs'])
    for (const m of read(f).matchAll(/StatusCodes\.Status(\d{3})/g)) if (m[1] !== '200') statuses.add(Number(m[1]))
  const documented = docs.GATEWAY_ERRORS.map((e) => e.status)
  const diff = (a, b) => a.filter((x) => !b.includes(x))
  const problems = [
    ...diff(src.open, open).map((r) => `undocumented ${r}`), ...diff(open, src.open).map((r) => `invented ${r}`),
    ...diff(src.closed, docs.CLOSED_ENDPOINTS).map((r) => `undocumented closed ${r}`), ...diff(docs.CLOSED_ENDPOINTS, src.closed).map((r) => `invented closed ${r}`),
    ...diff([...statuses], documented).map((s) => `undocumented status ${s}`), ...diff(documented, [...statuses]).map((s) => `invented status ${s}`),
  ]
  return ok(src.open.length > 0 && problems.length === 0,
    problems.join(' · ') || `${open.length} open routes, ${docs.CLOSED_ENDPOINTS.length} closed, statuses ${show([...statuses].sort())}`)
})

check('DOC.3', 2, 'every documented gateway endpoint answers; every documented closed one is 403', async () => {
  const g = await gateway()
  const docs = await lib('oddity-docs.js')
  const bodies = {
    '/api/chat': chatBody(), '/api/generate': { model: 'fake-ok', prompt: 'x' }, '/api/embed': { model: 'fake-ok', input: 'x' },
    '/api/embeddings': { model: 'fake-ok', prompt: 'x' }, '/api/show': { model: 'fake-ok' },
  }
  const bad = []
  for (const e of docs.GATEWAY_ENDPOINTS) {
    const r = await gw(e.path, { key: g.keyA.key, method: e.method, body: e.method === 'GET' ? undefined : bodies[e.path] })
    if (r.status !== 200) bad.push(`${e.method} ${e.path} ${r.status}`)
  }
  for (const p of docs.CLOSED_ENDPOINTS) {
    const r = await gw(p.replace('/*', '/sha256:abc'), { key: g.keyA.key, body: { model: 'fake-ok' } })
    if (r.status !== 403) bad.push(`${p} ${r.status}`)
  }
  return ok(bad.length === 0, bad.join(' · ') || `${docs.GATEWAY_ENDPOINTS.length} answered 200 · ${docs.CLOSED_ENDPOINTS.length} closed answered 403`)
})

check('DOC.4', 2, 'the docs list exactly the /api/oddity routes in Swagger, and Swagger hides the gateway (R11)', async () => {
  const docs = await lib('oddity-docs.js')
  const sw = await swagger()
  const inSwagger = []
  const untagged = []
  for (const [p, ops] of Object.entries(sw.paths ?? {})) {
    for (const [method, op] of Object.entries(ops)) {
      if (!p.startsWith('/api/oddity')) continue
      inSwagger.push(`${method.toUpperCase()} ${p}`)
      if (!(op.tags ?? []).includes('Oddity')) untagged.push(`${method.toUpperCase()} ${p}`)
    }
  }
  const documented = docs.ADMIN_ENDPOINTS.map((e) => `${e.method} ${e.path}`)
  const gatewayLeak = Object.keys(sw.paths ?? {}).filter((p) => p.startsWith('/oddity'))
  const missing = inSwagger.filter((r) => !documented.includes(r))
  const invented = documented.filter((r) => !inSwagger.includes(r))
  return ok(inSwagger.length > 0 && missing.length === 0 && invented.length === 0 && gatewayLeak.length === 0 && untagged.length === 0,
    `${inSwagger.length} admin routes` + (missing.length ? ` · undocumented ${show(missing)}` : '') + (invented.length ? ` · invented ${show(invented)}` : '')
    + (gatewayLeak.length ? ` · gateway in Swagger ${show(gatewayLeak)}` : ' · gateway hidden') + (untagged.length ? ` · not tagged Oddity ${show(untagged)}` : ''))
})

check('DOC.5', 2, 'documented bodies, responses, fields and query parameters equal the OpenAPI schemas', async () => {
  const docs = await lib('oddity-docs.js')
  const sw = await swagger()
  const schemas = sw.components?.schemas ?? {}
  const bad = []
  const compareFields = (name) => {
    const want = lower((docs.SCHEMAS[name] ?? []).map((f) => f.name))
    const have = lower(Object.keys(schemas[name]?.properties ?? {}))
    if (!docs.SCHEMAS[name]) bad.push(`${name} not documented`)
    else if (!schemas[name]) bad.push(`${name} not in OpenAPI`)
    else if (!sameSet(want, have)) bad.push(`${name}: docs ${show(want)} vs OpenAPI ${show(have)}`)
  }
  for (const e of docs.ADMIN_ENDPOINTS) {
    const op = sw.paths?.[e.path]?.[e.method.toLowerCase()]
    if (!op) continue                                     // DOC.4 reports a missing route
    const body = refName(op.requestBody?.content?.['application/json']?.schema)
    if ((e.body ?? '') !== (op.requestBody ? body : '')) bad.push(`${e.method} ${e.path} body: docs ${e.body ?? '—'} vs OpenAPI ${body || '—'}`)
    const okResponse = op.responses?.['200'] ?? op.responses?.['204']
    const response = refName(okResponse?.content?.['application/json']?.schema)
    if ((e.response ?? '') !== response) bad.push(`${e.method} ${e.path} response: docs ${e.response ?? '—'} vs OpenAPI ${response || '—'}`)
    if (e.query) {
      const want = lower(docs.SCHEMAS[e.query].map((f) => f.name))
      const have = lower((op.parameters ?? []).filter((p) => p.in === 'query').map((p) => p.name))
      if (!sameSet(want, have)) bad.push(`${e.path} query: docs ${show(want)} vs OpenAPI ${show(have)}`)
    }
    for (const name of [e.body, e.response?.replace('[]', '')].filter(Boolean)) compareFields(name)
  }
  // Schemas reached through another one (a page's items, a detail's request).
  for (const name of docs.NESTED_SCHEMAS ?? []) compareFields(name)
  return ok(bad.length === 0, bad.join(' · ') || 'every body, response, field and query parameter matches')
})

/** Git Bash on Windows (never WSL's bash, whose 127.0.0.1 is not this machine), bash elsewhere. */
function findBash() {
  const candidates = [process.env.ODDITY_EVAL_BASH, 'C:/Program Files/Git/bin/bash.exe', 'C:/Program Files/Git/usr/bin/bash.exe',
    process.platform === 'win32' ? null : 'bash'].filter(Boolean)
  return candidates.find((c) => spawnSync(c, ['-c', 'curl --version'], { encoding: 'utf8' }).status === 0) ?? null
}

/** Runs one documented example unchanged, filled with a real base URL and key, and reports what the fake saw. */
async function runExample(id, launch) {
  const g = await gateway()
  const { EXAMPLES, fillExample } = await lib('oddity-examples.js')
  const ex = EXAMPLES.find((e) => e.id === id)
  if (!ex) return ok(false, `no "${id}" example in lib/oddity-examples.js`)
  const code = fillExample(ex.code, { base: `${ctx.api.url}/oddity`, key: g.keyA.key })
  if (code.includes('{{')) return ok(false, 'a placeholder other than {{BASE}} and {{KEY}} is left')
  const before = ctx.fake.requests.length
  const run = await launch(code)
  if (run.skip) return run
  const up = ctx.fake.since(before).find((q) => q.path === '/api/chat')
  return { run, up, detail: `exit ${run.status}` + (run.status === 0 ? '' : ` ${run.stderr.slice(-300)}`) + ` · fake saw /api/chat ${!!up}${up?.headers.authorization ? ' WITH the key' : ''}` }
}

check('DOC.6', 2, 'the curl example runs unchanged and streams a complete reply', async () => {
  const bash = findBash()
  const res = await runExample('curl', (code) => (bash ? runAsync(bash, ['-c', code]) : { skip: true, pass: false, detail: 'SKIPPED: no bash with curl' }))
  if (res.skip || res.pass === false) return res
  const last = (() => { try { return JSON.parse(res.run.stdout.trim().split('\n').at(-1)) } catch { return null } })()
  return ok(res.run.status === 0 && last?.done === true && res.up && !res.up.headers.authorization, `${res.detail} · last line done=${last?.done}`)
})

check('DOC.7', 2, 'the JavaScript example runs unchanged with the ollama npm client', async () => {
  ensureJsClient()
  const dir = path.join(CLIENTS, 'js')
  const res = await runExample('js', (code) => {
    const file = path.join(dir, 'docs-example.mjs')
    fs.writeFileSync(file, code)
    return runAsync(process.execPath, [file], { cwd: dir })
  })
  if (res.pass === false) return res
  return ok(res.run.status === 0 && res.run.stdout.trim() === 'Hello from fake.' && res.up?.body?.stream === false, `${res.detail} · printed "${res.run.stdout.trim()}"`)
})

check('DOC.8', 2, 'the Python example runs unchanged with the ollama PyPI client', async () => {
  const exe = ensurePyClient()
  const res = await runExample('python', (code) => {
    if (!exe) return { skip: true, pass: false, detail: 'SKIPPED: Python is not available' }
    const dir = path.join(EVAL_ROOT, 'py-scripts')
    fs.mkdirSync(dir, { recursive: true })
    const file = path.join(dir, 'docs_example.py')
    fs.writeFileSync(file, code)
    return runAsync(exe, ['-I', file])
  })
  if (res.skip || res.pass === false) return res
  return ok(res.run.status === 0 && res.run.stdout.trim() === 'Hello from fake.' && res.up?.body?.stream === true, `${res.detail} · printed "${res.run.stdout.trim()}"`)
})

/** The project a reader pastes the C# example into: a plain .NET 8 console app. */
const CS_PROJECT = `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
`

check('DOC.9', 2, 'the C# example builds with no warnings in a .NET 8 console project and runs unchanged', async () => {
  const sdk = spawnSync('dotnet', ['--version'], { encoding: 'utf8', shell: process.platform === 'win32' })
  const dir = path.join(EVAL_ROOT, 'cs-example')
  let build = null
  const res = await runExample('csharp', async (code) => {
    if (sdk.status !== 0) return { skip: true, pass: false, detail: 'SKIPPED: no dotnet SDK' }
    fs.rmSync(dir, { recursive: true, force: true })
    fs.mkdirSync(dir, { recursive: true })
    fs.writeFileSync(path.join(dir, 'example.csproj'), CS_PROJECT)
    fs.writeFileSync(path.join(dir, 'Program.cs'), code)
    build = await runAsync('dotnet', ['build', '-nologo', '-v', 'q'], { cwd: dir, timeoutMs: 300_000 })
    if (build.status !== 0) return build
    return runAsync('dotnet', ['run', '--no-build'], { cwd: dir, timeoutMs: 120_000 })
  })
  if (res.skip || res.pass === false) return res
  const warnings = build?.stdout.match(/(\d+) Warning\(s\)/)?.[1]
  return ok(build?.status === 0 && warnings === '0' && res.run.status === 0 && res.run.stdout.includes('"done":true') && res.up?.body?.stream === false,
    `build ${build?.status} (${warnings ?? '?'} warnings) · ${res.detail} · printed ${res.run.stdout.trim().slice(0, 50)}…`)
})

check('DOC.10', 2, 'no v-html and no raw colours under components/oddity (G5.3, G5.8)', async () => {
  const dir = path.join(REPO, 'frontend', 'src', 'components', 'oddity')
  const files = [...fs.readdirSync(dir).map((f) => path.join(dir, f)), path.join(REPO, 'frontend', 'src', 'components', 'tabs', 'OddityTab.vue')]
  const hits = []
  for (const f of files) {
    fs.readFileSync(f, 'utf8').split('\n').forEach((line, i) => {
      if (/v-html|#[0-9a-fA-F]{3,8}\b|rgb\(/.test(line)) hits.push(`${path.basename(f)}:${i + 1}`)
    })
  }
  return ok(hits.length === 0, hits.length ? `found at ${show(hits)}` : `${files.length} files clean`)
})

check('DOC.11', 2, '/oddity/docs is a public route rendered outside the login gate', async () => {
  const router = read('frontend/src/app/router.js')
  const app = read('frontend/src/App.vue')
  const route = router.match(/\{\s*path:\s*'\/oddity\/docs'[^}]*\}/)?.[0] ?? ''
  const noTab = route && !route.includes('meta')
  // The page must be chosen before the login gate's v-if/v-else chain begins.
  const docsAt = app.indexOf('<OddityDocsPage')
  const gateAt = app.indexOf('<LoginGate')
  return ok(noTab && docsAt > 0 && docsAt < gateAt && /isPublicDocs/.test(app),
    `route ${route || 'missing'} · rendered before the login gate: ${docsAt > 0 && docsAt < gateAt}`)
})

// ─────────────────────────────── Changing a secret (requested after the docs round) ───────────────────────────────
//
// The seeded administrator's secret is published in the README, so changing it
// has to work before the app is exposed. AUTH.6 changes the admin's own secret and
// is registered last; it puts the secret back when it is done.

const SECRET_PATH = '/api/auth/secret'

async function freshUser(name) {
  const secret = 'start-' + crypto.randomBytes(6).toString('hex')
  return { ...(await login(name, secret)), secret }
}

const changeSecret = (token, currentSecret, newSecret) =>
  call(ctx.api.url, 'POST', SECRET_PATH, { token, body: { currentSecret, newSecret } })

check('AUTH.1', 2, 'changing a secret needs a session: 401 without one', async () => {
  const r = await call(ctx.api.url, 'POST', SECRET_PATH, { body: { currentSecret: 'x', newSecret: 'long-enough-secret' } })
  return ok(r.status === 401, `${r.status}`)
})

check('AUTH.2', 2, 'a wrong current secret is a 403 (never 401, which signs the user out) and changes nothing', async () => {
  const u = await freshUser('eval-secret-wrong')
  const r = await changeSecret(u.token, 'not-it', 'brand-new-secret')
  const tokenStill = (await call(ctx.api.url, 'GET', '/api/me/tabs', { token: u.token })).status
  const secretStill = (await call(ctx.api.url, 'POST', '/api/auth/login', { body: { username: u.username, secret: u.secret } })).status
  return ok(r.status === 403 && tokenStill === 200 && secretStill === 200,
    `${r.status} "${r.json?.error}" · old token ${tokenStill} · old secret ${secretStill}`)
})

check('AUTH.3', 2, 'a new secret that is short or unchanged is a 400', async () => {
  const u = await freshUser('eval-secret-rules')
  const short = await changeSecret(u.token, u.secret, 'short')
  const same = await changeSecret(u.token, u.secret, u.secret)
  return ok(short.status === 400 && same.status === 400,
    `short ${short.status} "${short.json?.error}" · unchanged ${same.status} "${same.json?.error}"`)
})

check('AUTH.4', 2, 'a change rotates the token: old token and old secret stop working, the new ones work', async () => {
  const u = await freshUser('eval-secret-change')
  const next = 'changed-' + crypto.randomBytes(6).toString('hex')
  const r = await changeSecret(u.token, u.secret, next)
  const oldToken = (await call(ctx.api.url, 'GET', '/api/me/tabs', { token: u.token })).status
  const newToken = (await call(ctx.api.url, 'GET', '/api/me/tabs', { token: r.json?.token })).status
  const oldSecret = (await call(ctx.api.url, 'POST', '/api/auth/login', { body: { username: u.username, secret: u.secret } })).status
  const newSecret = (await call(ctx.api.url, 'POST', '/api/auth/login', { body: { username: u.username, secret: next } })).status
  return ok(r.status === 200 && r.json?.token && r.json.token !== u.token
    && oldToken === 401 && newToken === 200 && oldSecret === 401 && newSecret === 200,
  `change ${r.status} · old token ${oldToken} · new token ${newToken} · old secret ${oldSecret} · new secret ${newSecret}`)
})

check('AUTH.5', 2, 'the audit log hides the secret hash and salt, for a new account and for a change (N3)', async () => {
  const u = await freshUser('eval-secret-audit')
  await changeSecret(u.token, u.secret, 'audited-' + crypto.randomBytes(6).toString('hex'))
  let rows = []
  for (let i = 0; i < 40 && rows.length < 2; i++) {
    rows = await sql("select Action, ChangesJson from AuditLogs where EntityName = 'User' and EntityKey = ? order by Id", String(u.id))
    if (rows.length < 2) await sleep(100)
  }
  const [stored] = await sql('select SecretHash, SecretSalt from Users where Id = ?', u.id)
  const leaks = []
  for (const row of rows) {
    for (const c of JSON.parse(row.ChangesJson ?? '[]')) {
      if (['SecretHash', 'SecretSalt'].includes(c.property) && [c.old, c.new].some((v) => v !== null && v !== '***'))
        leaks.push(`${row.Action} ${c.property}`)
    }
    if (row.ChangesJson?.includes(stored.SecretHash) || row.ChangesJson?.includes(stored.SecretSalt)) leaks.push(`${row.Action} holds the stored value`)
  }
  return ok(rows.length >= 2 && leaks.length === 0, `${rows.map((r) => r.Action).join(', ')} · leaks ${show(leaks)}`)
})

check('AUTH.6', 2, 'a changed admin secret survives a restart: the seeder never resets it', async () => {
  const seed = process.env.ODDITY_EVAL_ADMIN_SECRET
  const changed = 'admin-' + crypto.randomBytes(8).toString('hex')
  const r = await changeSecret(ctx.admin.token, seed, changed)
  if (r.status !== 200) return ok(false, `change ${r.status} ${r.text}`)
  await ctx.api.stop()
  ctx.api = await startApi({ fakeUrl: ctx.fake.url, runDir: ctx.api.runDir, port: ctx.api.port })
  const withNew = await call(ctx.api.url, 'POST', '/api/auth/login', { body: { username: ctx.admin.username, secret: changed } })
  const withSeed = await call(ctx.api.url, 'POST', '/api/auth/login', { body: { username: ctx.admin.username, secret: seed } })
  // Put the seed secret back, so the database ends as it started.
  const back = await changeSecret(withNew.json?.token, changed, seed)
  if (back.json?.token) ctx.admin.token = back.json.token
  return ok(withNew.status === 200 && withSeed.status === 401 && back.status === 200,
    `after restart: new secret ${withNew.status} · seed secret ${withSeed.status} · restored ${back.status}`)
})

// ─────────────────────────────── runner ───────────────────────────────

function parseArgs(argv) {
  const a = { phase: Infinity, only: null, build: true, dbCopy: null }
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === '--phase') a.phase = Number(argv[++i])
    else if (argv[i] === '--only') a.only = argv[++i]
    else if (argv[i] === '--no-build') a.build = false
    else if (argv[i] === '--db-copy') a.dbCopy = argv[++i]
  }
  return a
}

/**
 * The built SPA becomes the eval API's wwwroot, as it does in the Docker image,
 * so the SPA fallback can be compared against real pages (G2.13).
 */
function syncWebRoot() {
  const dist = path.join(REPO, 'frontend', 'dist')
  const target = path.join(BIN_DIR, 'wwwroot')
  fs.rmSync(target, { recursive: true, force: true })
  if (fs.existsSync(dist)) fs.cpSync(dist, target, { recursive: true })
}

async function main() {
  const args = parseArgs(process.argv.slice(2))
  const adminUser = process.env.ODDITY_EVAL_ADMIN_USER
  const adminSecret = process.env.ODDITY_EVAL_ADMIN_SECRET
  if (!adminUser || !adminSecret) {
    console.error('Set ODDITY_EVAL_ADMIN_USER and ODDITY_EVAL_ADMIN_SECRET to the seeded administrator.')
    process.exit(2)
  }

  // --only G2.5 runs one gate; --only DOC runs every DOC.n check.
  const selected = checks.filter((c) => c.phase <= args.phase
    && (!args.only || c.id === args.only || c.id.startsWith(args.only + '.')))
  if (!selected.length) { console.error('No checks match.'); process.exit(2) }

  if (args.build) buildApi()
  syncWebRoot()
  fs.mkdirSync(EVAL_ROOT, { recursive: true })
  const runDir = fs.mkdtempSync(path.join(EVAL_ROOT, 'run-'))
  // G2.12: start from a copy of an existing database instead of an empty folder.
  if (args.dbCopy) fs.copyFileSync(args.dbCopy, path.join(runDir, 'rabbitic.db'))

  ctx.fake = await startFakeOllama()
  let failed = 0
  let skipped = 0
  try {
    ctx.api = await startApi({ fakeUrl: ctx.fake.url, runDir })
    ctx.admin = await login(adminUser, adminSecret)

    console.log(`fake ${ctx.fake.url} · api ${ctx.api.url} · db ${ctx.api.dbPath}${args.dbCopy ? ` (copy of ${args.dbCopy})` : ''}\n`)
    console.log('id | check | result | detail')
    for (const c of selected) {
      let res
      try { res = await c.fn() } catch (e) { res = { pass: false, detail: `threw: ${e.message}` } }
      // A skip is reported, never counted as a pass, and does not fail the run.
      if (res.skip) skipped++
      else if (!res.pass) failed++
      console.log(`${c.id} | ${c.name} | ${res.skip ? 'SKIP' : res.pass ? 'PASS' : 'FAIL'} | ${res.detail ?? ''}`)
    }
    const counted = selected.length - skipped
    console.log(`\n${counted - failed}/${counted} passed${skipped ? ` · ${skipped} skipped` : ''}`)
  } finally {
    await ctx.api?.stop()
    await ctx.fake.stop()
  }
  process.exit(failed ? 1 : 0)
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  main().catch((e) => { console.error(e.message ?? e); process.exit(2) })
}
