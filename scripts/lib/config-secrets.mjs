// Secret-looking values in committed config files — a scan for the evals.
//
// Every tracked config file (appsettings*.json, launchSettings.json, .env*,
// docker-compose*.yml, *.toml, *.ini, *.config, *.yml/*.yaml, *.properties,
// .npmrc, *settings*.json / *config*.json) is read line by line, comments
// included: a commented-out key is still committed. A finding names the file,
// the line, the key and the rule — never the value.
//
// Two kinds of finding:
//   • a secret-named key (ApiKey, AuthToken, Password, ClientSecret, *_API_KEY …;
//     not a bare "Key", which names a provider) with a value that is not a
//     placeholder (empty, ${VAR}, <…>, …, xxx, changeme, your-…);
//   • a value shaped like a credential wherever it appears: a JWT, a provider
//     key prefix (sk-, gsk_, ghp_, AKIA…, AIza…, xox?-), a private-key block, a
//     password or token parameter in a connection string, or user:password@ in a URL.

import fs from 'node:fs'
import path from 'node:path'
import { spawnSync } from 'node:child_process'

const CONFIG_FILE = /(^|\/)(appsettings[^/]*\.json|launchSettings\.json|\.env[^/]*|[^/]+\.env|docker-compose[^/]*\.ya?ml|[^/]+\.toml|[^/]+\.ini|[^/]+\.config|[^/]+\.ya?ml|[^/]+\.properties|\.npmrc|[^/]*settings[^/]*\.json|[^/]*config[^/]*\.json)$/i
const SKIP_FILE = /(^|\/)(node_modules|package-lock\.json|pnpm-lock\.yaml|yarn\.lock)(\/|$)/i

/** The same names backend/Secrets/SecretConfig.cs discards. */
const SECRET_NAME = /(api[_-]?key|access[_-]?key|private[_-]?key|secret|token|password|passwd|pwd|credentials?|authorization)$/i

const PLACEHOLDER = /^(|\$\{[^}]*\}|\$[A-Z_][A-Z0-9_]*|<[^>]*>|\.{2,}|…|x+|\*+|changeme|change-me|replace-?me|your[-_ ].*|todo|none|null|false|true|0)$/i

const SHAPES = [
  ['jwt', /eyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}/],
  ['provider key prefix', /\b(sk-(or-v1-|ant-|proj-)?[A-Za-z0-9_-]{20,}|gsk_[A-Za-z0-9]{20,}|gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_-]{35}|xox[abprs]-[A-Za-z0-9-]{10,})\b/],
  ['private key block', /-----BEGIN [A-Z ]*PRIVATE KEY-----/],
  ['secret in a connection string', /(?:^|[;"'\s?&])(password|pwd|auth[_ ]?token|access[_ ]?token|token)\s*=\s*(?!\$\{|<|;|"|'|\s|$)[^;"'\s&]{3,}/i],
  ['credentials in a URL', /[a-z][a-z0-9+.-]*:\/\/[^\s:/@"']+:[^\s@/"']{3,}@/i],
]

/** key = value pairs as JSON, YAML, TOML, INI or .env write them. */
const ASSIGNMENT = [
  /"([^"]+)"\s*:\s*"([^"]*)"/g,                                   // JSON
  /^\s*(?:#\s*|\/\/\s*|export\s+)?([A-Za-z_][\w.-]*)\s*=\s*"?([^"#\s]*)"?/g, // .env, TOML, INI (commented too)
  /^\s*(?:#\s*|-\s+)?([A-Za-z_][\w.-]*)\s*:\s+["']?([^"'#\s]+)["']?/g,      // YAML
]

export function trackedConfigFiles(repo) {
  const r = spawnSync('git', ['ls-files', '-z'], { cwd: repo, encoding: 'utf8' })
  if (r.status !== 0) throw new Error(`git ls-files failed: ${r.stderr}`)
  return r.stdout.split('\0').filter((f) => f && CONFIG_FILE.test(f) && !SKIP_FILE.test(f))
}

/** Findings in one file's text: [{ line, key?, rule }] — never the value. */
export function scanText(text) {
  const out = []
  text.split(/\r?\n/).forEach((line, i) => {
    for (const re of ASSIGNMENT) {
      re.lastIndex = 0
      for (const m of line.matchAll(re)) {
        const key = m[1], value = m[2].trim()
        const last = key.split(/__|:|\./).pop()
        if (SECRET_NAME.test(last) && !PLACEHOLDER.test(value)) out.push({ line: i + 1, key, rule: 'secret-named key with a value' })
      }
    }
    for (const [rule, re] of SHAPES) if (re.test(line)) out.push({ line: i + 1, rule })
  })
  // One finding per line and rule.
  const seen = new Set()
  return out.filter((f) => { const k = `${f.line}|${f.rule}`; if (seen.has(k)) return false; seen.add(k); return true })
}

/** Every finding in the repository's tracked config files (working-tree contents). */
export function scanRepo(repo) {
  const files = trackedConfigFiles(repo)
  const findings = []
  for (const f of files) {
    const full = path.join(repo, f)
    if (!fs.existsSync(full)) continue
    for (const x of scanText(fs.readFileSync(full, 'utf8'))) findings.push({ file: f, ...x })
  }
  return { files, findings }
}
