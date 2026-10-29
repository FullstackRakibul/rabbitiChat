#!/usr/bin/env node
// HRIS Workspace eval — see docs/HRIS_WORKSPACE_PROMPT.md §4–§5 (Phases W1–W4).
//
//   node scripts/hris-eval.mjs --phase W1            every W1 check on the synthetic Matrix fixtures
//   node scripts/hris-eval.mjs --only G-W1.5         one gate
//   node scripts/hris-eval.mjs --no-build            reuse the last eval build
//   node scripts/hris-eval.mjs --shots <dir>         where the UI gates write their screenshots
//   node scripts/hris-eval.mjs --live                (W2+) the Mac's Ollama; counts only
//
// Environment: HRIS_EVAL_ADMIN_USER, HRIS_EVAL_ADMIN_SECRET (the seeded administrator;
// never written here).
//
// The fake Ollama, the API start-up with a throwaway SQLite file, the headless browser
// and the Matrix fixture generator are the other suites'. Fixtures are synthetic. Node 22+,
// no dependencies.

import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import crypto from 'node:crypto'
import { spawnSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'

import { buildApi, startApi, call, startFakeOllama, API_BIN_DIR } from './oddity-eval.mjs'
import { launchBrowser } from './lib/headless-browser.mjs'
import { makeFixtures } from './matrix/make-fixtures.mjs'
import { readWorkbook } from './matrix/xlsx-lite.mjs'

const REPO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const EVAL_ROOT = path.join(os.tmpdir(), 'hris-eval')
const FIXTURES = path.join(EVAL_ROOT, 'fixtures')
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))
const API_ENV = { Ollama__DefaultModel: 'fake-ok', Extraction__DefaultModel: 'fake-ok', Matrix__Model: 'fake-ok' }

/** Headers that must never leave in an export (Matrix §3.4), and the salary ones a user without view-salary must not get. */
const SENSITIVE = /nid|birthcert|bgmea|bank|branch|account|mobile|phone|contact|email|address|village|road|father|mother|spouse|husband|wife|dateofbirth|dob|religion|blood|emerg|pabx/
const SALARY = /gross|basic|salary|wage|houserent|medical|conveyance|food|payable/

// ─────────────────────────────── harness ───────────────────────────────

const checks = []
function check(id, phase, name, fn, { live = false } = {}) { checks.push({ id, phase, name, fn, live }) }
const ok = (pass, detail) => ({ pass: !!pass, detail })
const read = (rel) => fs.readFileSync(path.join(REPO, rel), 'utf8')
const exists = (rel) => fs.existsSync(path.join(REPO, rel))
const ctx = { args: null, uploads: {}, datasets: {} }

async function sql(query, ...params) {
  const { DatabaseSync } = await import('node:sqlite')
  const db = new DatabaseSync(ctx.api.dbPath, { readOnly: true })
  try { return db.prepare(query).all(...params) } finally { db.close() }
}

async function login(username, secret) {
  const r = await call(ctx.api.url, 'POST', '/api/auth/login', { body: { username, secret } })
  if (r.status !== 200 || !r.json?.token) throw new Error(`login ${username} → ${r.status} ${r.text.slice(0, 200)}`)
  const me = await call(ctx.api.url, 'GET', '/api/me/tabs', { token: r.json.token })
  return { username, token: r.json.token, id: me.json?.id }
}
const as = (user, method, p, body) => call(ctx.api.url, method, p, { token: user.token, body })
const asAdmin = (method, p, body) => as(ctx.admin, method, p, body)

async function restart(env = {}) {
  await ctx.api.stop()
  ctx.api = await startApi({ fakeUrl: ctx.fake.url, runDir: ctx.api.runDir, port: ctx.api.port, env: { ...API_ENV, ...env } })
}

async function upload(user, file) {
  const form = new FormData()
  form.append('files', new Blob([fs.readFileSync(file)]), path.basename(file))
  const res = await fetch(`${ctx.api.url}/api/matrix/uploads`, { method: 'POST', headers: { Authorization: `Bearer ${user.token}` }, body: form })
  const text = await res.text()
  if (res.status !== 201) throw new Error(`upload ${path.basename(file)} → ${res.status} ${text.slice(0, 200)}`)
  return JSON.parse(text).uploads[0]
}

