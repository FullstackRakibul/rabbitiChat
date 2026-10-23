// The public API, /truth-api/* (Vercel rewrites it to the backend's /public/truth/*).
//
// Every answer is kept in localStorage, so when the backend cannot be reached
// the shell shows the last copy it had under the grey bar (docs/TRUTH_PROMPT.md
// §4). Storage may be blocked or empty; the shell then shows the friendly error.

const BASE = '/truth-api'
const PREFIX = 'truth-cache:'

function remember(path, data) {
  try { localStorage.setItem(PREFIX + path, JSON.stringify({ savedAt: new Date().toISOString(), data })) } catch { /* storage blocked or full */ }
}

function recall(path) {
  try {
    const raw = localStorage.getItem(PREFIX + path)
    return raw ? JSON.parse(raw) : null
  } catch { return null }
}

/**
 * GET one path. Resolves { data, offline: false, serverNow } from the server;
 * { data, offline: true, savedAt } from the stored copy when the server can't be
 * reached; { notFound: true } for a 404. Rejects when there is neither.
 */
export async function load(path) {
  let res
  try {
    res = await fetch(BASE + path)
  } catch {
    res = null
  }
  if (res?.status === 404) return { notFound: true }
  if (res?.ok) {
    const data = await res.json()
    remember(path, data)
    return { data, offline: false, serverNow: res.headers.get('X-Server-Now') }
  }
  const stored = recall(path)
  if (stored) return { data: stored.data, offline: true, savedAt: stored.savedAt }
  throw new Error(`unreachable: ${path}`)
}

export const rssUrl = (lang) => `${BASE}/feed.xml?lang=${lang}`
