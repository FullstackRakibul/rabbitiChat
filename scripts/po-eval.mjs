#!/usr/bin/env node
// PO Studio eval — see docs/PO_STUDIO_PROMPT.md §7.
//
//   node scripts/po-eval.mjs                 every check
//   node scripts/po-eval.mjs --phase 1       checks up to phase 1
//   node scripts/po-eval.mjs --only G1.4     the checks of one gate
//   node scripts/po-eval.mjs --no-build      reuse the last eval build of the API
//
// The fake Ollama and the API start-up are REUSED from scripts/oddity-eval.mjs
// (§7: no third copy). What this file adds is a SCRIPTED /api/chat: a test says
// "the first answer fails the size check, the second fixes it", and the fake
// answers extraction calls from that script while every request it received is
// recorded for assertion.
//
// A check asserts against what the fake recorded, what the client received, or
// what the database holds — never against the API's own account of what it did.
// Node 18+, no dependencies (node:sqlite for the direct database reads).

import http from 'node:http'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import crypto from 'node:crypto'
import { spawnSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'

import { buildApi, startApi, call, fakeEmbedding } from './oddity-eval.mjs'

const REPO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const EVAL_ROOT = path.join(os.tmpdir(), 'po-eval')
const FIXTURES = path.join(REPO, 'scripts', 'po-fixtures')

const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

// ─────────────────────────── expected values, by hand ───────────────────────────
// Mirrors the design contract (§3.3), not the code: a value read from
// PoPromptBuilder.cs would only prove the code agrees with itself.

const DEFAULT_MODEL = 'gemma4:12b'
const NUM_CTX = 16384
const OUTPUT_RESERVE = 4096

// ─────────────────────────── scripted fake Ollama ───────────────────────────

/**
 * A fake Ollama whose /api/chat answers come from a script. Everything else
 * (tags, show, version, embed) answers like the oddity fake so the API starts
 * and its other features keep working.
 *
 * script: { [key]: string[] } — answers in order for calls matching that key.
 * The key is chosen by matching the request's user content against the
 * registered fixture names, so a test can queue "bad then good".
 */
async function startScriptedOllama() {
  const requests = []
  let script = {}
  const used = {}

  /** Which script key a request belongs to: the fixture whose marker appears in the prompt. */
  const keyFor = (body) => {
    const text = JSON.stringify(body?.messages ?? '')
    for (const k of Object.keys(script)) if (text.includes(k)) return k
    return '*'
  }

  const server = http.createServer(async (req, res) => {
    const chunks = []
    for await (const c of req) chunks.push(c)
    const raw = Buffer.concat(chunks).toString('utf8')
    let body = raw
    try { body = raw ? JSON.parse(raw) : null } catch { /* keep raw */ }

    const url = new URL(req.url, 'http://fake')
    const rec = { method: req.method, path: url.pathname, body, scriptKey: null, answer: null }

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
          models: [DEFAULT_MODEL, 'profile-model', 'candidate-model', 'nomic-embed-text']
            .map((name, i) => ({
              name, model: name, modified_at: '2026-01-01T00:00:00Z',
              size: 1000 + i, digest: `sha256:${String(i).padStart(64, '0')}`,
              details: { format: 'gguf', family: 'fake', parameter_size: '1B', quantization_level: 'Q4' },
            })),
        })

      case 'POST /api/show':
        return json(200, { capabilities: ['completion'], model_info: { 'fake.context_length': NUM_CTX } })

      case 'POST /api/embed': {
        const input = Array.isArray(body?.input) ? body.input : [body?.input ?? '']
        return json(200, { model, embeddings: input.map((t) => fakeEmbedding(t)) })
      }

      case 'POST /api/embeddings':
        return json(200, { embedding: fakeEmbedding(body?.prompt ?? '') })

      case 'POST /api/chat': {
        const key = keyFor(body)
        rec.scriptKey = key
        requests.push(rec)

        const queue = script[key] ?? script['*'] ?? []
        used[key] ??= 0
        const answer = queue[Math.min(used[key], queue.length - 1)] ?? '{}'
        used[key]++
        rec.answer = answer

        const frame = {
          model, created_at: '2026-01-01T00:00:00Z', done: true, done_reason: 'stop',
          message: { role: 'assistant', content: answer },
          prompt_eval_count: 123, eval_count: 45,
          total_duration: 1_000_000_000,
        }
        if (body?.stream === false) return json(200, frame)
        res.writeHead(200, { 'Content-Type': 'application/x-ndjson' })
        res.write(JSON.stringify({ ...frame, done: false }) + '\n')
        res.write(JSON.stringify({ ...frame, message: { role: 'assistant', content: '' } }) + '\n')
        return res.end()
      }

      default:
        requests.push(rec)
        return json(404, { error: 'fake: not found' })
    }
  })

  await new Promise((r) => server.listen(0, '127.0.0.1', r))
  return {
    url: `http://127.0.0.1:${server.address().port}`,
    requests,
    /** Queue the answers for the next calls whose prompt contains each key. */
    setScript: (s) => { script = s; for (const k of Object.keys(used)) delete used[k] },
    chats: () => requests.filter((r) => r.path === '/api/chat'),
    reset: () => { requests.length = 0; for (const k of Object.keys(used)) delete used[k] },
    stop: () => new Promise((r) => { server.closeAllConnections(); server.close(() => r()) }),
  }
}

// ─────────────────────────── fixtures ───────────────────────────
// Three small TEXT POs for two buyers, with the correct JSON written by hand.
// "EVALPO-x" in the body is the script key: it survives into the prompt, so the
// fake can answer per fixture.

