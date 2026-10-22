#!/usr/bin/env node
// Matrix eval — see docs/MATRIX_PROMPT.md §4–§6 (Phases M1–M3) and §8.
//
//   node scripts/matrix-eval.mjs --phase M1            every M1 check on the synthetic fixtures
//   node scripts/matrix-eval.mjs --phase M1 --live     also the live gates on matrix-samples/ (counts only)
//   node scripts/matrix-eval.mjs --only G-M1.6         one gate
//   node scripts/matrix-eval.mjs --no-build            reuse the last eval build
//   node scripts/matrix-eval.mjs --shots <dir>         where G-M1.9, G-M3 and G-M4 write their screenshots
//   node scripts/matrix-eval.mjs --model <name>        the real model the LIVE M4 checks use (default gemma4:12b)
//   node scripts/matrix-eval.mjs --golden all          every golden question (default: the ten of Step 3)
//
// Environment: MATRIX_EVAL_ADMIN_USER, MATRIX_EVAL_ADMIN_SECRET (the seeded
// administrator; never written here).
//
// Fixtures are synthetic (scripts/matrix/make-fixtures.mjs). The real files are
// read only by --live checks and by G-M1.2's "no real ID in the fixtures"
// comparison, and nothing from them is ever printed except counts and column
// names. The fake Ollama, the API start-up and the headless browser are shared
// with the other suites. Node 22+, no dependencies.

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
import { scanRepo, scanText } from './lib/config-secrets.mjs'

const REPO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const EVAL_ROOT = path.join(os.tmpdir(), 'matrix-eval')
const FIXTURES = path.join(EVAL_ROOT, 'fixtures')
const SAMPLES = path.join(REPO, 'matrix-samples')
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))
/** The settings every start of the eval API gets — a restart must not drop them (the fake's model). */
const API_ENV = { Ollama__DefaultModel: 'fake-ok', Extraction__DefaultModel: 'fake-ok', Matrix__Model: 'fake-ok' }

const REAL = {
  master: 'ADL-MASTER_FILE-HRM-Extended.xlsx',
  attendance: 'DailyAttDetailsReport_24.xlsx',
  designation: 'Designation_Equivalency_List.xlsx',
  hrBasic: 'HR-basic-details-from-2021.xlsx',
  manpower: 'Manpower-HR-AF-PE.xlsx',
}

/** Headers that must never become a dataset column (§3.4), as patterns over normalised names. */
const SENSITIVE_COLUMN = /nid|birthcert|bgmea|bank|branch|account|mobile|phone|contact|email|address|village|road|father|mother|spouse|husband|wife|dateofbirth|dob|religion|blood|emerg|pabx/

/** Every dataset column whose SOURCE HEADER names sensitive data — unmapped columns are col_<letter>, so names alone would hide them. */
async function sensitiveByHeader() {
  const list = (await asAdmin('GET', '/api/matrix/datasets')).json?.datasets ?? []
  return list.flatMap((d) => d.columns.filter((c) => !c.derived && SENSITIVE_COLUMN.test(c.header.replace(/[^a-z]/gi, '').toLowerCase()))
    .map((c) => `${d.sheet.trim()}.${c.key} (${c.header})`))
}

// ─────────────────────────────── harness ───────────────────────────────

const checks = []
function check(id, phase, name, fn, { live = false } = {}) { checks.push({ id, phase, name, fn, live }) }
const ok = (pass, detail) => ({ pass: !!pass, detail })
const show = (v) => (Array.isArray(v) && v.every((x) => x === null || typeof x !== 'object') ? `[${v.join(',')}]` : JSON.stringify(v))
const read = (rel) => fs.readFileSync(path.join(REPO, rel), 'utf8')
const exists = (rel) => fs.existsSync(path.join(REPO, rel))
const ctx = { args: null, M: null, uploads: {}, datasets: {} }

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

async function restart() {
  await ctx.api.stop()
  ctx.api = await startApi({ fakeUrl: ctx.fake.url, runDir: ctx.api.runDir, port: ctx.api.port, env: API_ENV })
}

/** Upload one or more files as multipart; returns the parsed body. */
async function upload(user, files) {
  const form = new FormData()
  for (const f of files) form.append('files', new Blob([fs.readFileSync(f)]), path.basename(f))
  const res = await fetch(`${ctx.api.url}/api/matrix/uploads`, { method: 'POST', headers: { Authorization: `Bearer ${user.token}` }, body: form })
  const text = await res.text()
  let json = null
  try { json = JSON.parse(text) } catch { /* not JSON */ }
  return { status: res.status, json, text }
}

/** Uploads a fixture once per run (cached), with the admin. */
async function fixtureUpload(name) {
  if (ctx.uploads[name]) return ctx.uploads[name]
  const r = await upload(ctx.admin, [path.join(FIXTURES, name)])
  if (r.status !== 201) throw new Error(`upload ${name} → ${r.status} ${r.text.slice(0, 300)}`)
  ctx.uploads[name] = r.json.uploads[0]
  return ctx.uploads[name]
}

const tableOf = (up, sheet) => up.profile.sheets.find((s) => s.name === sheet)?.tables?.[0]

/** Imports a fixture's chosen sheets with every suggestion confirmed (what the UI's "Confirm all" sends). */
async function fixtureImport(name, sheets) {
  const key = `${name}|${sheets?.join(',') ?? '*'}`
  if (ctx.datasets[key]) return ctx.datasets[key]
  // The designation lookup goes in first: the designation check compares against the current lookup at import.
  if (name !== 'fx-designation.xlsx') await fixtureImport('fx-designation.xlsx')
  const up = await fixtureUpload(name)
  const tables = (sheets ?? up.profile.sheets.map((s) => s.name)).map((sheet) => ({ table: tableOf(up, sheet).id, confirm: 'suggestions' }))
  const r = await asAdmin('POST', `/api/matrix/uploads/${up.id}/import`, { tables })
  if (r.status !== 200) throw new Error(`import ${name} → ${r.status} ${r.text.slice(0, 300)}`)
  ctx.datasets[key] = r.json
  return r.json
}

/** Every row of a dataset through the rows API (the viewer's route), paged. */
async function allRows(user, datasetId, query = '') {
  const out = []
  for (let offset = 0; ; offset += 2000) {
    const r = await as(user, 'GET', `/api/matrix/datasets/${datasetId}/rows?offset=${offset}&limit=2000${query}`)
    if (r.status !== 200) throw new Error(`rows ${datasetId} → ${r.status} ${r.text.slice(0, 200)}`)
    out.push(...r.json.rows)
    if (out.length >= r.json.total || r.json.rows.length === 0) return { columns: r.json.columns, rows: out, total: r.json.total }
  }
}

const datasetOf = (imp, sheet) => imp.datasets.find((d) => d.sheet === sheet)

// ═══════════════════════════════ Phase M1 ═══════════════════════════════

// ---- G-M1.1 Layers, tab and permissions ----

check('G-M1.1', 'M1', 'the §3.1 files of M1 exist, in the existing layers', async () => {
  const files = ['backend/Models/Matrix.cs', 'backend/Repositories/MatrixRepository.cs', 'backend/Matrix/WorkbookProfiler.cs',
    'backend/Matrix/FieldCatalog.cs', 'backend/Matrix/TemplateMatcher.cs', 'backend/Matrix/DatasetImporter.cs',
    'backend/Matrix/QualityChecks.cs', 'backend/Services/IMatrixService.cs', 'backend/Services/MatrixService.cs',
    'backend/MinimalApis/MatrixApis.cs', 'backend/DTOs/MatrixDtos.cs', 'frontend/src/services/matrix.service.js',
    'frontend/src/components/tabs/MatrixTab.vue', 'frontend/src/components/matrix/MatrixUpload.vue',
    'frontend/src/components/matrix/MatrixMapping.vue', 'frontend/src/components/matrix/MatrixDatasets.vue',
    'frontend/src/components/matrix/MatrixViewer.vue', 'frontend/src/components/matrix/MatrixQuality.vue',
    'frontend/src/lib/matrix-format.js', 'scripts/matrix/make-fixtures.mjs']
  const missing = files.filter((f) => !exists(f))
  const reg = /AddScoped<RabbitIC\.Api\.Services\.IMatrixService,\s*RabbitIC\.Api\.Services\.MatrixService>/.test(read('backend/Program.cs'))
  const group = /MapGroup\("\/api"\)\.WithTags\("Matrix"\)[\s\S]{0,80}MapMatrixApis\(\)/.test(read('backend/Middleware/MiddlewareDependencyInversion.cs'))
  const barrel = /from '\.\/matrix\.service\.js'/.test(read('frontend/src/services/index.js'))
  return ok(!missing.length && reg && group && barrel,
    `${files.length - missing.length}/${files.length} files${missing.length ? ` · missing ${show(missing)}` : ''} · service registered ${reg} · route group ${group} · barrel ${barrel}`)
})

check('G-M1.1', 'M1', 'the matrix tab: SystemAdmin, System and hr-assistant have it, General and DevOps not; its routes 403 without it', async () => {
  const u = await users()
  const has = async (user) => ((await as(user, 'GET', '/api/me/access')).json?.tabs ?? []).includes('matrix')
  const got = { admin: await has(ctx.admin), sys: await has(u.sys), hra: await has(u.hra), gen: await has(u.gen), dev: await has(u.dev) }
  const st = [(await as(u.gen, 'GET', '/api/matrix/datasets')).status, (await as(u.dev, 'GET', '/api/matrix/uploads')).status,
    (await as(u.gen, 'GET', '/api/matrix/catalog')).status]
  const label = ((await as(ctx.admin, 'GET', '/api/me/access')).json?.modules ?? []).find((m) => m.key === 'matrix')?.label
  return ok(got.admin && got.sys && got.hra && !got.gen && !got.dev && st.every((s) => s === 403) && label === 'ম্যাট্রিক্স · Matrix',
    `${show(got)} · General/DevOps on /api/matrix ${show(st)} · label "${label}"`)
})

check('G-M1.1', 'M1', 'the action keys: upload and view-personal for System, not view-salary; fix and templates SystemAdmin only', async () => {
  const u = await users()
  const acts = async (user) => (await as(user, 'GET', '/api/me/access')).json?.actions ?? []
  const sys = await acts(u.sys), hra = await acts(u.hra), admin = await acts(ctx.admin)
  const want = ['action:matrix.upload', 'action:matrix.dashboards.edit', 'action:matrix.view-personal']
  const not = ['action:matrix.view-salary', 'action:matrix.fix', 'action:matrix.templates', 'action:matrix.kpis.edit']
  const all = [...want, ...not]
  return ok(want.every((k) => sys.includes(k) && hra.includes(k)) && not.every((k) => !sys.includes(k) && !hra.includes(k))
      && all.every((k) => admin.includes(k)),
    `System ${show(sys.filter((k) => k.includes('matrix')))} · SystemAdmin has all ${all.every((k) => admin.includes(k))}`)
})

// ---- G-M1.2 Fixtures ----

check('G-M1.2', 'M1', 'make-fixtures.mjs writes the workbooks, each quirk of §2.2 present', async () => {
  const M = ctx.M
  const files = Object.values(M).map((m) => m.file)
  const missing = files.filter((f) => !fs.existsSync(path.join(FIXTURES, f)))
  const sheets = (f) => readWorkbook(path.join(FIXTURES, f))
  const master = sheets('fx-master.xlsx'), att = sheets('fx-attendance.xlsx'), hr = sheets('fx-hr-basic.xlsx'), mp = sheets('fx-manpower.xlsx')
  const col = (sheet, header) => {
    const letter = [...sheet.cells].find(([ref, v]) => /^[A-Z]+1$/.test(ref) && v === header)?.[0]?.replace(/\d+$/, '')
    return [...sheet.cells].filter(([ref]) => ref.replace(/\d+$/, '') === letter && ref !== `${letter}1`).map(([, v]) => v)
  }
  const hrm = master.find((s) => s.name === 'HRM')
  const zipText = (f) => fs.readFileSync(path.join(FIXTURES, f)).length
  const quirks = {
    'merged title rows': /mergeCell ref="A1:K1"/.test(await xmlOf('fx-attendance.xlsx', 'xl/worksheets/sheet1.xml')),
    'date range line': [...att[0].cells.values()].includes('(19-May-2026 To 19-May-2026)'),
    'text dates': col(hrm, 'JoiningDate').some((v) => /^\d{2}-[A-Z][a-z]{2}-\d{4}$/.test(v)) && col(hrm, 'JoiningDate').some((v) => / 00:00:00$/.test(v)),
    'leading zeros': col(hrm, 'EmpID').some((v) => /^0\d{5}$/.test(v)),
    NULL: col(hr[0], 'SectionName').includes('NULL'),
    'duplicate sheets': ['HRM', 'HRM (2)', 'For corrcetion', 'unit ', 'Sheet1'].every((n) => master.some((s) => s.name === n)),
    'LINE - n in WingName': col(hrm, 'WingName').some((v) => /^LINE - \d+$/.test(v)),
    'formulas with cached values': /<f>\(R2-2450\)\/1\.5<\/f><v>/.test(await xmlOf('fx-master.xlsx', 'xl/worksheets/sheet5.xml')),
    'Bengali columns': col(mp[0], 'UnitNameBan').some((v) => /[ঀ-৿]/.test(v)),
    'sensitive columns': ['NIDNo', 'Mobile', 'FathersName', 'DateOfBirth'].every((h) => col(hrm, h).length > 0),
    'times with a trailing space': [...att[0].cells.values()].some((v) => /^\d{2}:\d{2}:\d{2} [AP]M $/.test(v)),
    'header on row 3': att[0].cells.get('B3') === 'Emp ID',
  }
  const absent = Object.entries(quirks).filter(([, v]) => !v).map(([k]) => k)
  return ok(!missing.length && !absent.length && zipText('fx-master.xlsx') > 0,
    `${files.length} workbooks · quirks present ${Object.keys(quirks).length - absent.length}/${Object.keys(quirks).length}${absent.length ? ` · missing ${show(absent)}` : ''}`)
})

async function xmlOf(file, part) {
  // The reader keeps parts private; a second unzip here is cheap and keeps the helper small.
  const { default: zlib } = await import('node:zlib')
  const buf = fs.readFileSync(path.join(FIXTURES, file))
  let eocd = buf.length - 22
  while (buf.readUInt32LE(eocd) !== 0x06054b50) eocd--
  let p = buf.readUInt32LE(eocd + 16)
  for (let i = 0; i < buf.readUInt16LE(eocd + 10); i++) {
    const nameLen = buf.readUInt16LE(p + 28), extra = buf.readUInt16LE(p + 30), comment = buf.readUInt16LE(p + 32)
    const name = buf.toString('utf8', p + 46, p + 46 + nameLen)
    if (name === part) {
      const at = buf.readUInt32LE(p + 42), size = buf.readUInt32LE(p + 20)
      const start = at + 30 + buf.readUInt16LE(at + 26) + buf.readUInt16LE(at + 28)
      return zlib.inflateRawSync(buf.subarray(start, start + size)).toString('utf8')
    }
    p += 46 + nameLen + extra + comment
  }
  return ''
}

check('G-M1.2', 'M1', 'no real employee ID appears in the generator or the fixtures (compared, never printed)', async () => {
  const real = new Set()
  for (const f of Object.values(REAL)) {
    const file = path.join(SAMPLES, f)
    if (!fs.existsSync(file)) return ok(false, `matrix-samples/${f} is missing — cannot compare`)
    for (const sheet of readWorkbook(file)) {
      const cells = [...sheet.cells]
      for (const [ref, v] of cells) {
        if (!/^[A-Z]+[1-5]$/.test(ref) || !/^(emp ?id|employee ?id|empcode|emp code)$/i.test(String(v).trim())) continue
        const letter = ref.replace(/\d+$/, '')
        const headerRow = Number(ref.slice(letter.length))
        for (const [r2, v2] of cells)
          if (r2.replace(/\d+$/, '') === letter && Number(r2.slice(letter.length)) > headerRow && /^\d{3,}$/.test(String(v2).trim())) real.add(String(v2).trim())
      }
    }
  }
  const source = read('scripts/matrix/make-fixtures.mjs')
  const fixtureIds = new Set()
  for (const f of fs.readdirSync(FIXTURES).filter((f) => f.endsWith('.xlsx')))
    for (const sheet of readWorkbook(path.join(FIXTURES, f))) for (const v of sheet.cells.values()) if (/^\d{4,6}$/.test(v)) fixtureIds.add(v)
  // Three real IDs, chosen by the eval, searched for in the generator (the gate's grep) — and then every one of them.
  const three = [...real].filter((id) => id.length >= 4).slice(0, 3)
  const grep = three.map((id) => spawnSync('git', ['grep', '-c', '--no-index', '-F', id, '--', 'scripts/matrix/make-fixtures.mjs'], { cwd: REPO, encoding: 'utf8' }).status)
  const inSource = [...real].filter((id) => id.length >= 4 && source.includes(id)).length
  const inFixtures = [...real].filter((id) => id.length >= 6 && fixtureIds.has(id)).length
  return ok(real.size > 0 && three.length === 3 && grep.every((s) => s === 1) && inSource === 0 && inFixtures === 0,
    `${real.size} real IDs read · grep of 3 of them in make-fixtures.mjs: exit ${show(grep)} (1 = no match) · in the generator ${inSource} · six-digit IDs found in the fixtures ${inFixtures}`)
})

// ---- G-M1.3 Profiling ----

check('G-M1.3', 'M1', 'profiling the fixtures: sheets, ranges, header rows and row counts', async () => {
  const M = ctx.M
  const master = await fixtureUpload('fx-master.xlsx')
  const bad = []
  for (const [sheet, want] of Object.entries(M.master.sheets)) {
    const t = tableOf(master, sheet)
    if (!t || t.dataRows !== want.rows || t.columns.length !== want.cols || t.headerRows[0] !== 1) bad.push(`${sheet}: ${t?.dataRows}×${t?.columns?.length} header ${t?.headerRows}`)
  }
  const att = tableOf(await fixtureUpload('fx-attendance.xlsx'), 'rptDailyAttDetailsReport_Factor')
  if (att.headerRows[0] !== 3 || att.dataRows !== M.attendance.rows || att.range !== 'A3:K679') bad.push(`attendance ${att.range} header ${att.headerRows} rows ${att.dataRows}`)
  const des = tableOf(await fixtureUpload('fx-designation.xlsx'), 'Sheet1')
  const hr = tableOf(await fixtureUpload('fx-hr-basic.xlsx'), 'Sheet1')
  const mp = tableOf(await fixtureUpload('fx-manpower.xlsx'), 'Sheet1')
  for (const [n, t, m] of [['designation', des, M.designation], ['hr-basic', hr, M.hrBasic], ['manpower', mp, M.manpower]])
    if (t.dataRows !== m.rows || t.columns.length !== m.cols) bad.push(`${n} ${t.dataRows}×${t.columns.length}`)
  return ok(!bad.length, bad.length ? bad.join(' | ') : `master 5 sheets · attendance A3:K679 header row 3 · designation ${des.dataRows}×3 · hr-basic ${hr.dataRows}×12 · manpower ${mp.dataRows}×59`)
})

check('G-M1.3', 'M1', 'the attendance title and date range become report details, report date 2026-05-19', async () => {
  const up = await fixtureUpload('fx-attendance.xlsx')
  const t = tableOf(up, 'rptDailyAttDetailsReport_Factor')
  return ok(t.details?.includes('Daily Attendance Details Report') && t.details?.some((d) => d.includes('19-May-2026'))
      && t.reportDate === '2026-05-19' && up.reportDate === '2026-05-19' && t.kind === 'attendance-daily',
    `details ${show(t.details)} · table ${t.reportDate} · upload ${up.reportDate} · kind ${t.kind}`)
})

check('G-M1.3', 'M1', "Sheet1's formula columns use cached values and list their formulas; a two-row header is joined", async () => {
  const s1 = tableOf(await fixtureUpload('fx-master.xlsx'), 'Sheet1')
  const basic = s1.columns.find((c) => c.header === 'Basic')
  const rent = s1.columns.find((c) => c.header === 'House Rent')
  const mh = tableOf(await fixtureUpload('fx-multiheader.xlsx'), 'Machines')
  return ok(basic?.formula === '(R2-2450)/1.5' && basic.formulaCount === 1168 && basic.type === 'real' && rent?.formula === 'S2*0.5'
      && mh.columns.map((c) => c.header).join('|') === ctx.M.multiHeader.headers.join('|') && mh.dataRows === 3,
    `Basic ${basic?.type} formula ${basic?.formula} ×${basic?.formulaCount} · House Rent ${rent?.formula} · two-row header ${show(mh.columns.map((c) => c.header))}`)
})

// ---- G-M1.4 Mapping ----

check('G-M1.4', 'M1', 'at least 90% of columns mapped by synonyms on every fixture table', async () => {
  const rows = []
  for (const f of ['fx-master.xlsx', 'fx-attendance.xlsx', 'fx-designation.xlsx', 'fx-hr-basic.xlsx', 'fx-manpower.xlsx']) {
    const up = await fixtureUpload(f)
    for (const s of up.profile.sheets) for (const t of s.tables) {
      const n = t.columns.length
      const mapped = t.columns.filter((c) => c.synonym || c.sensitivity === 'sensitive').length
      rows.push({ f, sheet: s.name, pct: Math.round((mapped / n) * 100) })
    }
  }
  const low = rows.filter((r) => r.pct < 90 && r.sheet !== 'Sheet1')
  return ok(!low.length, rows.map((r) => `${r.f.replace('fx-', '').replace('.xlsx', '')}/${r.sheet} ${r.pct}%`).join(' · '))
})

check('G-M1.4', 'M1', 'unmatched headers go to the model: schema JSON, masked samples, awaiting confirmation', async () => {
  const before = ctx.fake.requests.length
  delete ctx.uploads['fx-master.xlsx']
  const up = await fixtureUpload('fx-master.xlsx')
  const sent = ctx.fake.since(before).filter((r) => r.path === '/api/chat')
  const body = sent[0]?.body
  const text = JSON.stringify(body ?? {})
  const hrm = tableOf(up, 'HRM')
  const mc = hrm.columns.find((c) => c.header === 'MachineCode')
  // A real value from that column ("MC-123") must not be in the request; its masked shape must.
  const real = /MC-\d{3}/.test(text)
  const masked = /XX-999/.test(text)
  return ok(sent.length === 1 && body?.format?.type === 'object' && body?.options?.temperature === 0 && body?.think === false
      && text.includes('MachineCode') && !real && masked && !/LINE - \d/.test(text)
      && mc?.suggestion?.source === 'model' && mc.suggestion.confirmed === false,
    `${sent.length} model call(s) · format ${body?.format?.type} · temperature ${body?.options?.temperature} · raw sample sent ${real} · masked sample ${masked} · MachineCode → ${mc?.suggestion?.field} (${mc?.suggestion?.source}, confirmed ${mc?.suggestion?.confirmed})`)
})

check('G-M1.4', 'M1', 'a second upload of the same layout matches the saved template and imports with no questions', async () => {
  await fixtureImport('fx-attendance.xlsx', ['rptDailyAttDetailsReport_Factor'])
  const r = await upload(ctx.admin, [path.join(FIXTURES, 'fx-attendance-2.xlsx')])
  const up = r.json?.uploads?.[0]
  const t = up && tableOf(up, 'rptDailyAttDetailsReport_Factor')
  const imp = await asAdmin('POST', `/api/matrix/uploads/${up?.id}/import`, {})
  const ds = imp.json?.datasets?.[0]
  const current = await sql("select Id, ReportDate, IsCurrent from MxDatasets where ReportKind = 'attendance-daily' order by Id")
  return ok(t?.template?.id && imp.status === 200 && /Imported with template .+ v\d+/.test(imp.json?.message ?? '') && ds?.reportDate === '2026-05-20'
      && current.at(-1)?.IsCurrent === 1 && current.slice(0, -1).every((d) => d.IsCurrent === 0),
    `template ${t?.template?.name} v${t?.template?.version} · import ${imp.status} "${imp.json?.message}" · current ${show(current.map((d) => `${d.ReportDate}:${d.IsCurrent}`))}`)
})

// ---- G-M1.5 Personal data ----

check('G-M1.5', 'M1', 'no dataset table holds a sensitive column (PRAGMA table_info); age_band exists', async () => {
  const imp = await fixtureImport('fx-master.xlsx', ['HRM', 'unit ', 'Sheet1'])
  await fixtureImport('fx-manpower.xlsx')
  const tables = (await sql("select name from sqlite_master where type = 'table' and name like 'mx_ds_%'")).map((r) => r.name)
  const leaks = []
  for (const t of tables)
    for (const c of await sql(`pragma table_info(${t})`)) if (SENSITIVE_COLUMN.test(c.name.replace(/[^a-z]/gi, '').toLowerCase())) leaks.push(`${t}.${c.name}`)
  const hrm = datasetOf(imp, 'HRM')
  const cols = (await sql(`pragma table_info(mx_ds_${hrm.id})`)).map((c) => c.name)
  const dropped = hrm.dropped?.length ?? 0
  const byHeader = await sensitiveByHeader()
  return ok(tables.length >= 4 && !leaks.length && !byHeader.length && cols.includes('age_band') && !cols.includes('date_of_birth') && dropped === ctx.M.master.sensitiveHeaders.length,
    `${tables.length} dataset tables · sensitive column names ${leaks.length}${leaks.length ? ` ${show(leaks)}` : ''} · columns from a sensitive header ${byHeader.length}${byHeader.length ? ` ${show(byHeader)}` : ''} · HRM has age_band ${cols.includes('age_band')} · dropped and recorded ${dropped}`)
})

