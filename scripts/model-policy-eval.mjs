#!/usr/bin/env node
// Model-policy eval — see docs/MODEL_POLICY_PROMPT.md §4.
//
//   node scripts/model-policy-eval.mjs                every check
//   node scripts/model-policy-eval.mjs --phase 1      checks up to phase 1
//   node scripts/model-policy-eval.mjs --only G1.3    one gate
//   node scripts/model-policy-eval.mjs --no-build     reuse the last eval build
//
// The fake Ollama, the API start-up and the HTTP helper are imported from
// oddity-eval.mjs rather than copied (§7). The seeded administrator's name and
// secret come from MOSTISKO_ADMIN_USER and MOSTISKO_ADMIN_SECRET; they are
// never written here.
//
// Every check asserts against what the FAKE RECORDED and what the CLIENT
// RECEIVED — never against the API's own account of what it did. A refusal is
// only proven when the fake shows no request for that model.

import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import crypto from 'node:crypto'
import { fileURLToPath } from 'node:url'
import { startFakeOllama, buildApi, startApi, call } from './oddity-eval.mjs'

const REPO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const EVAL_ROOT = path.join(os.tmpdir(), 'model-policy-eval')

// ---- Expected values, written by hand ----
// These mirror the design contract (§3.1-§3.2), not the code: a check that read
// them from TabCatalog.cs would only prove the code agrees with itself.
const SMALL = 'qwen3.5:0.8b'
const BIG = 'qwen3.8:27b'
const MID = 'gemma3:270m'
const FAKE_MODELS = [SMALL, BIG, MID]

/**
 * Section 1 / G1.2: General's baseline after this build — exactly five.
 * jsonto and 5lakhs left it; both stay grantable and start at System.
 */
const GENERAL_TABS = ['dashboard', 'todo', 'notes', 'sources', 'settings']

/** §3.2 seed. An empty list means "no restriction". */
const SEED = {
  General: { allowed: [SMALL], def: SMALL },
  System: { allowed: [], def: '' },
  GroundControl: { allowed: [], def: '' },
  SystemAdmin: { allowed: [], def: '' },
}

// ─────────────────────────────── checks ───────────────────────────────

const checks = []
function check(id, phase, name, fn) { checks.push({ id, phase, name, fn }) }

const ctx = {}

async function login(username, secret) {
  const r = await call(ctx.api.url, 'POST', '/api/auth/login', { body: { username, secret } })
  if (r.status !== 200 || !r.json?.token) throw new Error(`login ${username} → ${r.status} ${r.text}`)
  const me = await call(ctx.api.url, 'GET', '/api/me/tabs', { token: r.json.token })
  return { username, token: r.json.token, id: me.json?.id, me: me.json }
}

const meTabs = async (u) => (await call(ctx.api.url, 'GET', '/api/me/tabs', { token: u.token })).json
const setRole = (id, role) =>
  call(ctx.api.url, 'PUT', `/api/admin/rbac/users/${id}/role`, { token: ctx.admin.token, body: { role } })
const setGrants = (id, tabKeys) =>
  call(ctx.api.url, 'PUT', `/api/admin/rbac/users/${id}/tabs`, { token: ctx.admin.token, body: { tabKeys } })
const setPolicy = (role, body) =>
  call(ctx.api.url, 'PUT', `/api/admin/rbac/model-policies/${role}`, { token: ctx.admin.token, body })
const getPolicies = (token = ctx.admin.token) =>
  call(ctx.api.url, 'GET', '/api/admin/rbac/model-policies', { token })

async function sql(query, ...params) {
  const emit = process.emitWarning
  process.emitWarning = (w, ...a) => (String(w).includes('SQLite') ? undefined : emit.call(process, w, ...a))
  const { DatabaseSync } = await import('node:sqlite')
  const d = new DatabaseSync(ctx.api.dbPath, { readOnly: true })
  try { return d.prepare(query).all(...params) } finally { d.close() }
}

/** Requests the fake received since a mark, for a given path. */
const since = (mark, p) => ctx.fake.requests.slice(mark).filter((r) => r.path === p)
const mark = () => ctx.fake.requests.length

/** Every model name the fake was asked to run since a mark. */
const modelsRun = (m) =>
  ctx.fake.requests.slice(m)
    .filter((r) => r.path === '/api/chat' || r.path === '/api/generate')
    .map((r) => r.body?.model)

// ---- the four user-facing model entry points (§2 grep) ----
// Each takes a model name from the request body; each must consult the policy.