const PO_FIXTURES = [
  {
    key: 'EVALPO-1',
    buyer: 'EvalKohls',
    file: 'kohls-prepack.txt',
    text: [
      '--- page 1 ---',
      'KOHLS DEPARTMENT STORES        PURCHASE ORDER',
      'Order #: EVALPO-1        PO Type: PREPACK',
      'PO Date: 2026-01-05      Cancel Date: 2026-03-01',
      'Ship Window: 2026-02-01 to 2026-02-14',
      'Incoterms: FOB',
      'Vendor: ALPHA GARMENTS LTD   Country: BD',
      '',
      'Style        UPC            Unit Price   Packs    Line Total',
      'MS6SX117RN  400110380823    12.50        100      8750.00',
      '',
      'Prepack breakdown (per pack):',
      'Size S  qty 2   cost 12.50  retail 24.99',
      'Size M  qty 3   cost 12.50  retail 24.99',
      'Size L  qty 2   cost 12.50  retail 24.99',
      '',
      'Purchase Order Total: 8750.00',
    ].join('\n'),
    // 100 packs x 7 pieces = 700 pieces; 700 x 12.50 = 8750.00
    correct: {
      header: {
        buyer: 'EvalKohls', orderNumber: 'EVALPO-1', poType: 'PREPACK',
        poDate: '2026-01-05', cancelDate: '2026-03-01',
        shippingWindow: { start: '2026-02-01', end: '2026-02-14' },
        incoterms: 'FOB', vendor: { name: 'ALPHA GARMENTS LTD', country: 'BD' },
      },
      orderLine: {
        styleNumber: 'MS6SX117RN', upc: '400110380823',
        unitPrice: 12.5, quantity: 100, itemTotal: 8750.0, isPrepack: true,
      },
      prepackComponents: [
        { line: 1, size: 'S', qtyPerPrepack: 2, unitCost: 12.5, retailPrice: 24.99 },
        { line: 2, size: 'M', qtyPerPrepack: 3, unitCost: 12.5, retailPrice: 24.99 },
        { line: 3, size: 'L', qtyPerPrepack: 2, unitCost: 12.5, retailPrice: 24.99 },
      ],
      purchaseOrderTotal: 8750.0,
    },
  },
  {
    key: 'EVALPO-2',
    buyer: 'EvalKohls',
    file: 'kohls-flat.txt',
    text: [
      '--- page 1 ---',
      'KOHLS DEPARTMENT STORES        PURCHASE ORDER',
      'Order #: EVALPO-2        PO Type: FLAT',
      'PO Date: 2026-02-10',
      'Delivery Window: 2026-04-01 to 2026-04-10',
      'Vendor: BETA TEXTILES   Country: IN',
      '',
      'Style       UPC            Unit Price   Qty      Line Total',
      'WS1AB222CD  400110999001    7.25         400      2900.00',
      '',
      'Purchase Order Total: 2900.00',
    ].join('\n'),
    correct: {
      header: {
        buyer: 'EvalKohls', orderNumber: 'EVALPO-2', poType: 'FLAT',
        poDate: '2026-02-10',
        deliveryWindow: { start: '2026-04-01', end: '2026-04-10' },
        vendor: { name: 'BETA TEXTILES', country: 'IN' },
      },
      orderLine: {
        styleNumber: 'WS1AB222CD', upc: '400110999001',
        unitPrice: 7.25, quantity: 400, itemTotal: 2900.0, isPrepack: false,
      },
      prepackComponents: [],
      purchaseOrderTotal: 2900.0,
    },
  },
  {
    key: 'EVALPO-3',
    buyer: 'EvalWalmart',
    file: 'walmart-flat.txt',
    text: [
      '--- page 1 ---',
      'WALMART STORES INC             PURCHASE ORDER',
      'Order #: EVALPO-3        PO Type: FLAT',
      'PO Date: 2026-03-03      Cancel Date: 2026-05-01',
      'Ex-Factory End Date: 2026-04-20',
      'Incoterms: DDP',
      'Vendor: GAMMA APPAREL   Country: VN',
      '',
      'Style       UPC            Unit Price   Qty      Line Total',
      'KD9ZZ100XY  400111222333    3.00         1000     3000.00',
      '',
      'Purchase Order Total: 3000.00',
    ].join('\n'),
    correct: {
      header: {
        buyer: 'EvalWalmart', orderNumber: 'EVALPO-3', poType: 'FLAT',
        poDate: '2026-03-03', cancelDate: '2026-05-01',
        exitFactoryEndDate: '2026-04-20', incoterms: 'DDP',
        vendor: { name: 'GAMMA APPAREL', country: 'VN' },
      },
      orderLine: {
        styleNumber: 'KD9ZZ100XY', upc: '400111222333',
        unitPrice: 3.0, quantity: 1000, itemTotal: 3000.0, isPrepack: false,
      },
      prepackComponents: [],
      purchaseOrderTotal: 3000.0,
    },
  },
]