check('G-M1.5', 'M1', 'the uploaded file is not kept on disk after import (searched by SHA-256 and size)', async () => {
  const file = path.join(FIXTURES, 'fx-master.xlsx')
  const sha = crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex')
  const size = fs.statSync(file).size
  const hits = []
  const walk = (dir) => {
    for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
      const p = path.join(dir, e.name)
      if (e.isDirectory()) walk(p)
      else if (e.name.includes(sha) || e.name.includes(sha.slice(0, 16)) || (fs.statSync(p).size === size)) hits.push(p)
    }
  }
  walk(ctx.api.runDir)
  walk(API_BIN_DIR)
  const stored = await sql('select Sha256, SizeBytes, Status from MxUploads where Sha256 = ?', sha)
  return ok(hits.length === 0 && stored.some((u) => u.Status === 'imported'),
    `files matching name or size in the API's folders: ${hits.length} · uploads recorded by hash: ${stored.length} (${show(stored.map((s) => s.Status))})`)
})

check('G-M1.5', 'M1', 'a user without view-personal sees names masked in the rows API; with it, they are shown', async () => {
  const u = await users()
  const imp = await fixtureImport('fx-master.xlsx', ['HRM', 'unit ', 'Sheet1'])
  const id = datasetOf(imp, 'HRM').id
  const limited = (await as(u.limited, 'GET', `/api/matrix/datasets/${id}/rows?limit=20`)).json
  const full = (await as(u.sys, 'GET', `/api/matrix/datasets/${id}/rows?limit=20`)).json
  const nameCol = limited?.columns?.find((c) => c.key === 'name')
  const salaryCol = full?.columns?.find((c) => c.key === 'gross')
  const maskedAll = limited?.rows?.every((r) => r.name === '•••')
  const shown = full?.rows?.some((r) => r.name && r.name !== '•••')
  const salaryMasked = full?.rows?.every((r) => r.gross === '•••')
  return ok(nameCol?.masked === true && nameCol.sensitivity === 'personal' && maskedAll && shown && salaryCol?.masked === true && salaryMasked,
    `limited user: name masked ${maskedAll} · System (view-personal): names shown ${shown}, gross masked without view-salary ${salaryMasked}`)
})

// ---- G-M1.6 Types and IDs ----

check('G-M1.6', 'M1', 'emp_id keeps leading zeros; PunchNo padded to six digits is the EmpID', async () => {
  const imp = await fixtureImport('fx-master.xlsx', ['HRM', 'unit ', 'Sheet1'])
  const { rows, columns } = await allRows(ctx.admin, datasetOf(imp, 'HRM').id)
  const emp = columns.find((c) => c.key === 'emp_id'), punch = columns.find((c) => c.key === 'punch_no')
  const zeros = rows.filter((r) => /^0\d{5}$/.test(r.emp_id ?? '')).length
  const pad = rows.filter((r) => r.emp_id && r.punch_emp_id === r.emp_id).length
  return ok(emp?.type === 'text' && punch && zeros > 0 && pad === ctx.M.master.punchPadsToEmpId,
    `emp_id ${emp?.type}, ${zeros} with leading zeros · punch_no → emp_id through the normaliser: ${pad}/${ctx.M.master.punchRows} (expected ${ctx.M.master.punchPadsToEmpId})`)
})

check('G-M1.6', 'M1', 'NULL becomes null; text dates become ISO; "07:54:20 AM " becomes 07:54:20', async () => {
  const hr = datasetOf(await fixtureImport('fx-hr-basic.xlsx'), 'Sheet1')
  const hrRows = (await allRows(ctx.admin, hr.id)).rows
  const nullSection = hrRows.filter((r) => r.section === null).length
  const anyNullText = hrRows.some((r) => Object.values(r).includes('NULL'))
  const master = datasetOf(await fixtureImport('fx-master.xlsx', ['HRM', 'unit ', 'Sheet1']), 'HRM')
  const mRows = (await allRows(ctx.admin, master.id)).rows
  const isoDates = mRows.filter((r) => /^\d{4}-\d{2}-\d{2}$/.test(r.joining_date ?? '')).length
  const att = datasetOf(await fixtureImport('fx-attendance.xlsx', ['rptDailyAttDetailsReport_Factor']), 'rptDailyAttDetailsReport_Factor')
  const aRows = (await allRows(ctx.admin, att.id)).rows
  const times = aRows.filter((r) => /^\d{2}:\d{2}:\d{2}$/.test(r.in_time ?? '') && /^\d{2}:\d{2}:\d{2}$/.test(r.out_time ?? '')).length
  const pm = aRows.filter((r) => Number(r.out_time?.slice(0, 2)) >= 17).length
  return ok(nullSection === 112 && !anyNullText && isoDates === mRows.length && times === aRows.length && pm === aRows.length,
    `hr-basic: section null ${nullSection}, "NULL" left ${anyNullText} · master joining_date ISO ${isoDates}/${mRows.length} · attendance times HH:mm:ss ${times}/${aRows.length}, PM as 24 h ${pm}`)
})

check('G-M1.6', 'M1', 'line_no and line_type are derived: 53 sewing lines', async () => {
  const master = datasetOf(await fixtureImport('fx-master.xlsx', ['HRM', 'unit ', 'Sheet1']), 'HRM')
  const { rows } = await allRows(ctx.admin, master.id)
  const lines = new Set(rows.filter((r) => r.line_type === 'Sewing' && r.line_no !== null).map((r) => r.line_no))
  const types = [...new Set(rows.map((r) => r.line_type))].sort()
  return ok(lines.size === ctx.M.master.sewingLines && rows.every((r) => r.line_type !== 'Sewing' || typeof r.line_no === 'number'),
    `distinct sewing lines ${lines.size} (expected ${ctx.M.master.sewingLines}) · line types ${show(types)}`)
})

// ---- G-M1.7 Duplicate sheets ----

check('G-M1.7', 'M1', 'HRM (2) a copy of HRM with its overlap; For corrcetion a near-copy with 2 extra columns; HRM the default', async () => {
  const up = await fixtureUpload('fx-master.xlsx')
  const t = (n) => tableOf(up, n)
  const h2 = t('HRM (2)').copyOf, fc = t('For corrcetion').copyOf
  return ok(h2?.sheet === 'HRM' && h2.overlapPct >= 99 && h2.near === false && fc?.sheet === 'HRM' && fc.near === true && fc.extraColumns === 2
      && t('HRM').selected === true && t('HRM (2)').selected === false && t('For corrcetion').selected === false && t('Sheet1').selected === true,
    `HRM (2) → ${h2?.sheet} ${h2?.overlapPct}% · For corrcetion → ${fc?.sheet} ${fc?.overlapPct}% near ${fc?.near} +${fc?.extraColumns} columns · selected ${show(up.profile.sheets.map((s) => `${s.name.trim()}:${s.tables[0].selected}`))}`)
})

// ---- G-M1.8 Quality checks ----

async function issuesOf(datasetId) {
  return (await asAdmin('GET', `/api/matrix/datasets/${datasetId}/quality`)).json?.issues ?? []
}

check('G-M1.8', 'M1', 'each quality check fires on its planted fixture with the right count and sample cells', async () => {
  const P = ctx.M.master.plants
  // The lookup first, so the designation check has something to compare with.
  await fixtureImport('fx-designation.xlsx')
  const master = datasetOf(await fixtureImport('fx-master.xlsx', ['HRM', 'unit ', 'Sheet1']), 'HRM')
  const s1 = datasetOf(await fixtureImport('fx-master.xlsx', ['HRM', 'unit ', 'Sheet1']), 'Sheet1')
  const hr = datasetOf(await fixtureImport('fx-hr-basic.xlsx'), 'Sheet1')
  const att = datasetOf(await fixtureImport('fx-attendance.xlsx', ['rptDailyAttDetailsReport_Factor']), 'rptDailyAttDetailsReport_Factor')
  const des = datasetOf(await fixtureImport('fx-designation.xlsx'), 'Sheet1')
  const I = { master: await issuesOf(master.id), s1: await issuesOf(s1.id), hr: await issuesOf(hr.id), att: await issuesOf(att.id), des: await issuesOf(des.id) }
  const find = (set, chk, field) => I[set].find((i) => i.check === chk && (!field || i.field === field)
    && (chk !== 'cross_sheet_person' || i.detail.includes("'HRM (2)'")))
  const expect = [
    ['master', 'dup_emp_id', null, P.dup_emp_id, ctx.M.master.refs.dup_emp_id],
    ['master', 'cross_sheet_person', null, P.cross_sheet_person],
    ['master', 'id_leading_zeros', null, P.id_leading_zeros],
    ['master', 'mixed_date_formats', 'joining_date', P.mixed_date_formats],
    ['master', 'trailing_spaces', 'designation', P.trailing_spaces, ctx.M.master.refs.trailing_spaces.slice(0, 3)],
    ['master', 'near_duplicate_values', 'section', P.near_duplicate_values],
    ['master', 'empty_required', 'unit', P.empty_required.unit],
    ['master', 'empty_required', 'status', P.empty_required.status],
    ['master', 'empty_required', 'emp_id', P.empty_required.emp_id],
    ['master', 'designation_not_in_lookup', 'designation', P.designation_not_in_lookup],
    ['master', 'worker_designation_no_grade', 'designation', P.worker_designation_no_grade],
    ['master', 'grade_forms_unified', 'grade_id', P.grade_forms_unified],
    ['s1', 'formula_mismatch', 'basic', P.formula_mismatch, ctx.M.master.refs.formula_mismatch],
    ['hr', 'null_text', null, ctx.M.hrBasic.null_text],
    ['hr', 'separation_before_joining', null, ctx.M.hrBasic.separation_before_joining],
    ['att', 'numbers_as_dates', 'ot_hours', ctx.M.attendance.numbersAsDates],
    ['des', 'trailing_spaces', 'designation', ctx.M.designation.trailing_spaces],
  ]
  const bad = [], good = []
  for (const [set, chk, field, count, refs] of expect) {
    const i = find(set, chk, field)
    const refsOk = !refs || refs.every((r) => i?.samples?.includes(r))
    if (!i || i.count !== count || !refsOk || !i.samples?.length) bad.push(`${set}/${chk}${field ? `:${field}` : ''} ${i?.count ?? 'absent'}≠${count}${refsOk ? '' : ` refs ${show(i?.samples?.slice(0, 4))}`}`)
    else good.push(`${chk}${field ? `:${field}` : ''}=${count}`)
  }
  return ok(!bad.length, `${good.length}/${expect.length} · ${bad.length ? `wrong: ${bad.join(' | ')}` : good.join(' ')}`)
})

check('G-M1.8', 'M1', 'a fix makes the corrected column, keeps _raw_, and is logged', async () => {
  const master = datasetOf(await fixtureImport('fx-master.xlsx', ['HRM', 'unit ', 'Sheet1']), 'HRM')
  const issue = (await issuesOf(master.id)).find((i) => i.check === 'trailing_spaces' && i.field === 'designation')
  const r = await asAdmin('POST', `/api/matrix/quality/${issue?.id}/fix`, { note: 'eval: trim designations' })
  const u = await users()
  const denied = (await as(u.sys, 'POST', `/api/matrix/quality/${issue?.id}/fix`, {})).status
  const cols = (await sql(`pragma table_info(mx_ds_${master.id})`)).map((c) => c.name)
  const raw = await sql(`select count(*) n from mx_ds_${master.id} where _raw_designation like '% '`)
  const left = await sql(`select count(*) n from mx_ds_${master.id} where designation like '% '`)
  const log = await sql('select Field, Kind, RowsChanged, Note from MxFixLogs where DatasetId = ?', master.id)
  const state = (await issuesOf(master.id)).find((i) => i.id === issue?.id)?.state
  return ok(r.status === 200 && cols.includes('_raw_designation') && raw[0].n === ctx.M.master.plants.trailing_spaces && left[0].n === 0
      && log.some((l) => l.Field === 'designation' && l.RowsChanged === ctx.M.master.plants.trailing_spaces && l.Note === 'eval: trim designations')
      && state === 'fixed' && denied === 403,
    `fix ${r.status} · _raw_designation ${cols.includes('_raw_designation')} keeps ${raw[0].n} raw values · trailing left ${left[0].n} · log ${show(log)} · issue ${state} · System without action:matrix.fix ${denied}`)
})

check('G-M1.8', 'M1', 'the lookup check counts Management and Non-Management only; importing a newer lookup re-checks existing datasets', async () => {
  const master = datasetOf(await fixtureImport('fx-master.xlsx', ['HRM', 'unit ', 'Sheet1']), 'HRM')
  const before = (await issuesOf(master.id)).find((i) => i.check === 'designation_not_in_lookup')
  const worker = (await issuesOf(master.id)).find((i) => i.check === 'worker_designation_no_grade')
  // The worker designation "Thread Cutter" is not in the lookup either, but workers are out of its scope.
  const workerOutOfScope = !before?.detail?.includes('Thread Cutter') && worker?.detail?.includes('Thread Cutter') && worker?.severity === 'info'
  const up = await fixtureUpload('fx-designation-2.xlsx')
  const r = await asAdmin('POST', `/api/matrix/uploads/${up.id}/import`, { tables: [{ table: tableOf(up, 'Sheet1').id, confirm: 'suggestions' }] })
  const after = (await issuesOf(master.id)).find((i) => i.check === 'designation_not_in_lookup')
  return ok(before?.count === ctx.M.master.plants.designation_not_in_lookup && workerOutOfScope && r.status === 200
      && after?.count === ctx.M.master.plants.designation_after_recheck && !after.detail.includes('Mechanic Asst'),
    `before ${before?.count} · worker designations without a grade ${worker?.count} (${worker?.severity}), out of the lookup's scope ${workerOutOfScope} · newer lookup imported ${r.status} → master now ${after?.count}: ${after?.detail}`)
})

// ---- G-M1.9 Viewer and highlight ----

check('G-M1.9', 'M1', 'the viewer opens 8,500 rows virtualised and a highlight token outlines the right rows and cells (screenshots)', async () => {
  const master = datasetOf(await fixtureImport('fx-master.xlsx', ['HRM', 'unit ', 'Sheet1']), 'HRM')
  const issue = (await issuesOf(master.id)).find((i) => i.check === 'dup_emp_id')
  const shots = ctx.args.shots
  fs.mkdirSync(shots, { recursive: true })
  const browser = await launchBrowser({ base: ctx.api.url, profileRoot: EVAL_ROOT })
  try {
    const p = await browser.page()
    await p.session(ctx.admin, 'light', 1280, 900, `/matrix?dataset=${master.id}&tab=data`)
    try { await p.waitFor('[data-matrix-viewer] [data-row]', 15000) } catch (e) {
      await p.shot(path.join(shots, 'failure-viewer.png'))
      const grid = await p.eval(`JSON.stringify((() => { const g = document.querySelector('[data-matrix-grid]'); return g ? { h: g.clientHeight, w: g.clientWidth, kids: g.firstElementChild?.childElementCount, html: g.outerHTML.slice(0, 300) } : 'no grid' })())`)
      throw new Error(`${e.message} · page ${await p.eval('location.pathname + location.search')} · grid ${grid}`)
    }
    const total = await p.eval('Number(document.querySelector("[data-matrix-viewer]").getAttribute("data-total"))')
    const rendered = await p.eval('document.querySelectorAll("[data-matrix-viewer] [data-row]").length')
    // Scroll the grid and time the frames it takes.
    const frames = await p.eval(`(async () => {
      const grid = document.querySelector('[data-matrix-grid]')
      const times = []
      let last = performance.now()
      for (let i = 0; i < 40; i++) {
        grid.scrollTop += 1500
        await new Promise((r) => requestAnimationFrame(r))
        const now = performance.now(); times.push(now - last); last = now
      }
      times.sort((a, b) => a - b)
      return { median: times[20], p95: times[38], rendered: document.querySelectorAll('[data-matrix-viewer] [data-row]').length }
    })()`)
    await p.shot(path.join(shots, 'viewer-light-1280.png'))

    // A quality issue's token: its rows outlined, its cells filled.
    await p.session(ctx.admin, 'light', 1280, 900, `/matrix?dataset=${master.id}&highlight=${encodeURIComponent(issue.highlight)}`)
    await p.waitFor('[data-hl-row]', 15000)
    const hl = await p.eval(`({
      rows: [...document.querySelectorAll('[data-hl-row]')].map((e) => Number(e.getAttribute('data-src-row'))),
      cells: [...document.querySelectorAll('[data-hl-cell]')].map((e) => e.getAttribute('data-col')),
    })`)
    await p.shot(path.join(shots, 'viewer-highlight-light-1280.png'))
    await p.setTheme('dark')
    await p.shot(path.join(shots, 'viewer-highlight-dark-1280.png'))
    await p.viewport(360, 780)
    await sleep(500)
    await p.shot(path.join(shots, 'viewer-highlight-dark-360.png'))
    await p.setTheme('light')
    await p.shot(path.join(shots, 'viewer-highlight-light-360.png'))
    const overflow = await p.eval('document.documentElement.scrollWidth > window.innerWidth')

    // The Data section's lists, for the record.
    await p.viewport(1280, 900)
    await p.session(ctx.admin, 'light', 1280, 900, '/matrix')
    await p.waitFor('[data-matrix-datasets]', 15000)
    await p.shot(path.join(shots, 'data-light-1280.png'))
    await p.session(ctx.admin, 'light', 1280, 900, `/matrix?quality=${master.id}`)
    await p.waitFor('[data-matrix-quality]', 15000)
    await p.shot(path.join(shots, 'quality-light-1280.png'))
    // The mapping screen, for a layout no template knows yet.
    const fresh = (await upload(ctx.admin, [path.join(FIXTURES, 'fx-hr-basic.xlsx')])).json?.uploads?.[0]
    const unseen = (await upload(ctx.admin, [path.join(FIXTURES, 'fx-multiheader.xlsx')])).json?.uploads?.[0]
    for (const [up, name] of [[unseen, 'mapping-new'], [fresh, 'mapping-template']]) {
      await p.session(ctx.admin, 'light', 1280, 900, `/matrix?upload=${up.id}`)
      await p.waitFor('[data-matrix-mapping]', 15000)
      await p.shot(path.join(shots, `${name}-light-1280.png`))
    }
    await p.setTheme('dark')
    await p.viewport(360, 780)
    await sleep(400)
    await p.shot(path.join(shots, 'mapping-template-dark-360.png'))
    await p.setTheme('light')

    const wantRows = issue.highlightRows ?? []
    const rowsOk = hl.rows.length === wantRows.length && wantRows.every((r) => hl.rows.includes(r))
    const cellsOk = hl.cells.length === wantRows.length && hl.cells.every((c) => c === 'emp_id')
    return ok(total === 8514 && rendered < 120 && frames.rendered < 120 && frames.p95 < 100 && rowsOk && cellsOk && overflow === false,
      `total ${total} · rendered rows ${rendered} (after scrolling ${frames.rendered}) · frame median ${frames.median.toFixed(1)} ms, p95 ${frames.p95.toFixed(1)} ms · highlight rows ${show(hl.rows)} want ${show(wantRows)} · cells ${show([...new Set(hl.cells)])} ×${hl.cells.length} · 360px h-overflow ${overflow} · screenshots in ${shots}`)
  } finally {
    await browser.close()
  }
})

// ---- G-M1.10 Builds (the suites are run beside this one) ----

check('G-M1.10', 'M1', 'dotnet build: 0 warnings, 0 errors', async () => {
  const out = path.join(EVAL_ROOT, 'build-check')
  const r = spawnSync('dotnet', ['build', path.join(REPO, 'backend', 'RabbitIC.Api.csproj'), '--no-incremental', '-nologo', '-v', 'q', '-o', out],
    { encoding: 'utf8', shell: process.platform === 'win32' })
  const w = r.stdout.match(/(\d+) Warning\(s\)/)?.[1], e = r.stdout.match(/(\d+) Error\(s\)/)?.[1]
  return ok(r.status === 0 && w === '0' && e === '0', `${w} warnings · ${e} errors`)
})

check('G-M1.10', 'M1', 'npm run build is clean (the chunk-size notice predates Matrix)', async () => {
  const r = spawnSync('npm', ['run', 'build'], { cwd: path.join(REPO, 'frontend'), encoding: 'utf8', shell: true })
  const out = r.stdout + r.stderr
  return ok(r.status === 0 && !/error/i.test(out.replace(/chunkSizeWarningLimit/g, '')), `exit ${r.status}${/built in/.test(out) ? ' · built' : ''}`)
})

// ═══════════════════════════════ live (matrix-samples/, counts only) ═══════════════════════════════

const liveFile = (k) => path.join(SAMPLES, REAL[k])

async function liveUploads() {
  if (ctx.live) return ctx.live
  ctx.live = {}
  for (const [k] of Object.entries(REAL)) {
    if (!fs.existsSync(liveFile(k))) throw new Error(`matrix-samples/${REAL[k]} is missing`)
    const r = await upload(ctx.admin, [liveFile(k)])
    if (r.status !== 201) throw new Error(`upload ${k} → ${r.status} ${r.text.slice(0, 200)}`)
    ctx.live[k] = r.json.uploads[0]
  }
  return ctx.live
}

check('G-M1.3', 'M1', 'LIVE profile of the five real files (counts and column names only)', async () => {
  const L = await liveUploads()
  const lines = []
  for (const [k, up] of Object.entries(L))
    for (const s of up.profile.sheets) for (const t of s.tables)
      lines.push(`${REAL[k]} / '${s.name}': ${t.range}, header row ${t.headerRows.join('+')}, ${t.dataRows} rows × ${t.columns.length} cols, kind ${t.kind}${t.reportDate ? `, report date ${t.reportDate}` : ''}${t.copyOf ? `, copy of '${t.copyOf.sheet}' ${t.copyOf.overlapPct}%${t.copyOf.near ? ` (+${t.copyOf.extraColumns} cols)` : ''}` : ''}${t.columns.some((c) => c.formula) ? `, formulas in ${t.columns.filter((c) => c.formula).map((c) => `${c.header}=${c.formula}`).join('; ')}` : ''}`)
  fs.writeFileSync(path.join(EVAL_ROOT, 'live-profile.txt'), lines.join('\n') + '\n')
  const att = L.attendance.profile.sheets[0].tables[0]
  const hrm = L.master.profile.sheets.find((s) => s.name === 'HRM')?.tables?.[0]
  return ok(att.reportDate === '2026-05-19' && att.headerRows[0] === 3 && att.dataRows === 676 && hrm?.dataRows === 8514,
    `${lines.length} tables · attendance ${att.dataRows} rows, header ${att.headerRows}, date ${att.reportDate} · HRM ${hrm?.dataRows}×${hrm?.columns?.length} · table in ${path.join(EVAL_ROOT, 'live-profile.txt')}`)
}, { live: true })

check('G-M1.4', 'M1', 'LIVE at least 90% of columns mapped by synonyms on each real file (unmatched header names listed)', async () => {
  const L = await liveUploads()
  const out = [], low = []
  for (const [k, up] of Object.entries(L)) {
    const t = up.profile.sheets.map((s) => s.tables[0]).filter(Boolean).filter((x) => x.selected)
    for (const table of t) {
      const n = table.columns.length
      const mapped = table.columns.filter((c) => c.synonym || c.sensitivity === 'sensitive')
      const pct = Math.round((mapped.length / n) * 100)
      const unmatched = table.columns.filter((c) => !mapped.includes(c)).map((c) => c.header)
      out.push(`${k}/${table.sheet.trim()} ${pct}% (${mapped.length}/${n})${unmatched.length ? ` unmatched ${show(unmatched)}` : ''}`)
      if (pct < 90) low.push(`${k}/${table.sheet}`)
    }
  }
  return ok(!low.length, out.join(' · '))
}, { live: true })

