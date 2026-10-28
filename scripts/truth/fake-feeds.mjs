// A stand-in for the public feeds the Records Department reads (docs/TRUTH_PROMPT_P5.md §6): HTTPS
// on loopback, serving the synthetic files in fixtures/records with their times filled in. The API
// reaches it under invented host names (Truth:Records:TestHosts), so the SSRF guard is exercised
// for real: the names are mapped, nothing else on loopback is.
//
//   · ETag on every fixture, and 304 for a matching If-None-Match (G5.4);
//   · every request logged with its headers;
//   · route(path, fn) replaces what one path answers: a delay, a failure, a redirect, a huge body.

import fs from 'node:fs'
import https from 'node:https'
import path from 'node:path'
import crypto from 'node:crypto'
import { fileURLToPath } from 'node:url'

const FIXTURES = path.join(path.dirname(fileURLToPath(import.meta.url)), 'fixtures', 'records')
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']

/** {{rfc:-30m}} · {{iso:-2h}} · {{sp:-1h}} · {{date:+20d}} → times relative to now. */
export function fillTimes(text, now = Date.now()) {
  return text.replace(/\{\{(rfc|iso|sp|date):([+-]\d+)([mhd])\}\}/g, (_, kind, n, unit) => {
    const t = new Date(now + Number(n) * { m: 60_000, h: 3_600_000, d: 86_400_000 }[unit])
    if (kind === 'rfc') return t.toUTCString()
    if (kind === 'iso') return t.toISOString().replace(/\.\d{3}Z$/, 'Z')
    if (kind === 'date') return t.toISOString().slice(0, 10)
    return `${MONTHS[t.getUTCMonth()]} ${t.getUTCDate()}, ${String(t.getUTCHours()).padStart(2, '0')}:${String(t.getUTCMinutes()).padStart(2, '0')} UTC`
  })
}

const TYPES = { '.rss': 'application/rss+xml', '.atom': 'application/atom+xml', '.xml': 'application/xml', '.json': 'application/json', '.html': 'text/html; charset=utf-8', '.txt': 'text/plain' }

/**
 * @param {{ key: Buffer, cert: Buffer }} tls
 * @param {Record<string, string>} files  path → fixture file name, e.g. { '/demoai/feed.rss': 'incidentio-1.rss' }
 * @param {Set<string>} [noEtag]  paths served without an ETag: every fetch is parsed again (G5.2's dedupe)
 */
export async function startFakeFeeds(tls, files, noEtag = new Set()) {
  const log = []
  const routes = new Map()
  const map = { ...files }
  // Times are filled in once per fixture, so the body (and its ETag) stays the same between fetches.
  const bodies = new Map()
  const bodyOf = (file) => {
    if (!bodies.has(file)) bodies.set(file, fillTimes(fs.readFileSync(path.join(FIXTURES, file), 'utf8')))
    return bodies.get(file)
  }

  const server = https.createServer(tls, async (req, res) => {
    const url = new URL(req.url, 'https://x')
    log.push({ at: Date.now(), host: req.headers.host, path: url.pathname, headers: { ...req.headers } })
    const custom = routes.get(url.pathname)
    if (custom) return custom(req, res, url)
    const file = map[url.pathname]
    if (!file) { res.writeHead(404, { 'Content-Type': 'text/plain' }); return res.end('not found') }
    const body = bodyOf(file)
    const etag = `"${crypto.createHash('sha256').update(body).digest('hex').slice(0, 16)}"`
    const type = TYPES[path.extname(file)] ?? 'application/octet-stream'
    if (noEtag.has(url.pathname)) { res.writeHead(200, { 'Content-Type': type }); return res.end(body) }
    if (req.headers['if-none-match'] === etag) { res.writeHead(304, { ETag: etag }); return res.end() }
    res.writeHead(200, { 'Content-Type': type, ETag: etag, 'Last-Modified': new Date(0).toUTCString() })
    res.end(body)
  })
  await new Promise((r) => server.listen(0, '127.0.0.1', r))
  const port = server.address().port
  return {
    port,
    startedAt: Date.now(),
    log,
    /** Points a path at another fixture (an incident that changed, G5.2). */
    serve: (p, file) => { map[p] = file; routes.delete(p) },
    route: (p, fn) => routes.set(p, fn),
    requests: (p) => log.filter((l) => l.path === p),
    stop: () => new Promise((r) => { server.closeAllConnections?.(); server.close(r) }),
  }
}
