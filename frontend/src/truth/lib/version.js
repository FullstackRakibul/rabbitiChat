// The version checker (§3.5): safe, update to X, end of life, or "the patch isn't
// out yet". It runs in the browser only — the version a visitor types is never
// sent anywhere (§11, G2.5) — over the issue's own advisories.

import { compareVersions } from './template.js'

const VERSION = /^\s*v?(\d+(?:\.\d+){0,3})\s*$/
const BOUND = /(\d+(?:\.\d+){0,3})/

export function parseVersion(s) {
  const m = VERSION.exec(String(s ?? ''))
  return m ? m[1] : null
}

/** "3.1.5" → "3.1". */
export const lineOf = (v) => v.split('.').slice(0, 2).join('.')

const inLine = (v, line) => v === line || v.startsWith(line + '.')

/**
 * The ranges an advisory's free text names: "3.1.0 – 3.1.6, 3.2.0 – 3.2.3", "< 3.1.7",
 * "<= 3.1.6", ">= 3.2", "3.1.4". A range a reader can't parse is skipped, never guessed.
 */
export function parseRanges(text) {
  const out = []
  for (const part of String(text ?? '').split(/[,;]|\band\b|এবং|ও/)) {
    const p = part.trim()
    if (!p) continue
    const span = p.match(/(\d+(?:\.\d+){0,3})\s*(?:–|—|-|to|থেকে)\s*(\d+(?:\.\d+){0,3})/)
    if (span) { out.push({ from: span[1], to: span[2] }); continue }
    const op = p.match(/(<=|≤|<|>=|≥|>)\s*(\d+(?:\.\d+){0,3})/)
    if (op) {
      const [, o, v] = op
      out.push(o === '<' ? { below: v } : o === '<=' || o === '≤' ? { to: v } : o === '>' ? { above: v } : { from: v })
      continue
    }
    const one = p.match(BOUND)
    if (one) out.push({ from: one[1], to: one[1] })
  }
  return out
}

function inRange(v, r) {
  if (r.from && compareVersions(v, r.from) < 0) return false
  if (r.to && compareVersions(v, r.to) > 0) return false
  if (r.below && compareVersions(v, r.below) >= 0) return false
  if (r.above && compareVersions(v, r.above) <= 0) return false
  return true
}

const RANK = { safe: 0, noFix: 1, update: 2, notYet: 3, eol: 4 }

/**
 * One version against one technology's items in an issue:
 * { kind: invalid | safe | update | notYet | noFix | eol, version, line, fix, item }.
 * `now` is the server's time (lib/clock.js), so a wrong device clock can't say a patch is out.
 */
export function checkVersion(input, tech, items, now) {
  const version = parseVersion(input)
  if (!version) return { kind: 'invalid' }
  const line = lineOf(version)
  if ((tech.eolLines ?? []).some((l) => inLine(version, l))) return { kind: 'eol', version, line }

  let worst = { kind: 'safe', version, line }
  for (const item of items) {
    for (const a of item.advisories ?? []) {
      const ranges = parseRanges(a.affectedRange)
      if (!ranges.length || !ranges.some((r) => inRange(version, r))) continue
      const fixes = (a.fixedVersions ?? []).filter((f) => inLine(f, line) && compareVersions(f, version) > 0).sort(compareVersions)
      let found
      if (!fixes.length) found = { kind: 'noFix', version, line, item }
      else if (item.status === 'patch_scheduled' && (!item.patchScheduledAt || now < Date.parse(item.patchScheduledAt)))
        found = { kind: 'notYet', version, line, fix: fixes[0], item }
      else found = { kind: 'update', version, line, fix: fixes[0], item }
      if (RANK[found.kind] > RANK[worst.kind] || (found.kind === worst.kind && found.fix && worst.fix && compareVersions(found.fix, worst.fix) > 0)) worst = found
    }
  }
  return worst
}