function writeFixtures() {
  fs.mkdirSync(FIXTURES, { recursive: true })
  for (const f of PO_FIXTURES) {
    fs.writeFileSync(path.join(FIXTURES, f.file), f.text, 'utf8')
    fs.writeFileSync(
      path.join(FIXTURES, f.file.replace(/\.txt$/, '.expected.json')),
      JSON.stringify(f.correct, null, 2) + '\n', 'utf8')
  }
  fs.writeFileSync(path.join(FIXTURES, 'README.md'), [
    '# PO eval fixtures',
    '',
    'Written by `scripts/po-eval.mjs` (the eval owns them, do not hand-edit).',
    'Each `.txt` is a small text purchase order; the matching `.expected.json` is',
    'the correct extraction, written by hand. `EVALPO-n` in the body is the key the',
    'fake Ollama scripts its answers by.',
    '',
    'Real PDFs from KAKAROT land here too, beside their `.expected.json` (§11 Q1).',
    '',
  ].join('\n'), 'utf8')
}

// ─────────────────────────── checks ───────────────────────────

const checks = []
function check(id, phase, name, fn) { checks.push({ id, phase, name, fn }) }

/** Context shared by the checks, filled by main(). */
const ctx = {
  api: null, fake: null, token: null, profiles: {}, docs: {},
}

function read(rel) {
  const p = path.join(REPO, rel)
  return fs.existsSync(p) ? fs.readFileSync(p, 'utf8') : null
}

function exists(rel) { return fs.existsSync(path.join(REPO, rel)) }

/** grep -rn over a directory; returns matching "file:line: text" strings. */
function grepDir(rel, re, { ext = null } = {}) {
  const root = path.join(REPO, rel)
  const hits = []
  if (!fs.existsSync(root)) return hits
  const walk = (dir) => {
    for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
      const p = path.join(dir, e.name)
      if (e.isDirectory()) { if (e.name !== 'node_modules') walk(p); continue }
      if (ext && !e.name.endsWith(ext)) continue
      const lines = fs.readFileSync(p, 'utf8').split('\n')
      lines.forEach((l, i) => {
        if (re.test(l)) hits.push(`${path.relative(REPO, p).replace(/\\/g, '/')}:${i + 1}: ${l.trim()}`)
      })
    }
  }
  walk(root)
  return hits
}

async function sqlRows(query) {
  const { DatabaseSync } = await import('node:sqlite')
  const db = new DatabaseSync(ctx.api.dbPath, { readOnly: true })
  try { return db.prepare(query).all() } finally { db.close() }
}

async function login(username, secret) {
  // /api/auth/login auto-registers an unknown username, which is how the eval
  // gets its administrator on a throwaway database.
  const r = await call(ctx.api.url, 'POST', '/api/auth/login', { body: { username, secret } })
  if (r.status !== 200 || !r.json?.token)
    throw new Error(`login failed (${r.status}): ${r.text.slice(0, 300)}`)
  const me = await call(ctx.api.url, 'GET', '/api/me/tabs', { token: r.json.token })
  return { token: r.json.token, id: me.json?.id }
}

/** Upload one text fixture, returning its document id. */
async function upload(fixture, token = ctx.token) {
  const form = new FormData()
  form.append('file', new Blob([fixture.text], { type: 'text/plain' }), fixture.file)
  const res = await fetch(`${ctx.api.url}/api/documents`, {
    method: 'POST', headers: { Authorization: `Bearer ${token}` }, body: form,
  })
  const text = await res.text()
  let json = null
  try { json = JSON.parse(text) } catch { /* not JSON */ }
  if (!res.ok) throw new Error(`upload ${fixture.file} failed (${res.status}): ${text.slice(0, 300)}`)
  return json?.id ?? json?.documentId
}

const api = (method, p, body) => call(ctx.api.url, method, p, { token: ctx.token, body })

/**
 * Run an extraction. `buyerProfileId` is a QUERY parameter on this endpoint, not
 * a body field — see backend/DTOs/OllamaDtos.cs, whose ExtractRequest carries
 * only Model. Passing it in the body would silently extract with no profile.
 */
const extract = (docId, profileId) =>
  api('POST', `/api/documents/${docId}/extract?buyerProfileId=${profileId}`, {})

// ───────── Phase 1 ─────────

check('G1.1', 1, 'one PO Studio tab, /documents redirects, sections gated by docs/workspace', async () => {
  const tabs = read('frontend/src/app/router.js') ?? ''
  const allTabs = grepDir('frontend/src', /ALL_TABS|MODULE_ROUTES/)
  const studio = exists('frontend/src/components/tabs/PoStudioTab.vue')
  if (!studio) return { pass: false, detail: 'frontend/src/components/tabs/PoStudioTab.vue is missing' }

  const stillSeparate = exists('frontend/src/components/tabs/DocumentsTab.vue')
    && exists('frontend/src/components/tabs/WorkspaceTab.vue')
  if (stillSeparate)
    return { pass: false, detail: 'DocumentsTab.vue and WorkspaceTab.vue both still exist — the tabs were not merged' }

  const redirect = /\/documents/.test(tabs) && /workspace/.test(tabs)
  if (!redirect) return { pass: false, detail: '/documents redirect to /workspace not found in router.js' }

  const label = grepDir('frontend/src', /PO Studio/)
  if (!label.length) return { pass: false, detail: 'no "PO Studio" label found in frontend/src' }

  return { pass: true, detail: `PoStudioTab.vue present; label at ${label[0].split(':').slice(0, 2).join(':')}; ${allTabs.length} ALL_TABS/MODULE_ROUTES refs` }
})