check('G-M1.5', 'M1', 'LIVE after importing the real master: no sensitive column in any dataset table; age_band present', async () => {
  const L = await liveUploads()
  const up = L.master
  const hrm = up.profile.sheets.find((s) => s.name === 'HRM').tables[0]
  const r = await asAdmin('POST', `/api/matrix/uploads/${up.id}/import`, { tables: [{ table: hrm.id, confirm: 'suggestions' }] })
  if (r.status !== 200) return ok(false, `import ${r.status} ${r.text.slice(0, 200)}`)
  const ds = r.json.datasets[0]
  ctx.liveMaster = ds
  const cols = (await sql(`pragma table_info(mx_ds_${ds.id})`)).map((c) => c.name)
  const leaks = cols.filter((c) => SENSITIVE_COLUMN.test(c.replace(/[^a-z]/gi, '').toLowerCase()))
  const byHeader = (await sensitiveByHeader()).filter((x) => x.startsWith('HRM.'))
  return ok(!leaks.length && !byHeader.length && cols.includes('age_band'),
    `HRM imported: ${ds.rowCount} rows, ${cols.length} columns · sensitive column names ${leaks.length} · columns from a sensitive header ${byHeader.length}${byHeader.length ? ` ${show(byHeader)}` : ''} · dropped at import ${ds.dropped.length} · age_band ${cols.includes('age_band')}`)
}, { live: true })

check('G-M1.6', 'M1', 'LIVE the master\'s sewing sections give 53 distinct lines', async () => {
  const ds = ctx.liveMaster
  if (!ds) return ok(false, 'the live master import did not run')
  const n = await sql(`select count(distinct line_no) n from mx_ds_${ds.id} where line_type = 'Sewing' and line_no is not null`)
  const types = await sql(`select line_type, count(*) n from mx_ds_${ds.id} group by line_type order by n desc`)
  return ok(n[0].n === 53, `${n[0].n} distinct sewing lines · line types ${show(types.map((t) => `${t.line_type}:${t.n}`))}`)
}, { live: true })

check('G-M1.7', 'M1', 'LIVE HRM (2) and For corrcetion flagged as copies of HRM; HRM the default', async () => {
  const L = await liveUploads()
  const t = (n) => L.master.profile.sheets.find((s) => s.name === n)?.tables?.[0]
  const h2 = t('HRM (2)')?.copyOf, fc = t('For corrcetion')?.copyOf
  return ok(h2?.sheet === 'HRM' && fc?.sheet === 'HRM' && fc.near && fc.extraColumns === 2 && t('HRM').selected && !t('HRM (2)').selected,
    `HRM (2) → ${h2?.sheet} ${h2?.overlapPct}% · For corrcetion → ${fc?.sheet} ${fc?.overlapPct}% +${fc?.extraColumns} cols · HRM selected ${t('HRM')?.selected}`)
}, { live: true })

check('G-M1.8', 'M1', 'LIVE quality counts per real file (no personal data)', async () => {
  const L = await liveUploads()
  const out = []
  for (const k of ['designation', 'attendance', 'hrBasic', 'manpower']) {
    const up = L[k]
    const tables = up.profile.sheets.map((s) => s.tables[0]).filter((t) => t?.selected)
    const r = await asAdmin('POST', `/api/matrix/uploads/${up.id}/import`, { tables: tables.map((t) => ({ table: t.id, confirm: 'suggestions' })) })
    if (r.status !== 200) { out.push(`${k}: import ${r.status}`); continue }
    for (const ds of r.json.datasets) {
      const issues = await issuesOf(ds.id)
      out.push(`${k}/${ds.sheet.trim()}: ${issues.map((i) => `${i.check}${i.field ? `:${i.field}` : ''}=${i.count}`).join(' ') || 'none'}`)
    }
  }
  if (ctx.liveMaster) out.push(`master/HRM: ${(await issuesOf(ctx.liveMaster.id)).map((i) => `${i.check}${i.field ? `:${i.field}` : ''}=${i.count}`).join(' ')}`)
  return ok(out.length >= 5 && !out.some((l) => /import \d/.test(l)), out.join(' ‖ '))
}, { live: true })

// ═══════════════════════════════ Phase M2 ═══════════════════════════════
//
// Every expected number here is computed again, independently, from the fixture
// workbooks themselves (readWorkbook + plain JS) — never from the server's own
// tables or code.

/** A fixture sheet as objects keyed by header text (cell text as stored). */
function sheetObjects(file, sheetName, headerRow = 1) {
  const sheet = readWorkbook(path.join(FIXTURES, file)).find((s) => s.name === sheetName)
  const cols = new Map()
  const rows = new Map()
  for (const [ref, v] of sheet.cells) {
    const m = /^([A-Z]+)(\d+)$/.exec(ref)
    const r = Number(m[2])
    if (r === headerRow) cols.set(m[1], v)
    else if (r > headerRow) {
      if (!rows.has(r)) rows.set(r, {})
      rows.get(r)[m[1]] = v
    }
  }
  return [...rows.entries()].sort((a, b) => a[0] - b[0]).map(([, cells]) => {
    const o = {}
    for (const [letter, header] of cols) o[header] = cells[letter] ?? null
    return o
  })
}

