#!/usr/bin/env node
// Matrix eval — see docs/MATRIX_PROMPT.md §4 (Phase M1) and §8.
//
//   node scripts/matrix-eval.mjs --phase M1            every M1 check on the synthetic fixtures
//   node scripts/matrix-eval.mjs --phase M1 --live     also the live gates on matrix-samples/ (counts only)
//   node scripts/matrix-eval.mjs --only G-M1.6         one gate
//   node scripts/matrix-eval.mjs --no-build            reuse the last eval build
//   node scripts/matrix-eval.mjs --shots <dir>         where G-M1.9 writes its screenshots
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

const REPO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const EVAL_ROOT = path.join(os.tmpdir(), 'matrix-eval')
const FIXTURES = path.join(EVAL_ROOT, 'fixtures')
const SAMPLES = path.join(REPO, 'matrix-samples')
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

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
  ctx.api = await startApi({ fakeUrl: ctx.fake.url, runDir: ctx.api.runDir, port: ctx.api.port })
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
    await p.session(ctx.admin, 'light', 1280, 900, `/matrix?dataset=${master.id}`)
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
    headcount_by_grade: countBy(active, (r) => r.GradeId),
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

// ─────────────────────────────── users ───────────────────────────────

/**
 * admin; System (matrix, view-personal); hr-assistant; General (no matrix);
 * DevOps (no matrix); and a "limited" General who has the Data section but not
 * view-personal — General's preset rows are raised in the database for that.
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
  for (const key of ['tab:matrix', 'section:matrix.data'])
    db.prepare("insert into UserPermissionOverrides (UserId, Key, Effect, UpdatedAtUtc) values (?, ?, 'allow', ?)").run(u.limited.id, key, new Date().toISOString())
  db.close()
  ctx.api = await startApi({ fakeUrl: ctx.fake.url, runDir: ctx.api.runDir, port: ctx.api.port })
  ctx.users = u
  return u
}

// ─────────────────────────────── runner ───────────────────────────────

function parseArgs(argv) {
  const a = { phase: 'M1', only: null, build: true, live: false, shots: path.join(EVAL_ROOT, 'shots') }
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

/** The model's answer to a mapping-suggestion request: every header it is asked about, mapped or "none". */
function scriptFake(fake) {
  fake.setChatScript((body) => {
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
  if (!selected.length) { console.error('No checks match.'); process.exit(2) }

  ctx.M = makeFixtures(FIXTURES)
  if (args.build) buildApi()
  syncWebRoot()
  const runDir = fs.mkdtempSync(path.join(EVAL_ROOT, 'run-'))
  ctx.fake = await startFakeOllama()
  scriptFake(ctx.fake)
  let failed = 0
  try {
    ctx.api = await startApi({ fakeUrl: ctx.fake.url, runDir, env: { Ollama__DefaultModel: 'fake-ok', Extraction__DefaultModel: 'fake-ok', Matrix__Model: 'fake-ok' } })
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