check('G1.2', 1, 'buyer list comes from /api/buyer-profiles, not hard-coded', async () => {
  const hits = grepDir('frontend/src', /'Walmart'|"Walmart"|'Kohls'|"Kohls"/)
  if (hits.length) return { pass: false, detail: `hard-coded buyer names remain:\n    ${hits.join('\n    ')}` }

  const r = await api('GET', '/api/buyer-profiles')
  if (r.status !== 200) return { pass: false, detail: `GET /api/buyer-profiles → ${r.status}` }
  const names = (r.json ?? []).map((p) => p.name)
  const fetched = grepDir('frontend/src', /buyer-profiles/)
  if (!fetched.length) return { pass: false, detail: 'no frontend code calls /api/buyer-profiles' }
  return { pass: true, detail: `no hard-coded names; API lists [${names.join(', ')}]; fetched at ${fetched[0].split(':').slice(0, 2).join(':')}` }
})

check('G1.3', 1, 'upload stepper has the six steps and stops at a forced failure', async () => {
  const dir = 'frontend/src/components/po-studio'
  if (!exists(dir)) return { pass: false, detail: `${dir} is missing` }
  const stepper = read(`${dir}/UploadStepper.vue`)
  if (!stepper) return { pass: false, detail: `${dir}/UploadStepper.vue is missing` }
  const steps = ['Uploading', 'Reading text', 'Detecting buyer', 'Extracting', 'Checking', 'Ready for review']
  const missing = steps.filter((s) => !stepper.includes(s))
  if (missing.length) return { pass: false, detail: `stepper is missing steps: ${missing.join(', ')}` }
  const retry = /retry/i.test(stepper)
  if (!retry) return { pass: false, detail: 'stepper has no retry affordance' }
  return { pass: true, detail: `all six steps and a retry present in ${dir}/UploadStepper.vue` }
})

check('G1.4', 1, 'PoPromptBuilder is deterministic and matches the hand-written layout', async () => {
  const r = await api('POST', '/api/po-studio/prompt-preview', {
    buyer: 'EvalKohls',
    documentText: PO_FIXTURES[0].text,
  })
  if (r.status !== 200) return { pass: false, detail: `POST /api/po-studio/prompt-preview → ${r.status} ${r.text.slice(0, 200)}` }

  // Ten calls, byte-identical.
  const hashes = new Set()
  for (let i = 0; i < 10; i++) {
    const again = await api('POST', '/api/po-studio/prompt-preview', {
      buyer: 'EvalKohls', documentText: PO_FIXTURES[0].text,
    })
    hashes.add(crypto.createHash('sha256').update(JSON.stringify(again.json?.messages ?? null)).digest('hex'))
  }
  if (hashes.size !== 1) return { pass: false, detail: `10 calls produced ${hashes.size} different message sets` }

  const sys = r.json?.messages?.[0]?.content ?? ''
  const order = ['## Buyer rules', '## Glossary', '## Output schema']
  let at = -1
  for (const h of order) {
    const i = sys.indexOf(h)
    if (i < 0) return { pass: false, detail: `system prompt has no "${h}" heading` }
    if (i < at) return { pass: false, detail: `"${h}" appears out of the §3.3 order` }
    at = i
  }
  const user = r.json?.messages?.[1]?.content ?? ''
  if (!user.includes('--- page 1 ---'))
    return { pass: false, detail: 'user message lost the "--- page N ---" markers' }
  return { pass: true, detail: `byte-identical over 10 calls (${[...hashes][0].slice(0, 12)}…); headings in §3.3 order` }
})

check('G1.5', 1, 'the fake received the schema object as format, temperature 0, think false, the profile model', async () => {
  ctx.fake.reset()
  ctx.fake.setScript({ 'EVALPO-2': [JSON.stringify(PO_FIXTURES[1].correct)] })
  const doc = await upload(PO_FIXTURES[1])
  const r = await extract(doc, ctx.profiles.EvalKohls)
  if (r.status !== 200) return { pass: false, detail: `extract → ${r.status} ${r.text.slice(0, 200)}` }

  const chat = ctx.fake.chats().at(-1)
  if (!chat) return { pass: false, detail: 'the fake received no /api/chat call' }
  const b = chat.body ?? {}
  const problems = []
  if (typeof b.format !== 'object' || b.format === null)
    problems.push(`format is ${JSON.stringify(b.format)}, not a schema object`)
  else if (!b.format.properties && !b.format.type)
    problems.push('format object is not a JSON schema (no type/properties)')
  if (b.options?.temperature !== 0) problems.push(`options.temperature is ${b.options?.temperature}`)
  if (b.think !== false) problems.push(`think is ${JSON.stringify(b.think)}, not false`)
  if (b.model !== 'profile-model')
    problems.push(`model is "${b.model}", not the profile's "profile-model"`)
  if (problems.length) return { pass: false, detail: problems.join('; ') }
  return { pass: true, detail: `format=schema object, temperature=0, think=false, model=${b.model}` }
})

check('G1.5b', 1, 'an empty profile model falls back to the install default', async () => {
  ctx.fake.reset()
  ctx.fake.setScript({ 'EVALPO-3': [JSON.stringify(PO_FIXTURES[2].correct)] })
  const doc = await upload(PO_FIXTURES[2])
  const r = await extract(doc, ctx.profiles.EvalWalmart)
  if (r.status !== 200) return { pass: false, detail: `extract → ${r.status} ${r.text.slice(0, 200)}` }
  const chat = ctx.fake.chats().at(-1)
  if (chat?.body?.model !== DEFAULT_MODEL)
    return { pass: false, detail: `model is "${chat?.body?.model}", not the default "${DEFAULT_MODEL}"` }
  return { pass: true, detail: `empty ExtractionModel → ${DEFAULT_MODEL}` }
})

