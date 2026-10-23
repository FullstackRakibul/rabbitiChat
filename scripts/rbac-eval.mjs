#!/usr/bin/env node
// Access (role presets) eval — see docs/RBAC_ROLES_PROMPT.md §4–§6.
//
//   node scripts/rbac-eval.mjs --phase P1              every P1 check
//   node scripts/rbac-eval.mjs --only G-P1.4           one gate
//   node scripts/rbac-eval.mjs --no-build              reuse the last eval build
//   node scripts/rbac-eval.mjs --baseline <ref>        the build G-P1.2 compares against
//                                                      (default: merge-base with hr-knowledge)
//   node scripts/rbac-eval.mjs --dev-db <file>         the dev database G-P1.2 copies
//   node scripts/rbac-eval.mjs --shots <dir>           where G-P1.6 writes its screenshots
//
// The fake Ollama and the API start-up are REUSED from scripts/oddity-eval.mjs.
// Admin credentials come from RBAC_EVAL_ADMIN_USER and RBAC_EVAL_ADMIN_SECRET
// (the seeded administrator); they are never written here.
//
// Expected values are written by hand from the design contract and from today's
// behaviour (§2), never read from PermissionCatalog.cs: a value read from the
// code would only prove the code agrees with itself. Node 22+, no dependencies.