const chat = (u, model, source = 'local') =>
  call(ctx.api.url, 'POST', '/api/chat', {
    token: u.token, body: { model, source, message: 'hello', activeSourceIds: [] },
  })

const workspaceChat = (u, model) =>
  call(ctx.api.url, 'POST', '/api/documents/workspace-chat', {
    token: u.token, body: { model, message: 'hello' },
  })

const extract = (u, documentId, model) =>
  call(ctx.api.url, 'POST', `/api/documents/${documentId}/extract`, {
    token: u.token, body: model === undefined ? {} : { model },
  })

const splitter = (u, model, source = 'local') =>
  call(ctx.api.url, 'POST', '/api/component-splitter/analyse', {
    token: u.token,
    body: {
      model, source,
      componentTs: 'export class X {}',
      templateHtml: '<div>x</div>',
    },
  })

// ═══════════════════════════ G1.2 — baseline ═══════════════════════════

// These two use `spare`, not `gen`: the setup grants `gen` the four model tabs
// so G1.3-G1.6 can reach those endpoints at all. `spare` is the ungranted
// General account, which is what "a new General user" actually means.
check('G1.2', 1, 'a new General user has exactly the six baseline tabs', async () => {
  const t = (await meTabs(ctx.u.spare)).tabs
  const got = [...t].sort()
  const want = [...GENERAL_TABS].sort()
  if (JSON.stringify(got) !== JSON.stringify(want)) {
    return { ok: false, detail: `tabs ${JSON.stringify(t)} ≠ ${JSON.stringify(GENERAL_TABS)}` }
  }
  return { ok: true, detail: t.join(' ') }
})

check('G1.2', 1, 'with no grants the model tabs are refused and the self-service ones answer', async () => {
  const s = ctx.u.spare
  const gated = {
    chat: await chat(s, SMALL),
    workspace: await workspaceChat(s, SMALL),
    splitter: await splitter(s, SMALL),
  }
  const open = {
    todorabbit: await call(ctx.api.url, 'GET', '/api/todorabbit/boards', { token: s.token }),
    notes: await call(ctx.api.url, 'GET', '/api/notes', { token: s.token }),
    sources: await call(ctx.api.url, 'GET', '/api/context-sources', { token: s.token }),
  }
  const bad = [
    ...Object.entries(gated).filter(([, r]) => r.status !== 403).map(([k, r]) => `${k}=${r.status}≠403`),
    ...Object.entries(open).filter(([, r]) => r.status !== 200).map(([k, r]) => `${k}=${r.status}≠200`),
  ]
  return { ok: !bad.length, detail: bad.length ? bad.join(', ') : 'tab gate holds both ways' }
})

check('G1.2', 1, 'an explicit jsonto grant still works after the baseline shrinks', async () => {
  const spare = ctx.u.spare
  await setGrants(spare.id, ['jsonto'])
  const t = (await meTabs(spare)).tabs
  await setGrants(spare.id, [])
  return { ok: t.includes('jsonto'), detail: `granted tabs: ${t.join(' ')}` }
})

// ═══════════════════════════ G1.3 — refusal ════════════════════════════

check('G1.3', 1, 'a General user naming the big model is refused on all four entry points', async () => {
  const m = mark()
  const r = {
    chat: await chat(ctx.u.gen, BIG),
    workspace: await workspaceChat(ctx.u.gen, BIG),
    extract: await extract(ctx.u.gen, ctx.docId, BIG),
    splitter: await splitter(ctx.u.gen, BIG),
  }
  // 403 is the only acceptable answer here: extraction's 422 ("needs review")
  // means the model ran, which is exactly what must not happen.
  const wrong = Object.entries(r).filter(([, x]) => x.status !== 403).map(([k, x]) => `${k}=${x.status}`)
  const ran = modelsRun(m)
  const bad = []
  if (wrong.length) bad.push(`not 403: ${wrong.join(', ')}`)
  if (ran.length) bad.push(`fake RECEIVED ${ran.join(', ')} — must be none`)
  return { ok: !bad.length, detail: bad.length ? bad.join(' | ') : 'all 403, fake received nothing' }
})

check('G1.3', 1, 'the refusal names the role and the allowed model', async () => {
  const r = await chat(ctx.u.gen, BIG)
  const msg = r.json?.error ?? r.text ?? ''
  const ok = /General/i.test(msg) && msg.includes(SMALL)
  return { ok, detail: JSON.stringify(msg).slice(0, 140) }
})

check('G1.3', 1, 'a refused chat stores no message', async () => {
  const before = (await sql('select count(*) c from Messages'))[0].c
  await chat(ctx.u.gen, BIG)
  const after = (await sql('select count(*) c from Messages'))[0].c
  return { ok: after === before, detail: `Messages ${before} → ${after}` }
})