check('G1.6', 1, 'budget drops examples before the glossary and never cuts the document', async () => {
  if (grepDir('backend/Extraction', /60_000|60000/).length)
    return { pass: false, detail: 'the 60,000-character cut is still in backend/Extraction' }

  // A document that cannot fit must fail with the §3.3 message.
  const huge = 'x '.repeat(200_000)
  const r = await api('POST', '/api/po-studio/prompt-preview', {
    buyer: 'EvalKohls', documentText: huge,
  })
  const msg = r.json?.error ?? r.json?.report?.error ?? r.text
  if (!/Document is \d+ tokens; the window holds \d+\. Split it or raise Extraction:NumCtx\./.test(msg))
    return { pass: false, detail: `oversized document did not fail with the §3.3 message; got: ${String(msg).slice(0, 250)}` }

  // With a tight window, examples go before the glossary.
  const tight = await api('POST', '/api/po-studio/prompt-preview', {
    buyer: 'EvalKohls', documentText: PO_FIXTURES[0].text, numCtxOverride: 2600,
  })
  const dropped = tight.json?.report?.dropped ?? []
  const sys = tight.json?.messages?.[0]?.content ?? ''
  if (!dropped.length)
    return { pass: false, detail: 'a tight window dropped nothing — the budget is not being applied' }
  const droppedGlossaryFirst = dropped.indexOf('glossary') >= 0
    && dropped.indexOf('glossary') < Math.max(dropped.indexOf('example2'), dropped.indexOf('example1'))
  if (droppedGlossaryFirst)
    return { pass: false, detail: `glossary dropped before the examples: [${dropped.join(', ')}]` }
  if (sys.includes('Worked example 2') && dropped.includes('example2'))
    return { pass: false, detail: 'example 2 reported dropped but is still in the prompt' }

  // Nothing the fake ever received was a cut document.
  const cut = ctx.fake.chats().filter((c) => {
    const u = c.body?.messages?.at(-1)?.content ?? ''
    return u.length === 60_000 || /\.\.\.$|…$/.test(u.trim())
  })
  if (cut.length) return { pass: false, detail: `${cut.length} call(s) carried a truncated document` }
  return { pass: true, detail: `oversized → §3.3 message; tight window dropped [${dropped.join(', ')}]; no truncated document reached the fake` }
})

check('G1.7', 1, 'one repair round: a better second answer wins, a worse one is discarded', async () => {
  // Case A: first answer fails a check, the second fixes it → corrected JSON kept.
  const bad = structuredClone(PO_FIXTURES[0].correct)
  bad.purchaseOrderTotal = 9999.0            // breaks the arithmetic check
  const good = PO_FIXTURES[0].correct

  ctx.fake.reset()
  ctx.fake.setScript({ 'EVALPO-1': [JSON.stringify(bad), JSON.stringify(good)] })
  const docA = await upload(PO_FIXTURES[0])
  const a = await extract(docA, ctx.profiles.EvalKohls)
  if (a.status !== 200) return { pass: false, detail: `extract A → ${a.status} ${a.text.slice(0, 200)}` }

  const rowsA = await sqlRows(`SELECT "RepairUsed", "ResultJson", "FirstResultJson" FROM "ExtractionRuns" WHERE "DocumentId" = ${docA} ORDER BY "Id" DESC LIMIT 1`)
  const rowA = rowsA[0]
  if (!rowA) return { pass: false, detail: 'no ExtractionRuns row for the repair case' }
  if (!rowA.RepairUsed) return { pass: false, detail: `RepairUsed is ${rowA.RepairUsed} after a successful repair` }
  if (Number(JSON.parse(rowA.ResultJson)?.purchaseOrderTotal) !== 8750)
    return { pass: false, detail: `kept total is ${JSON.parse(rowA.ResultJson)?.purchaseOrderTotal}, not the corrected 8750` }
  if (Number(JSON.parse(rowA.FirstResultJson ?? '{}')?.purchaseOrderTotal) !== 9999)
    return { pass: false, detail: 'FirstResultJson does not hold the pre-repair answer' }
  const callsA = ctx.fake.chats().length
  if (callsA !== 2) return { pass: false, detail: `the repair case made ${callsA} chat calls, expected 2` }

  // Case B: the second answer is worse → the first is kept, RepairUsed false.
  const worse = structuredClone(PO_FIXTURES[0].correct)
  worse.purchaseOrderTotal = 1.0
  worse.orderLine.quantity = null            // fails more checks than `bad`
  ctx.fake.reset()
  ctx.fake.setScript({ 'EVALPO-1': [JSON.stringify(bad), JSON.stringify(worse)] })
  const docB = await upload(PO_FIXTURES[0])
  const b = await extract(docB, ctx.profiles.EvalKohls)
  if (b.status !== 200) return { pass: false, detail: `extract B → ${b.status} ${b.text.slice(0, 200)}` }
  const rowB = (await sqlRows(`SELECT "RepairUsed", "ResultJson" FROM "ExtractionRuns" WHERE "DocumentId" = ${docB} ORDER BY "Id" DESC LIMIT 1`))[0]
  if (!rowB) return { pass: false, detail: 'no ExtractionRuns row for the discard case' }
  if (rowB.RepairUsed) return { pass: false, detail: 'RepairUsed is true although the second answer was worse' }
  if (Number(JSON.parse(rowB.ResultJson)?.purchaseOrderTotal) !== 9999)
    return { pass: false, detail: `the worse answer was kept (total ${JSON.parse(rowB.ResultJson)?.purchaseOrderTotal})` }

  return { pass: true, detail: 'better repair kept with RepairUsed=1 and FirstResultJson preserved; worse repair discarded' }
})