/** Imports a fixture's sheets (every suggestion confirmed, as the UI's "Confirm all"); returns the datasets. */
async function importFixture(name, sheets, reportDate) {
  const up = await upload(ctx.admin, path.join(FIXTURES, name))
  const tables = (sheets ?? up.profile.sheets.map((s) => s.name)).map((sheet) => ({
    table: up.profile.sheets.find((s) => s.name === sheet).tables[0].id, confirm: 'suggestions', ...(reportDate ? { reportDate } : {}),
  }))
  const r = await asAdmin('POST', `/api/matrix/uploads/${up.id}/import`, { tables })
  if (r.status !== 200) throw new Error(`import ${name} → ${r.status} ${r.text.slice(0, 200)}`)
  return r.json.datasets
}

/**
 * The data the board reads: the designation lookup, the master, two manpower snapshots (the
 * attrition trend), and the attendance whose people are not in the master (attendance % unavailable).
 */
async function data() {
  if (ctx.data) return ctx.data
  await importFixture('fx-designation.xlsx')
  const master = (await importFixture('fx-master.xlsx', ['HRM', 'unit ', 'Sheet1'])).find((d) => d.sheet === 'HRM')
  await importFixture('fx-manpower.xlsx', ['Sheet1'], ctx.M.manpower2.previousReportDate)
  await importFixture('fx-manpower-2.xlsx', ['Sheet1'], ctx.M.manpower2.reportDate)
  const att = (await importFixture('fx-attendance-3.xlsx', ['rptDailyAttDetailsReport_Factor']))[0]
  ctx.data = { master: master.id, att: att.id }
  return ctx.data
}

/** admin; Executive (DMD Sir); hr-assistant; General. */
async function users() {
  if (ctx.users) return ctx.users
  const secret = ctx.userSecret ??= 'hr-' + crypto.randomBytes(6).toString('hex')
  const u = {}
  for (const [k, name, role] of [['exec', 'hris-exec', 'Executive'], ['hra', 'hris-hr', 'HrAssistant'], ['gen', 'hris-general', 'General']]) {
    u[k] = await login(name, secret)
    if (role !== 'General') {
      const r = await asAdmin('PUT', `/api/admin/rbac/users/${u[k].id}/role`, { role })
      if (r.status !== 200) throw new Error(`role ${role} → ${r.status} ${r.text.slice(0, 200)}`)
    }
  }
  ctx.users = u
  return u
}

const render = async (user, body = {}) => (await as(user, 'POST', '/api/hris/pins/render', { board: 'shared', ...body })).json
const plainRows = (rows) => JSON.stringify((rows ?? []).map((r) => Object.fromEntries(Object.entries(r).filter(([k]) => !k.startsWith('_')))))

async function download(user, url) {
  const res = await fetch(`${ctx.api.url}${url}`, { headers: { Authorization: `Bearer ${user.token}` } })
  return { status: res.status, name: /filename="?([^";]+)/.exec(res.headers.get('content-disposition') ?? '')?.[1], bytes: Buffer.from(await res.arrayBuffer()) }
}

function sheetsOf(bytes, tag) {
  const file = path.join(EVAL_ROOT, `export-${tag}.xlsx`)
  fs.writeFileSync(file, bytes)
  return readWorkbook(file)
}

/** Every header-looking cell: a bold table row's values (the figures tables) and row 1 (Matrix's rows sheet). */
const headersOf = (sheet) => [...sheet.cells.entries()].filter(([ref]) => /^[A-Z]+1$/.test(ref)).map(([, v]) => v)

// ─────────────────────────────── W1 ───────────────────────────────