const countBy = (rows, key) => rows.reduce((m, r) => { const k = key(r) ?? 'null'; m[k] = (m[k] ?? 0) + 1; return m }, {})
const median = (xs) => { const s = [...xs].sort((a, b) => a - b); const n = s.length; return n ? (n % 2 ? s[(n - 1) / 2] : (s[n / 2 - 1] + s[n / 2]) / 2) : null }
const round2 = (x) => Math.round(x * 100) / 100
/** GradeId as one spelling, written here again: "Grade 3", "Grade 03" and 3 are "3". */
const gradeOf = (g) => {
  if (g === null || g === undefined || String(g).trim() === '') return null
  const m = /^(?:grade(?![a-z])|gr\.)\s*[-:#]?\s*(.+)$/i.exec(String(g).trim())
  const rest = (m ? m[1] : String(g)).trim()
  return /^\d+(\.0+)?$/.test(rest) ? String(Number(rest)) : rest
}
const lineOf = (wing) => { const m = /^\s*line\s*[-–:#]?\s*(\d+)/i.exec(wing ?? ''); return m ? Number(m[1]) : null }
const sameMap = (a, b) => {
  const ka = Object.keys(a).sort(), kb = Object.keys(b).sort()
  return ka.join('|') === kb.join('|') && ka.every((k) => Math.abs(Number(a[k]) - Number(b[k])) < 1e-6)
}
/** A KPI's breakdown rows as { group value → value }. */
const asMap = (res, field) => Object.fromEntries((res?.rows ?? []).map((r) => [r[field] === null || r[field] === undefined ? 'null' : String(r[field]), r.value]))

async function runKpi(user, key, body) {
  return as(user, 'POST', `/api/matrix/kpis/${key}/run`, body)
}

async function fixtureIds() {
  const master = datasetOf(await fixtureImport('fx-master.xlsx', ['HRM', 'unit ', 'Sheet1']), 'HRM')
  const att = datasetOf(await fixtureImport('fx-attendance.xlsx', ['rptDailyAttDetailsReport_Factor']), 'rptDailyAttDetailsReport_Factor')
  const hr = datasetOf(await fixtureImport('fx-hr-basic.xlsx'), 'Sheet1')
  return { master: master.id, att: att.id, hr: hr.id }
}

// ---- G-M2.1 The validator ----

check('G-M2.1', 'M2', 'the validator refuses unknown datasets and fields, salary without permission, bad operators, big limits and injections — each with its reason', async () => {
  const u = await users()
  const { master } = await fixtureIds()
  const q = (query, user = u.sys, explain = false) => as(user, 'POST', '/api/matrix/query', { query, explain })
  const base = { dataset: String(master), measures: [['count', '*', 'n']] }
  const cases = [
    ['unknown dataset ref', { ...base, dataset: 'current:nope' }, 400, /dataset/i],
    ['no such dataset', { ...base, dataset: '999999' }, 400, /dataset/i],
    ['unknown field', { ...base, group_by: ['shoe_size'] }, 400, /shoe_size/],
    ['salary without view-salary', { ...base, measures: [['sum', 'gross', 'g']] }, 403, /view-salary/],
    ['forbidden operator', { ...base, filter: [['section', '~=', 'Sewing']] }, 400, /operator/i],
    ['limit 10,000', { ...base, group_by: ['section'], limit: 10000 }, 400, /500/],
    ['injection in a value', { ...base, filter: [['section', '=', "x'; DROP TABLE MxDatasets; --"]] }, 400, /value/i],
    ['injection in a field name', { ...base, group_by: ['section; DROP TABLE MxDatasets'] }, 400, /field/i],
    ['names grouped', { ...base, group_by: ['name'] }, 400, /personal/i],
  ]
  const out = []
  let pass = true
  for (const [name, query, status, reason] of cases) {
    const r = await q(query)
    const good = r.status === status && reason.test(r.json?.error ?? '')
    pass &&= good
    out.push(`${name} → ${r.status} "${(r.json?.error ?? '').slice(0, 90)}"${good ? '' : ' ✗'}`)
  }
  const tables = await sql("select count(*) n from sqlite_master where name = 'MxDatasets'")
  const valid = await q({ ...base, filter: [['section', '=', 'Sewing'], ['status', 'in', ['Active']]], group_by: ['unit'] }, ctx.admin, true)
  const sqlText = valid.json?.provenance?.sql ?? ''
  const paramsOnly = valid.status === 200 && /\$p\d+/.test(sqlText) && !/Sewing|Active/.test(sqlText) && (valid.json?.provenance?.params ?? []).includes('Sewing')
  return ok(pass && tables[0].n === 1 && paramsOnly,
    `${out.join(' · ')} · MxDatasets still there ${tables[0].n === 1} · valid query SQL uses parameters only ${paramsOnly}: ${sqlText.slice(0, 400)} · params ${JSON.stringify(valid.json?.provenance?.params)}`)
})

// ---- G-M2.2 Correctness: 15+ KPIs against an independent computation ----

check('G-M2.2', 'M2', '16 KPI queries on the fixtures equal an independent JS computation over the fixture rows', async () => {
  const { master, att, hr } = await fixtureIds()
  const M = sheetObjects('fx-master.xlsx', 'HRM')
  const A = sheetObjects('fx-attendance.xlsx', 'rptDailyAttDetailsReport_Factor', 3)
  const H = sheetObjects('fx-hr-basic.xlsx', 'Sheet1')
  const active = M.filter((r) => (r.EmpStatusName ?? '').trim() === 'Active')
  const sewing = active.filter((r) => r.SectionName === 'Sewing' && lineOf(r.WingName) !== null)
  const perLine = (pred) => {
    const lines = {}
    for (const r of sewing) { const l = lineOf(r.WingName); lines[l] = (lines[l] ?? 0) + (pred(r) ? 1 : 0) }
    return lines
  }
  const linesPerUnit = {}
  for (const r of sewing) (linesPerUnit[r.UnitName ?? 'null'] ??= new Set()).add(lineOf(r.WingName))
  const ops = perLine((r) => /operator/i.test(r.DesignationName ?? ''))
  const qc = perLine((r) => /quality inspector|quality controller|checker/i.test(r.DesignationName ?? ''))
  const grossByUnit = {}
  for (const r of active) grossByUnit[r.UnitName ?? 'null'] = (grossByUnit[r.UnitName ?? 'null'] ?? 0) + Number(r.GrossSalary)

  const want = {
    active_headcount: active.length,
    headcount_by_unit: countBy(active, (r) => r.UnitName),
    headcount_by_section: countBy(active, (r) => r.SectionName),
    headcount_by_gender: countBy(active, (r) => r.Gender),
    female_pct: round2((100 * active.filter((r) => r.Gender === 'Female').length) / active.length),
    headcount_by_emp_type: countBy(active, (r) => r.EmpTypeName),
    headcount_by_grade: countBy(active, (r) => gradeOf(r.GradeId)),
    sewing_lines_per_unit: Object.fromEntries(Object.entries(linesPerUnit).map(([k, v]) => [k, v.size])),
    operators_per_line: ops,
    operators_per_line_summary: { min: Math.min(...Object.values(ops)), median: median(Object.values(ops)), max: Math.max(...Object.values(ops)) },
    qc_per_line: qc,
    att_present: A.filter((r) => ['P', 'LT'].includes(r.Status)).length,
    att_late: A.filter((r) => r.Status === 'LT').length,
    ot_over_2h: A.filter((r) => Number(r['OT Hours']) > 2).length,
    shift_mix: countBy(A, (r) => r['Shift ']),
    separations_by_status: countBy(H.filter((r) => r.EmpStatusName !== 'Active'), (r) => r.EmpStatusName),
    gross_by_unit: grossByUnit,
  }
  const run = async (key, dataset) => (await runKpi(ctx.admin, key, { dataset })).json
  const got = {
    active_headcount: (await run('active_headcount', master))?.value,
    headcount_by_unit: asMap(await run('headcount_by_unit', master), 'unit'),
    headcount_by_section: asMap(await run('headcount_by_section', master), 'section'),
    headcount_by_gender: asMap(await run('headcount_by_gender', master), 'gender'),
    female_pct: (await run('female_pct', master))?.value,
    headcount_by_emp_type: asMap(await run('headcount_by_emp_type', master), 'emp_type'),
    headcount_by_grade: asMap(await run('headcount_by_grade', master), 'grade_id'),
    sewing_lines_per_unit: asMap(await run('sewing_lines_per_unit', master), 'unit'),
  }
  const opsRes = await run('operators_per_line', master)
  got.operators_per_line = asMap(opsRes, 'line_no')
  got.operators_per_line_summary = { min: opsRes?.summary?.min, median: opsRes?.summary?.median, max: opsRes?.summary?.max }
  got.qc_per_line = asMap(await run('qc_per_line', master), 'line_no')
  got.att_present = (await run('att_present', att))?.value
  got.att_late = (await run('att_late', att))?.value
  got.ot_over_2h = (await run('ot_over_2h', att))?.value
  got.shift_mix = asMap(await run('shift_mix', att), 'shift')
  got.separations_by_status = asMap(await run('separations_by_status', hr), 'status')
  got.gross_by_unit = asMap(await run('gross_by_unit', master), 'unit')

  const bad = Object.keys(want).filter((k) => typeof want[k] === 'object' ? !sameMap(want[k], got[k] ?? {}) : !(typeof got[k] === 'number' && Math.abs(want[k] - got[k]) <= 1e-6))
  return ok(bad.length === 0,
    `${Object.keys(want).length - bad.length}/${Object.keys(want).length} equal` + (bad.length ? ` · differ: ${bad.map((k) => `${k} want ${JSON.stringify(want[k]).slice(0, 120)} got ${JSON.stringify(got[k]).slice(0, 120)}`).join(' | ')}`
      : ` · e.g. active ${got.active_headcount}, female ${got.female_pct}%, operators per line ${JSON.stringify(got.operators_per_line_summary)}, late ${got.att_late}, OT > 2 h ${got.ot_over_2h}`))
})

// ---- G-M2.3 Joins ----

check('G-M2.3', 'M2', 'attendance joined to the master by emp_id gives present and late per sewing line; people missing from the master are counted', async () => {
  const { master, att } = await fixtureIds()
  const M = sheetObjects('fx-master.xlsx', 'HRM')
  const A = sheetObjects('fx-attendance.xlsx', 'rptDailyAttDetailsReport_Factor', 3)
  const firstById = new Map()
  for (const r of M) if (r.EmpID && !firstById.has(r.EmpID)) firstById.set(r.EmpID, r)
  const present = {}, late = {}
  let unmatched = 0
  for (const a of A) {
    const m = firstById.get(a['Emp ID'])
    if (!m) { unmatched++; continue }
    if (m.SectionName !== 'Sewing' || lineOf(m.WingName) === null) continue
    const l = lineOf(m.WingName)
    present[l] = (present[l] ?? 0) + (['P', 'LT'].includes(a.Status) ? 1 : 0)
    late[l] = (late[l] ?? 0) + (a.Status === 'LT' ? 1 : 0)
  }
  const r = await asAdmin('POST', '/api/matrix/query', { query: {
    dataset: String(att), join: [{ dataset: String(master), on: 'emp_id' }],
    filter: [['master.line_type', '=', 'Sewing']], group_by: ['master.line_no'],
    measures: [['count_if', ['att_status', 'in', ['P', 'LT']], 'present'], ['count_if', ['att_status', '=', 'LT'], 'late']], limit: 500,
  } })
  const rows = r.json?.rows ?? []
  const gotPresent = Object.fromEntries(rows.map((x) => [String(x['master.line_no']), x.present]))
  const gotLate = Object.fromEntries(rows.map((x) => [String(x['master.line_no']), x.late]))
  const join = r.json?.provenance?.joins?.[0]
  return ok(r.status === 200 && sameMap(present, gotPresent) && sameMap(late, gotLate) && join?.unmatched === unmatched && unmatched === ctx.M.attendance.notInMaster,
    `${r.status} · ${rows.length} sewing lines · present per line equal ${sameMap(present, gotPresent)} · late equal ${sameMap(late, gotLate)} · in attendance, not in the master: ${join?.unmatched} (independent ${unmatched}) · ${r.json?.provenance?.plain ?? r.json?.error}`)
})

// ---- G-M2.5 Report cards ----

check('G-M2.5', 'M2', 'a report card for each dataset kind: built by code, under 3,000 tokens, every value cited, a count\'s citation reopens exactly its rows', async () => {
  const { master, att, hr } = await fixtureIds()
  const des = datasetOf(await fixtureImport('fx-designation.xlsx'), 'Sheet1').id
  const mp = datasetOf(await fixtureImport('fx-manpower.xlsx'), 'Sheet1').id
  const out = []
  let pass = true
  for (const [kind, id] of [['master', master], ['attendance-daily', att], ['separation-list', hr], ['designation-lookup', des], ['manpower', mp]]) {
    const r = await asAdmin('GET', `/api/matrix/datasets/${id}/card`)
    const card = r.json
    const items = [...(card?.items ?? []), ...(card?.breakdowns ?? []).flatMap((b) => [...b.top, ...b.bottom])]
    const uncited = items.filter((i) => !i.cite).length
    let reopened = 0, wrong = 0
    for (const i of items.filter((x) => x.unit === 'people' && x.cite).slice(0, 6)) {
      const rows = await asAdmin('GET', `/api/matrix/datasets/${id}/rows?limit=1&highlight=${encodeURIComponent(i.cite)}`)
      if (rows.json?.total === i.value) reopened++; else wrong++
    }
    const good = r.status === 200 && card.kind === kind && card.tokens < 3000 && items.length > 0 && uncited === 0 && reopened > 0 && wrong === 0
    pass &&= good
    out.push(`${kind}: ${items.length} values, ${card?.tokens} tokens, uncited ${uncited}, citations reopened ${reopened}${wrong ? ` WRONG ${wrong}` : ''}${card?.comparison ? `, compared with dataset ${card.comparison.previousId}` : ''}${good ? '' : ' ✗'}`)
  }
  const att2 = (await sql("select Id from MxDatasets where ReportKind = 'attendance-daily' order by Id desc limit 1"))[0]?.Id
  const second = (await asAdmin('GET', `/api/matrix/datasets/${att2}/card`)).json
  pass &&= !!second?.comparison && second.comparison.items.length > 0
  return ok(pass, `${out.join(' · ')} · 20 May attendance compared with the previous snapshot: ${second?.comparison?.items?.length ?? 0} items`)
})

// ---- G-M2.6 Snapshots ----

check('G-M2.6', 'M2', 'two manpower snapshots a month apart give joiners, separations and attrition % equal to an independent computation', async () => {
  const importAs = async (file, reportDate) => {
    const r = await upload(ctx.admin, [path.join(FIXTURES, file)])
    const up = r.json.uploads[0]
    const imp = await asAdmin('POST', `/api/matrix/uploads/${up.id}/import`, { tables: [{ table: tableOf(up, 'Sheet1').id, confirm: 'suggestions', reportDate }] })
    return imp.json.datasets[0].id
  }
  const may = await importAs('fx-manpower.xlsx', ctx.M.manpower2.previousReportDate)
  const june = await importAs('fx-manpower-2.xlsx', ctx.M.manpower2.reportDate)
  const ids = (f) => new Set(sheetObjects(f, 'Sheet1').map((r) => r['Employee ID']))
  const a = ids('fx-manpower.xlsx'), b = ids('fx-manpower-2.xlsx')
  const joiners = [...b].filter((x) => !a.has(x)).length
  const leavers = [...a].filter((x) => !b.has(x)).length
  const attrition = round2((100 * leavers) / ((a.size + b.size) / 2))
  const body = { dataset: june, previous: may }
  const got = {
    joiners: (await runKpi(ctx.admin, 'joiners_since_previous', body)).json?.value,
    separations: (await runKpi(ctx.admin, 'separations_since_previous', body)).json?.value,
    attrition: (await runKpi(ctx.admin, 'attrition_pct', body)).json?.value,
  }
  return ok(got.joiners === joiners && got.separations === leavers && Math.abs(got.attrition - attrition) < 0.01,
    `joiners ${got.joiners} (independent ${joiners}) · separations ${got.separations} (${leavers}) · attrition ${got.attrition}% (${attrition}%: separations ÷ average of opening and closing active headcount × 100)`)
})

// ---- G-M2.7 Builds are shared with G-M1.10 (they run in every phase) ----

// ---- G-M2.4 Live KPIs (counts only) ----

check('G-M2.4', 'M2', 'LIVE KPIs on the real files (counts only)', async () => {
  const ds = await asAdmin('GET', '/api/matrix/datasets')
  const real = (ds.json?.datasets ?? []).filter((d) => !d.fileName.startsWith('fx-'))
  const pick = (f) => real.find((d) => d.fileName === REAL[f] && (f !== 'master' || d.sheet === 'HRM'))?.id
  const master = pick('master'), att = pick('attendance')
  if (!master || !att) return ok(false, 'the live master and attendance imports are missing (run with --live, after the M1 live checks)')
  const run = async (key, body) => (await runKpi(ctx.admin, key, body)).json
  const byUnit = await run('headcount_by_unit', { dataset: master })
  const lines = await run('sewing_lines_per_unit', { dataset: master })
  const ops = await run('operators_per_line', { dataset: master })
  const female = await run('female_pct', { dataset: master })
  const grade = await run('headcount_by_grade', { dataset: master })
  const eqv = await run('headcount_by_equivalency_grade', { dataset: master })
  const attPct = await run('attendance_pct', { dataset: att, against: master })
  const late = await run('late_pct', { dataset: att })
  const ot = await run('ot_over_2h', { dataset: att })
  const fmt = (r, f) => (r?.rows ?? []).map((x) => `${x[r.groupBy ?? f]}:${x.value}`).join(', ')
  const lines2 = [
    `active headcount by unit [${fmt(byUnit, 'unit')}]`,
    `sewing lines per unit [${fmt(lines, 'unit')}]`,
    `operators per line min ${ops?.summary?.min} · median ${ops?.summary?.median} · max ${ops?.summary?.max} over ${ops?.summary?.count} lines`,
    `female ${female?.value}%`,
    `GradeId distribution [${fmt(grade, 'grade_id')}]`,
    `equivalency grade (staff, through the lookup) [${fmt(eqv, 'equivalency_grade')}]`,
    `attendance on ${attPct?.provenance?.datasets?.[0]?.reportDate}: ${attPct?.value}% of active manpower (${attPct?.detail})`,
    `late ${late?.value}%`,
    `OT above 2 h: ${ot?.value} people`,
  ]
  const good = [byUnit, lines, ops, female, grade, attPct, late, ot].every((r) => r && !r.error) && (byUnit.rows?.length ?? 0) > 0 && ops?.summary?.count > 0
  return ok(good, lines2.join(' ‖ '))
}, { live: true })

// ═══════════════════════════════ Phase M3 ═══════════════════════════════
//
// The dashboards' numbers are checked twice: against the KPI or query each widget
// says it ran (run again through the public routes), and, for the main tiles,
// against an independent computation over the fixture workbooks.

/** Every fixture kind imported once: master, both attendance days, the separation list, the lookup, two manpower snapshots. */
async function m3Setup() {
  if (ctx.m3) return ctx.m3
  const ids = await fixtureIds()
  await fixtureImport('fx-designation.xlsx')
  const have = async (kind, date) => (await sql('select Id from MxDatasets where ReportKind = ? and ReportDate = ? order by Id desc limit 1', kind, date))[0]?.Id
  const importAs = async (file, sheet, reportDate) => {
    const up = (await upload(ctx.admin, [path.join(FIXTURES, file)])).json.uploads[0]
    const r = await asAdmin('POST', `/api/matrix/uploads/${up.id}/import`, { tables: [{ table: tableOf(up, sheet).id, confirm: 'suggestions', ...(reportDate ? { reportDate } : {}) }] })
    if (r.status !== 200) throw new Error(`import ${file} → ${r.status} ${r.text.slice(0, 200)}`)
    return r.json.datasets[0].id
  }
  ids.att2 = await have('attendance-daily', ctx.M.attendance2.reportDate)
    ?? await importAs('fx-attendance-2.xlsx', 'rptDailyAttDetailsReport_Factor')
  ids.may = await have('manpower', ctx.M.manpower2.previousReportDate) ?? await importAs('fx-manpower.xlsx', 'Sheet1', ctx.M.manpower2.previousReportDate)
  ids.june = await have('manpower', ctx.M.manpower2.reportDate) ?? await importAs('fx-manpower-2.xlsx', 'Sheet1', ctx.M.manpower2.reportDate)
  const list = (await asAdmin('GET', '/api/matrix/dashboards')).json?.dashboards ?? []
  ids.dash = Object.fromEntries(list.filter((d) => d.auto).map((d) => [d.auto, d.id]))
  ctx.m3 = ids
  return ids
}

const runDash = async (user, id, body = {}) => (await as(user, 'POST', `/api/matrix/dashboards/${id}/run`, body)).json
const plainRows = (rows) => JSON.stringify((rows ?? []).map((r) => Object.fromEntries(Object.entries(r).filter(([k]) => !k.startsWith('_')))))
const numbersOf = (w) => JSON.stringify([w.value, plainRows(w.rows), (w.trend ?? []).map((p) => p.value)])
const widgetOf = (run, id) => run?.widgets?.find((w) => w.id === id)

function activeMaster() {
  return sheetObjects('fx-master.xlsx', 'HRM').map((r, i) => ({ ...r, _src: i + 2 })).filter((r) => (r.EmpStatusName ?? '').trim() === 'Active')
}
const presentOf = (A) => A.filter((r) => ['P', 'LT'].includes(r.Status)).length

/** The fixture file a dataset came from (the eval's own uploads). */
const fileOf = async (id) => (await sql('select u.FileName f from MxDatasets d join MxUploads u on u.Id = d.UploadId where d.Id = ?', id))[0]?.f

/** Active staff whose designation the current lookup lacks — written here again, from the workbooks. */
async function noGradeOf(rows) {
  const id = (await sql("select Id from MxDatasets where ReportKind = 'designation-lookup' and IsCurrent = 1 order by Id desc limit 1"))[0]?.Id
  const lookup = new Set(sheetObjects(await fileOf(id), 'Sheet1').map((r) => String(r.DesignationName ?? '').trim().toLowerCase()))
  return rows.filter((r) => ['Management', 'Non-Management'].includes(r.EmpTypeName) && !lookup.has(String(r.DesignationName ?? '').trim().toLowerCase())).length
}

// ---- G-M3.1 Automatic dashboards ----

check('G-M3.1', 'M3', 'each fixture kind creates its automatic dashboard; every widget\'s numbers equal its own KPI or query run again, and the main tiles an independent computation', async () => {
  const S = await m3Setup()
  const want = ['manpower-overview', 'line-board', 'attendance-today', 'joiners-separations', 'data-quality']
  const missing = want.filter((a) => !S.dash[a])
  let widgets = 0, equal = 0
  const bad = [], runs = {}
  for (const a of want.filter((x) => S.dash[x])) {
    const run = runs[a] = await runDash(ctx.admin, S.dash[a])
    for (const w of run?.widgets ?? []) {
      widgets++
      if (w.error) { bad.push(`${a}/${w.id}: ${w.error}`); continue }
      const again = w.kpi ? (await runKpi(ctx.admin, w.kpi, w.input)).json : (await asAdmin('POST', '/api/matrix/query', { query: w.query })).json
      const same = plainRows(again?.rows) === plainRows(w.rows) && (!w.kpi || again?.value === w.value)
      if (same) equal++; else bad.push(`${a}/${w.id} differs from its own run`)
    }
  }
  const active = activeMaster()
  const A2 = sheetObjects('fx-attendance-2.xlsx', 'rptDailyAttDetailsReport_Factor', 3)
  const H = sheetObjects('fx-hr-basic.xlsx', 'Sheet1')
  // Joiners: the current manpower snapshot against the one before it, whichever files those are in this run.
  const jw = widgetOf(runs['joiners-separations'], 'joiners')
  const ids = (file) => new Set(sheetObjects(file, 'Sheet1').map((r) => r['Employee ID']))
  // The snapshot before, by the rule written again here: same kind and layout, an earlier report date, or the same date imported earlier.
  const cur = (await sql("select ReportKind k, TemplateId t, coalesce(ReportDate, '') r from MxDatasets where Id = ?", jw?.datasetId ?? -1))[0]
  const prevId = cur && (await sql("select Id from MxDatasets where ReportKind = ? and TemplateId = ? and Id <> ? and (coalesce(ReportDate, '') < ? or (coalesce(ReportDate, '') = ? and Id < ?)) order by coalesce(ReportDate, '') desc, Id desc limit 1",
    cur.k, cur.t, jw.datasetId, cur.r, cur.r, jw.datasetId))[0]?.Id
  const joiners = prevId
    ? await (async () => { const now = ids(await fileOf(jw.datasetId)), before = ids(await fileOf(prevId)); return [...now].filter((x) => !before.has(x)).length })()
    : 'no previous snapshot'
  const independent = [
    ['manpower-overview/headcount', widgetOf(runs['manpower-overview'], 'headcount')?.value, active.length],
    ['line-board/lines', widgetOf(runs['line-board'], 'lines')?.value, new Set(active.filter((r) => r.SectionName === 'Sewing' && lineOf(r.WingName) !== null).map((r) => lineOf(r.WingName))).size],
    ['attendance-today/present (20 May)', widgetOf(runs['attendance-today'], 'present')?.value, presentOf(A2)],
    ['joiners-separations/separations', widgetOf(runs['joiners-separations'], 'separations')?.value, H.filter((r) => r.EmpStatusName && r.EmpStatusName !== 'Active').length],
    ['joiners-separations/joiners', jw?.value, joiners],
    ['manpower-overview/no-grade', widgetOf(runs['manpower-overview'], 'no-grade')?.value, await noGradeOf(active)],
  ]
  const wrong = independent.filter(([, got, exp]) => got !== exp)
  return ok(!missing.length && widgets > 30 && equal === widgets && !wrong.length,
    `dashboards ${want.filter((a) => S.dash[a]).join(', ')}${missing.length ? ` · MISSING ${missing.join(', ')}` : ''} · ${equal}/${widgets} widgets equal their own run`
    + `${bad.length ? ` · ${bad.slice(0, 6).join(' | ')}` : ''} · independent: ${independent.map(([n, g, e]) => `${n} ${g}${g === e ? '' : ` ≠ ${e}`}`).join(', ')}`)
})

// ---- G-M3.2 Filters and drill-down ----

check('G-M3.2', 'M3', 'a unit filter changes every widget of the Manpower overview and the Line board, to the independent numbers', async () => {
  const S = await m3Setup()
  const active = activeMaster()
  const units = Object.entries(countBy(active, (r) => r.UnitName)).filter(([u]) => u !== 'null').sort((a, b) => b[1] - a[1])
  const unit = units[0][0]
  const out = []
  let pass = true
  for (const a of ['manpower-overview', 'line-board']) {
    const all = await runDash(ctx.admin, S.dash[a])
    const one = await runDash(ctx.admin, S.dash[a], { filters: { unit: [unit] } })
    // Sewing lines: every fixture unit runs all of them, so that tile keeps its number — it must still equal the unit's own count.
    const unitLines = new Set(active.filter((r) => r.UnitName === unit && r.SectionName === 'Sewing' && lineOf(r.WingName) !== null).map((r) => lineOf(r.WingName))).size
    // Staff without a grade mapping: the unit may hold every one of them (it depends on the current lookup).
    const unitNoGrade = await noGradeOf(active.filter((r) => r.UnitName === unit))
    const same = (w) => (w.id === 'lines' && w.value === unitLines) || (w.id === 'no-grade' && w.value === unitNoGrade)
    const notApplied = one.widgets.filter((w) => !w.filtersApplied.includes('unit'))
    const unchanged = one.widgets.filter((w) => w.filtersApplied.includes('unit') && numbersOf(w) === numbersOf(widgetOf(all, w.id)) && !same(w))
    pass &&= notApplied.length === 0 && unchanged.length === 0 && one.widgets.every((w) => !w.error)
    out.push(`${a}: the filter reached ${one.widgets.length - notApplied.length}/${one.widgets.length} widgets, changed ${one.widgets.length - unchanged.length - one.widgets.filter(same).length}`
      + `${one.widgets.some(same) ? ` (${one.widgets.filter(same).map((w) => `${w.id} ${w.value}`).join(', ')}: the same as for every unit, and equal to the unit's independent count)` : ''}`
      + `${unchanged.length ? ` · unchanged: ${unchanged.map((w) => w.id).join(', ')}` : ''}${notApplied.length ? ` · not reached: ${notApplied.map((w) => w.id).join(', ')}` : ''}`)
    if (a === 'manpower-overview') {
      const got = widgetOf(one, 'headcount')?.value, exp = active.filter((r) => r.UnitName === unit).length
      pass &&= got === exp
      const ng = widgetOf(one, 'no-grade')?.value
      pass &&= ng === unitNoGrade
      out.push(`headcount in the unit ${got} (independent ${exp}) · staff without a grade mapping ${ng} (independent ${unitNoGrade})`)
    } else {
      const ops = {}
      for (const r of active.filter((x) => x.UnitName === unit && x.SectionName === 'Sewing' && lineOf(x.WingName) !== null))
        ops[lineOf(r.WingName)] = (ops[lineOf(r.WingName)] ?? 0) + (/operator/i.test(r.DesignationName ?? '') ? 1 : 0)
      const got = Object.fromEntries((widgetOf(one, 'board')?.rows ?? []).map((r) => [String(r.line_no), r.operators]))
      pass &&= sameMap(ops, got)
      out.push(`line board operators per line equal the independent count ${sameMap(ops, got)} over ${Object.keys(ops).length} lines`)
    }
  }
  const att = await runDash(ctx.admin, S.dash['attendance-today'], { filters: { unit: [unit] } })
  out.push(`attendance today (for the record): the unit filter reaches ${att.widgets.filter((w) => w.filtersApplied.includes('unit')).map((w) => w.id).join(', ') || 'none'}; the others say it does not apply`)
  return ok(pass, `unit "${unit}" · ${out.join(' · ')}`)
})

check('G-M3.2', 'M3', 'clicking a line\'s bar opens the viewer with exactly that line\'s operators highlighted (screenshots)', async () => {
  const S = await m3Setup()
  const shots = ctx.args.shots
  fs.mkdirSync(shots, { recursive: true })
  const browser = await launchBrowser({ base: ctx.api.url, profileRoot: EVAL_ROOT })
  try {
    const p = await browser.page()
    await p.session(ctx.admin, 'light', 1280, 900, `/matrix?dashboard=${S.dash['line-board']}`)
    await p.waitFor('[data-widget="operators"] [data-chart-ready]', 20000)
    await p.shot(path.join(shots, 'line-board-light-1280.png'), { full: true })
    const target = await p.eval(`(async () => {
      const el = document.querySelector('[data-widget="operators"] [data-matrix-chart]')
      el.scrollIntoView({ block: 'center' })
      await new Promise((r) => setTimeout(r, 400))
      const c = el.__mxChart, rows = el.__mxRows
      const i = Math.floor(rows.length / 2)
      const top = c.convertToPixel({ seriesIndex: 0 }, [i, rows[i].value]), base = c.convertToPixel({ seriesIndex: 0 }, [i, 0])
      const box = el.getBoundingClientRect()
      return { x: box.left + top[0], y: box.top + (top[1] + base[1]) / 2, line: rows[i].line_no, value: rows[i].value }
    })()`)
    await p.mouseClick(target.x, target.y)
    await p.waitFor('[data-matrix-viewer] [data-hl-row]', 15000)
    const url = new URL(await p.eval('location.href'))
    const shown = await p.eval(`({ total: Number(document.querySelector('[data-matrix-viewer]').getAttribute('data-total')),
      rows: [...document.querySelectorAll('[data-hl-row]')].map((e) => Number(e.getAttribute('data-src-row'))) })`)
    await p.shot(path.join(shots, 'line-drill-viewer-light-1280.png'))
    const want = activeMaster().filter((r) => r.SectionName === 'Sewing' && lineOf(r.WingName) === target.line && /operator/i.test(r.DesignationName ?? '')).map((r) => r._src)
    const api = (await asAdmin('GET', `/api/matrix/datasets/${url.searchParams.get('dataset')}/rows?limit=2000&only=true&highlight=${encodeURIComponent(url.searchParams.get('highlight') ?? '')}`)).json
    const apiRows = (api?.rows ?? []).map((r) => r._row)
    const exact = apiRows.length === want.length && want.every((r) => apiRows.includes(r)) && shown.rows.every((r) => want.includes(r))
    return ok(Number(url.searchParams.get('dataset')) === S.master && shown.total === want.length && exact && target.value === want.length,
      `clicked line ${target.line} (bar ${target.value}) → ${url.pathname}?dataset=${url.searchParams.get('dataset')}&highlight=… · viewer ${shown.total} rows, ${shown.rows.length} on screen · `
      + `the token reopens ${apiRows.length} rows, exactly the independent ${want.length} operators of that line: ${exact}`)
  } finally { await browser.close() }
})

// ---- G-M3.3 Editor ----

check('G-M3.3', 'M3', 'editor through the API: add, move, resize, change type, rename, save, reload; bad layouts refused; a non-editor gets 403', async () => {
  const S = await m3Setup()
  const u = await users()
  const copy = await as(u.sys, 'POST', '/api/matrix/dashboards', { copyOf: S.dash['manpower-overview'], name: 'Eval copy' })
  const layout = structuredClone(copy.json?.layout ?? { widgets: [] })
  const w = (id) => layout.widgets.find((x) => x.id === id)
  layout.widgets.push({ id: 'eval-new', type: 'bar', kpi: 'headcount_by_designation', dataset: 'current:master', x: 0, y: 30, w: 6, h: 4 })
  Object.assign(w('by-gender'), { x: 6, y: 18 })                     // move
  Object.assign(w('by-grade'), { w: 8, h: 5 })                       // resize
  w('by-section').type = 'bar'                                       // change type
  w('tenure').title = { en: 'Years of service', bn: 'চাকরির বছর' }    // rename a widget
  const put = await as(u.sys, 'PUT', `/api/matrix/dashboards/${copy.json?.id}`, { name: 'Eval board', layout })
  const back = (await as(u.sys, 'GET', `/api/matrix/dashboards/${copy.json?.id}`)).json
  const same = JSON.stringify(back?.layout?.widgets) === JSON.stringify(layout.widgets) && back?.name === 'Eval board'
  const bad1 = await as(u.sys, 'PUT', `/api/matrix/dashboards/${copy.json?.id}`, { layout: { widgets: [{ ...layout.widgets[0], x: 10, w: 4 }] } })
  const bad2 = await as(u.sys, 'PUT', `/api/matrix/dashboards/${copy.json?.id}`, { layout: { widgets: [{ id: 'q', type: 'bar', query: { dataset: 'current:master', filter: [['section', '=', "x'; DROP TABLE MxDashboards; --"]] }, x: 0, y: 0, w: 6, h: 4 }] } })
  const bad3 = await as(u.sys, 'PUT', `/api/matrix/dashboards/${copy.json?.id}`, { layout: { widgets: [{ id: 'q', type: 'pie3d', kpi: 'active_headcount', x: 0, y: 0, w: 6, h: 4 }] } })
  const viewer = {
    get: (await as(u.limited, 'GET', `/api/matrix/dashboards/${copy.json?.id}`)).status,
    put: (await as(u.limited, 'PUT', `/api/matrix/dashboards/${copy.json?.id}`, { name: 'x' })).status,
    post: (await as(u.limited, 'POST', '/api/matrix/dashboards', { name: 'x' })).status,
    del: (await as(u.limited, 'DELETE', `/api/matrix/dashboards/${copy.json?.id}`)).status,
    canEdit: (await as(u.limited, 'GET', `/api/matrix/dashboards/${copy.json?.id}`)).json?.canEdit,
  }
  const autoDel = (await as(u.sys, 'DELETE', `/api/matrix/dashboards/${S.dash['manpower-overview']}`)).status
  ctx.m3.copy = copy.json?.id
  return ok(copy.status === 201 && put.status === 200 && same && bad1.status === 400 && bad2.status === 400 && bad3.status === 400
      && viewer.get === 200 && viewer.put === 403 && viewer.post === 403 && viewer.del === 403 && viewer.canEdit === false && autoDel === 400,
    `copy ${copy.status} · save ${put.status} · reloaded layout and name identical ${same} · refused: off the grid ${bad1.status} "${bad1.json?.error}", injection ${bad2.status} "${bad2.json?.error}", unknown type ${bad3.status} · `
    + `non-editor: read ${viewer.get}, canEdit ${viewer.canEdit}, save ${viewer.put}, create ${viewer.post}, delete ${viewer.del} · deleting an automatic dashboard ${autoDel}`)
})

check('G-M3.3', 'M3', 'editor in the browser: rename, change type, move, resize, add, save and reload; a non-editor sees no edit controls (screenshots)', async () => {
  const S = await m3Setup()
  const u = await users()
  const copy = (await as(u.sys, 'POST', '/api/matrix/dashboards', { copyOf: S.dash['manpower-overview'], name: 'Eval UI copy' })).json
  const before = copy.layout.widgets.find((w) => w.id === 'by-unit')
  const shots = ctx.args.shots
  const browser = await launchBrowser({ base: ctx.api.url, profileRoot: EVAL_ROOT })
  try {
    const p = await browser.page()
    await p.session(u.sys, 'light', 1280, 900, `/matrix?dashboard=${copy.id}`)
    await p.waitFor('[data-widget="by-unit"] [data-chart-ready]', 20000)
    await p.click('[data-dash-edit]')
    await p.type('[data-dash-name]', 'Eval UI board')
    await p.eval(`(() => { const s = document.querySelector('[data-widget="by-unit"] [data-w-type]'); s.value = 'donut'; s.dispatchEvent(new Event('change', { bubbles: true })) })(); 0`)
    await p.click('[data-widget="by-unit"] [data-w-move="down"]')
    await p.click('[data-widget="by-unit"] [data-w-wider]')
    await p.eval(`(() => { for (const [sel, v] of [['[data-add-kpi]', 'headcount_by_department'], ['[data-add-type]', 'bar']]) { const s = document.querySelector(sel); s.value = v; s.dispatchEvent(new Event('change', { bubbles: true })) } })(); 0`)
    await p.click('[data-add-widget]')
    await p.shot(path.join(shots, 'editor-light-1280.png'), { full: true })
    await p.click('[data-dash-save]')
    await p.waitFor('[data-dash-saved]', 10000)
    await p.reload()
    await p.waitFor('[data-widget="by-unit"] [data-chart-ready]', 20000)
    const title = await p.eval('document.querySelector("[data-dash-title]")?.textContent?.trim()')
    const saved = (await as(u.sys, 'GET', `/api/matrix/dashboards/${copy.id}`)).json
    const after = saved.layout.widgets.find((w) => w.id === 'by-unit')
    const added = saved.layout.widgets.find((w) => w.kpi === 'headcount_by_department' && w.type === 'bar')
    const okUi = saved.name === 'Eval UI board' && title?.includes('Eval UI board') && after.type === 'donut' && after.y === before.y + 1 && after.w === before.w + 1 && !!added

    const q = await browser.page()
    await q.session(u.limited, 'light', 1280, 900, `/matrix?dashboard=${copy.id}`)
    await q.waitFor('[data-widget="by-unit"] [data-chart-ready]', 20000)
    const controls = await q.eval('document.querySelectorAll("[data-dash-edit], [data-dash-save], [data-w-type], [data-add-widget]").length')
    await q.shot(path.join(shots, 'viewer-only-light-1280.png'))
    return ok(okUi && controls === 0,
      `saved and reloaded: name "${saved.name}" (shown "${title}") · by-unit type ${before.type}→${after.type}, y ${before.y}→${after.y}, w ${before.w}→${after.w} · added ${added ? `${added.id} (${added.kpi}, ${added.type})` : 'nothing'} · non-editor edit controls ${controls}`)
  } finally { await browser.close() }
})

// ---- G-M3.4 Thresholds and trends ----

check('G-M3.4', 'M3', 'KPI tiles colour by thresholds (seeded, then set by a manager); trends use the snapshot series; compare with previous gives the change', async () => {
  const S = await m3Setup()
  const u = await users()
  const A = sheetObjects('fx-attendance.xlsx', 'rptDailyAttDetailsReport_Factor', 3)
  const A2 = sheetObjects('fx-attendance-2.xlsx', 'rptDailyAttDetailsReport_Factor', 3)
  const latePct = round2((100 * A2.filter((r) => r.Status === 'LT').length) / presentOf(A2))
  const stateOf = (v, t) => t.direction === 'lower' ? (v <= t.green ? 'green' : v <= t.amber ? 'amber' : 'red') : (v >= t.green ? 'green' : v >= t.amber ? 'amber' : 'red')
  const seeded = { direction: 'lower', green: 5, amber: 10 }
  const run0 = await runDash(ctx.admin, S.dash['attendance-today'])
  const tile = widgetOf(run0, 'late-pct')
  const states = []
  for (const [want, t] of [['green', { direction: 'lower', green: latePct + 1, amber: latePct + 2 }], ['amber', { direction: 'lower', green: latePct - 2, amber: latePct + 1 }], ['red', { direction: 'lower', green: latePct - 2, amber: latePct - 1 }]]) {
    const put = await asAdmin('PUT', '/api/matrix/kpis/late_pct', { thresholds: t })
    states.push(`${want}:${put.status === 200 ? widgetOf(await runDash(ctx.admin, S.dash['attendance-today']), 'late-pct')?.state : put.status}`)
  }
  const refused = (await asAdmin('PUT', '/api/matrix/kpis/late_pct', { thresholds: { direction: 'lower', green: 10, amber: 5 } })).status
  const sysPut = (await as(u.sys, 'PUT', '/api/matrix/kpis/late_pct', { thresholds: seeded })).status
  await asAdmin('PUT', '/api/matrix/kpis/late_pct', { thresholds: seeded })

  const trend = widgetOf(run0, 'trend')?.trend ?? []
  const trendOk = trend.length === 2 && trend[0].reportDate === ctx.M.attendance.reportDate && trend[0].value === presentOf(A) && trend[1].value === presentOf(A2)
  const mpTrend = widgetOf(await runDash(ctx.admin, S.dash['joiners-separations']), 'trend')?.trend ?? []
  const mpDates = mpTrend.map((x) => x.reportDate)
  const mpOk = mpDates.includes(ctx.M.manpower2.previousReportDate) && mpDates.includes(ctx.M.manpower2.reportDate) && mpDates.every((d, i) => i === 0 || d >= mpDates[i - 1])
  const present = widgetOf(run0, 'present')
  const cmpOk = present?.compare?.before === presentOf(A) && present?.compare?.after === presentOf(A2) && present?.compare?.change === presentOf(A2) - presentOf(A)
  const withRows = widgetOf(await runDash(ctx.admin, S.dash['attendance-today'], { compare: true }), 'by-dept')
  const prevRun = (await runKpi(ctx.admin, 'present_by_department', { dataset: withRows?.compare?.previousId })).json
  const rowsOk = !!withRows?.compare?.rows && plainRows(withRows.compare.rows) === plainRows(prevRun?.rows)
  return ok(tile?.value === latePct && tile?.state === stateOf(latePct, seeded) && states.join() === 'green:green,amber:amber,red:red' && refused === 400 && sysPut === 403
      && trendOk && mpOk && cmpOk && rowsOk,
    `late % ${tile?.value} (independent ${latePct}) seeded state ${tile?.state} · set by a manager → ${states.join(' ')} · amber below green refused ${refused} · System without kpis.edit ${sysPut} · `
    + `present trend ${trend.map((x) => `${x.reportDate}:${x.value}`).join(' → ')} (independent ${presentOf(A)} → ${presentOf(A2)}) · manpower trend ${mpDates.join(' → ')} · `
    + `compare: present ${present?.compare?.before} → ${present?.compare?.after} (${present?.compare?.change}) · by department against ${withRows?.compare?.previousDate} equal to its own run ${rowsOk}`)
})

check('G-M3.4', 'M3', 'in the browser: tiles carry their threshold state, the trend chart has the snapshot points, "compare with previous" shows the change', async () => {
  const S = await m3Setup()
  const run = await runDash(ctx.admin, S.dash['attendance-today'])
  const browser = await launchBrowser({ base: ctx.api.url, profileRoot: EVAL_ROOT })
  try {
    const p = await browser.page()
    await p.session(ctx.admin, 'light', 1280, 900, `/matrix?dashboard=${S.dash['attendance-today']}`)
    await p.waitFor('[data-widget="trend"] [data-chart-ready]', 20000)
    const tiles = await p.eval(`Object.fromEntries([...document.querySelectorAll('[data-widget][data-type="kpi"]')].map((e) => [e.getAttribute('data-widget'), e.getAttribute('data-state') || null]))`)
    const points = await p.eval(`document.querySelector('[data-widget="trend"] [data-matrix-chart]').__mxChart.getOption().series[0].data.length`)
    await p.click('[data-dash-compare]')
    await p.waitFor('[data-widget="present"] [data-compare]', 10000)
    const change = await p.eval(`document.querySelector('[data-widget="present"] [data-compare]').getAttribute('data-change')`)
    await p.shot(path.join(ctx.args.shots, 'attendance-compare-light-1280.png'), { full: true })
    const want = Object.fromEntries(run.widgets.filter((w) => w.type === 'kpi').map((w) => [w.id, w.state ?? null]))
    return ok(JSON.stringify(tiles) === JSON.stringify(want) && points === 2 && Number(change) === widgetOf(run, 'present').compare.change,
      `tile states ${JSON.stringify(tiles)} (API ${JSON.stringify(want)}) · trend points ${points} · present change shown ${change}`)
  } finally { await browser.close() }
})

// ---- G-M3.5 Export ----

async function download(user, url) {
  const res = await fetch(`${ctx.api.url}${url}`, { headers: { Authorization: `Bearer ${user.token}` } })
  return { status: res.status, name: /filename="?([^";]+)/.exec(res.headers.get('content-disposition') ?? '')?.[1], bytes: Buffer.from(await res.arrayBuffer()) }
}

function csvHeader(bytes) {
  const text = bytes.toString('utf8').replace(/^﻿/, '')
  const line = text.split(/\r?\n/)[0]
  const out = []; let cur = '', q = false
  for (const ch of line) { if (ch === '"') q = !q; else if (ch === ',' && !q) { out.push(cur); cur = '' } else cur += ch }
  out.push(cur)
  return { headers: out, rows: text.split(/\r?\n/).filter(Boolean).length - 1 }
}

function xlsxSheet(bytes, name) {
  const file = path.join(EVAL_ROOT, `export-${name}.xlsx`)
  fs.writeFileSync(file, bytes)
  const sheet = readWorkbook(file)[0]
  const headers = [], cols = new Map()
  let rows = 0
  for (const [ref, v] of sheet.cells) {
    const m = /^([A-Z]+)(\d+)$/.exec(ref)
    if (m[2] === '1') { headers.push(v); cols.set(v, m[1]) } else rows = Math.max(rows, Number(m[2]) - 1)
  }
  const column = (header) => [...sheet.cells].filter(([ref]) => ref.replace(/\d+$/, '') === cols.get(header) && !/^[A-Z]+1$/.test(ref)).map(([, v]) => v)
  return { headers, rows, column }
}

check('G-M3.5', 'M3', 'dataset export: .xlsx and .csv without sensitive columns, without salary for a user without view-salary, without names without view-personal (headers listed)', async () => {
  const S = await m3Setup()
  const u = await users()
  const ds = (await asAdmin('GET', `/api/matrix/datasets/${S.master}`)).json
  const salary = ds.columns.filter((c) => c.sensitivity === 'salary').map((c) => c.header.trim())
  const personal = ds.columns.filter((c) => c.sensitivity === 'personal').map((c) => c.header.trim())
  const out = []
  let pass = true
  for (const [who, user, seeSalary, seePersonal] of [['admin', ctx.admin, true, true], ['System', u.sys, false, true], ['limited', u.limited, false, false]]) {
    for (const kind of ['xlsx', 'csv']) {
      const f = await download(user, `/api/matrix/export/${kind}/${S.master}`)
      const t = kind === 'xlsx' ? xlsxSheet(f.bytes, `${who}`) : csvHeader(f.bytes)
      const sens = t.headers.filter((h) => SENSITIVE_COLUMN.test(String(h).replace(/[^a-z]/gi, '').toLowerCase()))
      const sal = t.headers.filter((h) => salary.includes(String(h).trim()))
      const per = t.headers.filter((h) => personal.includes(String(h).trim()))
      const zeros = kind === 'xlsx' ? t.column('EmpID').filter((v) => /^00\d{4}$/.test(String(v))).length : 0
      const good = f.status === 200 && t.rows === ds.rowCount && t.headers[0] === 'Source row' && sens.length === 0
        && (seeSalary ? sal.length === salary.length : sal.length === 0) && (seePersonal ? per.length === personal.length : per.length === 0)
        && (kind !== 'xlsx' || zeros > 0)
      pass &&= good
      out.push(`${who} ${kind} ${f.status} ${f.name}: ${t.headers.length} columns, ${t.rows} rows, salary ${sal.length}, names ${per.length}, sensitive ${sens.length}${kind === 'xlsx' ? `, IDs kept with their leading zeros ${zeros}` : ''}${good ? '' : ' ✗'}`)
    }
  }
  const sysHeaders = csvHeader((await download(u.sys, `/api/matrix/export/csv/${S.master}`)).bytes).headers
  const denied = [(await download(u.gen, `/api/matrix/export/csv/${S.master}`)).status, (await download(u.dev, `/api/matrix/export/xlsx/${S.master}`)).status]
  const layout = await download(ctx.admin, `/api/matrix/export/dashboard/${S.dash['line-board']}`)
  const layoutOk = layout.status === 200 && JSON.parse(layout.bytes.toString('utf8')).layout?.widgets?.length > 0
  return ok(pass && denied.every((s) => s === 403) && layoutOk,
    `${out.join(' · ')} · General and DevOps ${denied.join(', ')} · dashboard layout ${layout.status} ${layout.name} · System's headers: ${sysHeaders.join(', ')}`)
})

check('G-M3.5', 'M3', 'PNG of each chart (ECharts\' own export), and a print view that the browser prints to PDF (screenshots)', async () => {
  const S = await m3Setup()
  const browser = await launchBrowser({ base: ctx.api.url, profileRoot: EVAL_ROOT })
  try {
    const p = await browser.page()
    await p.session(ctx.admin, 'light', 1280, 900, `/matrix?dashboard=${S.dash['manpower-overview']}`)
    await p.waitFor('[data-widget="trend"] [data-chart-ready]', 20000)
    const pngs = await p.eval(`[...document.querySelectorAll('[data-matrix-chart]')].map((el) => {
      const url = el.__mxChart.getDataURL({ type: 'png', pixelRatio: 1 })
      const bin = atob(url.split(',')[1])
      const u32 = (o) => ((bin.charCodeAt(o) << 24) | (bin.charCodeAt(o + 1) << 16) | (bin.charCodeAt(o + 2) << 8) | bin.charCodeAt(o + 3)) >>> 0
      return { png: bin.slice(1, 4) === 'PNG', w: u32(16), h: u32(20), bytes: bin.length, button: !!el.closest('[data-widget]').querySelector('[data-png]') }
    })`)
    await p.session(ctx.admin, 'light', 1280, 900, `/matrix?dashboard=${S.dash['manpower-overview']}&print=1`)
    await p.waitFor('[data-print-view] [data-widget="trend"] [data-chart-ready]', 20000)
    const view = await p.eval(`({ widgets: document.querySelectorAll('[data-print-view] [data-widget]').length, edit: document.querySelectorAll('[data-dash-edit], [data-png], [data-matrix-section], [data-dash-filters]').length })`)
    const pdf = await p.pdf()
    fs.writeFileSync(path.join(ctx.args.shots, 'manpower-overview-print.pdf'), pdf)
    await p.shot(path.join(ctx.args.shots, 'manpower-overview-print-1280.png'), { full: true })
    const run = await runDash(ctx.admin, S.dash['manpower-overview'])
    const charts = run.widgets.filter((w) => w.type !== 'kpi' && w.type !== 'table' && w.type !== 'line-board').length
    return ok(pngs.length === charts && pngs.every((x) => x.png && x.w > 100 && x.h > 100 && x.button) && view.widgets === run.widgets.length && view.edit === 0 && pdf.subarray(0, 4).toString() === '%PDF' && pdf.length > 10000,
      `${pngs.length} charts (${charts} chart widgets): every one a PNG ${pngs.every((x) => x.png)}, e.g. ${pngs[0]?.w}×${pngs[0]?.h} px, each with its PNG button ${pngs.every((x) => x.button)} · print view ${view.widgets}/${run.widgets.length} widgets, controls ${view.edit} · PDF ${pdf.length} bytes`)
  } finally { await browser.close() }
})

// ---- G-M3.7 Colours from theme tokens ----

check('G-M3.7', 'M3', 'no hard-coded colours in Matrix code; chart colours are the theme tokens read at runtime (light and dark differ)', async () => {
  const files = fs.readdirSync(path.join(REPO, 'frontend/src/components/matrix')).map((f) => `frontend/src/components/matrix/${f}`)
    .concat(['frontend/src/components/tabs/MatrixTab.vue', 'frontend/src/lib/matrix-format.js', 'frontend/src/lib/matrix-chart.js'].filter(exists))
  const literal = /#[0-9a-f]{3,8}\b(?![-\w])|\brgba?\(|\bhsla?\((?!var|\$\{)|\b(?:color|background|fill|stroke)\s*:\s*['"](?:red|green|blue|orange|black|white|gray|grey)['"]/i
  const hits = files.flatMap((f) => read(f).split('\n').map((l, i) => [f, i + 1, l]).filter(([, , l]) => literal.test(l) && !/^\s*(\/\/|\/?\*|<!--)/.test(l)).map(([f, n]) => `${f}:${n}`))
  const css = read('frontend/src/assets/index.css')
  const tokens = ['--chart-1', '--chart-8', '--status-green', '--status-amber', '--status-red']
  const inBoth = tokens.every((t) => { const at = css.indexOf('.dark {'); return css.slice(0, at).includes(t + ':') && css.slice(at).includes(t + ':') })
  const chartSrc = read('frontend/src/components/matrix/MatrixChart.vue')
  const S = await m3Setup()
  const browser = await launchBrowser({ base: ctx.api.url, profileRoot: EVAL_ROOT })
  try {
    const p = await browser.page()
    const probe = `(() => { const css = getComputedStyle(document.documentElement); const c = document.querySelector('[data-widget="by-unit"] [data-matrix-chart]').__mxChart;
      return { option: c.getOption().color.slice(0, 3), tokens: [1, 2, 3].map((i) => css.getPropertyValue('--chart-' + i).trim()) } })()`
    await p.session(ctx.admin, 'light', 1280, 900, `/matrix?dashboard=${S.dash['manpower-overview']}`)
    await p.waitFor('[data-widget="by-unit"] [data-chart-ready]', 20000)
    const light = await p.eval(probe)
    await p.setTheme('dark')
    await sleep(800)
    const dark = await p.eval(probe)
    const flat = (s) => String(s).replace(/[\s,]/g, '')
    const resolved = (x) => x.option.every((c, i) => flat(c) === flat(`hsl(${x.tokens[i]})`))
    return ok(hits.length === 0 && inBoth && /getComputedStyle/.test(chartSrc) && /--chart-/.test(chartSrc) && resolved(light) && resolved(dark) && light.option[0] !== dark.option[0],
      `literal colours ${hits.length ? hits.join(', ') : 'none'} in ${files.length} files · tokens in light and dark ${inBoth} · chart colours light ${light.option.join(' ')} = tokens ${resolved(light)} · dark ${dark.option.join(' ')} = tokens ${resolved(dark)}`)
  } finally { await browser.close() }
})

// ---- G-M3.8–9 Secrets: none in committed config, none read from appsettings ----

check('G-M3.8', 'M3', 'no key-, token- or password-looking value in any committed (or staged) config file (the scanner proven on planted samples)', async () => {
  const { files, findings, unstaged } = scanRepo(REPO)
  // Planted samples, built here so no real-looking secret is written into this file.
  const fake = (prefix, n) => prefix + 'Ab3'.repeat(Math.ceil(n / 3)).slice(0, n)
  const positives = [
    `{ "OllamaCloud": { "ApiKey": "${fake('', 40)}" } }`,
    `GROQ_API_KEY=${fake('gsk_', 30)}`,
    `      OllamaCloud__ApiKey: ${fake('', 32)}`,
    `  ConnectionStrings__Default = "Data Source=x.db;Password=${fake('', 12)}"`,
    `    //"Default": "libsql://db.example.invalid?authToken=${fake('eyJ', 20)}.${fake('eyJ', 20)}.${fake('', 20)}"`,
    `"Url": "https://admin:${fake('', 10)}@host.example.invalid"`,
  ]
  const negatives = ['"ApiKey": ""', 'OLLAMA_CLOUD_API_KEY=', '      OllamaCloud__ApiKey: ${OLLAMA_CLOUD_API_KEY:-}', '"Key": "openrouter"', '"UserSecretsId": "1b452343-33e8-4e83-99bd-0014a8836328"', '  ConnectionStrings__Default = "Data Source=/data/rabbitic.db"']
  const caught = positives.filter((s) => scanText(s).length > 0).length
  const quiet = negatives.filter((s) => scanText(s).length === 0).length
  return ok(findings.length === 0 && caught === positives.length && quiet === negatives.length && files.some((f) => f.endsWith('appsettings.json')),
    `${files.length} committed config files scanned as git holds them (${files.join(', ')}) · findings ${findings.length ? findings.map((f) => `${f.file}:${f.line} ${f.key ?? ''} (${f.rule})`).join(' | ') : 'none'} · scanner: ${caught}/${positives.length} planted secrets caught, ${quiet}/${negatives.length} placeholders left alone${unstaged.length ? ` · NOT STAGED, keep it so: a secret-looking value in the local copy of ${unstaged.join(', ')}` : ''}`)
})

check('G-M3.9', 'M3', 'a key put in appsettings.json is never read (search reports not_configured, the log names the key, not the value); the environment\'s key is', async () => {
  const bin = path.join(EVAL_ROOT, 'api-bin-secret')
  fs.rmSync(bin, { recursive: true, force: true })
  fs.cpSync(API_BIN_DIR, bin, { recursive: true })
  const planted = crypto.randomBytes(16).toString('hex') + '.' + crypto.randomBytes(12).toString('base64url')
  const file = path.join(bin, 'appsettings.json')
  fs.writeFileSync(file, fs.readFileSync(file, 'utf8').replace(/("ApiKey"\s*:\s*")[^"]*(")/, `$1${planted}$2`))
  const results = []
  for (const [label, env] of [['appsettings only', { OllamaCloud__ApiKey: undefined }], ['environment', { OllamaCloud__ApiKey: 'env-' + crypto.randomBytes(8).toString('hex') }]]) {
    const runDir = fs.mkdtempSync(path.join(EVAL_ROOT, 'secret-'))
    const api = await startApi({ fakeUrl: ctx.fake.url, runDir, binDir: bin, env })
    try {
      const r = await call(api.url, 'POST', '/api/auth/login', { body: { username: process.env.MATRIX_EVAL_ADMIN_USER, secret: process.env.MATRIX_EVAL_ADMIN_SECRET } })
      const h = await call(api.url, 'GET', '/api/search/health', { token: r.json?.token })
      results.push({ label, status: h.status, said: h.json?.status })
    } finally { await api.stop() }
    const log = fs.readFileSync(path.join(runDir, 'api.log'), 'utf8')
    results.at(-1).logged = log.includes('ignored a value for appsettings.json:OllamaCloud:ApiKey')
    results.at(-1).leaked = log.includes(planted)
  }
  const [a, e] = results
  return ok(a.status === 503 && a.said === 'not_configured' && a.logged && !a.leaked && e.said !== 'not_configured' && !e.leaked,
    results.map((x) => `${x.label}: health ${x.status} ${x.said} · warning names the key ${x.logged} · planted value in the log ${x.leaked}`).join(' ‖ '))
})

// ---- G-M3.10 GradeId: "3" and "Grade 3" are one value ----

check('G-M3.10', 'M3', 'GradeId "Grade 3", "Grade 03" and 3 are stored as one value; the change is reported as an info issue', async () => {
  const S = await m3Setup()
  const M = sheetObjects('fx-master.xlsx', 'HRM')
  const spelled = await sql(`select count(*) n from mx_ds_${S.master} where grade_id like '%grade%'`)
  const stored = countBy(await sql(`select grade_id from mx_ds_${S.master}`), (r) => r.grade_id)
  const want = countBy(M, (r) => gradeOf(r.GradeId))
  const issue = (await issuesOf(S.master)).find((i) => i.check === 'grade_forms_unified')
  const plantedForms = M.filter((r) => typeof r.GradeId === 'string' && /grade/i.test(r.GradeId)).length
  return ok(spelled[0].n === 0 && sameMap(want, stored) && issue?.count === ctx.M.master.plants.grade_forms_unified && issue?.count === plantedForms && issue?.severity === 'info',
    `cells still spelled "Grade …" ${spelled[0].n} · grade counts equal the independent normalisation ${sameMap(want, stored)} (${Object.keys(stored).length} grades) · issue ${issue?.check} ${issue?.severity} ×${issue?.count} (planted ${plantedForms}): ${issue?.detail}`)
})

// ---- G-M3.11 A lookup re-check refreshes the cards whose issues it changes ----

check('G-M3.11', 'M3', 'importing a lookup re-checks the datasets; a dataset whose issues change gets its report card made again, the others keep theirs', async () => {
  const S = await m3Setup()
  const stamp = async (id) => (await sql('select CreatedAtUtc from MxReportCards where DatasetId = ? order by Id desc limit 1', id))[0]?.CreatedAtUtc
  const count = async (id) => (await issuesOf(id)).find((i) => i.check === 'designation_not_in_lookup')?.count ?? 0
  const cardCount = async (id) => (await asAdmin('GET', `/api/matrix/datasets/${id}/card`)).json?.quality?.find((q) => q.check === 'designation_not_in_lookup')?.count ?? 0
  const steps = []
  for (const file of ['fx-designation-2.xlsx', 'fx-designation.xlsx', 'fx-designation.xlsx']) {
    const before = { card: await stamp(S.master), att: await stamp(S.att2), n: await count(S.master) }
    await sleep(20)
    const up = (await upload(ctx.admin, [path.join(FIXTURES, file)])).json.uploads[0]
    const st = (await asAdmin('POST', `/api/matrix/uploads/${up.id}/import`, { tables: [{ table: tableOf(up, 'Sheet1').id, confirm: 'suggestions' }] })).status
    const after = { card: await stamp(S.master), att: await stamp(S.att2), n: await count(S.master) }
    steps.push({ file, st, changed: before.n !== after.n, refreshed: before.card !== after.card, att: before.att === after.att, matches: (await cardCount(S.master)) === after.n, from: before.n, to: after.n })
  }
  const pass = steps.every((s) => s.st === 200 && s.att && s.matches && s.refreshed === s.changed) && steps.some((s) => s.changed) && steps.some((s) => !s.changed)
  return ok(pass, steps.map((s) => `${s.file} ${s.st}: master's lookup issue ${s.from}→${s.to}, card made again ${s.refreshed}, card shows the new count ${s.matches}, attendance card untouched ${s.att}`).join(' · '))
})

// ---- G-M3.12 Definitions ----

check('G-M3.12', 'M3', 'Definitions lists every KPI with its formula in words and every field; thresholds are edited there only with kpis.edit (screenshots)', async () => {
  const u = await users()
  const kpis = (await asAdmin('GET', '/api/matrix/kpis')).json?.kpis ?? []
  const fields = (await asAdmin('GET', '/api/matrix/catalog')).json?.fields ?? []
  const browser = await launchBrowser({ base: ctx.api.url, profileRoot: EVAL_ROOT })
  try {
    const p = await browser.page()
    await p.session(ctx.admin, 'light', 1280, 900, '/matrix?section=definitions')
    await p.waitFor('[data-matrix-definitions] [data-kpi]', 15000)
    const shown = await p.eval(`({ kpis: document.querySelectorAll('[data-kpi]').length, words: [...document.querySelectorAll('[data-kpi] [data-kpi-words]')].filter((e) => e.textContent.trim().length > 5).length,
      fields: document.querySelectorAll('[data-field]').length, edit: document.querySelectorAll('[data-threshold-edit]').length })`)
    await p.shot(path.join(ctx.args.shots, 'definitions-light-1280.png'))
    await p.click('[data-kpi="ot_over_2h"] [data-threshold-edit]')
    await p.eval(`(() => { const set = (sel, v) => { const e = document.querySelector('[data-kpi="ot_over_2h"] ' + sel); e.value = v; e.dispatchEvent(new Event(e.tagName === 'SELECT' ? 'change' : 'input', { bubbles: true })) };
      set('[data-th-direction]', 'lower'); set('[data-th-green]', '10'); set('[data-th-amber]', '30') })(); 0`)
    await p.click('[data-kpi="ot_over_2h"] [data-threshold-save]')
    await sleep(800)
    const saved = (await asAdmin('GET', '/api/matrix/kpis')).json.kpis.find((k) => k.key === 'ot_over_2h')?.thresholds
    await asAdmin('PUT', '/api/matrix/kpis/ot_over_2h', { thresholds: { cleared: true } })
    const q = await browser.page()
    await q.session(u.sys, 'light', 1280, 900, '/matrix?section=definitions')
    await q.waitFor('[data-matrix-definitions] [data-kpi]', 15000)
    const sysEdit = await q.eval('document.querySelectorAll("[data-threshold-edit]").length')
    return ok(shown.kpis === kpis.length && shown.words === kpis.length && shown.fields === fields.length && shown.edit === kpis.length
        && saved?.direction === 'lower' && saved?.green === 10 && saved?.amber === 30 && sysEdit === 0,
      `${shown.kpis}/${kpis.length} KPIs, ${shown.words} with their formula in words · ${shown.fields}/${fields.length} fields · admin edit controls ${shown.edit}, saved through the page ${JSON.stringify(saved)} · System (no kpis.edit) edit controls ${sysEdit}`)
  } finally { await browser.close() }
})

// ---- G-M3.13–15 The Report page (a dataset opens on its report, the table is the Data tab) ----

check('G-M3.13', 'M3', 'an import through the page lands on the Report: its tiles are the report card\'s, and each opens exactly its rows (screenshots)', async () => {
  await m3Setup()
  const shots = ctx.args.shots
  const browser = await launchBrowser({ base: ctx.api.url, profileRoot: EVAL_ROOT })
  try {
    const p = await browser.page()
    await p.session(ctx.admin, 'light', 1280, 900, '/matrix')
    // Choose the file in the upload box, as a person would; its layout is known, so it imports at once.
    await p.setFiles('[data-matrix-upload]', [path.join(FIXTURES, 'fx-attendance-2.xlsx')])
    await p.waitFor('[data-report] [data-card-item]', 30000)
    const id = Number(new URL(await p.eval('location.href')).searchParams.get('dataset'))
    const tab = await p.eval('document.querySelector("[data-dataset-tab][aria-current=page]")?.getAttribute("data-dataset-tab")')
    await sleep(800)
    await p.shot(path.join(shots, 'report-attendance-light-1280.png'))
    const card = (await asAdmin('GET', `/api/matrix/datasets/${id}/card`)).json
    const tiles = await p.eval(`[...document.querySelectorAll('[data-card-item]')].map((e) => [e.getAttribute('data-card-item'), e.getAttribute('data-value')])`)
    const sameTiles = JSON.stringify(tiles) === JSON.stringify(card.items.map((i) => [i.key, i.value === null ? null : String(i.value)]))
    // Each counted tile opens the viewer on exactly as many rows as it says.
    const opened = []
    for (const item of card.items.filter((i) => i.unit === 'people' && i.cite).slice(0, 4)) {
      await p.session(ctx.admin, 'light', 1280, 900, `/matrix?dataset=${id}`)
      await p.click(`[data-card-item="${item.key}"]`)
      await p.waitFor('[data-matrix-viewer]', 15000)
      await sleep(800)
      opened.push([item.key, item.value, await p.eval('Number(document.querySelector("[data-matrix-viewer]").getAttribute("data-total"))')])
    }
    // Compare with previous: this snapshot has one before it (the earlier 20 May import).
    await p.session(ctx.admin, 'light', 1280, 900, `/matrix?dataset=${id}`)
    await p.waitFor('[data-report-compare]', 15000)
    await p.click('[data-report-compare]')
    await p.waitFor('[data-card-change]', 10000)
    const changes = await p.eval('document.querySelectorAll("[data-card-change]").length')
    await p.shot(path.join(shots, 'report-attendance-compare-light-1280.png'))
    await p.click('[data-dataset-tab="data"]')
    await p.waitFor('[data-matrix-viewer]', 15000)
    const dataTab = await p.eval('!!document.querySelector("[data-matrix-viewer]")')
    return ok(id > 0 && tab === 'report' && sameTiles && opened.length > 0 && opened.every(([, v, n]) => v === n) && changes === card.comparison?.items?.length && dataTab,
      `uploaded in the page → dataset ${id}, tab ${tab} · tiles equal the card ${sameTiles} (${tiles.map(([k, v]) => `${k} ${v}`).join(', ')}) · `
      + `opened: ${opened.map(([k, v, n]) => `${k} ${v} → ${n} rows`).join(', ')} · compare with ${card.comparison?.previousDate}: ${changes} changes shown · Data tab shows the viewer ${dataTab}`)
  } finally { await browser.close() }
})

check('G-M3.14', 'M3', 'the Report\'s charts are its kind\'s automatic charts on that dataset, each equal to its own run; operators per line is a tile; compare only when there is a previous snapshot', async () => {
  const S = await m3Setup()
  const out = []
  let pass = true
  for (const [name, id, kinds] of [['master', S.master, ['mo-', 'lb-']], ['attendance', S.att2, ['at-']], ['separation list', S.hr, ['js-']]]) {
    const run = (await asAdmin('POST', `/api/matrix/datasets/${id}/report`, { compare: true })).json
    const card = (await asAdmin('GET', `/api/matrix/datasets/${id}/card`)).json
    let equal = 0
    const bad = []
    for (const w of run?.widgets ?? []) {
      if (w.error) { bad.push(`${w.id}: ${w.error}`); continue }
      const again = w.kpi ? (await runKpi(ctx.admin, w.kpi, w.input)).json : (await asAdmin('POST', '/api/matrix/query', { query: w.query })).json
      if (plainRows(again?.rows) === plainRows(w.rows) && (!w.kpi || again?.value === w.value) && w.datasetId === id) equal++
      else bad.push(`${w.id} differs or is not on dataset ${id}`)
    }
    const prefixes = kinds.every((k) => run.widgets.some((w) => w.id.startsWith(k))) && run.widgets.every((w) => w.type !== 'kpi')
    const good = run.widgets.length > 0 && equal === run.widgets.length && prefixes
    pass &&= good
    out.push(`${name}: ${equal}/${run.widgets.length} charts equal their own run on dataset ${id}${bad.length ? ` (${bad.slice(0, 3).join('; ')})` : ''}, previous snapshot ${card.comparison ? card.comparison.previousDate : 'none'}`)
  }
  // Operators per line among the master's tiles: the median line, written again here.
  const ops = {}
  for (const r of activeMaster().filter((x) => x.SectionName === 'Sewing' && lineOf(x.WingName) !== null))
    ops[lineOf(r.WingName)] = (ops[lineOf(r.WingName)] ?? 0) + (/operator/i.test(r.DesignationName ?? '') ? 1 : 0)
  const masterCard = (await asAdmin('GET', `/api/matrix/datasets/${S.master}/card`)).json
  const tile = masterCard.items.find((i) => i.key === 'operators_per_line')
  const operators = Object.values(ops).reduce((a, b) => a + b, 0)
  const reopened = (await asAdmin('GET', `/api/matrix/datasets/${S.master}/rows?limit=1&highlight=${encodeURIComponent(tile?.cite ?? '')}`)).json?.total
  // The separation list is a single snapshot: no comparison, no button.
  const hrCard = (await asAdmin('GET', `/api/matrix/datasets/${S.hr}/card`)).json
  const browser = await launchBrowser({ base: ctx.api.url, profileRoot: EVAL_ROOT })
  let hrButton
  try {
    const p = await browser.page()
    await p.session(ctx.admin, 'light', 1280, 900, `/matrix?dataset=${S.hr}`)
    await p.waitFor('[data-report] [data-card-item]', 20000)
    hrButton = await p.eval('!!document.querySelector("[data-report-compare]")')
    await p.session(ctx.admin, 'light', 1280, 2000, `/matrix?dataset=${S.master}`)
    await p.waitFor('[data-report] [data-chart-ready]', 20000)
    await sleep(1000)
    await p.shot(path.join(ctx.args.shots, 'report-master-light-1280.png'))
  } finally { await browser.close() }
  pass &&= tile?.value === median(Object.values(ops)) && reopened === operators && !hrCard.comparison && hrButton === false
  return ok(pass, `${out.join(' · ')} · master tile "${tile?.label}" ${tile?.value} (independent median ${median(Object.values(ops))}; ${tile?.detail}), it opens ${reopened} rows = every sewing operator (${operators}) · separation list: no comparison, compare button shown ${hrButton}`)
})

check('G-M3.15', 'M3', 'LIVE the Report page of the real master and the real attendance: light and dark, desktop and 360 px (counts only)', async () => {
  const ds = (await asAdmin('GET', '/api/matrix/datasets')).json?.datasets ?? []
  const real = ds.filter((d) => !d.fileName.startsWith('fx-'))
  const master = real.find((d) => d.fileName === REAL.master && d.sheet === 'HRM')?.id
  const att = real.find((d) => d.fileName === REAL.attendance)?.id
  if (!master || !att) return ok(false, 'the live master and attendance imports are missing (run with --live)')
  const out = []
  let pass = true
  const browser = await launchBrowser({ base: ctx.api.url, profileRoot: EVAL_ROOT })
  try {
    for (const [name, id] of [['master', master], ['attendance', att]]) {
      const card = (await asAdmin('GET', `/api/matrix/datasets/${id}/card`)).json
      const run = (await asAdmin('POST', `/api/matrix/datasets/${id}/report`, {})).json
      const names = run.widgets.some((w) => (w.rows ?? []).some((r) => 'name' in r || 'name_bn' in r))
      pass &&= card.items.length > 0 && run.widgets.length > 0 && !names
      out.push(`${name}: ${card.items.map((i) => `${i.key} ${i.text ?? i.value}`).join(', ')} · ${run.widgets.length} charts, ${run.widgets.filter((w) => w.error).length} without a result · names in any result ${names}`)
      const p = await browser.page()
      for (const theme of ['light', 'dark']) {
        for (const width of [1280, 360]) {
          await p.session(ctx.admin, theme, width, width === 360 ? 6000 : 2600, `/matrix?dataset=${id}`)
          await p.waitFor('[data-report] [data-card-item]', 20000)
          await sleep(2000)
          await p.blur('[data-personal]')
          await p.shot(path.join(ctx.args.shots, `live-report-${name}-${theme}-${width}.png`))
          if (width === 360 && await p.eval('document.documentElement.scrollWidth > window.innerWidth')) { pass = false; out.push(`${name} ${theme} 360px scrolls sideways`) }
        }
      }
    }
  } finally { await browser.close() }
  return ok(pass, `${out.join(' ‖ ')} · 8 screenshots in ${ctx.args.shots} (live-report-*.png)`)
}, { live: true })

// ---- G-M3.6 Live screenshots (counts only; personal data blurred) ----

check('G-M3.6', 'M3', 'LIVE Manpower overview and Line board on the real master, Attendance today on the real attendance: light and dark, desktop and 360 px (counts only)', async () => {
  const ds = (await asAdmin('GET', '/api/matrix/datasets')).json?.datasets ?? []
  const real = ds.filter((d) => !d.fileName.startsWith('fx-'))
  const master = real.find((d) => d.fileName === REAL.master && d.sheet === 'HRM')?.id
  const att = real.find((d) => d.fileName === REAL.attendance)?.id
  if (!master || !att) return ok(false, 'the live master and attendance imports are missing (run with --live)')
  const S = await m3Setup()
  const out = []
  let pass = true
  const browser = await launchBrowser({ base: ctx.api.url, profileRoot: EVAL_ROOT })
  try {
    for (const auto of ['manpower-overview', 'line-board', 'attendance-today']) {
      // A copy of the automatic dashboard pointed at the real datasets by id.
      const src = (await asAdmin('GET', `/api/matrix/dashboards/${S.dash[auto]}`)).json
      const text = JSON.stringify(src.layout).replaceAll('"current:master"', `"${master}"`).replaceAll('"current:attendance-daily"', `"${att}"`)
      const copy = (await asAdmin('POST', '/api/matrix/dashboards', { name: `Live — ${src.name}`, layout: JSON.parse(text) })).json
      const run = await runDash(ctx.admin, copy.id)
      const errors = run.widgets.filter((w) => w.error)
      const names = run.widgets.some((w) => (w.rows ?? []).some((r) => 'name' in r || 'name_bn' in r))
      pass &&= !!copy.id && !names
      out.push(`${auto}: ${run.widgets.length} widgets, ${errors.length} without a result${errors.length ? ` (${errors.map((w) => `${w.id}: ${w.error.slice(0, 80)}`).join('; ')})` : ''}, names in any result ${names}`)
      const p = await browser.page()
      for (const theme of ['light', 'dark']) {
        for (const width of [1280, 360]) {
          // A tall window: the tab scrolls inside its own frame, so the whole dashboard is in one picture.
          await p.session(ctx.admin, theme, width, width === 360 ? 5200 : 2000, `/matrix?dashboard=${copy.id}`)
          await p.waitFor('[data-dashboard] [data-widget]', 20000)
          await sleep(1500)
          await p.blur('[data-personal]')
          await p.shot(path.join(ctx.args.shots, `live-${auto}-${theme}-${width}.png`), { full: true })
          if (width === 360) {
            const overflow = await p.eval('document.documentElement.scrollWidth > window.innerWidth')
            pass &&= !overflow
            if (overflow) out.push(`${auto} ${theme} 360px scrolls sideways`)
          }
        }
      }
    }
  } finally { await browser.close() }
  return ok(pass, `${out.join(' · ')} · 12 screenshots in ${ctx.args.shots} (live-*.png)`)
}, { live: true })

// ═══════════════════════════════ Phase M4 ═══════════════════════════════
//
// The fake model writes what a careful model would — and, when a check plants it,
// what a careless one would — so each check proves the code catches it. The live
// checks restart the API against the real Ollama (--model, default gemma4:12b).

const BN = '০১২৩৪৫৬৭৮৯'
const toBnDigits = (s) => String(s).replace(/\d/g, (d) => BN[Number(d)])

/** The fake's summary, from the card lines it was sent. ctx.summaryMode plants a fault. */
function fakeSummary(body) {
  const user = body.messages.at(-1).content
  const bn = /Write the summary in Bengali/.test(user)
  const lines = [...user.matchAll(/\[(C\d+)\] ([^\n;]+)/g)].map((m) => ({ label: m[1], text: m[2].trim() }))
  const points = lines.slice(0, 6).map((l) => `- ${bn ? `রিপোর্টে আছে: ${toBnDigits(l.text)}` : `The card says: ${l.text}`} [${l.label}]`)
  // A difference, as a formula: the model never does the arithmetic.
  const nums = lines.map((l) => ({ ...l, n: Number(/:\s*([\d.]+)/.exec(l.text)?.[1]) })).filter((l) => Number.isFinite(l.n))
  if (nums.length >= 2) points.push(`- ${bn ? 'পার্থক্য' : 'The difference is'} [[= ${nums[0].n} - ${nums[1].n} ]] [${nums[0].label}][${nums[1].label}]`)
  const mode = ctx.summaryMode ?? 'faithful'
  if (mode === 'invented') points.push('- About 98765 people left last month [C1]')
  if (mode === 'uncited') points.push('- The factory is doing well.')
  if (mode === 'unknown') points.push('- See the line that does not exist [C99]')
  while (points.length < 5) points.push(`- ${bn ? 'রিপোর্ট কার্ড দেখুন' : 'See the report card'} [${lines[0]?.label ?? 'C1'}]`)
  return { words: [points.join('\n')] }
}

/** Every request the fake received, as text — to look for what must never reach a model. */
const fakeBodies = (from = 0) => ctx.fake.requests.slice(from).filter((r) => r.path === '/api/chat').map((r) => JSON.stringify(r.body))

/** Names and IDs of the fixtures' people: none may appear in anything sent to a model. */
function personalStrings() {
  const M = sheetObjects('fx-master.xlsx', 'HRM')
  const H = sheetObjects('fx-hr-basic.xlsx', 'Sheet1')
  const A = sheetObjects('fx-attendance.xlsx', 'rptDailyAttDetailsReport_Factor', 3)
  const names = new Set([...M.flatMap((r) => [r.Name, r.NameBan]), ...H.map((r) => r.NAME), ...A.map((r) => r.Name)].filter((x) => x && String(x).trim().length > 4).map((x) => String(x).trim()))
  const ids = new Set([...M.map((r) => r.EmpID), ...H.map((r) => r.EmpCode), ...A.map((r) => r['Emp ID'])].filter((x) => x && /^\d{6}$/.test(String(x))).map(String))
  return { names, ids }
}

function leaks(bodies) {
  const { names, ids } = personalStrings()
  const found = []
  for (const b of bodies) {
    for (const n of names) if (b.includes(n)) { found.push(`name ${n.slice(0, 2)}…`); break }
    for (const id of ids) if (new RegExp(`(?<!\\d)${id}(?!\\d)`).test(b)) { found.push('an employee ID'); break }
  }
  return found
}

// ---- G-M4.1 Summary ----

check('G-M4.1', 'M4', 'a summary for each fixture kind, Bengali and English: every number traced to the card, every point citing a card line; sent at temperature 0 with thinking off', async () => {
  const S = await m3Setup()
  const mp = (await sql("select Id from MxDatasets where ReportKind = 'manpower' order by Id desc limit 1"))[0].Id
  const lookup = (await sql("select Id from MxDatasets where ReportKind = 'designation-lookup' order by Id desc limit 1"))[0].Id
  ctx.summaryMode = 'faithful'
  const from = ctx.fake.requests.length
  const out = []
  let pass = true
  for (const [name, id] of [['master', S.master], ['attendance', S.att2], ['separation list', S.hr], ['manpower', mp], ['designation lookup', lookup]]) {
    for (const lang of ['bn', 'en']) {
      const r = await asAdmin('POST', `/api/matrix/datasets/${id}/summary`, { language: lang })
      const s = r.json
      const good = r.status === 200 && s.language === lang && s.points >= 5 && s.points <= 8 && s.unverified.length === 0 && s.uncitedPoints.length === 0
        && s.unknownCitations.length === 0 && s.formulas.every((f) => f.ok) && (lang === 'en' || /[০-৯]/.test(s.text))
      pass &&= good
      if (lang === 'en' || !good) out.push(`${name} ${lang}: ${r.status} ${s?.points} points, ${s?.formulas?.length} formula(s), unverified ${s?.unverified?.length}, uncited ${s?.uncitedPoints?.length}, unknown ${s?.unknownCitations?.length}${good ? '' : ' ✗ ' + (s?.text ?? r.text).slice(0, 200)}`)
    }
  }
  const sent = ctx.fake.requests.slice(from).filter((q) => q.path === '/api/chat').map((q) => q.body)
  const why = sent.map((b, i) => [i, [b.options?.temperature === 0 ? null : `temperature ${b.options?.temperature}`, b.think === false ? null : `think ${b.think}`,
    /report card/i.test(b.messages?.[0]?.content ?? '') ? null : `system "${(b.messages?.[0]?.content ?? '').slice(0, 40)}"`,
    /^Report card —/m.test(b.messages?.at(-1)?.content ?? '') ? null : 'no card'].filter(Boolean)]).filter(([, w]) => w.length)
  const settings = why.length === 0
  if (!settings) out.push(`requests not as expected: ${why.map(([i, w]) => `#${i} ${w.join(', ')}`).join('; ')}`)
  const saved = (await asAdmin('GET', `/api/matrix/datasets/${S.master}/summary?lang=en`)).json
  pass &&= settings && sent.length === 10 && saved?.points >= 5 && !saved.stale
  return ok(pass, `${out.join(' · ')} · ${sent.length} model calls, temperature 0 and thinking off with the card as the message ${settings} · the saved English summary reads back, not stale ${saved && !saved.stale}`)
})

check('G-M4.1', 'M4', 'the checks catch a careless model: an invented number, a point citing nothing, a label the card does not have; a formula is worked out by code', async () => {
  const S = await m3Setup()
  const results = {}
  for (const mode of ['invented', 'uncited', 'unknown']) {
    ctx.summaryMode = mode
    results[mode] = (await asAdmin('POST', `/api/matrix/datasets/${S.master}/summary`, { language: 'en' })).json
  }
  ctx.summaryMode = 'faithful'
  const good = (await asAdmin('POST', `/api/matrix/datasets/${S.master}/summary`, { language: 'en' })).json
  const f = good.formulas[0]
  const card = (await asAdmin('GET', `/api/matrix/datasets/${S.master}/card`)).json
  const [a, b] = card.items.filter((i) => typeof i.value === 'number').map((i) => i.value)
  return ok(results.invented.unverified.includes(98765) && results.uncited.uncitedPoints.length === 1 && results.unknown.unknownCitations.includes('C99')
      && f?.ok && Number(f.expression.split('-')[0]) === a && good.text.includes(f.display) && Math.abs(Number(f.display.replace(/,/g, '')) - (a - b)) < 0.01,
    `invented number flagged ${JSON.stringify(results.invented.unverified)} · uncited point ${JSON.stringify(results.uncited.uncitedPoints)} · unknown label ${JSON.stringify(results.unknown.unknownCitations)} · formula "${f?.expression}" → ${f?.display} (independent ${a - b})`)
})

check('G-M4.1', 'M4', 'in the browser: the summary heads the Report page, the language toggles, a [C…] chip opens exactly its rows (screenshots)', async () => {
  const S = await m3Setup()
  ctx.summaryMode = 'faithful'
  const browser = await launchBrowser({ base: ctx.api.url, profileRoot: EVAL_ROOT })
  try {
    const p = await browser.page()
    await p.session(ctx.admin, 'light', 1280, 1400, `/matrix?dataset=${S.att2}`)
    await p.waitFor('[data-summary-text] [data-cite]', 30000)
    const first = await p.eval(`({ lang: document.querySelector('[data-summary]').getAttribute('data-summary-lang'), points: document.querySelectorAll('[data-summary-text] li').length,
      top: !!(document.querySelector('[data-summary]').compareDocumentPosition(document.querySelector('[data-report-tiles]')) & Node.DOCUMENT_POSITION_FOLLOWING) })`)
    await p.shot(path.join(ctx.args.shots, 'summary-bn-light-1280.png'))
    await p.click('[data-summary-lang-button="en"]')
    await p.waitFor('[data-summary][data-summary-lang="en"] [data-summary-text] [data-cite]', 30000)
    await p.shot(path.join(ctx.args.shots, 'summary-en-light-1280.png'))
    // The first chip of a counted card line: its rows in the viewer.
    const s = (await asAdmin('GET', `/api/matrix/datasets/${S.att2}/summary?lang=en`)).json
    const card = (await asAdmin('GET', `/api/matrix/datasets/${S.att2}/card`)).json
    const people = card.items.filter((i) => i.unit === 'people' && i.cite).map((i) => i.key)
    const line = s.labels.find((l) => people.includes(l.key) && s.text.includes(`[${l.label}]`) && card.items.some((i) => i.key === l.key))
    const value = card.items.find((i) => i.key === line.key).value
    await p.click(`[data-summary-text] [data-cite="${line.label}"]`)
    await p.waitFor('[data-matrix-viewer]', 15000)
    await sleep(800)
    const total = await p.eval('Number(document.querySelector("[data-matrix-viewer]").getAttribute("data-total"))')
    await p.setTheme('dark')
    await p.session(ctx.admin, 'dark', 360, 1600, `/matrix?dataset=${S.att2}`)
    await p.waitFor('[data-summary-text] [data-cite]', 30000)
    await p.shot(path.join(ctx.args.shots, 'summary-en-dark-360.png'))
    return ok(first.lang === 'bn' && first.points >= 5 && first.top && total === value,
      `first shown in ${first.lang} with ${first.points} points, above the tiles ${first.top} · English after the toggle · chip [${line.label}] (${line.key} ${value}) opened ${total} rows`)
  } finally { await browser.close() }
})

// ---- Ask: the fake planner and answerer ----

/** The fake's plan for a question; "BROKEN once" / "BROKEN twice" plant an invalid query. */
function fakePlan(body) {
  const first = body.messages.find((m) => m.role === 'user')?.content ?? ''
  const q = /^Question: (.*)$/m.exec(first)?.[1] ?? ''
  const id = /Dataset in scope: "(\d+)"/.exec(body.messages[0].content)?.[1]
  const across = /about all the current datasets/.test(body.messages[0].content)
  const repairing = body.messages.length > 2
  const active = ['status', 'in', ['Active']]
  const broken = { kind: 'queries', queries: [{ dataset: id, group_by: ['shoe_size'], measures: [['count', '*', 'n']] }] }
  let plan
  if (across && /equivalency grade/i.test(q)) plan = { kind: 'queries', queries: [{ dataset: 'current:master', join: [{ dataset: 'current:designation-lookup', on: 'designation' }],
    filter: [active, ['emp_type', 'in', ['Management', 'Non-Management']]], group_by: ['designation-lookup.equivalency_grade'], measures: [['count', '*', 'staff']] }] }
  else if (across && /separations .*separation list/i.test(q)) plan = { kind: 'queries', queries: [
    { dataset: 'current:master', filter: [active], measures: [['count', '*', 'active']] },
    { dataset: 'current:separation-list', filter: [['status', 'not_null'], ['status', 'not_in', ['Active']]], measures: [['count', '*', 'separations']] }] }
  else if (across && /report card/i.test(q)) plan = { kind: 'answer_from_card' }
  else if (/UNGROUPED/.test(q)) plan = { kind: 'queries', queries: [repairing && /group_by/.test(body.messages.at(-1).content)
    ? { dataset: id, filter: [active], group_by: ['section'], measures: [['count', '*', 'people']] }
    : { dataset: id, filter: [active], measures: [['count', '*', 'people']] }] }
  else if (/BROKEN twice/i.test(q)) plan = broken
  else if (/BROKEN once/i.test(q) && !repairing) plan = broken
  else if (/payroll/i.test(q)) plan = { kind: 'queries', queries: [{ dataset: id, filter: [active], group_by: ['unit'], measures: [['sum', 'gross', 'payroll']] }] }
  else if (/headcount by unit|BROKEN once/i.test(q)) plan = { kind: 'queries', queries: [{ dataset: id, filter: [active], group_by: ['unit'], measures: [['count', '*', 'headcount']] }] }
  else if (/attrition/i.test(q)) plan = { kind: 'definition', definition: 'attrition_pct' }
  else if (/attendance rate/i.test(q)) plan = { kind: 'answer_from_card' }
  else if (/separations .*by status/i.test(q)) plan = repairing && /every group/.test(body.messages.at(-1).content)
    ? { kind: 'queries', queries: [{ dataset: id, filter: [['status', 'not_null'], ['status', 'not_in', ['Active']]], group_by: ['status'], measures: [['count', '*', 'separations']] }] }
    : { kind: 'answer_from_card' }
  else if (/efficiency/i.test(q)) plan = { kind: 'need', need: 'Production efficiency is not in these datasets.' }
  else if (/names/i.test(q)) plan = { kind: 'need', need: "People's names are not available." }
  else plan = { kind: 'answer_from_card' }
  return { words: [JSON.stringify(plan)] }
}

/** The fake's answer: the result rows it was given, each cited; or the KPI; or a card line. */
function fakeAnswer(body) {
  const user = body.messages.at(-1).content
  const rows = [...user.matchAll(/^(R\d+): (.*)$/gm)].slice(0, 12)
  const na = /^Asked but NOT AVAILABLE[^:]*: ([^:\n]+): (.*) \[(C\d+|Card)\]$/m.exec(user)
  const kpi = /^\[KPI: ([^\]]+)\] ([^\n]*)$/m.exec(user)
  let lines
  if (ctx.answerMode === 'substitute') lines = ['The attendance rate was 5.92% [C4].']
  else if (na) lines = [`${na[1]} cannot be computed: ${na[2]} [${na[3]}]`]
  else if (rows.length) lines = rows.map(([, l, t]) => `- ${t} [${l}]`)
  else if (kpi) lines = [`${kpi[1]}: ${kpi[2].split(' Computed by')[0]} [KPI: ${kpi[1]}]`]
  else { const c = /^\[(C\d+)\] (.*)$/m.exec(user); lines = [`From the report card: ${c?.[2]} [${c?.[1]}]`] }
  if (ctx.answerMode === 'unknown-row') lines.push('- And one more [R99]')
  return { words: lines.join('\n').match(/\S+\s*/g), gap: ctx.answerGap ?? 0 }
}

/** One question through POST /api/matrix/chat, its NDJSON frames read whole. */
async function ask(user, body) {
  const res = await fetch(`${ctx.api.url}/api/matrix/chat`, {
    method: 'POST', headers: { Authorization: `Bearer ${user.token}`, 'Content-Type': 'application/json' }, body: JSON.stringify(body),
  })
  const text = await res.text()
  const frames = res.headers.get('content-type')?.includes('ndjson') ? text.split('\n').filter(Boolean).map((l) => JSON.parse(l)) : []
  return {
    status: res.status, text, frames, conversationId: Number(res.headers.get('X-Conversation-Id')) || null,
    content: frames.filter((f) => f.type === 'content').map((f) => f.text).join(''),
    context: frames.find((f) => f.type === 'context'), checks: frames.find((f) => f.type === 'unverified'), done: frames.find((f) => f.type === 'done'),
    statuses: frames.filter((f) => f.type === 'status').map((f) => f.text),
  }
}
const planRequests = (from) => ctx.fake.requests.slice(from).filter((r) => r.path === '/api/chat' && r.body?.format?.properties?.kind)
const answerRequests = (from) => ctx.fake.requests.slice(from).filter((r) => r.path === '/api/chat' && /^You answer a manager's question/.test(r.body?.messages?.[0]?.content ?? ''))

// ---- G-M4.2 Plan ----

check('G-M4.2', 'M4', 'the plan: the plan schema as format, temperature 0, thinking off; an invalid query gets one repair round with the validator\'s words; still invalid → "I couldn\'t form a query for that" and the reason', async () => {
  const S = await m3Setup()
  let from = ctx.fake.requests.length
  const good = await ask(ctx.admin, { message: 'What is the headcount by unit?', datasetId: S.master })
  const p = planRequests(from)[0]?.body
  const shape = p && JSON.stringify(p.format.properties.kind.enum) === JSON.stringify(['answer_from_card', 'queries', 'definition', 'need'])
    && p.options?.temperature === 0 && p.think === false && /^You turn a manager's question/.test(p.messages[0].content)
  from = ctx.fake.requests.length
  const once = await ask(ctx.admin, { message: 'BROKEN once: what is the headcount by unit?', datasetId: S.master })
  const repairs = planRequests(from)
  const repairMsg = repairs[1]?.body?.messages?.at(-1)?.content ?? ''
  from = ctx.fake.requests.length
  const twice = await ask(ctx.admin, { message: 'BROKEN twice: count something', datasetId: S.master })
  const twicePlans = planRequests(from).length, twiceAnswers = answerRequests(from).length
  return ok(good.status === 200 && good.context?.results?.[0]?.rows?.length > 0 && shape
      && repairs.length === 2 && /not valid/.test(repairMsg) && /shoe_size/.test(repairMsg) && once.context?.repaired === true && once.context?.results?.[0]?.rows?.length > 0
      && twicePlans === 2 && twiceAnswers === 0 && twice.content.startsWith("I couldn't form a query for that") && /shoe_size/.test(twice.content),
    `plan call: schema format, temperature 0, thinking off ${shape} · one invalid query: ${repairs.length} plan calls, the second carries "${repairMsg.split('\n')[1]?.slice(0, 90)}", answered with ${once.context?.results?.[0]?.rows?.length} rows · still invalid: ${twicePlans} plan calls, ${twiceAnswers} answer calls, said "${twice.content.slice(0, 120)}"`)
})

// ---- G-M4.4 Citations ----

check('G-M4.4', 'M4', '[R1] citations map to result rows and each reopens exactly its rows; a [KPI: …] names its definition; a citation of a row that was not returned is "unknown source"', async () => {
  const S = await m3Setup()
  const r = await ask(ctx.admin, { message: 'What is the headcount by unit?', datasetId: S.master })
  const rows = r.context.results[0].rows
  const cited = [...r.content.matchAll(/\[(R\d+)\]/g)].map((m) => m[1])
  const reopened = []
  for (const row of rows) {
    const t = (await asAdmin('GET', `/api/matrix/datasets/${S.master}/rows?limit=1&only=true&highlight=${encodeURIComponent(row.cite)}`)).json?.total
    reopened.push([row.label, row.values.headcount, t])
  }
  const active = activeMaster()
  const independent = countBy(active, (x) => x.UnitName)
  const sameAsFiles = rows.every((x) => independent[x.values.unit ?? 'null'] === x.values.headcount)
  const def = await ask(ctx.admin, { message: 'What is attrition and how is it computed?', datasetId: S.master })
  ctx.answerMode = 'unknown-row'
  const unknown = await ask(ctx.admin, { message: 'What is the headcount by unit?', datasetId: S.master })
  ctx.answerMode = null
  return ok(cited.length === rows.length && cited.every((c) => rows.some((x) => x.label === c)) && reopened.every(([, v, t]) => v === t) && sameAsFiles
      && r.checks.citations.length === 0 && r.checks.amounts.length === 0
      && def.context.kind === 'definition' && def.context.kpi === 'attrition_pct' && /\[KPI: Attrition %\]/.test(def.content) && def.checks.citations.length === 0
      && unknown.checks.citations.includes('R99'),
    `${rows.length} rows ${cited.join(' ')} cited · each reopens its own rows ${reopened.map(([l, v, t]) => `${l} ${v}→${t}`).join(', ')} · equal to the files ${sameAsFiles} · `
    + `definition: plan ${def.context.kind} ${def.context.kpi}, "${def.content.slice(0, 80)}…" · R99 marked unknown ${JSON.stringify(unknown.checks.citations)}`)
})

// ---- G-M4.5 Permissions in chat ----

check('G-M4.5', 'M4', 'payroll asked without view-salary is refused, naming the permission (no answer call); with it, the payroll is computed', async () => {
  const S = await m3Setup()
  const u = await users()
  let from = ctx.fake.requests.length
  const sys = await ask(u.sys, { message: 'What is the gross payroll by unit?', datasetId: S.master })
  const answersForSys = answerRequests(from).length
  const admin = await ask(ctx.admin, { message: 'What is the gross payroll by unit?', datasetId: S.master })
  const rows = admin.context?.results?.[0]?.rows ?? []
  const want = {}
  for (const x of activeMaster()) want[x.UnitName ?? 'null'] = (want[x.UnitName ?? 'null'] ?? 0) + Number(x.GrossSalary)
  const equal = rows.length > 0 && rows.every((x) => Math.abs(want[x.values.unit ?? 'null'] - x.values.payroll) < 0.01)
  const gen = await ask(u.gen, { message: 'What is the gross payroll by unit?', datasetId: S.master })
  return ok(sys.status === 200 && /view-salary/.test(sys.content) && !sys.context && answersForSys === 0 && equal && gen.status === 403,
    `System: "${sys.content.slice(0, 140)}" (no rows, ${answersForSys} answer calls) · admin: ${rows.length} units, payroll equal to the files ${equal} · General (no Ask section) ${gen.status}`)
})

// ---- G-M4.7 The Ask panel ----

check('G-M4.7', 'M4', 'Ask on the Report page: the status line within 1 s, the answer streamed, chips that open rows, a chart with "Add to dashboard", the Data used panel, saved and reopened (screenshots)', async () => {
  const S = await m3Setup()
  const shots = ctx.args.shots
  ctx.answerGap = 120
  const browser = await launchBrowser({ base: ctx.api.url, profileRoot: EVAL_ROOT })
  try {
    const p = await browser.page()
    await p.session(ctx.admin, 'light', 1280, 1800, `/matrix?dataset=${S.master}`)
    await p.waitFor('[data-ask] [data-ask-input]', 20000)
    // Watch for the status line and the first word from the moment of sending.
    await p.eval(`(() => { window.__ask = { sent: 0, status: 0, words: 0, chunks: 0 }; let last = '';
      new MutationObserver(() => { const now = performance.now(); const a = window.__ask;
        if (!a.status && document.querySelector('[data-ask-status]')) a.status = now;
        const t = document.querySelector('[data-ask-answer]:last-of-type [data-ask-text]')?.textContent ?? '';
        if (t && !a.words) a.words = now; if (t !== last) { a.chunks++; last = t } }).observe(document.body, { subtree: true, childList: true, characterData: true }) })(); 0`)
    await p.type('[data-ask-input]', 'What is the headcount by unit?')
    await p.eval('window.__ask.sent = performance.now(); document.querySelector("[data-ask-send]").click(); 0')
    await p.waitFor('[data-ask-answer] [data-data-used]', 30000)
    const timing = await p.eval('({ status: window.__ask.status - window.__ask.sent, words: window.__ask.words - window.__ask.sent, chunks: window.__ask.chunks })')
    await sleep(800)
    const ui = await p.eval(`({ chips: [...document.querySelectorAll('[data-ask-text] [data-cite]')].map((e) => e.getAttribute('data-cite')),
      chart: !!document.querySelector('[data-ask-chart] [data-matrix-chart]'), add: !!document.querySelector('[data-add-to-dashboard]'),
      used: document.querySelector('[data-data-used-query]')?.textContent?.trim() ?? '' })`)
    await p.eval('document.querySelector("[data-data-used]").open = true; 0')
    await p.shot(path.join(shots, 'ask-light-1280.png'))
    await p.click('[data-add-to-dashboard]')
    await p.waitFor('[data-ask-notice]', 10000)
    const mine = (await asAdmin('GET', '/api/matrix/dashboards')).json.dashboards.find((d) => d.name === 'আমার চার্ট · My charts')
    const added = mine && (await asAdmin('GET', `/api/matrix/dashboards/${mine.id}`)).json.layout.widgets.some((w) => w.query?.group_by?.[0] === 'unit')
    // A chip: its unit's rows in the viewer.
    const first = (await asAdmin('POST', '/api/matrix/query', { query: { dataset: String(S.master), filter: [['status', 'in', ['Active']]], group_by: ['unit'], measures: [['count', '*', 'headcount']] } })).json.rows[0]
    await p.click('[data-ask-text] [data-cite="R1"]')
    await p.waitFor('[data-matrix-viewer]', 15000)
    await sleep(800)
    const total = await p.eval('Number(document.querySelector("[data-matrix-viewer]").getAttribute("data-total"))')
    // Saved: back on the Report page, the conversation reopens with its chips and panel.
    await p.session(ctx.admin, 'dark', 1280, 1800, `/matrix?dataset=${S.master}`)
    await p.waitFor('[data-ask-conversations]', 20000)
    await p.eval(`(() => { const s = document.querySelector('[data-ask-conversations]'); s.value = s.options[1].value; s.dispatchEvent(new Event('change', { bubbles: true })) })(); 0`)
    await p.waitFor('[data-ask-text] [data-cite="R1"]', 15000)
    const reopened = await p.eval('!!document.querySelector("[data-data-used]") && !!document.querySelector("[data-ask-chart] [data-matrix-chart]")')
    await sleep(800)
    await p.shot(path.join(shots, 'ask-dark-1280.png'))
    await p.session(ctx.admin, 'light', 360, 2400, `/matrix?dataset=${S.master}`)
    await p.waitFor('[data-ask-conversations]', 20000)
    await p.eval(`(() => { const s = document.querySelector('[data-ask-conversations]'); s.value = s.options[1].value; s.dispatchEvent(new Event('change', { bubbles: true })) })(); 0`)
    await p.waitFor('[data-ask-text] [data-cite="R1"]', 15000)
    await sleep(800)
    const overflow = await p.eval('document.documentElement.scrollWidth > window.innerWidth')
    await p.shot(path.join(shots, 'ask-light-360.png'))
    return ok(timing.status > 0 && timing.status < 1000 && timing.words > timing.status && timing.chunks > 3 && ui.chips.includes('R1') && ui.chart && ui.add && /headcount/.test(ui.used)
        && added && total === first.headcount && reopened && !overflow,
      `status line after ${Math.round(timing.status)} ms, first word after ${Math.round(timing.words)} ms, the text grew in ${timing.chunks} steps · chips ${ui.chips.join(' ')} · chart ${ui.chart}, Add to dashboard ${ui.add} → added to "My charts" ${!!added} · `
      + `Data used: "${ui.used.slice(0, 120)}" · [R1] opened ${total} rows (the group has ${first.headcount}) · reopened with chips, chart and panel ${reopened} · 360px sideways scroll ${overflow}`)
  } finally { ctx.answerGap = 0; await browser.close() }
})

// ---- G-M4.12 live: the first question's time to first word, without and with the warm-up ----

check('G-M4.12', 'M4', 'LIVE time to first word of a first question on the real master: without the warm-up, then with it (the real model)', async () => {
  const ds = (await asAdmin('GET', '/api/matrix/datasets')).json?.datasets ?? []
  const real = ds.filter((d) => !d.fileName.startsWith('fx-'))
  const master = real.find((d) => d.fileName === REAL.master && d.sheet === 'HRM')?.id
  const att = real.find((d) => d.fileName === REAL.attendance)?.id
  if (!master || !att) return ok(false, 'the live imports are missing (run with --live)')
  await useRealModel()
  const q = 'How many active people are in the Cutting section?'
  // The attendance's own prompt goes through the model in between, so the master's is not already cached.
  const other = async () => ask(ctx.admin, { message: 'How many people were late?', datasetId: att })
  await other()
  const cold = await ask(ctx.admin, { message: q, datasetId: master })
  await other()
  const warm = (await asAdmin('POST', `/api/matrix/datasets/${master}/warm`, {})).json
  const hot = await ask(ctx.admin, { message: q, datasetId: master })
  const before = cold.done?.firstWordMs, after = hot.done?.firstWordMs
  ctx.warmMeasure = { before, after, warmMs: warm?.ms }
  return ok(before > 0 && after > 0 && warm?.warmed === true,
    `${ctx.args.model} · first word without the warm-up ${(before / 1000).toFixed(1)} s · warm-up ${(warm?.ms / 1000).toFixed(1)} s (while the page is read) · first word after it ${(after / 1000).toFixed(1)} s`
    + ` · answers: "${cold.content.slice(0, 80)}" / "${hot.content.slice(0, 80)}"`)
}, { live: true })

// ---- G-M4.6 Golden questions (live, the real model) ----

/** A real file's sheet as objects by header (matrix-samples/; never printed). */
function realObjects(file, sheetName, headerRow = 1) {
  const sheet = readWorkbook(path.join(SAMPLES, file)).find((s) => s.name === sheetName)
  const cols = new Map(), rows = new Map()
  for (const [ref, v] of sheet.cells) {
    const m = /^([A-Z]+)(\d+)$/.exec(ref)
    const r = Number(m[2])
    if (r === headerRow) cols.set(m[1], v)
    else if (r > headerRow) { if (!rows.has(r)) rows.set(r, {}); rows.get(r)[m[1]] = v }
  }
  return [...rows.values()].map((cells) => Object.fromEntries([...cols].map(([l, h]) => [String(h).trim(), cells[l] ?? null])))
}

/** "07:54:20 AM" → "07:54:20", "11:26:38 PM" → "23:26:38"; a time already on the 24-hour clock is kept. */
function to24(s) {
  const m = /^(\d{1,2}):(\d{2}):(\d{2})\s*([AP]M)?$/i.exec(String(s ?? '').trim())
  if (!m) return null
  let h = Number(m[1]) % (m[4] ? 12 : 24)
  if (m[4]?.toUpperCase() === 'PM') h += 12
  return `${String(h).padStart(2, '0')}:${m[2]}:${m[3]}`
}

/** What each golden question's answer must say, computed here from the real files. */
function goldenExpect() {
  if (ctx.golden) return ctx.golden
  const M = realObjects(REAL.master, 'HRM'), A = realObjects(REAL.attendance, 'rptDailyAttDetailsReport_Factor', 3)
  const H = realObjects(REAL.hrBasic, 'Sheet1'), D = realObjects(REAL.designation, 'Sheet1')
  const t = (v) => String(v ?? '').trim()
  const active = M.filter((r) => t(r.EmpStatusName) === 'Active')
  const sewing = active.filter((r) => t(r.SectionName) === 'Sewing' && lineOf(r.WingName) !== null)
  const ops = {}
  for (const r of sewing) ops[lineOf(r.WingName)] = (ops[lineOf(r.WingName)] ?? 0) + (/operator/i.test(t(r.DesignationName)) ? 1 : 0)
  const fewest = Math.min(...Object.values(ops))
  const grade = new Map(D.map((r) => [t(r.DesignationName).toLowerCase(), t(r.EquivalencyGradeName)]))
  const staff = active.filter((r) => ['Management', 'Non-Management'].includes(t(r.EmpTypeName)))
  const pct = (a, b) => round2((100 * a) / b)
  const units = countBy(active.filter((r) => t(r.UnitName)), (r) => t(r.UnitName))
  const fin = active.filter((r) => t(r.SectionName) === 'Finishing')
  const seps = countBy(H.filter((r) => t(r.EmpStatusName) && t(r.EmpStatusName) !== 'Active'), (r) => t(r.EmpStatusName))
  const workers = active.filter((r) => ['Schedule-A', 'Schedule-B'].includes(t(r.EmpTypeName))).length
  const otByDept = countBy(A.filter((r) => Number(r['OT Hours']) > 2), (r) => t(r.Department))
  ctx.golden = {
    headcount_by_unit: { numbers: Object.values(units) },
    sewing_lines_unit1: { numbers: [new Set(sewing.filter((r) => t(r.UnitName) === 'ADL-Unit-1').map((r) => lineOf(r.WingName))).size] },
    fewest_operators: { numbers: [fewest], anyOf: Object.entries(ops).filter(([, n]) => n === fewest).map(([l]) => Number(l)) },
    op_helper_143: { numbers: [ops[143] ?? 0] },
    female_pct_finishing: { numbers: [pct(fin.filter((r) => t(r.GenderName ?? r.Gender) === 'Female').length, fin.length)] },
    // Attendance % cannot be computed for this file (no ID is in the master): the late % (C4) given as the rate is wrong.
    late_19_may: { numbers: [A.filter((r) => t(r.Status) === 'LT').length], unavailable: 'attendance_pct' },
    ot_over_2h_by_department: { numbers: Object.values(otByDept) },
    grade_m3: { numbers: [staff.filter((r) => grade.get(t(r.DesignationName).toLowerCase()) === 'M3').length] },
    separations_by_status: { numbers: Object.values(seps) },
    not_in_data: { need: true },
    refuse_personal: { refuse: true },
    sewing_lines_total: { numbers: [new Set(sewing.map((r) => lineOf(r.WingName))).size] },
    female_pct: { numbers: [pct(active.filter((r) => t(r.GenderName ?? r.Gender) === 'Female').length, active.length)] },
    late_count: { numbers: [A.filter((r) => t(r.Status) === 'LT').length] },
    ot_over_2h: { numbers: [A.filter((r) => Number(r['OT Hours']) > 2).length] },
    median_operators: { numbers: [median(Object.values(ops))] },
    sewing_section_active: { numbers: [active.filter((r) => t(r.SectionName) === 'Sewing').length] },
    no_grade_mapping: { numbers: [staff.filter((r) => !grade.has(t(r.DesignationName).toLowerCase())).length] },
    resignations: { numbers: [seps.Resignation ?? 0] },
    mgmt_worker_ratio: { numbers: [Math.round((staff.length / workers) * 100) / 100] },
    largest_unit: { numbers: [Math.max(...Object.values(units))] },
    // "11:26:38 PM" → "23:26:38": the 24-hour clock the card and the answers use.
    latest_out: { text: [A.map((r) => to24(t(r['Out Time']))).filter(Boolean).sort().at(-1)] },
    cutting_active: { numbers: [active.filter((r) => t(r.SectionName) === 'Cutting').length] },
    status_left: { numbers: [seps.Left ?? 0] },
  }
  return ctx.golden
}

/** The numbers an answer states (Bengali digits read, separators dropped, citations left out). */
const answerNumbers = (s) => [...String(s).replace(/\[(?:R|C)\d+\]/g, ' ').replace(/[০-৯]/g, (c) => String(c.charCodeAt(0) - 0x09e6)).replace(/(\d),(\d)/g, '$1$2')
  .matchAll(/\d+(?:\.\d+)?/g)].map((m) => Number(m[0]))
const says = (nums, want) => nums.some((n) => n === want || Math.abs(n - want) < 0.051 || (want % 1 !== 0 && Math.abs(n - Math.round(want)) < 0.001 && false))

function judge(g, want, r) {
  const nums = answerNumbers(r.content)
  const declined = !r.context?.results?.some((x) => x.rows?.length) && /need|refus|can't|cannot|not (?:in|available)|নেই|যাবে না|পাওয়া যায়নি|উত্তর নেই/i.test(r.content + JSON.stringify(r.context ?? {}))
  if (want.need) return declined || r.context?.kind === 'need' || /cannot answer|নেই/.test(r.content)
  if (want.refuse) return !r.context?.results?.some((x) => x.rows?.length && x.groupBy?.some((gb) => /name|phone|mobile/.test(gb))) && (declined || /cannot|can't|not available|নাম/i.test(r.content))
  if (want.text) return want.text.every((s) => r.content.includes(s))
  // Asked for what the data cannot give: said so, and no number put in its place.
  if (want.unavailable) {
    const u = r.checks?.unavailable?.find((x) => x.key === want.unavailable)
    if (!u || !u.said || u.substituted) return false
  }
  const needed = want.anyOf ? [...want.numbers, null] : want.numbers
  return needed.every((w) => w === null ? want.anyOf.some((l) => nums.includes(l)) : says(nums, w))
}

check('G-M4.6', 'M4', 'LIVE golden questions (Step 3: ten) with the real model on the real files: answers, correctness against independent numbers, unverified numbers, time to first word (no personal data)', async () => {
  const ds = (await asAdmin('GET', '/api/matrix/datasets')).json?.datasets ?? []
  const real = ds.filter((d) => !d.fileName.startsWith('fx-'))
  const ids = {
    master: real.find((d) => d.fileName === REAL.master && d.sheet === 'HRM')?.id,
    attendance: real.find((d) => d.fileName === REAL.attendance)?.id,
    hrBasic: real.find((d) => d.fileName === REAL.hrBasic)?.id,
  }
  if (!ids.master || !ids.attendance || !ids.hrBasic) return ok(false, 'the live imports are missing (run with --live)')
  const golden = JSON.parse(read('scripts/matrix/golden.json')).questions.filter((g) => ctx.args.golden === 'all' || g.step3)
  const expect = goldenExpect()
  const models = ctx.args.models ?? [ctx.args.model]
  const reports = []
  let gate = false
  for (const model of models) {
  await useRealModel(model)
  const out = []
  // Each dataset warmed just before its first question, as opening its Report page does (the model caches one prompt at a time).
  const warmed = []
  const warmOnce = async (k) => {
    if (warmed.some((w) => w.startsWith(k + ' '))) return
    const w = (await asAdmin('POST', `/api/matrix/datasets/${ids[k]}/warm`, {})).json
    warmed.push(`${k} ${w?.warmed ? `warmed in ${(w.ms / 1000).toFixed(1)} s` : w?.reason}`)
  }
  let right = 0, unverified = 0, firstWords = []
  for (const g of golden) {
    await warmOnce(g.dataset)
    const r = await ask(ctx.admin, { message: g.q, datasetId: ids[g.dataset] })
    const good = r.status === 200 && judge(g, expect[g.expect], r)
    if (good) right++
    unverified += r.checks?.amounts?.length ?? 0
    if (r.done?.firstWordMs) firstWords.push(r.done.firstWordMs)
    out.push(`${g.id} [${g.lang}] ${good ? 'RIGHT' : 'WRONG'} · plan ${r.context?.kind ?? (r.content.startsWith("I couldn't") ? 'could not plan' : 'refusal/need')}${r.context?.repaired ? ' (repaired)' : ''} · first word ${((r.done?.firstWordMs ?? 0) / 1000).toFixed(1)} s · unverified ${JSON.stringify(r.checks?.amounts ?? [])}`
      + `${r.checks?.citations?.length ? ` · unknown ${JSON.stringify(r.checks.citations)}` : ''}${r.checks?.unavailable?.length ? ` · unavailable ${JSON.stringify(r.checks.unavailable.map((u) => ({ key: u.key, said: u.said, substituted: u.substituted })))}` : ''}\n    Q: ${g.q}\n    A: ${r.content.replace(/\n/g, ' ⏎ ').slice(0, 600)}\n    expected: ${JSON.stringify(expect[g.expect])}`)
  }
  // No name of the real files' people in any answer (compared, never printed).
  const names = new Set(realObjects(REAL.master, 'HRM').map((r) => String(r.Name ?? '').trim()).filter((n) => n.length > 4))
  const answers = out.join('\n')
  const leaked = [...names].some((n) => answers.includes(n))
  const fw = firstWords.sort((a, b) => a - b)
  // Across every current dataset (the Ask section): the planner picks the datasets; scored apart from the 25.
  const across = ctx.args.golden === 'all' ? JSON.parse(read('scripts/matrix/golden.json')).across ?? [] : []
  let acrossRight = 0
  await asAdmin('POST', '/api/matrix/ask/warm', {})
  for (const g of across) {
    const r = await ask(ctx.admin, { message: g.q })
    const good = r.status === 200 && judge(g, expect[g.expect], r)
    if (good) acrossRight++
    unverified += r.checks?.amounts?.length ?? 0
    out.push(`${g.id} [${g.lang}, across] ${good ? 'RIGHT' : 'WRONG'} · plan ${r.context?.kind ?? 'could not plan'} on ${(r.context?.results ?? []).map((x) => x.datasets?.map((d) => d.kind).join('+')).join(', ') || '—'} · first word ${((r.done?.firstWordMs ?? 0) / 1000).toFixed(1)} s · unverified ${JSON.stringify(r.checks?.amounts ?? [])}\n    Q: ${g.q}\n    A: ${r.content.replace(/\n/g, ' ⏎ ').slice(0, 600)}`)
  }
  if (across.length) out.unshift(`across every current dataset: ${acrossRight}/${across.length} right`)
  const leakedAny = leaked || [...names].some((n) => out.join('\n').includes(n))
  if (model === models[0]) gate = right >= Math.ceil(golden.length * 0.84) && unverified === 0 && !leakedAny
  reports.push(`${model}: ${right}/${golden.length} right · warm-ups: ${warmed.join(', ')} · unverified numbers ${unverified} · personal data in answers ${leakedAny} · first word median ${((fw[Math.floor(fw.length / 2)] ?? 0) / 1000).toFixed(1)} s, max ${((fw.at(-1) ?? 0) / 1000).toFixed(1)} s\n${out.join('\n')}`)
  }
  return ok(gate, reports.join('\n\n'))
}, { live: true })

check('G-M4.7', 'M4', 'LIVE the Report page with the real model\'s summary and an Ask conversation, on the real master and attendance: light and dark, desktop and 360 px (personal data blurred)', async () => {
  const ds = (await asAdmin('GET', '/api/matrix/datasets')).json?.datasets ?? []
  const real = ds.filter((d) => !d.fileName.startsWith('fx-'))
  const sets = [['master', real.find((d) => d.fileName === REAL.master && d.sheet === 'HRM')?.id], ['attendance', real.find((d) => d.fileName === REAL.attendance)?.id]]
  if (sets.some(([, id]) => !id)) return ok(false, 'the live imports are missing (run with --live)')
  await useRealModel()
  const out = []
  let pass = true
  // The page shows the saved summary: have the real model write it first (an earlier check's fake one may be saved).
  for (const [, id] of sets) for (const language of ['bn', 'en']) await asAdmin('POST', `/api/matrix/datasets/${id}/summary`, { language })
  const browser = await launchBrowser({ base: ctx.api.url, profileRoot: EVAL_ROOT })
  try {
    for (const [name, id] of sets) {
      const convs =(await asAdmin('GET', `/api/matrix/conversations?dataset=${id}`)).json?.conversations ?? []
      const p = await browser.page()
      for (const theme of ['light', 'dark']) {
        for (const width of [1280, 360]) {
          await p.session(ctx.admin, theme, width, width === 360 ? 7000 : 3200, `/matrix?dataset=${id}`)
          await p.waitFor('[data-summary-text]', 120000)
          if (convs.length) {
            await p.eval(`(() => { const s = document.querySelector('[data-ask-conversations]'); s.value = s.options[1].value; s.dispatchEvent(new Event('change', { bubbles: true })) })(); 0`)
            await p.waitFor('[data-ask-text]', 20000)
          }
          await sleep(2500)
          await p.blur('[data-personal]')
          await p.shot(path.join(ctx.args.shots, `live-ask-${name}-${theme}-${width}.png`))
          if (width === 360 && await p.eval('document.documentElement.scrollWidth > window.innerWidth')) { pass = false; out.push(`${name} ${theme} 360px scrolls sideways`) }
        }
      }
      out.push(`${name}: summary shown, ${convs.length} saved conversation(s), the first opened`)
    }
  } finally { await browser.close() }
  return ok(pass, `${out.join(' · ')} · 8 screenshots in ${ctx.args.shots} (live-ask-*.png)`)
}, { live: true })

// ---- G-M4.10 What a dataset cannot give: listed with its reason, said, never replaced ----

/** The 21 May attendance: none of its people are in the master, so attendance % cannot be computed. */
async function strangersAttendance() {
  if (ctx.att3) return ctx.att3
  await m3Setup()
  const up = (await upload(ctx.admin, [path.join(FIXTURES, 'fx-attendance-3.xlsx')])).json.uploads[0]
  const r = await asAdmin('POST', `/api/matrix/uploads/${up.id}/import`, { tables: [{ table: tableOf(up, 'rptDailyAttDetailsReport_Factor').id, confirm: 'suggestions' }] })
  ctx.att3 = r.json.datasets[0].id
  return ctx.att3
}

check('G-M4.10', 'M4', 'a KPI the data cannot give is listed with its reason on the card and in the plan prompt; asked for, the answer says so and gives no other number — a substituted percentage is caught and corrected', async () => {
  const att3 = await strangersAttendance()
  const card = (await asAdmin('GET', `/api/matrix/datasets/${att3}/card`)).json
  const na = card.unavailable?.find((u) => u.key === 'attendance_pct')
  const S = await m3Setup()
  const withPct = (await asAdmin('GET', `/api/matrix/datasets/${S.att2}/card`)).json.items.find((i) => i.key === 'attendance_pct')
  let from = ctx.fake.requests.length
  ctx.answerMode = null
  const faithful = await ask(ctx.admin, { message: 'What was the attendance rate on 21 May?', datasetId: att3 })
  const planPrompt = planRequests(from)[0]?.body?.messages?.[0]?.content ?? ''
  const answerMsg = answerRequests(from)[0]?.body?.messages?.at(-1)?.content ?? ''
  ctx.answerMode = 'substitute'
  const sub = await ask(ctx.admin, { message: 'What was the attendance rate on 21 May?', datasetId: att3 })
  ctx.answerMode = null
  const fu = faithful.checks?.unavailable?.[0], su = sub.checks?.unavailable?.[0]
  return ok(na && /none of the people/.test(na.reason) && !card.items.some((i) => i.key === 'attendance_pct') && withPct?.value > 0 && !!withPct.cite
      && /attendance_pct — Attendance % — NOT AVAILABLE in this dataset: none of the people/.test(planPrompt) && /Asked but NOT AVAILABLE/.test(answerMsg)
      && fu?.said === true && fu?.substituted === false && !/%/.test(faithful.content.replace(/Attendance %/g, '')) && !faithful.content.includes('⚠')
      && su?.substituted === true && /⚠ The percentage above is not it\. Attendance % cannot be computed: none of the people/.test(sub.content),
    `card: attendance % not available — "${na?.reason}" (on 20 May, where the people are in the master, it is ${withPct?.value}% and cited) · plan prompt marks it ${/NOT AVAILABLE/.test(planPrompt)} · the answer call is told ${/Asked but NOT AVAILABLE/.test(answerMsg)} · `
    + `faithful answer "${faithful.content.slice(0, 120)}" (said ${fu?.said}, substituted ${fu?.substituted}) · substituting answer caught and corrected: "${sub.content.replace(/\n/g, ' ').slice(0, 220)}"`)
})

// ---- G-M4.11 A full breakdown is queried, never answered from the card's top and bottom three ----

check('G-M4.11', 'M4', 'card breakdowns say "top 3 of 8"; a question asking for a full breakdown that was planned from the card is planned again and runs a query giving every group', async () => {
  const S = await m3Setup()
  const card = (await asAdmin('GET', `/api/matrix/datasets/${S.hr}/card`)).json
  const b = card.breakdowns.find((x) => x.key === 'separations_by_status')
  const from = ctx.fake.requests.length
  const r = await ask(ctx.admin, { message: 'How many separations are there by status?', datasetId: S.hr })
  const plans = planRequests(from)
  const told = plans[1]?.body?.messages?.at(-1)?.content ?? ''
  const sentCard = plans[0]?.body?.messages?.[1]?.content ?? ''
  const rows = r.context?.results?.[0]?.rows ?? []
  const H = sheetObjects('fx-hr-basic.xlsx', 'Sheet1')
  const want = countBy(H.filter((x) => x.EmpStatusName && x.EmpStatusName !== 'Active'), (x) => x.EmpStatusName)
  const got = Object.fromEntries(rows.map((x) => [x.values.status, x.values.separations ?? x.values.n ?? x.values.value]))
  const cited = rows.every((x) => r.content.includes(`[${x.label}]`))
  return ok(b?.groups === Object.keys(want).length && b.top.length === 3 && /top 3 of 8/.test(sentCard) && plans.length === 2 && /every group/.test(told)
      && r.context?.repaired === true && sameMap(want, got) && cited,
    `card: ${b?.label} top ${b?.top.length} of ${b?.groups} ("top 3 of 8" in what the planner read ${/top 3 of 8/.test(sentCard)}) · planned from the card, then again (${plans.length} plan calls, told "${told.slice(0, 80)}…") · `
    + `the query gave ${rows.length} groups, equal to the file ${sameMap(want, got)} · every group cited ${cited}`)
})

// ---- G-M4.12 Warm-up: the plan prefix read before the first question ----

check('G-M4.12', 'M4', 'warm-up: one request with the plan\'s own system message, num_predict 1, the same model, num_ctx and keep_alive; not again within 10 minutes; opening a Report page sends it', async () => {
  const S = await m3Setup()
  let from = ctx.fake.requests.length
  const w1 = (await asAdmin('POST', `/api/matrix/datasets/${S.master}/warm`, {})).json
  const w2 = (await asAdmin('POST', `/api/matrix/datasets/${S.master}/warm`, {})).json
  const warmReqs = ctx.fake.requests.slice(from).filter((q) => q.path === '/api/chat' && q.body?.options?.num_predict === 1)
  from = ctx.fake.requests.length
  await ask(ctx.admin, { message: 'What is the headcount by unit?', datasetId: S.master })
  const plan = planRequests(from)[0]?.body
  const warm = warmReqs[0]?.body
  // The system message the plan call starts with, then a one-character user turn (some chat templates refuse a system message alone).
  const same = !!warm && !!plan && warm.messages.length === 2 && warm.messages[0].role === 'system' && warm.messages[0].content === plan.messages[0].content && warm.messages[1].role === 'user'
    && warm.model === plan.model && warm.options.num_ctx === plan.options.num_ctx && warm.keep_alive === plan.keep_alive && warm.keep_alive > 0 && warm.think === false
  // Opening a Report page warms its dataset.
  const before = ctx.fake.requests.length
  const browser = await launchBrowser({ base: ctx.api.url, profileRoot: EVAL_ROOT })
  let fromPage
  try {
    const p = await browser.page()
    await p.session(ctx.admin, 'light', 1280, 900, `/matrix?dataset=${S.hr}`)
    await p.waitFor('[data-ask] [data-ask-input]', 20000)
    await sleep(1500)
    fromPage = ctx.fake.requests.slice(before).filter((q) => q.path === '/api/chat' && q.body?.options?.num_predict === 1)
  } finally { await browser.close() }
  return ok(w1?.warmed === true && w2?.warmed === false && /10 minutes/.test(w2?.reason ?? '') && warmReqs.length === 1 && same && fromPage.length === 1
      && /Dataset in scope: "\d+" — separation-list/.test(fromPage[0].body.messages[0].content),
    `first warm-up ${w1?.warmed} (${w1?.ms} ms, ${w1?.model}), second within 10 minutes ${w2?.warmed} ("${w2?.reason}") · ${warmReqs.length} request to the model: num_predict 1, `
    + `its system message identical to the plan call's, same model, num_ctx ${warm?.options?.num_ctx} and keep_alive ${warm?.keep_alive} ${same} · opening the separation list's Report page sent ${fromPage.length} warm-up`)
})

// ---- G-M4.13 Ask across every current dataset (the Ask section) ----

check('G-M4.13', 'M4', 'Ask across every current dataset: the planner sees each kind\'s current dataset; a join and a two-dataset question give the files\' numbers, each chip on its own dataset; no card to answer from; its own conversations and warm-up', async () => {
  const S = await m3Setup()
  let from = ctx.fake.requests.length
  const grades = await ask(ctx.admin, { message: 'How many active staff are there in each equivalency grade?' })
  const prompt = planRequests(from)[0]?.body?.messages?.[0]?.content ?? ''
  const rows = grades.context?.results?.[0]?.rows ?? []
  // Written again: the current lookup's grade for each active Management / Non-Management person's designation.
  const lookupId = (await sql("select Id from MxDatasets where ReportKind = 'designation-lookup' and IsCurrent = 1 order by Id desc limit 1"))[0].Id
  const grade = new Map(sheetObjects(await fileOf(lookupId), 'Sheet1').map((r) => [String(r.DesignationName ?? '').trim().toLowerCase(), String(r.EquivalencyGradeName ?? '').trim()]))
  const staff = activeMaster().filter((r) => ['Management', 'Non-Management'].includes(r.EmpTypeName))
  const want = countBy(staff, (r) => grade.get(String(r.DesignationName ?? '').trim().toLowerCase()) || null)
  const got = Object.fromEntries(rows.map((x) => [String(x.values['designation-lookup.equivalency_grade'] ?? 'null'), x.values.staff]))
  const joinOk = sameMap(want, got)

  const two = await ask(ctx.admin, { message: 'How many active people are in the master, and how many separations are in the separation list?' })
  const sets = (two.context?.results ?? []).map((r) => r.datasets?.[0]?.kind)
  const reopen = []
  for (const r of two.context?.results ?? []) {
    const row = r.rows[0]
    const t = (await asAdmin('GET', `/api/matrix/datasets/${r.datasets[0].id}/rows?limit=1&only=true&highlight=${encodeURIComponent(row.cite)}`)).json?.total
    reopen.push([r.datasets[0].kind, Object.values(row.values)[0], t])
  }
  const H = sheetObjects('fx-hr-basic.xlsx', 'Sheet1')
  const twoOk = sets.join() === 'master,separation-list' && reopen.every(([, v, t]) => v === t)
    && reopen[0]?.[1] === activeMaster().length && reopen[1]?.[1] === H.filter((x) => x.EmpStatusName && x.EmpStatusName !== 'Active').length

  from = ctx.fake.requests.length
  const card = await ask(ctx.admin, { message: 'What does the report card say?' })
  const told = planRequests(from)[1]?.body?.messages?.at(-1)?.content ?? ''

  const mine = (await asAdmin('GET', '/api/matrix/conversations')).json?.conversations ?? []
  const scoped = (await asAdmin('GET', `/api/matrix/conversations?dataset=${S.master}`)).json?.conversations ?? []
  const listed = mine.some((c) => c.id === grades.conversationId) && !scoped.some((c) => c.id === grades.conversationId) && mine.every((c) => c.scope === null)

  // The Ask section: the panel, and its warm-up of the all-datasets prompt.
  const before = ctx.fake.requests.length
  const browser = await launchBrowser({ base: ctx.api.url, profileRoot: EVAL_ROOT })
  let section
  try {
    const p = await browser.page()
    await p.session(ctx.admin, 'light', 1280, 1400, '/matrix?section=ask')
    await p.waitFor('[data-ask-section] [data-ask-input], [data-ask] [data-ask-input]', 20000)
    await sleep(1500)
    section = ctx.fake.requests.slice(before).filter((q) => q.path === '/api/chat' && q.body?.options?.num_predict === 1)
    await p.eval(`(() => { const s = document.querySelector('[data-ask-conversations]'); s.value = '${grades.conversationId}'; s.dispatchEvent(new Event('change', { bubbles: true })) })(); 0`)
    await p.waitFor('[data-ask-text] [data-cite]', 15000)
    await sleep(800)
    await p.shot(path.join(ctx.args.shots, 'ask-section-light-1280.png'))
  } finally { await browser.close() }
  const sectionWarm = section.length === 1 && /about all the current datasets/.test(section[0].body.messages[0].content)
  return ok(/This conversation is about all the current datasets/.test(prompt) && /Dataset "current:master"/.test(prompt) && /Dataset "current:separation-list"/.test(prompt)
      && joinOk && twoOk && card.content.startsWith("I couldn't form a query for that") && /no report card here/.test(told) && listed && sectionWarm,
    `the planner reads every current dataset ${/Dataset "current:master"/.test(prompt)} · staff by equivalency grade through the join equal to the files ${joinOk} (${rows.length} grades) · `
    + `two datasets in one question: ${sets.join(' + ')}, each chip reopens its own rows ${reopen.map(([k, v, t]) => `${k} ${v}→${t}`).join(', ')} · `
    + `"answer from the card" with no card: replanned, then "${card.content.slice(0, 70)}…" · the section lists its own conversations only ${listed} · opening it warmed the all-datasets prompt ${sectionWarm}`)
})

// ---- G-M4.14 Guards for a weaker plan: a field on another dataset; a breakdown counted as one number ----

check('G-M4.14', 'M4', 'the validator says where an unknown field lives and how to join it; a breakdown question planned as one number is planned again, grouped', async () => {
  const S = await m3Setup()
  const bad = await asAdmin('POST', '/api/matrix/query', { query: { dataset: String(S.master), group_by: ['equivalency_grade'], measures: [['count', '*', 'n']] } })
  const hint = bad.json?.error ?? ''
  const from = ctx.fake.requests.length
  const r = await ask(ctx.admin, { message: 'UNGROUPED: how many people are there by section?', datasetId: S.master })
  const plans = planRequests(from)
  const told = plans[1]?.body?.messages?.at(-1)?.content ?? ''
  const rows = r.context?.results?.[0]?.rows ?? []
  const want = countBy(activeMaster(), (x) => x.SectionName)
  const got = Object.fromEntries(rows.map((x) => [String(x.values.section), x.values.people]))
  return ok(bad.status === 400 && /current designation-lookup dataset has it/.test(hint) && /designation-lookup\.equivalency_grade/.test(hint) && /"on": "designation"/.test(hint)
      && plans.length === 2 && /group_by/.test(told) && r.context?.repaired === true && sameMap(want, got),
    `unknown field: "${hint}" · a breakdown planned as one number: ${plans.length} plan calls, told "${told.slice(0, 90)}…", then ${rows.length} sections equal to the file ${sameMap(want, got)}`)
})

// ---- G-M4.3 No personal data to the model (after the summary and chat checks) ----

check('G-M4.3', 'M4', 'no name or employee ID of the fixtures reached the model in any request: summaries, plans and answers', async () => {
  if (!fakeBodies().length) {
    // Run alone: give the model something to have received first.
    const S = await m3Setup()
    ctx.summaryMode = 'faithful'
    for (const id of [S.master, S.att2, S.hr]) await asAdmin('POST', `/api/matrix/datasets/${id}/summary`, { language: 'en' })
  }
  const bodies = fakeBodies()
  const found = leaks(bodies)
  const { names, ids } = personalStrings()
  return ok(found.length === 0 && bodies.length > 0, `${bodies.length} model requests scanned for ${names.size} names and ${ids.size} IDs · found ${found.length ? found.join(', ') : 'none'}`)
})

// ---- live: the real model ----

/** Restarts the API against the real Ollama with the model to test (live checks only). */
async function useRealModel(model = ctx.args.model) {
  if (ctx.realModel === model) return
  const settings = read('backend/appsettings.json').split(/\r?\n/).filter((l) => !l.trim().startsWith('//')).join('\n').replace(/,(\s*[}\]])/g, '$1')
  const url = process.env.MATRIX_EVAL_OLLAMA_URL ?? JSON.parse(settings).Ollama.Url
  await ctx.api.stop()
  ctx.api = await startApi({ fakeUrl: url, runDir: ctx.api.runDir, port: ctx.api.port,
    env: { Matrix__Model: model, Extraction__DefaultModel: model, Ollama__DefaultModel: model } })
  ctx.admin = await login(process.env.MATRIX_EVAL_ADMIN_USER, process.env.MATRIX_EVAL_ADMIN_SECRET)
  ctx.realModel = model
}

check('G-M4.1', 'M4', 'LIVE the summary of the real master and attendance with the real model, Bengali and English (aggregates only)', async () => {
  const ds = (await asAdmin('GET', '/api/matrix/datasets')).json?.datasets ?? []
  const real = ds.filter((d) => !d.fileName.startsWith('fx-'))
  const master = real.find((d) => d.fileName === REAL.master && d.sheet === 'HRM')?.id
  const att = real.find((d) => d.fileName === REAL.attendance)?.id
  if (!master || !att) return ok(false, 'the live master and attendance imports are missing (run with --live)')
  await useRealModel()
  const out = []
  let pass = true
  for (const [name, id] of [['master', master], ['attendance', att]]) {
    for (const lang of ['en', 'bn']) {
      const t0 = Date.now()
      const r = await asAdmin('POST', `/api/matrix/datasets/${id}/summary`, { language: lang })
      const s = r.json
      const secs = ((Date.now() - t0) / 1000).toFixed(1)
      const good = r.status === 200 && s.unverified.length === 0 && s.unknownCitations.length === 0
      pass &&= r.status === 200
      out.push(`${name} ${lang} (${s?.model}, ${secs} s): ${s?.points} points, unverified ${JSON.stringify(s?.unverified)}, uncited ${JSON.stringify(s?.uncitedPoints)}, unknown ${JSON.stringify(s?.unknownCitations)}${good ? '' : ' ⚠'}\n${(s?.text ?? r.text).replace(/\n/g, ' ⏎ ')}`)
    }
  }
  return ok(pass, '\n' + out.join('\n'))
}, { live: true })

// ─────────────────────────────── users ───────────────────────────────

/**
 * admin; System (matrix, view-personal); hr-assistant; General (no matrix);
 * DevOps (no matrix); and a "limited" General who has the Data and Dashboards sections but not
 * view-personal nor dashboards.edit — General's preset rows are raised in the database for that.
 */
async function users() {
  if (ctx.users) return ctx.users
  const secret = 'mx-' + crypto.randomBytes(6).toString('hex')
  const u = {}
  for (const [k, name, role] of [['sys', 'mx-system', 'System'], ['hra', 'mx-hr', 'HrAssistant'], ['gen', 'mx-general', 'General'],
    ['dev', 'mx-devops', 'DevOps'], ['limited', 'mx-limited', 'General']]) {
    u[k] = await login(name, secret)
    if (role !== 'General') await asAdmin('PUT', `/api/admin/rbac/users/${u[k].id}/role`, { role })
  }
  // The limited user: their own Allow overrides, written while the API is stopped.
  await ctx.api.stop()
  const { DatabaseSync } = await import('node:sqlite')
  const db = new DatabaseSync(ctx.api.dbPath)
  for (const key of ['tab:matrix', 'section:matrix.data', 'section:matrix.dashboards'])
    db.prepare("insert into UserPermissionOverrides (UserId, Key, Effect, UpdatedAtUtc) values (?, ?, 'allow', ?)").run(u.limited.id, key, new Date().toISOString())
  db.close()
  ctx.api = await startApi({ fakeUrl: ctx.fake.url, runDir: ctx.api.runDir, port: ctx.api.port, env: API_ENV })
  ctx.users = u
  return u
}

// ─────────────────────────────── runner ───────────────────────────────

function parseArgs(argv) {
  const a = { phase: 'M1', only: null, build: true, live: false, shots: path.join(EVAL_ROOT, 'shots'), model: 'gemma4:12b' }
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === '--phase') a.phase = argv[++i]
    else if (argv[i] === '--only') a.only = argv[++i]
    else if (argv[i] === '--no-build') a.build = false
    else if (argv[i] === '--live') a.live = true
    else if (argv[i] === '--shots') a.shots = path.resolve(argv[++i])
    else if (argv[i] === '--model') a.model = argv[++i]
    else if (argv[i] === '--golden') a.golden = argv[++i]
    else if (argv[i] === '--models') a.models = argv[++i].split(',').map((s) => s.trim()).filter(Boolean)
  }
  return a
}

function syncWebRoot() {
  const dist = path.join(REPO, 'frontend', 'dist')
  const target = path.join(API_BIN_DIR, 'wwwroot')
  fs.rmSync(target, { recursive: true, force: true })
  if (fs.existsSync(dist)) fs.cpSync(dist, target, { recursive: true })
}

/** The model's answer to a mapping-suggestion request: every header it is asked about, mapped or "none". */
function scriptFake(fake) {
  fake.setChatScript((body) => {
    const system = body?.messages?.[0]?.content ?? ''
    if (/^You write the summary/.test(system)) return fakeSummary(body)
    if (body?.format?.properties?.kind) return fakePlan(body)
    if (/^You answer a manager's question/.test(system)) return fakeAnswer(body)
    const schema = JSON.stringify(body?.format ?? {})
    if (!schema.includes('suggestions')) return null
    const ask = JSON.stringify(body.messages ?? [])
    const headers = [...ask.matchAll(/\\"header\\":\\"([^\\]+)\\"/g)].map((m) => m[1])
    const suggestions = headers.map((h) => ({ header: h, field: /machine/i.test(h) ? 'position' : 'none', confidence: 0.4 }))
    return { words: [JSON.stringify({ suggestions })] }
  })
}

async function main() {
  const args = ctx.args = parseArgs(process.argv.slice(2))
  if (!process.env.MATRIX_EVAL_ADMIN_USER || !process.env.MATRIX_EVAL_ADMIN_SECRET) {
    console.error('Set MATRIX_EVAL_ADMIN_USER and MATRIX_EVAL_ADMIN_SECRET to the seeded administrator.')
    process.exit(2)
  }
  const order = ['M1', 'M2', 'M3', 'M4']
  const selected = checks.filter((c) => order.indexOf(c.phase) <= order.indexOf(args.phase) && (args.live || !c.live)
    && (!args.only || c.id === args.only || c.id.startsWith(args.only + '.')))
    // The live checks last: once the real files are imported they are the current datasets,
    // and every fixture check before them must still read the fixtures.
    .sort((a, b) => Number(a.live) - Number(b.live))
  if (!selected.length) { console.error('No checks match.'); process.exit(2) }

  ctx.M = makeFixtures(FIXTURES)
  if (args.build) buildApi()
  syncWebRoot()
  const runDir = fs.mkdtempSync(path.join(EVAL_ROOT, 'run-'))
  ctx.fake = await startFakeOllama()
  scriptFake(ctx.fake)
  let failed = 0
  try {
    ctx.api = await startApi({ fakeUrl: ctx.fake.url, runDir, env: API_ENV })
    ctx.admin = await login(process.env.MATRIX_EVAL_ADMIN_USER, process.env.MATRIX_EVAL_ADMIN_SECRET)
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