check('G1.8', 1, 'a run stores Model, ProfileVersion, PromptHash, tokens and duration', async () => {
  const cols = (await sqlRows(`PRAGMA table_info("ExtractionRuns")`)).map((c) => c.name)
  const want = ['Model', 'ProfileVersion', 'PromptHash', 'RepairUsed', 'FirstResultJson', 'PromptTokens', 'OutputTokens', 'DurationMs']
  const missing = want.filter((c) => !cols.includes(c))
  if (missing.length) return { pass: false, detail: `ExtractionRuns is missing columns: ${missing.join(', ')}` }

  ctx.fake.reset()
  ctx.fake.setScript({ 'EVALPO-2': [JSON.stringify(PO_FIXTURES[1].correct)] })
  const doc = await upload(PO_FIXTURES[1])
  await extract(doc, ctx.profiles.EvalKohls)
  const row = (await sqlRows(`SELECT ${want.map((c) => `"${c}"`).join(', ')}, "ProfileVersion" FROM "ExtractionRuns" WHERE "DocumentId" = ${doc} ORDER BY "Id" DESC LIMIT 1`))[0]
  if (!row) return { pass: false, detail: 'no run row written' }
  const blank = []
  if (!row.Model) blank.push('Model')
  if (row.ProfileVersion === null) blank.push('ProfileVersion')
  if (!row.PromptHash || String(row.PromptHash).length !== 64) blank.push('PromptHash(sha256)')
  if (!row.PromptTokens) blank.push('PromptTokens')
  if (!row.OutputTokens) blank.push('OutputTokens')
  if (row.DurationMs === null) blank.push('DurationMs')
  if (blank.length) return { pass: false, detail: `written but empty: ${blank.join(', ')}` }

  // Editing the profile bumps Version and changes PromptHash.
  const before = row
  const pid = ctx.profiles.EvalKohls
  const cur = (await api('GET', '/api/buyer-profiles')).json.find((p) => p.id === pid)
  const put = await api('PUT', `/api/buyer-profiles/${pid}`, {
    ...cur, rules: (cur.rules ?? '') + '\nAll quantities are packs.',
  })
  if (put.status >= 400) return { pass: false, detail: `profile PUT → ${put.status} ${put.text.slice(0, 200)}` }
  const v = (await api('GET', '/api/buyer-profiles')).json.find((p) => p.id === pid)?.version
  if (!(v > (cur.version ?? 0)))
    return { pass: false, detail: `Version did not increase on save (${cur.version} → ${v})` }

  ctx.fake.reset()
  ctx.fake.setScript({ 'EVALPO-2': [JSON.stringify(PO_FIXTURES[1].correct)] })
  const doc2 = await upload(PO_FIXTURES[1])
  await extract(doc2, pid)
  const after = (await sqlRows(`SELECT "PromptHash", "ProfileVersion" FROM "ExtractionRuns" WHERE "DocumentId" = ${doc2} ORDER BY "Id" DESC LIMIT 1`))[0]
  if (after?.PromptHash === before.PromptHash)
    return { pass: false, detail: 'PromptHash did not change after the rules were edited' }
  if (!(after?.ProfileVersion > before.ProfileVersion))
    return { pass: false, detail: `run ProfileVersion did not follow the bump (${before.ProfileVersion} → ${after?.ProfileVersion})` }
  return { pass: true, detail: `all columns written; Version ${cur.version}→${v}; PromptHash ${String(before.PromptHash).slice(0, 8)}…→${String(after.PromptHash).slice(0, 8)}…` }
})