check('G-W1.1', 'W1', 'no damage: the existing route handlers are unchanged; Matrix only exposes its widget runner (the suites before and after are compared outside this script)', async () => {
  // HrChatEndpoint.cs is the one extracted on purpose (§2: its chat moved into IHrChatService unchanged, proven by hr-eval).
  const handlers = ['backend/MinimalApis/MatrixApis.cs', 'backend/MinimalApis/HrApis.cs',
    'backend/MinimalApis/HrRulebookApis.cs', 'backend/MinimalApis/HrProcedureApis.cs', 'backend/MinimalApis/RbacApis.cs']
  const r = spawnSync('git', ['diff', '--stat', 'HEAD', '--', ...handlers], { cwd: REPO, encoding: 'utf8' })
  const touched = spawnSync('git', ['diff', '--name-only', 'HEAD', '--', 'backend/Services', 'backend/Matrix', 'backend/Hr'], { cwd: REPO, encoding: 'utf8' }).stdout.trim().split(/\r?\n/).filter(Boolean)
  const matrixDiff = spawnSync('git', ['diff', '-U0', 'HEAD', '--', 'backend/Services/MatrixService.Dashboards.cs'], { cwd: REPO, encoding: 'utf8' }).stdout
  const changed = matrixDiff.split('\n').filter((l) => /^[+-]\s/.test(l))
  // Only visibility may change there: private → internal/public on ScopeOf and RunWidgetsAsync.
  const onlyVisibility = changed.every((l) => /ScopeOf\(MxDashboardFilters\? f\)|RunWidgetsAsync\(User user, int id, string name/.test(l))
  const endpoint = read('backend/MinimalApis/HrChatEndpoint.cs')
  const thin = /hr\.AskAsync\(user, req,/.test(endpoint) && !/library.SelectExcerptsAsync|HrNumberCheck.Unverified/.test(endpoint) && exists('backend/Hr/HrChatService.cs')
  return ok(r.stdout.trim() === '' && onlyVisibility && thin && touched.every((f) => /MatrixService\.Dashboards\.cs|IMatrixService\.cs/.test(f)),
    `route handlers changed: ${r.stdout.trim() || 'none'} · extracted: HrChatEndpoint → IHrChatService (Hr/HrChatService.cs), endpoint now HTTP only: ${thin} · services/Matrix/Hr touched: ${touched.join(', ') || 'none'} · MatrixService lines changed: ${changed.length} (visibility only: ${onlyVisibility})`)
})

check('G-W1.2', 'W1', 'flag: Hris:Enabled=false removes the tab and every /api/hris route answers 404', async () => {
  const before = (await asAdmin('GET', '/api/me/access')).json
  await restart({ Hris__Enabled: 'false' })
  ctx.admin = await login(process.env.HRIS_EVAL_ADMIN_USER, process.env.HRIS_EVAL_ADMIN_SECRET)
  const off = (await asAdmin('GET', '/api/me/access')).json
  const routes = [['GET', '/api/hris/pins'], ['POST', '/api/hris/pins/render'], ['GET', '/api/hris/status'], ['GET', '/api/hris/export/board'], ['PUT', '/api/hris/pins/order']]
  const statuses = []
  for (const [m, p] of routes) statuses.push((await asAdmin(m, p, m === 'GET' ? undefined : {})).status)
  await restart()
  ctx.admin = await login(process.env.HRIS_EVAL_ADMIN_USER, process.env.HRIS_EVAL_ADMIN_SECRET)
  const on = (await asAdmin('GET', '/api/me/access')).json
  return ok(before?.tabs?.includes('hris') && !off?.tabs?.includes('hris') && statuses.every((s) => s === 404) && on?.tabs?.includes('hris'),
    `on: tab ${before?.tabs?.includes('hris')} · off: tab ${off?.tabs?.includes('hris')}, routes ${statuses.join(',')} · on again: tab ${on?.tabs?.includes('hris')}`)
})

check('G-W1.3', 'W1', 'RBAC: Executive has Reports and Ask, the status card but not the Hub, and 403 on the shared board; hr-assistant and admin as §3.1; General 403 everywhere', async () => {
  const u = await users()
  const access = async (user) => (await as(user, 'GET', '/api/me/access')).json
  const ex = await access(u.exec), hr = await access(u.hra), ad = await access(ctx.admin)
  const has = (a, k) => [...(a.tabs ?? []).map((t) => 'tab:' + t), ...(a.sections ?? []), ...(a.actions ?? [])].includes(k)
  const KEYS = ['tab:hris', 'section:hris.reports', 'section:hris.ask', 'section:hris.hub', 'action:hris.pin', 'action:hris.export', 'action:hris.debug', 'action:matrix.view-salary', 'action:matrix.view-personal']
  const want = {
    exec: [1, 1, 1, 0, 0, 1, 0, 0, 0], hra: [1, 1, 1, 1, 1, 1, 0, 0, 1], admin: [1, 1, 1, 1, 1, 1, 1, 1, 1],
  }
  const got = { exec: KEYS.map((k) => +has(ex, k)), hra: KEYS.map((k) => +has(hr, k)), admin: KEYS.map((k) => +has(ad, k)) }
  const matrix = Object.keys(want).every((r) => want[r].join() === got[r].join())
  const exTabs = (ex.tabs ?? []).slice().sort().join(',')

  const pin = { board: 'shared', kind: 'kpi', titleEn: 'Test', titleBn: 'পরীক্ষা', payload: { widgets: [{ id: 'a', type: 'kpi', kpi: 'active_headcount', dataset: 'current:master' }] } }
  const exShared = await as(u.exec, 'POST', '/api/hris/pins', pin)
  const exOrder = await as(u.exec, 'PUT', '/api/hris/pins/order', { board: 'shared', ids: [] })
  const seeded = (await as(u.exec, 'GET', '/api/hris/pins?board=shared')).json ?? []
  const exDelete = await as(u.exec, 'DELETE', `/api/hris/pins/${seeded[0]?.id}`)
  const exPersonal = await as(u.exec, 'POST', '/api/hris/pins', { ...pin, board: 'personal' })
  const exStatus = await as(u.exec, 'GET', '/api/hris/status')
  const hrShared = await as(u.hra, 'POST', '/api/hris/pins', pin)
  if (hrShared.json?.id) await as(u.hra, 'DELETE', `/api/hris/pins/${hrShared.json.id}`)
  // Someone else's personal pin is not there for anyone.
  const otherPersonal = await as(u.hra, 'DELETE', `/api/hris/pins/${exPersonal.json?.id}`)
  if (exPersonal.json?.id) await as(u.exec, 'DELETE', `/api/hris/pins/${exPersonal.json.id}`)

  const routes = [['GET', '/api/hris/pins'], ['POST', '/api/hris/pins', pin], ['PUT', '/api/hris/pins/1', {}], ['DELETE', '/api/hris/pins/1'],
    ['PUT', '/api/hris/pins/order', {}], ['POST', '/api/hris/pins/render', {}], ['GET', '/api/hris/status'], ['GET', '/api/hris/export/board']]
  const gen = []
  for (const [m, p, b] of routes) gen.push((await as(u.gen, m, p, b)).status)
  return ok(matrix && exTabs === 'hris,settings' && exShared.status === 403 && exOrder.status === 403 && exDelete.status === 403
      && exPersonal.status === 201 && exStatus.status === 200 && hrShared.status === 201 && otherPersonal.status === 404 && gen.every((s) => s === 403),
    `keys ${KEYS.join(' ')}\n  exec ${got.exec.join('')} (want ${want.exec.join('')}) · hra ${got.hra.join('')} (want ${want.hra.join('')}) · admin ${got.admin.join('')}\n`
    + `  Executive tabs [${exTabs}] · shared add/order/delete ${exShared.status}/${exOrder.status}/${exDelete.status} · personal add ${exPersonal.status} · status card ${exStatus.status}\n`
    + `  hr-assistant shared add ${hrShared.status} · another's personal pin ${otherPersonal.status} · General on every route: ${gen.join(',')}`)
})

check('G-W1.4', 'W1', 'seeding: the shared board starts with the 8 starter pins, and a restart adds none', async () => {
  const before = await sql("select SeedKey from HrisPins where Board = 'shared' and SeedKey is not null order by \"Order\"")
  await restart()
  ctx.admin = await login(process.env.HRIS_EVAL_ADMIN_USER, process.env.HRIS_EVAL_ADMIN_SECRET)
  const after = await sql("select count(*) n from HrisPins where Board = 'shared' and SeedKey is not null")
  const keys = before.map((r) => r.SeedKey)
  return ok(keys.length === 8 && new Set(keys).size === 8 && after[0].n === 8,
    `seeded: ${keys.join(', ')} · after a restart: ${after[0].n}`)
})

check('G-W1.5', 'W1', 'live pins: every chart and KPI pin gives the numbers Matrix gives for the same KPI run or query', async () => {
  await data()
  const u = await users()
  // One query pin too, on hr-assistant's personal board.
  const q = { dataset: 'current:master', filter: [['status', 'in', ['Active']]], group_by: ['section'], measures: [['count', '*', 'people']], limit: 500 }
  const made = await as(u.hra, 'POST', '/api/hris/pins', { board: 'personal', kind: 'chart', titleEn: 'By section', titleBn: 'সেকশন', payload: { widgets: [{ id: 's', type: 'bar', query: q }] } })
  const lines = []
  let pass = made.status === 201, compared = 0
  for (const [user, board] of [[ctx.admin, 'shared'], [u.hra, 'personal']]) {
    const r = await render(user, { board })
    for (const pin of (r?.pins ?? []).filter((p) => p.widgets)) {
      for (const w of pin.widgets) {
        let same
        if (w.kpi) {
          const res = await as(user, 'POST', `/api/matrix/kpis/${w.kpi}/run`, w.input ?? { dataset: w.datasetId ? String(w.datasetId) : undefined })
          const m = res.json
          // A KPI Matrix cannot run here: the pin carries Matrix's own refusal, and no number.
          same = w.error ? res.status !== 200 && w.value === null : m && m.value === w.value && plainRows(m.rows) === plainRows(w.rows)
          if (w.trend?.length) for (const t of w.trend) {
            const p = (await as(user, 'POST', `/api/matrix/kpis/${w.kpi}/run`, { ...w.input, dataset: String(t.datasetId) })).json
            same &&= p?.value === t.value
          }
        } else {
          const m = (await as(user, 'POST', '/api/matrix/query', { query: w.query })).json
          same = m && plainRows(m.rows) === plainRows(w.rows)
        }
        compared++
        pass &&= !!same
        lines.push(`${board} pin ${pin.id} ${w.kpi ?? 'query'}: ${w.value ?? `${w.rows.length} rows`}${w.trend?.length ? ` (trend ${w.trend.length})` : ''} ${same ? '=' : '≠ Matrix'}`)
      }
    }
  }
  return ok(pass && compared >= 10, `${compared} widgets compared\n  ` + lines.join('\n  '))
})

check('G-W1.6', 'W1', 'filters: a unit filter changes every pin with a unit; a pin without the field says "not applicable"; the URL keeps the filters', async () => {
  await data()
  const plain = await render(ctx.admin)
  const units = plain?.filterOptions?.unit ?? []
  const unit = units[units.length - 1]
  const f = await render(ctx.admin, { filters: { unit: [unit] } })
  const out = []
  let pass = units.length > 1, changedCount = 0
  for (const p of f?.pins ?? []) {
    const p0 = plain.pins.find((x) => x.id === p.id)
    const na = p.notApplicable.includes('unit')
    if (!p.widgets?.length) {
      // Cards, summaries and answers are never filtered: they say so.
      pass &&= na
      out.push(`pin ${p.id} ${p.kind}: unit n/a${na ? ', badge' : ', NO BADGE'}`)
      continue
    }
    for (const w of p.widgets) {
      const w0 = p0.widgets.find((x) => x.id === w.id)
      const applied = w.filtersApplied.includes('unit')
      const show = (x) => JSON.stringify([x.value, plainRows(x.rows), (x.trend ?? []).map((t) => t.value)])
      const changed = show(w) !== show(w0)
      // A widget with the field gives exactly Matrix's own filtered numbers (most change; one whose every unit has the
      // same value — lines 1–53 in each fixture unit — may not); one without the field is badged.
      const empty = w.value === null && !w.rows.length
      const m = applied && w.kpi ? (await asAdmin('POST', `/api/matrix/kpis/${w.kpi}/run`, w.input)).json : null
      const matrixSame = !applied || !w.kpi || (w.input?.scope?.some((x) => x.key === 'unit') && m?.value === w.value && plainRows(m?.rows) === plainRows(w.rows))
      const good = applied ? matrixSame : w.filtersIgnored.includes('unit') && na
      if (applied && changed) changedCount++
      pass &&= good
      out.push(`pin ${p.id} ${w.kpi ?? 'query'}: unit ${applied ? `applied, ${changed ? 'changed' : empty ? 'nothing to show' : 'same value'}, = Matrix's filtered run ${matrixSame}` : na ? 'n/a, badge' : 'n/a, NO BADGE'}`)
    }
  }
  // The URL: a link with the filter opens the board filtered.
  const browser = await launchBrowser({ base: ctx.api.url, profileRoot: EVAL_ROOT })
  let fromUrl
  try {
    const p = await browser.page()
    await p.session((await users()).exec, 'light', 1280, 900, `/hris?f_unit=${encodeURIComponent(unit)}`)
    await p.waitFor('[data-hris-reports] [data-hris-pin]', 20000)
    await sleep(2500)
    fromUrl = await p.eval(`({ select: document.querySelector('[data-hris-filter="unit"]')?.value, badges: document.querySelectorAll('[data-hris-na]').length,
      tile: document.querySelector('[data-pin-kind="kpi"] [data-widget]')?.textContent?.replace(/\\s+/g, ' ').slice(0, 80) })`)
  } finally { await browser.close() }
  const headcount = f.pins.find((p) => p.widgets?.[0]?.kpi === 'active_headcount')?.widgets[0].value
  const url = fromUrl.select === unit && fromUrl.badges >= 3
  pass &&= changedCount >= 5
  return ok(pass && url, `unit "${unit}" of ${units.length} · opened from the URL: filter bar "${fromUrl.select}", ${fromUrl.badges} not-applicable badges, headcount tile "${fromUrl.tile}" (filtered value ${headcount})\n  ${out.join('\n  ')}`)
})

check('G-W1.7', 'W1', 'unavailable data: with the attendance file sharing no IDs with the master, the attendance % pin gives the card\'s reason and no number', async () => {
  const { att } = await data()
  const card = (await asAdmin('GET', `/api/matrix/datasets/${att}/card`)).json
  const reason = card?.unavailable?.find((x) => x.key === 'attendance_pct')?.reason
  const r = await render(ctx.admin)
  const pin = r.pins.find((p) => (p.widgets ?? []).some((w) => w.kpi === 'attendance_pct'))
  const w = pin?.widgets.find((x) => x.kpi === 'attendance_pct')
  const na = pin?.unavailable.find((x) => x.kpi === 'attendance_pct')
  const late = pin?.widgets.find((x) => x.kpi === 'late_pct')
  return ok(reason && na?.reason === reason && w?.value === null && !(w?.rows?.length) && late?.value !== null,
    `card: "${reason}" · pin ${pin?.id}: value ${w?.value}, reason "${na?.reason}" · late % on the same pin still ${late?.value}`)
})

check('G-W1.8', 'W1', 'exports: pin .xlsx (figures and, with Matrix data access, the rows), board .xlsx one sheet per pin; no sensitive columns; no salary without view-salary', async () => {
  await data()
  const u = await users()
  const pins = (await asAdmin('GET', '/api/hris/pins?board=shared')).json
  const byUnit = pins.find((p) => p.titleEn === 'Headcount by unit')
  const out = []
  let pass = true
  for (const [name, user] of [['admin', ctx.admin], ['hr-assistant', u.hra], ['Executive', u.exec]]) {
    const one = await download(user, `/api/hris/export/pin?pin=${byUnit.id}`)
    const board = await download(user, `/api/hris/export/board?board=shared&filters=${encodeURIComponent(JSON.stringify({ unit: null }))}`)
    const s1 = one.status === 200 ? sheetsOf(one.bytes, `${name}-pin`) : []
    const s2 = board.status === 200 ? sheetsOf(board.bytes, `${name}-board`) : []
    const rows = s1.find((s) => s.name === 'Rows')
    const headers = rows ? headersOf(rows) : []
    const norm = headers.map((h) => h.replace(/[^a-z]/gi, '').toLowerCase())
    const sensitive = norm.filter((h) => SENSITIVE.test(h))
    const salary = norm.filter((h) => SALARY.test(h))
    const good = one.status === 200 && board.status === 200 && s2.length === pins.length && sensitive.length === 0
      && (name === 'admin' || salary.length === 0) && (name === 'Executive' ? !rows : !!rows)
    pass &&= good
    out.push(`${name}: pin ${one.status} sheets [${s1.map((s) => s.name).join(', ')}] · board ${board.status} ${s2.length} sheets [${s2.map((s) => s.name).join(', ')}]`
      + `\n    rows headers: ${headers.length ? headers.join(' | ') : '(no rows sheet: no Matrix data access)'} · sensitive ${sensitive.length} · salary ${salary.length}${good ? '' : ' ✗'}`)
  }
  const gen = await download(u.gen, `/api/hris/export/board`)
  pass &&= gen.status === 403
  // PNG and PDF are the browser's: the widget's own ECharts PNG button and the print view (screenshots in G-W1.9).
  return ok(pass, out.join('\n  ') + `\n  General: ${gen.status}`)
})

check('G-W1.9', 'W1', 'UI: the Reports tab, light and dark, 360 px and desktop, as Executive and as hr-assistant; the print view (screenshots)', async () => {
  await data()
  const u = await users()
  const shots = ctx.args.shots
  fs.mkdirSync(shots, { recursive: true })
  const browser = await launchBrowser({ base: ctx.api.url, profileRoot: EVAL_ROOT })
  const out = []
  let pass = true
  try {
    for (const [who, user] of [['executive', u.exec], ['hr', u.hra]]) {
      for (const theme of ['light', 'dark']) {
        for (const width of [360, 1280]) {
          const p = await browser.page()
          await p.session(user, theme, width, width < 600 ? 780 : 900, '/hris')
          await p.waitFor('[data-hris-reports] [data-hris-pin]', 20000)
          await sleep(1500)
          const m = await p.eval(`({ pins: document.querySelectorAll('[data-hris-pin]').length, scroll: document.documentElement.scrollWidth > window.innerWidth + 1,
            edit: document.querySelectorAll('[data-hris-pin-drag]').length, hub: !!document.querySelector('[data-hris-tab="hub"]'), na: document.querySelectorAll('[data-hris-unavailable]').length,
            charts: [...document.querySelectorAll('[data-hris-pin] [data-matrix-chart][data-chart-ready]')].filter((e) => e.getBoundingClientRect().height > 120).length })`)
          const file = path.join(shots, `hris-reports-${who}-${theme}-${width}.png`)
          await p.shot(file, { full: true })
          const good = m.pins >= 8 && !m.scroll && (who === 'executive' ? m.edit === 0 : m.edit > 0) && m.na >= 1 && m.charts >= 3
          pass &&= good
          out.push(`${who} ${theme} ${width}: ${m.pins} pins, horizontal scroll ${m.scroll}, drag handles ${m.edit}, unavailable notes ${m.na}, charts drawn ${m.charts}${good ? '' : ' ✗'} → ${path.basename(file)}`)
        }
      }
    }
    const p = await browser.page()
    await p.session(u.exec, 'light', 1280, 900, '/hris?print=1')
    await p.waitFor('[data-hris-print] [data-hris-pin]', 20000)
    await sleep(1500)
    const head = await p.eval(`document.querySelector('[data-hris-print-head]')?.textContent ?? ''`)
    await p.shot(path.join(shots, 'hris-print-preview.png'), { full: true })
    const pdf = await p.pdf()
    fs.writeFileSync(path.join(shots, 'hris-board.pdf'), pdf)
    const good = /Mostisko · HRIS Workspace/.test(head) && pdf.length > 10_000
    pass &&= good
    out.push(`print view header "${head.replace(/\s+/g, ' ').trim().slice(0, 140)}" · PDF ${pdf.length} bytes → hris-board.pdf`)
  } finally { await browser.close() }
  return ok(pass, `${shots}\n  ` + out.join('\n  '))
})

check('G-W1.10', 'W1', 'dotnet build: 0 warnings, 0 errors; npm run build is clean', async () => {
  const d = spawnSync('dotnet', ['build', 'backend/RabbitIC.Api.csproj', '-nologo', '-v', 'q', '--no-incremental'], { cwd: REPO, encoding: 'utf8', shell: process.platform === 'win32' })
  const warn = /(\d+) Warning\(s\)/.exec(d.stdout)?.[1], err = /(\d+) Error\(s\)/.exec(d.stdout)?.[1]
  const n = spawnSync('npm', ['run', 'build'], { cwd: path.join(REPO, 'frontend'), encoding: 'utf8', shell: process.platform === 'win32' })
  return ok(d.status === 0 && warn === '0' && err === '0' && n.status === 0,
    `dotnet ${d.status}: ${warn} warning(s), ${err} error(s) · npm run build ${n.status}${n.status ? ' ' + (n.stderr || n.stdout).slice(-300) : ''}`)
})

// ─────────────────────────────── runner ───────────────────────────────

function parseArgs(argv) {
  const a = { phase: 'W1', only: null, build: true, live: false, shots: path.join(EVAL_ROOT, 'shots') }
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === '--phase') a.phase = argv[++i]
    else if (argv[i] === '--only') a.only = argv[++i]
    else if (argv[i] === '--no-build') a.build = false
    else if (argv[i] === '--live') a.live = true
    else if (argv[i] === '--shots') a.shots = path.resolve(argv[++i])
  }
  return a
}

function syncWebRoot() {
  const dist = path.join(REPO, 'frontend', 'dist')
  const target = path.join(API_BIN_DIR, 'wwwroot')
  fs.rmSync(target, { recursive: true, force: true })
  if (fs.existsSync(dist)) fs.cpSync(dist, target, { recursive: true })
}

async function main() {
  const args = ctx.args = parseArgs(process.argv.slice(2))
  if (!process.env.HRIS_EVAL_ADMIN_USER || !process.env.HRIS_EVAL_ADMIN_SECRET) {
    console.error('Set HRIS_EVAL_ADMIN_USER and HRIS_EVAL_ADMIN_SECRET to the seeded administrator.')
    process.exit(2)
  }
  const order = ['W1', 'W2', 'W3', 'W4']
  const selected = checks.filter((c) => order.indexOf(c.phase) <= order.indexOf(args.phase) && (args.live || !c.live)
    && (!args.only || c.id === args.only || c.id.startsWith(args.only + '.')))
  if (!selected.length) { console.error('No checks match.'); process.exit(2) }

  ctx.M = makeFixtures(FIXTURES)
  if (args.build) {
    buildApi()
    // The UI gates need the current frontend in the API's web root.
    const n = spawnSync('npm', ['run', 'build'], { cwd: path.join(REPO, 'frontend'), encoding: 'utf8', shell: process.platform === 'win32' })
    if (n.status !== 0) throw new Error('npm run build failed:\n' + (n.stderr || n.stdout).slice(-800))
  }
  syncWebRoot()
  const runDir = fs.mkdtempSync(path.join(EVAL_ROOT, 'run-'))
  ctx.fake = await startFakeOllama()
  let failed = 0
  try {
    ctx.api = await startApi({ fakeUrl: ctx.fake.url, runDir, env: API_ENV })
    ctx.admin = await login(process.env.HRIS_EVAL_ADMIN_USER, process.env.HRIS_EVAL_ADMIN_SECRET)
    console.log(`fake ${ctx.fake.url} · api ${ctx.api.url} · db ${ctx.api.dbPath}${args.live ? ' · LIVE: counts only' : ''}\n`)
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