check('G1.3', 1, 'workspace chat refuses in plain text, not JSON (F5)', async () => {
  const r = await workspaceChat(ctx.u.gen, BIG)
  const isText = r.status === 403 && r.json === null && /General/i.test(r.text)
  return { ok: isText, detail: `status=${r.status} json=${r.json === null ? 'null' : 'JSON'} body=${JSON.stringify(r.text).slice(0, 90)}` }
})

// ═══════════════════════════ G1.4 — the allowed model ══════════════════

check('G1.4', 1, 'the allowed model succeeds on all four, and the fake receives it', async () => {
  const m = mark()
  const r = {
    chat: await chat(ctx.u.gen, SMALL),
    workspace: await workspaceChat(ctx.u.gen, SMALL),
    extract: await extract(ctx.u.gen, ctx.docId, SMALL),
    splitter: await splitter(ctx.u.gen, SMALL),
  }
  const refused = Object.entries(r).filter(([, x]) => x.status === 403).map(([k]) => k)
  const ran = modelsRun(m)
  const bad = []
  if (refused.length) bad.push(`403 on ${refused.join(', ')}`)
  if (!ran.length) bad.push('fake received nothing')
  if (ran.some((x) => x !== SMALL)) bad.push(`fake saw ${ran.join(', ')}`)
  return { ok: !bad.length, detail: bad.length ? bad.join(' | ') : `fake ran ${ran.join(', ')}` }
})

check('G1.4', 1, 'the comparison is case-insensitive', async () => {
  const m = mark()
  const r = await chat(ctx.u.gen, SMALL.toUpperCase())
  const ran = modelsRun(m)
  return {
    ok: r.status !== 403 && ran.length > 0,
    detail: `status=${r.status}, fake ran ${ran.join(', ') || '(nothing)'}`,
  }
})

check('G1.4', 1, 'a bare name matches the :latest form', async () => {
  // The policy lists "qwen3.5:0.8b"; a request for the same name without a tag
  // must not be a different model.
  const r = await chat(ctx.u.gen, SMALL.split(':')[0])
  return { ok: r.status === 403 || r.status === 200, detail: `status=${r.status} (not a crash)` }
})

// ═══════════════════════════ G1.5 — source ═════════════════════════════

check('G1.5', 1, 'a restricted role cannot reach a cloud model, even by the allowed name', async () => {
  // The policy must refuse on SOURCE before the registry is consulted. Without a
  // cloud key the registry would answer 400 "unknown source" on its own, which
  // would pass this check for the wrong reason — so the refusal has to be a 403
  // whose message names the role.
  const m = mark()
  const r = await chat(ctx.u.gen, SMALL, 'cloud')
  const ran = modelsRun(m)
  const msg = r.json?.error ?? r.text ?? ''
  const byPolicy = r.status === 403 && /General/i.test(msg)
  return {
    ok: byPolicy && ran.length === 0,
    detail: `status=${r.status} msg=${JSON.stringify(msg).slice(0, 80)}, fake ran ${ran.join(', ') || '(nothing)'}`,
  }
})

// ═══════════════════════════ G1.6 — default model ══════════════════════

check('G1.6', 1, 'extraction with no model uses the policy default for General', async () => {
  const m = mark()
  await extract(ctx.u.gen, ctx.docId, undefined)
  const ran = modelsRun(m)
  return {
    ok: ran.length > 0 && ran.every((x) => String(x).toLowerCase() === SMALL),
    detail: `fake ran ${ran.join(', ') || '(nothing)'} — expected ${SMALL}`,
  }
})

check('G1.6', 1, 'extraction with no model keeps the config fallback for System', async () => {
  const m = mark()
  await extract(ctx.u.sys, ctx.sysDocId, undefined)
  const ran = modelsRun(m)
  return {
    ok: ran.length > 0 && !ran.includes(SMALL),
    detail: `fake ran ${ran.join(', ') || '(nothing)'} — must not be the General default`,
  }
})

// ═══════════════════════════ G1.7 — /api/models ════════════════════════

check('G1.7', 1, '/api/models lists only the allowed model for General, with locked', async () => {
  const r = await call(ctx.api.url, 'GET', '/api/models', { token: ctx.u.gen.token })
  const names = (r.json?.models ?? r.json ?? []).map?.((x) => x.name) ?? []
  const locked = r.json?.locked === true || r.headers.get('X-Model-Locked') === 'true'
  const only = names.length === 1 && String(names[0]).toLowerCase() === SMALL
  return { ok: only && locked, detail: `models=${JSON.stringify(names)} locked=${locked}` }
})

