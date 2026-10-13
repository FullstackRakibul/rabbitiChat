#!/usr/bin/env node
// Model-policy eval — see docs/MODEL_POLICY_PROMPT.md §7.
//
//   node scripts/model-policy-eval.mjs                 every check
//   node scripts/model-policy-eval.mjs --phase 1       checks up to phase 1
//   node scripts/model-policy-eval.mjs --only G1.3     one gate
//   node scripts/model-policy-eval.mjs --no-build      reuse the last eval build
//
// The fake Ollama and the API start-up are REUSED from scripts/oddity-eval.mjs
// (§7: no third copy). This file adds a fake that lists the policy's models and
// records every request, so a check can assert the fake received NOTHING for a
// refused call — which is the whole point of enforcing before the model call.
//
// A check asserts what the fake received and what the client got, never what the
// server says it did. Node 18+, no dependencies.

import http from 'node:http'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { spawnSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'

import { buildApi, startApi, call, fakeEmbedding } from './oddity-eval.mjs'

const REPO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const EVAL_ROOT = path.join(os.tmpdir(), 'model-policy-eval')

// ─────────────────────── expected values, written by hand ───────────────────────
// These mirror the design contract (§1, §3.2), not the code: a value read from
// TabCatalog.cs or ModelPolicy.cs would only prove the code agrees with itself.

/** General's baseline after this build — exactly six, no `jsonto` (§1). */
const GENERAL_TABS = ['dashboard', 'todo', 'notes', 'sources', 'settings', '5lakhs']

const ALLOWED = 'gemma4:12b'          // what General may run
const FORBIDDEN = 'qwen3.8-27b-q3'    // a local model General may not run
const THIRD = 'gemma3:270m'           // for restricting System in G1.10
const EMBED = 'nomic-embed-text'

/** The local models the fake reports installed (§7). */
const LOCAL_MODELS = [ALLOWED, FORBIDDEN, THIRD, EMBED]

/** A cloud model that shares General's allowed NAME — G1.5's trap. */
const CLOUD_MODELS = [ALLOWED, 'cloud-only-model']

const ROLES = ['General', 'System', 'GroundControl', 'SystemAdmin']

// ─────────────────────────── the fake Ollama ───────────────────────────

async function startFake() {
  const requests = []

  const server = http.createServer(async (req, res) => {
    const chunks = []
    for await (const c of req) chunks.push(c)
    const raw = Buffer.concat(chunks).toString('utf8')
    let body = raw
    try { body = raw ? JSON.parse(raw) : null } catch { /* keep raw */ }

    const url = new URL(req.url, 'http://fake')
    requests.push({ method: req.method, path: url.pathname, body })

    const json = (status, obj) => {
      res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8' })
      res.end(JSON.stringify(obj))
    }
    const model = body?.model ?? body?.name ?? ''

    switch (`${req.method} ${url.pathname}`) {
      case 'GET /api/version':
        return json(200, { version: '0.0.0-fake' })

      case 'GET /api/tags':
        return json(200, {
          models: LOCAL_MODELS.map((name, i) => ({
            name, model: name, modified_at: '2026-01-01T00:00:00Z',
            size: 1000 + i, digest: `sha256:${String(i).padStart(64, '0')}`,
            details: { format: 'gguf', family: 'fake', parameter_size: '1B', quantization_level: 'Q4' },
          })),
        })

      case 'POST /api/show':
        return json(200, {
          capabilities: ['completion'],
          model_info: { 'fake.context_length': 16384 },
        })

      case 'POST /api/embed': {
        const input = Array.isArray(body?.input) ? body.input : [body?.input ?? '']
        return json(200, { model, embeddings: input.map((t) => fakeEmbedding(t)) })
      }

      case 'POST /api/embeddings':
        return json(200, { embedding: fakeEmbedding(body?.prompt ?? '') })

      case 'POST /api/chat':
      case 'POST /api/generate': {
        const isChat = url.pathname === '/api/chat'
        // Extraction asks for schema-shaped JSON; anything else gets prose.
        const content = body?.format && typeof body.format === 'object'
          ? JSON.stringify({
              header: { orderNumber: 'MP-1' },
              orderLine: { styleNumber: 'S1', upc: '400110000001', unitPrice: 1, quantity: 1, itemTotal: 1, isPrepack: false },
              prepackComponents: [],
              purchaseOrderTotal: 1,
            })
          : 'OK'
        const frame = (text, done) => isChat
          ? { model, created_at: '2026-01-01T00:00:00Z', message: { role: 'assistant', content: text }, done,
              prompt_eval_count: 10, eval_count: 2 }
          : { model, created_at: '2026-01-01T00:00:00Z', response: text, done,
              prompt_eval_count: 10, eval_count: 2 }

        if (body?.stream === false) return json(200, { ...frame(content, true), done_reason: 'stop' })

        res.writeHead(200, { 'Content-Type': 'application/x-ndjson' })
        res.write(JSON.stringify(frame(content, false)) + '\n')
        res.write(JSON.stringify({ ...frame('', true), done_reason: 'stop' }) + '\n')
        return res.end()
      }

      default:
        return json(404, { error: 'fake: not found' })
    }
  })

  await new Promise((r) => server.listen(0, '127.0.0.1', r))
  return {
    url: `http://127.0.0.1:${server.address().port}`,
    requests,
    /** Requests that would actually run a model. */
    runs: () => requests.filter((r) => r.path === '/api/chat' || r.path === '/api/generate'),
    reset: () => { requests.length = 0 },
    stop: () => new Promise((r) => { server.closeAllConnections(); server.close(() => r()) }),
  }
}

// ─────────────────────────── harness ───────────────────────────

const checks = []
function check(id, phase, name, fn) { checks.push({ id, phase, name, fn }) }
const ok = (pass, detail) => ({ pass, detail })

const ctx = { api: null, fake: null, admin: null, gen: null, sys: null }

async function sqlRows(query) {
  const { DatabaseSync } = await import('node:sqlite')
  const db = new DatabaseSync(ctx.api.dbPath, { readOnly: true })
  try { return db.prepare(query).all() } finally { db.close() }
}

async function login(username, secret) {
  const r = await call(ctx.api.url, 'POST', '/api/auth/login', { body: { username, secret } })
  if (r.status !== 200 || !r.json?.token)
    throw new Error(`login ${username} → ${r.status} ${r.text.slice(0, 200)}`)
  const me = await call(ctx.api.url, 'GET', '/api/me/tabs', { token: r.json.token })
  return { username, token: r.json.token, id: me.json?.id, tabs: me.json?.tabs ?? [], role: me.json?.role }
}

/** Re-read a user's tabs and role from the server. */
async function me(user) {
  const r = await call(ctx.api.url, 'GET', '/api/me/tabs', { token: user.token })
  return r.json ?? {}
}

const asAdmin = (method, p, body) => call(ctx.api.url, method, p, { token: ctx.admin.token, body })

function setRole(id, role) {
  return asAdmin('PUT', `/api/admin/rbac/users/${id}/role`, { role })
}
function setGrants(id, tabKeys) {
  return asAdmin('PUT', `/api/admin/rbac/users/${id}/tabs`, { tabKeys })
}

// ---- the four model entry points, each called the way its own API expects ----

/** /api/chat — plain text, streams NDJSON. */
async function chat(user, model, { source = 'local', message = 'hello' } = {}) {
  const res = await fetch(`${ctx.api.url}/api/chat`, {
    method: 'POST',
    headers: { Authorization: `Bearer ${user.token}`, 'Content-Type': 'application/json' },
    body: JSON.stringify({ model, source, message }),
  })
  return { status: res.status, text: await res.text() }
}

/** /api/documents/workspace-chat — plain text (F5). */
async function docChat(user, model, documentId) {
  const res = await fetch(`${ctx.api.url}/api/documents/workspace-chat`, {
    method: 'POST',
    headers: { Authorization: `Bearer ${user.token}`, 'Content-Type': 'application/json' },
    body: JSON.stringify({ documentId, model, message: 'what is the total?' }),
  })
  return { status: res.status, text: await res.text() }
}

/** /api/documents/{id}/extract — JSON; buyerProfileId is a QUERY parameter. */
async function extract(user, documentId, { model, profileId } = {}) {
  const q = profileId ? `?buyerProfileId=${profileId}` : ''
  return call(ctx.api.url, 'POST', `/api/documents/${documentId}/extract${q}`,
    { token: user.token, body: model ? { model } : {} })
}

/** /api/component-splitter/analyse — NDJSON. */
async function splitter(user, model, { source = 'local' } = {}) {
  const res = await fetch(`${ctx.api.url}/api/component-splitter/analyse`, {
    method: 'POST',
    headers: { Authorization: `Bearer ${user.token}`, 'Content-Type': 'application/json' },
    body: JSON.stringify({
      model, source,
      componentTs: 'export class A { title = "x"; }',
      templateHtml: '<div>{{ title }}</div>',
    }),
  })
  return { status: res.status, text: await res.text() }
}

/** Upload a tiny text PO, returning its document id. */
async function upload(user, name = 'policy-po.txt') {
  const text = [
    '--- page 1 ---',
    'PURCHASE ORDER',
    'Order #: MP-1   PO Type: FLAT',
    'Style  UPC            Unit Price  Qty  Line Total',
    'S1     400110000001   1.00        1    1.00',
    'Purchase Order Total: 1.00',
  ].join('\n')
  const form = new FormData()
  form.append('file', new Blob([text], { type: 'text/plain' }), name)
  const res = await fetch(`${ctx.api.url}/api/documents`, {
    method: 'POST', headers: { Authorization: `Bearer ${user.token}` }, body: form,
  })
  const body = await res.text()
  if (!res.ok) throw new Error(`upload → ${res.status} ${body.slice(0, 200)}`)
  return JSON.parse(body).id
}

/** Create a buyer profile with a given ExtractionModel, returning its id. */
async function profileWithModel(name, extractionModel) {
  await asAdmin('POST', '/api/buyer-profiles', {
    name, detectPattern: name.toUpperCase(), parserKey: 'GenericLlm',
    chunkingStrategy: 'none', isActive: true, extractionModel,
  })
  const list = await asAdmin('GET', '/api/buyer-profiles')
  return (list.json ?? []).find((p) => p.name === name)?.id
}

/**
 * Put a policy row into a state the API itself would refuse to save — which is
 * exactly how an uninstalled model gets into one. ModelPolicy caches rows, so
 * the cache is dropped afterwards by restarting the API: there is deliberately
 * no "clear the cache" endpoint to call.
 */
async function writeRowDirectly(role, allowedModels, defaultModel) {
  const { DatabaseSync } = await import('node:sqlite')
  const w = new DatabaseSync(ctx.api.dbPath)
  try {
    w.prepare('UPDATE "RoleModelPolicies" SET "AllowedModels" = ?, "DefaultModel" = ? WHERE "Role" = ?')
      .run(allowedModels, defaultModel, role)
  } finally { w.close() }

  await ctx.api.stop()
  ctx.api = await startApi({
    fakeUrl: ctx.fake.url, runDir: ctx.api.runDir, port: ctx.api.port,
    env: {
      OllamaCloud__ApiKey: 'eval-fake-key',
      OllamaCloud__BaseUrl: ctx.fake.url,
      OllamaCloud__Models__0: CLOUD_MODELS[0],
      OllamaCloud__Models__1: CLOUD_MODELS[1],
    },
  })
  ctx.admin = await login(ctx.admin.username, ctx.adminSecret)
}

/** The role message the refusal must carry (§3.1). */
const roleMessageRe = /Your role \(General\) can use: gemma4:12b\./

// ─────────────────────────── Phase 1 checks ───────────────────────────

check('G1.1', 1, 'every model entry point calls the one check (no copies of the rule)', async () => {
  // Where the CHECK must be called. Extraction is gated in DocumentApis rather
  // than in ExtractionDispatcher on purpose: the dispatcher knows nothing about
  // the user, and §3.1 requires the refusal to happen before any run row is
  // written — so the check belongs at the dispatcher's caller, and the extra
  // assertion below proves that caller is the only one.
  const files = {
    'backend/MinimalApis/ChatEndpoint.cs': 'chat',
    'backend/MinimalApis/DocumentApis.cs': 'document chat + extract (resolved model)',
    'backend/MinimalApis/ComponentSplitterApis.cs': 'splitter',
    'backend/MinimalApis/OllamaApis.cs': '/api/models filter',
  }
  const missing = []
  const found = []
  for (const [rel, what] of Object.entries(files)) {
    const p = path.join(REPO, rel)
    if (!fs.existsSync(p)) { missing.push(`${rel} (absent)`); continue }
    const lines = fs.readFileSync(p, 'utf8').split('\n')
    const hit = lines.findIndex((l) => /ModelPolicy|modelPolicy|policy\.(Check|Allowed|Filter|DefaultFor)/.test(l))
    if (hit < 0) missing.push(`${rel} — ${what}`)
    else found.push(`${rel}:${hit + 1}`)
  }
  if (missing.length)
    return ok(false, `no policy call in: ${missing.join('; ')}`)

  // One rule, one place: the rule itself lives only in ModelPolicy.cs.
  if (!fs.existsSync(path.join(REPO, 'backend/Rbac/ModelPolicy.cs')))
    return ok(false, 'backend/Rbac/ModelPolicy.cs does not exist')

  // Extraction: every caller of DispatchAsync must be a file that checks the
  // policy, or a run could be dispatched past it.
  const dispatchCallers = []
  const walk = (dir) => {
    for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
      const fp = path.join(dir, e.name)
      if (e.isDirectory()) { walk(fp); continue }
      if (!e.name.endsWith('.cs') || e.name === 'ExtractionDispatcher.cs') continue
      const body = fs.readFileSync(fp, 'utf8')
      if (/\bDispatchAsync\s*\(/.test(body)) {
        const rel = path.relative(REPO, fp).split(path.sep).join('/')
        dispatchCallers.push({ rel, gated: /ModelPolicy|modelPolicy/.test(body) })
      }
    }
  }
  walk(path.join(REPO, 'backend'))
  const ungated = dispatchCallers.filter((c) => !c.gated)
  if (ungated.length)
    return ok(false, `these call DispatchAsync without checking the policy: ${ungated.map((c) => c.rel).join(', ')}`)
  if (!dispatchCallers.length)
    return ok(false, 'found no caller of DispatchAsync — the check below proves nothing')

  // And the refusal must come before the run row: asserted live in G1.3/G1.6.
  return ok(true, `${found.join(', ')}; DispatchAsync callers all gated (${dispatchCallers.map((c) => c.rel).join(', ')})`)
})

check('G1.2', 1, 'General baseline is exactly six tabs; no grants means 403 from model tabs', async () => {
  const fresh = await login(`gen-base-${Date.now()}`, 'eval-secret-base')
  const tabs = (await me(fresh)).tabs ?? []

  const sameSet = (a, b) => a.length === b.length && [...a].sort().join() === [...b].sort().join()
  if (!sameSet(tabs, GENERAL_TABS))
    return ok(false, `tabs are [${tabs.join(', ')}], expected [${GENERAL_TABS.join(', ')}]`)

  // The four model surfaces are refused by the TAB gate, before any policy.
  const docId = 1 // need not exist: the tab gate answers first
  const gated = [
    ['chat', (await chat(fresh, ALLOWED)).status],
    ['workspace-chat', (await docChat(fresh, ALLOWED, docId)).status],
    ['extract', (await extract(fresh, docId, { model: ALLOWED })).status],
    ['splitter', (await splitter(fresh, ALLOWED)).status],
  ]
  const notForbidden = gated.filter(([, s]) => s !== 403)
  if (notForbidden.length)
    return ok(false, `expected 403 from ${notForbidden.map(([n, s]) => `${n} (${s})`).join(', ')}`)

  // And the tabs it DOES have still work.
  const allowed = [
    ['todorabbit', (await call(ctx.api.url, 'GET', '/api/todorabbit/boards', { token: fresh.token })).status],
    ['notes', (await call(ctx.api.url, 'GET', '/api/notes', { token: fresh.token })).status],
    ['context-sources', (await call(ctx.api.url, 'GET', '/api/context-sources', { token: fresh.token })).status],
  ]
  const broken = allowed.filter(([, s]) => s !== 200)
  if (broken.length)
    return ok(false, `expected 200 from ${broken.map(([n, s]) => `${n} (${s})`).join(', ')}`)

  // An explicit jsonto grant still works for someone who had it.
  await setGrants(fresh.id, [...GENERAL_TABS, 'jsonto'])
  const withGrant = (await me(fresh)).tabs ?? []
  if (!withGrant.includes('jsonto'))
    return ok(false, 'an explicit jsonto grant did not take effect')

  return ok(true, `six tabs exactly; model tabs 403; todo/notes/sources 200; jsonto grant honoured`)
})

check('G1.3', 1, 'a forbidden model is refused on all four, with nothing reaching the fake', async () => {
  const u = ctx.gen
  const docId = ctx.genDocId
  ctx.fake.reset()
  const before = (await sqlRows('SELECT COUNT(*) AS n FROM "Messages"'))[0].n
  const runsBefore = (await sqlRows('SELECT COUNT(*) AS n FROM "ExtractionRuns"'))[0].n

  const results = {
    chat: await chat(u, FORBIDDEN),
    docChat: await docChat(u, FORBIDDEN, docId),
    extract: await extract(u, docId, { model: FORBIDDEN }),
    splitter: await splitter(u, FORBIDDEN),
  }

  const wrong = Object.entries(results).filter(([, r]) => r.status !== 403)
  if (wrong.length)
    return ok(false, `expected 403, got ${wrong.map(([n, r]) => `${n} ${r.status}: ${(r.text ?? r.json?.error ?? '').slice(0, 120)}`).join(' | ')}`)

  const noMessage = Object.entries(results).filter(
    ([, r]) => !roleMessageRe.test(r.text ?? JSON.stringify(r.json ?? '')))
  if (noMessage.length)
    return ok(false, `refusal lacks the role message on: ${noMessage.map(([n]) => n).join(', ')}`)

  const ran = ctx.fake.runs()
  if (ran.length)
    return ok(false, `the fake received ${ran.length} model call(s) for refused requests: ${ran.map((r) => r.body?.model).join(', ')}`)

  const after = (await sqlRows('SELECT COUNT(*) AS n FROM "Messages"'))[0].n
  const runsAfter = (await sqlRows('SELECT COUNT(*) AS n FROM "ExtractionRuns"'))[0].n
  if (after !== before) return ok(false, `${after - before} chat message(s) were stored for a refused chat`)
  if (runsAfter !== runsBefore) return ok(false, `${runsAfter - runsBefore} ExtractionRuns row(s) written for a refused extraction`)

  return ok(true, `all four 403 with the role message; fake saw 0 model calls; Messages ${before}→${after}, ExtractionRuns ${runsBefore}→${runsAfter}`)
})

check('G1.4', 1, 'the allowed model succeeds on all four, case-insensitively', async () => {
  const u = ctx.gen
  const docId = ctx.genDocId

  for (const name of [ALLOWED, ALLOWED.toUpperCase()]) {
    ctx.fake.reset()
    const results = {
      chat: await chat(u, name),
      docChat: await docChat(u, name, docId),
      extract: await extract(u, docId, { model: name }),
      splitter: await splitter(u, name),
    }
    const refused = Object.entries(results).filter(([, r]) => r.status === 403)
    if (refused.length)
      return ok(false, `"${name}" was refused on ${refused.map(([n]) => n).join(', ')}`)

    const ran = ctx.fake.runs()
    if (!ran.length) return ok(false, `"${name}": the fake received no model call at all`)

    // §3.1: names compare case-insensitively, and the model actually sent is the real one.
    const sent = [...new Set(ran.map((r) => String(r.body?.model ?? '')))]
    const wrong = sent.filter((m) => m.toLowerCase() !== ALLOWED.toLowerCase())
    if (wrong.length)
      return ok(false, `"${name}": the fake received [${sent.join(', ')}], expected only ${ALLOWED}`)
  }
  return ok(true, `${ALLOWED} and ${ALLOWED.toUpperCase()} both succeed on all four; fake received ${ALLOWED}`)
})

check('G1.5', 1, 'a cloud model is refused even when its name is the allowed one', async () => {
  const u = ctx.gen
  ctx.fake.reset()

  // Same NAME, different source — §3.1: a restricted role may only use `local`.
  const sameName = await chat(u, ALLOWED, { source: 'cloud' })
  const cloudOnly = await chat(u, 'cloud-only-model', { source: 'cloud' })

  if (sameName.status !== 403)
    return ok(false, `cloud "${ALLOWED}" was not refused (${sameName.status}: ${sameName.text.slice(0, 150)})`)
  if (cloudOnly.status !== 403)
    return ok(false, `a cloud-only model was not refused (${cloudOnly.status})`)

  const ran = ctx.fake.runs()
  if (ran.length)
    return ok(false, `the fake received ${ran.length} call(s) for a refused cloud request`)

  return ok(true, `cloud ${ALLOWED} → 403, cloud-only → 403, fake saw 0 calls`)
})

check('G1.6', 1, 'extraction checks the RESOLVED model, not just the named one', async () => {
  const u = ctx.gen

  // (a) profile with an empty ExtractionModel → the default reaches the fake.
  ctx.fake.reset()
  const emptyDoc = await upload(u, 'resolve-empty.txt')
  const rEmpty = await extract(u, emptyDoc, { profileId: ctx.profileEmpty })
  if (rEmpty.status === 403)
    return ok(false, `an empty-model profile was refused: ${rEmpty.json?.error ?? rEmpty.text.slice(0, 150)}`)
  const sentEmpty = ctx.fake.runs().map((r) => r.body?.model)
  if (!sentEmpty.some((m) => String(m).toLowerCase() === ALLOWED))
    return ok(false, `empty profile sent [${sentEmpty.join(', ')}], expected ${ALLOWED}`)

  // (b) profile set to the forbidden model → 403 with the PROFILE message,
  //     although the request named no model at all.
  ctx.fake.reset()
  const runsBefore = (await sqlRows('SELECT COUNT(*) AS n FROM "ExtractionRuns"'))[0].n
  const rForbidden = await extract(u, emptyDoc, { profileId: ctx.profileForbidden })
  if (rForbidden.status !== 403)
    return ok(false, `a profile set to ${FORBIDDEN} was not refused (${rForbidden.status})`)

  const msg = rForbidden.json?.error ?? rForbidden.text
  if (!new RegExp(`This buyer profile uses ${FORBIDDEN}\\.`).test(msg))
    return ok(false, `refusal does not carry the profile message: ${String(msg).slice(0, 200)}`)
  if (!roleMessageRe.test(msg))
    return ok(false, `refusal does not carry the role message: ${String(msg).slice(0, 200)}`)

  if (ctx.fake.runs().length)
    return ok(false, 'the fake received a call for a profile-refused extraction')
  const runsAfter = (await sqlRows('SELECT COUNT(*) AS n FROM "ExtractionRuns"'))[0].n
  if (runsAfter !== runsBefore)
    return ok(false, `${runsAfter - runsBefore} ExtractionRuns row(s) written for a refused extraction`)

  // (c) a System user with the SAME profile reaches the fake with the 27B.
  ctx.fake.reset()
  const sysDoc = await upload(ctx.sys, 'resolve-sys.txt')
  const rSys = await extract(ctx.sys, sysDoc, { profileId: ctx.profileForbidden })
  if (rSys.status === 403)
    return ok(false, `a System user was refused: ${rSys.json?.error ?? rSys.text.slice(0, 150)}`)
  const sentSys = ctx.fake.runs().map((r) => String(r.body?.model))
  if (!sentSys.includes(FORBIDDEN))
    return ok(false, `System user's extraction sent [${sentSys.join(', ')}], expected ${FORBIDDEN}`)

  return ok(true, `empty profile → ${ALLOWED}; ${FORBIDDEN} profile → 403 with both messages, no run row; System → ${FORBIDDEN}`)
})

check('G1.7', 1, '/api/models is filtered and flagged for General, unchanged for others', async () => {
  const gen = await call(ctx.api.url, 'GET', '/api/models', { token: ctx.gen.token })
  const sys = await call(ctx.api.url, 'GET', '/api/models', { token: ctx.sys.token })
  const adm = await call(ctx.api.url, 'GET', '/api/models', { token: ctx.admin.token })

  const names = (r) => (Array.isArray(r.json) ? r.json : []).map((m) => m.name)
  const genNames = names(gen)

  if (genNames.length !== 1 || genNames[0].toLowerCase() !== ALLOWED)
    return ok(false, `General sees [${genNames.join(', ')}], expected only ${ALLOWED}`)

  // The lock flag: either on the row, or the header, whichever this build chose.
  const row = (Array.isArray(gen.json) ? gen.json : [])[0] ?? {}
  const locked = row.locked === true || gen.headers.get('x-model-locked') === 'true'
  if (!locked)
    return ok(false, 'General\'s one allowed model carries no lock flag (neither `locked: true` nor X-Model-Locked)')

  // Unrestricted roles are untouched — compared against the baseline captured
  // before the policy existed.
  const sysNames = names(sys).sort().join()
  const admNames = names(adm).sort().join()
  if (sysNames !== ctx.baselineModels || admNames !== ctx.baselineModels)
    return ok(false, `System/SystemAdmin lists changed: system [${sysNames}] admin [${admNames}] vs baseline [${ctx.baselineModels}]`)

  const sysLocked = (Array.isArray(sys.json) ? sys.json : []).some((m) => m.locked === true)
    || sys.headers.get('x-model-locked') === 'true'
  if (sysLocked) return ok(false, 'an unrestricted role was flagged as locked')

  return ok(true, `General [${genNames.join(', ')}] locked; System/Admin unchanged (${names(sys).length} models)`)
})

check('G1.8', 1, 'a role change applies to the very next request, with no restart', async () => {
  const u = ctx.gen

  ctx.fake.reset()
  const asGeneral = await chat(u, FORBIDDEN)
  if (asGeneral.status !== 403) return ok(false, `as General: expected 403, got ${asGeneral.status}`)

  await setRole(u.id, 'System')
  const promoted = await login(u.username, ctx.genSecret)
  ctx.fake.reset()
  const asSystem = await chat(promoted, FORBIDDEN)
  if (asSystem.status === 403)
    return ok(false, `after promotion to System the 27B was still refused: ${asSystem.text.slice(0, 150)}`)
  if (!ctx.fake.runs().some((r) => String(r.body?.model) === FORBIDDEN))
    return ok(false, 'promoted user succeeded but the fake never received the 27B')

  await setRole(u.id, 'General')
  const demoted = await login(u.username, ctx.genSecret)
  ctx.fake.reset()
  const again = await chat(demoted, FORBIDDEN)
  if (again.status !== 403)
    return ok(false, `after demotion the 27B was allowed again (${again.status})`)
  if (ctx.fake.runs().length)
    return ok(false, 'the fake received a call after demotion')

  // Keep the shared General user usable for later checks.
  ctx.gen = demoted
  return ok(true, 'General 403 → System 200 (fake got the 27B) → General 403, no restart')
})

check('G1.9', 1, 'admin policy routes: 403 for non-admins, 400 for each invalid save', async () => {
  const asGen = await call(ctx.api.url, 'GET', '/api/admin/rbac/model-policies', { token: ctx.gen.token })
  if (asGen.status !== 403) return ok(false, `a General user got ${asGen.status} from the policy list, expected 403`)

  const list = await asAdmin('GET', '/api/admin/rbac/model-policies')
  if (list.status !== 200) return ok(false, `admin GET → ${list.status} ${list.text.slice(0, 150)}`)

  const bad = {
    'default outside the list': await asAdmin('PUT', '/api/admin/rbac/model-policies/General',
      { allowedModels: [ALLOWED], defaultModel: FORBIDDEN }),
    'unknown role': await asAdmin('PUT', '/api/admin/rbac/model-policies/Wizard',
      { allowedModels: [ALLOWED], defaultModel: ALLOWED }),
    'model not installed': await asAdmin('PUT', '/api/admin/rbac/model-policies/General',
      { allowedModels: ['no-such-model:1b'], defaultModel: 'no-such-model:1b' }),
    'restricting SystemAdmin': await asAdmin('PUT', '/api/admin/rbac/model-policies/SystemAdmin',
      { allowedModels: [ALLOWED], defaultModel: ALLOWED }),
  }
  const wrong = Object.entries(bad).filter(([, r]) => r.status !== 400)
  if (wrong.length)
    return ok(false, `expected 400 for ${wrong.map(([n, r]) => `${n} (${r.status})`).join(', ')}`)

  // A valid save applies to the next request: allow the 27B for General.
  const save = await asAdmin('PUT', '/api/admin/rbac/model-policies/General',
    { allowedModels: [ALLOWED, FORBIDDEN], defaultModel: ALLOWED })
  if (save.status !== 200) return ok(false, `valid save → ${save.status} ${save.text.slice(0, 150)}`)

  ctx.fake.reset()
  const now = await chat(ctx.gen, FORBIDDEN)
  if (now.status === 403)
    return ok(false, 'after allowing the 27B for General it was still refused')

  // Put it back, and prove the restriction returns.
  await asAdmin('PUT', '/api/admin/rbac/model-policies/General',
    { allowedModels: [ALLOWED], defaultModel: ALLOWED })
  const back = await chat(ctx.gen, FORBIDDEN)
  if (back.status !== 403)
    return ok(false, 'restoring the restriction did not take effect')

  return ok(true, 'non-admin 403; four invalid saves 400; a valid save applied to the next request and reverted')
})

check('G1.9b', 1, 'a stale saved model never blocks a save that removes it', async () => {
  // Reported from the Access screen: a role whose row still named a model that
  // has since been deleted from the Mac (§2 F14) could not be saved at all —
  // the refusal named a model the form no longer showed, so there was no way to
  // fix the row through the UI that reported it.
  const stale = 'qwen3.5:0.8b'

  // Put the role into that state the way an uninstall does: straight into the
  // row, bypassing the API, because the API is what refuses it.
  await writeRowDirectly('General', `${ALLOWED},${stale}`, stale)

  // The listing must show the stale entry rather than hiding it, or an admin
  // cannot tell why the role behaves oddly.
  const list = await asAdmin('GET', '/api/admin/rbac/model-policies')
  const general = (list.json?.policies ?? []).find((p) => p.role === 'General')
  if (!general) return ok(false, 'the listing has no General row')
  if (!general.allowedModels.some((m) => m.toLowerCase() === stale))
    return ok(false, `the listing dropped the stale model silently: [${general.allowedModels.join(', ')}]`)

  // Saving a list that REMOVES the stale model must succeed: this is the fix.
  const fix = await asAdmin('PUT', '/api/admin/rbac/model-policies/General',
    { allowedModels: [ALLOWED], defaultModel: ALLOWED })
  if (fix.status !== 200)
    return ok(false, `removing the stale model was refused (${fix.status}): ${fix.json?.error ?? fix.text.slice(0, 200)}`)

  // And adding a model that is genuinely not installed is still refused, naming
  // only that model — the validation must not have been weakened wholesale.
  const bad = await asAdmin('PUT', '/api/admin/rbac/model-policies/General',
    { allowedModels: [ALLOWED, 'definitely-not-installed:9b'], defaultModel: ALLOWED })
  if (bad.status !== 400)
    return ok(false, `adding an uninstalled model was accepted (${bad.status})`)
  if (!/definitely-not-installed:9b/.test(bad.json?.error ?? ''))
    return ok(false, `the refusal does not name the offending model: ${bad.json?.error}`)
  if (new RegExp(stale).test(bad.json?.error ?? ''))
    return ok(false, `the refusal still blames the stale model: ${bad.json?.error}`)

  // Leave General as the other checks expect to find it.
  await asAdmin('PUT', '/api/admin/rbac/model-policies/General',
    { allowedModels: [ALLOWED], defaultModel: ALLOWED })

  return ok(true, `stale "${stale}" listed, removable, and no longer blocks the save; a newly added uninstalled model is still refused by name`)
})

check('G1.9c', 1, 'an uninstalled model stays listed and is flagged stale, so it can be unticked', async () => {
  // The other half of the same report. The row is NOT healed automatically (an
  // admin's list is their decision): the listing keeps the model and marks it
  // missing, which is what lets the editor show a ticked-but-stale entry instead
  // of hiding it and silently posting it back on the next save.
  await writeRowDirectly('General', 'gone-from-the-mac:1b', 'gone-from-the-mac:1b')

  const list = await asAdmin('GET', '/api/admin/rbac/model-policies')
  const general = (list.json?.policies ?? []).find((p) => p.role === 'General')

  // Put the row back BEFORE asserting, so a failure here cannot leave the
  // database in a state that fails every later check too.
  await asAdmin('PUT', '/api/admin/rbac/model-policies/General',
    { allowedModels: [ALLOWED], defaultModel: ALLOWED })

  if (!general?.stale?.length)
    return ok(false, `the listing does not flag which allowed models are missing: ${JSON.stringify(general)}`)
  if (!general.allowedModels.includes('gone-from-the-mac:1b'))
    return ok(false, 'the listing dropped the uninstalled model instead of flagging it')

  return ok(true, `a role whose models are all uninstalled is flagged: stale [${general.stale.join(', ')}]`)
})

check('G1.10', 1, 'one row per role with the seeded values; an edit survives a restart', async () => {
  const rows = await sqlRows('SELECT "Role", "AllowedModels", "DefaultModel" FROM "RoleModelPolicies" ORDER BY "Role"')
  const byRole = Object.fromEntries(rows.map((r) => [r.Role, r]))

  const missing = ROLES.filter((r) => !byRole[r])
  if (missing.length) return ok(false, `no row for: ${missing.join(', ')}`)

  if ((byRole.General.AllowedModels ?? '').toLowerCase() !== ALLOWED)
    return ok(false, `General's AllowedModels is "${byRole.General.AllowedModels}", expected "${ALLOWED}"`)
  if ((byRole.General.DefaultModel ?? '').toLowerCase() !== ALLOWED)
    return ok(false, `General's DefaultModel is "${byRole.General.DefaultModel}", expected "${ALLOWED}"`)

  const shouldBeEmpty = ['System', 'GroundControl', 'SystemAdmin']
    .filter((r) => (byRole[r].AllowedModels ?? '') !== '')
  if (shouldBeEmpty.length)
    return ok(false, `these roles should start unrestricted: ${shouldBeEmpty.join(', ')}`)

  // Restrict System to a third model and prove it bites, with no restart.
  const save = await asAdmin('PUT', '/api/admin/rbac/model-policies/System',
    { allowedModels: [THIRD], defaultModel: THIRD })
  if (save.status !== 200) return ok(false, `restricting System → ${save.status} ${save.text.slice(0, 150)}`)

  ctx.fake.reset()
  const refused = await chat(ctx.sys, FORBIDDEN)
  if (refused.status !== 403)
    return ok(false, `a restricted System user was not refused the 27B (${refused.status})`)
  if (!/Your role \(System\) can use: gemma3:270m\./.test(refused.text))
    return ok(false, `System's refusal message is wrong: ${refused.text.slice(0, 160)}`)

  const allowed = await chat(ctx.sys, THIRD)
  if (allowed.status === 403)
    return ok(false, `the restricted System user could not run its allowed model (${allowed.text.slice(0, 150)})`)

  // An admin's edit must survive a restart (never re-seeded over).
  await ctx.api.stop()
  ctx.api = await startApi({ fakeUrl: ctx.fake.url, runDir: ctx.api.runDir, port: ctx.api.port })
  const afterRestart = await sqlRows('SELECT "AllowedModels" FROM "RoleModelPolicies" WHERE "Role" = \'System\'')
  if ((afterRestart[0]?.AllowedModels ?? '').toLowerCase() !== THIRD)
    return ok(false, `after a restart System's row reads "${afterRestart[0]?.AllowedModels}", the seed overwrote the edit`)

  // Clear it again and prove the 27B works.
  ctx.admin = await login(ctx.admin.username, ctx.adminSecret)
  await asAdmin('PUT', '/api/admin/rbac/model-policies/System', { allowedModels: [], defaultModel: '' })
  ctx.sys = await login(ctx.sys.username, ctx.sysSecret)
  ctx.fake.reset()
  const cleared = await chat(ctx.sys, FORBIDDEN)
  if (cleared.status === 403)
    return ok(false, 'clearing System\'s restriction did not restore the 27B')

  return ok(true, `${rows.length} rows, General=${ALLOWED}, others empty; System restricted → 403 + allowed 200; edit survived a restart; cleared → 200`)
})

check('G1.11', 1, 'po-eval and oddity-eval still pass at the baseline counts', async () => {
  const run = (script, args) => {
    const r = spawnSync('node', [path.join(REPO, 'scripts', script), ...args], {
      cwd: REPO, encoding: 'utf8', shell: process.platform === 'win32',
      env: {
        ...process.env,
        PO_EVAL_ADMIN_USER: process.env.MODEL_POLICY_EVAL_ADMIN_USER,
        PO_EVAL_ADMIN_SECRET: process.env.MODEL_POLICY_EVAL_ADMIN_SECRET,
        ODDITY_EVAL_ADMIN_USER: process.env.MODEL_POLICY_EVAL_ADMIN_USER,
        ODDITY_EVAL_ADMIN_SECRET: process.env.MODEL_POLICY_EVAL_ADMIN_SECRET,
      },
      timeout: 900_000,
    })
    const out = (r.stdout ?? '') + (r.stderr ?? '')
    const m = out.match(/(\d+)\/(\d+) passed/)
    return { line: m ? m[0] : '(no summary line)', pass: m ? m[1] === m[2] : false, out }
  }

  const po = run('po-eval.mjs', ['--phase', '1'])
  const od = run('oddity-eval.mjs', ['--phase', '2'])

  if (!po.pass || !od.pass)
    return ok(false, `po-eval ${po.line}; oddity-eval ${od.line}\n` +
      (po.pass ? '' : po.out.split('\n').filter((l) => /FAIL/.test(l)).slice(0, 6).join('\n    ')) +
      (od.pass ? '' : od.out.split('\n').filter((l) => /FAIL/.test(l)).slice(0, 6).join('\n    ')))

  return ok(true, `po-eval ${po.line}; oddity-eval ${od.line}`)
})

check('G1.12', 1, 'dotnet build is clean, zero warnings', async () => {
  const r = spawnSync('dotnet',
    ['build', path.join(REPO, 'backend', 'RabbitIC.Api.csproj'), '-v', 'q', '--nologo'],
    { cwd: REPO, encoding: 'utf8', shell: process.platform === 'win32' })
  const out = (r.stdout ?? '') + (r.stderr ?? '')
  if (r.status !== 0) return ok(false, `build failed:\n${out.slice(-1200)}`)
  const warnings = out.match(/(\d+) Warning\(s\)/)
  if (warnings && warnings[1] !== '0')
    return ok(false, `${warnings[1]} warning(s):\n${out.split('\n').filter((l) => /warning/i.test(l)).slice(0, 8).join('\n')}`)
  return ok(true, 'exit 0, 0 warnings')
})

// ─────────────────────────── runner ───────────────────────────

function parseArgs(argv) {
  const a = { phase: 99, only: null, build: true }
  for (let i = 2; i < argv.length; i++) {
    const v = argv[i]
    if (v === '--phase') a.phase = Number(argv[++i])
    else if (v === '--only') a.only = argv[++i]
    else if (v === '--no-build') a.build = false
  }
  return a
}

async function seed() {
  const user = process.env.MODEL_POLICY_EVAL_ADMIN_USER
  const secret = process.env.MODEL_POLICY_EVAL_ADMIN_SECRET
  if (!user || !secret)
    throw new Error('set MODEL_POLICY_EVAL_ADMIN_USER and MODEL_POLICY_EVAL_ADMIN_SECRET (never written in this script)')

  ctx.adminSecret = secret
  ctx.admin = await login(user, secret)
  if (ctx.admin.role !== 'SystemAdmin')
    throw new Error(`the seeded admin is ${ctx.admin.role}, not SystemAdmin`)

  // The /api/models answer BEFORE any policy exists, so G1.7 compares an
  // unrestricted role against what it really used to return.
  const base = await call(ctx.api.url, 'GET', '/api/models', { token: ctx.admin.token })
  ctx.baselineModels = (Array.isArray(base.json) ? base.json : []).map((m) => m.name).sort().join()

  // A General user with every model tab granted, so the gates below exercise
  // the POLICY rather than the tab gate.
  ctx.genSecret = 'eval-secret-gen'
  ctx.gen = await login(`gen-${Date.now()}`, ctx.genSecret)
  await setGrants(ctx.gen.id, [...GENERAL_TABS, 'chat', 'workspace', 'docs', 'component-splitter'])
  ctx.gen = await login(ctx.gen.username, ctx.genSecret)

  ctx.sysSecret = 'eval-secret-sys'
  ctx.sys = await login(`sys-${Date.now()}`, ctx.sysSecret)
  await setRole(ctx.sys.id, 'System')
  ctx.sys = await login(ctx.sys.username, ctx.sysSecret)

  // One document each, and the two profiles G1.6 resolves through.
  ctx.genDocId = await upload(ctx.gen)
  ctx.profileEmpty = await profileWithModel('MpEmpty', '')
  ctx.profileForbidden = await profileWithModel('MpForbidden', FORBIDDEN)
}

async function main() {
  const args = parseArgs(process.argv)
  let selected = checks.filter((c) => c.phase <= args.phase)
  if (args.only) selected = selected.filter((c) => c.id === args.only || c.id.startsWith(args.only + '.'))
  if (!selected.length) { console.error('no checks selected'); process.exit(2) }

  if (args.build) {
    process.stdout.write('building the API for the eval… ')
    buildApi()
    console.log('done')
  }

  fs.mkdirSync(EVAL_ROOT, { recursive: true })
  const runDir = fs.mkdtempSync(path.join(EVAL_ROOT, 'run-'))
  ctx.fake = await startFake()
  let failed = 0

  try {
    // The cloud source is pointed at the same fake and given a key, so G1.5
    // proves the POLICY refuses a cloud model — not merely that cloud is
    // unconfigured (which answers 400 before any policy runs).
    ctx.api = await startApi({
      fakeUrl: ctx.fake.url, runDir,
      env: {
        OllamaCloud__ApiKey: 'eval-fake-key',
        OllamaCloud__BaseUrl: ctx.fake.url,
        OllamaCloud__Models__0: CLOUD_MODELS[0],
        OllamaCloud__Models__1: CLOUD_MODELS[1],
      },
    })
    await seed()
    console.log(`fake ${ctx.fake.url} · api ${ctx.api.url} · db ${ctx.api.dbPath}\n`)
    console.log('id | check | result | detail')
    for (const c of selected) {
      let res
      try { res = await c.fn() } catch (e) { res = { pass: false, detail: `threw: ${e.message}` } }
      if (!res.pass) failed++
      console.log(`${c.id} | ${c.name} | ${res.pass ? 'PASS' : 'FAIL'} | ${res.detail ?? ''}`)
    }
    console.log(`\n${selected.length - failed}/${selected.length} passed`)
  } catch (e) {
    console.log(`SETUP | eval set-up | FAIL | ${e.message}`)
    console.log(`\n0/${selected.length} passed`)
    failed = selected.length
  } finally {
    await ctx.api?.stop()
    await ctx.fake?.stop()
  }

  if (failed) console.log(`api log: ${ctx.api?.logPath ?? '(not started)'}`)
  process.exit(failed ? 1 : 0)
}

main().catch((e) => { console.error(e); process.exit(2) })
