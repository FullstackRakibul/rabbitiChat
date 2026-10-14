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
    out.push({ name: f.replace(/\.md$/i, ''), hrType: /rules/i.test(f) ? 'rules' : 'note', mode: 'always' })
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

async function retrievalCheck() {
  const q1 = await asAdmin('GET', `/api/hr/retrieve?q=${encodeURIComponent('How is service benefit calculated for 3 to 9 years?')}`)
  const q2 = await asAdmin('GET', `/api/hr/retrieve?q=${encodeURIComponent('Which attendance codes count for earned leave?')}`)
  const top1 = (q1.json?.hits ?? []).slice(0, 3), top2 = (q2.json?.hits ?? []).slice(0, 3)
  const a = top1.some((h) => h.section === 'Service Benefit')
  const b = top2.some((h) => h.text.includes(WORKED_CODES))
  const show = (hits) => hits.map((h) => `${h.sourceRef} (${h.score.toFixed(3)})`).join(' | ')
  return ok(a && b, `service benefit → ${a ? 'found' : 'MISSED'}: ${show(top1)}\n    attendance codes → ${b ? 'found' : 'MISSED'}: ${show(top2)}`)
}

check('G1.6', 1, 'fake', 'retrieval (fake embedding): both questions find their chunk in the top 3', retrievalCheck)
check('G1.6', 1, 'live', 'retrieval (nomic-embed-text): both questions find their chunk in the top 3', retrievalCheck)

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
  const upJson = await up.json().catch(() => null)
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
  const r = spawnSync('dotnet', ['build', path.join(REPO, 'backend', 'RabbitIC.Api.csproj'), '-v', 'q', '--nologo'],
    { cwd: REPO, encoding: 'utf8', shell: process.platform === 'win32' })
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

// ─────────────────────────── runner ───────────────────────────

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
  const fake = args.live ? null : await startFakeOllama()
  ctx.fakeUrl = args.live ? (process.env.HR_EVAL_OLLAMA_URL ?? 'http://localhost:11434') : fake.url
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