check('G1.7', 1, '/api/models is unchanged for System and SystemAdmin', async () => {
  const sys = await call(ctx.api.url, 'GET', '/api/models', { token: ctx.u.sys.token })
  const adm = await call(ctx.api.url, 'GET', '/api/models', { token: ctx.admin.token })
  const n = (r) => (r.json?.models ?? r.json ?? []).map?.((x) => x.name) ?? []
  const sysNames = n(sys)
  const baseline = ctx.modelsBefore            // captured before the policy existed
  const same = JSON.stringify([...sysNames].sort()) === JSON.stringify([...baseline].sort())
  return {
    ok: same && n(adm).length === sysNames.length,
    detail: `system=${JSON.stringify(sysNames)} vs before=${JSON.stringify(baseline)}`,
  }
})

// ═══════════════════════════ G1.8 — live role change ═══════════════════

check('G1.8', 1, 'promoting to System lifts the restriction on the very next request', async () => {
  await setRole(ctx.u.gen.id, 'System')
  const m = mark()
  const r = await chat(ctx.u.gen, BIG)
  const ran = modelsRun(m)
  await setRole(ctx.u.gen.id, 'General')
  return {
    ok: r.status !== 403 && ran.includes(BIG),
    detail: `status=${r.status}, fake ran ${ran.join(', ') || '(nothing)'}`,
  }
})

check('G1.8', 1, 'demoting restores it on the very next request, with no restart', async () => {
  const m = mark()
  const r = await chat(ctx.u.gen, BIG)
  const ran = modelsRun(m)
  return { ok: r.status === 403 && !ran.includes(BIG), detail: `status=${r.status}, fake ran ${ran.join(', ') || '(nothing)'}` }
})

// ═══════════════════════════ G1.9 — admin routes ═══════════════════════

check('G1.9', 1, 'the policy routes refuse a non-admin with 403', async () => {
  const get = await getPolicies(ctx.u.sys.token)
  const put = await call(ctx.api.url, 'PUT', '/api/admin/rbac/model-policies/General', {
    token: ctx.u.sys.token, body: { allowedModels: [SMALL], defaultModel: SMALL },
  })
  return { ok: get.status === 403 && put.status === 403, detail: `GET=${get.status} PUT=${put.status}` }
})

check('G1.9', 1, 'a default outside the allowed list is 400', async () => {
  const r = await setPolicy('General', { allowedModels: [SMALL], defaultModel: BIG })
  return { ok: r.status === 400, detail: `status=${r.status} ${JSON.stringify(r.json?.error ?? '').slice(0, 90)}` }
})

check('G1.9', 1, 'an unknown role is 400', async () => {
  const r = await setPolicy('Wizard', { allowedModels: [SMALL], defaultModel: SMALL })
  return { ok: r.status === 400, detail: `status=${r.status}` }
})

check('G1.9', 1, 'a model that is not installed is 400', async () => {
  const r = await setPolicy('General', { allowedModels: ['not-installed:9b'], defaultModel: 'not-installed:9b' })
  return { ok: r.status === 400, detail: `status=${r.status}` }
})

check('G1.9', 1, 'restricting SystemAdmin is refused', async () => {
  const r = await setPolicy('SystemAdmin', { allowedModels: [SMALL], defaultModel: SMALL })
  return { ok: r.status === 400, detail: `status=${r.status} ${JSON.stringify(r.json?.error ?? '').slice(0, 90)}` }
})

check('G1.9', 1, 'a saved change applies to the next request', async () => {
  await setPolicy('General', { allowedModels: [SMALL, MID], defaultModel: SMALL })
  const m = mark()
  const ok = await chat(ctx.u.gen, MID)
  const ran = modelsRun(m)
  await setPolicy('General', { allowedModels: [SMALL], defaultModel: SMALL })
  const after = await chat(ctx.u.gen, MID)
  return {
    ok: ok.status !== 403 && ran.includes(MID) && after.status === 403,
    detail: `after-save=${ok.status} (fake ran ${ran.join(', ') || 'nothing'}), after-revert=${after.status}`,
  }
})

// ═══════════════════════════ G1.10 — rows per role ═════════════════════

