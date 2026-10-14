#!/usr/bin/env node
// মানব সম্পদ (HR Assistant) eval — see docs/HR_MODULE_PROMPT.md §8.
//
//   node scripts/hr-eval.mjs --phase 1            every Phase 1 check, fake Ollama
//   node scripts/hr-eval.mjs --phase 1 --live     G1.6 against the real Ollama (nomic-embed-text)
//   node scripts/hr-eval.mjs --only G1.5          one gate
//   node scripts/hr-eval.mjs --no-build           reuse the last eval build
//   node scripts/hr-eval.mjs --no-suites          skip re-running the other suites in G1.9
//
// Environment (never written in this file):
//   HR_EVAL_ADMIN_USER, HR_EVAL_ADMIN_SECRET   the seeded SystemAdmin of the throwaway API
//   HR_EVAL_OLLAMA_URL                         --live only; default http://localhost:11434
//
// The fake Ollama and the API start-up are REUSED from scripts/oddity-eval.mjs
// (rule 6: no second fake). Its embeddings are a bag of hashed words, enough to
// make retrieval depend on the words a question shares with a chunk.
//
// The API is seeded from the real docs/hr/ library, so the SQL-splitting checks
// (G1.5) run on the real ProcessFinalSettelment.sql and its real line numbers.
//
// A check asserts what the client got and what landed in SQLite — never what
// the server says it did. Node 18+, no dependencies.

