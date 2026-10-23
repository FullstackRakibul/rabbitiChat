#!/usr/bin/env node
// Licence check — docs/TRUTH_PROMPT.md §1.2 (the owner's standing rule: open source only).
//
//   node scripts/license-check.mjs           every npm and NuGet dependency, with its licence
//   node scripts/license-check.mjs --quiet   only problems and the summary
//
// npm: every package frontend/package-lock.json installs (direct and transitive),
// with the licence the lockfile records. NuGet: every package the backend
// restores (backend/obj/project.assets.json — run `dotnet restore` first), with
// the licence from its .nuspec in the local package cache: a licence expression,
// or a licence URL this script knows. An SPDX expression passes when it can be
// satisfied from the allow-list ("MIT OR Apache-2.0" needs one side, "MIT AND
// Zlib" both). Anything unknown fails: a new kind of licence is a decision for a
// person, written into ALLOW below with the reason, never guessed.
//
// Exit 0 when every licence is allowed, 1 otherwise. Node 18+, no dependencies.

import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const REPO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const quiet = process.argv.includes('--quiet')

/** OSI-approved licences (OFL for fonts, as §1.2 allows). */
const ALLOW = new Set([
  'MIT', 'MIT-0', 'ISC', '0BSD', 'BSD-2-Clause', 'BSD-3-Clause', 'Apache-2.0', 'MPL-2.0',
  'LGPL-2.1-only', 'LGPL-2.1-or-later', 'LGPL-3.0-only', 'LGPL-3.0-or-later', 'OFL-1.1',
  'BlueOak-1.0.0', 'Zlib', 'Unlicense', 'Python-2.0', 'MS-PL',
])

/** Licence URLs older NuGet packages give instead of an expression — each read and mapped by hand. */
const URLS = {
  'https://github.com/dotnet/corefx/blob/master/license.txt': 'MIT',
  'https://github.com/dotnet/corefx/blob/master/LICENSE.TXT': 'MIT',
  'https://github.com/dotnet/standard/blob/master/LICENSE.TXT': 'MIT',
  'https://raw.githubusercontent.com/dotnet/standard/master/LICENSE.TXT': 'MIT',
  'https://www.apache.org/licenses/LICENSE-2.0': 'Apache-2.0',
  'http://www.apache.org/licenses/LICENSE-2.0': 'Apache-2.0',
  'https://licenses.nuget.org/MIT': 'MIT',
  'https://licenses.nuget.org/Apache-2.0': 'Apache-2.0',
}

/**
 * Named exceptions: a package whose licence is not an OSI licence but was decided
 * on by a person, with the reason. Each holds only while the package's own licence
 * file still says what was decided on.
 */
const EXCEPTIONS = {
  // SQLite itself, the engine the whole app has always run on (not new in any build).
  // Public domain is not an OSI licence because it is no licence at all; the owner confirmed it on 2026-10-23 (docs/THIRD_PARTY.md).
  'SQLitePCLRaw.lib.e_sqlite3': { license: 'public domain (SQLite)', file: /SQLite is Public Domain/ },
}

/** Whether an SPDX expression can be satisfied from ALLOW. "SEE LICENSE IN …" is not a licence. */
export function allowed(expr) {
  if (!expr) return false
  const tokens = String(expr).replace(/[()]/g, ' ( ').replace(/\s+/g, ' ').trim().split(' ')
  let i = 0
  const primary = () => {
    if (tokens[i] === '(') { i++; const v = or(); i++; return v }
    let id = tokens[i++]
    // "SEE LICENSE IN <file>" is one operand that is never allowed.
    if (id === 'SEE') { while (i < tokens.length && !['OR', 'AND', ')'].includes(tokens[i])) i++; return false }
    if (tokens[i] === 'WITH') i += 2
    id = id?.replace(/\+$/, '')
    return ALLOW.has(id)
  }
  const and = () => { let v = primary(); while (tokens[i] === 'AND') { i++; v = primary() && v } return v }
  const or = () => { let v = and(); while (tokens[i] === 'OR') { i++; v = and() || v } return v }
  return or()
}