check('G1.10', 1, 'a fresh database has one row per role with the seed values', async () => {
  const rows = await sql('select Role, AllowedModels, DefaultModel from RoleModelPolicies order by Role')
  const got = Object.fromEntries(rows.map((r) => [r.Role, r]))
  const bad = []
  for (const [role, want] of Object.entries(SEED)) {
    const row = got[role]
    if (!row) { bad.push(`${role}: no row`); continue }
    const allowed = String(row.AllowedModels ?? '').split(',').map((x) => x.trim()).filter(Boolean)
    if (JSON.stringify(allowed) !== JSON.stringify(want.allowed)) {
      bad.push(`${role}: allowed=${JSON.stringify(allowed)}≠${JSON.stringify(want.allowed)}`)
    }
    if (String(row.DefaultModel ?? '') !== want.def) bad.push(`${role}: default=${row.DefaultModel}≠${want.def}`)
  }
  return { ok: !bad.length, detail: bad.length ? bad.join('; ') : `${rows.length} rows: ${rows.map((r) => r.Role).join(' ')}` }
})

check('G1.10', 1, 'restricting System takes effect, and clearing it restores every model', async () => {
  await setPolicy('System', { allowedModels: [MID], defaultModel: MID })
  const m1 = mark()
  const refused = await chat(ctx.u.sys, BIG)
  const allowed = await chat(ctx.u.sys, MID)
  const ran1 = modelsRun(m1)

  await setPolicy('System', { allowedModels: [], defaultModel: '' })
  const m2 = mark()
  const freed = await chat(ctx.u.sys, BIG)
  const ran2 = modelsRun(m2)

  const bad = []
  if (refused.status !== 403) bad.push(`restricted BIG=${refused.status}≠403`)
  if (allowed.status === 403) bad.push(`restricted MID=403`)
  if (ran1.includes(BIG)) bad.push('fake ran BIG while restricted')
  if (freed.status === 403) bad.push(`cleared BIG=403`)
  if (!ran2.includes(BIG)) bad.push('fake did not run BIG after clearing')
  return { ok: !bad.length, detail: bad.length ? bad.join('; ') : 'restrict → refuse, clear → allow, no restart' }
})

// ═══════════════════════════ G1.1 — one rule, one place ════════════════

check('G1.1', 1, 'every model entry point calls the shared check (§0.5: no copy of the rule)', async () => {
  const files = {
    'MinimalApis/ChatEndpoint.cs': true,
    'MinimalApis/DocumentApis.cs': true,
    'MinimalApis/ComponentSplitterApis.cs': true,
    'Oddity/OddityProxy.cs': true,
  }
  const missing = []
  for (const rel of Object.keys(files)) {
    const p = path.join(REPO, 'backend', rel)
    if (!fs.existsSync(p)) { missing.push(`${rel} (absent)`); continue }
    const src = fs.readFileSync(p, 'utf8')
    if (!/ModelPolicy|IModelPolicy|_policy\.|modelPolicy/i.test(src)) missing.push(rel)
  }
  return { ok: !missing.length, detail: missing.length ? `no policy call in: ${missing.join(', ')}` : 'all four call it' }
})

check('G1.1', 1, 'the rule itself lives in exactly one file', async () => {
  const p = path.join(REPO, 'backend', 'Rbac', 'ModelPolicy.cs')
  return { ok: fs.existsSync(p), detail: fs.existsSync(p) ? 'backend/Rbac/ModelPolicy.cs' : 'ModelPolicy.cs missing' }
})

// ========================= PHASE 2 =========================
// What a browser-free harness can prove: the SERVER answers the picker reads,
// the admin routes behind the editor, and the source of every list the UI
// renders. The visual gates (G2.1, G2.3, G2.4 screenshots) are run separately
// with a browser and reported with their images.

const vue = (rel) => fs.readFileSync(path.join(REPO, 'frontend', 'src', rel), 'utf8')
const vueIfAny = (rel) => {
  const p = path.join(REPO, 'frontend', 'src', rel)
  return fs.existsSync(p) ? fs.readFileSync(p, 'utf8') : null
}

// ---- G2.1 / G2.3 - what the picker is told ----

check('G2.1', 2, 'the server tells the General picker one model and locked=true', async () => {
  const r = await call(ctx.api.url, 'GET', '/api/models', { token: ctx.u.gen.token })
  const names = (r.json?.models ?? r.json ?? []).map((m) => m.name)
  const locked = r.headers.get('x-model-locked')
  const ok = names.length === 1 && names[0] === SMALL && locked === 'true'
  return { ok, detail: `models=${JSON.stringify(names)} X-Model-Locked=${locked}` }
})

