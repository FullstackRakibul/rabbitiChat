// A stand-in for Vercel, for the Ministry of Truth eval (docs/TRUTH_PROMPT.md §4, G1.6, G1.7).
//
// It reads frontend/vercel.json and does what Vercel does with it, in Vercel's
// order: a file in the build output is served as it is; otherwise the rewrites
// are tried in order (`:name` is one segment, `:name*` zero or more); `headers`
// rules apply by the request's own path. An external destination — the tunnel —
// is proxied to `tunnel` instead of the real host, with X-Forwarded-For set as
// the tunnel would set it — a different documentation-range address per request
// unless `clientIp` is given, so a browser check's page loads never add up to the
// rate limit another check is measuring. `setDown(true)` makes the tunnel unreachable (502), as
// Vercel answers when the backend is off.
//
// It is not Vercel: it proves the rewrites, the headers and the tunnel guard
// together on one machine, and the live check (docs/truth-deploy.md) proves the
// same against the real deployment.

import http from 'node:http'
import https from 'node:https'
import fs from 'node:fs'
import path from 'node:path'
import zlib from 'node:zlib'

const TYPES = {
  '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8',
  '.json': 'application/json', '.svg': 'image/svg+xml', '.png': 'image/png', '.ico': 'image/x-icon',
  '.woff2': 'font/woff2', '.woff': 'font/woff', '.txt': 'text/plain; charset=utf-8', '.xml': 'application/xml',
}

/** A Vercel source pattern as a RegExp with named groups. Supports literals, :name, :name*, and (.*). */
export function compile(source) {
  let re = ''
  for (const part of source.split('/').filter(Boolean)) {
    const star = part.match(/^:([a-zA-Z]\w*)\*$/)
    const one = part.match(/^:([a-zA-Z]\w*)$/)
    if (star) re += `(?:/(?<${star[1]}>.*))?`
    else if (one) re += `/(?<${one[1]}>[^/]+)`
    else re += '/' + part.split('(.*)').map((s) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('(?:.*)')
  }
  return new RegExp(`^${re || '/'}/?$`)
}

function fill(destination, groups) {
  return destination.replace(/:([a-zA-Z]\w*)\*?/g, (m, name) => (groups && name in groups ? groups[name] ?? '' : m))
}

export async function startVercelLocal({ dist: distDir, config, tunnel, port = 0, clientIp, tls }) {
  // Normalised, so the "stays inside the build output" test below compares like with like on Windows.
  const dist = path.resolve(distDir)
  let visitor = 0
  const vercel = JSON.parse(fs.readFileSync(config, 'utf8'))
  const rewrites = (vercel.rewrites ?? []).map((r) => ({ ...r, re: compile(r.source) }))
  const headerRules = (vercel.headers ?? []).map((h) => ({ ...h, re: compile(h.source) }))
  let down = false
  const log = []

  // tls { key, cert }: served over HTTPS, as Vercel is — see G1.17 for why that matters to a measurement.
  const handler = async (req, res) => {
    const url = new URL(req.url, 'http://local')
    const p = decodeURIComponent(url.pathname)
    for (const rule of headerRules) if (rule.re.test(p)) for (const h of rule.headers) res.setHeader(h.key, h.value)

    const file = (rel) => {
      const full = path.join(dist, rel)
      if (!full.startsWith(dist) || !fs.existsSync(full) || !fs.statSync(full).isFile()) return false
      const type = TYPES[path.extname(full)] ?? 'application/octet-stream'
      res.setHeader('Content-Type', type)
      let body = fs.readFileSync(full)
      // As Vercel does: text is sent compressed to a browser that accepts it.
      if (/text|javascript|json|svg|xml/.test(type) && /\bgzip\b/.test(req.headers['accept-encoding'] ?? '')) {
        body = zlib.gzipSync(body)
        res.setHeader('Content-Encoding', 'gzip')
        res.setHeader('Vary', 'Accept-Encoding')
      }
      res.end(body)
      return true
    }
    if (p !== '/' && file(p)) return

    for (const r of rewrites) {
      const m = r.re.exec(p)
      if (!m) continue
      const dest = fill(r.destination, m.groups)
      if (!/^https?:\/\//.test(dest)) { if (file(dest)) return; break }
      const target = new URL(dest)
      const upstream = new URL(target.pathname + url.search, tunnel)
      log.push({ path: p, upstream: upstream.pathname + upstream.search })
      if (down) { res.statusCode = 502; res.end('upstream unreachable'); return }
      const body = []
      for await (const c of req) body.push(c)
      const headers = { ...req.headers, host: upstream.host, 'x-forwarded-for': clientIp ?? `203.0.113.${1 + (visitor++ % 250)}` }
      delete headers.connection
      try {
        const r2 = await fetch(upstream, { method: req.method, headers, body: body.length ? Buffer.concat(body) : undefined, redirect: 'manual' })
        res.statusCode = r2.status
        r2.headers.forEach((v, k) => { if (!['content-encoding', 'content-length', 'transfer-encoding', 'connection'].includes(k)) res.setHeader(k, v) })
        res.end(Buffer.from(await r2.arrayBuffer()))
      } catch {
        res.statusCode = 502
        res.end('upstream unreachable')
      }
      return
    }
    res.statusCode = 404
    res.end('not found')
  }
  const server = tls ? https.createServer(tls, handler) : http.createServer(handler)
  await new Promise((r) => server.listen(port, '127.0.0.1', r))
  return {
    url: `${tls ? 'https' : 'http'}://127.0.0.1:${server.address().port}`,
    log,
    setDown: (v) => { down = v },
    stop: () => new Promise((r) => server.close(r)),
  }
}