import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { spawnSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'

import { buildApi, startFakeOllama, startApi, call } from './oddity-eval.mjs'

const REPO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const EVAL_ROOT = path.join(os.tmpdir(), 'hr-eval')
const LIBRARY = path.join(REPO, 'docs', 'hr')
const FIXTURES = path.join(REPO, 'scripts', 'hr', 'fixtures')

// ─────────────────────── expected values, written by hand ───────────────────────
// They mirror the design contract (§3), not the code.

const HR_LABEL = 'মানব সম্পদ'
const HR_ASSISTANT = 'hr-assistant'
const HR_ASSISTANT_TABS = ['hr', 'notes', 'settings']
const HR_ASSISTANT_MODEL = 'gemma4:12b'
const MAX_CHUNK_CHARS = 1500
const SECRET = 'eval-hr-assistant-secret'   // the throwaway API's only; never a real one

/** G1.5: the line HR knows by number, and the formula that must never reach the model. */
const SERVICE_BENEFIT_LINE = 686
const SERVICE_BENEFIT_TEXT = 'IF @LengthYear BETWEEN 3 AND 9 AND @EmpStatusID NOT IN (8)'
const AGER_FORMULA = /\*\s*14\b/
const WORKED_CODES = "'P','LT','LTP','SD','TL','EE','NOP','EXL'"

// ─────────────────────────── harness ───────────────────────────

const checks = []
/** mode: 'fake' runs without --live, 'live' only with it, 'any' in both. */
function check(id, phase, mode, name, fn) { checks.push({ id, phase, mode, name, fn }) }
const ok = (pass, detail) => ({ pass: !!pass, detail })
const read = (rel) => fs.readFileSync(path.join(REPO, rel), 'utf8')
const sameSet = (a, b) => a.length === b.length && [...a].sort().join() === [...b].sort().join()
const squash = (s) => String(s ?? '').replace(/\s+/g, ' ').trim()
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

const ctx = {}

async function login(username, secret, api = ctx.api) {
  const r = await call(api.url, 'POST', '/api/auth/login', { body: { username, secret } })
  if (r.status !== 200 || !r.json?.token) throw new Error(`login ${username} → ${r.status} ${r.text.slice(0, 200)}`)
  const me = await call(api.url, 'GET', '/api/me/tabs', { token: r.json.token })
  return { username, secret, token: r.json.token, id: me.json?.id, role: me.json?.role, tabs: me.json?.tabs ?? [] }
}
const as = (user, method, p, body) => call(ctx.api.url, method, p, { token: user.token, body })
const asAdmin = (method, p, body) => as(ctx.admin, method, p, body)

async function sqlite(query, dbPath = ctx.api.dbPath) {
  const { DatabaseSync } = await import('node:sqlite')
  const db = new DatabaseSync(dbPath, { readOnly: true })
  try { return db.prepare(query).all() } finally { db.close() }
}

/** A General user, created by logging in, with optional extra tabs. */
async function generalUser(name, tabs) {
  const u = await login(`${name}-${Date.now()}`, `eval-${name}`)
  if (tabs) await asAdmin('PUT', `/api/admin/rbac/users/${u.id}/tabs`, { tabKeys: tabs })
  return login(u.username, u.secret)
}

async function library(user = ctx.admin) {
  const r = await as(user, 'GET', '/api/hr/library')
  if (r.status !== 200) throw new Error(`/api/hr/library → ${r.status} ${r.text.slice(0, 160)}`)
  return r.json?.items ?? []
}

/** Waits until every `search` item is split and embedded (embedding runs after start-up). */
async function embedded(timeoutMs = 120_000) {
  const until = Date.now() + timeoutMs
  let items = []
  while (Date.now() < until) {
    items = await library()
    const search = items.filter((i) => i.mode === 'search')
    if (search.length && search.every((i) => i.chunkCount > 0 && i.embeddedCount === i.chunkCount)) return items
    await sleep(500)
  }
  throw new Error(`library not embedded after ${timeoutMs} ms: ${items.map((i) => `${i.name} ${i.embeddedCount}/${i.chunkCount}`).join(', ')}`)
}

async function chunksOf(name) {
  const item = (await library()).find((i) => i.name === name)
  if (!item) throw new Error(`no library item named ${name}`)
  const r = await asAdmin('GET', `/api/hr/library/${item.id}/chunks`)
  return { item, chunks: r.json?.chunks ?? [] }
}

async function restartApi() {
  await ctx.api.stop()
  ctx.api = await startApi({ fakeUrl: ctx.fakeUrl, runDir: ctx.runDir, env: ctx.env })
}

/** Library file → the item the seed should make of it (§3.2). */
function expectedSeed() {
  const out = []
  for (const f of fs.readdirSync(LIBRARY).filter((f) => f.toLowerCase().endsWith('.md')))
    // Phase 2 contract: rules and codes default to always, notes to search.
    out.push({ name: f.replace(/\.md$/i, ''), hrType: /rules/i.test(f) ? 'rules' : 'note', mode: /rules/i.test(f) ? 'always' : 'search' })
  for (const f of fs.readdirSync(path.join(LIBRARY, 'procedures')).filter((f) => f.toLowerCase().endsWith('.sql')))
    out.push({ name: f, hrType: 'procedure', mode: 'search' })
  return out
}

// ─────────────────────────── G1.1 — user type ───────────────────────────

check('G1.1', 1, 'fake', 'with HR_ASSISTANT_SECRET: hr-assistant exists as HrAssistant with exactly hr, notes, settings', async () => {
  const rows = await sqlite(`SELECT Id, Role FROM Users WHERE Username = '${HR_ASSISTANT}'`)
  const u = await login(HR_ASSISTANT, SECRET)
  return ok(rows.length === 1 && rows[0].Role === 'HrAssistant' && u.role === 'HrAssistant' && sameSet(u.tabs, HR_ASSISTANT_TABS),
    `${rows.length} row(s), stored role ${rows[0]?.Role}; /api/me/tabs → role ${u.role}, tabs [${u.tabs}]`)
})

check('G1.1', 1, 'fake', `its model-policy row allows only ${HR_ASSISTANT_MODEL}`, async () => {
  const rows = await sqlite(`SELECT AllowedModels, DefaultModel FROM RoleModelPolicies WHERE Role = 'HrAssistant'`)
  return ok(rows.length === 1 && rows[0].AllowedModels === HR_ASSISTANT_MODEL && rows[0].DefaultModel === HR_ASSISTANT_MODEL,
    `RoleModelPolicies HrAssistant → ${JSON.stringify(rows[0] ?? null)}`)
})

check('G1.1', 1, 'fake', 'without the variable the step is skipped with one log line, and no account is made', async () => {
  const runDir = fs.mkdtempSync(path.join(EVAL_ROOT, 'nosecret-'))
  const api = await startApi({ fakeUrl: ctx.fakeUrl, runDir, env: { ...ctx.env, HR_ASSISTANT_SECRET: '' } })
  try {
    const rows = await sqlite(`SELECT COUNT(*) AS n FROM Users WHERE Username = '${HR_ASSISTANT}'`, api.dbPath)
    const lines = fs.readFileSync(api.logPath, 'utf8').split('\n').filter((l) => l.includes('HR_ASSISTANT_SECRET'))
    return ok(rows[0].n === 0 && lines.length === 1, `${rows[0].n} hr-assistant row(s); log: ${lines.map((l) => `"${l.trim()}"`).join(' | ')}`)
  } finally { await api.stop() }
})

check('G1.1', 1, 'fake', 'a database from the earlier model-policy build (UpdatedByUserId NOT NULL) starts, gains its HrAssistant row, keeps its edits', async () => {
  // Reported on KAKAROT's own database: RoleModelPolicies created by the local
  // model-policy code (3a08328) declares UpdatedByUserId NOT NULL, so seeding
  // the new role's row (UpdatedByUserId NULL) failed and the API did not start.
  const runDir = fs.mkdtempSync(path.join(EVAL_ROOT, 'legacy-policy-'))
  let api = await startApi({ fakeUrl: ctx.fakeUrl, runDir, env: ctx.env })
  await api.stop()

  const { DatabaseSync } = await import('node:sqlite')
  const db = new DatabaseSync(api.dbPath)
  db.exec(`
    CREATE TABLE "RoleModelPolicies_legacy" (
      "Role" TEXT NOT NULL CONSTRAINT "PK_RoleModelPolicies" PRIMARY KEY,
      "AllowedModels" TEXT NOT NULL, "DefaultModel" TEXT NOT NULL,
      "UpdatedAtUtc" TEXT NOT NULL, "UpdatedByUserId" INTEGER NOT NULL);
    INSERT INTO "RoleModelPolicies_legacy" SELECT "Role", "AllowedModels", "DefaultModel", "UpdatedAtUtc", COALESCE("UpdatedByUserId", 0)
      FROM "RoleModelPolicies" WHERE "Role" <> 'HrAssistant';
    UPDATE "RoleModelPolicies_legacy" SET "AllowedModels" = 'qwen3.8-27b-q3', "DefaultModel" = 'qwen3.8-27b-q3', "UpdatedByUserId" = 3
      WHERE "Role" = 'System';
    DROP TABLE "RoleModelPolicies";
    ALTER TABLE "RoleModelPolicies_legacy" RENAME TO "RoleModelPolicies";`)
  db.close()

  let started = true, error = ''
  try { api = await startApi({ fakeUrl: ctx.fakeUrl, runDir, env: ctx.env }) } catch (e) { started = false; error = e.message.split('\n')[0] }
  try {
    const rows = started ? await sqlite('SELECT Role, AllowedModels, UpdatedByUserId FROM RoleModelPolicies ORDER BY Role', api.dbPath) : []
    const nullable = started ? (await sqlite(`PRAGMA table_info("RoleModelPolicies")`, api.dbPath)).find((c) => c.name === 'UpdatedByUserId')?.notnull === 0 : false
    const hr = rows.find((r) => r.Role === 'HrAssistant'), sys = rows.find((r) => r.Role === 'System')
    const failed = started && /Failed executing DbCommand/.test(fs.readFileSync(api.logPath, 'utf8'))
    return ok(started && !failed && nullable && hr?.AllowedModels === HR_ASSISTANT_MODEL && sys?.AllowedModels === 'qwen3.8-27b-q3' && sys?.UpdatedByUserId === 3,
      `started ${started}${error ? ` (${error})` : ''}; failed command in log ${failed}; UpdatedByUserId nullable ${nullable}; `
      + `HrAssistant → ${hr?.AllowedModels}; System's edit kept → ${sys?.AllowedModels} by ${sys?.UpdatedByUserId}; ${rows.length} rows`)
  } finally { if (started) await api.stop() }
})

check('G1.1', 1, 'fake', 'restarting does not create a second hr-assistant', async () => {
  await restartApi()
  const rows = await sqlite(`SELECT Role FROM Users WHERE Username = '${HR_ASSISTANT}'`)
  return ok(rows.length === 1 && rows[0].Role === 'HrAssistant',
    `${rows.length} hr-assistant row(s) after a restart, role ${rows[0]?.Role}`)
})

// ─────────────────────────── G1.2 — the tab ───────────────────────────

check('G1.2', 1, 'fake', 'SystemAdmin has hr; General and System without a grant get 403; a grant opens it', async () => {
  const admin = (await call(ctx.api.url, 'GET', '/api/me/tabs', { token: ctx.admin.token })).json?.tabs ?? []
  const gen = await generalUser('g12')
  const sys = await login(`s12-${Date.now()}`, 'eval-s12')
  await asAdmin('PUT', `/api/admin/rbac/users/${sys.id}/role`, { role: 'System' })
  const a = (await as(gen, 'GET', '/api/hr/library')).status
  const b = (await as(sys, 'GET', '/api/hr/library')).status
  await asAdmin('PUT', `/api/admin/rbac/users/${gen.id}/tabs`, { tabKeys: ['hr'] })
  const c = (await as(gen, 'GET', '/api/hr/library')).status
  return ok(admin.includes('hr') && a === 403 && b === 403 && c === 200,
    `admin has hr: ${admin.includes('hr')}; General ${a}, System ${b}; General with the hr grant ${c}`)
})

check('G1.2', 1, 'any', `the label is "${HR_LABEL}" in the Access screen, tab bar, command palette and docs page`, async () => {
  const tabs = await asAdmin('GET', '/api/admin/rbac/tabs')
  const access = (tabs.json ?? []).find?.((t) => t.key === 'hr')?.label
  const app = read('frontend/src/App.vue')
  // The tab bar and the command palette both render ALL_TABS (the palette maps the
  // permission-filtered `tabs` computed over it), so one entry labels both.
  const tabBar = new RegExp(`id: 'hr', label: '${HR_LABEL}'`).test(app)
  const palette = /const commands = computed\(\(\) => \[\s*\.\.\.tabs\.value\.map/.test(app)
  const route = /key: 'hr', path: '\/hr'/.test(read('frontend/src/app/router.js'))
  const docs = read('frontend/src/components/mostisko-docs/sections-data.js').includes(HR_LABEL)
  return ok(access === HR_LABEL && tabBar && palette && route && docs,
    `Access "${access}"; ALL_TABS entry ${tabBar}; palette built from tabs ${palette}; route /hr ${route}; docs page ${docs}`)
})

// ─────────────────────────── G1.3 — a shared library ───────────────────────────

check('G1.3', 1, 'fake', 'an item hr-assistant adds is readable, not editable, by an hr-granted user; SystemAdmin can edit it', async () => {
  const hra = await login(HR_ASSISTANT, SECRET)
  const reader = await generalUser('reader', ['hr'])
  const made = await as(hra, 'POST', '/api/hr/library',
    { name: 'eval-shared-note', hrType: 'note', content: 'Shared note written by hr-assistant.' })
  const id = made.json?.id
  const seen = (await library(reader)).find((i) => i.id === id)
  const get = await as(reader, 'GET', `/api/hr/library/${id}`)
  const put = await as(reader, 'PUT', `/api/hr/library/${id}`, { content: 'reader edit' })
  const del = await as(reader, 'DELETE', `/api/hr/library/${id}`)
  const adminPut = await asAdmin('PUT', `/api/hr/library/${id}`, { content: 'Edited by SystemAdmin.' })
  const after = await as(reader, 'GET', `/api/hr/library/${id}`)
  return ok(made.status === 201 && seen && seen.canEdit === false && get.status === 200 && put.status === 403 && del.status === 403
      && adminPut.status === 200 && after.json?.content === 'Edited by SystemAdmin.' && after.json?.version === 2,
    `hr-assistant add → ${made.status}; reader sees it ${!!seen} (canEdit ${seen?.canEdit}), GET ${get.status}, PUT ${put.status}, DELETE ${del.status}; `
    + `SystemAdmin PUT ${adminPut.status} → version ${after.json?.version}`)
})

check('G1.3', 1, 'fake', 'owner scoping is unchanged for other kinds, and HR items never show on the generic routes', async () => {
  const a = await generalUser('ownerA'), b = await generalUser('ownerB')
  const src = await as(a, 'POST', '/api/context-sources', { name: 'private', kind: 'source', content: 'A only', isActive: true })
  const bGet = (await as(b, 'GET', `/api/context-sources/${src.json?.id}`)).status
  const bList = ((await as(b, 'GET', '/api/context-sources')).json ?? []).some((s) => s.id === src.json?.id)
  const hrItem = (await library()).find((i) => i.name === 'eval-shared-note')
  const generic = await asAdmin('GET', `/api/context-sources/${hrItem?.id}`)
  const genericList = ((await asAdmin('GET', '/api/context-sources')).json ?? []).filter((s) => s.kind === 'hr').length
  const sneak = await as(a, 'POST', '/api/context-sources', { name: 'sneak', kind: 'hr', content: 'x' })
  return ok(src.status === 201 && bGet === 404 && !bList && generic.status === 404 && genericList === 0 && sneak.json?.kind === 'source',
    `A's source: B GET ${bGet}, in B's list ${bList}; HR item on /api/context-sources/{id} → ${generic.status}; hr items in the generic list ${genericList}; `
    + `kind "hr" posted generically lands as "${sneak.json?.kind}"`)
})

// ─────────────────────────── G1.4 — seed ───────────────────────────

check('G1.4', 1, 'fake', 'a fresh start loads every docs/hr file with the right type and mode', async () => {
  const items = await embedded()
  const want = expectedSeed()
  const bad = want.filter((w) => {
    const i = items.find((x) => x.name === w.name)
    return !i || i.hrType !== w.hrType || i.mode !== w.mode || i.version !== 1
  })
  // Two procedure files are UTF-16; their text must have come through readable.
  const utf16 = (await as(ctx.admin, 'GET', `/api/hr/library/${items.find((i) => i.name === 'GetDateDurationDay.sql')?.id}`)).json?.content ?? ''
  return ok(!bad.length && /FUNCTION/i.test(utf16) && !utf16.includes('\u0000'),
    `${want.length} files → ${items.filter((i) => want.some((w) => w.name === i.name)).map((i) => `${i.name} (${i.hrType}/${i.mode}, ${i.chunkCount} chunks)`).join(', ')}`
    + `${bad.length ? `; WRONG [${bad.map((b) => b.name)}]` : ''}; UTF-16 file read as text: ${/FUNCTION/i.test(utf16)}`)
})

check('G1.4', 1, 'fake', 'an edit made in the app survives a restart; the version does not reset and nothing is duplicated', async () => {
  const before = (await library()).find((i) => i.name === 'HR_SETTLEMENT_RULES')
  const original = (await asAdmin('GET', `/api/hr/library/${before.id}`)).json.content
  const edited = original + '\n\n<!-- eval edit -->\n'
  const put = await asAdmin('PUT', `/api/hr/library/${before.id}`, { content: edited })
  await restartApi()
  const all = (await library()).filter((i) => i.name === 'HR_SETTLEMENT_RULES')
  const after = (await asAdmin('GET', `/api/hr/library/${all[0]?.id}`)).json
  return ok(put.status === 200 && all.length === 1 && after?.content === edited && after?.version === before.version + 1,
    `PUT ${put.status}; after restart: ${all.length} item(s), version ${before.version} → ${after?.version}, edit kept ${after?.content === edited}`)
})

// ─────────────────────────── G1.5 — SQL splitting ───────────────────────────

check('G1.5', 1, 'fake', 'ProcessFinalSettelment.sql: no Ager Formula, line 686 in "Service Benefit", no chunk over 1,500 characters', async () => {
  const { chunks } = await chunksOf('ProcessFinalSettelment.sql')
  const ager = chunks.filter((c) => AGER_FORMULA.test(c.text) || /Ager Formula/i.test(c.text))
  const holder = chunks.find((c) => squash(c.text).includes(SERVICE_BENEFIT_TEXT))
  const tooLong = chunks.filter((c) => c.text.length > MAX_CHUNK_CHARS)
  const refShape = chunks.filter((c) => !/^ProcessFinalSettelment\.sql (§.+ )?L\d+-\d+$/.test(c.sourceRef))
  const pass = chunks.length > 0 && !ager.length && holder?.section === 'Service Benefit'
    && holder.lineFrom <= SERVICE_BENEFIT_LINE && SERVICE_BENEFIT_LINE <= holder.lineTo && !tooLong.length && !refShape.length
  return ok(pass, `${chunks.length} chunks; Ager/×14 in [${ager.map((c) => c.sourceRef)}]; line ${SERVICE_BENEFIT_LINE} → "${holder?.sourceRef}"; `
    + `longest ${Math.max(...chunks.map((c) => c.text.length))} chars; bad refs [${refShape.map((c) => c.sourceRef)}]`
    + `\n    first five: ${chunks.slice(0, 5).map((c) => c.sourceRef).join(' | ')}`)
})

check('G1.5', 1, 'fake', 'line ranges point at the original file; section comments and short notes are kept', async () => {
  const { chunks } = await chunksOf('ProcessFinalSettelment.sql')
  const lines = read('docs/hr/procedures/ProcessFinalSettelment.sql').split(/\r?\n/)
  // The first and last line of each chunk must be the file's own lines at that range.
  const off = chunks.filter((c) => {
    const t = c.text.split('\n').map((l) => l.trim()).filter(Boolean)
    return t[0] !== lines[c.lineFrom - 1]?.trim() || t[t.length - 1] !== lines[c.lineTo - 1]?.trim()
  })
  const all = chunks.map((c) => c.text).join('\n')
  const kept = ['--//Gratutity Calculation Start', '--NO Service Benefit for DEATH'].filter((s) => all.includes(s))
  const dropped = ['--select * from Common_EmploymentStatus', '/******'].filter((s) => all.includes(s))
  return ok(!off.length && kept.length === 2 && !dropped.length,
    `${chunks.length - off.length}/${chunks.length} ranges match the file${off.length ? ` (off: ${off.slice(0, 3).map((c) => c.sourceRef)})` : ''}; `
    + `kept [${kept}]; commented code still present [${dropped}]`)
})

// ─────────────────────────── G1.6 — retrieval ───────────────────────────

check('G1.5', 1, 'fake', 'a library split the old way (no SplitVersion) is re-split once on start, keyword and vector indexes included', async () => {
  const item = (await asAdmin('GET', '/api/hr/library')).json.items.find((i) => i.name === 'ProcessFinalSettelment.sql')
  const ids = async () => (await sqlite(`SELECT Id FROM EmbeddingChunks WHERE ContextSourceId = ${item.id} ORDER BY Id`)).map((r) => r.Id)
  const before = await ids()
  // As a database from before the change looks: chunks present, no SplitVersion.
  await sqliteWrite(`UPDATE ContextSources SET SplitVersion = NULL WHERE Id = ${item.id}`)
  await restartApi()
  const after = await ids()
  await restartApi()
  const third = await ids()
  await embedded()
  const version = (await sqlite(`SELECT SplitVersion FROM ContextSources WHERE Id = ${item.id}`))[0]?.SplitVersion
  const stats = (await asAdmin('GET', '/api/hr/library/index-stats')).json?.byItem?.[item.id] ?? {}
  const log = fs.readFileSync(ctx.api.logPath, 'utf8').split('\n').filter((l) => l.includes('re-split')).map((l) => l.trim())
  const replaced = after.length > 0 && after.every((id) => !before.includes(id))
  return ok(replaced && JSON.stringify(after) === JSON.stringify(third) && version === 2 && stats.index === after.length && stats.fts === after.length,
    `chunks ${before.length} → ${after.length}, all new ids ${replaced}; second restart leaves them ${JSON.stringify(after) === JSON.stringify(third)}; `
    + `SplitVersion ${version}; index ${stats.index}, db ${stats.db}, fts ${stats.fts}; log: ${log.at(-1) ?? 'none'}`)
})

async function retrievalCheck() {
  const q1 = await asAdmin('GET', `/api/hr/retrieve?q=${encodeURIComponent('How is service benefit calculated for 3 to 9 years?')}`)
  const q2 = await asAdmin('GET', `/api/hr/retrieve?q=${encodeURIComponent('Which attendance codes count for earned leave?')}`)
  // /api/hr/retrieve answers with exactly the excerpts the question would be sent with (at most 5).
  const top1 = q1.json?.hits ?? [], top2 = q2.json?.hits ?? []
  const a = top1.length <= 5 && top1.some((h) => h.section === 'Service Benefit')
  const b = top2.length <= 5 && top2.some((h) => h.text.includes(WORKED_CODES))
  const show = (hits) => hits.map((h) => `${h.sourceRef} (${h.score.toFixed(3)})`).join(' | ')
  return ok(a && b, `service benefit → ${a ? 'found' : 'MISSED'}: ${show(top1)}\n    attendance codes → ${b ? 'found' : 'MISSED'}: ${show(top2)}`)
}

check('G1.6', 1, 'fake', 'retrieval (fake embedding): both questions find their chunk among the top 5 excerpts sent', retrievalCheck)
check('G1.6', 1, 'live', 'retrieval (nomic-embed-text): both questions find their chunk among the top 5 excerpts sent', retrievalCheck)

check('G1.6', 1, 'fake', 'retrieval never returns a document chunk, and document search never returns an HR chunk', async () => {
  const hits = (await asAdmin('GET', `/api/hr/retrieve?q=${encodeURIComponent('purchase order style number quantity')}`)).json?.hits ?? []
  const notHr = hits.filter((h) => !h.itemId)
  const hrInDocs = await sqlite(`SELECT COUNT(*) AS n FROM EmbeddingChunks WHERE Kind = 'hr' AND (DocumentId IS NOT NULL OR ContextSourceId IS NULL)`)
  return ok(!notHr.length && hrInDocs[0].n === 0, `${hits.length} HR hits, ${notHr.length} without an HR item; HR chunks tied to a document: ${hrInDocs[0].n}`)
})

// ─────────────────────────── G1.7 — edits and deletes ───────────────────────────

const dbChunks = async (id) => (await sqlite(`SELECT Id FROM EmbeddingChunks WHERE ContextSourceId = ${id}`)).map((r) => r.Id)
const indexCount = async (id) => (await asAdmin('GET', '/api/hr/library/index-stats')).json?.byItem?.[id]?.index ?? 0

check('G1.7', 1, 'fake', 'editing a search item replaces its chunks and raises its version; deleting removes them; no orphans', async () => {
  const sql = fs.readFileSync(path.join(FIXTURES, 'ProcessFinalSettelment.trimmed.sql'), 'utf8')
  const made = await asAdmin('POST', '/api/hr/library', { name: 'eval-trimmed.sql', hrType: 'procedure', content: sql })
  const id = made.json?.id
  await embedded()
  const first = await dbChunks(id), firstIdx = await indexCount(id)

  const longer = sql + '\n\n\t--//Eval Section Start\n\tSET @X = 1\n\n\tSET @Y = 2\n\t--//Eval Section End\n'
  const put = await asAdmin('PUT', `/api/hr/library/${id}`, { content: longer })
  await embedded()
  const second = await dbChunks(id), secondIdx = await indexCount(id)
  const leftovers = second.filter((c) => first.includes(c))

  const del = await asAdmin('DELETE', `/api/hr/library/${id}`)
  const third = await dbChunks(id), thirdIdx = await indexCount(id)
  const orphans = await sqlite(`SELECT COUNT(*) AS n FROM EmbeddingChunks c WHERE c.Kind = 'hr'
    AND NOT EXISTS (SELECT 1 FROM ContextSources s WHERE s.Id = c.ContextSourceId)`)
  const stats = (await asAdmin('GET', '/api/hr/library/index-stats')).json

  return ok(made.status === 201 && put.json?.version === 2 && first.length > 0 && second.length === first.length + 1
      && firstIdx === first.length && secondIdx === second.length && !leftovers.length
      && del.status === 204 && third.length === 0 && thirdIdx === 0 && orphans[0].n === 0 && stats?.indexChunks === stats?.dbChunks,
    `chunks ${first.length} (index ${firstIdx}) → edit, version ${put.json?.version} → ${second.length} (index ${secondIdx}), old ids left ${leftovers.length}; `
    + `delete ${del.status} → ${third.length} (index ${thirdIdx}); orphans ${orphans[0].n}; HR index ${stats?.indexChunks} = db ${stats?.dbChunks}`)
})

check('G1.7', 1, 'fake', 'switching mode: search → always drops the chunks; always → search splits again; content version unchanged', async () => {
  const made = await asAdmin('POST', '/api/hr/library', { name: 'eval-mode.sql', hrType: 'procedure', content: fs.readFileSync(path.join(FIXTURES, 'ProcessFinalSettelment.trimmed.sql'), 'utf8') })
  const id = made.json?.id
  await embedded()
  const a = (await dbChunks(id)).length
  const toAlways = await asAdmin('PUT', `/api/hr/library/${id}`, { mode: 'always' })
  const b = (await dbChunks(id)).length
  const toSearch = await asAdmin('PUT', `/api/hr/library/${id}`, { mode: 'search' })
  await embedded()
  const c = (await dbChunks(id)).length
  await asAdmin('DELETE', `/api/hr/library/${id}`)
  return ok(a > 0 && toAlways.json?.mode === 'always' && b === 0 && toSearch.json?.mode === 'search' && c === a && toSearch.json?.version === 1,
    `search ${a} chunks → always ${b} → search ${c}; version ${toSearch.json?.version}`)
})

// ─────────────────────────── G1.8 — library UI and its routes ───────────────────────────

check('G1.8', 1, 'fake', 'add by paste and by .sql upload (UTF-16 included); defaults by type; approval label', async () => {
  const paste = await asAdmin('POST', '/api/hr/library',
    { name: 'eval-codes', hrType: 'codes', content: fs.readFileSync(path.join(FIXTURES, 'attendance-codes.csv'), 'utf8') })
  const form = new FormData()
  form.append('file', new Blob([fs.readFileSync(path.join(REPO, 'docs/hr/procedures/GetDateDurationMonth.sql'))]), 'eval-upload.sql')
  const up = await fetch(`${ctx.api.url}/api/hr/library/upload`, { method: 'POST', headers: { Authorization: `Bearer ${ctx.admin.token}` }, body: form })
  // Phase 2: the upload takes many files and answers per file.
  const upJson = (await up.json().catch(() => null))?.results?.[0]?.item ?? null
  const upItem = upJson?.id ? (await asAdmin('GET', `/api/hr/library/${upJson.id}`)).json : null
  const bad = new FormData()
  bad.append('file', new Blob(['MZ']), 'tool.exe')
  const refused = await fetch(`${ctx.api.url}/api/hr/library/upload`, { method: 'POST', headers: { Authorization: `Bearer ${ctx.admin.token}` }, body: bad })
  const approve = await asAdmin('PUT', `/api/hr/library/${paste.json?.id}`, { approvedBy: 'HR Manager' })
  for (const id of [paste.json?.id, upJson?.id]) if (id) await asAdmin('DELETE', `/api/hr/library/${id}`)
  return ok(paste.status === 201 && paste.json?.mode === 'always' && up.status === 201 && upJson?.hrType === 'procedure'
      && upJson?.mode === 'search' && /FUNCTION/i.test(upItem?.content ?? '') && refused.status === 400
      && approve.json?.approvedBy === 'HR Manager' && !!approve.json?.approvedAt,
    `paste codes → ${paste.status} ${paste.json?.mode}; upload UTF-16 .sql → ${up.status} ${upJson?.hrType}/${upJson?.mode}, readable ${/FUNCTION/i.test(upItem?.content ?? '')}; `
    + `.exe → ${refused.status}; approval → "${approve.json?.approvedBy}" at ${approve.json?.approvedAt}`)
})

check('G1.8', 1, 'any', 'the library UI exists with paste, upload, mode switch, chunk preview; edit controls only for editors; no hard-coded colours', async () => {
  const file = 'frontend/src/components/hr/HrLibrary.vue'
  if (!fs.existsSync(path.join(REPO, file))) return ok(false, `${file} does not exist`)
  const src = read(file)
  const has = {
    // The accept list may be written in or bound to the server's list; either way
    // the four extensions must be what the picker offers.
    paste: /paste|পেস্ট/i.test(src),
    upload: /type="file"/.test(src) && /:?accept=/.test(src) && ['.md', '.txt', '.sql', '.csv'].every((e) => src.includes(e)),
    mode: /mode/.test(src) && /always/.test(src) && /search/.test(src), preview: /chunks/.test(src), gate: /canEdit/.test(src),
  }
  const vue = ['frontend/src/components/hr/HrLibrary.vue', 'frontend/src/components/tabs/HrTab.vue']
    .filter((f) => fs.existsSync(path.join(REPO, f)))
  const colours = vue.flatMap((f) => read(f).split('\n').map((l, i) => [f, i + 1, l]))
    .filter(([, , l]) => /#[0-9a-fA-F]{3,8}\b|rgba?\(/.test(l)).map(([f, n]) => `${f}:${n}`)
  return ok(Object.values(has).every(Boolean) && !colours.length, `${JSON.stringify(has)}; hard-coded colours [${colours}]`)
})

// ─────────────────────────── G1.9 — build, other suites, no hr-assist ───────────────────────────

check('G1.9', 1, 'any', 'dotnet build is clean, zero warnings', async () => {
  // Its own output folder: a running copy of the app locks bin/, and a copy
  // retry is a build warning that says nothing about the code.
  const r = spawnSync('dotnet', ['build', path.join(REPO, 'backend', 'RabbitIC.Api.csproj'), '-v', 'q', '--nologo',
    '-o', path.join(EVAL_ROOT, 'build-check')], { cwd: REPO, encoding: 'utf8', shell: process.platform === 'win32' })
  const out = (r.stdout ?? '') + (r.stderr ?? '')
  const warnings = out.match(/(\d+) Warning\(s\)/)?.[1]
  return ok(r.status === 0 && warnings === '0', `exit ${r.status}, ${warnings} warning(s)${r.status ? `\n${out.slice(-800)}` : ''}`)
})

check('G1.9', 1, 'any', 'git diff main: no Microsoft.Data.SqlClient and nothing from hr-assist', async () => {
  const diff = spawnSync('git', ['diff', 'main', '--stat'], { cwd: REPO, encoding: 'utf8' }).stdout
  const names = spawnSync('git', ['diff', 'main', '--name-only'], { cwd: REPO, encoding: 'utf8' }).stdout.split('\n').filter(Boolean)
  const hrAssistOnly = spawnSync('git', ['diff', '--name-only', 'main', 'hr-assist'], { cwd: REPO, encoding: 'utf8' }).stdout
    .split('\n').filter((f) => /backend\/Hr\/|HrQueryCatalog|SqlServerHr|coreerp-/.test(f))
  const sqlClient = /Microsoft\.Data\.SqlClient/.test(read('backend/RabbitIC.Api.csproj')) || /SqlClient/.test(diff)
  const leaked = names.filter((f) => hrAssistOnly.includes(f))
  return ok(!sqlClient && !leaked.length, `${names.length} files differ from main; SqlClient ${sqlClient}; hr-assist files present [${leaked}]`)
})

check('G1.9', 1, 'fake', 'model-policy-eval (which runs po-eval and oddity-eval) is unchanged', async () => {
  if (ctx.args.noSuites) return ok(false, 'skipped with --no-suites')
  const r = spawnSync(process.execPath, [path.join(REPO, 'scripts', 'model-policy-eval.mjs'), '--phase', '1'], {
    cwd: REPO, encoding: 'utf8', timeout: 900_000,
    env: { ...process.env, MODEL_POLICY_EVAL_ADMIN_USER: process.env.HR_EVAL_ADMIN_USER, MODEL_POLICY_EVAL_ADMIN_SECRET: process.env.HR_EVAL_ADMIN_SECRET },
  })
  const out = r.stdout ?? ''
  const summary = out.match(/^(\d+)\/(\d+) passed$/m)
  const nested = out.match(/^G1\.11 \|.*$/m)?.[0] ?? ''
  return ok(r.status === 0 && summary?.[0] === '14/14 passed' && /po-eval 14\/14/.test(nested) && /oddity-eval 78\/78/.test(nested),
    `model-policy-eval ${summary?.[0] ?? '(no summary)'}; ${nested.split('|').slice(3).join('|').trim()}`
    + `${r.status ? `\n${out.split('\n').filter((l) => /FAIL/.test(l)).slice(0, 6).join('\n')}` : ''}`)
})

// ═══════════════════════════ Phase 2 — the HR chat ═══════════════════════════

/** One HR chat turn, read to the end. Frames are parsed; refusals keep their body. */
async function hrChat(user, body) {
  const res = await fetch(`${ctx.api.url}/api/hr/chat`, {
    method: 'POST', headers: { 'content-type': 'application/json', authorization: `Bearer ${user.token}` }, body: JSON.stringify(body),
  })
  const text = await res.text()
  const frames = /ndjson/.test(res.headers.get('content-type') ?? '')
    ? text.split('\n').filter(Boolean).map((l) => { try { return JSON.parse(l) } catch { return null } }).filter(Boolean)
    : []
  let json = null
  try { json = frames.length ? null : JSON.parse(text) } catch { /* plain text */ }
  return {
    status: res.status, text, json, frames,
    convId: Number(res.headers.get('X-Conversation-Id')) || null,
    context: frames.find((f) => f.type === 'context'),
    unverified: frames.find((f) => f.type === 'unverified')?.amounts,
    unverifiedFormulas: frames.find((f) => f.type === 'unverified')?.formulas,
    answer: frames.filter((f) => f.type === 'content').map((f) => f.text).join(''),
    tools: frames.filter((f) => f.type === 'tool'),
    statusFrames: frames.filter((f) => f.type === 'status'),
    formulas: frames.filter((f) => f.type === 'formula'),
  }
}

/** The first frame of an HR answer and how soon it arrived, read as the stream comes in. */
async function statusTiming(user, message) {
  const t0 = Date.now()
  const res = await fetch(`${ctx.api.url}/api/hr/chat`, {
    method: 'POST', headers: { 'content-type': 'application/json', authorization: `Bearer ${user.token}` }, body: JSON.stringify({ message }),
  })
  const reader = res.body.getReader(); const dec = new TextDecoder()
  let buf = '', firstType = null, ms = null
  const lines = []
  for (;;) {
    const { value, done } = await reader.read(); if (done) break
    buf += dec.decode(value, { stream: true })
    const parts = buf.split('\n'); buf = parts.pop()
    for (const l of parts) {
      let f; try { f = JSON.parse(l) } catch { continue }
      if (firstType === null) { firstType = f.type; ms = Date.now() - t0 }
      if (f.type === 'status') lines.push(`"${f.text}"`)
    }
  }
  return { firstType, ms, lines }
}

/** Chat requests the fake (or, --live, the proxy) received, oldest first. */
const chatRequests = () => ctx.fake.requests.filter((r) => r.path === '/api/chat')
const script = (fn) => ctx.fake.setChatScript?.(fn)
const sqliteWrite = async (sql) => {
  const { DatabaseSync } = await import('node:sqlite')
  const db = new DatabaseSync(ctx.api.dbPath)
  try { db.exec(sql) } finally { db.close() }
}

/** Multipart upload of [name, Buffer|string] pairs in one request. */
async function upload(files, user = ctx.admin, fields = {}) {
  const form = new FormData()
  for (const [name, data] of files) form.append('file', new Blob([data]), name)
  for (const [k, v] of Object.entries(fields)) form.append(k, v)
  const res = await fetch(`${ctx.api.url}/api/hr/library/upload`, { method: 'POST', headers: { Authorization: `Bearer ${user.token}` }, body: form })
  return { status: res.status, json: await res.json().catch(() => null) }
}

async function waitFor(predicate, timeoutMs = 120_000, stepMs = 400) {
  const until = Date.now() + timeoutMs
  for (;;) {
    const v = await predicate()
    if (v) return v
    if (Date.now() > until) return null
    await sleep(stepMs)
  }
}

const median = (xs) => { const s = [...xs].sort((a, b) => a - b); return s.length ? s[Math.floor(s.length / 2)] : null }
const ALWAYS_CAP_MESSAGE = /Always items would use [\d,]+ of 8,000 tokens; make this item Search or switch another off/

// ─────────────────────────── G2.1 — context sent ───────────────────────────

check('G2.1', 2, 'fake', 'the model gets the persona and every active always item whole in SortOrder, then the excerpts with SourceRefs; inactive items are not sent', async () => {
  const codesText = fs.readFileSync(path.join(FIXTURES, 'attendance-codes.csv'), 'utf8')
  const codes = await asAdmin('POST', '/api/hr/library', { name: 'eval-g21-codes', hrType: 'codes', content: codesText })
  const off = await asAdmin('POST', '/api/hr/library', { name: 'eval-g21-inactive', hrType: 'note', mode: 'always', content: 'INACTIVE-MARKER-G21 must never be sent.' })
  await asAdmin('PUT', `/api/hr/library/${off.json?.id}`, { isActive: false })
  const rules = (await library()).find((i) => i.name === 'HR_SETTLEMENT_RULES')
  const rulesText = (await asAdmin('GET', `/api/hr/library/${rules.id}`)).json.content.trim()
  await embedded()

  const before = chatRequests().length
  const r = await hrChat(ctx.admin, { message: 'How is service benefit calculated for 3 to 9 years?' })
  const sent = chatRequests().slice(before)[0]?.body
  const sys = sent?.messages?.[0], last = sent?.messages?.at(-1)
  const rulesAt = sys?.content.indexOf(rulesText) ?? -1, codesAt = sys?.content.indexOf(codesText.trim().replace(/\r\n/g, '\n')) ?? -1
  const refs = r.context?.excerpts?.map((e) => e.sourceRef) ?? []
  const excerptTokens = (r.context?.excerpts ?? []).reduce((n, e) => n + e.tokens, 0)
  const t = r.context?.tokens ?? {}
  const pass = r.status === 200 && sys?.role === 'system' && /মানব সম্পদ/.test(sys.content) && rulesAt >= 0 && codesAt > rulesAt
    && !sys.content.includes('INACTIVE-MARKER-G21') && last?.role === 'user' && last.content.includes('HR context for this question')
    && refs.length > 0 && refs.length <= 5 && refs.every((s) => last.content.includes(s)) && refs.every((s) => !sys.content.includes(s))
    && excerptTokens <= 1500 && t.system <= 8000 && t.system + t.excerpts + t.history + t.question <= t.window - t.reserved
  for (const id of [codes.json?.id, off.json?.id]) if (id) await asAdmin('DELETE', `/api/hr/library/${id}`)
  return ok(pass, `status ${r.status}; system: persona ${/মানব সম্পদ/.test(sys?.content ?? '')}, rules at ${rulesAt}, codes at ${codesAt} (after rules), inactive sent ${sys?.content.includes('INACTIVE-MARKER-G21')}; `
    + `last user message has "HR context for this question" ${last?.content.includes('HR context for this question')} and ${refs.length} excerpts: ${refs.slice(0, 4).join(' | ')}; `
    + `excerpt chunks ${excerptTokens} tokens; tokens ${JSON.stringify(t)}`)
})

// ─────────────────────────── G2.2 — never cut ───────────────────────────

check('G2.2', 2, 'fake', 'always items over 8,000 tokens: refused, naming the largest, and the model receives nothing', async () => {
  // The Library refuses this (G2.12), so the state is made directly in SQLite.
  const big = 'Overflow clause. '.repeat(2600)
  await sqliteWrite(`INSERT INTO ContextSources (OwnerUserId, Name, Kind, Content, IsActive, SortOrder, CreatedAt, UpdatedAt, HrType, Mode, Version)
    VALUES (${ctx.admin.id}, 'eval-g22-oversized', 'hr', '${big}', 1, 900, '2026-01-01 00:00:00', '2026-01-01 00:00:00', 'note', 'always', 1)`)
  const before = chatRequests().length
  const r = await hrChat(ctx.admin, { message: 'Anything at all?' })
  const after = chatRequests().length
  await sqliteWrite(`DELETE FROM ContextSources WHERE Name = 'eval-g22-oversized'`)
  const msg = r.json?.error ?? r.text
  return ok(r.status === 422 && /eval-g22-oversized/.test(msg) && /8,000/.test(msg) && after === before,
    `${r.status} "${String(msg).slice(0, 200)}"; model requests ${before}→${after}`)
})

check('G2.2', 2, 'fake', 'a long history drops its oldest turns first; the system message stays byte-identical', async () => {
  // Each turn about 2,600 tokens: four of them cannot all fit beside the system message.
  const filler = (n) => `TURN-${n}-MARKER ` + 'background detail about the employee and the case. '.repeat(200)
  let conv = null
  const systems = [], lasts = []
  for (let n = 1; n <= 5; n++) {
    const before = chatRequests().length
    const r = await hrChat(ctx.admin, { conversationId: conv, message: filler(n) })
    conv = r.convId
    const sent = chatRequests().slice(before)[0]?.body
    systems.push(sent?.messages?.[0]?.content); lasts.push({ sent, ctxFrame: r.context })
  }
  const final = lasts.at(-1)
  const history = final.sent.messages.slice(1, -1).map((m) => m.content).join('\n')
  const kept = [1, 2, 3, 4].filter((n) => history.includes(`TURN-${n}-MARKER`))
  const identical = systems.every((s) => s === systems[0])
  // Dropped turns must be the oldest: whatever is kept is a suffix of 1..4.
  const suffix = kept.length > 0 && kept.every((n, i) => n === 4 - kept.length + 1 + i)
  return ok(identical && kept.length < 4 && suffix && final.ctxFrame?.historyDropped > 0,
    `system identical across 5 turns ${identical}; turns kept in the last request [${kept}] of [1,2,3,4]; dropped ${final.ctxFrame?.historyDropped}; history tokens ${final.ctxFrame?.tokens?.history}`)
})

// ─────────────────────────── G2.3 — calculate ───────────────────────────

check('G2.3', 2, 'fake', 'the evaluator: round((12000/30)*15*7, 0) is 42000; "12000*" and system("x") are refused with a reason', async () => {
  const a = await asAdmin('POST', '/api/hr/calculate', { expression: 'round((12000/30)*15*7, 0)' })
  const b = await asAdmin('POST', '/api/hr/calculate', { expression: '12000*' })
  const c = await asAdmin('POST', '/api/hr/calculate', { expression: 'system("x")' })
  return ok(a.status === 200 && String(a.json?.result) === '42000' && b.status === 400 && b.json?.error && c.status === 400 && c.json?.error,
    `→ ${a.status} ${a.json?.result}; "12000*" → ${b.status} "${b.json?.error}"; system("x") → ${c.status} "${c.json?.error}"`)
})

check('G2.3', 2, 'fake', 'inline formulas: split across chunks → ৳42,000 in place, stored in HrMetaJson; an invalid one → "⚠ গণনা করা যায়নি", unverified; no tools on the HR route', async () => {
  // The marker arrives in pieces, as a model streams it; one ৳ before a formula must not double.
  script(() => ({ words: [
    'The service benefit is [', '[= round((12000/30)*15', '*7, 0) ]', ']. Per day ৳', '[[= 12000/30 ]]. ',
    'Broken: [[= 12000* ]] here. ', 'Cut: [[= 5 *\n', 'next line.',
  ] }))
  const before = chatRequests().length
  const r = await hrChat(ctx.admin, { message: 'Basic is 12,000, 7 years, Non-Management resignation. Service benefit?' })
  const sent = chatRequests().slice(before)
  script(null)
  const f = r.formulas
  const okOne = f.find((x) => x.ok && x.expression === 'round((12000/30)*15*7, 0)')
  const perDay = f.find((x) => x.ok && x.expression === '12000/30')
  const bad = f.filter((x) => !x.ok).map((x) => x.expression)
  const offsetsRight = f.every((x) => r.answer.slice(x.offset, x.offset + x.length) === x.display)
  const meta = JSON.parse((await sqlite(`SELECT HrMetaJson FROM Messages WHERE ConversationId = ${r.convId} AND Role = 'assistant'`))[0]?.HrMetaJson ?? '{}')
  const stored = (meta.formulas ?? []).map((x) => x.expression)
  const hrTools = sent.map((s) => (s.body?.tools ?? []).length)
  const mainBefore = chatRequests().length
  await call(ctx.api.url, 'POST', '/api/chat', { token: ctx.admin.token, body: { model: 'fake-ok', source: 'local', message: 'hello' } })
  const mainTools = (chatRequests().slice(mainBefore)[0]?.body?.tools ?? []).map((t) => t.function?.name)
  const pass = r.status === 200 && okOne?.display === '৳42,000' && perDay?.display === '৳400' && r.answer.includes('Per day ৳400.')
    && !r.answer.includes('৳৳') && !r.answer.includes('[[=') && JSON.stringify(bad) === JSON.stringify(['12000*', '5 *'])
    && (r.answer.match(/⚠ গণনা করা যায়নি/g) ?? []).length === 2 && r.answer.includes('⚠ গণনা করা যায়নি\nnext line.')
    && JSON.stringify(r.unverifiedFormulas) === JSON.stringify(['12000*', '5 *']) && offsetsRight
    && stored.join('|') === f.map((x) => x.expression).join('|') && sent.length === 1 && hrTools.every((n) => n === 0)
    && !mainTools.includes('calculate')
  return ok(pass, `answer "${r.answer.replace(/\n/g, '⏎')}"; formulas ${f.map((x) => `${x.expression} → ${x.display}${x.ok ? '' : ` (${x.error})`}`).join('; ')}; `
    + `offsets right ${offsetsRight}; unverified formulas ${JSON.stringify(r.unverifiedFormulas)}; stored in HrMetaJson [${stored.join(' | ')}]; `
    + `HR requests ${sent.length}, tools offered ${JSON.stringify(hrTools)}; /api/chat tools [${mainTools}]`)
})

check('G2.3', 2, 'fake', '/api/chat: after a tool runs, only the final round is shown and saved; the call goes back with the draft', async () => {
  // A separate API where web search counts as configured, pointed at the fake, so
  // nothing leaves the machine; the search itself fails there and the loop goes on.
  const runDir = fs.mkdtempSync(path.join(EVAL_ROOT, 'toolround-'))
  const api = await startApi({ fakeUrl: ctx.fakeUrl, runDir, env: { ...ctx.env, OllamaCloud__ApiKey: 'eval-fake', OllamaCloud__BaseUrl: ctx.fakeUrl } })
  try {
    const admin = (await call(api.url, 'POST', '/api/auth/login', { body: { username: process.env.HR_EVAL_ADMIN_USER, secret: process.env.HR_EVAL_ADMIN_SECRET } })).json
    script((body) => body.messages.some((m) => m.role === 'tool')
      ? { words: ['Final ', 'answer.'] }
      : { words: ['Draft ', 'before the search. '], toolCalls: [{ name: 'web_search', arguments: { query: 'eval' } }] })
    const before = chatRequests().length
    const res = await fetch(`${api.url}/api/chat`, {
      method: 'POST', headers: { 'content-type': 'application/json', authorization: `Bearer ${admin.token}` },
      body: JSON.stringify({ model: 'fake-ok', source: 'local', message: 'search something', webSearch: true }),
    })
    const frames = (await res.text()).split('\n').filter(Boolean).map((l) => { try { return JSON.parse(l) } catch { return null } }).filter(Boolean)
    script(null)
    const convId = Number(res.headers.get('X-Conversation-Id'))
    const sent = chatRequests().slice(before)
    const types = frames.map((f) => f.type)
    // What the client ends with: content, cleared at each content_reset.
    let shown = ''
    for (const f of frames) { if (f.type === 'content') shown += f.text; if (f.type === 'content_reset') shown = '' }
    const saved = (await sqlite(`SELECT Content FROM Messages WHERE ConversationId = ${convId} AND Role = 'assistant'`, api.dbPath))[0]?.Content
    const asked = sent[1]?.body?.messages?.find((m) => m.role === 'assistant' && m.tool_calls)
    const pass = res.status === 200 && types.includes('content_reset') && types.indexOf('content_reset') < types.indexOf('tool')
      && shown === 'Final answer.' && saved === 'Final answer.' && asked?.content === 'Draft before the search. ' && asked?.tool_calls?.[0]?.function?.name === 'web_search'
    return ok(pass, `frames ${types.join(',')}; shown "${shown}"; saved "${saved}"; second request's assistant turn "${asked?.content}" with ${JSON.stringify(asked?.tool_calls?.map((t) => t.function?.name))}`)
  } finally { await api.stop() }
})

// ─────────────────────────── G2.4 — number check ───────────────────────────

check('G2.4', 2, 'fake', 'number check: a formula result and the user\'s amount pass, only the invented amount is unverified; the UI shows the warning line', async () => {
  script(() => ({ words: ['Basic ৳12,000 ', 'gives [[= round((12000/30)*15*7, 0) ]]; ', 'the bonus is ৳77,777.'] }))
  const r = await hrChat(ctx.admin, { message: 'Basic is 12,000, 7 years. Service benefit?' })
  script(null)
  const ui = fs.existsSync(path.join(REPO, 'frontend/src/components/hr/HrAsk.vue')) ? read('frontend/src/components/hr/HrAsk.vue') : ''
  const line = ui.includes('এই সংখ্যাগুলো যাচাই করা যায়নি') && ui.includes('Not verified')
  return ok(r.status === 200 && r.answer.includes('gives ৳42,000;') && JSON.stringify(r.unverified) === '[77777]' && line,
    `answer "${r.answer}"; unverified ${JSON.stringify(r.unverified)}; warning line in HrAsk.vue ${line}`)
})

// ─────────────────────────── G2.5 — saved separately ───────────────────────────

check('G2.5', 2, 'fake', 'HR conversations have Kind = hr, show in the HR list only; a normal chat does not show in the HR list', async () => {
  const r = await hrChat(ctx.admin, { message: 'Where is earned leave described?' })
  const main = await call(ctx.api.url, 'POST', '/api/chat', { token: ctx.admin.token, body: { model: 'fake-ok', source: 'local', message: 'a normal chat' } })
  const mainId = Number(main.headers.get('X-Conversation-Id'))
  const kind = await sqlite(`SELECT Kind FROM Conversations WHERE Id = ${r.convId}`)
  const hrList = ((await asAdmin('GET', '/api/hr/conversations')).json ?? []).map((c) => c.id)
  const mainList = ((await asAdmin('GET', '/api/conversations')).json ?? []).map((c) => c.id)
  const mainGet = (await asAdmin('GET', `/api/conversations/${r.convId}`)).status
  return ok(kind[0]?.Kind === 'hr' && hrList.includes(r.convId) && !mainList.includes(r.convId) && !hrList.includes(mainId) && mainList.includes(mainId) && mainGet === 404,
    `HR conversation ${r.convId} Kind ${kind[0]?.Kind}; in HR list ${hrList.includes(r.convId)}, in main list ${mainList.includes(r.convId)}, main GET ${mainGet}; `
    + `normal chat ${mainId} in HR list ${hrList.includes(mainId)}`)
})

// ─────────────────────────── G2.6 — access ───────────────────────────

check('G2.6', 2, 'fake', 'no hr tab → 403; hr-assistant naming qwen3.8-27b-q3 → 403 and the model receives nothing', async () => {
  const plain = await generalUser('g26')
  const a = await hrChat(plain, { message: 'hello' })
  const hra = await login(HR_ASSISTANT, SECRET)
  const before = chatRequests().length
  const b = await hrChat(hra, { message: 'hello', model: 'qwen3.8-27b-q3' })
  const after = chatRequests().length
  return ok(a.status === 403 && b.status === 403 && /gemma4:12b/.test(b.text) && after === before,
    `no tab → ${a.status}; hr-assistant + qwen3.8-27b-q3 → ${b.status} "${b.text.slice(0, 120)}"; model requests ${before}→${after}`)
})

// ─────────────────────────── G2.7 — UI ───────────────────────────

check('G2.7', 2, 'any', 'the Ask section: instant search, Search-in picker, clickable citations, status line, formula on hover, Context used panel, drawer; no hard-coded colours', async () => {
  const file = 'frontend/src/components/hr/HrAsk.vue'
  if (!fs.existsSync(path.join(REPO, file))) return ok(false, `${file} does not exist`)
  const src = read(file)
  const tab = read('frontend/src/components/tabs/HrTab.vue')
  const has = {
    search: /searchHr|\/hr\/search/.test(src + read('frontend/src/services/hr.service.js')),
    scope: /Search in/.test(src), cite: /data-cite/.test(src), panel: /Context used/.test(src),
    status: /data-status/.test(src) && /onStatus/.test(src), formula: /data-formula/.test(src) && /title=/.test(src),
    drawer: /drawer/i.test(src), stream: /reactive\(/.test(src), placeholderGone: !/শীঘ্রই আসছে/.test(tab),
  }
  const colours = ['frontend/src/components/hr/HrAsk.vue', 'frontend/src/components/hr/HrItemViewer.vue', 'frontend/src/components/hr/HrLibrary.vue']
    .filter((f) => fs.existsSync(path.join(REPO, f)))
    .flatMap((f) => read(f).split('\n').map((l, i) => [f, i + 1, l])).filter(([, , l]) => /#[0-9a-fA-F]{3,8}\b|rgba?\(/.test(l)).map(([f, n]) => `${f}:${n}`)
  return ok(Object.values(has).every(Boolean) && !colours.length, `${JSON.stringify(has)}; hard-coded colours [${colours}]`)
})

// ─────────────────────────── G2.8 — live ───────────────────────────

const SECTION1_QUESTIONS = [
  'Schedule-A কর্মী ৭ বছর ৮ মাস চাকরি করে ইস্তফা দিলে সার্ভিস বেনিফিট কীভাবে হিসাব হয়?',
  'Which attendance codes count as worked days for earned leave?',
  'Basic is 12,000, 7 years 8 months, 250 fraction days, resignation in April 2026, Non-Management. What is the service benefit?',
  'What happens when someone in unit 27 separates on the 28th?',
]

// The amounts each §1 question may show: Q3 has one right answer, ৳48,000, and its
// steps; the others give no taka amount (Q1 has no basic, so it must ask).
const SECTION1_ALLOWED = [[], [], [400, 6000, 48000], []]

check('G2.8', 2, 'live', 'gemma4:12b, the four §1 questions × 3: all cite; ≥ 10 of 12 use formulas for every amount; 0 wrong amounts shown without the warning', async () => {
  const lines = []
  let cited = 0, formulaRuns = 0, wrongUnwarned = 0
  for (let round = 1; round <= 3; round++) {
    for (const [i, q] of SECTION1_QUESTIONS.entries()) {
      const t0 = Date.now()
      const r = await hrChat(ctx.admin, { message: q })
      const cites = /\[[^\]\n]{3,}\]/.test(r.answer)
      const results = r.formulas.filter((f) => f.ok)
      const nothingUnverified = (r.unverified ?? []).length === 0 && (r.unverifiedFormulas ?? []).length === 0
      // Formulas for every amount: nothing untraced or uncalculated, and Q3's amount came from a formula.
      const usesFormulas = nothingUnverified && (i !== 2 || results.some((f) => Math.abs(Number(f.display.replace(/[^\d.-]/g, '')) - 48000) < 1))
      // A wrong amount shown without the warning can only be a formula result (anything else
      // untraced is flagged): one that is not among this question's right amounts.
      const wrong = results.filter((f) => !SECTION1_ALLOWED[i].some((v) => Math.abs(Number(f.display.replace(/[^\d.-]/g, '')) - v) < 1))
      cited += cites ? 1 : 0
      formulaRuns += r.status === 200 && usesFormulas ? 1 : 0
      wrongUnwarned += wrong.length
      lines.push(`${cites && usesFormulas && !wrong.length ? 'ok ' : 'BAD'} round ${round} Q${i + 1} (${((Date.now() - t0) / 1000).toFixed(1)} s; cites ${cites}; `
        + `formulas ${r.formulas.map((f) => `${f.expression} → ${f.display}`).join(', ') || 'none'}; unverified ${JSON.stringify(r.unverified)} ${JSON.stringify(r.unverifiedFormulas)}; `
        + `wrong unwarned ${JSON.stringify(wrong.map((f) => f.display))})\n      Q: ${q}\n      A: ${r.answer.replace(/\n/g, '\n         ')}`)
    }
  }
  return ok(cited === 12 && formulaRuns >= 10 && wrongUnwarned === 0,
    `cited ${cited}/12; formulas for every amount ${formulaRuns}/12; wrong amounts shown without the warning ${wrongUnwarned}\n    ` + lines.join('\n    '))
})

// ─────────────────────────── G2.9 — JSON ───────────────────────────

check('G2.9', 2, 'fake', 'a pasted array of 50 objects: chunks ≤ 1,500 chars with $[i] paths; one value is found in the top 3; invalid JSON names line and column', async () => {
  const items = Array.from({ length: 50 }, (_, i) => ({
    id: i, code: `JSONITEM-${i}`, value: `VAL-${i * 7}-ZETA`,
    note: `Allowance rule ${i} applies to staff in grade ${i % 9} for ${10 + i} days. `.repeat(2),
  }))
  const made = await asAdmin('POST', '/api/hr/library', { name: 'eval-allowances.json', hrType: 'note', content: JSON.stringify(items, null, 2) })
  await embedded()
  const chunks = (await asAdmin('GET', `/api/hr/library/${made.json?.id}/chunks`)).json?.chunks ?? []
  const pathRe = /\$\[(\d+)\](?:-\$\[(\d+)\])?/
  const covers = (ref, i) => { const m = pathRe.exec(ref ?? ''); return m && +m[1] <= i && i <= +(m[2] ?? m[1]) }
  const hits = (await asAdmin('GET', `/api/hr/search?q=${encodeURIComponent('VAL-259-ZETA')}`)).json?.hits ?? []
  const found = hits.slice(0, 3).some((h) => h.text.includes('VAL-259-ZETA') && covers(h.sourceRef, 37))
  const bad = await asAdmin('POST', '/api/hr/library', { name: 'eval-bad.json', hrType: 'note', content: '{\n  "a": 1,\n  "b": \n}' })
  await asAdmin('DELETE', `/api/hr/library/${made.json?.id}`)
  return ok(made.status === 201 && chunks.length > 1 && chunks.every((c) => c.text.length <= 1500 && pathRe.test(c.sourceRef)) && found
      && bad.status === 400 && /line \d+, column \d+/i.test(bad.json?.error ?? ''),
    `${chunks.length} chunks, longest ${Math.max(0, ...chunks.map((c) => c.text.length))}, refs ${chunks.slice(0, 3).map((c) => c.sourceRef).join(' | ')}…; `
    + `"VAL-259-ZETA" top 3: ${hits.slice(0, 3).map((h) => h.sourceRef).join(' | ')} (found ${found}); invalid → ${bad.status} "${bad.json?.error}"`)
})

// ─────────────────────────── G2.10 — other formats ───────────────────────────

check('G2.10', 2, 'fake', '.csv, .pdf and .xlsx uploaded together; header repeated, rows/page/sheet in SourceRef; .docx refused', async () => {
  const csv = ['code,meaning,counts_as_worked_day']
    .concat(Array.from({ length: 120 }, (_, i) => `C${String(i).padStart(3, '0')},Generated attendance code number ${i} for the eval,${i % 2 ? 'yes' : 'no'}`)).join('\n')
  const up = await upload([
    ['eval-codes-big.csv', csv],
    ['notice-policy.pdf', fs.readFileSync(path.join(FIXTURES, 'notice-policy.pdf'))],
    ['stamp-rates.xlsx', fs.readFileSync(path.join(FIXTURES, 'stamp-rates.xlsx'))],
    ['policy.docx', Buffer.from('PK fake docx')],
  ], ctx.admin, { mode: 'search' })   // a big table HR wants searched, not sent whole
  const results = up.json?.results ?? []
  const byName = (n) => results.find((x) => x.file === n)
  await embedded()
  const chunksOfItem = async (r) => r?.item?.id ? (await asAdmin('GET', `/api/hr/library/${r.item.id}/chunks`)).json?.chunks ?? [] : []
  const csvChunks = await chunksOfItem(byName('eval-codes-big.csv'))
  const pdfChunks = await chunksOfItem(byName('notice-policy.pdf'))
  const xlsxChunks = await chunksOfItem(byName('stamp-rates.xlsx'))
  const docx = byName('policy.docx')
  for (const r of results) if (r.item?.id) await asAdmin('DELETE', `/api/hr/library/${r.item.id}`)
  const csvOk = csvChunks.length > 1 && csvChunks.every((c) => c.text.startsWith('code,meaning,counts_as_worked_day') && /rows \d+-\d+/.test(c.sourceRef))
  const pdfOk = pdfChunks.some((c) => /Page 1/.test(c.sourceRef)) && pdfChunks.some((c) => /Page 2/.test(c.sourceRef))
  const xlsxOk = xlsxChunks.some((c) => /StampRates/.test(c.sourceRef) && /Stamp fee/.test(c.text))
  return ok(csvOk && pdfOk && xlsxOk && docx && !docx.ok && /save it as PDF first/i.test(docx.error ?? ''),
    `one request → ${up.status}; csv ${csvChunks.length} chunks (${csvChunks.map((c) => c.sourceRef).join(' | ')}); `
    + `pdf [${pdfChunks.map((c) => c.sourceRef).join(' | ')}]; xlsx [${xlsxChunks.map((c) => c.sourceRef).join(' | ')}]; docx "${docx?.error}"`)
})

// ─────────────────────────── G2.11 — hybrid search ───────────────────────────

check('G2.11', 2, 'fake', 'EXL is found at rank 1 by the keyword side, also with the vector side unavailable (both ranks shown)', async () => {
  const q = async () => (await asAdmin('GET', '/api/hr/search?q=EXL')).json?.hits ?? []
  const normal = await q()
  ctx.fake.setEmbedFailure(true)
  const keywordOnly = await q()
  ctx.fake.setEmbedFailure(false)
  const top = (hits) => hits[0] ? `${hits[0].sourceRef} (keyword rank ${hits[0].keywordRank}, vector rank ${hits[0].vectorRank ?? 'none'})` : '(no hits)'
  return ok(normal[0]?.text.includes("'EXL'") && normal[0]?.keywordRank === 1 && keywordOnly[0]?.text.includes("'EXL'") && keywordOnly[0]?.keywordRank === 1 && keywordOnly[0]?.vectorRank == null,
    `with both sides: ${top(normal)}; vector side down: ${top(keywordOnly)}`)
})

check('G2.11', 2, 'fake', 'editing and deleting an item updates the FTS5 table', async () => {
  const fts = async (id) => (await asAdmin('GET', '/api/hr/library/index-stats')).json?.byItem?.[id]?.fts ?? 0
  const made = await asAdmin('POST', '/api/hr/library', { name: 'eval-fts.md', hrType: 'note', content: 'FTSMARKER one. ' + 'filler text '.repeat(60) })
  const id = made.json?.id
  const a = await fts(id)
  await asAdmin('PUT', `/api/hr/library/${id}`, { content: 'FTSMARKER two. ' + 'filler text '.repeat(400) })
  const b = await fts(id)
  const chunks = ((await asAdmin('GET', `/api/hr/library/${id}/chunks`)).json?.chunks ?? []).length
  const hit = ((await asAdmin('GET', '/api/hr/search?q=FTSMARKER')).json?.hits ?? []).some((h) => h.itemId === id)
  await asAdmin('DELETE', `/api/hr/library/${id}`)
  const c = await fts(id)
  const gone = !((await asAdmin('GET', '/api/hr/search?q=FTSMARKER')).json?.hits ?? []).some((h) => h.itemId === id)
  return ok(a >= 1 && b === chunks && b > a && hit && c === 0 && gone, `FTS rows ${a} → edit → ${b} (chunks ${chunks}) → delete → ${c}; searchable while present ${hit}, gone after delete ${gone}`)
})

// ─────────────────────────── G2.12 — always cap ───────────────────────────

check('G2.12', 2, 'fake', 'switching an item to always past 8,000 tokens is refused with the message; the chat is never refused for budget', async () => {
  const made = await asAdmin('POST', '/api/hr/library', { name: 'eval-big-note.md', hrType: 'note', content: 'Leave policy paragraph. '.repeat(1200) })
  const toAlways = await asAdmin('PUT', `/api/hr/library/${made.json?.id}`, { mode: 'always' })
  const direct = await asAdmin('POST', '/api/hr/library', { name: 'eval-big-always.md', hrType: 'note', mode: 'always', content: 'Leave policy paragraph. '.repeat(1200) })
  const r = await hrChat(ctx.admin, { message: 'What is the stamp fee?' })
  await asAdmin('DELETE', `/api/hr/library/${made.json?.id}`)
  if (direct.json?.id) await asAdmin('DELETE', `/api/hr/library/${direct.json.id}`)
  return ok(toAlways.status === 400 && ALWAYS_CAP_MESSAGE.test(toAlways.json?.error ?? '') && direct.status === 400 && ALWAYS_CAP_MESSAGE.test(direct.json?.error ?? '') && r.status === 200,
    `switch → ${toAlways.status} "${toAlways.json?.error}"; add as always → ${direct.status}; chat → ${r.status}`)
})

// ─────────────────────────── G2.13 — scale ───────────────────────────

check('G2.13', 2, 'fake', '200 items (~2 MB): the Library stays responsive while indexing, search median < 1 s, a failed embed shows Retry and recovers', async () => {
  const words = 'gratuity notice leave basic gross service termination allowance unit grade rule payment'.split(' ')
  const doc = (i) => `# Generated policy ${i}\n\nSCALEITEM-${i} ` + Array.from({ length: 1500 }, (_, k) => words[(i * 7 + k * 3) % words.length]).join(' ')
  const ids = [], uploads = [], listMs = [], progress = []
  let uploading = true
  // The Library is read throughout the uploads and the background indexing.
  const sampler = (async () => {
    while (uploading) {
      const t0 = Date.now()
      const items = (await asAdmin('GET', '/api/hr/library')).json?.items ?? []
      listMs.push(Date.now() - t0)
      const indexing = items.filter((i) => i.indexState === 'indexing')
      if (indexing.length) progress.push(`${indexing.length} indexing, e.g. ${indexing[0].name} ${indexing[0].embeddedCount} of ${indexing[0].chunkCount}`)
      await sleep(250)
    }
  })()
  for (let b = 0; b < 4; b++) {
    const t0 = Date.now()
    const up = await upload(Array.from({ length: 50 }, (_, k) => [`eval-scale-${b * 50 + k}.md`, doc(b * 50 + k)]))
    uploads.push(Date.now() - t0)
    for (const r of up.json?.results ?? []) if (r.item?.id) ids.push(r.item.id)
  }
  await sleep(1500)
  uploading = false
  await sampler
  const bytes = Array.from({ length: 200 }, (_, i) => Buffer.byteLength(doc(i))).reduce((a, b) => a + b, 0)
  const searchMs = []
  for (let k = 0; k < 20; k++) {
    const t0 = Date.now()
    await asAdmin('GET', `/api/hr/search?q=${encodeURIComponent(`SCALEITEM-${(k * 37) % 200} gratuity`)}`)
    searchMs.push(Date.now() - t0)
  }
  // A failed embed: the item is marked failed with its reason, and Retry recovers it.
  ctx.fake.setEmbedFailure(true)
  const failing = await asAdmin('POST', '/api/hr/library', { name: 'eval-fails.md', hrType: 'note', content: 'RETRYMARKER ' + 'text '.repeat(300) })
  const failed = await waitFor(async () => {
    const i = (await asAdmin('GET', `/api/hr/library/${failing.json?.id}`)).json
    return i?.indexState === 'failed' ? i : null
  }, 60_000)
  ctx.fake.setEmbedFailure(false)
  const retry = await asAdmin('POST', `/api/hr/library/${failing.json?.id}/retry`)
  const recovered = await waitFor(async () => {
    const i = (await asAdmin('GET', `/api/hr/library/${failing.json?.id}`)).json
    return i?.indexState === 'ready' ? i : null
  }, 60_000)
  const allIndexed = await waitFor(async () => {
    const items = (await asAdmin('GET', '/api/hr/library')).json?.items ?? []
    return items.filter((i) => ids.includes(i.id)).every((i) => i.indexState === 'ready')
  }, 600_000, 1000)
  for (const id of [...ids, failing.json?.id]) if (id) await asAdmin('DELETE', `/api/hr/library/${id}`)
  return ok(ids.length === 200 && median(listMs) < 1000 && Math.max(...listMs) < 3000 && progress.length > 0 && median(searchMs) < 1000
      && failed?.indexError && retry.status === 200 && recovered && allIndexed,
    `${ids.length} items, ${(bytes / 1024 / 1024).toFixed(2)} MB, uploads ${uploads.join('/')} ms; library list median ${median(listMs)} ms, max ${Math.max(...listMs)} ms; `
    + `progress seen: ${progress[0] ?? 'none'}; search median ${median(searchMs)} ms (20 queries, max ${Math.max(...searchMs)}); `
    + `failed: "${failed?.indexError}" → retry ${retry.status} → ${recovered ? 'ready' : 'NOT recovered'}; all 200 indexed ${!!allIndexed}`)
})

// ─────────────────────────── G2.14 — speed and cache ───────────────────────────

check('G2.14', 2, 'fake', 'system message byte-identical; excerpts only last, at most 5 within 1,500 tokens; num_ctx, keep_alive, temperature fixed; status line first, within 1 s', async () => {
  const sent = [], receipts = []
  let conv = null
  for (const q of ['What is the gratuity rule for Management?', 'And for Non-Management?']) {
    const before = chatRequests().length
    const r = await hrChat(ctx.admin, { conversationId: conv, message: q })
    conv = r.convId
    sent.push(chatRequests().slice(before)[0]?.body)
    receipts.push(r)
  }
  const limits = receipts.map((r) => `${r.context?.excerpts?.length}/${(r.context?.excerpts ?? []).reduce((n, e) => n + e.tokens, 0)}`)
  const withinLimits = receipts.every((r) => (r.context?.excerpts?.length ?? 9) <= 5 && (r.context?.excerpts ?? []).reduce((n, e) => n + e.tokens, 0) <= 1500)
  const status = await statusTiming(ctx.admin, 'Which attendance codes count for earned leave?')
  const [a, b] = sent
  const same = a?.messages?.[0]?.content === b?.messages?.[0]?.content
  const inMiddle = (b?.messages ?? []).slice(0, -1).some((m) => m.content.includes('HR context for this question'))
  const inLast = b?.messages?.at(-1)?.content.includes('HR context for this question')
  const opts = sent.map((s) => `${s?.options?.num_ctx}/${s?.keep_alive}/${s?.options?.temperature}`)
  return ok(same && !inMiddle && inLast && opts.every((o) => o === '16384/-1/0') && withinLimits && status.firstType === 'status' && status.ms < 1000,
    `system identical ${same}; excerpts outside the last message ${inMiddle}; in the last ${inLast}; num_ctx/keep_alive/temperature ${opts.join(', ')}; `
    + `excerpts/tokens ${limits.join(', ')}; first frame "${status.firstType}" after ${status.ms} ms: ${status.lines.join(' → ')}`)
})

const G214_PAIRS = [
  ['What is the gratuity rule for Management?', 'And what changes from 28 June 2026?'],
  ['How is earned leave paid at settlement?', 'What is the cap for Non-Management?'],
  ['What does the notice period rule say?', 'Who pays notice when the employee leaves without it?'],
  ['How is the salary month chosen for a separation?', 'And for unit 27?'],
  ['What is the retirement benefit?', 'Does a death case get it too?'],
  ['How is length of service counted?', 'What are fraction days used for?'],
]

check('G2.14', 2, 'live', 'live gemma4:12b, warm: status line within 1 s; the second question reaches its first word in < 15 s (median of 5); Ollama reused the always prefix', async () => {
  // Ollama's MLX runner reports the whole prompt in prompt_eval_count even when it
  // reused a cached prefix, so reuse is read from time instead: a cold prompt
  // (unique text, never cached) gives this machine's evaluation rate, and a warm
  // request that reused the system message takes only as long as its new tokens.
  const rules = fs.readFileSync(path.join(REPO, 'docs/hr/HR_SETTLEMENT_RULES.md'), 'utf8')
  const cold = await (await fetch(`${ctx.fakeUrl}/api/chat`, {
    method: 'POST', headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ model: 'gemma4:12b', stream: false, think: false, keep_alive: -1, options: { num_ctx: 16384, num_predict: 1 },
      messages: [{ role: 'system', content: `calibration ${Date.now()}\n\n${rules}` }, { role: 'user', content: 'ok' }] }),
  })).json()
  const rate = cold.prompt_eval_count / (cold.prompt_eval_duration / 1e9)   // tokens per second, cold

  // Warm means the system prefix has been seen: the runner re-evaluates the first two
  // requests after the prefix changes (the calibration above changes it), so one
  // conversation runs unmeasured first. Its cold cost is reported, not hidden.
  const firsts = [], statuses = [], evals = []
  let coldTurn = null
  for (let n = -1; n < 5; n++) {
    // A different conversation each time: repeating one at temperature 0 would hit a
    // fully cached prompt and flatter the time.
    const [firstQ, secondQ] = G214_PAIRS[n + 1]
    const a = await hrChat(ctx.admin, { message: firstQ })
    const t0 = Date.now()
    const res = await fetch(`${ctx.api.url}/api/hr/chat`, {
      method: 'POST', headers: { 'content-type': 'application/json', authorization: `Bearer ${ctx.admin.token}` },
      body: JSON.stringify({ conversationId: a.convId, message: secondQ }),
    })
    const reader = res.body.getReader(); const dec = new TextDecoder(); let buf = '', first = null, sys = null, statusAt = null
    for (;;) {
      const { value, done } = await reader.read(); if (done) break
      buf += dec.decode(value, { stream: true })
      if (statusAt === null && /"type":"status"/.test(buf)) statusAt = Date.now() - t0
      if (first === null && /"type":"content"/.test(buf)) first = Date.now() - t0
      const m = /"type":"context".*?"system":(\d+)/.exec(buf); if (m) sys = +m[1]
    }
    const done = ctx.proxyFinals?.at(-1)
    const evaluated = Math.round((done?.prompt_eval_duration ?? 0) / 1e9 * rate)
    if (n < 0) { coldTurn = { first, evaluated, prompt: done?.prompt_eval_count }; continue }
    firsts.push(first)
    statuses.push(statusAt)
    evals.push({ prompt: done?.prompt_eval_count, evaluated, seconds: (done?.prompt_eval_duration ?? 0) / 1e9, system: sys })
  }
  // Reused: the tokens actually evaluated leave out (most of) the system message.
  const reused = evals.every((e) => e.prompt != null && e.system != null && e.evaluated <= e.prompt - 0.8 * e.system)
  return ok(median(firsts) < 15000 && statuses.every((s) => s !== null && s < 1000) && reused,
    `unmeasured warm-up after the prefix changed: first word ${(coldTurn.first / 1000).toFixed(1)} s, ~${coldTurn.evaluated} of ${coldTurn.prompt} tokens evaluated; `
    + `status line after ${statuses.map((s) => `${s} ms`).join(', ')}; `
    + `first word on the second question: ${firsts.map((f) => `${(f / 1000).toFixed(1)} s`).join(', ')} (median ${(median(firsts) / 1000).toFixed(1)} s); `
    + `cold rate ${rate.toFixed(0)} tokens/s (${cold.prompt_eval_count} tokens in ${(cold.prompt_eval_duration / 1e9).toFixed(1)} s); `
    + `prefix reused ${reused}: ${evals.map((e) => `${e.prompt} sent, ~${e.evaluated} evaluated in ${e.seconds.toFixed(1)} s (system ${e.system})`).join('; ')}`)
})

// ─────────────────────────── G2.15 — instant search, scoped ask ───────────────────────────

check('G2.15', 2, 'fake', '/api/hr/search?q=gratuity ranks chunks with snippets; a scoped ask retrieves from that item only', async () => {
  const t0 = Date.now()
  const s = await asAdmin('GET', '/api/hr/search?q=gratuity')
  const ms = Date.now() - t0
  const hits = s.json?.hits ?? []
  const target = hits[0]
  const before = chatRequests().length
  const r = await hrChat(ctx.admin, { message: 'What does this say about gratuity?', items: [target?.itemId] })
  const last = chatRequests().slice(before)[0]?.body?.messages?.at(-1)?.content ?? ''
  const exItems = (r.context?.excerpts ?? []).map((e) => e.itemId)
  return ok(hits.length > 0 && hits.length <= 10 && hits.every((h, i) => i === 0 || h.score <= hits[i - 1].score) && hits.some((h) => /«[^»]+»/.test(h.snippet ?? ''))
      && ms < 1000 && exItems.length > 0 && exItems.every((id) => id === target.itemId) && last.includes(target.sourceRef.split(' ')[0]),
    `${hits.length} hits in ${ms} ms, top: ${hits.slice(0, 3).map((h) => `${h.sourceRef} "${(h.snippet ?? '').slice(0, 60)}"`).join(' | ')}; `
    + `scoped to ${target?.itemName}: excerpt items [${[...new Set(exItems)]}]`)
})

// ─────────────────────────── G2.16 — build and suites ───────────────────────────

check('G2.16', 2, 'any', 'the frontend builds', async () => {
  const r = spawnSync('npm', ['run', 'build'], { cwd: path.join(REPO, 'frontend'), encoding: 'utf8', shell: process.platform === 'win32' })
  const out = (r.stdout ?? '') + (r.stderr ?? '')
  return ok(r.status === 0 && /built in/.test(out), `exit ${r.status}; ${(out.match(/built in [\d.]+m?s/) ?? ['no build line'])[0]}`)
})

// ─────────────────────────── runner ───────────────────────────

/** Forwards everything to the real Ollama, recording requests and each chat's final frame. */
async function startLiveProxy(upstream) {
  const http = await import('node:http')
  const requests = [], finals = []
  const server = http.createServer(async (req, res) => {
    const chunks = []
    for await (const c of req) chunks.push(c)
    const raw = Buffer.concat(chunks)
    let body = null
    try { body = raw.length ? JSON.parse(raw.toString('utf8')) : null } catch { /* not JSON */ }
    requests.push({ method: req.method, path: new URL(req.url, 'http://x').pathname, body })
    const up = await fetch(upstream + req.url, {
      method: req.method, headers: { 'content-type': 'application/json' }, body: req.method === 'GET' ? undefined : raw,
    })
    res.writeHead(up.status, { 'content-type': up.headers.get('content-type') ?? 'application/json' })
    const reader = up.body.getReader(), dec = new TextDecoder()
    let tail = ''
    for (;;) {
      const { value, done } = await reader.read()
      if (done) break
      res.write(value)
      if (req.url.startsWith('/api/chat')) {
        tail = (tail + dec.decode(value, { stream: true })).split('\n').slice(-2).join('\n')
      }
    }
    res.end()
    if (req.url.startsWith('/api/chat')) {
      for (const line of tail.split('\n').reverse()) {
        try { const j = JSON.parse(line); if (j.done) { finals.push(j); break } } catch { /* partial */ }
      }
    }
  })
  await new Promise((r) => server.listen(0, '127.0.0.1', r))
  return {
    url: `http://127.0.0.1:${server.address().port}`, requests, finals,
    setChatScript: () => {}, setEmbedFailure: () => {},
    stop: () => new Promise((r) => { server.closeAllConnections(); server.close(() => r()) }),
  }
}

function parseArgs(argv) {
  const a = { phase: 99, only: null, build: true, live: false, noSuites: false }
  for (let i = 2; i < argv.length; i++) {
    const v = argv[i]
    if (v === '--phase') a.phase = Number(argv[++i])
    else if (v === '--only') a.only = argv[++i]
    else if (v === '--no-build') a.build = false
    else if (v === '--live') a.live = true
    else if (v === '--no-suites') a.noSuites = true
  }
  return a
}

async function main() {
  const args = parseArgs(process.argv)
  ctx.args = args
  const selected = checks.filter((c) => c.phase <= args.phase && (!args.only || c.id === args.only))
    .filter((c) => c.mode === 'any' || c.mode === (args.live ? 'live' : 'fake'))
  if (!selected.length) { console.error('no checks selected'); process.exit(2) }

  const user = process.env.HR_EVAL_ADMIN_USER, secret = process.env.HR_EVAL_ADMIN_SECRET
  if (!user || !secret) { console.error('set HR_EVAL_ADMIN_USER and HR_EVAL_ADMIN_SECRET (never written in this script)'); process.exit(2) }

  if (args.build) {
    process.stdout.write('building the API for the eval… ')
    buildApi()
    console.log('done')
  }

  fs.mkdirSync(EVAL_ROOT, { recursive: true })
  ctx.runDir = fs.mkdtempSync(path.join(EVAL_ROOT, 'run-'))
  // --live: a logging proxy in front of the real Ollama, so checks can read what
  // the model was sent and Ollama's final frame (prompt_eval_count, G2.14).
  const fake = args.live ? await startLiveProxy(process.env.HR_EVAL_OLLAMA_URL ?? 'http://localhost:11434') : await startFakeOllama()
  ctx.fake = fake
  ctx.proxyFinals = fake.finals
  ctx.fakeUrl = fake.url
  // The fake's bag-of-words vectors score lower than a real model's, so the eval
  // asks for every hit; --live keeps the configured floor.
  ctx.env = {
    Hr__SeedDir: LIBRARY,
    HR_ASSISTANT_SECRET: SECRET,
    ...(args.live ? {} : { Hr__MinScore: '0' }),
  }
  let failed = 0

  try {
    ctx.api = await startApi({ fakeUrl: ctx.fakeUrl, runDir: ctx.runDir, env: ctx.env })
    ctx.admin = await login(user, secret)
    if (ctx.admin.role !== 'SystemAdmin') throw new Error(`the seeded admin is ${ctx.admin.role}, not SystemAdmin`)
    // Best effort here; the checks that need the library wait for it themselves.
    await embedded().catch(() => {})
    console.log(`mode ${args.live ? `LIVE (${ctx.fakeUrl})` : 'fake Ollama'} · api ${ctx.api.url} · db ${ctx.api.dbPath}\n`)
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
    await fake?.stop?.()
  }

  if (failed) console.log(`api log: ${ctx.api?.logPath ?? '(not started)'}`)
  process.exit(failed ? 1 : 0)
}

main().catch((e) => { console.error(e); process.exit(2) })