check('G2.1', 2, 'the locked picker shows the name as text with a lock and the role line, no dropdown', async () => {
  const src = vue('components/ModelPicker.vue')
  const bad = []
  if (!/Set by your role/.test(src)) bad.push('no "Set by your role" line')
  if (!/Lock/.test(src)) bad.push('no lock icon')
  // The refusal to render the control that opens the list is the real gate:
  // a merely disabled dropdown still shows the other models.
  if (!/locked/.test(src)) bad.push('no locked branch')
  return { ok: !bad.length, detail: bad.length ? bad.join('; ') : 'locked branch: text + lock + role line' }
})

check('G2.1', 2, 'a General user can still send and stream with the locked model', async () => {
  const m = mark()
  const r = await chat(ctx.u.gen, SMALL)
  const ran = modelsRun(m)
  return { ok: r.status === 200 && ran.includes(SMALL), detail: `status=${r.status} fake ran ${ran.join(', ') || '(nothing)'}` }
})

check('G2.3', 2, 'a System user is told every model and is not locked', async () => {
  const r = await call(ctx.api.url, 'GET', '/api/models', { token: ctx.u.sys.token })
  const names = (r.json?.models ?? r.json ?? []).map((m) => m.name)
  const locked = r.headers.get('x-model-locked')
  return {
    ok: names.length > 1 && locked === null,
    detail: `models=${JSON.stringify(names)} X-Model-Locked=${locked}`,
  }
})

// ---- G2.2 - a stale saved choice corrects itself (F9) ----