check('G1.9', 1, 'worked examples: confirmed runs only, held-out runs refused', async () => {
  const pid = ctx.profiles.EvalKohls

  // An unconfirmed run cannot be an example.
  ctx.fake.reset()
  ctx.fake.setScript({ 'EVALPO-2': [JSON.stringify(PO_FIXTURES[1].correct)] })
  const doc = await upload(PO_FIXTURES[1])
  await extract(doc, pid)
  const unconfirmed = (await sqlRows(`SELECT "Id" FROM "ExtractionRuns" WHERE "DocumentId" = ${doc} ORDER BY "Id" DESC LIMIT 1`))[0]?.Id
  const cur = (await api('GET', '/api/buyer-profiles')).json.find((p) => p.id === pid)
  const r1 = await api('PUT', `/api/buyer-profiles/${pid}`, { ...cur, exampleRunIds: String(unconfirmed) })
  if (r1.status < 400)
    return { pass: false, detail: `an unconfirmed run was accepted as a worked example (PUT → ${r1.status})` }
  if (!/confirm/i.test(r1.text))
    return { pass: false, detail: `refused, but without saying why: ${r1.text.slice(0, 200)}` }

  // A held-out run (DocumentId % 5 == 0) is refused even when confirmed.
  const heldOut = (await sqlRows(
    `SELECT "Id", "DocumentId" FROM "ExtractionRuns" WHERE "DocumentId" % 5 = 0 ORDER BY "Id" DESC LIMIT 1`))[0]
  const trainRun = (await sqlRows(
    `SELECT "Id", "DocumentId" FROM "ExtractionRuns" WHERE "DocumentId" % 5 != 0 ORDER BY "Id" DESC LIMIT 1`))[0]
  if (!heldOut || !trainRun)
    return { pass: false, detail: `need a held-out and a non-held-out run; have heldOut=${!!heldOut} train=${!!trainRun}` }

  for (const runId of [heldOut.Id, trainRun.Id]) {
    const conf = await api('POST', `/api/documents/${runId === heldOut.Id ? heldOut.DocumentId : trainRun.DocumentId}/extraction/confirm`,
      { resultJson: PO_FIXTURES[1].correct, edits: [] })
    if (conf.status !== 200) return { pass: false, detail: `confirm → ${conf.status} ${conf.text.slice(0, 200)}` }
  }

  const heldRun = (await sqlRows(`SELECT "Id" FROM "ExtractionRuns" WHERE "DocumentId" = ${heldOut.DocumentId} AND "ConfirmedJson" IS NOT NULL ORDER BY "Id" DESC LIMIT 1`))[0]?.Id
  const r2 = await api('PUT', `/api/buyer-profiles/${pid}`, { ...cur, exampleRunIds: String(heldRun) })
  if (r2.status < 400)
    return { pass: false, detail: `a held-out run (DocumentId ${heldOut.DocumentId} % 5 == 0) was accepted as an example` }
  if (!/held|test|hold/i.test(r2.text))
    return { pass: false, detail: `refused, but the reason does not mention the held-out set: ${r2.text.slice(0, 200)}` }

  const trainConfirmed = (await sqlRows(`SELECT "Id" FROM "ExtractionRuns" WHERE "DocumentId" = ${trainRun.DocumentId} AND "ConfirmedJson" IS NOT NULL ORDER BY "Id" DESC LIMIT 1`))[0]?.Id
  const r3 = await api('PUT', `/api/buyer-profiles/${pid}`, { ...cur, exampleRunIds: String(trainConfirmed) })
  if (r3.status >= 400)
    return { pass: false, detail: `a confirmed, non-held-out run was refused: ${r3.status} ${r3.text.slice(0, 200)}` }
  return { pass: true, detail: `unconfirmed refused; held-out run ${heldRun} (doc ${heldOut.DocumentId}) refused with a reason; confirmed train run ${trainConfirmed} accepted` }
})

check('G1.10', 1, 'Review is a split view, confirm still writes ConfirmedJson/EditsJson, no v-html', async () => {
  // The DIRECTIVE, not the word: a comment explaining why v-html is not used is
  // not a use of it. Matches v-html as an attribute, with or without a binding.
  const vhtml = grepDir('frontend/src/components/po-studio', /v-html\s*=/)
  if (vhtml.length) return { pass: false, detail: `v-html in po-studio:\n    ${vhtml.join('\n    ')}` }

  const review = read('frontend/src/components/po-studio/Review.vue')
  if (!review) return { pass: false, detail: 'frontend/src/components/po-studio/Review.vue is missing' }
  if (!/pdfjs/i.test(review)) return { pass: false, detail: 'Review.vue does not render the PDF page (no pdfjs)' }
  if (!/failed|FailedChecks|failedChecks/.test(review))
    return { pass: false, detail: 'Review.vue does not pin the failed checks' }

  // Confirm still works end to end.
  ctx.fake.reset()
  ctx.fake.setScript({ 'EVALPO-3': [JSON.stringify(PO_FIXTURES[2].correct)] })
  const doc = await upload(PO_FIXTURES[2])
  await extract(doc, ctx.profiles.EvalWalmart)
  const edited = structuredClone(PO_FIXTURES[2].correct)
  edited.header.incoterms = 'FOB'
  const conf = await api('POST', `/api/documents/${doc}/extraction/confirm`, {
    resultJson: edited, edits: [{ field: 'header.incoterms', from: 'DDP', to: 'FOB' }],
  })
  if (conf.status !== 200) return { pass: false, detail: `confirm → ${conf.status} ${conf.text.slice(0, 200)}` }
  const row = (await sqlRows(`SELECT "ConfirmedJson", "EditsJson" FROM "ExtractionRuns" WHERE "DocumentId" = ${doc} ORDER BY "Id" DESC LIMIT 1`))[0]
  if (JSON.parse(row?.ConfirmedJson ?? '{}')?.header?.incoterms !== 'FOB')
    return { pass: false, detail: 'ConfirmedJson does not hold the edited value' }
  if (!Array.isArray(JSON.parse(row?.EditsJson ?? 'null')) || !JSON.parse(row.EditsJson).length)
    return { pass: false, detail: `EditsJson is ${row?.EditsJson}` }
  return { pass: true, detail: 'no v-html; Review.vue renders the PDF and pins failed checks; confirm wrote both columns' }
})

