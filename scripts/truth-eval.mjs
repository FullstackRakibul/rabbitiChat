#!/usr/bin/env node
// Ministry of Truth eval — docs/TRUTH_PROMPT.md §10 (Phase P1).
//
//   node scripts/truth-eval.mjs                every P1 check
//   node scripts/truth-eval.mjs --only G1.6    one gate
//   node scripts/truth-eval.mjs --no-build     reuse the last eval build of the API
//   node scripts/truth-eval.mjs --no-web       reuse frontend/dist (skip `npm run build`)
//   node scripts/truth-eval.mjs --shots <dir>  where G1.10 writes its screenshots
//
// Environment: TRUTH_EVAL_ADMIN_USER, TRUTH_EVAL_ADMIN_SECRET (the seeded
// administrator; never written here).
//
// The API runs on a throwaway database with the tunnel port open, the built
// frontend as its wwwroot, and scripts/truth/vercel-local.mjs in front of it
// reading frontend/vercel.json — so the browser checks go through the same
// rewrites and the same tunnel guard a visitor's request would. Fixtures are
// invented (ExampleLang, DemoHost, CVE-0000-0001, demo.upload_feature,
// example.com). Expected values are written from the spec, not read from the
// code. Node 22+, no dependencies.

import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import zlib from 'node:zlib'
import crypto from 'node:crypto'
import net from 'node:net'
import { spawn, spawnSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'

import { buildApi, startApi, call, startFakeOllama, API_BIN_DIR } from './oddity-eval.mjs'
import { launchBrowser } from './lib/headless-browser.mjs'
import { startVercelLocal } from './truth/vercel-local.mjs'

const REPO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const WEB = path.join(REPO, 'frontend')
const DIST = path.join(WEB, 'dist')
const EVAL_ROOT = path.join(os.tmpdir(), 'truth-eval')
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

// ─────────────────────────────── harness ───────────────────────────────

const checks = []
function check(id, name, fn) { checks.push({ id, name, fn }) }
const ok = (pass, detail) => ({ pass: !!pass, detail })
const ctx = { args: null, fx: {} }

async function login(username, secret) {
  const r = await call(ctx.api.url, 'POST', '/api/auth/login', { body: { username, secret } })
  if (r.status !== 200 || !r.json?.token) throw new Error(`login ${username} → ${r.status} ${r.text.slice(0, 200)}`)
  return { username, token: r.json.token }
}
const as = (user, method, p, body) => call(ctx.api.url, method, p, { token: user.token, body })
const admin = (method, p, body) => as(ctx.admin, method, p, body)
const pub = (p, opts) => call(ctx.api.url, 'GET', p, opts)
/** Through the stand-in for Vercel, as a visitor's browser would ask. */
const viaVercel = (p, opts) => call(ctx.vercel.url, 'GET', p, opts)
/** Straight at the tunnel port, as the tunnel daemon would ask. */
const viaTunnel = (method, p) => rawHttp(ctx.tunnelPort, method, p)

async function sql(query, ...params) {
  const { DatabaseSync } = await import('node:sqlite')
  const db = new DatabaseSync(ctx.api.dbPath, { readOnly: true })
  try { return db.prepare(query).all(...params) } finally { db.close() }
}

/** A request with the path sent byte for byte (fetch would normalise "..", which is the point of some checks). */
function rawHttp(port, method, p) {
  return new Promise((resolve, reject) => {
    const socket = net.connect({ host: '127.0.0.1', port }, () => {
      socket.write(`${method} ${p} HTTP/1.1\r\nHost: truth-tunnel.invalid\r\nX-Forwarded-For: 198.51.100.9\r\nConnection: close\r\nContent-Length: 0\r\n\r\n`)
    })
    let data = ''
    socket.on('data', (c) => { data += c })
    socket.on('end', () => {
      const [head, ...rest] = data.split('\r\n\r\n')
      resolve({ status: Number(head.split(' ')[1]), head, body: rest.join('\r\n\r\n') })
    })
    socket.on('error', reject)
    socket.setTimeout(10_000, () => { socket.destroy(); reject(new Error('timeout')) })
  })
}

// ─────────────────────────────── fixtures ───────────────────────────────
// Invented, every one (§10). The source texts are our own sentences about
// imaginary software.

const EXL_SOURCE = [
  'ExampleLang Security Advisory EXL-2026-01, published 2026-10-19.',
  'CVE-0000-0001 (CVSS 9.8) affects ExampleLang 3.1.0 through 3.1.6 and 3.2.0 through 3.2.3 when the demo.upload_feature directive is enabled.',
  'The fixed releases 3.1.7 and 3.2.4 will be published on 21 October 2026 at 14:00 UTC.',
  'Until then, set demo.upload_feature = Off. Researchers counted 120 exposed servers.',
  'FETCHED-ONLY-MARKER this sentence is third-party text and must never be public.',
].join(' ')

const DEMO_SOURCE = 'DemoHost reported a partial outage of its build service on 2026-10-20. About 35% of deployments in one region failed for two hours.'

function exlItem(techId, over = {}) {
  return {
    techId, severity: 'critical', status: 'patch_scheduled',
    headlineBn: 'ExampleLang 3.1 ও 3.2: ফাইল আপলোডে গুরুতর ত্রুটি, প্যাচ আসছে',
    headlineEn: 'ExampleLang 3.1 and 3.2: a serious upload flaw, patch coming',
    summaryBn: 'demo.upload_feature চালু থাকলে CVE-0000-0001 দিয়ে সার্ভারে কোড চালানো যায়। গবেষকেরা ১২০টি খোলা সার্ভার পেয়েছেন [1]।',
    summaryEn: 'With demo.upload_feature on, CVE-0000-0001 lets an attacker run code on the server. Researchers found 120 exposed servers [1].',
    impactBn: 'ExampleLang 3.1.0–3.1.6 আর 3.2.0–3.2.3 আক্রান্ত।',
    impactEn: 'ExampleLang 3.1.0–3.1.6 and 3.2.0–3.2.3 are affected.',
    whatToDoBn: 'প্যাচ না আসা পর্যন্ত demo.upload_feature বন্ধ রাখুন (অস্থায়ী)।',
    whatToDoEn: 'Until the patch is out, turn demo.upload_feature off (temporary).',
    patchScheduledAt: '2026-10-21T14:00:00Z',
    tags: ['upload', 'rce'], authorName: 'Eval Writer', order: 1,
    advisories: [{
      cveId: 'CVE-0000-0001', cvss: 9.8, titleBn: 'ফাইল আপলোডে কোড চালানোর ত্রুটি', titleEn: 'Code execution through file upload',
      affectedRange: '3.1.0 – 3.1.6, 3.2.0 – 3.2.3', fixedVersions: ['3.1.7', '3.2.4'], exploitedInWild: false, kevListed: false,
    }],
    timeline: [{ occurredAt: '2026-10-19T09:00:00Z', level: 'ERR', textBn: 'নিরাপত্তা পরামর্শ প্রকাশ', textEn: 'Advisory published' }],
    sources: [{
      url: 'https://example.com/advisories/exl-2026-01', publisher: 'ExampleLang Project', author: null,
      title: 'Security Advisory EXL-2026-01', publishedOn: '2026-10-19', kind: 'official', fetchedText: EXL_SOURCE,
    }],
    ...over,
  }
}

function demoItem(techId, over = {}) {
  return {
    techId, severity: 'medium', status: 'ongoing',
    headlineBn: 'DemoHost-এর বিল্ড সার্ভিসে আংশিক বিভ্রাট',
    headlineEn: null,
    summaryBn: '২০২৬-১০-২০ তারিখে এক অঞ্চলে প্রায় ৩৫% ডিপ্লয় ব্যর্থ হয়েছে [1]।',
    summaryEn: null, impactBn: null, impactEn: null, whatToDoBn: null, whatToDoEn: null, patchScheduledAt: null,
    tags: ['outage'], authorName: 'Eval Writer', order: 2, advisories: [],
    timeline: [{ occurredAt: '2026-10-20T06:00:00Z', level: 'WARN', textBn: 'বিল্ড সার্ভিসে বিভ্রাট শুরু', textEn: null }],
    sources: [{
      url: 'https://example.org/news/demohost-build-outage', publisher: 'Example News', author: 'A. Reporter',
      title: 'DemoHost build service outage', publishedOn: '2026-10-20', kind: 'news', fetchedText: DEMO_SOURCE,
    }],
    ...over,
  }
}

const ISSUE = { slug: '2026-w43', titleBn: 'সাইবার সপ্তাহ: ২০–২৬ অক্টোবর', titleEn: 'Cyber Week: 20–26 October', subtitleBn: 'এই সপ্তাহে দুটি বিষয়', subtitleEn: 'Two items this week', weekStart: '2026-10-20', weekEnd: '2026-10-26' }

/** Creates the techs and the published issue every later check reads. Once per run. */
async function setup() {
  if (ctx.fx.ready) return ctx.fx
  const tech = async (body) => {
    const r = await admin('POST', '/api/truth/techs', body)
    if (r.status !== 201) throw new Error(`tech ${body.slug} → ${r.status} ${r.text}`)
    return r.json.id
  }
  ctx.fx.exl = await tech({ slug: 'examplelang', name: 'ExampleLang', category: 'language', monogram: 'EL', hue: 210, homepage: 'https://example.com/examplelang', versionLines: ['3.1', '3.2', '3.3', '3.4'] })
  ctx.fx.demo = await tech({ slug: 'demohost', name: 'DemoHost', category: 'platform', monogram: 'DH', hue: 30, homepage: 'https://example.org/demohost' })
  const issue = await admin('POST', '/api/truth/issues', ISSUE)
  if (issue.status !== 201) throw new Error(`issue → ${issue.status} ${issue.text}`)
  ctx.fx.issueId = issue.json.id
  const a = await admin('POST', `/api/truth/issues/${ctx.fx.issueId}/items`, exlItem(ctx.fx.exl))
  const b = await admin('POST', `/api/truth/issues/${ctx.fx.issueId}/items`, demoItem(ctx.fx.demo))
  if (a.status !== 201 || b.status !== 201) throw new Error(`items → ${a.status} ${a.text} · ${b.status} ${b.text}`)
  ctx.fx.exlItem = a.json
  ctx.fx.demoItem = b.json
  const p = await admin('POST', `/api/truth/issues/${ctx.fx.issueId}/publish`)
  if (p.status !== 200) throw new Error(`publish → ${p.status} ${p.text}`)
  // A correction after publishing, so every kind of block the page draws exists from the start.
  await admin('POST', `/api/truth/items/${ctx.fx.demoItem}/corrections`, { textBn: 'বিভ্রাটের সময়কাল ঠিক করা হয়েছে।', textEn: null })
  const p2 = await admin('POST', `/api/truth/issues/${ctx.fx.issueId}/publish`)
  if (p2.status !== 200) throw new Error(`republish → ${p2.status} ${p2.text}`)
  ctx.fx.ready = true
  return ctx.fx
}

/** A fresh draft issue with one item, for the refusal checks. */
async function draftIssue(slug, item) {
  // An earlier week, so a refusal check that ends up published never becomes the current issue.
  const i = await admin('POST', '/api/truth/issues', { ...ISSUE, slug, weekStart: '2026-10-06', weekEnd: '2026-10-12' })
  if (i.status !== 201) throw new Error(`issue ${slug} → ${i.status} ${i.text}`)
  if (item) {
    const r = await admin('POST', `/api/truth/issues/${i.json.id}/items`, item)
    if (r.status !== 201) throw new Error(`item → ${r.status} ${r.text}`)
    return { id: i.json.id, itemId: r.json }
  }
  return { id: i.json.id }
}

const codes = (r) => (r.json?.problems ?? []).map((p) => p.code)

// ═══════════════════════════════ G1.1 ═══════════════════════════════

check('G1.1', 'publishing without a source is refused (noSource), and nothing is served', async () => {
  const f = await setup()
  const d = await draftIssue('eval-nosource', exlItem(f.exl, { sources: [] }))
  const r = await admin('POST', `/api/truth/issues/${d.id}/publish`)
  const served = await pub('/public/truth/issues/eval-nosource')
  return ok(r.status === 400 && codes(r).includes('noSource') && served.status === 404, `publish ${r.status} ${JSON.stringify(codes(r))} · public ${served.status}`)
})

check('G1.1', 'a source without a publisher or title, or with an http:// link, does not count', async () => {
  const f = await setup()
  const d = await draftIssue('eval-badsource', exlItem(f.exl, { sources: [{ ...exlItem(f.exl).sources[0], publisher: '', title: '' }] }))
  const r = await admin('POST', `/api/truth/issues/${d.id}/publish`)
  const http = await admin('PUT', `/api/truth/items/${d.itemId}`, exlItem(f.exl, { sources: [{ ...exlItem(f.exl).sources[0], url: 'http://example.com/a' }] }))
  return ok(r.status === 400 && codes(r).includes('sourceIncomplete') && http.status === 400,
    `incomplete → ${r.status} ${JSON.stringify(codes(r))} · http link → ${http.status} ${http.json?.error ?? ''}`)
})

check('G1.1', 'an empty Bangla headline, summary or timeline text is refused (bangla)', async () => {
  const f = await setup()
  const results = []
  for (const [name, over] of [
    ['headline', { headlineBn: '' }],
    ['summary', { summaryBn: '   ' }],
    ['timeline', { timeline: [{ occurredAt: '2026-10-19T09:00:00Z', level: 'ERR', textBn: '', textEn: 'Advisory published' }] }],
    ['impact with English only', { impactBn: null }],
  ]) {
    const d = await draftIssue(`eval-bn-${name.split(' ')[0]}`, exlItem(f.exl, over))
    const r = await admin('POST', `/api/truth/issues/${d.id}/publish`)
    results.push(`${name}: ${r.status} ${codes(r).includes('bangla') ? 'bangla' : JSON.stringify(codes(r))}`)
  }
  const blankTitle = await draftIssue('eval-bn-title', exlItem(f.exl))
  await admin('PUT', `/api/truth/issues/${blankTitle.id}`, { ...ISSUE, slug: 'eval-bn-title', titleBn: '', weekStart: '2026-10-06', weekEnd: '2026-10-12' })
  const t = await admin('POST', `/api/truth/issues/${blankTitle.id}/publish`)
  results.push(`issue title: ${t.status} ${codes(t).includes('bangla') ? 'bangla' : JSON.stringify(codes(t))}`)
  return ok(results.every((x) => / 400 bangla$/.test(x)), results.join(' · '))
})

check('G1.1', 'a number not in any source blocks publishing until fixed or verified by hand with a note', async () => {
  const f = await setup()
  const d = await draftIssue('eval-numbers', exlItem(f.exl, { summaryBn: 'গবেষকেরা ৯৯৯টি খোলা সার্ভার পেয়েছেন, CVE-0000-0002 সহ [1]।' }))
  const before = await admin('POST', `/api/truth/issues/${d.id}/publish`)
  const flagged = (before.json?.problems ?? []).filter((p) => p.code === 'numbers').map((p) => p.message.split(' ')[0])
  const shortNote = await admin('POST', `/api/truth/items/${d.itemId}/verify`, { token: '999', note: 'ok' })
  const notInItem = await admin('POST', `/api/truth/items/${d.itemId}/verify`, { token: '4242', note: 'checked by phone with the vendor' })
  await admin('POST', `/api/truth/items/${d.itemId}/verify`, { token: '999', note: 'Counted from the researchers’ public scan page by hand.' })
  await admin('POST', `/api/truth/items/${d.itemId}/verify`, { token: 'CVE-0000-0002', note: 'Second ID confirmed on the vendor page by hand.' })
  const after = await admin('POST', `/api/truth/issues/${d.id}/publish`)
  const clean = await admin('GET', `/api/truth/issues/${f.issueId}/check`)
  return ok(before.status === 400 && flagged.includes('999') && flagged.includes('CVE-0000-0002') && flagged.length === 2
    && shortNote.status === 400 && notInItem.status === 400 && after.status === 200 && clean.json?.ok === true,
  `before ${before.status} flagged ${JSON.stringify(flagged)} · short note ${shortNote.status} · unknown token ${notInItem.status} · after hand-verify ${after.status} · the fixture issue's own check ok=${clean.json?.ok}`)
})

check('G1.1', 'the number check reads Bangla digits, Bangla month names and 12-hour times in sources', async () => {
  const f = await setup()
  const src = { ...exlItem(f.exl).sources[0], fetchedText: 'প্যাচ আসবে ২১ অক্টোবর ২০২৬, 2:00 PM UTC-তে। CVE-0000-0001 (CVSS 9.8): ৩.১.০–৩.১.৬ ও ৩.২.০–৩.২.৩ আক্রান্ত, ঠিক হবে 3.1.7 ও 3.2.4-এ। 120 servers. Published 19 Oct 2026.' }
  const d = await draftIssue('eval-numbers-bn', exlItem(f.exl, { sources: [src] }))
  const r = await admin('GET', `/api/truth/issues/${d.id}/check`)
  const bad = (r.json?.items?.[0]?.numbers ?? []).filter((n) => !n.ok).map((n) => n.token)
  return ok(r.status === 200 && bad.length === 0, `unfound: ${JSON.stringify(bad)}`)
})

check('G1.1', 'only "claim" sources: the status must be unconfirmed (rule 3)', async () => {
  const f = await setup()
  const claim = { ...demoItem(f.demo).sources[0], kind: 'claim' }
  const d = await draftIssue('eval-claim', demoItem(f.demo, { sources: [claim] }))
  const r = await admin('POST', `/api/truth/issues/${d.id}/publish`)
  await admin('POST', `/api/truth/items/${d.itemId}/status`, { status: 'unconfirmed' })
  const r2 = await admin('POST', `/api/truth/issues/${d.id}/publish`)
  return ok(r.status === 400 && codes(r).includes('claimOnly') && r2.status === 200, `ongoing → ${r.status} ${JSON.stringify(codes(r))} · unconfirmed → ${r2.status}`)
})

check('G1.1', 'patch_scheduled needs a time; an urgent item needs a what-to-do note; [n] must cite a listed source; no private IPs', async () => {
  const f = await setup()
  const d1 = await draftIssue('eval-urgent', exlItem(f.exl, { patchScheduledAt: null, whatToDoBn: null, whatToDoEn: null, summaryBn: 'সার্ভার 192.168.10.5-এ দেখা গেছে [2]।' }))
  const r = await admin('POST', `/api/truth/issues/${d1.id}/publish`)
  const c = codes(r)
  const status = await admin('POST', `/api/truth/items/${d1.itemId}/status`, { status: 'active_attack' })
  const back = await admin('POST', `/api/truth/items/${d1.itemId}/status`, { status: 'patch_scheduled' })
  return ok(r.status === 400 && ['patchTime', 'urgentNote', 'citation', 'privateIp'].every((x) => c.includes(x)) && status.status === 200 && back.status === 400,
    `${r.status} ${JSON.stringify([...new Set(c)])} · back to patch_scheduled without a time → ${back.status}`)
})

check('G1.1', 'only a holder of action:truth.publish publishes; a General account gets 403 on /api/truth', async () => {
  await setup()
  const general = await login('eval-truth-general', 'start-' + crypto.randomBytes(5).toString('hex'))
  const list = await as(general, 'GET', '/api/truth/issues')
  const publish = await as(general, 'POST', `/api/truth/issues/${ctx.fx.issueId}/publish`)
  const anon = await call(ctx.api.url, 'GET', '/api/truth/issues')
  return ok(list.status === 403 && publish.status === 403 && anon.status === 401, `general list ${list.status} · general publish ${publish.status} · no session ${anon.status}`)
})

// ═══════════════════════════════ G1.2 ═══════════════════════════════

async function everyPublicBody() {
  const archive = await pub('/public/truth/issues')
  const slugs = (archive.json?.issues ?? []).map((i) => i.slug)
  const bodies = [await pub('/public/truth/current'), archive, await pub('/public/truth/site-content'), await pub('/public/truth/time')]
  for (const s of slugs) bodies.push(await pub(`/public/truth/issues/${s}`))
  return { bodies, slugs, text: bodies.map((b) => b.text).join('\n') }
}

check('G1.2', 'a draft issue is never served: not by slug, not as current, not in the archive', async () => {
  const f = await setup()
  const d = await draftIssue('eval-draft-only', exlItem(f.exl, { headlineBn: 'DRAFT-MARKER-7f3a খসড়া', headlineEn: 'DRAFT-MARKER-7f3a draft' }))
  const bySlug = await pub('/public/truth/issues/eval-draft-only')
  const viaRewrite = await pub('/truth-api/issues/eval-draft-only')
  const all = await everyPublicBody()
  return ok(bySlug.status === 404 && viaRewrite.status === 404 && !all.text.includes('DRAFT-MARKER') && !all.slugs.includes('eval-draft-only'),
    `slug ${bySlug.status} · /truth-api ${viaRewrite.status} · marker in public: ${all.text.includes('DRAFT-MARKER')} · archive ${JSON.stringify(all.slugs)}`)
})

check('G1.2', 'an edit to a published item is not public until it is published again', async () => {
  const f = await setup()
  const item = exlItem(f.exl, { headlineEn: 'UNPUBLISHED-EDIT-9b1c headline' })
  const put = await admin('PUT', `/api/truth/items/${f.exlItem}`, item)
  const current = await pub('/public/truth/current')
  const leaked = current.text.includes('UNPUBLISHED-EDIT')
  const needsCorrection = await admin('POST', `/api/truth/issues/${f.issueId}/publish`)
  await admin('PUT', `/api/truth/items/${f.exlItem}`, exlItem(f.exl))   // back as published: nothing to correct
  const clean = await admin('GET', `/api/truth/issues/${f.issueId}/check`)
  return ok(put.status === 200 && !leaked && needsCorrection.status === 400 && codes(needsCorrection).includes('correctionNeeded') && clean.json?.ok,
    `save ${put.status} · in public: ${leaked} · republish with changed words → ${needsCorrection.status} ${JSON.stringify(codes(needsCorrection))} · words restored → check ok=${clean.json?.ok}`)
})

check('G1.2', 'a withdrawn issue stops being served; publishing it again brings it back', async () => {
  const f = await setup()
  const w = await admin('POST', `/api/truth/issues/${f.issueId}/withdraw`)
  const gone = await pub(`/public/truth/issues/${ISSUE.slug}`)
  const cur = await pub('/public/truth/current')
  const again = await admin('POST', `/api/truth/issues/${f.issueId}/publish`)
  const back = await pub(`/public/truth/issues/${ISSUE.slug}`)
  return ok(w.status === 200 && gone.status === 404 && cur.json?.issue?.slug !== ISSUE.slug && again.status === 200 && back.status === 200,
    `withdraw ${w.status} · served ${gone.status} · current ${cur.json?.issue?.slug ?? cur.status} · republish ${again.status} · served ${back.status}`)
})

// ═══════════════════════════════ G1.3 ═══════════════════════════════
// The allow-list, from §2 and §3: what a visitor's page needs, nothing else.

const ALLOWED = {
  snapshot: ['schema', 'issue', 'urgent', 'siteContentVersion', 'items'],
  issue: ['slug', 'titleBn', 'titleEn', 'subtitleBn', 'subtitleEn', 'weekStart', 'weekEnd', 'firstPublishedAt', 'publishedAt'],
  item: ['id', 'order', 'tech', 'severity', 'status', 'statusSince', 'patchScheduledAt', 'headlineBn', 'headlineEn', 'summaryBn', 'summaryEn',
    'impactBn', 'impactEn', 'whatToDoBn', 'whatToDoEn', 'tags', 'author', 'advisories', 'timeline', 'statusEvents', 'sources', 'corrections'],
  tech: ['slug', 'name', 'category', 'monogram', 'hue', 'homepage'],
  advisory: ['cveId', 'cvss', 'titleBn', 'titleEn', 'affectedRange', 'fixedVersions', 'exploitedInWild', 'kevListed'],
  timeline: ['at', 'level', 'textBn', 'textEn'],
  statusEvent: ['at', 'from', 'to', 'textBn', 'textEn'],
  source: ['n', 'url', 'publisher', 'author', 'title', 'publishedOn', 'kind'],
  correction: ['at', 'textBn', 'textEn'],
  archive: ['issues'],
  archiveIssue: ['slug', 'titleBn', 'titleEn', 'weekStart', 'weekEnd', 'publishedAt', 'itemCount', 'urgent'],
  siteContent: ['version', 'schemaVersion', 'text', 'textVersion', 'memes', 'memesVersion', 'glossary', 'glossaryVersion'],
  time: ['serverNow'],
}

function extraKeys(obj, allowed, where, out) {
  for (const k of Object.keys(obj ?? {})) if (!allowed.includes(k)) out.push(`${where}.${k}`)
}

function snapshotExtras(s) {
  const out = []
  extraKeys(s, ALLOWED.snapshot, 'snapshot', out)
  extraKeys(s.issue, ALLOWED.issue, 'issue', out)
  for (const it of s.items ?? []) {
    extraKeys(it, ALLOWED.item, 'item', out)
    extraKeys(it.tech, ALLOWED.tech, 'item.tech', out)
    for (const [list, allow] of [['advisories', ALLOWED.advisory], ['timeline', ALLOWED.timeline], ['statusEvents', ALLOWED.statusEvent], ['sources', ALLOWED.source], ['corrections', ALLOWED.correction]])
      for (const x of it[list] ?? []) extraKeys(x, allow, `item.${list}[]`, out)
  }
  return out
}

check('G1.3', 'every public JSON passes the allow-list: no key the spec does not name', async () => {
  await setup()
  // A correction and a status change with text, so every list has a member to check.
  await admin('POST', `/api/truth/items/${ctx.fx.demoItem}/corrections`, { textBn: 'অঞ্চলের নাম ঠিক করা হয়েছে।', textEn: null })
  await admin('POST', `/api/truth/items/${ctx.fx.demoItem}/verify`, { token: '35', note: 'HAND-NOTE-MARKER checked on the status page' })
  const pubd = await admin('POST', `/api/truth/issues/${ctx.fx.issueId}/publish`)
  const all = await everyPublicBody()
  const [current, archive, site, time, ...issues] = all.bodies
  const out = [...snapshotExtras(current.json), ...issues.flatMap((b) => snapshotExtras(b.json))]
  extraKeys(archive.json, ALLOWED.archive, 'archive', out)
  for (const i of archive.json.issues) extraKeys(i, ALLOWED.archiveIssue, 'archive.issues[]', out)
  extraKeys(site.json, ALLOWED.siteContent, 'site-content', out)
  extraKeys(time.json, ALLOWED.time, 'time', out)
  // Every kind of list has a member somewhere, so none of them passes only by being empty.
  const lists = ['advisories', 'timeline', 'statusEvents', 'sources', 'corrections'].map((k) => [k, current.json.items.reduce((n, i) => n + i[k].length, 0)])
  const empty = lists.filter(([, n]) => n === 0).map(([k]) => k)
  return ok(pubd.status === 200 && out.length === 0 && empty.length === 0 && issues.length >= 1,
    out.length ? `extra: ${out.join(', ')}` : `clean over ${all.bodies.length} bodies · members checked ${JSON.stringify(Object.fromEntries(lists))}${empty.length ? ` · EMPTY ${empty}` : ''}`)
})

check('G1.3', 'no email, note, acknowledgement, fetched source text, user id or staff username in any public body', async () => {
  await setup()
  const { text } = await everyPublicBody()
  const found = []
  if (/[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}/.test(text)) found.push('an email address')
  for (const [name, re] of [['"note"', /"note[A-Za-z]*"\s*:/i], ['acknowledg', /acknowledg/i], ['fetchedText', /fetched/i], ['handVerified', /handVerified|HAND-NOTE-MARKER/],
    ['the fetched-only sentence', /FETCHED-ONLY-MARKER/], ['user ids', /"(by|byUserId|publishedBy|editedBy|createdBy)[A-Za-z]*"\s*:/i], ['the admin username', new RegExp(ctx.admin.username.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'), 'i')]])
    if (re.test(text)) found.push(name)
  return ok(found.length === 0, found.length ? `found: ${found.join(', ')}` : 'none')
})

// ═══════════════════════════════ G1.4 ═══════════════════════════════

const BN_DIGITS = (s) => String(s).replace(/\d/g, (d) => '০১২৩৪৫৬৭৮৯'[d])
/** Hour and minute in Asia/Dhaka (UTC+6, no daylight saving since 2009) — computed here, not by the app. */
function dhaka(iso) {
  const d = new Date(new Date(iso).getTime() + 6 * 3600_000)
  return { h: d.getUTCHours(), m: d.getUTCMinutes() }
}

check('G1.4', 'a status change is logged (from, to, time) and is in the next snapshot', async () => {
  const f = await setup()
  const r = await admin('POST', `/api/truth/items/${f.demoItem}/status`, { status: 'contained', textBn: 'বিভ্রাট নিয়ন্ত্রণে এসেছে।', textEn: 'The outage is under control.' })
  const rows = await sql('SELECT FromStatus, ToStatus, AtUtc FROM TrStatusEvents WHERE ItemId = ? ORDER BY Id', f.demoItem)
  const p = await admin('POST', `/api/truth/issues/${f.issueId}/publish`)
  const snap = (await pub('/public/truth/current')).json
  const ev = snap.items.find((i) => i.id === f.demoItem)?.statusEvents ?? []
  const last = ev.at(-1)
  ctx.fx.statusAt = last?.at
  return ok(r.status === 200 && rows.length === 2 && rows[0].FromStatus === null && rows[1].FromStatus === 'ongoing' && rows[1].ToStatus === 'contained'
    && p.status === 200 && last?.from === 'ongoing' && last?.to === 'contained' && /Z$/.test(last?.at ?? ''),
  `status ${r.status} · rows ${JSON.stringify(rows.map((x) => `${x.FromStatus}→${x.ToStatus}`))} · snapshot ${last?.from}→${last?.to} at ${last?.at}`)
})

check('G1.4', 'the page shows status times in Asia/Dhaka time to a visitor whose browser is in New York', async () => {
  await setup()
  if (!ctx.fx.statusAt) await checks.find((c) => c.name.startsWith('a status change is logged')).fn()
  const page = await ctx.browser.page()
  await page.cdp('Emulation.setTimezoneOverride', { timezoneId: 'America/New_York' })
  await page.goto(`${ctx.vercel.url}/truth?lang=bn`, 1500)
  await page.waitFor('[data-testid="status-event"]')
  const shown = await page.eval(`[...document.querySelectorAll('[data-testid="status-event"] time')].map((t) => ({ dt: t.getAttribute('datetime'), text: t.textContent.trim() }))`)
  const patchBn = await page.eval(`document.querySelector('[data-testid="patch-time"]')?.textContent.trim()`)
  await page.goto(`${ctx.vercel.url}/truth?lang=en`, 1500)
  const patchEn = await page.eval(`document.querySelector('[data-testid="patch-time"]')?.textContent.trim()`)
  const nyHour = new Intl.DateTimeFormat('en-US', { timeZone: 'America/New_York', hour: 'numeric', hour12: false }).format(new Date(ctx.fx.statusAt))
  const ev = shown.find((s) => s.dt === ctx.fx.statusAt)
  const { h } = dhaka(ctx.fx.statusAt)
  const h12 = h % 12 === 0 ? 12 : h % 12
  const evOk = ev && ev.text.includes(`${BN_DIGITS(h12)}টা`)
  // Rule 6's own example: 14:00 UTC is 8 PM in Dhaka.
  return ok(evOk && patchBn?.includes('রাত ৮টা (14:00 UTC)') && patchEn?.includes('8 PM (14:00 UTC)'),
    `status event ${ctx.fx.statusAt} (Dhaka ${h}:xx, New York ${nyHour}:xx) shown "${ev?.text}" · patch bn "${patchBn}" · en "${patchEn}"`)
})

// ═══════════════════════════════ G1.5 ═══════════════════════════════

/** The modules and files of every chunk the truth entry can load, from the build's own report (vite.config.js). */
function truthBuild() {
  const report = JSON.parse(fs.readFileSync(path.join(WEB, 'node_modules', '.truth-build', 'modules.json'), 'utf8'))
  return report
}

check('G1.5', 'the /truth bundle holds no admin, auth or session code', async () => {
  const report = truthBuild()
  const own = report.modules.filter((m) => m.startsWith('src/'))
  const allowed = (m) => m.startsWith('src/truth/') || m === 'src/composables/useTheme.js'
  const outside = own.filter((m) => !allowed(m))
  const pkgs = [...new Set(report.modules.filter((m) => m.startsWith('node_modules/')).map((m) => m.split('/').slice(1, m.split('/')[1].startsWith('@') ? 3 : 2).join('/')))]
  const js = report.files.filter((f) => f.endsWith('.js')).map((f) => fs.readFileSync(path.join(DIST, f), 'utf8')).join('\n')
  const words = ['rabbitic.token', '/api/auth', '/api/me', 'Bearer ', 'LoginGate', 'session.service'].filter((w) => js.includes(w))
  return ok(outside.length === 0 && words.length === 0 && own.length > 0,
    `${own.length} own modules, outside src/truth: ${JSON.stringify(outside)} · packages ${JSON.stringify(pkgs)} · admin words in JS: ${JSON.stringify(words)}`)
})

check('G1.5', 'the /truth bundle is at most 90 KB of gzipped JS', async () => {
  const report = truthBuild()
  const sizes = report.files.filter((f) => f.endsWith('.js')).map((f) => [f, zlib.gzipSync(fs.readFileSync(path.join(DIST, f)), { level: 9 }).length])
  const total = sizes.reduce((a, [, n]) => a + n, 0)
  return ok(total <= 90 * 1024 && sizes.length > 0, `${(total / 1024).toFixed(1)} KB gzip over ${sizes.length} file(s): ${sizes.map(([f, n]) => `${path.basename(f)} ${(n / 1024).toFixed(1)}`).join(', ')}`)
})

// ═══════════════════════════════ G1.6 ═══════════════════════════════

check('G1.6', 'through the Vercel rewrites: /truth and /truth/<slug> load the shell, /truth-api/current returns the snapshot', async () => {
  await setup()
  const shell = await viaVercel('/truth')
  const deep = await viaVercel(`/truth/${ISSUE.slug}?lang=en`)
  const api = await viaVercel('/truth-api/current')
  const csp = shell.headers.get('content-security-policy') ?? ''
  const viaTunnelOnly = ctx.vercel.log.filter((l) => l.path.startsWith('/truth-api')).every((l) => l.upstream.startsWith('/public/truth/'))
  return ok(shell.status === 200 && /src="\/assets\/truth-[^"]+\.js"/.test(shell.text) && deep.status === 200 && deep.text === shell.text
    && api.status === 200 && api.json?.issue?.slug === ISSUE.slug && /script-src 'self'/.test(csp) && shell.headers.get('strict-transport-security') && viaTunnelOnly,
  `/truth ${shell.status} · deep link ${deep.status} (same shell: ${deep.text === shell.text}) · /truth-api/current ${api.status} ${api.json?.issue?.slug} · CSP ${csp ? 'set' : 'missing'} · HSTS ${shell.headers.get('strict-transport-security') ? 'set' : 'missing'} · upstream paths ${JSON.stringify([...new Set(ctx.vercel.log.map((l) => l.upstream.split('?')[0]))])}`)
})

const REFUSED = [
  ['GET', '/api/models'], ['GET', '/api/truth/issues'], ['POST', '/api/auth/login'], ['GET', '/api/me/tabs'], ['GET', '/'], ['GET', '/index.html'],
  ['GET', '/swagger/index.html'], ['GET', '/swagger/v1/swagger.json'], ['GET', '/truth'], ['GET', '/truth-api/current'], ['GET', '/public/server/status'],
  ['GET', '/public/truthx'], ['GET', '/public/truth/../../api/models'], ['GET', '/public/truth/%2e%2e/%2e%2e/api/models'], ['GET', '/oddity/api/tags'],
  ['DELETE', '/public/truth/current'], ['PUT', '/public/truth/current'], ['GET', '/assets/'],
]

check('G1.6', 'through the tunnel, every path outside /public/truth/* is refused (and /public/truth/current is served)', async () => {
  await setup()
  const results = []
  for (const [m, p] of REFUSED) results.push([m, p, (await viaTunnel(m, p)).status])
  const served = await viaTunnel('GET', '/public/truth/current')
  const lan = await call(ctx.api.url, 'GET', '/api/models')
  const leaked = results.filter(([, , s]) => s !== 404)
  return ok(leaked.length === 0 && served.status === 200 && lan.status !== 404,
    `${results.length} refused paths: ${leaked.length ? `LEAKED ${JSON.stringify(leaked)}` : 'all 404'} · /public/truth/current ${served.status} · the same /api/models on the LAN port ${lan.status}`)
})

check('G1.6', 'vercel.json sends nothing but /public/truth/* to the tunnel, and holds no secret', async () => {
  const v = JSON.parse(fs.readFileSync(path.join(WEB, 'vercel.json'), 'utf8'))
  const external = v.rewrites.filter((r) => /^https?:\/\//.test(r.destination))
  const bad = external.filter((r) => !new URL(r.destination.replace(/:path\*/g, 'x')).pathname.startsWith('/public/truth/'))
  const raw = fs.readFileSync(path.join(WEB, 'vercel.json'), 'utf8')
  // A secret-named field (not Vercel's own "key" of a header), an Authorization header, or user:password@ in a URL.
  const secretish = /"(api[_-]?key|token|secret|password|passwd|credentials?)"\s*:/i.test(raw)
    || /"key"\s*:\s*"(authorization|x-api-key|cookie)"/i.test(raw) || /\/\/[^/\s"]+:[^@\s"]+@/.test(raw)
  return ok(external.length >= 2 && bad.length === 0 && !secretish, `${external.length} external rewrite(s), outside /public/truth: ${JSON.stringify(bad.map((r) => r.destination))} · secret-shaped: ${secretish}`)
})

check('G1.6', 'the rate limit holds at 120 GETs a minute per visitor IP, by the tunnel\'s forwarded header', async () => {
  // A visitor of its own (X-Forwarded-For), so the other checks' requests do not count.
  const ip = '192.0.2.' + (10 + Math.floor(Math.random() * 200))
  const hit = (forwarded) => new Promise((resolve, reject) => {
    const s = net.connect({ host: '127.0.0.1', port: ctx.tunnelPort }, () => s.write(`GET /public/truth/time HTTP/1.1\r\nHost: t\r\nX-Forwarded-For: ${forwarded}\r\nConnection: close\r\n\r\n`))
    let d = ''; s.on('data', (c) => { d += c }); s.on('end', () => resolve(Number(d.split(' ')[1]))); s.on('error', reject)
  })
  const statuses = []
  for (let i = 0; i < 121; i++) statuses.push(await hit(ip))
  const other = await hit('192.0.2.250')
  const firstRefused = statuses.indexOf(429) + 1
  return ok(statuses.slice(0, 120).every((s) => s === 200) && statuses[120] === 429 && other === 200,
    `first refusal at request ${firstRefused || 'never'} · another IP meanwhile ${other}`)
})

// ═══════════════════════════════ G1.7 ═══════════════════════════════

check('G1.7', 'backend down: a returning visitor sees the stored snapshot under the grey bar', async () => {
  await setup()
  const page = await ctx.browser.page()
  await page.goto(`${ctx.vercel.url}/truth?lang=bn`, 1500)
  await page.waitFor('[data-testid="status-board"]')
  ctx.vercel.setDown(true)
  // The browser's own HTTP cache would answer for up to max-age (300 s) and the page would
  // simply show the copy it has; turn it off, as if that time had passed, so the stored copy is what is left.
  await page.cdp('Network.enable', {})
  await page.cdp('Network.setCacheDisabled', { cacheDisabled: true })
  try {
    await page.reload()
    await page.waitFor('[data-testid="offline-bar"]')
    const bar = await page.eval(`document.querySelector('[data-testid="offline-bar"]').textContent.trim()`)
    const headline = await page.eval(`document.querySelector('[data-testid="item-headline"]')?.textContent.trim()`)
    if (ctx.args.shots) await page.shot(path.join(ctx.args.shots, 'g1.7-offline-bn-360.png'))
    return ok(bar.startsWith('সর্বশেষ সংরক্ষিত তথ্য ·') && bar.includes('সার্ভারে এখন পৌঁছানো যাচ্ছে না') && !!headline,
      `bar "${bar}" · first headline "${headline}"`)
  } finally { ctx.vercel.setDown(false) }
})

check('G1.7', 'backend down on a first visit: the friendly error with the RSS link, in the built-in text', async () => {
  await setup()
  const page = await ctx.browser.page()
  ctx.vercel.setDown(true)
  try {
    await page.goto(`${ctx.vercel.url}/truth?lang=en`, 2000)
    await page.waitFor('[data-testid="load-error"]')
    const text = await page.eval(`document.querySelector('[data-testid="load-error"]').textContent`)
    const rss = await page.eval(`document.querySelector('[data-testid="load-error"] a[href*="feed.xml"]')?.getAttribute('href')`)
    if (ctx.args.shots) { await page.viewport(360, 800); await sleep(300); await page.shot(path.join(ctx.args.shots, 'backend-down-first-visit-360-dark-en.png'), { full: true }) }
    return ok(text.includes("We can't load the bulletin right now") && rss === '/truth-api/feed.xml?lang=en', `"${text.trim().slice(0, 90)}…" · RSS ${rss}`)
  } finally { ctx.vercel.setDown(false) }
})

// ═══════════════════════════════ G1.8 ═══════════════════════════════

function distHash() {
  const h = crypto.createHash('sha256')
  const walk = (dir) => { for (const e of fs.readdirSync(dir, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name))) {
    const f = path.join(dir, e.name); if (e.isDirectory()) walk(f); else { h.update(e.name); h.update(fs.readFileSync(f)) } } }
  walk(DIST)
  return h.digest('hex').slice(0, 16)
}

check('G1.8', 'a label edited and published in the admin changes on the live page, with no redeploy', async () => {
  await setup()
  const before = distHash()
  const state = (await admin('GET', '/api/truth/content/site')).json
  const text = structuredClone(state.published?.text ?? state.default)
  text.bn.nav.board = 'অবস্থা-যাচাই'
  text.en.nav.board = 'Status-check'
  const r = await admin('POST', '/api/truth/content/site/publish', { text, note: 'eval: G1.8' })
  const page = await ctx.browser.page()
  await page.goto(`${ctx.vercel.url}/truth?lang=bn`, 1500)
  await page.waitFor('[data-testid="chip-nav"]')
  const bn = await page.eval(`document.querySelector('[data-testid="chip-nav"]').textContent`)
  await page.goto(`${ctx.vercel.url}/truth?lang=en`, 1500)
  const en = await page.eval(`document.querySelector('[data-testid="chip-nav"]').textContent`)
  ctx.fx.siteVersion = r.json?.version
  return ok(r.status === 200 && bn.includes('অবস্থা-যাচাই') && en.includes('Status-check') && distHash() === before,
    `publish ${r.status} v${r.json?.version} · bn chip nav has it: ${bn.includes('অবস্থা-যাচাই')} · en: ${en.includes('Status-check')} · dist unchanged: ${distHash() === before}`)
})

check('G1.8', 'a missing key, an unknown key, a wrong placeholder, HTML or an http:// link is refused', async () => {
  const state = (await admin('GET', '/api/truth/content/site')).json
  const base = state.published?.text ?? state.default
  const cases = {
    'missing key': (t) => { delete t.bn.nav.board },
    'unknown key': (t) => { t.en.nav.extra = 'x' },
    'wrong placeholder': (t) => { t.bn.hero.updated = 'সর্বশেষ আপডেট: {when}' },
    'stray brace': (t) => { t.en.hero.updated = 'Last updated: {time' },
    'HTML': (t) => { t.en.footer.note = 'Public <b>only</b>' },
    'entity': (t) => { t.en.footer.note = 'Public &amp; sourced' },
    'http link': (t) => { t.en.footer.note = 'See http://example.com' },
    'javascript:': (t) => { t.en.footer.note = 'javascript:alert(1)' },
    'empty Bangla': (t) => { t.bn.footer.rss = ' ' },
  }
  const out = []
  for (const [name, mutate] of Object.entries(cases)) {
    const t = structuredClone(base)
    mutate(t)
    const r = await admin('POST', '/api/truth/content/site/publish', { text: t })
    out.push(`${name}: ${r.status}${r.status === 400 ? ` (${r.json?.problems?.[0]?.field} ${r.json?.problems?.[0]?.message})` : ''}`)
  }
  const emptyEn = structuredClone(base); emptyEn.en.footer.rss = ''
  const enOk = await admin('POST', '/api/truth/content/site/validate', { text: emptyEn })
  const after = (await admin('GET', '/api/truth/content/site')).json
  return ok(out.every((x) => / 400 \(/.test(x)) && enOk.json?.errors?.length === 0 && after.history.length === state.history.length,
    `${out.join(' · ')} · an empty English value is allowed: ${enOk.json?.errors?.length === 0} · versions unchanged: ${after.history.length === state.history.length}`)
})

check('G1.8', 'rollback publishes the old version again and the page follows', async () => {
  await setup()
  if (!ctx.fx.siteVersion) await checks.find((c) => c.id === 'G1.8').fn()
  const state = (await admin('GET', '/api/truth/content/site')).json
  // Version 1 is the eval's own edit when no earlier version exists: roll back to the default by publishing it as a version first.
  let target = state.history.find((h) => h.version < ctx.fx.siteVersion)?.version
  if (!target) {
    await admin('POST', '/api/truth/content/site/publish', { text: state.default, note: 'eval: the default as a version' })
    await admin('POST', `/api/truth/content/site/rollback/${ctx.fx.siteVersion}`)
    target = ctx.fx.siteVersion + 1
  }
  const r = await admin('POST', `/api/truth/content/site/rollback/${target}`)
  const page = await ctx.browser.page()
  await page.goto(`${ctx.vercel.url}/truth?lang=en`, 1500)
  const en = await page.eval(`document.querySelector('[data-testid="chip-nav"]').textContent`)
  const hist = (await admin('GET', '/api/truth/content/site')).json.history
  return ok(r.status === 200 && !en.includes('Status-check') && en.includes('Status') && hist[0].note === `rollback to v${target}`,
    `rollback to v${target} → ${r.status} v${r.json?.version} · page "${en.trim().slice(0, 60)}" · latest note "${hist[0].note}"`)
})

check('G1.8', 'in the admin tab: a label typed into the site-text editor and published shows on the public page', async () => {
  await setup()
  const page = await ctx.browser.page()
  await page.viewport(360, 800)
  // The admin app is the backend's own SPA (wwwroot), on the LAN — not behind the Vercel stand-in.
  await page.goto(`${ctx.api.url}/settings`, 300)
  await page.eval(`localStorage.setItem('rabbitic.token', ${JSON.stringify(ctx.admin.token)}); localStorage.setItem('rabbitic.username', ${JSON.stringify(ctx.admin.username)}); localStorage.setItem('rabbitic-theme', 'dark'); 0`)
  await page.goto(`${ctx.api.url}/ministry-of-truth?section=site-text`, 2000)
  await page.waitFor('[data-site-key="nav.log"] textarea[lang="bn"]')
  await page.type('[data-site-key="nav.log"] textarea[lang="bn"]', 'লগ-অ্যাডমিন')
  await sleep(500)
  if (ctx.args.shots) await page.shot(path.join(ctx.args.shots, 'admin-site-text-360-dark.png'))
  // Refused in the editor before the server is asked: a placeholder this key does not take.
  await page.type('[data-site-key="nav.board"] textarea[lang="en"]', 'Status {time}')
  await sleep(300)
  const blocked = await page.eval(`document.querySelector('[data-truth-publish-text]').disabled`)
  const shown = await page.eval(`document.querySelector('[data-site-key="nav.board"]').textContent.includes('unknown placeholder {time}')`)
  await page.type('[data-site-key="nav.board"] textarea[lang="en"]', 'Status')
  await sleep(300)
  await page.click('[data-truth-publish-text]')
  await sleep(1500)
  const live = await page.eval(`document.querySelector('[data-truth-site-text] h3')?.textContent`)
  const pub = await ctx.browser.page()
  await pub.goto(`${ctx.vercel.url}/truth?lang=bn`, 1500)
  await pub.waitFor('[data-testid="chip-nav"]')
  const chips = await pub.eval(`document.querySelector('[data-testid="chip-nav"]').textContent`)
  return ok(blocked && shown && chips.includes('লগ-অ্যাডমিন'),
    `wrong placeholder blocks Publish: ${blocked} (shown: ${shown}) · after Publish: "${live?.trim()}" · public chip nav has the new label: ${chips.includes('লগ-অ্যাডমিন')}`)
})

// ═══════════════════════════════ G1.9 ═══════════════════════════════

/** Visible text and labelled attributes in the wrong script for the language — what a visitor would see untranslated. */
const WRONG_SCRIPT = (lang) => `(() => {
  const bad = []
  const visible = (el) => el && el.getClientRects().length > 0 && getComputedStyle(el).visibility !== 'hidden'
  const wrong = (s, el) => ${lang === 'en'}
    ? /[\\u0980-\\u09FF]/.test(s) && !el.closest('[lang="bn"]')
    : /[A-Za-z]/.test(s.replace(/\\bUTC\\b/g, '')) && !el.closest('[lang="en"],[translate="no"],[data-content]')
  const w = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT)
  for (let n = w.nextNode(); n; n = w.nextNode()) {
    const t = n.textContent.trim(), el = n.parentElement
    if (t && visible(el) && !el.closest('script,style,noscript') && wrong(t, el)) bad.push(t.slice(0, 60))
  }
  for (const el of document.querySelectorAll('[aria-label],[title],[placeholder],[alt]'))
    for (const a of ['aria-label', 'title', 'placeholder', 'alt']) { const v = el.getAttribute(a); if (v && wrong(v, el)) bad.push(a + '=' + v.slice(0, 60)) }
  return bad
})()`

check('G1.9', 'the switch changes every visible string, without a reload, and the URL keeps the language', async () => {
  await setup()
  const page = await ctx.browser.page()
  await page.goto(`${ctx.vercel.url}/truth?lang=en`, 1500)
  await page.waitFor('[data-testid="status-board"]')
  const enBad = await page.eval(WRONG_SCRIPT('en'))
  const htmlLangEn = await page.eval('document.documentElement.lang')
  await page.eval('window.__noReload = 42; 0')
  await page.click('[data-testid="lang-bn"]')
  await sleep(500)
  const bnBad = await page.eval(WRONG_SCRIPT('bn'))
  const kept = await page.eval('window.__noReload')
  const url = await page.eval('location.search')
  const htmlLangBn = await page.eval('document.documentElement.lang')
  const title = await page.eval('document.title')
  // Staff-written text follows the switch field by field: each headline is the snapshot's Bangla one now.
  const snap = (await pub('/public/truth/current')).json
  const heads = await page.eval(`[...document.querySelectorAll('[data-item-id]')].map((c) => [Number(c.dataset.itemId), c.querySelector('[data-testid="item-headline"]').textContent.trim()])`)
  const wrongHead = heads.filter(([id, h]) => h !== snap.items.find((i) => i.id === id)?.headlineBn)
  return ok(enBad.length === 0 && bnBad.length === 0 && wrongHead.length === 0 && heads.length === snap.items.length && kept === 42 && url.includes('lang=bn') && htmlLangEn === 'en' && htmlLangBn === 'bn' && /সত্য মন্ত্রণালয়/.test(title),
    `en wrong-script: ${JSON.stringify(enBad.slice(0, 5))} · bn headlines not the Bangla field: ${JSON.stringify(wrongHead)} · bn wrong-script: ${JSON.stringify(bnBad.slice(0, 5))} · no reload: ${kept === 42} · url ${url} · <html lang> ${htmlLangEn}→${htmlLangBn} · title "${title}"`)
})

check('G1.9', '?lang=en survives sharing; the default follows the browser language and falls back to Bangla', async () => {
  await setup()
  const page = await ctx.browser.page()
  await page.goto(`${ctx.vercel.url}/truth/${ISSUE.slug}?lang=en`, 1500)
  const shared = await page.eval('document.documentElement.lang')
  const shareHref = await page.eval(`document.querySelector('[data-testid="share"]')?.getAttribute('data-url')`)
  const fresh = async (locale) => {
    const p = await ctx.browser.page()
    await p.cdp('Emulation.setLocaleOverride', { locale })
    await p.cdp('Network.enable', {})
    await p.cdp('Network.setUserAgentOverride', { userAgent: 'Mozilla/5.0 truth-eval', acceptLanguage: locale, platform: 'Win32' })
    await p.goto(`${ctx.vercel.url}/truth`, 1500)
    await p.waitFor('[data-testid="status-board"]')
    return p.eval('document.documentElement.lang')
  }
  const fr = await fresh('fr-FR'), bnBD = await fresh('bn-BD'), enGB = await fresh('en-GB')
  return ok(shared === 'en' && shareHref?.includes('lang=en') && fr === 'bn' && bnBD === 'bn' && enGB === 'en',
    `shared link → ${shared} · share button carries ${shareHref} · browser fr-FR → ${fr} · bn-BD → ${bnBD} · en-GB → ${enGB}`)
})

check('G1.9', 'missing English falls back to Bangla with the note, marked lang="bn"', async () => {
  await setup()
  const page = await ctx.browser.page()
  await page.goto(`${ctx.vercel.url}/truth?lang=en`, 1500)
  await page.waitFor(`[data-item-id="${ctx.fx.demoItem}"]`)
  const r = await page.eval(`(() => { const card = document.querySelector('[data-item-id="${ctx.fx.demoItem}"]');
    const h = card.querySelector('[data-testid="item-headline"]');
    return { headline: h.textContent.trim(), lang: h.closest('[lang]')?.getAttribute('lang'), note: card.querySelector('[data-testid="en-missing"]')?.textContent.trim() } })()`)
  return ok(r.headline.includes('DemoHost-এর') && r.lang === 'bn' && r.note === 'English version not available yet.', JSON.stringify(r))
})

/** Every t('key') in the public app's source, and every t(`prefix.${…}`) prefix. */
function keysInCode() {
  const files = []
  const walk = (d) => { for (const e of fs.readdirSync(d, { withFileTypes: true })) { const f = path.join(d, e.name); if (e.isDirectory()) walk(f); else if (/\.(vue|js)$/.test(e.name)) files.push(f) } }
  walk(path.join(WEB, 'src', 'truth'))
  const keys = new Set(), prefixes = new Set()
  for (const f of files) {
    const s = fs.readFileSync(f, 'utf8')
    for (const m of s.matchAll(/\bt[e]?\(\s*['"]([a-zA-Z][\w.]*)['"]/g)) keys.add(m[1])
    for (const m of s.matchAll(/\bt[e]?\(\s*`([a-zA-Z][\w.]*)\.\$\{/g)) prefixes.add(m[1])
    // Keys handed to components that look them up: <SectionTitle plain="…" nick="…">, <Msg k="…">.
    for (const m of s.matchAll(/\s(?:plain|nick|k)="([a-zA-Z][\w.]*)"/g)) keys.add(m[1])
  }
  return { files, keys: [...keys], prefixes: [...prefixes] }
}

check('G1.9', 'CI key check: every key used in code is in bn and en; the schema matches the default', async () => {
  const def = JSON.parse(fs.readFileSync(path.join(WEB, 'src/truth/content/site.default.json'), 'utf8'))
  const schema = JSON.parse(fs.readFileSync(path.join(WEB, 'src/truth/content/site.schema.json'), 'utf8'))
  const get = (o, k) => k.split('.').reduce((n, p) => (n && typeof n === 'object' ? n[p] : undefined), o)
  const { keys, prefixes } = keysInCode()
  const missing = []
  for (const k of keys) for (const l of ['bn', 'en']) if (typeof get(def[l], k) !== 'string') missing.push(`${l}.${k}`)
  for (const p of prefixes) for (const l of ['bn', 'en']) if (typeof get(def[l], p) !== 'object') missing.push(`${l}.${p}.*`)
  // The schema and the default: the same keys both ways, and every placeholder the default uses is one the schema allows.
  const drift = []
  const walk = (node, bn, en, at) => {
    if (node.type === 'object') {
      for (const k of Object.keys(node.properties)) walk(node.properties[k], bn?.[k], en?.[k], at ? `${at}.${k}` : k)
      for (const k of new Set([...Object.keys(bn ?? {}), ...Object.keys(en ?? {})])) if (!(k in node.properties)) drift.push(`${at}.${k} not in schema`)
      return
    }
    for (const [l, v] of [['bn', bn], ['en', en]]) {
      if (typeof v !== 'string') { drift.push(`${l}.${at} missing`); continue }
      for (const m of v.matchAll(/\{([a-zA-Z]\w*)\}/g)) if (!node['x-placeholders'].includes(m[1])) drift.push(`${l}.${at} uses {${m[1]}}`)
    }
  }
  walk(schema.$defs.lang, def.bn, def.en, '')
  // Status pills and source kinds are read by prefix; each value the data can hold has its label.
  const labels = [...['active_attack', 'breach_confirmed', 'patch_scheduled', 'ongoing', 'contained', 'unconfirmed', 'patched', 'blocked', 'resolved'].map((s) => `status.${s}`),
    ...['official', 'vendor', 'research', 'community', 'news', 'claim'].map((s) => `refs.${s}`), ...['critical', 'high', 'medium', 'low', 'info'].map((s) => `severity.${s}`),
    ...['ERR', 'WARN', 'FIX', 'INFO'].map((s) => `log.${s}`)]
  for (const k of labels) for (const l of ['bn', 'en']) if (typeof get(def[l], k) !== 'string') missing.push(`${l}.${k}`)
  return ok(keys.length > 40 && missing.length === 0 && drift.length === 0,
    `${keys.length} keys and ${prefixes.length} prefixes in code · missing ${JSON.stringify(missing)} · schema drift ${JSON.stringify(drift)}`)
})

check('G1.9', 'no user-facing text written into the public components (it is in the site JSON)', async () => {
  const { files } = keysInCode()
  const found = []
  for (const f of files.filter((x) => x.endsWith('.vue'))) {
    const tpl = (fs.readFileSync(f, 'utf8').match(/<template>([\s\S]*)<\/template>/) ?? [])[1] ?? ''
    const text = tpl.replace(/<!--[\s\S]*?-->/g, '').replace(/\{\{[\s\S]*?\}\}/g, '').replace(/<[^>]*>/g, '\n')
    for (const line of text.split('\n').map((l) => l.trim()).filter((l) => /[A-Za-zঀ-৿]{2,}/.test(l))) found.push(`${path.basename(f)}: ${line.slice(0, 50)}`)
    for (const m of tpl.matchAll(/\s(aria-label|title|placeholder|alt)="([^"]*[A-Za-zঀ-৿][^"]*)"/g)) found.push(`${path.basename(f)}: ${m[1]}="${m[2]}"`)
  }
  return ok(found.length === 0, found.length ? JSON.stringify(found.slice(0, 8)) : 'none')
})

// ═══════════════════════════════ G1.10 ═══════════════════════════════

check('G1.10', 'no horizontal page scroll at 360, 390, 768 and 1280 px, both themes, both languages', async () => {
  await setup()
  const shots = ctx.args.shots ?? path.join(EVAL_ROOT, 'shots')
  fs.mkdirSync(shots, { recursive: true })
  const page = await ctx.browser.page()
  const out = []
  for (const width of [360, 390, 768, 1280]) for (const theme of ['dark', 'light']) for (const lang of ['bn', 'en']) {
    await page.viewport(width, 800)
    await page.goto(`${ctx.vercel.url}/truth?lang=${lang}`, 300)
    await page.eval(`localStorage.setItem('rabbitic-theme', '${theme}'); 0`)
    await page.reload()
    await page.waitFor('[data-testid="status-board"]')
    const m = await page.eval(`({ sw: document.documentElement.scrollWidth, cw: document.documentElement.clientWidth, dark: document.documentElement.classList.contains('dark') })`)
    const file = path.join(shots, `truth-${width}-${theme}-${lang}.png`)
    await page.shot(file, { full: true })
    out.push({ width, theme, lang, ...m, pass: m.sw <= m.cw && m.dark === (theme === 'dark') })
  }
  const bad = out.filter((o) => !o.pass)
  return ok(bad.length === 0, `${out.length} screenshots in ${shots} · ${bad.length ? `overflow: ${JSON.stringify(bad)}` : 'none overflow, theme applied in each'}`)
})

check('G1.10', 'dark by default; tap targets ≥ 44 px; body text ≥ 16 px; Bangla line height ≥ 1.6', async () => {
  await setup()
  const page = await ctx.browser.page()
  await page.viewport(360, 800)
  await page.goto(`${ctx.vercel.url}/truth?lang=bn`, 1500)
  await page.waitFor('[data-testid="status-board"]')
  const r = await page.eval(`(() => {
    const small = [...document.querySelectorAll('a, button, [role="button"], summary')].filter((e) => e.getClientRects().length)
      .map((e) => [e, e.getBoundingClientRect()]).filter(([e, b]) => (b.height < 44 || b.width < 44) && !e.closest('[data-inline-link]'))
      .map(([e, b]) => (e.getAttribute('data-testid') || e.textContent.trim().slice(0, 20)) + ' ' + Math.round(b.width) + 'x' + Math.round(b.height))
    const body = getComputedStyle(document.body)
    const p = document.querySelector('[data-testid="item-summary"]'), ps = p && getComputedStyle(p)
    return { dark: document.documentElement.classList.contains('dark'), small, font: parseFloat(body.fontSize), summaryFont: ps && parseFloat(ps.fontSize),
      lineHeight: ps && parseFloat(ps.lineHeight) / parseFloat(ps.fontSize) }
  })()`)
  return ok(r.dark && r.small.length === 0 && r.font >= 16 && r.summaryFont >= 16 && r.lineHeight >= 1.6, JSON.stringify(r))
})

// ═══════════════════════════════ G1.11 ═══════════════════════════════

check('G1.11', 'license-check.mjs passes, and docs/THIRD_PARTY.md lists every new package with version and licence', async () => {
  const r = spawnSync(process.execPath, [path.join(REPO, 'scripts', 'license-check.mjs')], { encoding: 'utf8', cwd: REPO })
  const doc = fs.existsSync(path.join(REPO, 'docs', 'THIRD_PARTY.md')) ? fs.readFileSync(path.join(REPO, 'docs', 'THIRD_PARTY.md'), 'utf8') : ''
  // What this build added, against the commit before it.
  const base = spawnSync('git', ['show', 'faeea44:frontend/package.json'], { cwd: REPO, encoding: 'utf8' }).stdout
  const before = JSON.parse(base)
  const now = JSON.parse(fs.readFileSync(path.join(WEB, 'package.json'), 'utf8'))
  const added = Object.entries({ ...now.dependencies, ...now.devDependencies }).filter(([n]) => !(n in { ...before.dependencies, ...before.devDependencies }))
  const csprojBefore = spawnSync('git', ['show', 'faeea44:backend/RabbitIC.Api.csproj'], { cwd: REPO, encoding: 'utf8' }).stdout
  const csproj = fs.readFileSync(path.join(REPO, 'backend', 'RabbitIC.Api.csproj'), 'utf8')
  const nuget = [...csproj.matchAll(/PackageReference Include="([^"]+)" Version="([^"]+)"/g)].filter((m) => !csprojBefore.includes(`Include="${m[1]}"`)).map((m) => [m[1], m[2]])
  const unlisted = [...added, ...nuget].filter(([n, v]) => !(doc.includes(n) && doc.includes(String(v).replace(/^[\^~]/, ''))))
  const summary = r.stdout.trim().split('\n').slice(-2).join(' / ')
  return ok(r.status === 0 && unlisted.length === 0 && added.length > 0, `license-check exit ${r.status}: ${summary} · new packages ${JSON.stringify([...added, ...nuget].map(([n, v]) => `${n}@${v}`))} · not in THIRD_PARTY.md: ${JSON.stringify(unlisted)}`)
})

// ═══════════════════════════════ G1.12 – G1.17: the theme (§3A) ═══════════════════════════════

/** Every decoration the theme draws, by the markers and classes §3A.3 names. */
const DECO = '[data-meme],[data-nickname],[data-stamp],[data-effect],.glitch,.crack,.scanlines,.mascot,.stamp,.meme'

/**
 * The visitor's motion setting, said out loud: headless Chrome takes the machine's
 * own (Windows' "animation effects" off reads as reduce), and a check about motion
 * must not depend on how the eval machine is set up.
 */
const motion = (page, value) => page.cdp('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-reduced-motion', value }] })

/** A fresh page on the public site, motion allowed unless `before` says otherwise. */
async function publicPage(p, { lang = 'bn', width = 360, settle = 1500, before } = {}) {
  const page = await ctx.browser.page()
  await page.viewport(width, 800)
  await motion(page, 'no-preference')
  if (before) await before(page)
  await page.goto(`${ctx.vercel.url}${p}${p.includes('?') ? '&' : '?'}lang=${lang}`, settle)
  return page
}

check('G1.12', 'no meme, nickname, stamp or effect inside a pill, the patch time, what-to-do, advisories, references, corrections or the urgent banner', async () => {
  await setup()
  const page = await publicPage('/truth')
  await page.waitFor('[data-testid="status-board"]')
  const r = await page.eval(`(() => {
    const deco = ${JSON.stringify(DECO)}
    const safety = [...document.querySelectorAll('[data-safety]')]
    const inside = safety.flatMap((s) => [...s.querySelectorAll(deco)].map((e) => s.dataset.safety + ' > ' + (e.dataset.meme || e.dataset.stamp || e.dataset.effect || e.getAttribute('class'))))
    const self = safety.filter((s) => s.matches(deco)).map((s) => s.dataset.safety)
    const stampOnly = [...document.querySelectorAll('[data-item-id]')].filter((c) => c.querySelector('[data-stamp]') && !c.querySelector('[data-safety="pill"]')).length
    const nickOnly = [...document.querySelectorAll('[data-nickname]')].filter((n) => !n.parentElement.textContent.replace(n.textContent, '').trim()).length
    const pillsPlain = [...document.querySelectorAll('[data-safety="pill"]')].every((p) => p.textContent.trim().length > 0)
    return { inside, self, kinds: [...new Set(safety.map((s) => s.dataset.safety))].sort(), stampOnly, nickOnly, pillsPlain, deco: document.querySelectorAll(deco).length }
  })()`)
  const need = ['advisories', 'corrections', 'patch', 'pill', 'references', 'urgent', 'warning']
  return ok(r.inside.length === 0 && r.self.length === 0 && need.every((k) => r.kinds.includes(k)) && r.stampOnly === 0 && r.nickOnly === 0 && r.pillsPlain && r.deco > 5,
    `${r.deco} decorations on the page · safety blocks ${JSON.stringify(r.kinds)} · decoration inside them ${JSON.stringify(r.inside)}${r.self.length ? ` · themed safety ${JSON.stringify(r.self)}` : ''} · stamp without a pill ${r.stampOnly} · nickname without its plain name ${r.nickOnly}`)
})

/** Every element's computed animation-name that is not none (pseudo-elements included). */
const ANIMATED = `(() => [...document.querySelectorAll('*')].flatMap((e) => [null, '::before', '::after']
  .map((p) => [e, p, getComputedStyle(e, p).animationName]).filter(([, , n]) => n && n !== 'none')
  .map(([e, p, n]) => (e.dataset.testid || e.getAttribute('class') || e.tagName) + (p || '') + ':' + n)))()`

check('G1.13', 'serious mode by itself during an active attack: no glitch, no crack, no meme outside the footer, a worried mascot', async () => {
  const f = await setup()
  const toAttack = await admin('POST', `/api/truth/items/${f.exlItem}/status`, { status: 'active_attack', textBn: 'সক্রিয় আক্রমণ শুরু হয়েছে।' })
  const pub1 = await admin('POST', `/api/truth/issues/${f.issueId}/publish`)
  try {
    const page = await publicPage('/truth')
    await page.waitFor('[data-testid="hero"]')
    const r = await page.eval(`(() => {
      const hero = document.querySelector('[data-testid="hero"]'), title = document.querySelector('[data-testid="hero-title"]')
      const visible = (e) => e.getClientRects().length > 0
      return { glitch: title.classList.contains('glitch'), titleAnim: getComputedStyle(title).animationName,
        heroEffects: [...hero.querySelectorAll('[data-effect]')].length,
        memesOutsideFooter: [...document.querySelectorAll('[data-meme]')].filter((m) => visible(m) && !m.closest('footer')).map((m) => m.dataset.meme),
        pose: document.querySelector('[data-testid="hero-mascot"]')?.dataset.pose,
        banner: document.querySelector('[data-testid="urgent-banner"]')?.textContent.trim() }
    })()`)
    if (ctx.args.shots) await page.shot(path.join(ctx.args.shots, 'serious-active-attack-360-dark-bn.png'), { full: true })
    return ok(toAttack.status === 200 && pub1.status === 200 && !r.glitch && r.titleAnim === 'none' && r.heroEffects === 0 && r.memesOutsideFooter.length === 0 && r.pose === 'worried' && !!r.banner,
      JSON.stringify(r))
  } finally {
    await admin('POST', `/api/truth/items/${f.exlItem}/status`, { status: 'patch_scheduled', textBn: 'আক্রমণ থেমেছে; প্যাচের অপেক্ষা।' })
    await admin('POST', `/api/truth/issues/${f.issueId}/publish`)
  }
})

check('G1.13', 'the visitor switch and prefers-reduced-motion each turn every animation off (computed animation-name: none)', async () => {
  await setup()
  const normal = await publicPage('/truth')
  await normal.waitFor('[data-testid="hero"]')
  const before = await normal.eval(ANIMATED)
  await normal.click('[data-testid="serious-switch"]')
  await sleep(400)
  const switched = await normal.eval(ANIMATED)
  const hidden = await normal.eval(`[...document.querySelectorAll('[data-meme],[data-effect]')].filter((e) => e.getClientRects().length > 0).length`)
  await normal.reload()
  const remembered = await normal.eval(`document.documentElement.classList.contains('serious') && document.querySelector('[data-testid="serious-switch"]').getAttribute('aria-checked')`)
  const reduced = await publicPage('/truth', { before: (p) => motion(p, 'reduce') })
  await reduced.waitFor('[data-testid="hero"]')
  const stillMoving = await reduced.eval(ANIMATED)
  return ok(before.length > 0 && switched.length === 0 && hidden === 0 && remembered === 'true' && stillMoving.length === 0,
    `animated before: ${JSON.stringify(before.slice(0, 6))} · after the switch: ${JSON.stringify(switched)} · memes and effects still visible: ${hidden} · remembered after reload: ${remembered} · reduced motion: ${JSON.stringify(stillMoving)}`)
})

check('G1.14', 'nothing animates more than 3 times a second; the glitch and the crack run once, and the crack never again in the session', async () => {
  await setup()
  const page = await ctx.browser.page()
  await page.viewport(360, 800)
  await motion(page, 'no-preference')
  // Every animation start and repeat on this page, recorded from the first byte.
  await page.cdp('Page.addScriptToEvaluateOnNewDocument', { source: `window.__anim = [];
    for (const type of ['animationstart', 'animationiteration'])
      document.addEventListener(type, (e) => window.__anim.push({ type, name: e.animationName, t: performance.now(),
        el: e.target.dataset?.testid || e.target.getAttribute?.('class') || e.target.tagName }), true);` })
  await page.goto(`${ctx.vercel.url}/truth?lang=bn`, 4500)
  const first = await page.eval(`(() => ({ events: window.__anim, timing: document.getAnimations().map((a) => ({ name: a.animationName,
    duration: a.effect.getTiming().duration, iterations: a.effect.getTiming().iterations })) }))()`)
  await page.reload()
  await sleep(1500)
  const second = await page.eval('window.__anim')
  const starts = (evs, name) => evs.filter((e) => e.type === 'animationstart' && e.name === name).length
  const iterations = (evs, name) => evs.filter((e) => e.type === 'animationiteration' && e.name === name).length
  // A repeating animation slower than 3 a second has a period of at least 334 ms.
  const tooFast = first.timing.filter((a) => (a.iterations === Infinity || a.iterations > 1) && a.duration < 334)
  const once = ['glitch', 'crack'].map((n) => [n, starts(first.events, n), iterations(first.events, n)])
  const led = first.timing.find((a) => a.name === 'led')
  return ok(tooFast.length === 0 && starts(first.events, 'glitch') === 1 && iterations(first.events, 'glitch') === 0
    && starts(first.events, 'crack') > 0 && iterations(first.events, 'crack') === 0 && starts(second, 'crack') === 0
    && (!led || led.duration >= 1000),
  `start/repeat counts ${JSON.stringify(once)} · LED ${led ? `${led.duration} ms × ${led.iterations}` : 'not blinking'} · faster than 3/s: ${JSON.stringify(tooFast)} · after a reload in the same session: crack ${starts(second, 'crack')}, glitch ${starts(second, 'glitch')}`)
})

check('G1.15', 'the meme schema refuses over 90 characters, a missing language, HTML and an unknown placement', async () => {
  const state = (await admin('GET', '/api/truth/content/memes')).json
  const base = state.default
  const cases = {
    'over 90 characters': (d) => { d.memes[0].en = 'A'.repeat(91) },
    'missing a language': (d) => { delete d.memes[0].bn },
    'HTML': (d) => { d.memes[0].en = 'PATCH <b>NOW</b>' },
    'unknown placement': (d) => { d.memes[0].placement = ['sidebar'] },
    'three lines': (d) => { d.memes[0].en = 'one\ntwo\nthree' },
    'duplicate id': (d) => { d.memes[1].id = d.memes[0].id },
  }
  const out = []
  for (const [name, mutate] of Object.entries(cases)) {
    const doc = structuredClone(base); mutate(doc)
    const r = await admin('POST', '/api/truth/content/memes/publish', { text: doc })
    out.push(`${name}: ${r.status}${r.status === 400 ? ` (${r.json?.problems?.[0]?.field}: ${r.json?.problems?.[0]?.message})` : ''}`)
  }
  const good = await admin('POST', '/api/truth/content/memes/validate', { text: base })
  return ok(out.every((x) => / 400 \(/.test(x)) && good.json?.errors?.length === 0, `${out.join(' · ')} · the built-in set validates: ${good.json?.errors?.length === 0}`)
})

check('G1.15', 'rotation: no meme twice in a view, the mood fits the news, and at most one meme per screen height at 360 px', async () => {
  const f = await setup()
  // A calm issue and a relieved one, besides the current (alert: a patch is scheduled).
  const calm = await draftIssue('eval-mood-calm', demoItem(f.demo, { sources: [{ ...demoItem(f.demo).sources[0], kind: 'claim' }], status: 'unconfirmed' }))
  const relief = await draftIssue('eval-mood-relief', demoItem(f.demo, { status: 'patched' }))
  await admin('POST', `/api/truth/issues/${calm.id}/publish`)
  await admin('POST', `/api/truth/issues/${relief.id}/publish`)
  const views = []
  for (const [p, mood] of [['/truth', 'alert'], ['/truth/eval-mood-calm', 'calm'], ['/truth/eval-mood-relief', 'relief']]) {
    for (let n = 0; n < 4; n++) {
      const page = await publicPage(p, { settle: 1200 })
      await page.waitFor('[data-testid="status-board"]')
      const m = await page.eval(`(() => ({ h: innerHeight, memes: [...document.querySelectorAll('[data-meme]')].filter((e) => e.getClientRects().length)
        .map((e) => ({ id: e.dataset.meme, mood: e.dataset.mood, top: e.getBoundingClientRect().top + scrollY, bottom: e.getBoundingClientRect().bottom + scrollY })) }))()`)
      const ids = m.memes.map((x) => x.id)
      const sorted = [...m.memes].sort((a, b) => a.top - b.top)
      const close = sorted.slice(1).filter((x, i) => x.top - sorted[i].bottom < m.h).length
      views.push({ p, mood, ids, moods: m.memes.map((x) => x.mood), repeat: ids.length !== new Set(ids).size, wrongMood: m.memes.filter((x) => x.mood !== mood).length, close })
    }
  }
  const bad = views.filter((v) => v.repeat || v.wrongMood || v.close || !v.ids.length)
  const shown = [...new Set(views.flatMap((v) => v.ids))]
  return ok(bad.length === 0, `${views.length} views, ${shown.length} different memes shown (${shown.join(', ')}) · problems: ${JSON.stringify(bad)}`)
})

check('G1.16', 'no third parties: every request /truth makes is to its own origin', async () => {
  await setup()
  const out = []
  for (const p of ['/truth', '/truth/about', '/truth/glossary', '/truth/no-such-page']) {
    const page = await publicPage(p, { settle: 2000 })
    if (ctx.args.shots && p === '/truth/no-such-page') await page.shot(path.join(ctx.args.shots, 'room-101-360-dark-bn.png'), { full: true })
    const r = await page.eval(`(() => ({ urls: performance.getEntriesByType('resource').map((e) => e.name),
      external: [...document.querySelectorAll('script[src],link[href],img[src],iframe[src]')].map((e) => e.src || e.href) }))()`)
    // data: counts as foreign too: the CSP refuses it, so one would be a blocked request and a console error.
    const foreign = [...r.urls, ...r.external].filter((u) => !u.startsWith(ctx.vercel.url))
    out.push({ p, requests: r.urls.length, foreign })
  }
  const bad = out.filter((o) => o.foreign.length)
  return ok(bad.length === 0 && out.every((o) => o.requests > 0), out.map((o) => `${o.p}: ${o.requests} requests, foreign ${JSON.stringify(o.foreign)}`).join(' · '))
})

/** A child process that leaves this process's servers running: { status, stdout, stderr, error }. */
function spawnAsync(cmd, args, { shell, env, timeoutMs }) {
  return new Promise((resolve) => {
    const child = spawn(cmd, args, { shell, env })
    let stdout = '', stderr = ''
    child.stdout.on('data', (d) => { stdout += d })
    child.stderr.on('data', (d) => { stderr += d })
    const timer = setTimeout(() => { child.kill(); resolve({ status: null, stdout, stderr, error: new Error(`timed out after ${timeoutMs} ms`) }) }, timeoutMs)
    child.on('close', (status) => { clearTimeout(timer); resolve({ status, stdout, stderr, error: null }) })
  })
}

/** The <template> of a component, as bytes. */
const templateBytes = (rel) => Buffer.byteLength((fs.readFileSync(path.join(WEB, rel), 'utf8').match(/<template>([\s\S]*)<\/template>/) ?? [])[1] ?? '')

check('G1.17', 'asset weight: the mascot\'s poses ≤ 10 KB, the crack ≤ 8 KB, display fonts subset and loaded only where used', async () => {
  await setup()
  const mascotSrc = templateBytes('src/truth/components/Mascot.vue')
  const crackSrc = templateBytes('src/truth/components/CrackOverlay.vue')
  // As drawn: every pose on the About page's gallery, and the crack on the hero.
  const about = await publicPage('/truth/about')
  await about.waitFor('[data-testid="mascot-gallery"]')
  const poses = await about.eval(`[...document.querySelectorAll('[data-testid="mascot-gallery"] svg')].map((s) => [s.dataset.pose, s.outerHTML.length])`)
  if (ctx.args.shots) {
    await about.eval(`document.querySelector('[data-testid="mascot-gallery"]').scrollIntoView(); 0`)
    await about.shot(path.join(ctx.args.shots, 'mascot-poses-360-dark-bn.png'))
    await about.eval(`localStorage.setItem('rabbitic-theme', 'light'); 0`); await about.reload()
    await about.eval(`document.querySelector('[data-testid="mascot-gallery"]').scrollIntoView(); 0`)
    await about.shot(path.join(ctx.args.shots, 'mascot-poses-360-light-bn.png'))
  }
  const page = await publicPage('/truth', { lang: 'en' })
  await page.waitFor('[data-testid="hero"]')
  await sleep(1000)
  const fonts = await page.eval(`(async () => {
    await document.fonts.ready
    const loaded = [...document.fonts].filter((f) => f.status === 'loaded').map((f) => ({ family: f.family.replace(/"/g, ''), range: f.unicodeRange, weight: f.weight }))
    const used = new Set([...document.querySelectorAll('body *')].filter((e) => e.getClientRects().length && e.textContent.trim())
      .map((e) => getComputedStyle(e).fontFamily.split(',')[0].replace(/"/g, '').trim()))
    const files = performance.getEntriesByType('resource').filter((e) => /\\.woff2?$/.test(e.name)).map((e) => e.name.split('/').pop())
    return { loaded, used: [...used], files }
  })()`)
  const display = ['Anton', 'Baloo Da 2', 'Special Elite']
  const loadedDisplay = fonts.loaded.filter((f) => display.includes(f.family))
  const unused = [...new Set(loadedDisplay.map((f) => f.family))].filter((fam) => !fonts.used.includes(fam))
  const whole = fonts.loaded.filter((f) => !f.range || /U\+0-10FFFF/i.test(f.range))
  const crack = await page.eval(`document.querySelector('.crack')?.outerHTML.length ?? 0`)
  const posesTotal = poses.reduce((n, [, b]) => n + b, 0)
  return ok(mascotSrc <= 10240 && posesTotal <= 10240 && poses.length === 6 && crackSrc <= 8192 && crack <= 8192 && crack > 0 && unused.length === 0 && whole.length === 0 && loadedDisplay.length > 0,
    `mascot template ${mascotSrc} B, 6 poses drawn ${posesTotal} B (${poses.map(([p, b]) => `${p} ${b}`).join(', ')}) · crack template ${crackSrc} B, drawn ${crack} B · display faces loaded ${JSON.stringify([...new Set(loadedDisplay.map((f) => f.family))])}, loaded but unused ${JSON.stringify(unused)}, not subset ${JSON.stringify(whole.map((f) => f.family))} · font files ${JSON.stringify(fonts.files)}`)
})

/**
 * A throwaway self-signed certificate for 127.0.0.1, made with openssl (Git for Windows,
 * macOS and Linux all ship it). Null when openssl isn't there.
 */
function selfSignedTls() {
  const dir = path.join(EVAL_ROOT, 'tls')
  fs.mkdirSync(dir, { recursive: true })
  const key = path.join(dir, 'key.pem'), cert = path.join(dir, 'cert.pem')
  if (!fs.existsSync(cert)) {
    const r = spawnSync('openssl', ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-keyout', key, '-out', cert, '-days', '2',
      '-subj', '/CN=127.0.0.1', '-addext', 'subjectAltName=IP:127.0.0.1'], { encoding: 'utf8', env: { ...process.env, MSYS_NO_PATHCONV: '1' } })
    if (r.status !== 0) return null
  }
  return { key: fs.readFileSync(key), cert: fs.readFileSync(cert) }
}

check('G1.17', 'Lighthouse (mobile) with the theme on: Performance ≥ 90, Accessibility ≥ 95, Best Practices ≥ 95, SEO ≥ 95, in both languages', async () => {
  await setup()
  const { findBrowser } = await import('./lib/headless-browser.mjs')
  // Over HTTPS, as Vercel serves the page. On some machines (this one) the browser's plain-HTTP traffic
  // reaches it decompressed — something on the machine inspects it — and Lighthouse would then model
  // every script and stylesheet at its full size; over HTTPS the browser gets what was sent.
  const tls = selfSignedTls()
  const site = tls ? await startVercelLocal({ dist: DIST, config: path.join(WEB, 'vercel.json'), tunnel: `http://127.0.0.1:${ctx.tunnelPort}`, tls }) : ctx.vercel
  // Lighthouse runs its own Chrome; the eval's, with every page the checks opened, would starve it.
  await ctx.browser.close()
  // One argument with a space: the Windows shell needs it quoted.
  const flags = '--chrome-flags=--headless=new' + (tls ? ' --ignore-certificate-errors' : '')
  const out = []
  try {
    for (const lang of ['bn', 'en']) {
      const file = path.join(EVAL_ROOT, `lighthouse-${lang}.json`)
      fs.rmSync(file, { force: true })
      // Not spawnSync: the stand-ins answer from this same process, and a blocked event loop would never serve Lighthouse's Chrome.
      const r = await spawnAsync('npx', ['-y', 'lighthouse@13.5.0', `${site.url}/truth?lang=${lang}`, '--quiet', '--output=json', `--output-path=${file}`,
        '--only-categories=performance,accessibility,best-practices,seo', process.platform === 'win32' ? `"${flags}"` : flags],
      { shell: process.platform === 'win32', env: { ...process.env, CHROME_PATH: findBrowser() }, timeoutMs: 240_000 })
      if (!fs.existsSync(file)) { out.push({ lang, error: (r.error?.message ?? '') + (r.stderr || r.stdout).slice(-300) }); continue }
      const rep = JSON.parse(fs.readFileSync(file, 'utf8'))
      if (rep.runtimeError) { out.push({ lang, error: rep.runtimeError.code }); continue }
      const score = (k) => Math.round((rep.categories[k]?.score ?? 0) * 100)
      const js = rep.audits['network-requests'].details.items.find((i) => i.resourceType === 'Script')
      out.push({ lang, performance: score('performance'), accessibility: score('accessibility'), bestPractices: score('best-practices'), seo: score('seo'),
        lcp: rep.audits['largest-contentful-paint'].displayValue, script: `${(js.transferSize / 1024).toFixed(1)} of ${(js.resourceSize / 1024).toFixed(1)} KB on the wire` })
      if (ctx.args.shots) fs.copyFileSync(file, path.join(ctx.args.shots, `lighthouse-${lang}.json`))
    }
  } finally {
    if (site !== ctx.vercel) await site.stop()
    ctx.browser = await launchBrowser({ base: ctx.vercel.url, profileRoot: path.join(EVAL_ROOT, 'profiles') })
  }
  const pass = out.every((o) => !o.error && o.performance >= 90 && o.accessibility >= 95 && o.bestPractices >= 95 && o.seo >= 95)
  return ok(pass, `${tls ? 'over https' : 'over http (no openssl)'} · ${JSON.stringify(out)}`)
})

// ─────────────────────────────── runner ───────────────────────────────

function parseArgs(argv) {
  const a = { only: null, build: true, web: true, shots: null }
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === '--only') a.only = argv[++i]
    else if (argv[i] === '--no-build') a.build = false
    else if (argv[i] === '--no-web') a.web = false
    else if (argv[i] === '--shots') a.shots = path.resolve(argv[++i])
  }
  return a
}

async function freePort() {
  const s = net.createServer()
  await new Promise((r) => s.listen(0, '127.0.0.1', r))
  const port = s.address().port
  await new Promise((r) => s.close(r))
  return port
}

async function main() {
  ctx.args = parseArgs(process.argv.slice(2))
  const user = process.env.TRUTH_EVAL_ADMIN_USER, secret = process.env.TRUTH_EVAL_ADMIN_SECRET
  if (!user || !secret) { console.error('Set TRUTH_EVAL_ADMIN_USER and TRUTH_EVAL_ADMIN_SECRET to the seeded administrator.'); process.exit(2) }
  const selected = checks.filter((c) => !ctx.args.only || c.id === ctx.args.only)
  if (!selected.length) { console.error('No checks match.'); process.exit(2) }
  if (ctx.args.shots) fs.mkdirSync(ctx.args.shots, { recursive: true })

  if (ctx.args.web) {
    const r = spawnSync('npm', ['run', 'build'], { cwd: WEB, encoding: 'utf8', shell: process.platform === 'win32' })
    if (r.status !== 0) { console.error(`frontend build failed:\n${r.stdout.slice(-3000)}\n${r.stderr.slice(-3000)}`); process.exit(2) }
  }
  if (ctx.args.build) buildApi()
  const webroot = path.join(API_BIN_DIR, 'wwwroot')
  fs.rmSync(webroot, { recursive: true, force: true })
  if (fs.existsSync(DIST)) fs.cpSync(DIST, webroot, { recursive: true })

  fs.mkdirSync(EVAL_ROOT, { recursive: true })
  const runDir = fs.mkdtempSync(path.join(EVAL_ROOT, 'run-'))
  ctx.fake = await startFakeOllama()
  ctx.tunnelPort = await freePort()
  let failed = 0
  try {
    ctx.api = await startApi({ fakeUrl: ctx.fake.url, runDir, env: { Truth__Tunnel__Port: String(ctx.tunnelPort) } })
    ctx.admin = await login(user, secret)
    ctx.vercel = await startVercelLocal({ dist: DIST, config: path.join(WEB, 'vercel.json'), tunnel: `http://127.0.0.1:${ctx.tunnelPort}` })
    ctx.browser = await launchBrowser({ base: ctx.vercel.url, profileRoot: path.join(EVAL_ROOT, 'profiles') })
    console.log(`api ${ctx.api.url} · tunnel :${ctx.tunnelPort} · vercel-local ${ctx.vercel.url} · db ${ctx.api.dbPath}\n`)
    console.log('id | check | result | detail')
    for (const c of selected) {
      let res
      try { res = await c.fn() } catch (e) { res = { pass: false, detail: `threw: ${e.message}` } }
      if (!res.pass) failed++
      console.log(`${c.id} | ${c.name} | ${res.pass ? 'PASS' : 'FAIL'} | ${res.detail ?? ''}`)
    }
    console.log(`\n${selected.length - failed}/${selected.length} passed`)
  } finally {
    await ctx.browser?.close()
    await ctx.vercel?.stop()
    await ctx.api?.stop()
    await ctx.fake.stop()
  }
  process.exit(failed ? 1 : 0)
}

main().catch((e) => { console.error(e.stack ?? e); process.exit(2) })