import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import crypto from 'node:crypto'
import { spawnSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'

import { buildApi, startApi, call, startFakeOllama, API_BIN_DIR } from './oddity-eval.mjs'
import { launchBrowser } from './lib/headless-browser.mjs'

const REPO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const EVAL_ROOT = path.join(os.tmpdir(), 'rbac-eval')
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

// ─────────────────────── expected values, written by hand ───────────────────────

const ROLES = ['General', 'HrAssistant', 'DevOps', 'System', 'GroundControl', 'SystemAdmin']

/** Today's role baselines (§2 P-F2, TabCatalog before this build). */
const GENERAL = ['dashboard', 'todo', 'notes', 'sources', 'settings', '5lakhs']
const SYSTEM = [...GENERAL, 'chat', 'workspace', 'docs', 'search', 'ielts', 'component-splitter']
/**
 * Tabs a module added after P1, with the roles its prompt seeds them for. They are
 * part of the expected presets, and outside G-P1.2's before/after (the build P1 is
 * compared with predates them).
 */
const LATER_TABS = {
  matrix: ['HrAssistant', 'System', 'GroundControl', 'SystemAdmin'],   // docs/MATRIX_PROMPT.md §3.10
  truth: ['SystemAdmin'],                                              // docs/TRUTH_PROMPT.md §8: the admin preset only
}
const withLater = (role, tabs) => [...tabs, ...Object.keys(LATER_TABS).filter((t) => LATER_TABS[t].includes(role))]

const BASELINE = Object.fromEntries(Object.entries({
  General: GENERAL,
  HrAssistant: ['hr', 'notes', 'settings'],
  DevOps: ['hr', 'settings'],
  System: SYSTEM,
  GroundControl: [...SYSTEM, 'oddity'],
  SystemAdmin: ['dashboard', 'chat', 'workspace', 'docs', 'hr', 'todo', 'search', 'jsonto', '5lakhs', 'ielts',
    'component-splitter', 'sources', 'settings', 'notes', 'logs', 'oddity', 'rbac'],
}).map(([role, tabs]) => [role, withLater(role, tabs)]))

/** The capability keys §3.1 names that exist today, and who holds them (§2 P-F5). */
const ACTIONS = {
  'action:hr.library.edit': ['HrAssistant', 'SystemAdmin'],
  'action:hr.rulebook.edit': ['HrAssistant', 'SystemAdmin'],
  'action:hr.rulebook.confirm': ['HrAssistant', 'SystemAdmin'],
  'action:hr.procedures.upload': ['DevOps', 'SystemAdmin'],
  'action:hr.procedures.answer': ['DevOps', 'HrAssistant', 'SystemAdmin'],
  'action:hr.lookups.edit': ['DevOps', 'SystemAdmin'],
  'action:hr.library.stats': ['SystemAdmin'],
  'action:logs.audit': ['SystemAdmin'],
  'action:access.manage': ['SystemAdmin'],
}

/** TabAccessMiddleware's gated prefixes (§2 P-F3), probed by the access dump. */
const GATED = ['/api/chat', '/api/conversations', '/api/documents', '/api/buyer-profiles', '/api/po-studio',
  '/api/search', '/api/context-sources', '/api/todorabbit', '/api/ielts', '/api/component-splitter',
  '/api/notes', '/api/logs', '/api/stream/logs', '/api/oddity', '/api/hr']

const INVARIANTS = ['system-admin-has-everything', 'settings-always-on', 'last-system-admin']

// ─────────────────────────────── harness ───────────────────────────────

const checks = []
function check(id, phase, name, fn) { checks.push({ id, phase, name, fn }) }
const ok = (pass, detail) => ({ pass: !!pass, detail })
const show = (v) => (Array.isArray(v) ? `[${v.join(',')}]` : JSON.stringify(v))
const sorted = (a) => [...(a ?? [])].sort()
const sameSet = (a, b) => sorted(a).join(',') === sorted(b).join(',')
const read = (rel) => fs.readFileSync(path.join(REPO, rel), 'utf8')

const ctx = { args: null }

async function sql(query, ...params) {
  const { DatabaseSync } = await import('node:sqlite')
  const db = new DatabaseSync(ctx.api.dbPath, { readOnly: true })
  try { return db.prepare(query).all(...params) } finally { db.close() }
}

/** Writes with the API stopped, then starts it again: the only way to change what it caches. */
async function sqlWhileStopped(statements) {
  await ctx.api.stop()
  const { DatabaseSync } = await import('node:sqlite')
  try {
    const db = new DatabaseSync(ctx.api.dbPath)
    try { for (const [q, ...p] of statements) db.prepare(q).run(...p) } finally { db.close() }
  } finally {
    // Started again even when a statement failed, so one failing check cannot take the rest down.
    ctx.api = await startApi({ fakeUrl: ctx.fake.url, runDir: ctx.api.runDir, port: ctx.api.port })
  }
}

async function restart() {
  await ctx.api.stop()
  ctx.api = await startApi({ fakeUrl: ctx.fake.url, runDir: ctx.api.runDir, port: ctx.api.port })
}

async function loginAt(base, username, secret) {
  const r = await call(base, 'POST', '/api/auth/login', { body: { username, secret } })
  if (r.status !== 200 || !r.json?.token) throw new Error(`login ${username} → ${r.status} ${r.text.slice(0, 200)}`)
  const me = await call(base, 'GET', '/api/me/tabs', { token: r.json.token })
  return { username, token: r.json.token, id: me.json?.id }
}
const login = (u, s) => loginAt(ctx.api.url, u, s)

const as = (user, method, p, body) => call(ctx.api.url, method, p, { token: user.token, body })
const asAdmin = (method, p, body) => as(ctx.admin, method, p, body)
const myTabs = async (user) => (await as(user, 'GET', '/api/me/tabs')).json?.tabs ?? []
const putRole = (role, values, note, user = ctx.admin) => as(user, 'PUT', `/api/access/roles/${role}`, { values, note })

/** The eval's users: one per role, two plain Generals, and a General holding grants. */
async function users() {
  if (ctx.users) return ctx.users
  const secret = 'rbac-' + crypto.randomBytes(6).toString('hex')
  const u = {}
  for (const [k, name, role, grants] of [
    ['gen1', 'rbac-general-1', 'General'],
    ['gen2', 'rbac-general-2', 'General'],
    ['genHr', 'rbac-general-hr', 'General', ['hr', 'logs']],
    ['sys', 'rbac-system', 'System'],
    ['gc', 'rbac-ground', 'GroundControl'],
    ['hra', 'rbac-hr-assistant', 'HrAssistant'],
    ['dev', 'rbac-devops', 'DevOps'],
  ]) {
    u[k] = await login(name, secret)
    if (role !== 'General') {
      const r = await asAdmin('PUT', `/api/admin/rbac/users/${u[k].id}/role`, { role })
      if (r.status !== 200) throw new Error(`set ${name} → ${role}: ${r.status} ${r.text}`)
    }
    if (grants) {
      const r = await asAdmin('PUT', `/api/admin/rbac/users/${u[k].id}/tabs`, { tabKeys: grants })
      if (r.status !== 200) throw new Error(`grant ${name} ${grants}: ${r.status} ${r.text}`)
    }
  }
  ctx.users = u
  return u
}

// ─────────────────────── the access dump (G-P1.2) ───────────────────────
// Everything a user's access decides, read through the API only — so the same
// function runs against the build from before the change and the build after it.

async function dumpUser(base, token) {
  const get = (p) => call(base, 'GET', p, { token })
  const me = await get('/api/me/tabs')
  if (me.status !== 200) return { session: me.status }
  const gates = {}
  for (const prefix of GATED) gates[prefix] = (await get(`${prefix}/__rbac_probe__`)).status === 403
  gates['/api/documents/workspace-chat'] = (await get('/api/documents/workspace-chat')).status === 403

  const lib = await get('/api/hr/library')
  const proc = await get('/api/hr/procedures')
  const look = await get('/api/hr/lookups')
  const rule = await get('/api/hr/rulebook')
  const models = await get('/api/models')
  return {
    role: me.json.role,
    isSystemAdmin: me.json.isSystemAdmin,
    tabs: sorted(me.json.tabs.filter((t) => !(t in LATER_TABS))),
    gates,
    capabilities: {
      libraryEdit: lib.json?.canEdit ?? null,
      procedureUpload: proc.json?.canUpload ?? null,
      questionAnswer: proc.json?.canAnswer ?? null,
      registryEdit: proc.json?.canEditRegistry ?? null,
      lookupsEdit: look.json?.canEdit ?? null,
      rulebookEdit: rule.json?.canEdit ?? null,
      indexStats: (await get('/api/hr/library/index-stats')).status,
      auditLog: (await get('/api/logs/audit')).status,
      accessAdmin: (await get('/api/admin/rbac/tabs')).status,
      modelPolicyAdmin: (await get('/api/admin/rbac/model-policies')).status,
    },
    models: {
      status: models.status,
      names: sorted((Array.isArray(models.json) ? models.json : models.json?.models ?? []).map((m) => `${m.source ?? ''}/${m.name ?? m}`)),
      locked: models.json?.locked ?? null,
    },
  }
}

async function dumpAll(base, dbPath) {
  const { DatabaseSync } = await import('node:sqlite')
  const db = new DatabaseSync(dbPath, { readOnly: true })
  let rows
  try { rows = db.prepare('select Id, Username, Token from Users order by Id').all() } finally { db.close() }
  const out = {}
  for (const r of rows) out[`${r.Id}:${r.Username}`] = r.Token ? await dumpUser(base, r.Token) : { session: 'none' }
  return out
}

function gitDiff(a, b) {
  const r = spawnSync('git', ['-c', 'core.autocrlf=false', '-c', 'core.safecrlf=false', 'diff', '--no-index', '--exit-code', a, b], { encoding: 'utf8' })
  return { code: r.status, out: (r.stdout + r.stderr).trim() }
}

/** The build from before this change, made once per commit from a throwaway worktree. */
function baselineBuild() {
  const ref = ctx.args.baseline
    ?? spawnSync('git', ['merge-base', 'HEAD', 'hr-knowledge'], { cwd: REPO, encoding: 'utf8' }).stdout.trim()
  const sha = spawnSync('git', ['rev-parse', ref], { cwd: REPO, encoding: 'utf8' }).stdout.trim()
  const dir = path.join(EVAL_ROOT, `baseline-${sha.slice(0, 10)}`)
  const bin = path.join(dir, 'bin')
  if (fs.existsSync(path.join(bin, 'RabbitIC.Api.dll'))) return { sha, bin }
  const src = path.join(dir, 'src')
  if (!fs.existsSync(src)) {
    const w = spawnSync('git', ['worktree', 'add', '--detach', src, sha], { cwd: REPO, encoding: 'utf8' })
    if (w.status !== 0) throw new Error(`worktree: ${w.stderr}`)
  }
  const b = spawnSync('dotnet', ['build', path.join(src, 'backend', 'RabbitIC.Api.csproj'), '-o', bin, '-nologo', '-v', 'q'],
    { encoding: 'utf8', shell: process.platform === 'win32' })
  if (b.status !== 0) throw new Error(`baseline build failed:\n${b.stdout}\n${b.stderr}`)
  spawnSync('git', ['worktree', 'remove', '--force', src], { cwd: REPO })
  return { sha, bin }
}

// ═══════════════════════════════ Phase P1 ═══════════════════════════════

// ---- G-P1.1 The list of role checks, now calling AccessPolicy ----

/**
 * Files that may still name a role, each with why it is not an access decision.
 * Everything else under backend/ that compares a role fails the gate.
 */
const ROLE_NAMES_ALLOWED = {
  'backend/Models/Rbac.cs': 'the roles themselves and their normalisation (§0 rule 6)',
  'backend/Rbac/AccessPolicy.cs': 'THE policy: the SystemAdmin and Settings invariants live here',
  'backend/Rbac/RolePresetSeeds.cs': "today's baselines, written once into RolePermissions for missing rows",
  'backend/Rbac/RbacSeeder.cs': 'seeding: the built-in admin account and each role’s first model-policy row',
  'backend/Hr/HrAssistantSeeder.cs': 'seeding: creates the hr-assistant/devops accounts with their role',
  'backend/Program.cs': 'start-up wiring: which seeder runs with which role, and the owner of seeded HR items',
  'backend/Repositories/RbacRepository.cs': 'counts SystemAdmins for the last-admin invariant',
  'backend/Models/Entities.cs': "the Role column's default value for a new account (General)",
}

check('G-P1.1', 'P1', 'no role comparison decides access outside AccessPolicy (git grep)', async () => {
  const re = String.raw`UserRoles\.(General|HrAssistant|DevOps|System|GroundControl|SystemAdmin)\b|IsSystemAdmin\(|CanEditShared\(|CanAccessTabAsync|TabCatalog\.(DefaultTabsFor|EffectiveTabs)`
  const r = spawnSync('git', ['grep', '-nE', re, '--', 'backend/*.cs'], { cwd: REPO, encoding: 'utf8' })
  const hits = r.stdout.split('\n').filter(Boolean)
    .filter((l) => !/^\S+:\d+:\s*(\/\/|\/\/\/|\*)/.test(l))   // comments do not decide anything
  const outside = hits.filter((l) => !Object.keys(ROLE_NAMES_ALLOWED).some((f) => l.startsWith(f + ':')))
  return ok(hits.length > 0 && outside.length === 0,
    `${hits.length} role mentions, all in [${Object.keys(ROLE_NAMES_ALLOWED).map((f) => path.basename(f)).join(', ')}]`
    + (outside.length ? ` · OUTSIDE: ${outside.slice(0, 8).join(' | ')}` : ''))
})

check('G-P1.1', 'P1', 'every capability site calls AccessPolicy with its action key', async () => {
  const sites = {
    'backend/Services/ContextSourceService.cs': ['action:hr.library.edit'],
    'backend/Hr/HrRulebook.cs': ['action:hr.rulebook.edit', 'action:hr.rulebook.confirm'],
    'backend/Hr/HrProcedures.cs': ['action:hr.procedures.upload', 'action:hr.procedures.answer', 'action:hr.lookups.edit'],
    'backend/MinimalApis/HrApis.cs': ['action:hr.library.stats'],
    'backend/MinimalApis/LogEndpoints.cs': ['action:logs.audit'],
    'backend/Middleware/TabAccessMiddleware.cs': ['tab:'],
    'backend/Oddity/OddityProxy.cs': ['tab:oddity'],
    'backend/Services/RbacService.cs': ['action:access.manage'],
  }
  const catalog = read('backend/Rbac/PermissionCatalog.cs')
  const missing = []
  for (const [file, keys] of Object.entries(sites)) {
    const src = read(file)
    if (!/access\w*\.(HasAsync|GetAsync)|AccessPolicy/.test(src)) missing.push(`${file}: no AccessPolicy call`)
    for (const k of keys) {
      // The key is named in the catalog, and the site uses it (by constant or literal).
      if (!catalog.includes(`"${k}`)) missing.push(`${k} not in PermissionCatalog`)
    }
  }
  return ok(missing.length === 0, missing.length ? missing.join(' | ') : `${Object.keys(sites).length} sites`)
})

/** Today's capabilities, at the route (P-F5). Same answers before and after. */
check('G-P1.1', 'P1', "today's capabilities hold at the routes (fresh database)", async () => {
  const u = await users()
  const flags = async (user) => {
    const lib = await as(user, 'GET', '/api/hr/library')
    const proc = await as(user, 'GET', '/api/hr/procedures')
    const rule = await as(user, 'GET', '/api/hr/rulebook')
    return [lib.json?.canEdit, proc.json?.canUpload, proc.json?.canAnswer, proc.json?.canEditRegistry, rule.json?.canEdit]
      .map((v) => (v ? 1 : 0)).join('')
  }
  const want = { admin: '11111', hra: '10101', dev: '01110', genHr: '00000' }
  const got = {
    admin: await flags(ctx.admin), hra: await flags(u.hra), dev: await flags(u.dev), genHr: await flags(u.genHr),
  }
  // Routes, not flags: a refusal is decided before any input is read.
  const routes = [
    ['hra POST /api/hr/procedures', (await as(u.hra, 'POST', '/api/hr/procedures', {})).status, 403],
    ['dev POST /api/hr/procedures', (await as(u.dev, 'POST', '/api/hr/procedures', {})).status, 400],
    ['hra PUT /api/hr/lookups', (await as(u.hra, 'PUT', '/api/hr/lookups', {})).status, 403],
    ['genHr POST /api/hr/library', (await as(u.genHr, 'POST', '/api/hr/library', { name: 'rbac-eval probe', content: 'x' })).status, 403],
    ['hra GET index-stats', (await as(u.hra, 'GET', '/api/hr/library/index-stats')).status, 403],
    ['admin GET index-stats', (await asAdmin('GET', '/api/hr/library/index-stats')).status, 200],
    ['genHr GET /api/logs/audit', (await as(u.genHr, 'GET', '/api/logs/audit')).status, 403],
    ['admin GET /api/logs/audit', (await asAdmin('GET', '/api/logs/audit')).status, 200],
    ['sys GET /api/admin/rbac/tabs', (await as(u.sys, 'GET', '/api/admin/rbac/tabs')).status, 403],
  ]
  const badFlags = Object.keys(want).filter((k) => want[k] !== got[k])
  const badRoutes = routes.filter(([, s, w]) => s !== w)
  return ok(!badFlags.length && !badRoutes.length,
    `flags ${show(got)}${badFlags.length ? ` want ${show(want)}` : ''} · ${badRoutes.length ? badRoutes.map(([n, s, w]) => `${n} ${s}≠${w}`).join('; ') : `${routes.length} routes as expected`}`)
})

/**
 * The proof the checks read the data: flip General's action rows in the
 * database and a General holding the hr and logs tabs gains every capability at
 * the routes; flip back and they are gone. Code that still compared roles
 * would not move.
 */
check('G-P1.1', 'P1', 'each capability follows its RolePermissions row, not the role name', async () => {
  const u = await users()
  const keys = ['action:hr.library.edit', 'action:hr.library.stats', 'action:hr.rulebook.edit', 'action:hr.rulebook.confirm',
    'action:hr.procedures.upload', 'action:hr.procedures.answer', 'action:hr.lookups.edit', 'action:logs.audit']
  const probe = async () => {
    const lib = (await as(u.genHr, 'GET', '/api/hr/library')).json?.canEdit
    const proc = (await as(u.genHr, 'GET', '/api/hr/procedures')).json
    const rule = (await as(u.genHr, 'GET', '/api/hr/rulebook')).json?.canEdit
    return {
      flags: [lib, proc?.canUpload, proc?.canAnswer, proc?.canEditRegistry, rule].map((v) => (v ? 1 : 0)).join(''),
      upload: (await as(u.genHr, 'POST', '/api/hr/procedures', {})).status,
      stats: (await as(u.genHr, 'GET', '/api/hr/library/index-stats')).status,
      audit: (await as(u.genHr, 'GET', '/api/logs/audit')).status,
    }
  }
  const set = (v) => keys.map((k) => ["update RolePermissions set Allowed = ? where Role = 'General' and Key = ?", v, k])
  const rows = await sql(`select count(*) n from RolePermissions where Role = 'General' and Key in (${keys.map(() => '?').join(',')})`, ...keys)
  await sqlWhileStopped(set(1))
  const on = await probe()
  await sqlWhileStopped(set(0))
  const off = await probe()
  return ok(rows[0]?.n === keys.length
      && on.flags === '11111' && on.upload === 400 && on.stats === 200 && on.audit === 200
      && off.flags === '00000' && off.upload === 403 && off.stats === 403 && off.audit === 403,
    `${rows[0]?.n}/${keys.length} rows · on ${show(on)} · off ${show(off)}`)
})

// ---- G-P1.2 Nothing changed ----

check('G-P1.2', 'P1', 'every user’s effective access is identical before and after (fresh db and dev-db copy)', async () => {
  const { sha, bin } = baselineBuild()
  const dumps = path.join(EVAL_ROOT, 'dumps')
  fs.mkdirSync(dumps, { recursive: true })
  const lines = []
  let pass = true

  const scenario = async (name, prepare) => {
    const runDir = fs.mkdtempSync(path.join(EVAL_ROOT, `p12-${name}-`))
    await prepare(runDir)
    const old = await startApi({ fakeUrl: ctx.fake.url, runDir, binDir: bin })
    let before, oldGrants
    try {
      if (name === 'fresh') await seedFreshOld(old.url)
      before = await dumpAll(old.url, old.dbPath)
      oldGrants = await grantsIn(old.dbPath)
    } finally { await old.stop() }
    const neu = await startApi({ fakeUrl: ctx.fake.url, runDir })
    let after, overrides
    try {
      after = await dumpAll(neu.url, neu.dbPath)
      overrides = await overridesIn(neu.dbPath)
    } finally { await neu.stop() }
    const a = path.join(dumps, `${name}-before.json`), b = path.join(dumps, `${name}-after.json`)
    fs.writeFileSync(a, JSON.stringify(before, null, 2) + '\n')
    fs.writeFileSync(b, JSON.stringify(after, null, 2) + '\n')
    const d = gitDiff(a, b)
    const migrated = sameSet(oldGrants, overrides)
    pass &&= d.code === 0 && d.out === '' && migrated && Object.keys(before).length > 0
    lines.push(`${name}: ${Object.keys(before).length} users · git diff --no-index ${path.basename(a)} ${path.basename(b)} → exit ${d.code}${d.out ? ` «${d.out.slice(0, 600)}»` : ', empty'}`
      + ` · ${oldGrants.length} grants → ${overrides.length} allow overrides ${migrated ? '(same)' : `(DIFFER: ${show(oldGrants)} vs ${show(overrides)})`}`)
  }

  await scenario('fresh', () => {})
  const devDb = ctx.args.devDb
  if (devDb && fs.existsSync(devDb)) {
    await scenario('devcopy', async (runDir) => {
      const copy = path.join(runDir, 'rabbitic.db')
      fs.copyFileSync(devDb, copy)
      // A dev database the P1 build has already run on holds presets and overrides
      // people may have edited since. P1 leaves UserTabPermissions untouched, so
      // dropping its new tables from the COPY gives back the database as it was
      // before P1 — the state this before/after is about.
      const { DatabaseSync } = await import('node:sqlite')
      const db = new DatabaseSync(copy)
      try {
        const migrated = db.prepare("select name from sqlite_master where type = 'table' and name in ('RolePermissions', 'UserPermissionOverrides', 'AccessChangeLog')").all()
        for (const { name } of migrated) db.exec(`DROP TABLE "${name}"`)
        if (migrated.length) lines.push(`dev db copy was already migrated by the P1 build; its ${migrated.length} access tables were dropped from the copy first`)
      } finally { db.close() }
    })
  } else {
    pass = false
    lines.push(`dev db not found at ${devDb}`)
  }
  return ok(pass, `baseline ${sha.slice(0, 7)} · ${lines.join(' ‖ ')} · dumps in ${dumps}`)
})

/** On a fresh database, the old build: one user per role, and grants beyond the baseline. */
async function seedFreshOld(base) {
  const admin = await loginAt(base, process.env.RBAC_EVAL_ADMIN_USER, process.env.RBAC_EVAL_ADMIN_SECRET)
  const secret = 'p12-' + crypto.randomBytes(6).toString('hex')
  const plan = [
    ['p12-general', 'General', []], ['p12-general-grants', 'General', ['chat', 'hr', 'logs', 'oddity']],
    ['p12-hr', 'HrAssistant', []], ['p12-hr-grants', 'HrAssistant', ['sources', 'docs']],
    ['p12-devops', 'DevOps', []], ['p12-devops-grants', 'DevOps', ['logs']],
    ['p12-system', 'System', []], ['p12-system-grants', 'System', ['oddity', 'jsonto']],
    ['p12-ground', 'GroundControl', []], ['p12-admin2', 'SystemAdmin', []],
  ]
  for (const [name, role, grants] of plan) {
    const u = await loginAt(base, name, secret)
    if (role !== 'General') await call(base, 'PUT', `/api/admin/rbac/users/${u.id}/role`, { token: admin.token, body: { role } })
    if (grants.length) await call(base, 'PUT', `/api/admin/rbac/users/${u.id}/tabs`, { token: admin.token, body: { tabKeys: grants } })
  }
}

async function grantsIn(dbPath) {
  const { DatabaseSync } = await import('node:sqlite')
  const db = new DatabaseSync(dbPath, { readOnly: true })
  try { return db.prepare('select UserId, TabKey from UserTabPermissions').all().map((r) => `${r.UserId}:tab:${r.TabKey}`) } finally { db.close() }
}

async function overridesIn(dbPath) {
  const { DatabaseSync } = await import('node:sqlite')
  const db = new DatabaseSync(dbPath, { readOnly: true })
  try {
    return db.prepare("select UserId, Key, Effect from UserPermissionOverrides").all()
      .map((r) => `${r.UserId}:${r.Key}${r.Effect === 'allow' ? '' : `(${r.Effect})`}`)
  } finally { db.close() }
}

// ---- G-P1.3 Seeds ----

check('G-P1.3', 'P1', 'a fresh database has exactly one RolePermissions row per role × catalog key', async () => {
  const cat = (await asAdmin('GET', '/api/access/catalog')).json?.permissions ?? []
  const rows = await sql('select Role, Key, Allowed from RolePermissions')
  const dup = await sql('select Role, Key, count(*) n from RolePermissions group by Role, Key having n > 1')
  const keys = new Set(cat.map((p) => p.key))
  const strays = rows.filter((r) => !keys.has(r.Key) || !ROLES.includes(r.Role))
  return ok(cat.length > 0 && rows.length === ROLES.length * cat.length && dup.length === 0 && strays.length === 0,
    `${rows.length} rows = ${ROLES.length} roles × ${cat.length} keys · duplicates ${dup.length} · strays ${strays.length}`)
})

check('G-P1.3', 'P1', "seeded values equal today's baselines and capabilities", async () => {
  const rows = await sql('select Role, Key, Allowed from RolePermissions')
  const v = (role, key) => rows.find((r) => r.Role === role && r.Key === key)?.Allowed === 1
  const bad = []
  for (const role of ROLES) {
    const tabs = rows.filter((r) => r.Role === role && r.Key.startsWith('tab:') && r.Allowed === 1).map((r) => r.Key.slice(4))
    if (!sameSet(tabs, BASELINE[role])) bad.push(`${role} tabs ${show(sorted(tabs))}`)
    for (const [key, holders] of Object.entries(ACTIONS)) if (v(role, key) !== holders.includes(role)) bad.push(`${role} ${key}=${v(role, key)}`)
  }
  // DevOps sees only Procedures in HR today (HrTab.vue, P-F5); everyone else every section.
  for (const s of ['ask', 'library', 'rulebook']) if (v('DevOps', `section:hr.${s}`)) bad.push(`DevOps section:hr.${s}`)
  if (!v('DevOps', 'section:hr.procedures') || !v('HrAssistant', 'section:hr.ask')) bad.push('hr sections')
  return ok(bad.length === 0, bad.length ? bad.join(' | ') : `${ROLES.length} roles match tabs, sections and ${Object.keys(ACTIONS).length} actions`)
})

check('G-P1.3', 'P1', 'grants are stored as allow overrides, and a restart neither duplicates nor overwrites', async () => {
  const u = await users()
  const ov = (await sql('select Key, Effect from UserPermissionOverrides where UserId = ?', u.genHr.id)).map((r) => `${r.Key}:${r.Effect}`)
  const edit = await putRole('General', { 'tab:jsonto': true }, 'G-P1.3 restart check')
  const before = (await sql('select count(*) n from RolePermissions'))[0].n
  await restart()
  const after = (await sql('select count(*) n from RolePermissions'))[0].n
  const kept = (await sql("select Allowed from RolePermissions where Role = 'General' and Key = 'tab:jsonto'"))[0]?.Allowed
  const back = await putRole('General', { 'tab:jsonto': false }, 'G-P1.3 revert')
  return ok(sameSet(ov, ['tab:hr:allow', 'tab:logs:allow']) && edit.status === 200 && before === after && kept === 1 && back.status === 200,
    `overrides ${show(ov)} · edit ${edit.status} · rows ${before} → restart → ${after} · edited row kept ${kept === 1} · revert ${back.status}`)
})

// ---- G-P1.4 Role edit applies ----

check('G-P1.4', 'P1', 'General + tab:chat: both Generals pass the tab gate on their next request, ModelPolicy still applies; removed: 403', async () => {
  const u = await users()
  const chat = async (user, model) => {
    const res = await fetch(`${ctx.api.url}/api/chat`, {
      method: 'POST', headers: { Authorization: `Bearer ${user.token}`, 'Content-Type': 'application/json' },
      body: JSON.stringify({ model, source: 'local', message: 'hello' }),
    })
    return { status: res.status, text: await res.text() }
  }
  // Prime both caches first, so the change has something to clear.
  const primed = [await chat(u.gen1, 'gemma4:12b'), await chat(u.gen2, 'gemma4:12b')].map((r) => r.status)
  const rev0 = (await as(u.gen1, 'GET', '/api/me/tabs')).headers.get('x-access-rev')
  const on = await putRole('General', { 'tab:chat': true }, 'G-P1.4')
  const rev1 = (await as(u.gen1, 'GET', '/api/me/tabs')).headers.get('x-access-rev')
  const allowed = [await chat(u.gen1, 'gemma4:12b'), await chat(u.gen2, 'gemma4:12b')]
  const policy = await chat(u.gen1, 'fake-ok')
  const tabsOn = [await myTabs(u.gen1), await myTabs(u.gen2)].map((t) => t.includes('chat'))
  const off = await putRole('General', { 'tab:chat': false }, 'G-P1.4 revert')
  const refused = [await chat(u.gen1, 'gemma4:12b'), await chat(u.gen2, 'gemma4:12b')]
  const tabsOff = [await myTabs(u.gen1), await myTabs(u.gen2)].map((t) => t.includes('chat'))
  return ok(primed.every((s) => s === 403) && on.status === 200 && on.json?.affectedUsers >= 3
      && allowed.every((r) => r.status === 200)
      && policy.status === 403 && /can use/i.test(policy.text) && !/tab/i.test(policy.text)
      && tabsOn.every(Boolean) && off.status === 200 && refused.every((r) => r.status === 403 && /tab/i.test(r.text))
      && tabsOff.every((t) => !t) && rev0 && rev1 && rev0 !== rev1,
    `before ${show(primed)} · save ${on.status} (${on.json?.affectedUsers} users) · after ${show(allowed.map((r) => r.status))} · `
    + `fake-ok ${policy.status} "${policy.text.slice(0, 80)}" · me/tabs chat ${show(tabsOn)} · removed ${off.status} → ${show(refused.map((r) => r.status))}, me/tabs chat ${show(tabsOff)} · X-Access-Rev ${rev0} → ${rev1}`)
})

// ---- G-P1.5 Invariants ----

check('G-P1.5', 'P1', 'SystemAdmin without a tab, Settings off for any role: 400 naming the invariant, nothing saved', async () => {
  const snap = async () => JSON.stringify(await sql('select Role, Key, Allowed from RolePermissions order by Role, Key'))
  const logs = async () => (await sql('select count(*) n from AccessChangeLog'))[0].n
  const s0 = await snap(), l0 = await logs()
  const a = await putRole('SystemAdmin', { 'tab:chat': false })
  const results = [['SystemAdmin −chat', a, 'system-admin-has-everything']]
  for (const role of ROLES.filter((r) => r !== 'SystemAdmin'))
    results.push([`${role} −settings`, await putRole(role, { 'tab:settings': false }), 'settings-always-on'])
  // A valid change in the same body is not saved either: the save is whole or nothing.
  results.push(['General +chat −settings', await putRole('General', { 'tab:chat': true, 'tab:settings': false }), 'settings-always-on'])
  const s1 = await snap(), l1 = await logs()
  const bad = results.filter(([, r, inv]) => r.status !== 400 || r.json?.invariant !== inv || !r.json?.error?.includes(inv))
  return ok(bad.length === 0 && s0 === s1 && l0 === l1,
    `${results.map(([n, r]) => `${n} ${r.status} ${r.json?.invariant ?? ''}`).join(' · ')} · rows unchanged ${s0 === s1} · log unchanged ${l0 === l1}`)
})

check('G-P1.5', 'P1', 'demoting the only SystemAdmin: 400 naming the invariant, role unchanged', async () => {
  const u = await users()
  const admins = await sql("select Id from Users where lower(Role) = 'systemadmin'")
  // A System user who may manage access (Q2's default), so the refusal is the invariant's, not the caller's.
  const grant = (v) => [["update RolePermissions set Allowed = ? where Role = 'System' and Key in ('tab:rbac', 'action:access.manage')", v]]
  await sqlWhileStopped(grant(1))
  const r = await as(u.sys, 'PUT', `/api/admin/rbac/users/${admins[0]?.Id}/role`, { role: 'General' })
  const role = (await sql('select Role from Users where Id = ?', admins[0]?.Id))[0]?.Role
  await sqlWhileStopped(grant(0))
  return ok(admins.length === 1 && r.status === 400 && r.json?.invariant === 'last-system-admin' && role === 'SystemAdmin',
    `${admins.length} SystemAdmin · ${r.status} ${r.json?.invariant} "${r.json?.error}" · role now ${role}`)
})

// ---- the API's shape and its gate ----

check('API.1', 'P1', 'the catalog: every tab, section and action with English, Bengali, group and a real parent', async () => {
  const r = await asAdmin('GET', '/api/access/catalog')
  const perms = r.json?.permissions ?? []
  const keys = new Set(perms.map((p) => p.key))
  const tabs = perms.filter((p) => p.kind === 'tab').map((p) => p.key.slice(4))
  const bad = perms.filter((p) => !p.en || !p.bn || !['Work', 'Tools', 'System'].includes(p.group)
    || (p.kind === 'tab' ? p.parent : !keys.has(p.parent)))
  const sections = ['ask', 'library', 'rulebook', 'procedures'].map((s) => `section:hr.${s}`)
  return ok(r.status === 200 && sameSet(tabs, BASELINE.SystemAdmin) && bad.length === 0
      && sections.every((s) => keys.has(s)) && Object.keys(ACTIONS).every((a) => keys.has(a))
      && sameSet(r.json?.groups ?? [], ['Work', 'Tools', 'System']),
    `${r.status} · ${perms.length} keys (${tabs.length} tabs) · incomplete ${show(bad.map((p) => p.key))}`)
})

check('API.2', 'P1', '/api/me/access: tabs equal /api/me/tabs, with sections, actions, modules and the model policy', async () => {
  const u = await users()
  const out = []
  for (const user of [u.gen1, u.hra, u.dev, ctx.admin]) {
    const a = (await as(user, 'GET', '/api/me/access')).json
    const t = await myTabs(user)
    out.push(a && sameSet(a.tabs, t) && Array.isArray(a.sections) && Array.isArray(a.actions)
      && sameSet(a.modules?.map((m) => m.key) ?? [], t) && a.modules.every((m) => m.label && m.group)
      && a.models && Array.isArray(a.models.allowed))
  }
  const dev = (await as(u.dev, 'GET', '/api/me/access')).json
  return ok(out.every(Boolean) && dev?.sections?.join() === 'section:hr.procedures' && dev.actions.includes('action:hr.procedures.upload'),
    `${show(out)} · DevOps sections ${show(dev?.sections)} actions ${show(dev?.actions)}`)
})

check('API.3', 'P1', 'only access managers reach /api/access/*; P1 saves tab keys only; unknown role or key is 400', async () => {
  const u = await users()
  const st = [
    (await as(u.sys, 'GET', '/api/access/catalog')).status,
    (await as(u.sys, 'GET', '/api/access/roles')).status,
    (await putRole('General', { 'tab:chat': true }, null, u.sys)).status,
    (await call(ctx.api.url, 'GET', '/api/access/roles')).status,
  ]
  const section = await putRole('General', { 'section:hr.ask': false })
  const unknownKey = await putRole('General', { 'tab:nope': true })
  const unknownRole = await putRole('Wizard', { 'tab:chat': true })
  const roles = (await asAdmin('GET', '/api/access/roles')).json?.roles ?? []
  const sa = roles.find((r) => r.role === 'SystemAdmin')
  return ok(sameSet(st.map(String), ['403', '403', '403', '401']) && section.status === 400 && unknownKey.status === 400
      && unknownRole.status === 400 && sameSet(roles.map((r) => r.role), ROLES) && sa?.locked === true
      && roles.every((r) => typeof r.userCount === 'number' && r.values?.['tab:settings'] === true),
    `System ${show(st)} · section key ${section.status} · unknown key ${unknownKey.status} · unknown role ${unknownRole.status} · roles ${show(roles.map((r) => `${r.role}${r.locked ? '🔒' : ''}`))}`)
})

check('API.4', 'P1', 'every saved change is in AccessChangeLog with before → after; the log is append-only', async () => {
  const before = (await sql('select max(Id) m from AccessChangeLog'))[0]?.m ?? 0
  const r = await putRole('System', { 'tab:jsonto': true, 'tab:chat': true }, 'API.4 note')   // chat is already on: not a change
  const rows = await sql('select Scope, Target, Key, Before, After, Note, ByUserId from AccessChangeLog where Id > ?', before)
  await putRole('System', { 'tab:jsonto': false }, 'API.4 revert')
  const { DatabaseSync } = await import('node:sqlite')
  const db = new DatabaseSync(ctx.api.dbPath)
  const tryRun = (q) => { try { db.prepare(q).run(); return 'ran' } catch (e) { return /append-only/.test(e.message) ? 'refused' : e.message } }
  const upd = tryRun("update AccessChangeLog set After = 'x'")
  const del = tryRun('delete from AccessChangeLog')
  db.close()
  const row = rows[0]
  return ok(r.status === 200 && rows.length === 1 && row.Scope === 'role' && row.Target === 'System' && row.Key === 'tab:jsonto'
      && row.Before === 'false' && row.After === 'true' && row.Note === 'API.4 note' && row.ByUserId === ctx.admin.id
      && upd === 'refused' && del === 'refused',
    `${r.status} · ${rows.length} row(s) ${show(rows[0])} · UPDATE ${upd} · DELETE ${del}`)
})

check('FE.1', 'P1', 'the frontend keeps no list of roles, tabs or permissions', async () => {
  const app = read('frontend/src/App.vue')
  const matrix = read('frontend/src/components/rbac/RolePresetMatrix.vue')
  const roleLiteral = /['"](General|HrAssistant|DevOps|GroundControl|SystemAdmin)['"]/
  const issues = []
  if (/ALL_TABS\s*=\s*\[/.test(app) || /label:\s*'Dashboard'/.test(app)) issues.push('App.vue still lists tabs with labels')
  if (/v-if="isSystemAdmin"/.test(app)) issues.push('App.vue gates Access on the role, not on the tab')
  if (roleLiteral.test(matrix)) issues.push('RolePresetMatrix.vue names a role')
  if (/'tab:(chat|settings|rbac)'/.test(matrix)) issues.push('RolePresetMatrix.vue names a tab key')
  if (!/\/access\/catalog/.test(read('frontend/src/services/rbac.service.js'))) issues.push('no catalog call')
  return ok(issues.length === 0, issues.length ? issues.join(' | ') : 'App.vue icons only; matrix reads catalog and roles from the API')
})

// ---- G-P1.6 The UI, in a real browser ----

check('G-P1.6', 'P1', 'Roles matrix saves and reloads; an affected user’s module launcher changes on their next call (screenshots)', async () => {
  const shots = ctx.args.shots
  fs.mkdirSync(shots, { recursive: true })
  const u = await users()
  const browser = await launchBrowser({ base: ctx.api.url, profileRoot: EVAL_ROOT })
  const out = []
  try {
    // Admin: the Roles matrix, light and dark, desktop and 360 px.
    const admin = await browser.page()
    await admin.session(ctx.admin, 'light', 1280, 900, '/access')
    await admin.click('[data-access-view="roles"]')
    await admin.waitFor('[data-role-matrix]')
    await admin.shot(path.join(shots, 'roles-light-1280.png'))
    // Untick nothing, tick General → Chat: the unsaved bar counts it.
    await admin.click('[data-cell="General|tab:chat"]')
    const count = await admin.eval('document.querySelector("[data-unsaved-count]")?.textContent?.trim()')
    await admin.shot(path.join(shots, 'roles-unsaved-light-1280.png'))

    // The affected user, signed in BEFORE the save: no Chat in their launcher.
    const gen = await browser.page()
    await gen.session(u.gen1, 'light', 1280, 900, '/dashboard')
    const before = await gen.launcherIds(path.join(shots, 'general-launcher-before-light-1280.png'))

    await admin.type('[data-save-note]', 'G-P1.6 screenshot run')
    await admin.click('[data-save-roles]')
    try { await admin.waitFor('[data-saved-toast]') } catch (e) {
      await admin.shot(path.join(shots, 'failure-save.png'))
      throw new Error(`${e.message}; save bar says "${await admin.eval('document.querySelector("[data-role-matrix-root]")?.innerText?.slice(-300).replace(/\s+/g, " ")')}" · path ${await admin.eval('location.pathname')}`)
    }
    const toast = await admin.eval('document.querySelector("[data-saved-toast]")?.textContent?.trim()')
    await admin.shot(path.join(shots, 'roles-saved-light-1280.png'))
    await admin.reload()
    await admin.click('[data-access-view="roles"]')
    await admin.waitFor('[data-role-matrix]')
    const reloaded = await admin.eval('document.querySelector(\'[data-cell="General|tab:chat"]\')?.getAttribute("data-state")')

    // Their next call: the shell's own request when the window regains focus. Its
    // response carries the new X-Access-Rev, and the launcher is redrawn from /api/me/access.
    await sleep(1200)
    await gen.eval('window.dispatchEvent(new Event("focus")); 0')
    await sleep(800)
    const after = await gen.launcherIds(path.join(shots, 'general-launcher-after-light-1280.png'))

    await admin.setTheme('dark')
    await admin.shot(path.join(shots, 'roles-dark-1280.png'))
    await admin.viewport(360, 780)
    await admin.waitFor('[data-role-picker]')
    await admin.shot(path.join(shots, 'roles-dark-360.png'))
    await admin.setTheme('light')
    await admin.shot(path.join(shots, 'roles-light-360.png'))
    const overflow = await admin.eval('document.documentElement.scrollWidth > window.innerWidth')

    // Put General back.
    await putRole('General', { 'tab:chat': false }, 'G-P1.6 revert')
    out.push(`unsaved "${count}" · toast "${toast}" · reloaded General|chat=${reloaded} · launcher before has chat ${before.includes('chat')}, after ${after.includes('chat')} · 360px h-overflow ${overflow}`)
    return ok(/1/.test(count ?? '') && /affected|user/i.test(toast ?? '') && reloaded === 'on'
        && !before.includes('chat') && after.includes('chat') && overflow === false,
      `${out.join(' ')} · ${fs.readdirSync(shots).filter((f) => f.endsWith('.png')).length} screenshots in ${shots}`)
  } finally {
    await browser.close()
  }
})

// ─────────────────────────────── runner ───────────────────────────────

function parseArgs(argv) {
  const a = {
    phase: 'P1', only: null, build: true, baseline: null,
    devDb: path.join(REPO, 'backend', 'bin', 'Debug', 'net8.0', 'Data', 'rabbitic.db'),
    shots: path.join(EVAL_ROOT, 'shots'),
  }
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === '--phase') a.phase = argv[++i]
    else if (argv[i] === '--only') a.only = argv[++i]
    else if (argv[i] === '--no-build') a.build = false
    else if (argv[i] === '--baseline') a.baseline = argv[++i]
    else if (argv[i] === '--dev-db') a.devDb = argv[++i]
    else if (argv[i] === '--shots') a.shots = path.resolve(argv[++i])
  }
  return a
}

/** The SPA the eval API serves, as in the Docker image, so G-P1.6 drives the real shell. */
function syncWebRoot() {
  const dist = path.join(REPO, 'frontend', 'dist')
  const target = path.join(API_BIN_DIR, 'wwwroot')
  fs.rmSync(target, { recursive: true, force: true })
  if (fs.existsSync(dist)) fs.cpSync(dist, target, { recursive: true })
}

async function main() {
  const args = ctx.args = parseArgs(process.argv.slice(2))
  if (!process.env.RBAC_EVAL_ADMIN_USER || !process.env.RBAC_EVAL_ADMIN_SECRET) {
    console.error('Set RBAC_EVAL_ADMIN_USER and RBAC_EVAL_ADMIN_SECRET to the seeded administrator.')
    process.exit(2)
  }
  const order = ['P1', 'P2', 'P3']
  const selected = checks.filter((c) => order.indexOf(c.phase) <= order.indexOf(args.phase)
    && (!args.only || c.id === args.only || c.id.startsWith(args.only + '.')))
  if (!selected.length) { console.error('No checks match.'); process.exit(2) }

  if (args.build) buildApi()
  syncWebRoot()
  fs.mkdirSync(EVAL_ROOT, { recursive: true })
  const runDir = fs.mkdtempSync(path.join(EVAL_ROOT, 'run-'))

  ctx.fake = await startFakeOllama()
  let failed = 0
  try {
    ctx.api = await startApi({ fakeUrl: ctx.fake.url, runDir })
    ctx.admin = await login(process.env.RBAC_EVAL_ADMIN_USER, process.env.RBAC_EVAL_ADMIN_SECRET)
    console.log(`fake ${ctx.fake.url} · api ${ctx.api.url} · db ${ctx.api.dbPath}\n`)
    console.log('id | check | result | detail')
    for (const c of selected) {
      let res
      try { res = await c.fn() } catch (e) { res = { pass: false, detail: `threw: ${e.message}` } }
      if (!res.pass) failed++
      console.log(`${c.id} | ${c.name} | ${res.pass ? 'PASS' : 'FAIL'} | ${res.detail ?? ''}`)
    }
    console.log(`\n${selected.length - failed}/${selected.length} passed`)
  } finally {
    await ctx.api?.stop()
    await ctx.fake.stop()
  }
  process.exit(failed ? 1 : 0)
}

main().catch((e) => { console.error(e.message ?? e); process.exit(2) })