check('G1.11', 1, 'new po-studio files use theme tokens, no hard-coded colours', async () => {
  const dir = 'frontend/src/components/po-studio'
  if (!exists(dir)) return { pass: false, detail: `${dir} is missing` }
  // #abc / #aabbcc / rgb() / hsl() outside a var() fallback.
  const hits = grepDir(dir, /(#[0-9a-fA-F]{3,8}\b|\brgba?\s*\(|\bhsla?\s*\()/)
    .filter((h) => !/var\(--/.test(h) && !/currentColor/.test(h))
  if (hits.length) return { pass: false, detail: `hard-coded colours:\n    ${hits.join('\n    ')}` }
  const files = fs.readdirSync(path.join(REPO, dir)).filter((f) => f.endsWith('.vue'))
  return { pass: true, detail: `${files.length} .vue files, no hard-coded colours` }
})

check('G1.12', 1, 'workspace chat with a pinned PO still streams', async () => {
  ctx.fake.reset()
  ctx.fake.setScript({ '*': ['The order total is 8750.00.'] })
  const doc = await upload(PO_FIXTURES[0])
  const res = await fetch(`${ctx.api.url}/api/documents/workspace-chat`, {
    method: 'POST',
    headers: { Authorization: `Bearer ${ctx.token}`, 'Content-Type': 'application/json' },
    body: JSON.stringify({ documentId: doc, message: 'What is the order total?', model: DEFAULT_MODEL }),
  })
  if (!res.ok) return { pass: false, detail: `workspace-chat → ${res.status} ${(await res.text()).slice(0, 200)}` }
  const ctype = res.headers.get('content-type') ?? ''
  const text = await res.text()
  if (!text.length) return { pass: false, detail: 'workspace-chat returned an empty body' }
  return { pass: true, detail: `streamed ${text.length} bytes (${ctype.split(';')[0]})` }
})

check('G1.13', 1, 'dotnet build and npm run build are clean', async () => {
  const dn = spawnSync('dotnet', ['build', path.join(REPO, 'backend', 'RabbitIC.Api.csproj'), '-v', 'q', '--nologo'],
    { cwd: REPO, encoding: 'utf8', shell: process.platform === 'win32' })
  if (dn.status !== 0)
    return { pass: false, detail: `dotnet build failed:\n${(dn.stdout + dn.stderr).slice(-1500)}` }
  const npm = spawnSync('npm', ['run', 'build'],
    { cwd: path.join(REPO, 'frontend'), encoding: 'utf8', shell: process.platform === 'win32' })
  if (npm.status !== 0)
    return { pass: false, detail: `npm run build failed:\n${(npm.stdout + npm.stderr).slice(-1500)}` }
  return { pass: true, detail: 'dotnet build and npm run build both exited 0' }
})

// ─────────────────────────── runner ───────────────────────────

function parseArgs(argv) {
  const a = { phase: null, only: null, build: true }
  for (let i = 2; i < argv.length; i++) {
    const v = argv[i]
    if (v === '--phase') a.phase = Number(argv[++i])
    else if (v === '--only') a.only = argv[++i]
    else if (v === '--no-build') a.build = false
  }
  return a
}

async function seed() {
  const user = process.env.PO_EVAL_ADMIN_USER
  const secret = process.env.PO_EVAL_ADMIN_SECRET
  if (!user || !secret)
    throw new Error('set PO_EVAL_ADMIN_USER and PO_EVAL_ADMIN_SECRET (never written in this script)')
  // The built-in administrator the API seeds on a fresh database; it already
  // holds SystemAdmin, and so every tab this eval calls through.
  const admin = await login(user, secret)
  ctx.token = admin.token
  ctx.userId = admin.id

  // Two buyer profiles: one naming a model, one leaving it empty for the default.
  for (const [name, model] of [['EvalKohls', 'profile-model'], ['EvalWalmart', '']]) {
    const r = await call(ctx.api.url, 'POST', '/api/buyer-profiles', {
      token: ctx.token,
      body: {
        name, detectPattern: name.toUpperCase(), parserKey: 'GenericLlm',
        chunkingStrategy: 'none', isActive: true,
        extractionModel: model,
        rules: name === 'EvalKohls' ? 'Quantities on these POs are packs; pieces = packs x pack size.' : '',
        glossary: 'prepack = a carton holding several sizes\nex-factory = date goods leave the factory',
      },
    })
    const list = await call(ctx.api.url, 'GET', '/api/buyer-profiles', { token: ctx.token })
    const items = Array.isArray(list.json) ? list.json : (list.json?.items ?? [])
    ctx.profiles[name] = items.find((p) => p.name === name)?.id
    if (!ctx.profiles[name])
      throw new Error(`could not create/find profile ${name}: POST ${r.status} ${r.text.slice(0, 200)}; GET ${list.status} ${list.text.slice(0, 200)}`)
  }
}

async function main() {
  const args = parseArgs(process.argv)
  writeFixtures()

  let selected = checks
  if (args.only) selected = checks.filter((c) => c.id === args.only || c.id.startsWith(args.only))
  if (args.phase !== null) selected = selected.filter((c) => c.phase <= args.phase)
  if (!selected.length) { console.error('no checks selected'); process.exit(2) }

  if (args.build) {
    process.stdout.write('building the API for the eval… ')
    buildApi()
    console.log('done')
  }

  const runDir = path.join(EVAL_ROOT, `run-${Date.now()}`)
  ctx.fake = await startScriptedOllama()
  ctx.api = await startApi({ fakeUrl: ctx.fake.url, runDir })

  const results = []
  try {
    await seed()
    for (const c of selected) {
      let r
      try { r = await c.fn() } catch (e) { r = { pass: false, detail: `threw: ${e.message}` } }
      results.push({ ...c, ...r })
      const mark = r.pass ? 'PASS' : 'FAIL'
      console.log(`${c.id.padEnd(7)} | ${c.name.padEnd(68)} | ${mark} | ${r.detail ?? ''}`)
    }
  } catch (e) {
    console.log(`SETUP   | eval set-up                                                          | FAIL | ${e.message}`)
    results.push({ id: 'SETUP', pass: false })
  } finally {
    await ctx.api?.stop()
    await ctx.fake?.stop()
  }

  const passed = results.filter((r) => r.pass).length
  console.log(`\n${passed}/${results.length} passed`)
  if (passed !== results.length) {
    console.log(`api log: ${ctx.api?.logPath ?? '(not started)'}`)
    process.exit(1)
  }
}

main().catch((e) => { console.error(e); process.exit(2) })