function npmPackages() {
  const lock = JSON.parse(fs.readFileSync(path.join(REPO, 'frontend', 'package-lock.json'), 'utf8'))
  return Object.entries(lock.packages)
    .filter(([key]) => key)
    .map(([key, v]) => {
      const name = v.name ?? key.slice(key.lastIndexOf('node_modules/') + 'node_modules/'.length)
      let license = typeof v.license === 'string' ? v.license : v.license?.type
      if (!license) {
        try { license = JSON.parse(fs.readFileSync(path.join(REPO, 'frontend', key, 'package.json'), 'utf8')).license } catch { /* not installed */ }
      }
      return { kind: 'npm', name, version: v.version, license: license ?? null, dev: !!v.dev }
    })
}

function nugetPackages() {
  const assets = path.join(REPO, 'backend', 'obj', 'project.assets.json')
  if (!fs.existsSync(assets)) throw new Error('backend/obj/project.assets.json is missing: run `dotnet restore backend` first.')
  const a = JSON.parse(fs.readFileSync(assets, 'utf8'))
  const root = a.packageFolders ? Object.keys(a.packageFolders)[0] : path.join(os.homedir(), '.nuget', 'packages')
  return Object.entries(a.libraries)
    .filter(([, v]) => v.type === 'package')
    .map(([key, v]) => {
      const [name, version] = key.split('/')
      const dir = path.join(root, v.path ?? `${name.toLowerCase()}/${version}`)
      const nuspec = fs.existsSync(dir) ? fs.readdirSync(dir).find((f) => f.endsWith('.nuspec')) : null
      const xml = nuspec ? fs.readFileSync(path.join(dir, nuspec), 'utf8') : ''
      const file = xml.match(/<license\s+type="file"\s*>([^<]+)<\/license>/i)?.[1]?.trim()
      const exception = EXCEPTIONS[name]
      if (file && exception && exception.file.test(fs.readFileSync(path.join(dir, file), 'utf8')))
        return { kind: 'nuget', name, version, license: exception.license, exception: true }
      const expr = xml.match(/<license\s+type="expression"\s*>([^<]+)<\/license>/i)?.[1]?.trim()
      const url = xml.match(/<licenseUrl>([^<]+)<\/licenseUrl>/i)?.[1]?.trim()
      const license = expr ?? (url ? URLS[url] ?? URLS[url.replace(/\/$/, '')] ?? null : null)
      return { kind: 'nuget', name, version, license, url: expr ? null : url ?? null }
    })
}

function main() {
  const all = [...npmPackages(), ...nugetPackages()].sort((a, b) => a.kind.localeCompare(b.kind) || a.name.localeCompare(b.name))
  const ok = (p) => p.exception || allowed(p.license)
  const bad = all.filter((p) => !ok(p))
  if (!quiet) for (const p of all) console.log(`${p.exception ? 'exc ' : ok(p) ? 'ok  ' : 'FAIL'} ${p.kind.padEnd(5)} ${p.name}@${p.version}  ${p.license ?? `unknown${p.url ? ` (${p.url})` : ''}`}`)
  for (const p of bad) console.error(`not allowed: ${p.kind} ${p.name}@${p.version} — ${p.license ?? 'no licence found'}${p.url ? ` (licence URL ${p.url})` : ''}`)
  const npm = all.filter((p) => p.kind === 'npm').length
  const exceptions = all.filter((p) => p.exception).map((p) => `${p.name}@${p.version} (${p.license})`)
  console.log(`\n${npm} npm and ${all.length - npm} NuGet packages checked; ${bad.length} not on the allow-list${exceptions.length ? `; named exception: ${exceptions.join(', ')}` : ''}.`)
  console.log(bad.length ? 'FAIL' : 'PASS')
  process.exit(bad.length ? 1 : 0)
}

main()