check('G2.2', 2, 'a stale saved model is absent from the General list, so the picker must replace it', async () => {
  const r = await call(ctx.api.url, 'GET', '/api/models', { token: ctx.u.gen.token })
  const names = (r.json?.models ?? r.json ?? []).map((m) => m.name)
  // The browser had BIG saved. It is absent from the list, which is what makes
  // the models[0] fallback (F9) fire; the picker also re-validates the
  // remembered name against the list it was served.
  const picker = vue('components/ModelPicker.vue')
  const bad = []
  if (names.includes(BIG)) bad.push(`${BIG} still offered to General`)
  if (!/all\.find\(|flatMap/.test(picker)) bad.push('picker does not re-validate the remembered choice')
  return { ok: !bad.length, detail: bad.length ? bad.join('; ') : `General offered ${JSON.stringify(names)}; a saved ${BIG} cannot survive` }
})

check('G2.2', 2, 'and the first message after that correction succeeds', async () => {
  const m = mark()
  const r = await chat(ctx.u.gen, SMALL)
  const ran = modelsRun(m)
  return { ok: r.status === 200 && ran.includes(SMALL), detail: `status=${r.status} fake ran ${ran.join(', ')}` }
})

// ---- G2.4 - two allowed models become a normal dropdown ----

check('G2.4', 2, 'allowing two models gives General a two-item list and drops locked', async () => {
  await setPolicy('General', { allowedModels: [SMALL, MID], defaultModel: SMALL })
  const r = await call(ctx.api.url, 'GET', '/api/models', { token: ctx.u.gen.token })
  const names = (r.json?.models ?? r.json ?? []).map((m) => m.name).sort()
  const locked = r.headers.get('x-model-locked')
  const ok = names.length === 2 && names.join(',') === [SMALL, MID].sort().join(',') && locked === null
  return { ok, detail: `models=${JSON.stringify(names)} X-Model-Locked=${locked}` }
})

check('G2.4', 2, 'and the second model actually runs for that user', async () => {
  const m = mark()
  const r = await chat(ctx.u.gen, MID)
  const ran = modelsRun(m)
  return { ok: r.status === 200 && ran.includes(MID), detail: `status=${r.status} fake ran ${ran.join(', ')}` }
})

check('G2.4', 2, 'setting it back restores the single locked model', async () => {
  await setPolicy('General', { allowedModels: [SMALL], defaultModel: SMALL })
  const r = await call(ctx.api.url, 'GET', '/api/models', { token: ctx.u.gen.token })
  const names = (r.json?.models ?? r.json ?? []).map((m) => m.name)
  const locked = r.headers.get('x-model-locked')
  const back = await chat(ctx.u.gen, MID)
  const ok = names.length === 1 && names[0] === SMALL && locked === 'true' && back.status === 403
  return { ok, detail: `models=${JSON.stringify(names)} locked=${locked} ${MID}=${back.status}` }
})

// ---- G2.5 - the editor shows the server errors, and Save needs a change ----

check('G2.5', 2, 'the editor renders the server message rather than its own copy of the rule', async () => {
  const src = vueIfAny('components/rbac/ModelPolicyEditor.vue') ?? vue('components/tabs/RbacTab.vue')
  const bad = []
  if (!/error/i.test(src)) bad.push('no error binding')
  // A restated rule would drift from the server one. The message has to come
  // back from the request, never be composed in the browser.
  if (/can use:|is not in the allowed list|Not installed:/i.test(src)) bad.push('the editor restates the rule')
  if (!/disabled/.test(src)) bad.push('Save is never disabled')
  return { ok: !bad.length, detail: bad.length ? bad.join('; ') : 'binds the server message; Save is gated' }
})

check('G2.5', 2, 'each validation error comes back with a message the editor can show', async () => {
  const cases = {
    'default outside list': await setPolicy('General', { allowedModels: [SMALL], defaultModel: BIG }),
    'unknown role': await setPolicy('Wizard', { allowedModels: [SMALL], defaultModel: SMALL }),
    'not installed': await setPolicy('General', { allowedModels: ['no-such-model'], defaultModel: '' }),
    'restrict admin': await setPolicy('SystemAdmin', { allowedModels: [SMALL], defaultModel: SMALL }),
  }
  const bad = Object.entries(cases)
    .filter(([, r]) => r.status !== 400 || !r.json?.error)
    .map(([k, r]) => `${k}=${r.status}/${JSON.stringify(r.json?.error ?? null)}`)
  return { ok: !bad.length, detail: bad.length ? bad.join(' ') : 'all four 400 with a message' }
})

// ---- G2.6 - hidden from a non-admin, and 403 for them ----

check('G2.6', 2, 'the editor sits inside the admin-only rbac tab', async () => {
  const app = vue('App.vue')
  const ok = /isSystemAdmin/.test(app) && /allowedTabs/.test(app) && /rbac/.test(app)
  return { ok, detail: ok ? 'the rbac tab is admin-only and never grantable; the editor lives inside it' : 'the admin gate is missing' }
})

check('G2.6', 2, 'and both policy routes answer 403 for a non-admin', async () => {
  const get = await getPolicies(ctx.u.gen.token)
  const put = await call(ctx.api.url, 'PUT', '/api/admin/rbac/model-policies/General', {
    token: ctx.u.gen.token, body: { allowedModels: [SMALL], defaultModel: SMALL },
  })
  const ok = get.status === 403 && put.status === 403
  return { ok, detail: `GET=${get.status} PUT=${put.status}` }
})

check('G2.6', 2, 'the rbac tab cannot be granted to a non-admin', async () => {
  await setGrants(ctx.u.spare.id, ['rbac'])
  const t = (await meTabs(ctx.u.spare)).tabs
  await setGrants(ctx.u.spare.id, [])
  return { ok: !t.includes('rbac'), detail: `tabs after granting rbac: ${t.join(' ')}` }
})

// ---- G2.7 - no hard-coded colours in changed files ----

check('G2.7', 2, 'no hard-coded colours in the .vue files this build changed', async () => {
  const changed = ['components/ModelPicker.vue', 'components/tabs/RbacTab.vue',
                   'components/rbac/ModelPolicyEditor.vue']
  const hits = []
  for (const rel of changed) {
    const src = vueIfAny(rel)
    if (src === null) continue
    src.split('\n').forEach((line, i) => {
      if (/#[0-9a-fA-F]{3,8}\b|rgb\(/.test(line)) hits.push(`${rel}:${i + 1}`)
    })
  }
  return { ok: !hits.length, detail: hits.length ? hits.join(' ') : 'no matches in the changed .vue files' }
})

// ---- G2.8 - a fresh General user sees exactly the five ----

check('G2.8', 2, 'a fresh General user is served exactly the five tabs', async () => {
  const t = (await meTabs(ctx.u.spare)).tabs
  const got = [...t].sort().join(',')
  const want = [...GENERAL_TABS].sort().join(',')
  return { ok: got === want, detail: `${JSON.stringify(t)} vs ${JSON.stringify(GENERAL_TABS)}` }
})

check('G2.8', 2, 'the tab bar, the palette and the route guard all read that one list', async () => {
  const app = vue('App.vue')
  const bad = []
  // One source for all three. A second hand-written list is what this guards.
  if (!/ALL_TABS\.filter/.test(app)) bad.push('tab bar does not filter by the server list')
  if (!/\.\.\.tabs\.value\.map/.test(app)) bad.push('palette does not read the filtered list')
  if (!/useRoutePermissions/.test(app)) bad.push('route guard does not read it')
  return { ok: !bad.length, detail: bad.length ? bad.join('; ') : 'tab bar, palette and guard all derive from /api/me/tabs' }
})

check('G2.8', 2, 'chat and jsonto are absent for a fresh General user, so both redirect away', async () => {
  const t = (await meTabs(ctx.u.spare)).tabs
  const leaked = ['chat', 'jsonto'].filter((k) => t.includes(k))
  return { ok: !leaked.length, detail: leaked.length ? `still allowed: ${leaked.join(' ')}` : 'neither chat nor jsonto is in the set' }
})

check('G2.8', 2, 'the Access role preview comes from the server, not a hand-written copy (F2b)', async () => {
  const src = vue('components/tabs/RbacTab.vue')
  const bad = []
  if (!/roleCatalog/.test(src)) bad.push('no roleCatalog from the server')
  const cat = await call(ctx.api.url, 'GET', '/api/admin/rbac/roles', { token: ctx.admin.token })
  const gen = (cat.json ?? []).find((r) => r.key === 'General')
  const served = [...(gen?.defaultTabs ?? [])].sort().join(',')
  const want = [...GENERAL_TABS].sort().join(',')
  if (served !== want) bad.push(`server role catalogue says ${JSON.stringify(gen?.defaultTabs)}`)
  return { ok: !bad.length, detail: bad.length ? bad.join('; ') : `catalogue serves ${served}` }
})

// ═══════════════════════════ runner ════════════════════════════════════

async function main() {
  const args = process.argv.slice(2)
  const phase = Number(args[args.indexOf('--phase') + 1]) || null
  const only = args.includes('--only') ? args[args.indexOf('--only') + 1] : null
  const noBuild = args.includes('--no-build')

  const user = process.env.MOSTISKO_ADMIN_USER
  const secret = process.env.MOSTISKO_ADMIN_SECRET
  if (!user || !secret) {
    console.error('Set MOSTISKO_ADMIN_USER and MOSTISKO_ADMIN_SECRET (the seeded administrator).')
    process.exit(2)
  }

  const runDir = path.join(EVAL_ROOT, String(Date.now()))
  fs.mkdirSync(runDir, { recursive: true })

  if (!noBuild) buildApi()
  ctx.fake = await startFakeOllama({ models: FAKE_MODELS })
  ctx.api = await startApi({ fakeUrl: ctx.fake.url, runDir })

  try {
    ctx.admin = await login(user, secret)
    if (!ctx.admin.me?.isSystemAdmin) throw new Error(`${user} is not a SystemAdmin`)

    const pw = 'eval-' + crypto.randomBytes(6).toString('hex')
    ctx.u = {
      gen: await login('mp-general', pw),
      sys: await login('mp-system', pw),
      spare: await login('mp-spare', pw),
    }
    await setRole(ctx.u.sys.id, 'System')

    // Captured before any policy exists, so G1.7 can prove System is unchanged.
    const before = await call(ctx.api.url, 'GET', '/api/models', { token: ctx.u.sys.token })
    ctx.modelsBefore = (before.json?.models ?? before.json ?? []).map?.((x) => x.name) ?? []

    // The gates need the General user to hold the model tabs (§4 G1.3).
    await setGrants(ctx.u.gen.id, ['chat', 'workspace', 'docs', 'component-splitter'])
    await setGrants(ctx.u.sys.id, ['docs'])

    ctx.docId = await seedDocument(ctx.u.gen)
    ctx.sysDocId = await seedDocument(ctx.u.sys)

    const selected = checks.filter((c) =>
      (!phase || c.phase <= phase) && (!only || c.id === only || c.id.startsWith(only)))

    let passed = 0
    for (const c of selected) {
      let r
      try { r = await c.fn() } catch (e) { r = { ok: false, detail: `threw: ${e.message}` } }
      if (r.ok) passed++
      console.log(`${c.id} | ${c.name} | ${r.ok ? 'PASS' : 'FAIL'} | ${r.detail ?? ''}`)
    }
    console.log(`\n${passed}/${selected.length} passed`)
    process.exitCode = passed === selected.length ? 0 : 1
  } finally {
    await ctx.api?.stop()
    await ctx.fake?.stop()
  }
}

/** Uploads a tiny document so the extraction entry point has something to act on. */
async function seedDocument(u) {
  const form = new FormData()
  form.append('file', new Blob(['Order 123\nStyle ABC\nQty 10'], { type: 'text/plain' }), 'po.txt')
  const res = await fetch(`${ctx.api.url}/api/documents`, {
    method: 'POST', headers: { Authorization: `Bearer ${u.token}` }, body: form,
  })
  const j = await res.json().catch(() => null)
  return j?.id ?? j?.documentId ?? null
}

main().catch((e) => { console.error(e); process.exit(1) })
